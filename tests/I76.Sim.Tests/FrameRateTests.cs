using System.Globalization;
using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>The tick as a setting (docs/frame-rate.md §2 and §6).</summary>
public class SimRateTests
{
    [Fact]
    public void TheOfferedTicksAreTheSpecsAndTwentyIsTheDefault()
    {
        Assert.Equal([20, 25, 28, 30, 40, 60], SimRate.Offered.Select(r => r.Label));
        Assert.Equal(SimRate.R20, SimRate.Default);
        Assert.Equal(102f / 2048f, SimRate.R20.TickSeconds);
        Assert.Equal(0.036f, SimRate.R28.TickSeconds);
        Assert.Equal(SimRate.R20, SimRate.FromLabel(17));       // an unknown label is the default
        Assert.Equal(SimRate.R60, SimRate.FromLabel(60));
    }

    /// <summary>
    /// The original changes discontinuously at a frame time of exactly 0.05 s (two vehicle substeps; a 20/s weapon
    /// firing every other frame), and a 1/20, 1/15 or 1/60 tick sits on its own weapon rates. So none of the
    /// offered ticks may be 0.05 s or the exact reciprocal of its label; all but 28's are on the clock's k/2048 grid.
    /// </summary>
    [Fact]
    public void NoOfferedTickIsFiftyMillisecondsOrAReciprocal()
    {
        foreach (var r in SimRate.Offered)
        {
            Assert.NotEqual(0.05f, r.TickSeconds);
            Assert.NotEqual(1f / r.Label, r.TickSeconds);
            Assert.NotEqual(1f / 15f, r.TickSeconds);
            Assert.NotEqual(1f / 60f, r.TickSeconds);
            if (r.Label != 28) Assert.Equal(0f, r.TickSeconds * 2048f - MathF.Round(r.TickSeconds * 2048f));
            // one vehicle substep at every offered rate
            Assert.Equal(1, VehicleStep.Substeps(r.TickSeconds).Count);
        }
        Assert.Equal(2, VehicleStep.Substeps(0.05f).Count);       // and this is what 0.05 would do
    }

    [Fact]
    public void TheTickLimitsKeepTheirSecondsAtEveryRate()
    {
        // the wire's limits in ticks: 0.2 s of input history, 0.288 s queued, 0.108 s caught up (the 28 Hz values 6, 8, 3)
        Assert.Equal((6, 8, 3), (SimRate.R28.TicksFor(0.2f), SimRate.R28.TicksFor(0.288f), SimRate.R28.TicksFor(0.108f)));
        foreach (var r in SimRate.Offered)
        {
            Assert.InRange(r.TicksFor(0.2f) * r.TickSeconds, 0.2f, 0.2f + r.TickSeconds);
            Assert.InRange(r.TicksFor(0.288f) * r.TickSeconds, 0.288f, 0.288f + r.TickSeconds);
        }
    }

    [Fact]
    public void AutoReverseHoldsTheSameTimeAtEveryRate()
    {
        Assert.Equal(8, new AutoReverse(SimRate.R28).HoldTicks);
        foreach (var r in SimRate.Offered)
            Assert.InRange(new AutoReverse(r).HoldTicks * r.TickSeconds, 0.25f, 0.33f);
    }

    /// <summary>
    /// The original at 20 fps read 102.4 clock quanta a frame as 102, 102, 103, 102, 103 (fps-drive-50 288:225, T09 at
    /// 50 ms 417:279), and the 103s (50.293 ms) took two vehicle substeps. The 20 Hz world frame follows that sequence;
    /// a constant 102 never took the second step, and the car flew off T09's crests (<see cref="T09CrestTests"/>).
    /// The other rates' frames stay on one side of 0.05 s and keep their tick.
    /// </summary>
    [Fact]
    public void TwentyHzFramesFollowTheReferenceClocksSequence()
    {
        var q = Enumerable.Range(0, 10).Select(k => SimRate.R20.FrameDelta(k) * 2048f).ToArray();
        Assert.Equal([102f, 102f, 103f, 102f, 103f, 102f, 102f, 103f, 102f, 103f], q);
        Assert.Equal([1, 1, 2, 1, 2], Enumerable.Range(0, 5).Select(k => VehicleStep.Substeps(SimRate.R20.FrameDelta(k)).Count));
        Assert.Equal(0.25, Enumerable.Range(0, 5).Sum(k => (double)SimRate.R20.FrameDelta(k)), 9);
        foreach (var r in SimRate.Offered.Where(r => r.Label != 20))
            foreach (int k in Enumerable.Range(0, 60)) Assert.Equal(r.TickSeconds, r.FrameDelta(k));
        Assert.Equal(0.05f, new SimRate(20, 0.05f).FrameDelta(2));   // an off-grid tick is its own frame
    }
}

