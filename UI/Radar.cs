using System.Collections.Generic;
using Godot;

namespace Vellichor.UI;

/// <summary>
/// Top-right radar: nearby entities as dots (colored by allegiance) around the player at center,
/// north-up. Positions are in Godot world XZ (same frame the 3D scene uses), so it needs the player's
/// Godot position + a snapshot of entity Godot XZ + allegiance each frame.
/// </summary>
public partial class Radar : Control
{
    private const float Radius = 90f;   // px
    private const float Range = 60f;     // world units mapped to the radar edge
    private readonly List<(Vector2 rel, Color col)> _dots = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -(Radius * 2) - 16; OffsetTop = 16;
        OffsetRight = -16; OffsetBottom = 16 + Radius * 2;
        QueueRedraw();
    }

    /// <param name="entities">(relative world XZ from player, color) — already player-relative.</param>
    public void SetDots(List<(Vector2 rel, Color col)> dots)
    {
        _dots.Clear();
        _dots.AddRange(dots);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var c = new Vector2(Radius, Radius);
        DrawCircle(c, Radius, new Color(0, 0, 0, 0.45f));
        DrawArc(c, Radius, 0, Mathf.Tau, 48, new Color(1, 1, 1, 0.25f), 1.5f);
        DrawArc(c, Radius * 0.5f, 0, Mathf.Tau, 32, new Color(1, 1, 1, 0.12f), 1f);
        // player at center (small triangle pointing up = north)
        DrawColoredPolygon(new[] { c + new Vector2(0, -6), c + new Vector2(-4, 5), c + new Vector2(4, 5) },
                           new Color(1f, 0.95f, 0.3f));
        foreach (var (rel, col) in _dots)
        {
            // world XZ -> radar px (north-up: world +Z is down on screen)
            var p = c + new Vector2(rel.X, rel.Y) / Range * Radius;
            if (p.DistanceTo(c) > Radius) continue;
            DrawCircle(p, 3f, col);
        }
    }
}
