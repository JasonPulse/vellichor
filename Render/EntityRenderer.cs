using System.Collections.Generic;
using Godot;
using XiHeadless.Game;

namespace Vellichor.Render;

/// <summary>
/// Renders live entities from a <see cref="WorldState"/>. Each entity shows its real animated FFXI model
/// when its look resolves (via <see cref="EntityModelCache"/>), else a colored capsule placeholder; both
/// carry a billboard nameplate colored by allegiance. Idle/walk clips are chosen from per-frame movement.
/// Positions use the same FFXI(Y-down)->Godot(Y-up) flip as the zone: Godot = (-X, YSign*Y, Z).
/// </summary>
public partial class EntityRenderer : Node3D
{
    private sealed class EntNode
    {
        public Node3D Root = null!;
        public Label3D Tag = null!;
        public MeshInstance3D? Capsule;
        public Node3D? ModelNode;
        public Skeleton3D? Skel;
        public AnimationDriver? Driver;
        public CharacterModel? Model;
        public string? Clip;
        public bool ModelTried;       // resolution attempted for the current (Known) look
        public Vector3 LastPos;
        public double StillFor;       // seconds since last significant move (for idle/walk hysteresis)
        public MeshInstance3D? HpFill; // thin billboarded HP bar under the nameplate (shown when damaged)
    }

    // Shared billboard material for the HP-bar fill (vertex-colored per entity via instance modulate is not
    // available on a plain quad, so we tint the material per update — cheap, one quad per entity).
    private static QuadMesh HpQuad() => new() { Size = new Vector2(1.0f, 0.12f) };

    private readonly Dictionary<uint, EntNode> _nodes = new();

    /// Resolves+decodes models for entity looks (capsule fallback when null). Set by Main after zone load.
    public EntityModelCache? Models { get; set; }

    public int Rendered => _nodes.Count;

    public static readonly float YSign =
        System.Environment.GetEnvironmentVariable("VELLICHOR_ENT_YSIGN") == "1" ? 1f : -1f;

    private static Color ColorFor(Entity e) => e.Allegiance switch
    {
        0 => new Color(0.85f, 0.25f, 0.20f), // mob (red)
        1 => new Color(0.30f, 0.70f, 1.00f), // player (cyan)
        >= 2 and <= 4 => new Color(0.35f, 0.85f, 0.40f), // town NPC (green)
        _ => new Color(0.95f, 0.65f, 0.15f), // beastmen / other (orange)
    };

    private Entity[] _lastEnts = System.Array.Empty<Entity>();
    private MeshInstance3D? _targetRing;

    private MeshInstance3D TargetRing()
    {
        if (_targetRing is null)
        {
            _targetRing = new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = 0.55f, OuterRadius = 0.7f, RingSegments = 24 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(1f, 0.85f, 0.2f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
                Rotation = new Vector3(Mathf.Pi / 2, 0, 0),
                Visible = false,
            };
            AddChild(_targetRing);
        }
        return _targetRing;
    }

    public void Update(WorldState ws) => Update(ws, 1.0 / 60.0);

    public void Update(WorldState ws, double delta)
    {
        Entity[] ents = _lastEnts;
        try { ents = System.Linq.Enumerable.ToArray(ws.Entities.Values); _lastEnts = ents; }
        catch { /* raced with the receive thread — reuse last good snapshot this frame */ }

        var cam = GetViewport()?.GetCamera3D();
        var seen = new HashSet<uint>();
        foreach (var e in ents)
        {
            if (!string.IsNullOrEmpty(ws.MyName) && e.Name == ws.MyName) continue; // Main renders self
            seen.Add(e.Id);
            var pos = new Vector3(-e.X, YSign * e.Y, e.Z);
            if (!_nodes.TryGetValue(e.Id, out var n))
            {
                n = new EntNode { Root = new Node3D(), LastPos = pos };
                n.Tag = new Label3D
                {
                    Text = e.Name,
                    Position = new Vector3(0, 2.4f, 0),
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    FontSize = 48,
                    NoDepthTest = true,
                    Modulate = ColorFor(e),
                };
                n.Root.AddChild(n.Tag);
                AddChild(n.Root);
                _nodes[e.Id] = n;
            }

            EnsureVisual(n, e);

            // Move + face heading; drive idle/walk from movement.
            float moved = n.Root.Position.DistanceTo(pos);
            n.StillFor = moved > 0.02f ? 0 : n.StillFor + delta;
            n.Root.Position = pos;
            if (moved > 0.001f)
            {
                var dir = (pos - n.LastPos) with { Y = 0 };
                if (dir.LengthSquared() > 1e-4f) n.Root.Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Z), 0);
            }
            n.LastPos = pos;
            n.Tag.Text = string.IsNullOrEmpty(e.Name) ? $"#{e.Index:X4}" : e.Name;
            UpdateHpBar(n, e, ws.CurrentTargetId == e.Id);

