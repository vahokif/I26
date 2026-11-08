using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Net;

/// <summary>
/// A network client's prediction of its own car (docs/multiplayer.md, "Prediction"). Each input tick the car is
/// stepped at once with that input, by the same sim code and frame deltas as the server's
/// (<see cref="SimSession.PredictTick"/>), so it is drawn at the predicted present rather than 100 ms behind the
/// newest snapshot. Each snapshot carries the car at full precision (<see cref="OwnMotion"/>) after the last input
/// the server applied (the ack): the car is put back to it, the inputs since are stepped again, and the result is
/// compared with what had been predicted. The other cars are not predicted: their pairs are tested against them as
/// the newest snapshot has them, moved on along their velocity. Firing, damage and rounds stay the server's
/// (<see cref="PredictDamage"/>). A correction is drawn away over <see cref="SmoothSeconds"/> (a render-only offset
/// that decays; the car itself snaps); one too big for that (a respawn, a regen pad, a teleport, a hard crash) is not.
/// </summary>
public sealed class OwnCarPredictor
{
    private const int Ring = 256;

    public ClientWorld World { get; }
    private readonly MeleeClient _client;
    private Driver? _driver;
    private readonly CarInput[] _inputs = new CarInput[Ring];
    private readonly OwnMotion?[] _predicted = new OwnMotion?[Ring];
    private readonly uint[] _predictedTick = new uint[Ring];
    /// <summary>Each tick's predictions whose hashes went to the server (a replay may give another), for a confirmation to name.</summary>
    private readonly List<(uint Hash, OwnMotion Motion)>[] _hashed = [.. Enumerable.Range(0, Ring).Select(_ => new List<(uint, OwnMotion)>(2))];
    private uint _lastInput, _reconciled;
    private long _offset;
    private OwnMotion.SlowMotion? _slow;
    private readonly PredictDamage _damage;
    private readonly List<Vehicle> _order = [];
    private readonly Dictionary<int, Vehicle> _ghosts = [];
    private readonly List<(Vehicle Ghost, Double3 Pos, Vector3 Vel, uint At)> _moving = [];

    /// <summary>The own shots drawn at once (null when they are not: <c>predictFire</c> false), and kept square with the server's.</summary>
    public OwnShots? Shots { get; }

    /// <summary>A snapshot has been taken: the car is predicted from here on.</summary>
    public bool Ready { get; private set; }
    /// <summary>The predicted car before and after the newest input tick (what the view draws between).</summary>
    public Rows PrevRot { get; private set; } = Rows.Identity;
    public Rows Rot { get; private set; } = Rows.Identity;
    public Double3 PrevPos { get; private set; }
    public Double3 Pos { get; private set; }

    /// <summary>How long a correction takes to be drawn away (the offset's time constant, seconds).</summary>
    public float SmoothSeconds { get; set; } = 0.1f;
    /// <summary>A correction bigger than this (metres), or than <see cref="SnapRadians"/>, is drawn at once.</summary>
    public double SnapDistance { get; set; } = 8;
    public float SnapRadians { get; set; } = MathF.PI / 4;
    /// <summary>A prediction within this of the server's position (metres) counts as agreeing.</summary>
    public double Tolerance { get; set; } = 0.01;

    private Vector3 _posOffset;
    private Quaternion _rotOffset = Quaternion.Identity;

    /// <summary>Counters: snapshots reconciled, of those compared with a prediction for the acked tick, the ones off by more than <see cref="Tolerance"/>, and not exact; snapped corrections.</summary>
    public int Reconciles, Compared, Corrections, Inexact, Snaps;
    /// <summary>Snapshots whose motion came confirmed (by the hash of a prediction) rather than sent, and confirmations naming a prediction no longer kept.</summary>
    public int Confirmed, Unresolved;
    /// <summary>The last compared prediction's distance from the server's car, the largest, and the sum (metres).</summary>
    public double LastError, MaxError, SumError;
    /// <summary>The last correction as drawn (metres): how far the predicted present moved.</summary>
    public double LastCorrection;
    /// <summary>Each comparison: the acked input tick, the distance, and whether it was exact.</summary>
    public event Action<uint, double, bool>? OnCompare;

    /// <param name="predictFire">Also fire the own car's weapons at once (its flashes, sounds and rounds drawn from the predicted car).</param>
    public OwnCarPredictor(ClientWorld world, MeleeClient client, bool predictFire = true)
    {
        World = world;
        _client = client;
        _damage = new PredictDamage(world.Setup.Damage);
        world.Predicted = true;
        world.Prediction = this;
        if (predictFire)
        {
            Shots = new OwnShots(world);
            world.OwnShots = Shots;
            world.Setup.Combat.PredictedShot = Shots.OnShot;
        }
    }