/// <summary>Weapons and the flame stream at each tick (docs/frame-rate.md §6.3), from the Piranha's guns.</summary>
public class SimRateCombatTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private sealed class Sink : IDamageSink
    {
        public readonly List<(object Victim, DamagePacket P, Vector3 Local)> Hits = [];
        public void Apply(object victim, DamagePacket p, Vector3 hitLocal) => Hits.Add((victim, p, hitLocal));
    }

    private static (SimWorld World, Vehicle Car, Combat Combat, Sink Sink, AssetVfs Vfs) Rig(string vcf)
    {
        var vfs = new AssetVfs(new GameInstall(InstallDir!));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = VehicleFactory.Create(vfs, vcf);
        world.Vehicles.Add(car);
        world.Player = car;
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink };
        return (world, car, combat, sink, vfs);
    }

    private static string MgPattern(float dt, int ticks = 12)
    {
        var (_, car, combat, _, _) = Rig("vppirna2.vcf");
        var a = car.Armory!;
        int mg = a.Weapons.FindIndex(w => w.Def.Kind == 1 && w.Def.Rate == 20);
        var keys = new bool[5]; keys[mg] = true;
        var pattern = "";
        for (int f = 0; f < ticks; f++)
        {
            a.PlayerTriggers(false, keys);
            combat.Frame(dt);
            pattern += combat.Events.OfType<ShotFired>().Count(e => e.Weapon == a.Weapons[mg]);
            combat.Events.Clear();
        }
        return pattern;
    }

    [Theory]
    [InlineData(20, "111111111111")]
    [InlineData(25, "111101111011")]
    [InlineData(28, "110110110110")]
    [InlineData(30, "110110110110")]
    [InlineData(40, "101010101010")]
    [InlineData(60, "100100100100")]
    public void TheTwentyAShotGunFiresAsTheOriginalDidAtThatRate(int hz, string expected)
    {
        if (InstallDir is null) return;
        Assert.Equal(expected, MgPattern(SimRate.FromLabel(hz).TickSeconds));
    }

    /// <summary>The edge the 102/2048 tick avoids: at exactly 0.05 s a 20/s gun fires every other frame.</summary>
    [Fact]
    public void AnExactFiftyMillisecondTickWouldHalveATwentyAShotGun()
    {
        if (InstallDir is null) return;
        Assert.Equal("101010101010", MgPattern(0.05f));
    }

    private static string Stream(float dt, bool hold, int ticks = 18)
    {
        var (_, car, combat, _, _) = Rig("vppirna2.vcf");
        combat.FlameHoldEnabled = hold;
        var a = car.Armory!;
        int hose = a.Weapons.FindIndex(w => w.Def.Kind == 11);
        var keys = new bool[5]; keys[hose] = true;
        var counts = new List<int>();
        for (int f = 0; f < ticks; f++)
        {
            a.PlayerTriggers(false, keys);
            combat.Frame(dt);
            counts.Add(combat.Flames.FirstOrDefault()?.Count ?? 0);
        }
        return string.Join(",", counts);
    }

    /// <summary>With the flame-hold deviation off, the original's stream: full at 20 Hz, about half at 25, a stub from 28.</summary>
    [Theory]
    [InlineData(20, 19, 19, 19)]
    [InlineData(25, 9, 13, 19)]
    [InlineData(28, 2, 3, 3)]
    [InlineData(30, 2, 3, 3)]
    [InlineData(40, 2, 3, 3)]
    [InlineData(60, 2, 3, 3)]
    public void WithoutFlameHoldTheNapalmStreamIsTheOriginalsAtThatRate(int hz, int lo, int hi, int _)
    {
        if (InstallDir is null) return;
        var counts = Stream(SimRate.FromLabel(hz).TickSeconds, hold: false).Split(',').Select(int.Parse).ToList();
        if (hz == 20) Assert.Equal(19, counts.Max());
        else if (hz == 25) { Assert.InRange(counts.Skip(8).Average(), lo - 3, hi + 1); Assert.True(counts.Max() < 19, string.Join(",", counts)); }
        else Assert.True(counts.Max() <= hi, string.Join(",", counts));
    }

    [Fact]
    public void FlameHoldChangesNothingAtTwentyAndFillsTheStreamFromTwentyEight()
    {
        if (InstallDir is null) return;
        Assert.Equal(Stream(SimRate.R20.TickSeconds, hold: false), Stream(SimRate.R20.TickSeconds, hold: true));
        Assert.True(Stream(SimRate.R20.TickSeconds, hold: true).Split(',').Select(int.Parse).Max() == 19);
        foreach (var r in new[] { SimRate.R28, SimRate.R30, SimRate.R40, SimRate.R60 })
            Assert.Equal(19, Stream(r.TickSeconds, hold: true, 40).Split(',').Select(int.Parse).Max());
    }

    /// <summary>The original's packet: max(1, ftol(45 · dt)) per touching point per tick: 2 at the 20 Hz tick, 1 at 28.</summary>
    [Theory]
    [InlineData(20, 2)]
    [InlineData(28, 1)]
    [InlineData(60, 1)]
    public void TheNapalmBurnPerPointPerTickFollowsTheTick(int hz, int expected)
    {
        if (InstallDir is null) return;
        float dt = SimRate.FromLabel(hz).TickSeconds;
        var (world, car, combat, sink, vfs) = Rig("vppirna2.vcf");
        var a = car.Armory!;
        int hose = a.Weapons.FindIndex(w => w.Def.Kind == 11);
        var w = a.Weapons[hose];
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var muzzle = car.Pos.ToVector3() + car.Rot.ToWorld(w.MuzzlePos);
        victim.Pos = new Double3(muzzle.X, 0, muzzle.Z - 10);
        world.Vehicles.Add(victim);
        var keys = new bool[5]; keys[hose] = true;
        for (int f = 0; f < 45; f++) { a.PlayerTriggers(false, keys); combat.Frame(dt); }
        sink.Hits.Clear();
        a.PlayerTriggers(false, keys); combat.Frame(dt);
        var burns = sink.Hits.Where(h => h.Victim == victim).ToList();
        Assert.NotEmpty(burns);
        Assert.All(burns, h => Assert.Equal(expected, h.P.Ch[2]));
    }
}