            // Fade distant nameplates so a crowded town stays readable (full <=25y, gone by ~55y);
            // the current target always stays fully visible.
            var tc = ColorFor(e);
            if (cam is not null && ws.CurrentTargetId != e.Id)
                tc.A = Mathf.Clamp(1f - (cam.GlobalPosition.DistanceTo(pos) - 25f) / 30f, 0f, 1f);
            n.Tag.Modulate = tc;
            n.Tag.Visible = tc.A > 0.02f;

            if (n.Driver is not null && n.Model is not null)
            {
                bool walking = n.StillFor < 0.25;
                string? want = walking ? n.Model.FindClip("wlk", "run", "mov") : n.Model.FindClip("idl", "dw0", "brth");
                want ??= n.Model.FindClip(""); // any clip as a last resort
                if (want is not null && want != n.Clip && n.Skel is not null)
                {
                    var clip = n.Model.Clip(want);
                    if (clip is { } c)
                    {
                        n.Driver.Setup(n.Skel, c.tracks, c.frames, c.fps);
                        n.Driver.Loop = true;
                        n.Clip = want;
                    }
                }
            }
        }

        // Age out.
        var gone = new List<uint>();
        foreach (var id in _nodes.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (var id in gone) { _nodes[id].Root.QueueFree(); _nodes.Remove(id); }

        SpawnCombatFx(ws);

        var ring = TargetRing();
        if (ws.CurrentTargetId != 0 && _nodes.TryGetValue(ws.CurrentTargetId, out var tn))
        {
            ring.Position = tn.Root.Position + new Vector3(0, 0.06f, 0);
            ring.Visible = true;
        }
        else ring.Visible = false;
    }

    /// Show a small HP bar under an entity's nameplate when it's damaged (Hpp 1..99) or is the current
    /// target; hide it otherwise. Fill width = Hpp%, color green->red. One billboarded quad per entity.
    private void UpdateHpBar(EntNode n, Entity e, bool isTarget)
    {
        bool show = e.Hpp is > 0 and < 100 || (isTarget && e.Hpp > 0);
        if (!show) { if (n.HpFill is not null) n.HpFill.Visible = false; return; }
        if (n.HpFill is null)
        {
            n.HpFill = new MeshInstance3D
            {
                Mesh = HpQuad(),
                Position = new Vector3(0, 2.05f, 0),
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                },
            };
            n.Root.AddChild(n.HpFill);
        }
        float f = e.Hpp / 100f;
        n.HpFill.Visible = true;
        n.HpFill.Scale = new Vector3(f, 1, 1); // centered shrink (billboarded; entity rotation makes an x-offset drift)
        ((StandardMaterial3D)n.HpFill.MaterialOverride).AlbedoColor =
            new Color(Mathf.Lerp(0.9f, 0.2f, f), Mathf.Lerp(0.15f, 0.85f, f), 0.2f); // red(low)->green(high)
    }

    private long _lastFxSeq;

    /// Spawn floating damage/heal numbers for combat events newer than the last we showed, positioned over
    /// the target entity. Damage = white, heal = green. Self-targeted events (no entity node here) are skipped.
    private void SpawnCombatFx(WorldState ws)
    {
        XiHeadless.Game.WorldState.CombatFxEvent[] fx;
        try { fx = System.Linq.Enumerable.ToArray(ws.CombatFx); } catch { return; }
        foreach (var e in fx)
        {
            if (e.Seq <= _lastFxSeq) continue;
            _lastFxSeq = e.Seq;
            if (!_nodes.TryGetValue(e.Target, out var tn)) continue;
            var col = e.Kind == 1 ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.95f, 0.9f);
            string txt = e.Kind == 2 ? "miss" : (e.Kind == 1 ? "+" : "") + e.Amount;
            FloatingText.Spawn(this, tn.Root.Position + new Vector3(0, 2.2f, 0), txt, col);
        }
    }

    /// Ensure the entity has the best visual it can: a real animated model if its look resolves, else a
    /// capsule. Upgrades capsule -> model once the look arrives (entities often spawn before UPDATE_LOOK).
    private void EnsureVisual(EntNode n, Entity e)
    {
        if (n.ModelNode is not null) return;               // already has a model
        bool canTry = e.Look.Known || (Models?.HasForce ?? false);

        if (Models is not null && canTry && !n.ModelTried)
        {
            n.ModelTried = true;
            var model = Models.Get(e.Look, e.Name);
            if (model is not null)
            {
                var (root, skel, _) = model.BuildInstance();
                n.ModelNode = root;
                n.Model = model;
                n.Skel = skel;
                n.Root.AddChild(root);
                if (model.FindClip("") is not null)
                {
                    var driver = new AnimationDriver();
                    n.Root.AddChild(driver);
                    n.Driver = driver;
                    n.Clip = null; // forces clip setup on first Update pass
                }
                if (n.Capsule is not null) { n.Capsule.QueueFree(); n.Capsule = null; }
                return;
            }
        }

        if (n.Capsule is null)
        {
            n.Capsule = new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2.0f },
                Position = new Vector3(0, 1f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = ColorFor(e) },
            };
            n.Root.AddChild(n.Capsule);
        }
    }
}