    private Vehicle? Car => World.Own?.Vehicle;

    private Driver? EnsureDriver()
    {
        if (Car is not { } car) return null;
        if (_driver?.Car != car)
        {
            if (_driver is not null) World.Session.RemoveDriver(_driver);
            _driver = World.Session.AddDriver(car);
        }
        return _driver;
    }

    /// <summary>The input for client tick <paramref name="tick"/> (just sent): the car steps with it now.</summary>
    public void Step(uint tick, CarInput input)
    {
        _inputs[tick % Ring] = input;
        _lastInput = tick;
        if (!Ready || EnsureDriver() is null) return;
        PrevRot = Rot;
        PrevPos = Pos;
        StepOne(tick, fresh: true);
        Rot = Car!.Rot;
        Pos = Car.Pos;
    }

    private void StepOne(uint k, bool fresh)
    {
        // input k runs in the server's tick whose count before it is k + offset − 1 (the ack's tick less one, moved on)
        int tick = (int)(k + _offset - 1);
        PlaceGhosts(tick);
        World.Session.NetEndTick = (int?)_client.EndTick;
        _damage.Fresh = fresh;
        World.Session.PredictTick(_driver!, _inputs[k % Ring], tick, _order, _damage, fresh);
        if (Shots is { } shots)
        {
            shots.Tick = k;
            World.Session.PredictFire(_driver!, fresh);
        }
        else World.Session.PredictSelect(_driver!);   // the weapon cycle and link, which PredictFire runs after the triggers
        var m = OwnMotion.Capture(Car!, _driver);
        _predicted[k % Ring] = m;
        if (_predictedTick[k % Ring] != k) _hashed[k % Ring].Clear();
        _predictedTick[k % Ring] = k;
        // its hash goes with the next inputs: the server confirms by it instead of sending the motion when it agrees
        uint h = OwnMotion.Hash(m.Key());
        var hashed = _hashed[k % Ring];
        if (!hashed.Exists(x => x.Hash == h))
        {
            if (hashed.Count >= 4) hashed.RemoveAt(0);
            hashed.Add((h, m));
        }
        _client.ReportMotion(k, h);
    }

    /// <summary>The prediction for client tick <paramref name="tick"/> whose hash ends in <paramref name="echo"/> (a confirmed motion), if still kept.</summary>
    private OwnMotion? Hashed(uint tick, ushort echo)
    {
        if (_predictedTick[tick % Ring] != tick) return null;
        foreach (var (h, m) in _hashed[tick % Ring]) if ((ushort)h == echo) return m;
        return null;
    }

