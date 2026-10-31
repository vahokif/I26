using Godot;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using N = System.Numerics;

namespace I76.Game.Sim;

/// <summary>
/// Scripted driving for measurements (either vehicle model, the same inputs),
/// read from the environment; none of it runs without them:
///   I76_TEST_CRUISE=v           throttle and brake held to speed v (a P controller on the sim's speed)
///   I76_TEST_STEER=s@a-b;...    the steer input set to s from sim tick a to b (+1 is full left), no ramp
///   I76_TEST_HEADING=1          outside those spans, steer to hold the heading the car started with
///   I76_TEST_BRAKE=a-b          full brake from tick a to b
///   I76_TEST_BOOST=v            the car's velocity set to v along its heading every tick once it is moving (a car faster than any in the game, to
///                               cross a big map: a cheat for captures)
///   I76_TELEMETRY=1             one line a tick: speed, yaw rate, heading, height over the ground, attitude, slip
///                               (=phys: Modern Physics also logs heading and steer at each of its 120 Hz steps)
/// </summary>
public sealed class DriveTest
{
    private float? _cruise;
    private readonly List<(float S, int A, int B)> _steer = [];
    private (int A, int B)? _brake;
    private bool _heading, _log;
    private List<N.Vector2>? _route;
    private int _rk = -1;
    private float _routeSpeed = 1000;
    private float? _boost;
    private N.Vector3? _startFwd;
    private float _prevHeading = float.NaN;

    /// <summary>Whether it writes the car's controls (else it only logs, and the keys drive).</summary>
    public bool Drives => _cruise is not null || _steer.Count > 0 || _brake is not null || _heading || _route is not null || _boost is not null;

    public static DriveTest? FromEnvironment()
    {
        string? E(string n) => System.Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;
        var t = new DriveTest();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (E("I76_TEST_CRUISE") is { } c) t._cruise = float.Parse(c, inv);
        if (E("I76_TEST_STEER") is { } st)
            foreach (var part in st.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = part.Split('@'); var ab = at[1].Split('-');
                t._steer.Add((float.Parse(at[0], inv), int.Parse(ab[0]), int.Parse(ab[1])));
            }
        if (E("I76_TEST_BRAKE") is { } br && br.Split('-') is [var a, var b]) t._brake = (int.Parse(a), int.Parse(b));
        if (E("I76_TEST_BOOST") is { } bo) t._boost = float.Parse(bo, inv);
        t._heading = E("I76_TEST_HEADING") is not null;
        t._log = E("I76_TELEMETRY") is not null;
        // I76_TEST_ROUTE="SPEED;x,z x,z ...": round the route as `i76 airtime` drives it (aim at point k, the next within 20)
        if (E("I76_TEST_ROUTE") is { } rt && rt.Split(';') is [var rs, var pts])
        {
            t._routeSpeed = float.Parse(rs, inv);
            t._route = pts.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(q => q.Split(',')).Select(q => new N.Vector2(float.Parse(q[0], inv), float.Parse(q[1], inv))).ToList();
        }
        return t._cruise is null && t._steer.Count == 0 && t._brake is null && !t._heading && !t._log && t._route is null && t._boost is null ? null : t;
    }

    public void Controls(Vehicle v, int tick)
    {
        _startFwd ??= v.Rot.Forward with { Y = 0 };
        if (_boost is { } bs && v.Speed > 5) { var hf = N.Vector3.Normalize(v.Rot.Forward with { Y = 0 }); v.Vel = new N.Vector3(hf.X * bs, MathF.Min(v.Vel.Y, 0), hf.Z * bs); }
        if (_route is { } r)
        {
            var p = new N.Vector2((float)v.Pos.X, (float)v.Pos.Z);
            if (_rk < 0) _rk = (Enumerable.Range(0, r.Count).MinBy(i => N.Vector2.Distance(p, r[i])) + 1) % r.Count;
            while (N.Vector2.Distance(p, r[_rk]) < 20) _rk = (_rk + 1) % r.Count;
            var to = r[_rk] - p;
            float err = Wrap(MathF.Atan2(to.X, to.Y) - MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z));
            v.Steer = err > 0.03f ? -1 : err < -0.03f ? 1 : 0;
            v.Throttle = v.Speed < _routeSpeed ? 0.9999f : v.Speed > _routeSpeed + 3 ? -0.9999f : 0;
            return;
        }
        if (_cruise is { } target)
        {
            float signed = v.Speed * (N.Vector3.Dot(v.Vel, v.Rot.Forward) < 0 ? -1 : 1);
            v.Throttle = Math.Clamp((target - signed) * 0.6f, -1f, 0.9999f);
        }
        if (_brake is { } br && tick >= br.A && tick <= br.B) v.Throttle = -0.9999f;
        bool steered = false;
        foreach (var (s, a, b) in _steer)
            if (tick >= a && tick <= b) { v.Steer = s; steered = true; }
        if (!steered && _heading)
        {
            var want = N.Vector3.Normalize(_startFwd.Value);
            float err = N.Vector3.Dot(want, -v.Rot.Right);          // + when the wanted heading is to the left
            v.Steer = Math.Clamp(err * 3f, -1f, 1f);
        }
        else if (!steered && _steer.Count > 0) v.Steer = 0;
    }

    public void Log(Vehicle v, IGround ground, int tick, float dt)
    {
        if (!_log) return;
        var f = v.Rot.Forward;
        float heading = MathF.Atan2(f.X, f.Z);
        float rate = float.IsNaN(_prevHeading) ? 0 : Wrap(heading - _prevHeading) / dt;
        _prevHeading = heading;
        var (h, _) = ground.Query(v.Pos.X, v.Pos.Y, v.Pos.Z);
        float pitch = MathF.Asin(Math.Clamp(f.Y, -1, 1)), roll = MathF.Asin(Math.Clamp(v.Rot.Right.Y, -1, 1));
        float slip = N.Vector3.Dot(v.Vel, v.Rot.Right);
        GD.Print(FormattableString.Invariant($"tel {tick} x {v.Pos.X:0.00} y {v.Pos.Y:0.000} z {v.Pos.Z:0.00} v {v.Speed:0.000} yawrate {rate:0.0000} heading {heading:0.0000} above {v.Pos.Y - h:0.000} pitch {pitch:0.0000} roll {roll:0.0000} slip {slip:0.000} steer {v.Steer:0.000} thr {v.Throttle:0.000} air {((v.Flags & VehicleFlags.Airborne) != 0 ? 1 : 0)} vy {v.Vel.Y:0.000} gear {v.Engine.Gear} eforce {v.Engine.Force:0}"));
    }

    private static float Wrap(float a) => a > MathF.PI ? a - 2 * MathF.PI : a < -MathF.PI ? a + 2 * MathF.PI : a;
}