/// <summary>
/// The port at each tick against the original measured at that frame rate (docs/frame-rate.md §5 and §6.1-2).
/// The traces are runs of the original game, fps-{drive,turn}-MS (m01, Piranha, a fixed frame step of MS), trimmed to the
/// replay's columns.
/// </summary>
public class FrameRateTraceTests(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed record Row(int Frame, double T, float Delta, Double3 Pos, Vector3 Up, Vector3 Fwd, Vector3 Vel, float Speed,
        float Steer, float Throttle, float Gear, int EBrake, Vector3 Spin, uint Flags, float Rpm);

    private static List<Row> Trace(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", name + ".csv"));
        var col = lines[0].Split(',').Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var rows = new List<Row>();
        double t = 0;
        foreach (var l in lines.Skip(1))
        {
            var f = l.Split(',');
            float F(string n) => float.Parse(f[col[n]], CultureInfo.InvariantCulture);
            double D(string n) => double.Parse(f[col[n]], CultureInfo.InvariantCulture);
            t += F("delta");
            rows.Add(new Row(int.Parse(f[col["frame"]]), t, F("delta"), new Double3(D("x"), D("y"), D("z")),
                new(F("upx"), F("upy"), F("upz")), new(F("fx"), F("fy"), F("fz")), new(F("vx"), F("vy"), F("vz")), F("speed"),
                F("steer"), F("throttle"), F("gear"), int.Parse(f[col["ebrake"]]), new(F("wx"), F("wy"), F("wz")),
                uint.Parse(f[col["flags"]]), F("rpm")));
        }
        return rows;
    }

    private static void Load(Vehicle v, Row r)
    {
        v.Pos = r.Pos;
        v.Rot = new Rows(Vector3.Normalize(Vector3.Cross(r.Up, r.Fwd)), r.Up, r.Fwd);
        v.Vel = r.Vel; v.Speed = r.Speed; v.AngVel = r.Spin; v.Flags = (VehicleFlags)r.Flags; v.Engine.Rpm = r.Rpm;
    }

    private static MissionSetup M01()
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("m01")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false);
        setup.World.Vehicles.RemoveAll(x => x != setup.World.Player);
        return setup;
    }

    /// <summary>Reference mode: each row stepped with the trace's own delta and controls, from one second in, no resync.</summary>
    [Theory]
    [InlineData("fps-drive-17")]
    [InlineData("fps-drive-36")]
    [InlineData("fps-drive-49")]
    [InlineData("fps-drive-50")]
    public void TheDriveReplaysWithinTheSpecTolerancesAtEachFrameRate(string name)
    {
        if (InstallDir is null) return;
        var rows = Trace(name);
        var setup = M01();
        var world = setup.World;
        var v = world.Player!;
        rows = rows.Where(r => r.T >= 1.0).ToList();
        double t0 = rows[0].T;
        Load(v, rows[0]);
        for (int k = 1; k < rows.Count; k++)
        {
            var prev = rows[k - 1]; var r = rows[k];
            v.Steer = prev.Steer; v.Throttle = prev.Throttle; v.Gear = prev.Gear; v.Handbrake = prev.EBrake;
            world.Step.Impacts.Clear(); world.Step.ObjectHits.Clear(); world.Step.CarHits.Clear();
            world.Step.StaticContacts(v, r.Delta);
            if ((r.Flags & 0x20000) != 0) world.Step.CheapFrame(v, r.Delta, true);
            else world.Step.Frame(v, r.Delta, true);
            foreach (var (at, maxPos) in new[] { (6.0, 2.5), (12.0, 6.0) })
                if (r.T >= at && prev.T < at)
                {
                    double err = System.Math.Sqrt(System.Math.Pow(v.Pos.X - r.Pos.X, 2) + System.Math.Pow(v.Pos.Y - r.Pos.Y, 2) + System.Math.Pow(v.Pos.Z - r.Pos.Z, 2));
                    output.WriteLine($"{name} t={at}: position error {err:0.00} m, speed error {v.Speed - r.Speed:+0.00;-0.00} m/s");
                    Assert.True(err <= maxPos, $"{name} at {at} s: {err:0.00} m");
                    Assert.True(System.Math.Abs(v.Speed - r.Speed) <= 2, $"{name} at {at} s: speed {v.Speed - r.Speed:+0.00;-0.00}");
                }
        }
    }

    [Theory]
    [InlineData("fps-turn-36")]
    [InlineData("fps-turn-50")]
    public void TheTurnReplaysWithinTheSpecToleranceAtSixSeconds(string name)
    {
        if (InstallDir is null) return;
        var rows = Trace(name).Where(r => r.T >= 1.0).ToList();
        var world = M01().World;
        var v = world.Player!;
        Load(v, rows[0]);
        for (int k = 1; k < rows.Count; k++)
        {
            var prev = rows[k - 1]; var r = rows[k];
            v.Steer = prev.Steer; v.Throttle = prev.Throttle; v.Gear = prev.Gear; v.Handbrake = prev.EBrake;
            world.Step.Impacts.Clear(); world.Step.ObjectHits.Clear(); world.Step.CarHits.Clear();
            world.Step.StaticContacts(v, r.Delta);
            if ((r.Flags & 0x20000) != 0) world.Step.CheapFrame(v, r.Delta, true);
            else world.Step.Frame(v, r.Delta, true);
            if (r.T >= 6.0 && prev.T < 6.0)
            {
                double err = System.Math.Sqrt(System.Math.Pow(v.Pos.X - r.Pos.X, 2) + System.Math.Pow(v.Pos.Y - r.Pos.Y, 2) + System.Math.Pow(v.Pos.Z - r.Pos.Z, 2));
                output.WriteLine($"{name} t=6: position error {err:0.00} m");
                Assert.True(err <= 2.5, $"{name}: {err:0.00} m");
            }
        }
    }

    /// <summary>The fps-drive-50 controls through a SimSession at the given rate; the player's speed against the trace's at 10 and 12 s.</summary>
    private (double Sim, double Orig)[] PlayDrive50(SimRate rate)
    {
        var rows = Trace("fps-drive-50").Where(r => r.T >= 1.0).ToList();
        var setup = M01();
        var v = setup.World.Player!;
        Load(v, rows[0]);
        var s = new SimSession(setup, rate: rate);
        s.AddDriver(v);
        double t0 = rows[0].T, t = 0;
        var result = new List<(double, double)>();
        var marks = new Queue<double>([10.0, 12.0]);
        while (marks.Count > 0)
        {
            // the trace's throttle at this moment, as the stick (the original game's key ramp is not what is under test)
            var ctl = rows.Last(r => r.T - t0 <= t + 1e-9);
            s.Tick([new CarInput { Buttons = CarButtons.Analog, Throttle = (short)(ctl.Throttle * 32767), Steer = (short)(ctl.Steer * 32767) }]);
            t += rate.TickSeconds;
            if (t0 + t >= marks.Peek())
            {
                double mark = marks.Dequeue();
                var o = rows.First(r => r.T >= mark);
                result.Add((v.Speed, o.Speed));
            }
        }
        output.WriteLine($"rate {rate.Label} ({rate.TickSeconds}): " + string.Join("  ", result.Select(x => $"sim {x.Item1:0.0} orig {x.Item2:0.0}")));
        return [.. result];
    }

    /// <summary>Play mode at the default 20 Hz tick (102/2048 s) against the original at 20 fps (spec §6.2).</summary>
    [Fact]
    public void ADriveAtTheDefaultTickMatchesTheOriginalAtTwentyFps()
    {
        if (InstallDir is null) return;
        var r = PlayDrive50(SimRate.R20);
        Assert.InRange(System.Math.Abs(r[0].Sim - r[0].Orig), 0, 1.5);
        Assert.InRange(System.Math.Abs(r[1].Sim - r[1].Orig) / r[1].Orig, 0, 0.04);
    }

    /// <summary>The same drive at exactly 0.05 s plays like 40 fps: well behind (spec §2). A guard against simplifying the tick to 1/20.</summary>
    [Fact]
    public void ADriveAtAnExactFiftyMillisecondTickDoesNotMatch()
    {
        if (InstallDir is null) return;
        var r = PlayDrive50(new SimRate(20, 0.05f));
        Assert.True(r[0].Orig - r[0].Sim >= 3, $"{r[0].Orig - r[0].Sim:0.0} m/s behind at 10 s");
    }

    [Fact]
    public void ADriveAtTwentyEightHzPlaysLikeTheOriginalAtTwentyEight()
    {
        if (InstallDir is null) return;
        var rows = Trace("fps-drive-36");
        // fps-drive-36's controls through the 28 Hz session: the same method as above, on its own trace
        var rate = SimRate.R28;
        var rs = rows.Where(r => r.T >= 1.0).ToList();
        var setup = M01();
        var v = setup.World.Player!;
        Load(v, rs[0]);
        var s = new SimSession(setup, rate: rate);
        s.AddDriver(v);
        double t0 = rs[0].T, t = 0;
        while (t0 + t < 10.0)
        {
            var ctl = rs.Last(r => r.T - t0 <= t + 1e-9);
            s.Tick([new CarInput { Buttons = CarButtons.Analog, Throttle = (short)(ctl.Throttle * 32767), Steer = (short)(ctl.Steer * 32767) }]);
            t += rate.TickSeconds;
        }
        var o = rs.First(r => r.T >= 10.0);
        output.WriteLine($"28 Hz at 10 s: sim {v.Speed:0.0} orig {o.Speed:0.0}");
        Assert.InRange(System.Math.Abs(v.Speed - o.Speed), 0, 1.5);
    }
}

