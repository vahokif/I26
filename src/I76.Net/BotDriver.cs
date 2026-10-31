using System.Numerics;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Net;

/// <summary>
/// A headless player's driving (i26-server --bots, and the game's own car under the capture hook I76_NET_BOT): one input a
/// tick from the newest snapshot. A hunter makes for the nearest live car and fires when it points at it; a roamer drives
/// to random points within 400 m of where it started and hunts now and then. Dead, it asks to come back once the 5 s are up.
/// In a co-op mission it is a teammate: it hunts only A.I. cars of another team than its own and not a bystander's (what the
/// server says the cars are, <see cref="Learn"/>), and while none is near the players drive in a column: each behind the living
/// player of the next seat ahead, the first behind the nearest allied car (T01's Taurus).
/// </summary>
public sealed class BotDriver(bool roams, int seed)
{
    private int _ticks, _deadTicks, _until, _stuck, _backOff, _trailOf = -1;
    private Vector3? _home, _waypoint;
    private readonly Random _rng = new(seed);
    private readonly Dictionary<int, CarInfo> _info = [];
    private readonly List<Vector3> _trail = [];
    private JoinAccept? _learnt;

    /// <summary>What the server says a car is (the join's list, an answer to <see cref="MeleeClient.AskCars"/>): co-op tells friend from foe by it.</summary>
    public void Learn(CarInfo info) => _info[info.Serial] = info;

    /// <summary>The newest snapshot's cars it has not been told about, to ask the server (<see cref="MeleeClient.AskCars"/>).</summary>
    public List<int> Unknown(MeleeClient c) => c.Snapshots.Count == 0 ? [] : [.. c.Snapshots.Values[^1].Cars.Select(x => x.Serial).Where(s => !_info.ContainsKey(s))];

    public CarInput Next(MeleeClient c)
    {
        int t = _ticks++;
        if (c.Snapshots.Count == 0 || c.Accept is not { } acc) return default;
        if (_learnt != acc) { _learnt = acc; foreach (var info in acc.Cars) Learn(info); }
        var snap = c.Snapshots.Values[^1];
        var me = snap.Cars.FirstOrDefault(x => x.Serial == acc.CarSerial);
        if (me is null) return default;
        // dead: Space (RAISE_DEAD) once the 5 s are up, and again each second until the server brings the car back
        if ((me.Flags & VehicleFlags.Dead) != 0 || me.Wreck)
        {
            int dead = _deadTicks++;
            int wait = acc.Rate.TicksFor(5.5f), every = acc.Rate.TicksFor(1f);
            return dead >= wait && (dead - wait) % every == 0 ? new CarInput { Commands = CarCommands.RaiseDead } : default;
        }
        _deadTicks = 0;
        var pos = me.Pos.ToVector3();
        if (acc.Coop is not null) return Teammate(me, snap, t);
        if (roams && t % 600 <= 450)
        {
            _home ??= pos;
            if (_waypoint is null || t >= _until || Vector3.DistanceSquared(pos, _waypoint.Value) < 30 * 30)
            {
                _waypoint = _home.Value + new Vector3(_rng.NextSingle() * 800 - 400, 0, _rng.NextSingle() * 800 - 400);
                _until = t + 300;
            }
            return Toward(me, _waypoint.Value, t, false);
        }
        CarState? best = null;
        float bd = float.MaxValue;
        foreach (var o in snap.Cars)
        {
            if (o.Serial == me.Serial || (o.Flags & VehicleFlags.Dead) != 0 || o.Wreck) continue;
            float d = Vector3.DistanceSquared(o.Pos.ToVector3(), pos);
            if (d < bd) (bd, best) = (d, o);
        }
        var i = best is null ? new CarInput { Buttons = CarButtons.Analog, Throttle = 26000 } : Toward(me, best.Pos.ToVector3(), t, bd < 200 * 200);
        if (t % 90 == 0) i.Commands |= CarCommands.TargetNearest;
        return i;
    }

