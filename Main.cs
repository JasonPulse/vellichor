using Godot;
using Vellichor.Dat;
using Vellichor.Render;

namespace Vellichor;

/// <summary>
/// M0 render harness. Builds a lit scene with a free-look camera and renders geometry.
/// Right now it shows a PLACEHOLDER mesh so the harness is verifiable before the MMB
/// decoder lands; once decoding works, <see cref="LoadZone"/> swaps in real Ronfaure
/// meshes (corpus/ROM5/0/11.DAT) with no other change to the scene.
/// </summary>
public partial class Main : Node3D
{
    private string? _shot;
    private int _frames;

    // Live server bridge (VELLICHOR_ACCOUNT/PASSWORD): connects, renders live entities, then
    // does a timed graceful logout so the session is never left stale.
    private Vellichor.Net.EntityBridge? _bridge;
    private EntityRenderer? _entityRenderer;
    private double _liveElapsed;
    private double _liveDuration = 20; // observe seconds before graceful logout
    private bool _liveLoggingOut;

    public override void _Ready()
    {
        // Debug: if VELLICHOR_SHOT is set, render a few frames, save a PNG, and quit.
        _shot = System.Environment.GetEnvironmentVariable("VELLICHOR_SHOT");

        // DAT texture browser: VELLICHOR_VIEW=<dat-file-or-folder> shows a thumbnail grid
        // (for inspecting/identifying mod textures) instead of loading the zone.
        string? viewPath = System.Environment.GetEnvironmentVariable("VELLICHOR_VIEW");
        if (viewPath is not null)
        {
            var layer = new CanvasLayer();
            layer.AddChild(new Vellichor.Render.DatViewer(viewPath));
            AddChild(layer);
            return;
        }

        // Ambient + sun so untextured meshes are visible from any angle.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            // Diagnostic: bright magenta background under VELLICHOR_MAGENTA so real holes
            // (see-through to background) are unmistakable vs merely dark surfaces.
            BackgroundColor = System.Environment.GetEnvironmentVariable("VELLICHOR_MAGENTA") is null
                ? new Color(0.08f, 0.09f, 0.12f) : new Color(1f, 0f, 1f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.7f, 0.72f, 0.78f),
            AmbientLightEnergy = 1.4f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // Soft even lighting (no hard shadows) so untextured terrain reads cleanly — hard
        // self-shadowing was creating big dark patches that looked like holes.
        var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-55, -40, 0), ShadowEnabled = false, LightEnergy = 0.7f };
        AddChild(sun);
        var fill = new DirectionalLight3D { RotationDegrees = new Vector3(-30, 140, 0), ShadowEnabled = false, LightEnergy = 0.4f };
        AddChild(fill);

        var cam = new FlyCamera { Speed = 60f };

        GD.Print("Vellichor M0 harness up. Look: ARROW KEYS (or right-drag). Move: WASD/QE. Wheel: speed.");

        // Load the zone. Fall back to the placeholder if the corpus isn't present.
        string zonePath = ProjectSettings.GlobalizePath("res://corpus/ROM5/0/11.DAT");
        if (System.IO.File.Exists(zonePath))
        {
            var zone = ZoneLoader.Load(zonePath, out string report, out Aabb b);
            AddChild(zone);
            GD.Print("Zone: " + report);

            // Water plane: the DAT has no ground under rivers/ponds, so drop a translucent
            // plane near the low point — it shows through the no-ground regions as water and
            // is occluded by the higher terrain elsewhere. (Approximate single level for now.)
            var wc = b.GetCenter();
            AddChild(new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(b.Size.X, b.Size.Z) },
                Position = new Vector3(wc.X, b.Position.Y + 3f, wc.Z),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.15f, 0.30f, 0.42f, 0.72f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Metallic = 0.3f, Roughness = 0.15f,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            });

            // Entity render check: VELLICHOR_ENT_DEMO places a few demo entities (the live
            // bridge will feed a real WorldState here instead).
            if (System.Environment.GetEnvironmentVariable("VELLICHOR_ENT_DEMO") != null)
            {
                var er = new Vellichor.Render.EntityRenderer();
                AddChild(er);
                var ec = b.GetCenter();
                var demo = new XiHeadless.Game.WorldState();
                for (int i = 0; i < 5; i++)
                    demo.Entities[(uint)(100 + i)] = new XiHeadless.Game.Entity
                    {
                        Id = (uint)(100 + i), Index = (ushort)(0x100 + i),
                        Name = i == 0 ? "You" : $"NPC_{i}",
                        // WorldState positions are FFXI (Y-down); renderer flips. Use -center.Y.
                        X = ec.X + (i - 2) * 12, Y = -ec.Y, Z = ec.Z + (i % 2) * 12,
                        Allegiance = (byte)(i == 0 ? 1 : i % 3), TypeKnown = true,
                    };
                er.Update(demo);
            }

            // Live entities: VELLICHOR_ACCOUNT (+ VELLICHOR_PASSWORD) connects to the LSB
            // server, selects the EXISTING char (never creates), and streams live entities into
            // the renderer. _Process does a timed graceful logout so the session isn't stale.
            string? acct = System.Environment.GetEnvironmentVariable("VELLICHOR_ACCOUNT");
            if (acct is not null)
            {
                _entityRenderer = new EntityRenderer();
                AddChild(_entityRenderer);
                _bridge = new Vellichor.Net.EntityBridge();
                string pass = System.Environment.GetEnvironmentVariable("VELLICHOR_PASSWORD") ?? "";
                string resDir = ProjectSettings.GlobalizePath("res://res");
                if (double.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_LIVE_SECS"), out var s)) _liveDuration = s;
                GD.Print($"[live] connecting as '{acct}' -> ffxi.network-gnomes.com (select-existing, no create)");
                _ = System.Threading.Tasks.Task.Run(() =>
                    _bridge.ConnectAsync("ffxi.network-gnomes.com", "30251101_2", acct, pass, resDir));
            }

            var c = b.GetCenter();
            if (System.Environment.GetEnvironmentVariable("VELLICHOR_GROUND") != null)
            {
                cam.Position = new Vector3(c.X, b.Position.Y + b.Size.Y + 45, c.Z + 90);
                cam.RotationDegrees = new Vector3(-22, 0, 0);
            }
            else
            {
                // Spawn above the zone centre, looking down at it.
                cam.Position = new Vector3(c.X, b.Position.Y + b.Size.Y + 80, c.Z + 80);
                cam.RotationDegrees = new Vector3(-40, 0, 0);
            }
        }
        else
        {
            GD.Print($"corpus zone not found at {zonePath} — showing placeholder.");
            cam.Position = new Vector3(0, 3, 8);
            LoadPlaceholder();
        }

        AddChild(cam);
    }

    public override void _Process(double delta)
    {
        // Live session: stream entities into the renderer, log status ~1/s, then a timed
        // graceful logout (Shutdown blocks ~40s) before quitting so the session isn't stale.
        if (_bridge is not null)
        {
            if (_bridge.State is not null && _entityRenderer is not null) _entityRenderer.Update(_bridge.State);
            int prev = (int)_liveElapsed;
            _liveElapsed += delta;
            if ((int)_liveElapsed != prev)
                GD.Print($"[live] t={(int)_liveElapsed}s  {_bridge.Status}  entities={_bridge.State?.Entities.Count ?? 0}");
            if (!_liveLoggingOut && _liveElapsed >= _liveDuration)
            {
                _liveLoggingOut = true;
                if (_shot is not null) { GetViewport().GetTexture().GetImage().SavePng(_shot); GD.Print($"saved -> {_shot}"); }
                GD.Print($"[live] observe done: {_bridge.Status}; entities={_bridge.State?.Entities.Count ?? 0}. graceful logout (~40s)...");
                _bridge.Shutdown();
                GD.Print("[live] logged out cleanly.");
                GetTree().Quit();
            }
            return;
        }

        if (_shot is null) return;
        if (++_frames != 10) return;
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(_shot);
        GD.Print($"saved screenshot -> {_shot}");
        GetTree().Quit();
    }

    private void LoadZone(System.Collections.Generic.IEnumerable<MeshData> meshes)
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.7f, 0.72f) };
        foreach (var m in meshes)
            AddChild(ZoneRenderer.BuildMesh(m, mat));
    }

    private void LoadPlaceholder()
    {
        // A unit cube as MeshData — exercises the exact ArrayMesh path real meshes will use.
        float[] p =
        {
            -1,-1,-1,  1,-1,-1,  1,1,-1,  -1,1,-1,
            -1,-1, 1,  1,-1, 1,  1,1, 1,  -1,1, 1,
        };
        int[] idx =
        {
            0,1,2, 0,2,3,  5,4,7, 5,7,6,  4,0,3, 4,3,7,
            1,5,6, 1,6,2,  3,2,6, 3,6,7,  4,5,1, 4,1,0,
        };
        LoadZone(new[] { new MeshData { Positions = p, Indices = idx } });
    }
}
