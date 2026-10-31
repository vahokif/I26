using System.Diagnostics;
using System.Numerics;
using I76.Net;
using I76.Server;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The client's prediction of its own car (docs/multiplayer.md, "Prediction"): the same car stepped with the same
/// inputs on the client and the server comes out the same, and over a link with latency, jitter and loss the predicted
/// car agrees with the server's for the acked tick, and comes back to it after the server moves it.
/// </summary>
public class PredictionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A drive: accelerate, steer, brake, reverse and back, a shift, the handbrake, the engine off and on, the lights, the horn, the stick.</summary>
    internal static CarInput Script(long t)
    {
        long p = t % 400;
        var i = new CarInput();
        if (p < 60) i.Buttons = CarButtons.ThrottleUp;
        else if (p < 100) i.Buttons = CarButtons.ThrottleUp | CarButtons.SteerLeft;
        else if (p < 130) i.Buttons = CarButtons.ThrottleDown;
        else if (p < 170) i.Buttons = CarButtons.ThrottleUp | (p > 150 ? CarButtons.SteerRight : 0);
        else if (p < 220) i.Buttons = CarButtons.ThrottleUp | CarButtons.SteerRight | (p is > 200 and < 210 ? CarButtons.EBrake : 0);
        else if (p < 260) i.Buttons = p < 240 ? CarButtons.ThrottleDown : CarButtons.ThrottleUp;
        else if (p < 330) { i.Buttons = CarButtons.Analog; i.Throttle = (short)(20000 + p * 30); i.Steer = (short)(Math.Sin(p * 0.1) * 30000); }
        else i.Buttons = CarButtons.ThrottleUp | (p % 40 < 20 ? CarButtons.SteerLeft : CarButtons.SteerRight);
        if (p is >= 80 and < 120) i.Buttons |= CarButtons.Horn;   // the horn bit is in the motion: predicted too
        i.Commands = p switch
        {
            50 => CarCommands.Lights,
            130 or 170 => CarCommands.Reverse,
            175 => CarCommands.ShiftDown,
            185 => CarCommands.ShiftUp,
            222 or 245 => CarCommands.StartEngine,
            300 => CarCommands.WeaponCycle,
            _ => 0,
        };
        return i;
    }

    /// <summary>
    /// The server's car and the client's prediction of it, stepped with the same inputs, are the same car bit for
    /// bit at every acked tick: with the snapshots coming at once, and coming 6 ticks late (each one restored and the
    /// six inputs since stepped again), over a minute of the scripted drive.
    /// </summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(6, 20)]
    [InlineData(6, 60)]
    public void TheClientsStepIsTheServers(int lateTicks, int hz)
    {
        if (HostTests.InstallDir is not { } install) return;
        var rate = SimRate.FromLabel(hz);
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7, Rate = rate });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(seat));
        var client = new MeleeClient();
        var predictor = new OwnCarPredictor(mirror, client);
        var late = new Queue<Snapshot>();
        int n = rate.TicksFor(60);
        var firstBad = (Tick: 0u, Err: 0.0);
        predictor.OnCompare += (t, err, exact) => { if (!exact && firstBad.Tick == 0) firstBad = (t, err); };
        for (uint t = 1; t <= n; t++)
        {
            var input = Script(t);
            seat.Inputs.Add(new InputMessage(t, [input]));
            host.Step();
            host.TakeEvents();
            predictor.Step(t, input);
            late.Enqueue(Through(host, seat));
            while (late.Count > lateTicks)
            {
                client.Inject(late.Dequeue());
                predictor.Reconcile();
            }
        }
        var real = seat.Player.Car;
        var mw = new NetWriter();
        OwnMotion.Capture(real, seat.Driver).Write(mw);
        int fast = mw.Length;
        mw.Clear();
        OwnMotion.Capture(real, seat.Driver, slow: true).Write(mw);
        output.WriteLine($"the motion: {fast} B, {mw.Length} B with the slow part");
        output.WriteLine($"{hz} Hz, {lateTicks} late: {predictor.Compared} compared, {predictor.Inexact} not exact (first at {firstBad.Tick}, {firstBad.Err:0.000} m), " +
            $"max {predictor.MaxError:0.000} m; server car at {real.Pos.X:0.0},{real.Pos.Z:0.0} speed {real.Speed:0.0}, flags {real.Flags}");
        Assert.True(predictor.Compared > n - lateTicks - 5, $"{predictor.Compared} of {n}");
        Assert.Equal(0, predictor.Inexact);
    }

    /// <summary>
    /// A small correction is drawn away, not jumped: the server nudges the car half a metre and turns it 5°, and right
    /// after the snapshot is taken the car is drawn where it was before (the offset), then over a few tenths of a second
    /// where the prediction now has it; the car itself has snapped. A big one (8 m) is drawn at once.
    /// </summary>
    [Fact]
    public void ASmallCorrectionIsDrawnAwayABigOneSnaps()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(seat));
        var client = new MeleeClient();
        var predictor = new OwnCarPredictor(mirror, client);
        uint t = 0;
        void Tick()
        {
            t++;
            var input = new CarInput { Buttons = CarButtons.ThrottleUp };
            seat.Inputs.Add(new InputMessage(t, [input]));
            host.Step();
            host.TakeEvents();
            predictor.Step(t, input);
        }
        void Take() { client.Inject(Through(host, seat)); predictor.Reconcile(); }
        for (int i = 0; i < 60; i++) { Tick(); Take(); }
        Assert.Equal(0, predictor.Inexact);
        foreach (var (move, turn, snaps) in new[] { (0.5, 5f, false), (8.5, 0f, true) })
        {
            Tick();
            var (rotBefore, posBefore) = predictor.Pose(1);
            var car = seat.Player.Car;
            car.Pos = new I76.Sim.Math.Double3(car.Pos.X + move, car.Pos.Y, car.Pos.Z);
            car.Rot = I76.Sim.Math.Rows.Concat(car.Rot, I76.Sim.Math.Rows.AboutY(turn * MathF.PI / 180));
            int snapped = predictor.Snaps;
            Take();
            var (rotNow, posNow) = predictor.Pose(1);
            double jump = Math.Sqrt(Math.Pow(posNow.X - posBefore.X, 2) + Math.Pow(posNow.Z - posBefore.Z, 2));
            output.WriteLine($"moved {move} m, turned {turn}°: correction {predictor.LastCorrection:0.000} m, drawn jump {jump:0.000} m, offset {predictor.Offset:0.000} m, snapped {predictor.Snaps > snapped}");
            Assert.Equal(snaps, predictor.Snaps > snapped);
            if (!snaps)
            {
                Assert.True(predictor.LastCorrection > 0.4, $"{predictor.LastCorrection}");
                Assert.True(jump < 1e-3, $"drawn jump {jump}");
                Assert.True(Vector3.Distance(rotNow.Forward, rotBefore.Forward) < 1e-3f, "the drawn heading jumped");
                // the car itself is where the server put it
                Assert.True(Math.Abs(mirror.Own!.Vehicle.Pos.X - car.Pos.X) < 0.05);
                for (int f = 0; f < 30; f++) predictor.Smooth(1 / 60.0);
                var (rotLater, posLater) = predictor.Pose(1);
                Assert.True(predictor.Offset < 0.01f, $"offset {predictor.Offset}");
                Assert.True(Math.Abs(posLater.X - predictor.Pos.X) < 0.01 && Vector3.Distance(rotLater.Forward, predictor.Rot.Forward) < 1e-3f);
            }
            else Assert.True(jump > 8, $"drawn jump {jump}");
        }
    }

    /// <summary>
    /// Nitro is predicted: the press is felt on the tick it is sent (the predicted car speeds up with the server's on the
    /// same input tick, 6 ticks before the snapshot says so), every acked tick is exact through the burn and the charges,
    /// and its sound (bnitro.wav) plays at once from the predicted car; the server's copy of that click is not played again.
    /// </summary>
    [Fact]
    public void NitroIsFeltAndHeardAtOnce()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(seat));
        var client = new MeleeClient();
        var predictor = new OwnCarPredictor(mirror, client);
        // a nitro in the first special slot of both copies of the car
        seat.Player.Car.Health!.Slots[17] = new I76.Sim.Damage.SpecialPart(2);
        mirror.Own!.Vehicle.Health!.Slots[17] = new I76.Sim.Damage.SpecialPart(2);
        var late = new Queue<(Snapshot S, EventBatch? E)>();
        var serverSpeed = new Dictionary<uint, float>();
        var predictedSpeed = new Dictionary<uint, float>();
        const uint press = 80;
        uint heardAt = 0;
        int heard = 0;
        for (uint t = 1; t <= 200; t++)
        {
            var input = new CarInput { Buttons = CarButtons.ThrottleUp, Commands = t is press or press + 100 ? CarCommands.Special1 : 0 };
            seat.Inputs.Add(new InputMessage(t, [input]));
            host.Step();
            var events = host.TakeEvents();
            serverSpeed[t] = seat.Player.Car.Speed;
            predictor.Step(t, input);
            predictedSpeed[t] = mirror.Own!.Vehicle.Speed;
            late.Enqueue((Through(host, seat), events.Count > 0 ? new EventBatch(host.Tick, events) : null));
            while (late.Count > 6)
            {
                var (s, e) = late.Dequeue();
                if (e is not null) client.Events.Enqueue(e);
                client.Inject(s);
            }
            mirror.Advance(host.Session.TickSeconds, client);
            predictor.Reconcile();
            foreach (var c in mirror.Setup.Combat.Events.OfType<I76.Sim.Weapons.WeaponClick>())
                if (c.Sound == "bnitro.wav") { heard++; if (heardAt == 0) heardAt = t; }
            mirror.Setup.Combat.Events.Clear();
        }
        float gain(Dictionary<uint, float> sp, uint t) => sp[t] - sp[t - 1];
        output.WriteLine($"nitro pressed on tick {press}: server speed gain then {gain(serverSpeed, press):0.000} m/s a tick (before {gain(serverSpeed, press - 1):0.000}), " +
            $"predicted {gain(predictedSpeed, press):0.000}; heard on tick {heardAt}, {heard} times; {predictor.Compared} compared, {predictor.Inexact} not exact");
        Assert.True(gain(serverSpeed, press + 1) > 2 * gain(serverSpeed, press - 2), "the server's nitro did not push");
        for (uint t = press; t < press + 20; t++) Assert.Equal(serverSpeed[t], predictedSpeed[t]);
        Assert.Equal(0, predictor.Inexact);
        Assert.Equal(press, heardAt);
        Assert.Equal(2, heard);
    }

    /// <summary>The snapshot the server would send this seat now, through its bytes.</summary>
    internal static Snapshot Through(MeleeHost host, Seat seat)
    {
        var w = new NetWriter();
        var (cars, rounds) = host.Capture();
        Snapshot.WriteHeader(w, host.Tick, seat.Inputs.LastApplied, host.CaptureOwn(seat), seat.Inputs.Slack, host.CaptureMotion(seat), host.CaptureFire(seat));
        Snapshot.WriteBody(w, cars, rounds, host.CaptureFlags(), host.CaptureFlames());
        var r = new NetReader(w.Span);
        r.U8();
        return Snapshot.Read(ref r);
    }

    /// <summary>
    /// Over a real link through the lag proxy: the client predicts its car with the scripted drive, alone on m01. The
    /// prediction for each acked tick is compared with the server's car: almost always exact, corrections rare and
    /// small. Then the server moves the car 10 m (a teleport) and takes a chunk of its armour: the prediction snaps to
    /// it and agrees again within half a second.
    /// </summary>
    [Theory]
    [InlineData(50, 0, 0f)]
    [InlineData(75, 30, 5f)]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void ThePredictedCarAgreesWithTheServersOverALaggyLink(int latencyMs, int jitterMs, float lossPercent)
    {
        if (HostTests.InstallDir is not { } install) return;
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("I76_NET_TEST_SECONDS"), out var sec) ? Math.Min(sec, 30) : 20;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Ai = 0, Seed = 11 });
        server.Start();
        using var proxy = new LagProxy(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port), latencyMs, lossPercent, jitterMs: jitterMs, seed: 5);
        using var client = new MeleeClient();
        client.Connect("127.0.0.1", proxy.Port, "me", "vppirna1.vcf");
        ClientWorld? mirror = null;
        OwnCarPredictor? predictor = null;
        var errors = new List<(double At, uint Tick, double Err, bool Exact)>();
        var corrections = new List<(double At, double Size)>();
        var clock = Stopwatch.StartNew();
        double last = 0, nextTick = 0, kickAt = -1, warm = 2;
        long ticks = 0;
        uint lastSnap = 0;
        double Sim() => client.NewestTick * SimRate.Default.TickSeconds;
        while (Sim() < seconds + 2 && clock.Elapsed.TotalSeconds < (seconds + 2) * 6)
        {
            double now = clock.Elapsed.TotalSeconds;
            client.Poll();
            if (mirror is null && client.Accept is { } acc)
            {
                mirror = new ClientWorld(HostTests.Mirror("m01"), acc);
                predictor = new OwnCarPredictor(mirror, client);
                predictor.OnCompare += (t, err, exact) => errors.Add((Sim(), t, err, exact));
            }
            if (client.Snapshots.Count > 0 && client.Snapshots.Values[^1] is { } ns && ns.Tick != lastSnap)
            {
                lastSnap = ns.Tick;
                if (Environment.GetEnvironmentVariable("I76_PREDICT_TRACE") is not null) output.WriteLine($"t {Sim():0.00} slack {ns.Slack} clock {client.ClockScale:0.000} gap {client.InputTick - ns.AckedInput}");
            }
            if (mirror is not null)
            {
                mirror.Advance(now - last, client);
                int before = predictor!.Reconciles;
                predictor.Reconcile();
                if (predictor.Reconciles > before && predictor.LastCorrection > 0) corrections.Add((Sim(), predictor.LastCorrection));
                predictor.Smooth(now - last);
            }
            last = now;
            while (now >= nextTick)
            {
                nextTick += SimRate.Default.TickSeconds / client.ClockScale;
                if (client.State != ClientState.Joined) continue;
                var input = Script(++ticks);
                client.SendInput(input);
                predictor?.Step(client.InputTick, input);
            }
            // two thirds in: the server moves the car and takes some of its armour
            if (kickAt < 0 && Sim() > seconds * 2 / 3 && predictor is { Ready: true })
            {
                kickAt = Sim();
                server.Invoke(h =>
                {
                    var car = h.Seats[0].Player.Car;
                    car.Pos = new I76.Sim.Math.Double3(car.Pos.X + 10, car.Pos.Y, car.Pos.Z);
                    var p = new I76.Sim.Weapons.DamagePacket { Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
                    p.Ch[1] = 40;
                    h.Setup.Damage.Apply(car, p, new Vector3(0, 0.5f, 2));
                });
            }
            Thread.Sleep(2);
        }
        Assert.NotNull(predictor);
        var steady = errors.Where(e => e.At > warm && (kickAt < 0 || e.At < kickAt)).ToList();
        var after = errors.Where(e => kickAt > 0 && e.At > kickAt + 0.5 + (2 * latencyMs + jitterMs) / 1000.0).ToList();
        int agree = steady.Count(e => e.Err <= predictor!.Tolerance), exact = steady.Count(e => e.Exact);
        var bad = steady.Where(e => e.Err > predictor!.Tolerance).Select(e => e.Err).OrderBy(e => e).ToList();
        output.WriteLine($"{latencyMs} ms each way ±{jitterMs}, {lossPercent}% lost: rtt {client.Rtt} ms, ack gap {client.InputTick - client.AckedInput}, clock {client.ClockScale:0.000}");
        output.WriteLine($"steady: {steady.Count} compared, {agree} within {predictor!.Tolerance * 100:0} cm ({100.0 * agree / Math.Max(1, steady.Count):0.0}%), {exact} exact; " +
            $"off: {bad.Count} (median {(bad.Count > 0 ? bad[bad.Count / 2] : 0):0.000} m, max {(bad.Count > 0 ? bad[^1] : 0):0.000} m)");
        output.WriteLine($"drawn corrections: {corrections.Count} ({string.Join(", ", corrections.Select(c => $"{c.Size:0.000} m at {c.At:0.0} s"))}), snaps {predictor.Snaps}; " +
            $"after the kick: {after.Count} compared, {after.Count(e => e.Exact)} exact; server input queue starved {server.Invoke(h => h.Seats[0].Inputs.Starved)}, dropped {server.Invoke(h => h.Seats[0].Inputs.Dropped)}");
        output.WriteLine($"proxy: {proxy.Forwarded} forwarded, {proxy.Dropped} dropped; snapshots {client.SnapshotsReceived}, avg {client.SnapshotBytes / Math.Max(1, client.SnapshotsReceived)} B");
        output.WriteLine($"motion confirmed by hash in {predictor.Confirmed} of {predictor.Reconciles} snapshots taken, {predictor.Unresolved} confirmations unresolved");
        // compares come once per reconcile, and a client loop starved by a loaded machine takes several snapshots at once
        // (only the newest is compared): judged against the server ticks in the window, not the wall's seconds
        double window = ((kickAt > 0 ? kickAt : Sim()) - warm) / SimRate.Default.TickSeconds;
        Assert.True(steady.Count > window * 0.5, $"{steady.Count} compared of {window:0} ticks");
        Assert.True(agree >= steady.Count * (lossPercent > 0 ? 0.95 : 0.98), $"{agree} of {steady.Count} agree");
        Assert.True(kickAt > 0);
        Assert.True(predictor.Snaps >= 1, "the teleport was not snapped to");
        Assert.True(after.Count > 5 && after.Count(e => e.Exact) >= after.Count * 0.9, $"{after.Count(e => e.Exact)} of {after.Count} exact after the kick");
        // the cheaper motion: most snapshots confirm the prediction by its hash instead of carrying the motion
        Assert.True(predictor.Confirmed >= predictor.Reconciles * 0.8, $"{predictor.Confirmed} of {predictor.Reconciles} confirmed");
        Assert.True(predictor.Unresolved <= 2, $"{predictor.Unresolved} confirmations unresolved");
    }
}
