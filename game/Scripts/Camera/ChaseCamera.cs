using Godot;
using I76.Sim.Views;

namespace I76.Game.Camera;

/// <summary>
/// The original's chase view, PRESET_VIEW_2 (F2; camera
/// mode 7). Worked in the game frame, then
/// handed to Godot:
/// <list type="bullet">
/// <item>The camera stands zoom × the car's collider radius (capped at 150)
/// behind the collider centre — the midpoint of the hierarchy's box, the
/// radius raised to the farthest COLP corner + 0.1.</item>
/// <item>It is turned by pitch and yaw over a smoothed copy of the car's
/// heading only (the forward row is flattened, then slerped
/// at 2·dt a frame). Pitch, yaw and zoom also ease at 2·dt (snapping when
/// 2·dt leaves 0..0.95 or the step is under 0.005).</item>
/// <item>It is kept 0.6 above the ground under it. The projection is the
/// engine's one: 90° across a 4:3 view, 73.7° high.</item>
/// <item>the world camera rebuild starts pitch at 30°, yaw at 0; the zoom
/// (track_distance, clamped 1..10) reads 3 in the original game.</item>
/// </list>
/// Keys (input.map): the arrows swing it (track_yaw_minus/plus,
/// track_pitch_minus/plus; pitch 1°..90°), Page Up/Down zoom
/// (track_distance_minus/plus), End resets. The rates are approximate: the
/// engine ramps these axes in its binding layer, which is not
/// modelled. The mouse (Mouse Camera, on by default) moves the same targets by its motion. An axis bound to the track_yaw_delta or track_pitch_delta rows
/// (the pad's right stick in the port's preset) turns it in proportion.
/// When the car dies (and round the winner at a network match's end) it swings
/// into an orbit round the wreck, 8 degrees per sim tick's worth of time as the
/// original's 8 a frame (DeathOrbit).
/// </summary>
public partial class ChaseCamera : Camera3D
{
    public Node3D? Target { get; set; }
    /// <summary>The collider centre in the car's frame (Godot axes) and the collider radius.</summary>
    public Vector3 LocalCentre { get; set; } = new(0, 0.7f, 0);
    public float Radius { get; set; } = 2.5f;
    /// <summary>The death orbit: true once the target is a wreck.</summary>
    public Func<bool>? Orbit { get; set; }
    /// <summary>The bare terrain height under a point, for the camera's floor: game axes, both relative to the floating origin.</summary>
    public Func<float, float, float>? Ground { get; set; }
    /// <summary>
    /// A fixed-angle preset (F3 front: 15°, π; F9 front quarter: 15°, 150°): pitch and yaw held, the centre
    /// raised by Lift (2), the track keys moving only the distance. Null is the chase view (F2).
    /// </summary>
    public (float Pitch, float Yaw, float Lift)? Fixed { get; set; }
    private readonly I76.Sim.Views.DeathOrbit _death = new();
    /// <summary>The track axes at full deflection, degrees a second (the original maps them to ±10° a frame; the port's keys turn at 90 and 45).</summary>
    private const float StickYawRate = 180, StickPitchRate = 90;
    private bool _orbiting;
    private const float BaseFov = 73.74f;
    private readonly I76.Sim.Views.ChaseOrbit _orbit = new() { Pitch = DefaultPitch };
    private int _epoch = MouseCamera.ZoomEpoch;
    private float _wheel = MouseCamera.ZoomOf(ViewZoom.Kind.Chase);

    private const float DefaultPitch = 0.5235988f, DefaultZoom = 3f;   // the original game has zoom 3 in F2 on a01
    // targets (pitch, yaw, zoom) and their eased values
    private float _pitchT = DefaultPitch, _yawT, _zoomT = DefaultZoom;
    private float _zoom = DefaultZoom;
    private float _heading = float.NaN;   // the slerped heading, as an angle: the rotations are all about up