    /// <summary>
    /// Takes the newest snapshot, if it is new: puts the car back as the server had it after the acked input, steps
    /// the inputs since again, and draws the difference away.
    /// </summary>
    public void Reconcile()
    {
        var snaps = _client.Snapshots;
        if (snaps.Count == 0) return;
        var s = snaps.Values[^1];
        if (s.Tick <= _reconciled || (s.Motion is null && s.MotionEcho is null) || EnsureDriver() is null) return;
        // a snapshot of another build of the car (a reconfigured respawn): one of the next build waits for its car-changed
        // event to rebuild the mirror's car; one of the build before is passed over
        if (s.Own is { } own && own.Epoch != World.OwnEpoch)
        {
            if ((byte)(own.Epoch - World.OwnEpoch) >= 128) _reconciled = s.Tick;
            return;
        }
        uint acked = s.AckedInput;
        var motion = s.Motion;
        if (motion is null)
        {
            // confirmed: the server's car is the one predicted for the acked tick with that hash
            motion = Hashed(acked, s.MotionEcho!.Value);
            if (motion is null)
            {
                // not kept (or never ours): the server is asked for the motion itself until it comes
                _reconciled = s.Tick;
                Unresolved++;
                _client.NeedExact = true;
                return;
            }
            Confirmed++;
        }
        else _client.NeedExact = false;
        var car = Car!;
        _reconciled = s.Tick;
        Reconciles++;
        if ((s.Slow ?? s.Motion?.Slow) is { } slow) _slow = slow;
        if (Ready && acked > 0 && _predictedTick[acked % Ring] == acked && _predicted[acked % Ring] is { } was)
        {
            double err = was.Distance(motion);
            bool exact = was.Key().AsSpan().SequenceEqual(motion.Key());
            Compared++;
            if (!exact) Inexact++;
            if (err > Tolerance) Corrections++;
            LastError = err;
            MaxError = Math.Max(MaxError, err);
            SumError += err;
            OnCompare?.Invoke(acked, err, exact);
        }
        var beforeRot = Rot;
        var beforePos = Pos;
        int gen = car.NetGeneration;
        bool wasReady = Ready;
        Shots?.Reconcile(s, car);
        // the looks and the driver's view of the car (as the mirror would write them), then the motion over them
        foreach (var c in s.Cars) if (c.Serial == World.OwnSerial) { c.ApplyTo(car); break; }
        s.Own?.ApplyTo(car, World.Setup.Combat, World.Car);
        if (Shots is not null) s.Fire?.ApplyTo(car);
        motion.ApplyTo(car, _driver, _slow, World.Car, World.Structure);
        _offset = (long)s.Tick - acked;
        BuildGhosts(s);
        Ready = true;
        // the inputs the server has not applied yet; more than the ring holds and the car waits at the server's
        uint from = acked + 1, to = _lastInput;
        if (to >= from && to - acked >= Ring - 1) from = to + 1;
        Rows prevRot = car.Rot;
        Double3 prevPos = car.Pos;
        for (uint k = from; k <= to && to >= from; k++)
        {
            if (k == to) { prevRot = car.Rot; prevPos = car.Pos; }
            StepOne(k, fresh: false);
        }
        PrevRot = prevRot;
        PrevPos = prevPos;
        Rot = car.Rot;
        Pos = car.Pos;
        // the drawn correction: the present's jump, unless it is too big to slide (or there was nothing before)
        var d = new Vector3((float)(beforePos.X - Pos.X), (float)(beforePos.Y - Pos.Y), (float)(beforePos.Z - Pos.Z));
        var qb = ToQuat(beforeRot);
        var qa = ToQuat(Rot);
        float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(qb, qa)), 0f, 1f));
        LastCorrection = wasReady ? d.Length() : 0;
        if (!wasReady || gen != car.NetGeneration || d.Length() > SnapDistance || angle > SnapRadians)
        {
            if (wasReady) Snaps++;
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
            if (!wasReady) PrevRot = Rot;
            if (!wasReady) PrevPos = Pos;
        }
        else if (LastCorrection > 0 || angle > 0)
        {
            _posOffset += d;
            // the offset after the actual rotation: drawn = actual then offset, so the new offset is (old ∘ before) undone by after
            _rotOffset = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Concatenate(Quaternion.Inverse(qa), qb), _rotOffset));
        }
    }

    /// <summary>The frame: the correction's offset decays.</summary>
    public void Smooth(double dt)
    {
        if (Shots is { } shots)
        {
            shots.Rtt = _client.Rtt / 1000.0;
            shots.Sweep(World.Delay);
        }
        float k = SmoothSeconds <= 0 ? 0 : MathF.Exp(-(float)dt / SmoothSeconds);
        _posOffset *= k;
        _rotOffset = Quaternion.Slerp(Quaternion.Identity, _rotOffset, k);
        if (_posOffset.LengthSquared() < 1e-8f) _posOffset = Vector3.Zero;
    }

    /// <summary>The car to draw at <paramref name="alpha"/> between the last two input ticks, with the correction's offset.</summary>
    public (Rows Rot, Double3 Pos) Pose(float alpha)
    {
        var rows = new Rows(Vector3.Lerp(PrevRot.Right, Rot.Right, alpha), Vector3.Lerp(PrevRot.Up, Rot.Up, alpha), Vector3.Lerp(PrevRot.Forward, Rot.Forward, alpha));
        var q = Quaternion.Normalize(Quaternion.Concatenate(ToQuat(Orthonormal(rows)), _rotOffset));
        double Lerp(double a, double b) => a + (b - a) * alpha;
        var pos = new Double3(Lerp(PrevPos.X, Pos.X) + _posOffset.X, Lerp(PrevPos.Y, Pos.Y) + _posOffset.Y, Lerp(PrevPos.Z, Pos.Z) + _posOffset.Z);
        return (ToRows(q), pos);
    }

    /// <summary>
    /// The server tick the car is drawn at by <see cref="Pose"/> (fractional, counted as <see cref="CarState.SampleTick"/>):
    /// input k runs in the server's tick k + offset − 1, so the car after it is that of tick k + offset.
    /// </summary>
    public double PresentTick(float alpha) => (long)_lastInput + _offset - 1 + alpha;

    /// <summary>The offset still being drawn away (metres).</summary>
    public float Offset => _posOffset.Length();

    // ---- the other cars, as the newest snapshot has them ----

    private void BuildGhosts(Snapshot s)
    {
        _order.Clear();
        _moving.Clear();
        var seen = new HashSet<int>();
        foreach (var c in s.Cars)
        {
            if (c.Serial == World.OwnSerial) { _order.Add(Car!); continue; }
            if (World.Car(c.Serial) is not { } real) continue;
            if (!_ghosts.TryGetValue(c.Serial, out var g)) _ghosts[c.Serial] = g = new Vehicle();
            FieldCopy.Into(real, g);
            (g.Pos, g.Rot, g.Vel, g.AngVel, g.Flags, g.Wreck, g.Speed) = (c.Pos, c.Rot, c.Vel, c.AngVel, c.Flags, c.Wreck, c.Speed);
            g.P = real.P;
            _order.Add(g);
            // a far car's state may be of an earlier tick than the snapshot's (interest management)
            _moving.Add((g, c.Pos, c.Vel, c.SampleTick == 0 ? s.Tick : c.SampleTick));
            seen.Add(c.Serial);
        }
        foreach (var gone in _ghosts.Keys.Where(k => !seen.Contains(k)).ToList()) _ghosts.Remove(gone);
    }

    /// <summary>The others at the start of server tick <paramref name="tick"/>: as the snapshot of that tick would have them, moved on from the newest.</summary>
    private void PlaceGhosts(int tick)
    {
        foreach (var (g, p, v, at) in _moving)
        {
            double t = (tick - (long)at) * World.Session.TickSeconds;
            g.Pos = new Double3(p.X + v.X * t, p.Y + v.Y * t, p.Z + v.Z * t);
        }
    }

    // ---- rotations ----

    private static Rows Orthonormal(Rows r)
    {
        var f = Vector3.Normalize(r.Forward);
        var right = Vector3.Normalize(Vector3.Cross(r.Up, f));
        return new Rows(right, Vector3.Cross(f, right), f);
    }

    /// <summary>The rotation that takes local to world as the rows do (<see cref="Rows.ToWorld"/>).</summary>
    public static Quaternion ToQuat(Rows r) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
        r.Right.X, r.Right.Y, r.Right.Z, 0, r.Up.X, r.Up.Y, r.Up.Z, 0, r.Forward.X, r.Forward.Y, r.Forward.Z, 0, 0, 0, 0, 1)));

    public static Rows ToRows(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        return new Rows(new(m.M11, m.M12, m.M13), new(m.M21, m.M22, m.M23), new(m.M31, m.M32, m.M33));
    }
}

