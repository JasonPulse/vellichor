using Godot;

namespace Vellichor.Render;

/// <summary>
/// A billboard damage/heal number that rises and fades, then frees itself — the classic MMO combat-text
/// pop. Spawn one per landed hit/cure over the target. Self-managed lifetime (no external cleanup).
/// </summary>
public partial class FloatingText : Label3D
{
    private double _age;
    private const float Life = 1.3f;
    private Vector3 _vel = new(0, 1.4f, 0);

    public static void Spawn(Node parent, Vector3 worldPos, string text, Color color)
    {
        var ft = new FloatingText
        {
            Text = text,
            Position = worldPos,
            Modulate = color,
            FontSize = 44,
            OutlineSize = 12,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FixedSize = true,     // constant on-screen size regardless of distance
            PixelSize = 0.0007f,
        };
        parent.AddChild(ft);
    }

    public override void _Process(double delta)
    {
        _age += delta;
        Position += _vel * (float)delta;
        _vel.Y = Mathf.Max(0.2f, _vel.Y - (float)delta * 1.2f); // ease upward
        float t = (float)(_age / Life);
        var c = Modulate; c.A = Mathf.Clamp(1f - t, 0f, 1f); Modulate = c;   // fade out
        if (_age >= Life) QueueFree();
    }
}