    public ChaseCamera()
    {
        Fov = 73.74f;
        // I76_CHASE_YAW=degrees starts the camera swung round the car, for off-screen captures
        if (float.TryParse(System.Environment.GetEnvironmentVariable("I76_CHASE_YAW"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var y))
            _orbit.Yaw = _yawT = Mathf.DegToRad(y);
        // I76_CHASE_ZOOM=1..10 starts it nearer or further (the track_distance setting)
        if (float.TryParse(System.Environment.GetEnvironmentVariable("I76_CHASE_ZOOM"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var z))
            _zoomT = _zoom = System.Math.Clamp(z, 1f, 10f);
    }

    private static Vector3 Game(Vector3 g) => new(g.X, g.Y, -g.Z);

    /// <summary>Where the camera stands relative to the car (its eased pitch, yaw, zoom and heading, and their targets), for a checkpoint.</summary>
    public float[] SaveState() => [_pitchT, _yawT, _zoomT, _orbit.Pitch, _orbit.Yaw, _zoom, _heading, _death.Yaw, _orbiting ? 1 : 0];

    /// <summary>The camera as <see cref="SaveState"/> had it.</summary>
    public void LoadState(float[] s)
    {
        if (s.Length < 9) return;
        (_pitchT, _yawT, _zoomT, _zoom, _heading, _death.Yaw) = (s[0], s[1], s[2], s[5], s[6], s[7]);
        (_orbit.Pitch, _orbit.Yaw) = (s[3], s[4]);
        _orbiting = s[8] != 0;
        _orbit.Reset();
    }

    public override void _Process(double delta)
    {
        if (Target is null) return;
        var t = Target.GlobalTransform;
        var centreG = t * LocalCentre;
        float dt = System.Math.Clamp((float)delta, 0.001f, 0.2f);
        if (Orbit?.Invoke() == true) { DeathOrbit(t, centreG, dt); return; }
        _orbiting = false;

        // the track keys act only while this is the view (the arrows are also the cockpit's glances and the map's keys)
        bool mine = Current;
        if (Fixed is null && mine)
        {
            if (Controls.Down("track_yaw_minus")) _yawT -= Mathf.DegToRad(90) * dt;
            if (Controls.Down("track_yaw_plus")) _yawT += Mathf.DegToRad(90) * dt;
            if (Controls.Down("track_pitch_plus")) _pitchT += Mathf.DegToRad(45) * dt;
            if (Controls.Down("track_pitch_minus")) _pitchT -= Mathf.DegToRad(45) * dt;
            // the axis rows (the pad's right stick in the port's preset): turning in proportion to the row, up to twice the keys' rates
            var (ay, ap) = Controls.CameraAxes;
            // the stick and the mouse (Mouse Camera) are offsets on the targets, the stick's eased with them as ever, the mouse's
            // more closely (ChaseOrbit); they ease back behind the car a few seconds after either last moved (Camera Return)
            var (my, mp) = MouseCamera.Take();
            _orbit.Move(ay * Mathf.DegToRad(StickYawRate) * dt, ap * Mathf.DegToRad(StickPitchRate) * dt, my, mp, dt, MouseCamera.ReturnBehind);
        }
        else { if (!MouseCamera.CockpitActive) MouseCamera.Take(); _orbit.Reset(); }
        if (mine && Controls.Down("track_distance_plus")) _zoomT += 2 * dt;
        if (mine && Controls.Down("track_distance_minus")) _zoomT -= 2 * dt;
        if (mine && Controls.Down("zoom_factor_reset")) { _yawT = 0; _pitchT = DefaultPitch; _zoomT = DefaultZoom; MouseCamera.ResetZoom(ViewZoom.Kind.Chase); _orbit.Reset(); }
        _pitchT = System.Math.Clamp(_pitchT, 0.017453292f, 1.5707963f);
        _zoomT = System.Math.Clamp(_zoomT, 1f, 10f);

        float f = dt + dt;
        static float Ease(float cur, float want, float f) =>
            f >= 0 && f <= 0.95f && MathF.Abs(want - cur) >= 0.005f ? f * (want - cur) + cur : want;
        _zoom = Ease(_zoom, _zoomT, f);
        _orbit.Ease(_pitchT, _yawT, dt);
        // the port's addition over the original's distance: the mouse wheel's zoom
        if (_epoch != MouseCamera.ZoomEpoch) { _epoch = MouseCamera.ZoomEpoch; _wheel = MouseCamera.ZoomOf(ViewZoom.Kind.Chase); _orbit.Reset(); }   // a new start: no easing from the old
        _wheel = ChaseZoom.Ease(_wheel, MouseCamera.ZoomOf(ViewZoom.Kind.Chase), dt);
        Fov = BaseFov;
        var (pitch, yaw) = _orbit.View;
        if (Fixed is { } fx) (_orbit.Pitch, _orbit.Yaw, pitch, yaw) = (fx.Pitch, fx.Yaw, fx.Pitch, fx.Yaw);

        // the car's heading from its flattened forward row, slerped at 2·dt
        var fwd = Game(-t.Basis.Z);
        if (fwd.X * fwd.X + fwd.Z * fwd.Z >= 0.001f)
        {
            float want = MathF.Atan2(fwd.X, fwd.Z);
            if (float.IsNaN(_heading)) _heading = want;
            else _heading += Mathf.Wrap(want - _heading, -Mathf.Pi, Mathf.Pi) * MathF.Min(f, 1f);
        }
        if (float.IsNaN(_heading)) _heading = 0;

        // M = RotX(pitch) · RotY(yaw) · H(heading), rows, applied to row vectors; the camera is at (0,0,−d)·M from the centre
        var (sp, cp) = MathF.SinCos(pitch);
        var (sy, cy) = MathF.SinCos(yaw);
        var (sh, ch) = MathF.SinCos(_heading);
        Basis3 rx = new(new(1, 0, 0), new(0, cp, sp), new(0, -sp, cp));
        Basis3 ry = new(new(cy, 0, sy), new(0, 1, 0), new(-sy, 0, cy));
        Basis3 h = new(new(ch, 0, -sh), new(0, 1, 0), new(sh, 0, ch));
        var m = rx * ry * h;
        float d = MathF.Min(_zoom * Radius * _wheel, 150f);
        var centre = Game(centreG);
        if (Fixed is { } lift) centre.Y += lift.Lift;
        var eye = centre - m.R2 * d;
        float ground = Ground?.Invoke(eye.X, eye.Z) ?? float.NegativeInfinity;
        if (eye.Y - 0.1f < ground + 0.5f) eye.Y = ground + 0.6f;
        Position = Game(eye);
        LookAt(Position + Game(m.R2), Game(m.R1));
    }

    private void DeathOrbit(Transform3D t, Vector3 centreG, float dt)
    {
        float k = 1f - Mathf.Exp(-6f * dt);
        if (!_orbiting)
        {
            var from = Position - t.Origin;
            _death.Start(Mathf.Atan2(from.X, from.Z));
            _orbiting = true;
        }
        _death.Advance(dt, I76.Game.Sim.SimClock.Tick);
        float d = MathF.Min(_zoom * Radius, 150f);
        // the angle is the orbit's; only the distance and the height ease in (easing the position cut inside the circle)
        var off = Position - centreG;
        float r = Mathf.Lerp(new Vector2(off.X, off.Z).Length(), d * 1.3f, k * 0.5f);
        float y = Mathf.Lerp(off.Y, d * 0.6f, k * 0.5f);
        Position = centreG + new Vector3(Mathf.Sin(_death.Yaw) * r, y, Mathf.Cos(_death.Yaw) * r);
        LookAt(centreG, Vector3.Up);
    }

    /// <summary>A 3×3 of rows, multiplied as the original's rotation composition does (row vectors: a then b).</summary>
    private readonly record struct Basis3(Vector3 R0, Vector3 R1, Vector3 R2)
    {
        private static Vector3 Row(Vector3 r, Basis3 b) => r.X * b.R0 + r.Y * b.R1 + r.Z * b.R2;
        public static Basis3 operator *(Basis3 a, Basis3 b) => new(Row(a.R0, b), Row(a.R1, b), Row(a.R2, b));
    }
}
