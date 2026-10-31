using Godot;
using I76.Sim.Views;

namespace I76.Game.Camera;

/// <summary>
/// The preset views that stand on the player's car or over it, each
/// worked out from the car's pose every frame:
/// <list type="bullet">
/// <item><b>Orbit</b> (F6): FOV 120 across a 4:3 view; the eye fixed at (0, 0.7, 3.5) in the car's
/// frame, ahead of the car (the offset is not turned), looking ahead turned by the pilot's glance yaw, pitch
/// and roll (eased at 4·dt, as the original does); no ground clamp. The eye does sit out in front.</item>
/// <item><b>Side</b> (F7) and <b>SideBack</b> (F8): in the car's frame at the collider centre +
/// (−1.5, 0, 0.2) (F7, looking ahead) or (−1.5, 0, −0.25) (F8, turned by π), kept 0.6 above the ground.</item>
/// <item><b>Overhead</b> (F10): looking straight down, north up, from the ground under the car + h; h
/// starts at 100 and is held within [4·radius, 0.85·far]. Page Down (overview_zoom_plus) raises it and Page Up
/// lowers it, by 1 % a frame per unit of a ramp that builds while the key is held (<see cref="I76.Sim.Views.OverviewZoom"/>).</item>
/// </list>
/// All with the engine's projection (90° across a 4:3 view) unless noted.
/// </summary>
public partial class PresetCamera : Camera3D
{
    public enum View { Orbit, Side, SideBack, Overhead }

    public View Mode { get; set; }
    public Node3D? Target { get; set; }
    /// <summary>The collider centre in the car's frame (Godot axes) and the collider radius.</summary>
    public Vector3 LocalCentre { get; set; } = new(0, 0.7f, 0);
    public float Radius { get; set; } = 2.5f;
    /// <summary>The bare terrain height under a point, game axes, relative to the floating origin (the chase camera's).</summary>
    public Func<float, float, float>? Ground { get; set; }
    /// <summary>The pilot's glance (yaw, pitch, roll in radians, game signs): the cockpit's head.</summary>
    public Func<(float Yaw, float Pitch, float Roll)>? Head { get; set; }

    private const float Four3 = 0.75f;
    private float _yaw, _pitch, _roll;
    private readonly I76.Sim.Views.OverviewZoom _zoom = new();
    private float _zoomAcc;
    /// <summary>The port's wheel factors (<see cref="ViewZoom"/>), eased: the side offset, the F6 lens and the F10 height.</summary>
    private float _wheelSide = MouseCamera.ZoomOf(ViewZoom.Kind.Side), _wheelBumper = MouseCamera.ZoomOf(ViewZoom.Kind.Bumper), _wheelOver = MouseCamera.ZoomOf(ViewZoom.Kind.Overhead);

    private int _epoch = MouseCamera.ZoomEpoch;

    public PresetCamera() { Near = 0.25f; Far = 20000; Fov = 73.74f; }

