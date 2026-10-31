using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using I76.Net;
using I76.Server;
using I76.Sim.Math;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// Delta snapshots and interest management (docs/multiplayer.md, "Bandwidth"): the byte delta, the snapshot against a
/// baseline and without one, acks that stop or come late, a client that loses snapshots, the interest tiers and each
/// remote car drawn on its own clock.
/// </summary>
public class DeltaTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void AByteDeltaComesBackExactly()
    {
        var rng = new Random(3);
        var w = new NetWriter();
        for (int k = 0; k < 500; k++)
        {
            var b = new byte[rng.Next(0, 300)];
            rng.NextBytes(b);
            var c = (byte[])b.Clone();
            if (rng.Next(3) == 0) Array.Resize(ref c, rng.Next(0, 400));
            for (int i = 0; i < c.Length; i++) if (rng.Next(8) == 0) c[i] = (byte)rng.Next(256);
            w.Clear();
            XorDelta.Write(w, c, b);
            var r = new NetReader(w.Span);
            Assert.Equal(c, XorDelta.Read(ref r, b));
            Assert.True(r.End);
        }
        w.Clear();
        var same = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        XorDelta.Write(w, same, same);
        Assert.Equal(3, w.Length);   // the length (2 bytes) and the end
        // a delta that runs past its length is refused
        Assert.Throws<WireException>(() =>
        {
            var bad = new NetReader(new byte[] { 4, 0x0f, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0 });
            XorDelta.Read(ref bad, []);
        });
    }

    /// <summary>Every field of a car survives the snapshot within its precision: a field added to CarState and left out of its channels or cold bytes fails here.</summary>
    [Fact]
    public void EveryFieldOfACarGoesOnTheWire()
    {
        var rng = new Random(5);
        for (int k = 0; k < 50; k++)
        {
            var s = RandomCar(rng, 7);
            var back = CarState.FromRec(7, s.Rec(0));
            foreach (var f in typeof(CarState).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.Name == nameof(CarState.SampleTick)) continue;
                object? a = f.GetValue(s), b = f.GetValue(back);
                string what = $"CarState.{f.Name}";
                switch (a)
                {
                    case float fa: Assert.True(MathF.Abs(fa - (float)b!) < 0.01f + MathF.Abs(fa) * 1e-3f, $"{what}: {fa} came back {b}"); break;
                    case Double3 da:
                        var db = (Double3)b!;
                        Assert.True(Math.Abs(da.X - db.X) + Math.Abs(da.Y - db.Y) + Math.Abs(da.Z - db.Z) < 3e-3, what);
                        break;
                    case Vector3 va: Assert.True(Vector3.Distance(va, (Vector3)b!) < 0.01f, $"{what}: {va} came back {b}"); break;
                    case Rows ra: Assert.True(Vector3.Distance(ra.Forward, ((Rows)b!).Forward) < 1e-3f && Vector3.Distance(ra.Up, ((Rows)b!).Up) < 1e-3f, what); break;
                    case float[] fa: Assert.All(fa.Zip((float[])b!), p => Assert.True(MathF.Abs(p.First - p.Second) < 1e-3f, what)); break;
                    case List<(float, float)> ta:
                        var tb = (List<(float, float)>)b!;
                        Assert.Equal(ta.Count, tb.Count);
                        for (int i = 0; i < ta.Count; i++) Assert.True(MathF.Abs(MathF.IEEERemainder(ta[i].Item1 - tb[i].Item1, 2 * MathF.PI)) < 1e-3f, what);
                        break;
                    case System.Collections.IEnumerable ea: Assert.Equal(ea.Cast<object>(), ((System.Collections.IEnumerable)b!).Cast<object>()); break;
                    default: Assert.Equal(a, b); break;
                }
            }
        }
    }

    internal static CarState RandomCar(Random rng, int serial)
    {
        float F(float r) => (rng.NextSingle() * 2 - 1) * r;
        var s = new CarState
        {
            Serial = serial, Generation = (byte)rng.Next(256), Flags = (VehicleFlags)rng.Next(), Pos = new Double3(F(3000), F(200), F(3000)),
            Rot = Wire.ToRows(Quaternion.Normalize(new Quaternion(F(1), F(1), F(1), F(1)))), Vel = new(F(60), F(10), F(60)), AngVel = new(F(3), F(3), F(3)),
            Steer = F(1), Throttle = F(1), Rpm = rng.Next(0, 9000), Speed = F(60), EngineGear = rng.Next(8), DriveState = rng.Next(6),
            Reverse = rng.Next(2) == 0, Handbrake = rng.Next(2) == 0, Wreck = rng.Next(2) == 0, SteerFront0 = F(1.5f), SteerFront1 = F(1.5f), Blown = (byte)rng.Next(64),
        };
        for (int i = 0; i < 4; i++) s.Travel[i] = F(0.5f);
        for (int i = 0; i < 10; i++) s.Zones[i] = rng.Next(-5, 2000);
        rng.NextBytes(s.Parts);
        for (int i = rng.Next(3); i > 0; i--) s.Turrets.Add((F(3), F(1)));
        return s;
    }

    /// <summary>A little world of cars and rounds that moves on, comes and goes, as a server would capture it each tick.</summary>
    private sealed class World(int seed)
    {
        private readonly Random _rng = new(seed);
        public readonly SortedDictionary<int, CarState> Cars = [];
        public readonly SortedDictionary<int, RoundState> Rounds = [];
        private int _nextCar = 1, _nextRound = 1;
        public byte[] Own = [], Motion = [], Slow = [], Fire = [];
        private readonly OwnFire _fire = new();
        private readonly OwnCar _own = new() { Selected = 1 };
        private readonly OwnMotion _motion = new() { Wheels = new OwnMotion.WheelMotion[4], Rot = Rows.Identity };
        private readonly OwnMotion.SlowMotion _slow = new() { Wheels = new (float, float, float)[4] };

        public void Step(float dt)
        {
            if (Cars.Count < 12 || _rng.Next(40) == 0) Cars[_nextCar] = RandomCar(_rng, _nextCar++);
            if (_rng.Next(60) == 0 && Cars.Count > 0) Cars.Remove(Cars.Keys.ElementAt(_rng.Next(Cars.Count)));
            foreach (var c in Cars.Values)
            {
                c.Vel += new Vector3(_rng.NextSingle() - 0.5f, 0, _rng.NextSingle() - 0.5f);
                c.Pos += c.Vel * dt;
                c.Rot = Rows.Concat(c.Rot, Rows.AboutY(c.AngVel.Y * dt));
                c.Rpm = Math.Clamp(c.Rpm + _rng.Next(-200, 200), 0, 9000);
                for (int i = 0; i < 4; i++) c.Travel[i] += (_rng.NextSingle() - 0.5f) * 0.01f;
                if (_rng.Next(50) == 0) c.Zones[_rng.Next(10)] -= 10;
                if (_rng.Next(200) == 0) { c.Generation++; c.Pos += new Vector3(100, 0, 0); }
            }
            if (_rng.Next(3) == 0) Rounds[_nextRound] = new RoundState { Serial = _nextRound++, Shooter = 1, Pos = new(_rng.Next(1000), 3, _rng.Next(1000)), Rot = Rows.AboutY(_rng.Next(6)) };
            foreach (var r in Rounds.Values) { r.Travelled += 20; r.Pos += r.Rot.Forward * 20; }
            foreach (var k in Rounds.Keys.Where(k => k + 30 < _nextRound).ToList()) Rounds.Remove(k);
            _own.Speedo = _rng.NextSingle() * 100;
            if (_rng.Next(10) == 0) _own.Parts[_rng.Next(20)] = _rng.Next(-1, 500);
            if (_rng.Next(20) == 0) _own.Target = _rng.Next(0, 40);
            Own = Snapshot.Bytes(_own);
            var m = _motion;
            m.Pos = new Double3(m.Pos.X + _rng.NextDouble(), m.Pos.Y, m.Pos.Z + _rng.NextDouble() * 3);
            m.Vel = new Vector3(_rng.NextSingle(), 0, 30 + _rng.NextSingle());
            m.Rpm = 2000 + _rng.NextSingle() * 3000;
            m.RecKind = (byte)(_rng.Next(10) == 0 ? 2 : m.RecKind == 2 && _rng.Next(5) == 0 ? 0 : m.RecKind);
            m.RecSerial = 3;
            for (int i = 0; i < 4; i++) { m.Wheels[i].Travel = _rng.NextSingle() * 0.1f; m.Wheels[i].Y1 += _rng.NextDouble() * 1e-3; }
            m.HoldUp = _rng.Next(3) == 0 ? 0 : m.HoldUp + 0.05f;
            Motion = m.Key();
            if (_fire.Weapons.Count == 0) for (int i = 0; i < 3; i++) _fire.Weapons.Add(new OwnFire.Entry { Weapon = (byte)i });
            var fw = _fire.Weapons[_rng.Next(3)];
            if (_rng.Next(4) == 0) { fw.Shots++; fw.WaitA = _rng.NextSingle(); fw.WasFiring = true; } else fw.WasFiring = false;
            Fire = Snapshot.Bytes(_fire);
            if (_rng.Next(30) == 0) _slow.Params[_rng.Next(12)] = _rng.NextSingle();
            var sw = new NetWriter();
            _slow.Write(sw);
            Slow = sw.ToArray();
        }

        public (List<(int, EntityRec)> Cars, List<(int, EntityRec)> Rounds) Recs(uint tick) =>
            ([.. Cars.Values.Select(c => (c.Serial, c.Rec(tick)))], [.. Rounds.Values.Select(r => (r.Serial, r.Rec(tick)))]);
    }

    private static SortedList<uint, SnapFrame> Frames(SnapshotDecoder d) =>
        (SortedList<uint, SnapFrame>)typeof(SnapshotDecoder).GetField("_frames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;

    /// <summary>
    /// The server's frame for each client and the client's after decoding are the same, tick after tick, whatever is lost:
    /// snapshots dropped, acks late, acks stopping (a full snapshot follows once the last ack is too old), cars and rounds
    /// skipped by interest. A car sent this tick is the server's quantized car; the own car's bytes are exact.
    /// </summary>
    [Theory]
    [InlineData(20, 0.0, false)]
    [InlineData(20, 0.2, false)]
    [InlineData(20, 0.2, true)]
    [InlineData(60, 0.3, true)]
    public void TheClientsFrameIsTheServersThroughLoss(int hz, double loss, bool interest)
    {
        var rate = SimRate.FromLabel(hz);
        var world = new World(hz + (int)(loss * 100));
        var enc = new SnapshotEncoder(rate);
        var dec = new SnapshotDecoder(rate);
        var rng = new Random(11);
        var acks = new Queue<(uint Due, uint Tick)>();
        int decoded = 0, full = 0, deltaBytes = 0, deltas = 0;
        var w = new NetWriter(8192);
        for (uint t = 1; t <= 1500; t++)
        {
            world.Step(rate.TickSeconds);
            var (cars, rounds) = world.Recs(t);
            // the acks come 4 ticks later; between ticks 600 and 700 none come at all
            while (acks.Count > 0 && acks.Peek().Due <= t) enc.Ack(acks.Dequeue().Tick);
            var b = enc.Baseline(t);
            if (b is null) full++;
            var f = new SnapFrame();
            w.Clear();
            SnapshotCodec.WriteHead(w, t, t - 2, 0, b, world.Own, world.Motion, world.Slow, f, world.Fire);
            uint tick = t;
            Func<int, CarSend>? send = interest
                ? s => (s % 3) switch { 0 => CarSend.Full, 1 => Interest.Send(Interest.Tier.Half, s, tick, rate), _ => Interest.Send(Interest.Tier.Low, s, tick, rate) }
                : null;
            SnapshotCodec.WriteCars(w, t, rate.TickSeconds, b, cars, send, f);
            SnapshotCodec.WriteRounds(w, t, rate.TickSeconds, b, rounds, interest ? s => s % 2 == 0 ? CarSend.Full : CarSend.Skip : null, f);
            SnapshotCodec.WriteTail(w, [], [new FlameState(3, 1, (byte)(2 + t % 17))]);
            enc.Sent(f);
            if (b is not null) { deltaBytes += w.Length; deltas++; }
            if (rng.NextDouble() < loss) continue;
            var r = new NetReader(w.Span);
            Assert.Equal(MsgType.Snapshot, (MsgType)r.U8());
            var snap = dec.Read(ref r);
            Assert.NotNull(snap);
            Assert.True(r.End);
            decoded++;
            // the client's copy of the frame is the server's
            var mine = Frames(dec)[t];
            Assert.Equal(f.Own, mine.Own);
            Assert.Equal(f.Motion, mine.Motion);
            Assert.Equal(f.Slow, mine.Slow);
            Assert.Equal(f.Fire, mine.Fire);
            Assert.Equal(world.Fire, Snapshot.Bytes(snap!.Fire!));
            Assert.Equal((byte)(2 + t % 17), snap.Flames.Single().Count);
            Assert.Equal(f.Cars.Keys.Order(), mine.Cars.Keys.Order());
            foreach (var (k, rec) in f.Cars) { Assert.Equal(rec.Hot, mine.Cars[k].Hot); Assert.Equal(rec.Cold, mine.Cars[k].Cold); Assert.Equal(rec.SampleTick, mine.Cars[k].SampleTick); }
            Assert.Equal(f.Rounds.Keys.Order(), mine.Rounds.Keys.Order());
            foreach (var (k, rec) in f.Rounds) Assert.Equal(rec.Hot, mine.Rounds[k].Hot);
            // a car sent this tick is the server's own quantized car; a coarse one (the low tier's) within half its coarse
            // units, without its driving values and wheels
            foreach (var c in snap!.Cars.Where(c => c.SampleTick == t))
            {
                var truth = cars.First(x => x.Item1 == c.Serial).Item2;
                bool coarse = interest && c.Serial % 3 == 2 && Interest.Send(Interest.Tier.Low, c.Serial, t, rate) == CarSend.Coarse;
                if (!coarse) Assert.Equal(truth.Hot.AsSpan(0, 17).ToArray(), f.Cars[c.Serial].Hot.AsSpan(0, 17).ToArray());
                else
                    for (int ch = 0; ch < 13; ch++)
                        Assert.True(Math.Abs(truth.Hot[ch] - f.Cars[c.Serial].Hot[ch]) <= SnapshotCodec.CoarseUnits[ch] / 2, $"channel {ch}: {truth.Hot[ch]} sent as {f.Cars[c.Serial].Hot[ch]}");
                Assert.Equal(truth.Cold, f.Cars[c.Serial].Cold);
                var back = CarState.FromRec(c.Serial, truth);
                Assert.True(Math.Abs(back.Pos.X - c.Pos.X) + Math.Abs(back.Pos.Z - c.Pos.Z) <= (coarse ? 1 / 32.0 : 0), $"{back.Pos} sent as {c.Pos}");
                Assert.Equal(back.Generation, c.Generation);
            }
            Assert.Equal(world.Cars.Count, snap.Cars.Count);
            Assert.Equal(world.Motion, snap.Motion!.Key());
            Assert.NotNull(snap.Motion.Slow);
            Assert.Equal(world.Own, Snapshot.Bytes(snap.Own!));
            if (t is < 600 or > 700) acks.Enqueue((t + 4, dec.Last));
        }
        output.WriteLine($"{hz} Hz, {loss:P0} lost, interest {interest}: {decoded} decoded, {full} full, {deltaBytes / Math.Max(1, deltas)} B a delta");
        Assert.Equal(0, dec.Misses);
        Assert.True(full >= 2 && full < 100, $"{full} full snapshots");     // the first ones, and once the acks had stopped for 1.6 s
        Assert.True(decoded > 1500 * (1 - loss) * 0.9);
    }

    /// <summary>A snapshot that names a baseline the client no longer has cannot be read; one against a baseline it has can.</summary>
    [Fact]
    public void ASnapshotWithoutItsBaselineIsLostNotMisread()
    {
        var rate = SimRate.R20;
        var world = new World(1);
        var w = new NetWriter();
        SnapFrame Write(uint t, SnapFrame? b)
        {
            var (cars, rounds) = world.Recs(t);
            var f = new SnapFrame();
            w.Clear();
            SnapshotCodec.WriteHead(w, t, 0, 0, b, world.Own, world.Motion, world.Slow, f);
            SnapshotCodec.WriteCars(w, t, rate.TickSeconds, b, cars, null, f);
            SnapshotCodec.WriteRounds(w, t, rate.TickSeconds, b, rounds, null, f);
            SnapshotCodec.WriteTail(w, []);
            return f;
        }
        world.Step(0.05f);
        var f1 = Write(1, null);
        var bytes1 = w.ToArray();
        world.Step(0.05f);
        Write(2, f1);
        var dec = new SnapshotDecoder(rate);
        var r = new NetReader(w.Span);
        r.U8();
        Assert.Null(dec.Read(ref r));
        Assert.Equal(1, dec.Misses);
        Assert.Equal(0u, dec.Newest);
        r = new NetReader(bytes1);
        r.U8();
        Assert.NotNull(dec.Read(ref r));
        r = new NetReader(w.Span);
        r.U8();
        Assert.NotNull(dec.Read(ref r));
        Assert.Equal(2u, dec.Newest);
        // the full form reads alone; a delta does not
        var delta = w.ToArray();
        Assert.Throws<WireException>(() =>
        {
            var rd = new NetReader(delta);
            rd.U8();
            Snapshot.Read(ref rd);
        });
        // an ack of a tick never sent gives no baseline, nor one too old
        var enc = new SnapshotEncoder(rate);
        enc.Sent(f1);
        enc.Ack(77);
        Assert.Null(enc.Baseline(78));
        enc.Ack(1);
        Assert.Same(f1, enc.Baseline(5));
        Assert.Null(enc.Baseline(2 + (uint)rate.TicksFor(1.6f)));
    }

    [Fact]
    public void FarCarsAreSentLessOften()
    {
        var at = Vector3.Zero;
        var fwd = Vector3.UnitZ;
        Assert.Equal(Interest.Tier.Full, Interest.Of(at, fwd, new(150, 0, 0), false));
        Assert.Equal(Interest.Tier.Full, Interest.Of(at, fwd, new(0, 0, 550), false));      // ahead
        Assert.Equal(Interest.Tier.Half, Interest.Of(at, fwd, new(0, 0, -550), false));     // behind
        Assert.Equal(Interest.Tier.Half, Interest.Of(at, fwd, new(500, 0, 100), false));    // beside
        Assert.Equal(Interest.Tier.Low, Interest.Of(at, fwd, new(0, 0, 900), false));
        Assert.Equal(Interest.Tier.Full, Interest.Of(at, fwd, new(0, 0, -2000), true));     // the target, or targeting us
        foreach (var (rate, half, low) in new[] { (SimRate.R20, 2, 4), (SimRate.R28, 3, 6), (SimRate.R60, 6, 12) })
        {
            Assert.Equal((1, half, low), (Interest.Interval(Interest.Tier.Full, rate), Interest.Interval(Interest.Tier.Half, rate), Interest.Interval(Interest.Tier.Low, rate)));
            for (int serial = 1; serial < 20; serial++)
            {
                int[] sent = [0, 0, 0];
                for (uint t = 1; t <= 120; t++)
                    for (int k = 0; k < 3; k++)
                        if (Interest.Send((Interest.Tier)k, serial, t, rate) != CarSend.Skip) sent[k]++;
                Assert.Equal([120, 120 / half, 120 / low], sent);
            }
            Assert.Equal(CarSend.Coarse, Enumerable.Range(1, low).Select(t => Interest.Send(Interest.Tier.Low, 5, (uint)t, rate)).First(x => x != CarSend.Skip));
        }
        // the far cars' ticks are staggered: of twelve far cars a quarter come in each tick at 20 Hz
        for (uint t = 1; t < 20; t++) Assert.Equal(3, Enumerable.Range(1, 12).Count(s => Interest.Send(Interest.Tier.Low, s, t, SimRate.R20) != CarSend.Skip));
        Assert.True(Interest.SendRound(at, new(0, 0, 790), false, 3, 1, SimRate.R20));
        Assert.True(Interest.SendRound(at, new(0, 0, 5000), true, 3, 1, SimRate.R20));
        Assert.Equal(1, Enumerable.Range(1, 4).Count(t => Interest.SendRound(at, new(0, 0, 5000), false, 3, (uint)t, SimRate.R20)));
    }

    /// <summary>
    /// A remote car on its own clock: driving at 30 m/s and sent every tick, then at the half rate, then at the low
    /// rate, with snapshots lost now and then, it is drawn without a jump: each frame's step is about the car's travel
    /// in that frame, never half again more, and the drawn car stays within half a metre of where it was at the drawn time.
    /// </summary>
    [Theory]
    [InlineData(20, 0.0)]
    [InlineData(20, 0.1)]
    [InlineData(60, 0.1)]
    public void ARemoteCarIsDrawnSmoothlyWhateverItsRate(int hz, double loss)
    {
        var rate = SimRate.FromLabel(hz);
        var track = new CarTrack();
        var rng = new Random(2);
        const float speed = 30;
        double ts = rate.TickSeconds, dt = 1 / 60.0, delay = 0.1;
        // round a 120 m circle: the velocity turns, so going on along it (a lost state) is off, and the correction is drawn away
        const double radius = 120;
        Double3 Where(double tick) { double a = speed * tick * ts / radius; return new Double3(radius * Math.Sin(a), 0, radius * Math.Cos(a)); }
        CarState At(uint tick)
        {
            double a = speed * tick * ts / radius;
            return new() { SampleTick = tick, Pos = Where(tick), Vel = new Vector3((float)(speed * Math.Cos(a)), 0, (float)(-speed * Math.Sin(a))), Rot = Rows.AboutY((float)a) };
        }
        double now = 0, worstStep = 0, worstOff = 0;
        uint sent = 0;
        Double3? last = null;
        int frames = 0;
        while (now < 12)
        {
            now += dt;
            int interval = now < 4 ? 1 : now < 8 ? Interest.Interval(Interest.Tier.Half, rate) : Interest.Interval(Interest.Tier.Low, rate);
            // what has arrived: the server's ticks 50 ms ago
            uint arrived = (uint)Math.Max(0, (now - 0.05) / ts);
            while (sent < arrived)
            {
                sent++;
                if (sent % interval == 0 && rng.NextDouble() >= loss) track.Add(At(sent));
            }
            double renderTick = (now - 0.05 - delay) / ts;
            var pose = track.Step(renderTick, dt, rate.TickSeconds);
            if (pose is null || now < 1) { last = pose?.Pos; continue; }
            frames++;
            var p = pose.Value.Pos;
            if (last is { } l) worstStep = Math.Max(worstStep, Math.Sqrt((p.X - l.X) * (p.X - l.X) + (p.Z - l.Z) * (p.Z - l.Z)));
            // where the car was at the time drawn (its own clock runs Extra ticks behind the view)
            var truth = Where(renderTick - track.Extra);
            worstOff = Math.Max(worstOff, Math.Sqrt((p.X - truth.X) * (p.X - truth.X) + (p.Z - truth.Z) * (p.Z - truth.Z)));
            last = pose.Value.Pos;
        }
        output.WriteLine($"{hz} Hz, {loss:P0} lost: {frames} frames, worst step {worstStep:0.000} m (a frame's travel {speed * dt:0.000}), worst off {worstOff:0.000} m, extrapolated {track.Extrapolated} frames, extra {track.Extra:0.0} ticks");
        Assert.True(worstStep < speed * dt * 1.5, $"a step of {worstStep:0.000} m");
        Assert.True(worstOff < 0.5, $"off by {worstOff:0.000} m");
        int low = Interest.Interval(Interest.Tier.Low, rate);
        Assert.InRange(track.Extra, low - 1.5, low - 0.5);
    }

    /// <summary>
    /// A remote car sent at uneven intervals, as the snapshot budget sends a car it puts off (a tick, two, three, now
    /// and then five), with 5% of snapshots lost: drawn without a jump and close to where it was at the time drawn.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    public void ARemoteCarSentAtUnevenIntervalsIsDrawnSmoothly(int hz)
    {
        var rate = SimRate.FromLabel(hz);
        var track = new CarTrack();
        var rng = new Random(9);
        const float speed = 30;
        double ts = rate.TickSeconds, dt = 1 / 60.0, delay = 0.1;
        const double radius = 120;
        Double3 Where(double tick) { double a = speed * tick * ts / radius; return new Double3(radius * Math.Sin(a), 0, radius * Math.Cos(a)); }
        CarState At(uint tick)
        {
            double a = speed * tick * ts / radius;
            return new() { SampleTick = tick, Pos = Where(tick), Vel = new Vector3((float)(speed * Math.Cos(a)), 0, (float)(-speed * Math.Sin(a))), Rot = Rows.AboutY((float)a) };
        }
        int[] gaps = hz == 20 ? [1, 1, 1, 2, 2, 3, 5] : [1, 2, 3, 3, 4, 6, 9];
        double now = 0, worstStep = 0, worstOff = 0;
        uint sent = 0, next = 1;
        Double3? last = null;
        int frames = 0;
        while (now < 12)
        {
            now += dt;
            uint arrived = (uint)Math.Max(0, (now - 0.05) / ts);
            while (sent < arrived)
            {
                sent++;
                if (sent < next) continue;
                next = sent + (uint)gaps[rng.Next(gaps.Length)];
                if (rng.NextDouble() >= 0.05) track.Add(At(sent));
            }
            double renderTick = (now - 0.05 - delay) / ts;
            var pose = track.Step(renderTick, dt, rate.TickSeconds);
            if (pose is null || now < 2) { last = pose?.Pos; continue; }
            frames++;
            var p = pose.Value.Pos;
            if (last is { } l) worstStep = Math.Max(worstStep, Math.Sqrt((p.X - l.X) * (p.X - l.X) + (p.Z - l.Z) * (p.Z - l.Z)));
            var truth = Where(renderTick - track.Extra);
            worstOff = Math.Max(worstOff, Math.Sqrt((p.X - truth.X) * (p.X - truth.X) + (p.Z - truth.Z) * (p.Z - truth.Z)));
            last = pose.Value.Pos;
        }
        output.WriteLine($"{hz} Hz uneven: {frames} frames, worst step {worstStep:0.000} m (a frame's travel {speed * dt:0.000}), worst off {worstOff:0.000} m, extrapolated {track.Extrapolated} frames, extra {track.Extra:0.0} ticks");
        Assert.True(worstStep < speed * dt * 1.5, $"a step of {worstStep:0.000} m");
        Assert.True(worstOff < 0.5, $"off by {worstOff:0.000} m");
        // the longest gaps are gone on along the velocity rather than waited for (the delay covers three in four)
        Assert.True(track.Extrapolated < frames / 5, $"{track.Extrapolated} of {frames} frames went on along the velocity");
    }

    private static int Words(byte[] c, byte[] b)
    {
        if (c.Length != b.Length) return c.Length + 2;
        var w = new NetWriter();
        int n = (c.Length + 3) / 4;
        w.Bytes(new byte[(n + 7) / 8]);
        for (int i = 0; i < n; i++)
        {
            uint x = 0;
            for (int k = 0; k < 4 && 4 * i + k < c.Length; k++) x |= (uint)(c[4 * i + k] ^ b[4 * i + k]) << (8 * k);
            if (x != 0) w.VarU(x);
        }
        return w.Length;
    }

    /// <summary>The snapshot writing's cost on the server (with I76_NET_BENCH set: 32 players on m01; =nointerest without interest management).</summary>
    [Fact]
    public void SnapshotWritingCost()
    {
        if (HostTests.InstallDir is not { } install || Environment.GetEnvironmentVariable("I76_NET_BENCH") is not { } mode) return;
        int n = int.TryParse(Environment.GetEnvironmentVariable("I76_NET_BENCH_SEATS"), out var ns) ? ns : 32;
        int budget = int.TryParse(Environment.GetEnvironmentVariable("I76_NET_BENCH_BUDGET"), out var nb) ? nb : int.MaxValue;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", MaxPlayers = n, InterestManagement = mode != "nointerest" });
        var seats = Enumerable.Range(0, n).Select(i => host.Join($"p{i}", "vppirna1.vcf", 0)).ToList();
        var simWatch = new Stopwatch();
        var worldWatch = new Stopwatch();
        host.Session.FrameStarting += () => { if (simWatch.IsRunning) worldWatch.Start(); };
        host.Session.FrameDone += () => worldWatch.Stop();
        var w = new NetWriter(8192);
        var sw = new Stopwatch();
        long bytes = 0, roundBytes = 0, rounds = 0, mBytes = 0, mWords = 0, mN = 0, oBytes = 0;
        int mLen = 0;
        var hist = new Queue<(byte[] M, byte[] O)>();
        for (uint t = 1; t <= 400; t++)
        {
            foreach (var s in seats) s.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp | CarButtons.Fire | ((t + (uint)s.Slot * 7) % 80 < 40 ? CarButtons.SteerLeft : 0) }]));
            if (t >= 200) simWatch.Start();
            host.Step();
            simWatch.Stop();
            host.TakeEvents();
            if (t < 200) continue;
            sw.Start();
            var tk = host.CaptureTick();
            foreach (var s in seats) { w.Clear(); var (h, c, _) = host.WriteSnapshot(w, s, tk, budget); bytes += w.Length; roundBytes += w.Length - h - c; host.AckSnapshot(s, t - 3); }
            rounds += tk.Rounds.Count;
            var me = seats[3];
            var cur = (OwnMotion.Capture(me.Player.Car, me.Driver).Key(), Snapshot.Bytes(host.CaptureOwn(me)));
            hist.Enqueue(cur);
            if (hist.Count > 4)
            {
                var old = hist.Dequeue();
                var mw = new NetWriter();
                XorDelta.Write(mw, cur.Item1, old.M);
                mBytes += mw.Length;
                mw.Clear();
                XorDelta.Write(mw, cur.Item2, old.O);
                oBytes += mw.Length;
                mWords += Words(cur.Item1, old.M);
                mLen = cur.Item1.Length;
                mN++;
            }
            sw.Stop();
        }
        output.WriteLine($"motion deltas: bytes {mBytes / Math.Max(1, mN)}, words {mWords / Math.Max(1, mN)} of {mLen}; own {oBytes / Math.Max(1, mN)}");
        output.WriteLine($"{sw.Elapsed.TotalMilliseconds / 200:0.000} ms a tick for {n} snapshots (simulation {simWatch.Elapsed.TotalMilliseconds / 200:0.000} ms, the world's frame {worldWatch.Elapsed.TotalMilliseconds / 200:0.000}), {bytes / 200 / n} B a snapshot, {rounds / 200} rounds in flight, {roundBytes / 200 / n} B of rounds");
        for (int k = 0; k < 3; k++)
        {
            foreach (var s in seats) s.Inputs.Add(new InputMessage(401 + (uint)k, [new CarInput { Buttons = CarButtons.ThrottleUp | CarButtons.Fire }]));
            host.Step();
            var tk2 = host.CaptureTick();
            foreach (var (serial, rec) in tk2.Rounds.Take(3)) output.WriteLine($"round {serial}: [{string.Join(", ", rec.Hot)}] cold [{string.Join(",", rec.Cold)}]");
        }
    }
}
