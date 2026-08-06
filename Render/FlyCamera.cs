using Godot;

namespace Vellichor.Render;

/// <summary>
/// Free-look debug camera. Look: ARROW KEYS (or hold right mouse). Move: WASD, Q/E
/// down/up, Shift to sprint, mouse wheel to change speed. Used to fly around a decoded
/// zone during M0 before there is a real player entity.
/// </summary>
public partial class FlyCamera : Camera3D
{
    [Export] public float Speed = 20f;
    [Export] public float Sensitivity = 0.003f;
    [Export] public float TurnSpeed = 1.6f; // radians/sec for arrow-key look

    private float _pitch;
    private float _yaw;
    private bool _looking;

    public override void _Ready()
    {
        Current = true;
        var e = RotationDegrees;
        _yaw = Mathf.DegToRad(e.Y);
        _pitch = Mathf.DegToRad(e.X);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            _looking = mb.Pressed;
            Input.MouseMode = _looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventMouseMotion mm && _looking)
        {
            _yaw -= mm.Relative.X * Sensitivity;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * Sensitivity, -1.5f, 1.5f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }
        else if (@event is InputEventMouseButton wheel && wheel.Pressed)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp) Speed *= 1.2f;
            else if (wheel.ButtonIndex == MouseButton.WheelDown) Speed /= 1.2f;
        }
    }

    public override void _Process(double delta)
    {
        // Arrow-key look: Left/Right yaw, Up/Down pitch.
        float turn = TurnSpeed * (float)delta;
        bool turned = false;
        if (Input.IsKeyPressed(Key.Left)) { _yaw += turn; turned = true; }
        if (Input.IsKeyPressed(Key.Right)) { _yaw -= turn; turned = true; }
        if (Input.IsKeyPressed(Key.Up)) { _pitch += turn; turned = true; }
        if (Input.IsKeyPressed(Key.Down)) { _pitch -= turn; turned = true; }
        if (turned)
        {
            _pitch = Mathf.Clamp(_pitch, -1.5f, 1.5f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }

        var dir = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) dir -= Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.S)) dir += Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.A)) dir -= Transform.Basis.X;
        if (Input.IsKeyPressed(Key.D)) dir += Transform.Basis.X;
        if (Input.IsKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) dir += Vector3.Down;
        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? 5f : 1f);
        if (dir != Vector3.Zero) Position += dir.Normalized() * speed * (float)delta;
    }
}
