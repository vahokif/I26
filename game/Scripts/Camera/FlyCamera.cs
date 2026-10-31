using Godot;

namespace I76.Game.Camera;

/// <summary>
/// Free-flying camera. Right mouse button to look; WASD to move, Q/E down/up,
/// Shift for speed, the mouse wheel to change base speed.
/// </summary>
public partial class FlyCamera : Camera3D
{
    public float Speed { get; set; } = 60f;
    protected float _yaw, _pitch;

    public void SetYawPitch(float yawDeg, float pitchDeg)
    {
        _yaw = Mathf.DegToRad(yawDeg);
        _pitch = Mathf.DegToRad(pitchDeg);
        ApplyRotation();
    }

    protected void ApplyRotation() => Basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0), EulerOrder.Yxz);

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Right)
                Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) Speed *= 1.25f;
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) Speed /= 1.25f;
        }
        else if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= mm.Relative.X * 0.003f;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.003f, -1.55f, 1.55f);
            ApplyRotation();
        }
    }

    public override void _Process(double delta)
    {
        if (!Current) return;   // WASDQE are the radar camera, targets...: only the current fly camera reads them
        var d = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) d -= Basis.Z;
        if (Input.IsKeyPressed(Key.S)) d += Basis.Z;
        if (Input.IsKeyPressed(Key.A)) d -= Basis.X;
        if (Input.IsKeyPressed(Key.D)) d += Basis.X;
        if (Input.IsKeyPressed(Key.E)) d += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) d -= Vector3.Up;
        if (d != Vector3.Zero)
            Position += d.Normalized() * Speed * (Input.IsKeyPressed(Key.Shift) ? 5f : 1f) * (float)delta;
    }
}
