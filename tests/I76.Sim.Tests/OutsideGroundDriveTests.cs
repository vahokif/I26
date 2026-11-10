using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Driving off the map onto the outside ground (OutsideGround). The original reads 0 past the map's tiles: its edge cell falls to 0 and a
/// plain at 0 runs on. Here a high edge falls so (a car drives off and drops, and a drop of more than 35 m kills it), and a low edge
/// carries on at its own height (a car at speed crosses it as it crosses any other line of samples and runs on); the AI drives out after
/// a car; the upright reset puts a car on the outside ground.
/// </summary>
public class OutsideGroundDriveTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("i26-edge-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string? Dir(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (MissionSetup Setup, SimSession Session, Vehicle Car) Drive(string env, string mission, bool melee, int hz)
    {
        var install = new GameInstall(Dir(env)!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission(mission)!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), melee: melee ? new List<string>() : null, seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(hz));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        return (setup, s, v);
    }

    /// <summary>
    /// A new flat map 1.5 m high: its edge is low, so the ground carries on at 1.5 m (no step) and eases down to the plain's 0 well out,
    /// gently (no 5 m step steeper than a tenth's rounding, 0.02); full throttle out across its north edge the car stays on the ground with no landing, impact or jolt, and runs on.
    /// </summary>
    [Fact]
    public void ALowEdgeCarriesOnWithoutAStep()
    {
        if (Dir("I76_INSTALL") is null) return;
        var (setup, s, v) = Plateau(1.5);
        var g = ((I76.Sim.Collision.WorldGround)setup.Ground).Terrain;
        for (double x = 2600; x <= 3800; x += 7.3)
        {
            Assert.Equal(1.5f, g.Height(x, 47355), 3);
            Assert.Equal(1.5f, g.Height(x, 47357.5), 3);
            Assert.Equal(1.5f, g.Height(x, 47390), 3);
            Assert.Equal(0f, g.Height(x, 47800));
        }
        double steepest = 0;
        for (double z = 47360; z < 47800; z += 5) steepest = System.Math.Max(steepest, System.Math.Abs(g.Height(3200, z + 5) - g.Height(3200, z)) / 5);
        Assert.True(steepest <= 0.021, $"the carried height eases down at up to {steepest:0.000} in 1");
        Put(setup, v, 3200, 47210, 0);
        float dt = SimRate.FromLabel(20).TickSeconds;
        float prevVy = 0, maxDvy = 0;
        for (int f = 1; f <= (int)(40 / dt) && v.Pos.Z < 47700; f++)
        {
            var i = new CarInput();
            if (f is >= 2 and <= 6) i.Commands = CarCommands.StartEngine;
            if (f > 8) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            if (System.Math.Abs(v.Pos.Z - 47360) < 60)
            {
                Assert.False(s.World.Step.Landings.Contains(v) || s.World.Step.Impacts.Any(h => h.Self == v && h.Other is null), $"a landing or impact at z {v.Pos.Z:0.0}");
                Assert.True((v.Flags & VehicleFlags.Airborne) == 0, $"airborne at z {v.Pos.Z:0.0}");
                if (f > 40) maxDvy = MathF.Max(maxDvy, MathF.Abs(v.Vel.Y - prevVy));
            }
            prevVy = v.Vel.Y;
        }
        log.WriteLine($"off a 1.5 m edge: now at z {v.Pos.Z:0} at {v.Speed * 3.6f:0} km/h, vertical speed changes up to {maxDvy:0.000} m/s a tick near the edge");
        Assert.True(v.Pos.Z >= 47700, $"only got to z {v.Pos.Z:0}");
        Assert.True(maxDvy < 0.5f, $"the vertical speed jumps {maxDvy:0.000} m/s a tick at the edge");
    }

    /// <summary>A new flat map of 2 x 2 tiles <paramref name="metres"/> high, as the map editor makes it (its tiles at grid row 72, column 4: x 2560..3840, z 46080..47360).</summary>
    private (MissionSetup Setup, SimSession Session, Vehicle Car) Plateau(double metres)
    {
        var install = new GameInstall(Dir("I76_INSTALL")!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var (_, files) = I76.Editor.NewMap.CreateFlat(_dir, $"plat{(int)metres}", "Plateau", 2, metres, nitro: false);
        var w = new MissionWorld(vfs, asm, files.First(f => f.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase)));
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), melee: new List<string>(), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(20));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        return (setup, s, v);
    }

    /// <summary>
    /// The edge cell of a 150 m plateau is the original's: from the map's last sample (150 m) the ground falls over the 5 m to the next
    /// sample, 0, bilinear (75 m halfway) with the original's normal, and the plain runs on at 0 (upward normal) for the 600 m round the map;
    /// height, normal and surface agree with the original's reading to the bit along the whole edge.
    /// </summary>
    [Fact]
    public void AHighEdgeFallsToTheOriginalsPlain()
    {
        if (Dir("I76_INSTALL") is null) return;
        var (setup, _, _) = Plateau(150);
        var g = ((I76.Sim.Collision.WorldGround)setup.Ground).Terrain;
        Assert.NotNull(g.Outside);
        var orig = new TerrainGround(g.Tiles, new Surface[8]);
        Assert.Equal(150f, g.Height(3200, 47355), 3);
        Assert.Equal(75f, g.Height(3200, 47357.5), 3);
        Assert.Equal(0f, g.Height(3200, 47360));
        Assert.Equal(0f, g.Height(3200, 47700));
        Assert.Equal(0f, g.Height(3200, 47950));
        Assert.Equal(Vector3.UnitY, g.Normal(3200.3, 47700.6));
        for (double x = 2400; x <= 4000; x += 1.7)
            foreach (double z in new[] { 47352.0, 47355.0, 47356.2, 47358.9, 47360.0, 47361.3, 47390.0, 47800.0 })
            {
                Assert.Equal(orig.Height(x, z), g.Height(x, z));
                Assert.Equal(orig.Normal(x, z), g.Normal(x, z));
                Assert.Equal(orig.SurfaceId(x, z), g.SurfaceId(x, z));
            }
        Assert.Equal(0, g.SurfaceId(3200, 47500));
    }

    /// <summary>
    /// Full throttle off a plateau's north edge: off 150 m the car falls to the plain at 0 and the landing kills it (the original's 35 m fall);
    /// off 30 m it lands on the plain and drives on.
    /// </summary>
    [Theory]
    [InlineData(150.0, true)]
    [InlineData(30.0, false)]
    public void DrivingOffAHighEdgeDropsToThePlain(double metres, bool dies)
    {
        if (Dir("I76_INSTALL") is null) return;
        var (setup, s, v) = Plateau(metres);
        Put(setup, v, 3200, 47210, 0);   // 150 m in, heading +z, out across the north edge at z 47360
        float dt = SimRate.FromLabel(20).TickSeconds;
        bool fell = false, landed = false, dead = false;
        double lowest = double.MaxValue;
        for (int f = 1; f <= (int)(40 / dt); f++)
        {
            var i = new CarInput();
            if (f is >= 2 and <= 6) i.Commands = CarCommands.StartEngine;
            if (f > 8) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            if ((v.Flags & VehicleFlags.Airborne) != 0 && v.Pos.Z > 47355) fell = true;
            if (fell && s.World.Step.Landings.Contains(v)) landed = true;
            dead |= ((uint)v.Flags & 0x8020) != 0;
            lowest = System.Math.Min(lowest, v.Pos.Y);
            if (v.Pos.Z > 47700 || dead) break;
        }
        log.WriteLine($"off {metres} m: fell {fell}, landed {landed}, dead {dead}, lowest {lowest:0.00}, now at z {v.Pos.Z:0} y {v.Pos.Y:0.00} over ground {s.World.Ground.BareHeight(v.Pos.X, v.Pos.Z):0.00}");
        Assert.True(fell, "the car never left the edge");
        Assert.True(lowest < 5, $"it never came down to the plain: lowest {lowest:0.00}");
        Assert.Equal(dies, dead);
        if (!dies)
        {
            Assert.True(landed && v.Pos.Z > 47700, $"it landed {landed} and got to z {v.Pos.Z:0}");
            Assert.True(MathF.Abs((float)v.Pos.Y - s.World.Ground.BareHeight(v.Pos.X, v.Pos.Z)) < 1 && s.World.Ground.BareHeight(v.Pos.X, v.Pos.Z) == 0, "not on the plain");
        }
    }

    private static void Put(MissionSetup s, Vehicle v, double x, double z, float yaw)
    {
        var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        v.Pos = new Double3(x, s.Ground.BareHeight(x, z), z);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        v.Vel = default; v.AngVel = default; v.Speed = 0;
    }

    /// <summary>
    /// Full throttle from 200 m inside the map, straight out across a low edge (T01's south edge, a few decimetres over the plain; N01's, a
    /// Nitro arena): the ground carries on, and the car crosses at speed with all four wheels down, no landing or impact anywhere near the
    /// edge, its vertical speed changing no more from one tick to the next there than on the map before it, and keeps its speed; 300 m out
    /// it is still on the ground and going.
    /// </summary>
    [Theory]
    [InlineData("I76_INSTALL", "t01", false, 2025.0, 34760.0, 34560.0, 20)]
    [InlineData("I76_INSTALL", "t01", false, 2025.0, 34760.0, 34560.0, 60)]
    [InlineData("I76_NITRO_INSTALL", "n01", true, 3715.0, 48840.0, 48640.0, 28)]
    public void ACarDrivesOffTheMapAtSpeedWithoutABump(string env, string mission, bool melee, double x, double z, double edge, int hz)
    {
        if (Dir(env) is null) return;
        var (setup, s, v) = Drive(env, mission, melee, hz);
        Put(setup, v, x, z, MathF.PI);   // heading -z, out across the south edge
        float dt = SimRate.FromLabel(hz).TickSeconds;
        float speedAtEdge = -1, maxDvyIn = 0, maxDvyEdge = 0, prevVy = 0;
        int ticks = (int)(40 / dt);
        bool crossed = false;
        for (int f = 1; f <= ticks; f++)
        {
            var i = new CarInput();
            if (f is >= 2 and <= 6) i.Commands = CarCommands.StartEngine;
            if (f > 8) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            float out_ = (float)(edge - v.Pos.Z);   // metres past the edge
            float dvy = MathF.Abs(v.Vel.Y - prevVy);
            prevVy = v.Vel.Y;
            bool landed = s.World.Step.Landings.Contains(v), hit = s.World.Step.Impacts.Any(h => h.Self == v && h.Other is null);
            if (out_ > -60 && out_ < 60)
            {
                Assert.False(landed || hit, $"{mission}: a {(landed ? "landing" : "terrain impact")} {out_:0.0} m from the edge");
                Assert.True((v.Flags & VehicleFlags.Airborne) == 0, $"{mission}: airborne {out_:0.0} m from the edge");
                maxDvyEdge = MathF.Max(maxDvyEdge, dvy);
            }
            else if (out_ < -60 && out_ > -150 && f > 40) maxDvyIn = MathF.Max(maxDvyIn, dvy);
            if (!crossed && out_ >= 0) { crossed = true; speedAtEdge = v.Speed; }
            if (out_ > 300) break;
            setup.Damage?.Events.Clear();
        }
        float past = (float)(edge - v.Pos.Z);
        log.WriteLine($"{mission} at {hz} Hz: crossed at {speedAtEdge * 3.6f:0} km/h, now {past:0} m out at {v.Speed * 3.6f:0} km/h, {v.Pos.Y - s.World.Ground.BareHeight(v.Pos.X, v.Pos.Z):0.00} m over the ground; " +
                      $"vertical speed changes a tick: up to {maxDvyEdge:0.000} m/s at the edge, {maxDvyIn:0.000} on the map before it");
        Assert.True(crossed && speedAtEdge > 15, $"{mission}: crossed the edge at {speedAtEdge} m/s");
        Assert.True(past > 300, $"{mission}: only {past:0} m out");
        Assert.True(v.Speed > speedAtEdge * 0.9f, $"{mission}: slowed from {speedAtEdge:0.0} to {v.Speed:0.0} m/s out there");
        Assert.True((v.Flags & VehicleFlags.Airborne) == 0 && MathF.Abs((float)v.Pos.Y - s.World.Ground.BareHeight(v.Pos.X, v.Pos.Z)) < 1, $"{mission}: off the ground out there");
        Assert.True(maxDvyEdge <= MathF.Max(0.5f, 2 * maxDvyIn), $"{mission}: the vertical speed jumps {maxDvyEdge:0.000} m/s a tick at the edge ({maxDvyIn:0.000} on the map)");
    }

    /// <summary>
    /// The AI drives on the outside ground as on the map (it reads the same ground): a car off the map sent after a target off the map
    /// closes on it there. (It does not cross the edge to get out: the stock maps flag their outermost samples impassable, all of them,
    /// which the AI's planner and probes keep off, as the original's AI does; that is the maps' own rule and stays.)
    /// </summary>
    [Fact]
    public void AnAiCarOffTheMapDrivesAfterItsTargetThere()
    {
        if (Dir("I76_INSTALL") is null) return;
        var install = new GameInstall(Dir("I76_INSTALL")!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var s = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("t01")!), asm, rand: new CrtRand(1));
        var h = s.Script!.Host;
        var player = s.Entities.First(e => e.Vehicle == s.World.Player);
        var car = s.Entities.First(e => e.Vehicle is { } cv && cv != s.World.Player && !cv.IsHeli && !cv.IsEmplacement);
        foreach (var e in s.Entities.Where(e => e != car && e != player && e.Vehicle is not null)) s.World.Vehicles.Remove(e.Vehicle!);
        // the edge's samples are impassable to the AI, on every stock map
        Assert.True(s.Ground.Impassable(2025, 34560 - 2) || s.Ground.Impassable(2025, 34560 + 2));
        Put(s, s.World.Player!, 1700, 34150, 0);      // the player 410 m out past T01's south edge
        Put(s, car.Vehicle!, 2100, 34450, MathF.PI);  // the AI car 110 m out, 500 m from it
        float dt = SimRate.FromLabel(20).TickSeconds;
        float start = Vector3.Distance(car.Vehicle!.Pos.ToVector3(), s.World.Player!.Pos.ToVector3()), nearest = start;
        double outside = double.MaxValue, airborne = 0;
        for (int i = 0; i < (int)(40 / dt); i++)
        {
            h.Now += dt; h.FsmTick++; h.FrameDelta = dt;
            h.Order(car, player, AiOrder.Attack, null, 0);
            h.Ptable.EndFrame();
            s.World.Frame(dt);
            var cv = car.Vehicle!;
            nearest = MathF.Min(nearest, Vector3.Distance(cv.Pos.ToVector3(), s.World.Player!.Pos.ToVector3()));
            outside = System.Math.Min(outside, 34560 - cv.Pos.Z);
            if ((cv.Flags & VehicleFlags.Airborne) != 0) airborne += dt;
        }
        var v = car.Vehicle!;
        log.WriteLine($"the AI car {car.Name}: {start:0} m from its target, came within {nearest:0} m, never nearer the map than {outside:0} m out, {airborne:0.0} s in the air; now {v.Pos.Y - s.Ground.BareHeight(v.Pos.X, v.Pos.Z):0.00} m over the ground");
        Assert.True(nearest < 60, $"the AI car came no nearer than {nearest:0} m");
        Assert.True(outside > 0, "it stayed off the map");
        Assert.True(airborne < 1, $"{airborne:0.0} s in the air");
    }

    /// <summary>The upright reset off the map stands the car on the outside ground, on a mountain's slope far out, at 0 on the plain.</summary>
    [Fact]
    public void TheUprightResetOffTheMapStandsTheCarOnTheOutsideGround()
    {
        if (Dir("I76_INSTALL") is null) return;
        var (setup, _, v) = Drive("I76_INSTALL", "t01", false, 20);
        double x = 2500, z = 32500;   // 2 km out, on the mountain's flank south of T01
        v.Pos = new Double3(x, 400, z);
        v.Rot = Rows.Concat(Rows.AboutZ(2.5f), Rows.AboutY(0.4f));
        v.Vel = new Vector3(3, -9, 2);
        UprightReset.Apply(setup.World, v);
        float g = setup.Ground.BareHeight(x, z);
        log.WriteLine($"reset at {x},{z}: ground {g:0.00} (the original's 0), car at {v.Pos.Y:0.00}, up {v.Rot.Up}");
        Assert.True(g > 50, "the outside ground there is the mountain's flank, not 0");
        Assert.Equal(g, (float)v.Pos.Y, 2);
        Assert.True(Vector3.Dot(v.Rot.Up, setup.Ground.BareNormal(x, z)) > 0.999f);
        // 500 m out, on the plain
        (x, z) = (2025, 34060);
        v.Pos = new Double3(x, 50, z);
        v.Rot = Rows.Concat(Rows.AboutZ(2.5f), Rows.AboutY(0.4f));
        UprightReset.Apply(setup.World, v);
        Assert.Equal(0f, setup.Ground.BareHeight(x, z));
        Assert.Equal(0f, (float)v.Pos.Y, 2);
    }
}