/// <summary>
/// T09's straight south of the start (x 1181, from z 48720 heading -z) runs over a crest. Taken at full throttle from
/// 28 to 46 u/s, the 20 Hz session that stepped every frame once (a constant 102/2048 s) left the ground at 9 of the 10
/// speeds, 13.2 s in the air between them: "the car flies off the road really easily" (a player at the default rate).
/// With the original's 20 fps sequence (2 frames in 5 stepped twice) it leaves it at 2 speeds for 4.2 s, of a size with
/// 28 Hz (3.8 s, at 44 and 46).
/// </summary>
public class T09CrestTests(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Seconds airborne crossing the crest at each start speed, the throttle held and the heading kept.</summary>
    private float[] AirTimes(SimRate rate)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var air = new List<float>();
        for (int speed = 28; speed <= 46; speed += 2)
        {
            var w = new MissionWorld(vfs, asm, install.FindMission("t09")!);
            var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1));
            var v = setup.World.Player!;
            setup.World.Vehicles.RemoveAll(x => x != v);
            var f0 = new Vector3(0, 0, -1);
            v.Pos = new Double3(1181, setup.Ground.BareHeight(1181, 48720), 48720);
            v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f0), Vector3.UnitY, f0);
            v.Vel = f0 * speed; v.Speed = speed;
            var s = new SimSession(setup, rate: rate);
            s.AddDriver(v);
            int airborne = 0;
            for (int i = 0; i < rate.TicksFor(4f); i++)
            {
                // heading off -z, + toward +x, which is the car's left (+ steer): steer back
                float err = MathF.Atan2(v.Rot.Forward.X, -v.Rot.Forward.Z);
                s.Tick([new CarInput { Buttons = CarButtons.Analog, Throttle = 32767, Steer = (short)(System.Math.Clamp(-err * 3f, -1f, 1f) * 32767) }]);
                if ((v.Flags & VehicleFlags.Airborne) != 0) airborne++;
            }
            air.Add(airborne * rate.TickSeconds);
        }
        output.WriteLine($"{rate.Label} Hz: " + string.Join(" ", air.Select((a, i) => $"{28 + 2 * i}:{a:0.00}")) + $" total {air.Sum():0.00} s");
        return [.. air];
    }

    [Fact]
    public void AtTwentyHzTheCarKeepsToTheGroundOverTheCrestAboutAsAtTwentyEight()
    {
        if (InstallDir is null) return;
        var at20 = AirTimes(SimRate.R20);
        var at28 = AirTimes(SimRate.R28);
        Assert.True(at20.Sum() <= 6f, $"{at20.Sum():0.00} s in the air at 20 Hz");
        Assert.True(at20.Count(a => a > 0) <= 4, $"left the ground at {at20.Count(a => a > 0)} of 10 speeds at 20 Hz");
        Assert.True(at20.Sum() <= 2 * at28.Sum() + 1f, $"20 Hz {at20.Sum():0.00} s against 28 Hz {at28.Sum():0.00} s");
    }
}

