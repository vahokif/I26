using Godot;
using I76.Game.Sim;
using I76.Sim.Math;
using I76.Sim.Vehicles;
using I76.Sim.Views;
using I76.Sim.Weapons;

namespace I76.Game.Camera;

/// <summary>
/// The views that follow something (the geometry is I76.Sim.Views.TargetViews):
/// <list type="bullet">
/// <item><b>Target</b> (F4) and <b>Reverse</b> (F5): from the player towards the
/// radar target, or from the target towards the player. While the radar holds a target the grace timer is 3 s; when it is gone
/// (killed, or past the radar's range) the view stays on the last place it was seen and the timer runs down, then
/// <see cref="Expired"/> pops the camera stack.</item>
/// <item><b>Projectile</b> (F11): 5 behind the round, pitched 20 degrees, turning with it. The round is the one the
/// view was set up on; once the latest round is not that one any more (it hit, or another was fired) the timer is 1 s, and
/// the view keeps the pose it had.</item>
/// </list>
/// Everything is stored in absolute game coordinates, so the floating origin can move under it.
/// </summary>
public partial class TargetViewCamera : Camera3D
{
    public enum View { Target, Reverse, Projectile }

    public View Mode { get; private set; }
    public Node3D? Player => PlayerNode?.Invoke();
    public Func<Node3D?>? PlayerNode { get; set; }
    /// <summary>The radar's target now (none if it is gone).</summary>
    public Func<Vehicle?>? RadarTarget { get; set; }
    public Func<Vehicle, Node3D?>? CarOf { get; set; }
    /// <summary>The latest round the player fired, and whether a round is still in flight.</summary>
    public Func<Round?>? LatestRound { get; set; }
    public Func<Round, bool>? InFlight { get; set; }
    public Func<System.Numerics.Vector3>? Origin { get; set; }
    public Func<float>? Alpha { get; set; }
    public Func<float, float, float>? Ground { get; set; }
    /// <summary>The timer ran out: the camera stack pops.</summary>
    public Action? Expired { get; set; }

    private int _epoch = MouseCamera.ZoomEpoch;
    private float _timer;
    private float _wheelTarget = MouseCamera.ZoomOf(ViewZoom.Kind.Target), _wheelRound = MouseCamera.ZoomOf(ViewZoom.Kind.Projectile);
    private System.Numerics.Vector3 _seen;      // the target's last place, game coordinates
    private Round? _round;
    private System.Numerics.Vector3 _roundPos;  // the followed round's last pose
    private Rows _roundRot = Rows.Identity;

    public TargetViewCamera() { Near = 0.25f; Far = 20000; Fov = 73.74f; }

    /// <summary>
    /// The camera's object when it is not the player's car: F11 sets it to the round it follows, kept
    /// at the round's last pose through the grace. F4 and F5 keep the player's car. Godot's frame; null for the player's car.
    /// </summary>
    public Vector3? Subject => Current && Mode == View.Projectile && _round is not null ? Coords.ToGodot(_roundPos - (Origin?.Invoke() ?? default)) : null;

    /// <summary>The setup: the view and, for F11, the round it follows.</summary>
    public void Start(View mode, Round? round = null)
    {
        Mode = mode;
        _round = round;
        _timer = mode == View.Projectile ? TargetViews.ProjectileGrace : TargetViews.TargetGrace;
        var o = Origin?.Invoke() ?? default;
        if (mode == View.Projectile && round is not null) Remember(round, o);
        else if (RadarTarget?.Invoke() is { } t) _seen = Where(t, o);
    }

    private System.Numerics.Vector3 Where(Vehicle t, System.Numerics.Vector3 o) =>
        CarOf?.Invoke(t) is { } n && IsInstanceValid(n) ? o + Coords.ToGame(n.GlobalPosition) : t.Pos.ToVector3();

    private void Remember(Round r, System.Numerics.Vector3 o)
    {
        float a = Alpha?.Invoke() ?? 1f;
        _roundPos = r.PrevPos + (r.Pos - r.PrevPos) * a;
        _roundRot = r.Rot;
    }

    public override void _Process(double delta)
    {
        if (!Current || Player is not { } pn) return;
        Fov = ViewFov.Main;
        float dt0 = Mathf.Clamp((float)delta, 0.001f, 0.2f);
        if (_epoch != MouseCamera.ZoomEpoch) { _epoch = MouseCamera.ZoomEpoch; _wheelTarget = MouseCamera.ZoomOf(ViewZoom.Kind.Target); _wheelRound = MouseCamera.ZoomOf(ViewZoom.Kind.Projectile); }
        _wheelTarget = ChaseZoom.Ease(_wheelTarget, MouseCamera.ZoomOf(ViewZoom.Kind.Target), dt0);
        _wheelRound = ChaseZoom.Ease(_wheelRound, MouseCamera.ZoomOf(ViewZoom.Kind.Projectile), dt0);
        if (Controls.Down("zoom_factor_reset")) MouseCamera.ResetZoom(Mode == View.Projectile ? ViewZoom.Kind.Projectile : ViewZoom.Kind.Target);
        float dt = Mathf.Clamp((float)delta, 0.001f, 0.2f);
        var o = Origin?.Invoke() ?? default;
        if (Mode == View.Projectile)
        {
            var latest = LatestRound?.Invoke();
            if (latest is not null && latest == _round) _timer = TargetViews.ProjectileGrace;
            else
            {
                _timer -= dt;
                if (_timer <= 0) { Expired?.Invoke(); return; }
            }
            if (_round is not null && InFlight?.Invoke(_round) == true) Remember(_round, o);
            var (e, f, u) = TargetViews.Projectile(_roundPos - o, _roundRot, _wheelRound);
            Place(e, f, u);
            return;
        }
        if (RadarTarget?.Invoke() is { } t) { _timer = TargetViews.TargetGrace; _seen = Where(t, o); }
        else
        {
            _timer -= dt;
            if (_timer <= 0) { Expired?.Invoke(); return; }
        }
        var player = Coords.ToGame(pn.GlobalPosition);
        var other = _seen - o;
        var (eye, fwd) = Mode == View.Target ? TargetViews.Look(player, other, Ground, _wheelTarget) : TargetViews.Look(other, player, Ground, _wheelTarget);
        Place(eye, fwd, TargetViews.Up);
    }

    private void Place(System.Numerics.Vector3 eye, System.Numerics.Vector3 fwd, System.Numerics.Vector3 up)
    {
        var f = Coords.ToGodot(fwd);
        var u = Coords.ToGodot(up);
        if (f.LengthSquared() < 1e-8f) return;
        if (Mathf.Abs(f.Normalized().Dot(u.Normalized())) > 0.999f) u = Vector3.Right;
        GlobalTransform = new Transform3D(Basis.LookingAt(f, u), Coords.ToGodot(eye));
    }
}
