using System.Collections.Generic;
using Godot;
using XiHeadless.Game;

namespace Vellichor.Render;

/// <summary>
/// Renders live entities from a <see cref="WorldState"/> as capsules + nameplates, colored by
/// allegiance (player / mob / NPC / beastman). Call <see cref="Update"/> each frame with the
/// world state maintained by the protocol bridge; it spawns, moves, and ages out nodes.
/// Positions use the same FFXI(Y-down)->Godot(Y-up) flip as the zone (X, -Y, Z).
/// </summary>
public partial class EntityRenderer : Node3D
{
    private readonly Dictionary<uint, (Node3D root, Label3D tag)> _nodes = new();

    private static Color ColorFor(Entity e) => e.Allegiance switch
    {
        0 => new Color(0.85f, 0.25f, 0.20f), // mob (red)
        1 => new Color(0.30f, 0.70f, 1.00f), // player (cyan)
        >= 2 and <= 4 => new Color(0.35f, 0.85f, 0.40f), // town NPC (green)
        _ => new Color(0.95f, 0.65f, 0.15f), // beastmen / other (orange)
    };

    public void Update(WorldState ws)
    {
        var seen = new HashSet<uint>();
        foreach (var e in ws.Entities.Values)
        {
            seen.Add(e.Id);
            var pos = new Vector3(e.X, -e.Y, e.Z); // FFXI Y-down -> Godot Y-up
            if (!_nodes.TryGetValue(e.Id, out var n))
            {
                var root = new Node3D();
                var body = new MeshInstance3D
                {
                    Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2.0f },
                    Position = new Vector3(0, 1f, 0),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = ColorFor(e) },
                };
                var tag = new Label3D
                {
                    Text = e.Name,
                    Position = new Vector3(0, 2.4f, 0),
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    FontSize = 48,
                    NoDepthTest = true,
                };
                root.AddChild(body);
                root.AddChild(tag);
                AddChild(root);
                n = (root, tag);
                _nodes[e.Id] = n;
            }
            n.root.Position = pos;
            n.tag.Text = string.IsNullOrEmpty(e.Name) ? $"#{e.Index:X4}" : e.Name;
        }

        // Age out entities no longer present.
        var gone = new List<uint>();
        foreach (var id in _nodes.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (var id in gone) { _nodes[id].root.QueueFree(); _nodes.Remove(id); }
    }
}