/// <summary>
/// T04's race loop (the script's path 13), the player's car alone, driven round by a fixed rule (aim at the next
/// point, the next one within 20; steer while 0.03 rad off; throttle under the speed, brake 3 over), as the original game was driven.
/// The original at 28 fps: 0.2% of the time airborne at
/// 30 u/s (one settle off the start), and at 20 u/s 0.6%, every hop in the one dip near (970, 50475). The port, at
/// 20 Hz and at 28: 0.0% at 30, 0.4–0.5% at 20, in the same dip.
/// </summary>
public class T04RaceTests(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static readonly Vector2[] Route = [.. new[]
    {
        (1210, 50335), (1210, 50455), (1200, 50490), (1140, 50535), (1080, 50545), (1025, 50540), (1000, 50530), (975, 50510),
        (965, 50445), (975, 50415), (1005, 50345), (1010, 50325), (1015, 50290), (1005, 50240), (965, 50210), (900, 50170),
        (885, 50150), (870, 50110), (870, 50060), (880, 50035), (935, 50005), (1040, 50015), (1095, 50030), (1160, 50055),
        (1185, 50070), (1205, 50100), (1210, 50130), (1210, 50175), (1210, 50235),
    }.Select(p => new Vector2(p.Item1, p.Item2))];

    /// <summary>The share of 120 s spent airborne, and where each flight began.</summary>
    private (float Share, List<Vector2> Hops) Drive(SimRate rate, float speed)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("t04")!);
        var setup = MissionSetup.Build(vfs, w, asm, null, script: false, rand: new CrtRand(1));
        var v = setup.World.Player!;
        setup.World.Vehicles.RemoveAll(x => x != v);
        var s = new SimSession(setup, rate: rate);
        s.AddDriver(v);
        var f0 = Vector2.Normalize(Route[1] - Route[0]);
        var fwd = new Vector3(f0.X, 0, f0.Y);
        v.Pos = new Double3(Route[0].X, setup.World.Ground.Query(Route[0].X, 1e4, Route[0].Y).Height + 1, Route[0].Y);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        int k = 1, airborne = 0, ticks = rate.TicksFor(120f);
        bool was = false;
        var hops = new List<Vector2>();
        for (int t = 0; t < ticks; t++)
        {
            var p = new Vector2((float)v.Pos.X, (float)v.Pos.Z);
            while (Vector2.Distance(p, Route[k]) < 20) k = (k + 1) % Route.Length;
            var to = Route[k] - p;
            float err = MathF.Atan2(to.X, to.Y) - MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z);
            while (err > MathF.PI) err -= 2 * MathF.PI;
            while (err < -MathF.PI) err += 2 * MathF.PI;
            var b = err > 0.03f ? CarButtons.SteerRight : err < -0.03f ? CarButtons.SteerLeft : CarButtons.None;
            if (v.Speed < speed) b |= CarButtons.ThrottleUp;
            else if (v.Speed > speed + 3) b |= CarButtons.ThrottleDown;
            s.Tick([new CarInput { Buttons = b }]);
            bool air = (v.Flags & VehicleFlags.Airborne) != 0;
            // the first second is the drop onto the start (the original game's teleport puts the car 1 above the ground too)
            if (air && t * rate.TickSeconds > 1f) { airborne++; if (!was) hops.Add(new Vector2((float)v.Pos.X, (float)v.Pos.Z)); }
            was = air;
        }
        float share = (float)airborne / ticks;
        output.WriteLine($"{rate.Label} Hz, {speed} u/s: {share:P1} airborne, {hops.Count} flights " + string.Join(" ", hops.Select(h => $"({h.X:0},{h.Y:0})")));
        return (share, hops);
    }

    [Fact]
    public void TheCarKeepsToTheRoadAsTheOriginalDoes()
    {
        if (InstallDir is null) return;
        var dip = new Vector2(970, 50475);
        foreach (var rate in new[] { SimRate.R20, SimRate.R28 })
        {
            var (at30, _) = Drive(rate, 30);
            Assert.True(at30 <= 0.01f, $"{rate.Label} Hz: {at30:P1} airborne at 30 u/s (the original 0.2%)");
            var (at20, hops) = Drive(rate, 20);
            Assert.True(at20 <= 0.015f, $"{rate.Label} Hz: {at20:P1} airborne at 20 u/s (the original 0.6%)");
            Assert.All(hops, h => Assert.True(Vector2.Distance(h, dip) < 20, $"a flight at ({h.X:0},{h.Y:0}), away from the dip the original hops in"));
        }
    }
}