/// <summary>
/// The damage model as the prediction's step sees it: the death and engine timer runs (the ignition, the engine
/// starting and stopping, nitro running out are the driver's to see at once), and only on a fresh tick does it leave
/// its ignition and engine events for the views; replays leave none. Everything that damages is the server's.
/// </summary>
public sealed class PredictDamage(DamageModel? model) : IStepDamage, IHeliDamage
{
    /// <summary>A tick predicted for the first time (not a replay after a snapshot).</summary>
    public bool Fresh;

    public void Timer(Vehicle v, float dt)
    {
        if (model is null) return;
        // dying (0x8000 without 0x4000): the death's effects are the server's; its timer runs as the server's, so a dead
        // car waiting for RAISE_DEAD is predicted exactly
        bool dying = ((uint)v.Flags & 0xc000) == 0x8000;
        int n = model.Events.Count;
        model.Timer(v, dt, effects: !dying);
        for (int i = model.Events.Count - 1; i >= n; i--)
            if (!Fresh || model.Events[i] is not (IgnitionStarted or EngineStarted or EngineStopped)) model.Events.RemoveAt(i);
    }

    // a helicopter's kill and crash change the car (it falls, turning; its controls go to zero), the explosions are the server's
    public void ShootDown(Vehicle v)
    {
        v.Flags |= HeliStep.Falling;
        v.AngVel = new Vector3(0.2f, 0, -0.5f);
    }

    public void Crash(Vehicle v) => (v.Throttle, v.Steer) = (0, 0);

    public void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal) { }
    public void Landing(Vehicle v, Vector3 impactVel, Vector3 n) { }
    public void FallKill(Vehicle v) { }
    public void Rough(Vehicle v, float dt) { }
    public void StaticContact(Vehicle v, I76.Sim.Collision.StaticObject o, Contact rec) { }
}
