using Godot;
using I76.Game.Camera;
using I76.Game.Sim;

namespace I76.Game.Editor;

/// <summary>
/// The map editor's camera, RTS-style: it looks at a pivot on the ground and stands <see cref="Distance"/> metres from it.
/// WASD (or the mouse at the window's edge, Tab) slides the pivot over the ground; Q/E turn; the right button orbits, the
/// middle one drags the ground; the wheel zooms toward the point under the cursor; F brings the point under the cursor to the
/// middle. The pivot is kept in the game's frame, so the main scene's floating origin can move under it.
/// </summary>
public partial class EditorCamera : FlyCamera
{
    public Func<System.Numerics.Vector3> Origin { get; set; } = () => default;
    /// <summary>Moves the scene's floating origin (Main.Rebase) when the pivot has wandered far from it.</summary>
    public Action<System.Numerics.Vector3> Rebase { get; set; } = _ => { };
    public Func<float, float, float> GroundAt { get; set; } = (_, _) => 0;
    /// <summary>The ground point under the mouse (game frame), or null: the wheel zooms toward it.</summary>
    public Func<System.Numerics.Vector3?> Cursor { get; set; } = () => null;
    /// <summary>Whether keys and the mouse move the camera (false under a menu or a text field).</summary>
    public Func<bool> Enabled { get; set; } = () => true;

    public System.Numerics.Vector3 Pivot;
    public float Distance = 300f;
    public const float MinDistance = 6f, MaxDistance = 3500f;
    public bool EdgePan { get; set; }
    private System.Numerics.Vector3? _glideTo;
    private bool _orbiting, _dragging;

    public float Yaw { get => _yaw; set { _yaw = value; ApplyRotation(); } }
    public float Pitch { get => _pitch; set { _pitch = Mathf.Clamp(value, -1.5f, -0.12f); ApplyRotation(); } }

    public void Set(System.Numerics.Vector3 pivot, float yaw, float pitch, float distance)
    {
        Pivot = pivot; _yaw = yaw; Distance = Mathf.Clamp(distance, MinDistance, MaxDistance);
        _pitch = Mathf.Clamp(pitch, -1.5f, -0.12f);
        ApplyRotation();
        Place();
    }

    /// <summary>Glides the pivot to a ground point.</summary>
    public void FocusOn(System.Numerics.Vector3 p) => _glideTo = p;

    /// <summary>The heading on the ground the camera faces, degrees from game +Z toward +X (a car dropped there faces it).</summary>
    public float HeadingDegrees
    {
        get
        {
            var f = -Basis.Z;                 // Godot space; game z is mirrored
            return Mathf.RadToDeg(Mathf.Atan2(f.X, -f.Z));
        }
    }

    private void Place()
    {
        var pivotG = Coords.ToGodot(Pivot - Origin());
        Position = pivotG + Basis * new Vector3(0, 0, Distance);
    }

    private Vector3 Flat(Vector3 v) { v.Y = 0; return v.LengthSquared() < 1e-8f ? Vector3.Zero : v.Normalized(); }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Current || !Enabled()) { if (_orbiting || _dragging) Release(); return; }
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex == MouseButton.Right)
                {
                    _orbiting = mb.Pressed;
                    Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.Middle)
                {
                    _dragging = mb.Pressed;
                    Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    float k = mb.ButtonIndex == MouseButton.WheelUp ? 0.86f : 1 / 0.86f;
                    if (mb.ShiftPressed || mb.CtrlPressed || mb.AltPressed) break;   // the brush's, MapEditor
                    float nd = Mathf.Clamp(Distance * k, MinDistance, MaxDistance);
                    // toward the point under the cursor when zooming in: the ground there stays under the cursor
                    if (Cursor() is { } c && nd < Distance)
                    {
                        float f = 1 - nd / Distance;
                        Pivot = System.Numerics.Vector3.Lerp(Pivot, c, f * 0.5f);
                    }
                    Distance = nd;
                    _glideTo = null;
                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseMotion mm when _orbiting:
                _yaw -= mm.Relative.X * 0.004f;
                _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.004f, -1.5f, -0.12f);
                ApplyRotation();
                break;
            case InputEventMouseMotion mm when _dragging:
                {
                    var right = Flat(Basis.X); var fwd = Flat(-Basis.Z);
                    var d = (-right * mm.Relative.X + fwd * mm.Relative.Y) * (Distance * 0.0016f);
                    Pivot += new System.Numerics.Vector3(d.X, 0, -d.Z);
                    _glideTo = null;
                    break;
                }
        }
    }

    private void Release()
    {
        _orbiting = _dragging = false;
        if (Input.MouseMode == Input.MouseModeEnum.Captured) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _Process(double delta)
    {
        if (!Current) return;
        float dt = (float)Math.Min(delta, 0.1);
        if (Enabled() && GetViewport().GuiGetFocusOwner() is not LineEdit)
        {
            var move = Vector2.Zero;     // x right, y forward
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) move.Y += 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) move.Y -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move.X += 1;
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) move.X -= 1;
            if (EdgePan && DisplayServer.WindowIsFocused() && !_orbiting && !_dragging)
            {
                var m = GetViewport().GetMousePosition();
                var size = GetViewport().GetVisibleRect().Size;
                const float edge = 4f;
                if (m.X >= 0 && m.Y >= 0 && m.X <= size.X && m.Y <= size.Y)
                {
                    if (m.X < edge) move.X -= 1; else if (m.X > size.X - edge) move.X += 1;
                    if (m.Y < edge) move.Y += 1; else if (m.Y > size.Y - edge) move.Y -= 1;
                }
            }
            if (move != Vector2.Zero)
            {
                move = move.Normalized();
                float speed = Distance * 1.1f * (Input.IsKeyPressed(Key.Shift) ? 3f : 1f);
                var d = (Flat(Basis.X) * move.X + Flat(-Basis.Z) * move.Y) * speed * dt;
                Pivot += new System.Numerics.Vector3(d.X, 0, -d.Z);
                _glideTo = null;
            }
            float turn = (Input.IsKeyPressed(Key.E) ? 1 : 0) - (Input.IsKeyPressed(Key.Q) ? 1 : 0);
            if (turn != 0) { _yaw -= turn * 1.3f * dt; ApplyRotation(); }
            float zoom = (Input.IsKeyPressed(Key.Pagedown) ? 1 : 0) - (Input.IsKeyPressed(Key.Pageup) ? 1 : 0);
            if (zoom != 0) Distance = Mathf.Clamp(Distance * Mathf.Pow(1.9f, zoom * dt), MinDistance, MaxDistance);
        }
        if (_glideTo is { } g)
        {
            float k = 1 - Mathf.Exp(-9f * dt);
            Pivot = System.Numerics.Vector3.Lerp(Pivot, g, k);
            if ((Pivot - g).Length() < 0.3f) _glideTo = null;
        }
        if (new System.Numerics.Vector2(Pivot.X - Origin().X, Pivot.Z - Origin().Z).Length() > 1500f) Rebase(Pivot);
        // the pivot rides the ground; the eye keeps above it
        float ground = GroundAt(Pivot.X, Pivot.Z);
        Pivot.Y += (ground - Pivot.Y) * (1 - Mathf.Exp(-10f * dt));
        Place();
        var eyeG = Coords.ToGame(Position) + Origin();
        float floor = GroundAt(eyeG.X, eyeG.Z) + 3f;
        if (eyeG.Y < floor) Position = Position with { Y = floor - Origin().Y };
    }
}