    /// <summary>
    /// Co-op: the nearest A.I. car of another team (not a bystander's, team 0) is the enemy. Away from any, the players drive in a
    /// column in seat order, each 15 m behind the one ahead along the way it drove, the first behind the nearest allied A.I. car
    /// (T01's Taurus, whom the mission has the player keep up with).
    /// </summary>
    private CarInput Teammate(CarState me, Snapshot snap, int t)
    {
        var pos = me.Pos.ToVector3();
        int? team = _info.TryGetValue(me.Serial, out var mine) ? mine.Team : null;
        // the players line up in seat order: each follows the living player of the next seat ahead of its own
        CarState? foe = null, ally = null, ahead = null;
        float fd = float.MaxValue, ad = float.MaxValue;
        int seat = mine?.Owner ?? int.MaxValue, aheadSeat = -1;
        foreach (var o in snap.Cars)
        {
            if (o.Serial == me.Serial || (o.Flags & VehicleFlags.Dead) != 0 || o.Wreck || !_info.TryGetValue(o.Serial, out var oi)) continue;
            if (oi.Owner >= 0) { if (oi.Owner < seat && oi.Owner > aheadSeat) (aheadSeat, ahead) = (oi.Owner, o); continue; }
            if (team is null || oi.Team == 0) continue;
            float d = Vector3.DistanceSquared(o.Pos.ToVector3(), pos);
            if (oi.Team != team) { if (d < fd) (fd, foe) = (d, o); }
            else if (d < ad) (ad, ally) = (d, o);
        }
        var follow = ahead ?? ally;
        CarInput i;
        if (follow is not null && (foe is null || fd > (ahead is null ? 300 * 300 : 150 * 150)))
        {
            var fp = follow.Pos.ToVector3();
            // the way it went, a crumb every 4 m (a new car to follow, or one that jumped, starts it again), so the bot keeps
            // to the road it drove instead of cutting across the desert; the crumbs it has passed are dropped
            if (_trailOf != follow.Serial || _trail.Count > 0 && Vector3.DistanceSquared(_trail[^1], fp) > 60 * 60) { _trail.Clear(); _trailOf = follow.Serial; }
            if (_trail.Count == 0 || Vector3.DistanceSquared(_trail[^1], fp) > 4 * 4) _trail.Add(fp);
            if (_trail.Count > 500) _trail.RemoveAt(0);
            while (_trail.Count > 1 && (Vector3.DistanceSquared(_trail[0], pos) < 10 * 10 || Vector3.DistanceSquared(_trail[1], pos) < Vector3.DistanceSquared(_trail[0], pos)))
                _trail.RemoveAt(0);
            i = Toward(me, _trail[0], t, false);
            // its pace: full on far behind, easing off as it closes to 15 m, braking when it comes up too fast
            float gap = Vector3.Distance(pos, fp) - 15;
            float closing = Vector3.Dot(me.Vel - follow.Vel, Vector3.Normalize(fp - pos));
            if (i.Throttle > 0) i.Throttle = (short)(Math.Clamp(gap / 25f - closing / 15f, -0.8f, 1f) * 26000);
        }
        else if (foe is not null) i = Toward(me, foe.Pos.ToVector3(), t, fd < 200 * 200);
        else i = new CarInput { Buttons = CarButtons.Analog, Throttle = 14000 };
        if (t % 90 == 0) i.Commands |= CarCommands.TargetNearest;
        return i;
    }

    private CarInput Toward(CarState me, Vector3 at, int t, bool fire)
    {
        var local = me.Rot.ToLocal(at - me.Pos.ToVector3());
        float angle = MathF.Atan2(local.X, local.Z);
        var i = new CarInput { Buttons = CarButtons.Analog, Throttle = 26000, Steer = (short)(Math.Clamp(-angle / (0.6f + me.Speed * 0.02f), -1f, 1f) * 32000) };   // gentler as it goes faster, or it weaves
        if (fire && MathF.Abs(angle) < 0.35f) i.Buttons |= CarButtons.Fire;
        // stuck against something (2 s hardly moving): back off a moment
        _stuck = me.Speed < 1.5f ? _stuck + 1 : 0;
        if (_stuck > 40) _backOff = t + 30;
        if (t < _backOff) { i.Throttle = -20000; i.Steer = (short)-i.Steer; _stuck = 0; }
        return i;
    }
}