    /// <summary>Called when the view is selected (each sets its own start).</summary>
    public void Start(View mode)
    {
        Mode = mode;
        if (mode == View.Overhead) { _zoom.Reset(); _zoomAcc = 0; }
        Fov = mode == View.Orbit ? Mathf.RadToDeg(2 * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(60)) * Four3)) : 73.74f;
        if (mode == View.Orbit && Head?.Invoke() is { } h) (_yaw, _pitch, _roll) = h;
        else (_yaw, _pitch, _roll) = (0, 0, 0);
    }

    private static Vector3 Game(Vector3 g) => new(g.X, g.Y, -g.Z);

    public override void _Process(double delta)
    {
        if (Target is null) return;
        float dt = Mathf.Clamp((float)delta, 0.001f, 0.2f);
        var t = Target.GlobalTransform;
        if (_epoch != MouseCamera.ZoomEpoch) { _epoch = MouseCamera.ZoomEpoch; _wheelSide = MouseCamera.ZoomOf(ViewZoom.Kind.Side); _wheelBumper = MouseCamera.ZoomOf(ViewZoom.Kind.Bumper); _wheelOver = MouseCamera.ZoomOf(ViewZoom.Kind.Overhead); }
        _wheelSide = ChaseZoom.Ease(_wheelSide, MouseCamera.ZoomOf(ViewZoom.Kind.Side), dt);
        _wheelBumper = ChaseZoom.Ease(_wheelBumper, MouseCamera.ZoomOf(ViewZoom.Kind.Bumper), dt);
        _wheelOver = ChaseZoom.Ease(_wheelOver, MouseCamera.ZoomOf(ViewZoom.Kind.Overhead), dt);
        if (Current && Controls.Down("zoom_factor_reset"))
            switch (Mode) { case View.Orbit: MouseCamera.ResetZoom(ViewZoom.Kind.Bumper); break; case View.Overhead: MouseCamera.ResetZoom(ViewZoom.Kind.Overhead); break; default: MouseCamera.ResetZoom(ViewZoom.Kind.Side); break; }
        switch (Mode)
        {
            case View.Orbit:
            {
                Fov = ViewZoom.BumperFov(_wheelBumper);
                var (ty, tp, tr) = Head?.Invoke() ?? default;
                float k = dt * 4f;
                float dy = Mathf.Wrap(ty - _yaw, -Mathf.Pi, Mathf.Pi);
                if (MathF.Abs(dy) >= 0.005f) _yaw = Mathf.Wrap(_yaw + k * dy, -Mathf.Pi * 2, Mathf.Pi * 2);
                _pitch = k <= 0.95f && MathF.Abs(tp - _pitch) >= 0.005f ? _pitch + k * (tp - _pitch) : tp;
                _roll = k <= 0.95f && MathF.Abs(tr - _roll) >= 0.005f ? _roll + k * (tr - _roll) : tr;
                // the car's frame in game axes is Godot's with Z mirrored: a game yaw to the right is a negative Y turn, pitch is down-positive
                var local = new Transform3D(Basis.FromEuler(new Vector3(-_pitch, -_yaw, _roll), EulerOrder.Yxz), Game(new Vector3(0, 0.7f, 3.5f)));
                GlobalTransform = t * local;
                break;
            }
            case View.Side:
            case View.SideBack:
            {
                bool back = Mode == View.SideBack;
                var off = Game(new Vector3(-1.5f * _wheelSide, 0, back ? -0.25f : 0.2f));
                var basis = back ? new Basis(Vector3.Up, Mathf.Pi) : Basis.Identity;
                var g = t * new Transform3D(basis, LocalCentre + off);
                // kept 0.6 above the ground (cond: y - 0.1 < ground + 0.5)
                float ground = Ground?.Invoke(g.Origin.X, -g.Origin.Z) ?? float.NegativeInfinity;
                if (g.Origin.Y - 0.1f < ground + 0.5f) g.Origin = new Vector3(g.Origin.X, ground + 0.6f, g.Origin.Z);
                GlobalTransform = g;
                break;
            }
            case View.Overhead:
            {
                float far = Far;
                bool plus = Controls.Down("overview_zoom_plus"), minus = Controls.Down("overview_zoom_minus");
                _zoomAcc = MathF.Min(_zoomAcc + dt, 8 * I76.Sim.Views.OverviewZoom.Step);
                for (; _zoomAcc >= I76.Sim.Views.OverviewZoom.Step; _zoomAcc -= I76.Sim.Views.OverviewZoom.Step) _zoom.Frame(plus, minus, Radius, far);
                var p = t.Origin;
                float ground = Ground?.Invoke(p.X, -p.Z) ?? p.Y;
                // looking down with north (game +Z, Godot -Z) up the screen
                GlobalTransform = new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Forward), new Vector3(p.X, ground + I76.Sim.Views.OverviewZoom.Scaled(_zoom.Height, _wheelOver, Radius, far), p.Z));
                break;
            }
        }
    }
}
