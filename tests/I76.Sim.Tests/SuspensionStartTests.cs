using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A placed car's suspension history (#141): the original's entity set-up sets each wheel's ground and last target to
/// the ground under the wheel, and the rate to 0, before the first step, so a car at rest does not move on its springs.
/// Left at 0, every car took the whole terrain height as its first bump and bobbed as the mission began.
/// </summary>
public class SuspensionStartTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Ground of any shape, with no bump (roughness 0).</summary>
    private sealed class Shaped(Func<double, double, float> height) : IGround
    {
        public Func<double, double, float> Height = height;
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (Height(x, z), Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static Shaped Level(float h) => new((_, _) => h);

    /// <summary>A car as the step tests build it, at <c>height</c>, its history as built (unprimed).</summary>
    private static Vehicle Car(float height)
    {
        var v = new Vehicle { Pos = new Double3(0, height, 0) };
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, z);
            w.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f;
        return v;
    }

    /// <summary>Each wheel's ground and last target are the ground the step queries at the wheel, its rate 0.</summary>
    private static void AssertPrimed(Vehicle v, IGround g, string who)
    {
        foreach (var w in v.Wheels.Concat(v.MiddleWheels))
        {
            var p = v.Pos.ToVector3() + v.Rot.ToWorld(w.Mount);
            float h = g.Query(p.X, p.Y, p.Z).Height;
            Assert.True(w.Ground == h && w.PrevTarget == h && w.PrevRate == 0,
                $"{who}: ground {w.Ground}, last target {w.PrevTarget}, rate {w.PrevRate}; under the wheel {h}");
        }
    }

    [Theory]
    [InlineData(3.3f)]
    [InlineData(64f)]
    [InlineData(400f)]
    public void APlacedCarAtRestStandsStillAtAnyHeight(float height)
    {
        var v = Car(height);
        var g = Level(height);
        VehicleStep.PrimeWheels(v, g);
        var step = new VehicleStep(g, new CrtRand());
        for (int i = 0; i < 40; i++)
        {
            step.Step(v, 0.04f, true);
            foreach (var w in v.Wheels) Assert.True(MathF.Abs(w.Travel) < 1e-6f, $"substep {i}: travel {w.Travel}");
        }
    }

    /// <summary>The unprimed history (0) on ground at 3.3: the first input is 5 × 3.3 and the springs ring at their limits.</summary>
    [Fact]
    public void UnprimedACarTakesTheTerrainHeightAsABump()
    {
        var v = Car(3.3f);
        var step = new VehicleStep(Level(3.3f), new CrtRand());
        var t = new float[40];
        for (int i = 0; i < 40; i++) { step.Step(v, 0.04f, true); t[i] = v.FrontLeft.Travel; }
        output.WriteLine(string.Join(" ", t.Select(x => x.ToString("0.0000"))));
        Assert.Equal(0.25f * 0.33f, t[0]);
        Assert.True(t.Count(x => MathF.Abs(x) >= 0.25f * 0.33f) >= 5, string.Join(" ", t));
    }

    [Fact]
    public void OnASlopeEachWheelTakesItsOwnGround()
    {
        var g = new Shaped((x, z) => (float)(10 + 0.2 * x + 0.1 * z));
        var v = Car(10);
        v.Rot = Rows.AboutY(0.4f);
        VehicleStep.PrimeWheels(v, g);
        AssertPrimed(v, g, "slope");
        Assert.Equal(4, v.Wheels.Select(w => w.Ground).Distinct().Count());
    }

    /// <summary>
    /// The prime sets the history only: a primed car at rest on ground at 20 that then meets a 0.5 step gets the
    /// filter's whole input, 5 × 0.5, on that substep, goes to its upper limit and settles as before.
    /// </summary>
    [Fact]
    public void APrimedCarStillFeelsAStep()
    {
        var g = Level(20);
        var v = Car(20);
        VehicleStep.PrimeWheels(v, g);
        var step = new VehicleStep(g, new CrtRand());
        for (int i = 0; i < 5; i++) step.Step(v, 0.04f, true);
        Assert.True(MathF.Abs(v.FrontLeft.Travel) < 1e-6f);
        g.Height = (_, _) => 20.5f;
        step.Step(v, 0.04f, true);
        var w = v.FrontLeft;
        Assert.Equal(5.0 * 0.5, w.Y1 / w.B0, 3);
        Assert.Equal(w.TravelMax, w.Travel);
        for (int i = 0; i < 60; i++) step.Step(v, 0.04f, true);
        Assert.True(MathF.Abs(v.FrontLeft.Travel) < 1e-3f, $"still ringing: {v.FrontLeft.Travel}");
    }

    /// <summary>A melee's cars, at its start and joining after it: each placed on its spawn point with its wheels primed there.</summary>
    [Fact]
    public void AMeleePlacesEveryCarPrimed()
    {
        var g = Level(7.5f);
        var world = new SimWorld(g, new CrtRand(5));
        var host = new ScriptHost(world, [], (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => 1f);
        var game = new MeleeGame
        {
            World = world, Host = host, Rand = world.Rand, Roster = ["x.vcf", "x.vcf"], PlayerCar = "x.vcf",
            Build = (_, _) => Car(0), Rules = new MeleeRules { Network = true },
        };
        for (int i = 0; i < 6; i++) game.Spawns.Add(new MeleeGame.Spot(Rows.Identity, new Double3(i * 40, 0, 0)));
        game.AddPlayer("x.vcf", "early");
        game.Start();
        game.AddPlayer("x.vcf", "late");
        Assert.Equal(4, world.Vehicles.Count);
        foreach (var v in world.Vehicles) AssertPrimed(v, g, $"car at {v.Pos.X}");
    }

    /// <summary>
    /// T01 through the mission's set-up, alone and as a co-op game of two (the second seat stood beside the first), and
    /// the second seat's car chosen again in place: every car's wheels are primed on the ground under them. Skipped
    /// without an install.
    /// </summary>
    [Fact]
    public void T01sCarsArePrimedWhereTheyArePlaced()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        foreach (var coop in new[] { null, CoopSetup.Of(2) })
        {
            var vfs = new AssetVfs(install);
            var asm = new ModelAssembler(vfs);
            var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("t01")!), asm, coop: coop);
            var world = setup.World;
            Assert.True(world.Vehicles.Count >= 2);
            foreach (var v in world.Vehicles) AssertPrimed(v, world.Ground, $"{(coop is null ? "" : "co-op ")}{v.Definition}");
            if (coop is null) continue;
            var seat = setup.Script!.Host.Coop!.Seats[1].Car;
            seat.Pos = new Double3(seat.Pos.X + 30, seat.Pos.Y, seat.Pos.Z);
            setup.Script.Host.CoopRecar(1, "vcgcour1.vcf");
            AssertPrimed(seat, world.Ground, "co-op seat 1 after a change of car");
        }
    }

    private static void Kill(MissionSetup setup, Vehicle victim)
    {
        for (int k = 0; k < 200 && !Damage.DamageModel.IsDead(victim); k++)
        {
            var p = new DamagePacket { Mask = 2, Kind = Damage.DamageKind.Round };
            p.Ch[1] = 5000;
            setup.Damage.Apply(victim, p, new Vector3(0, 0.5f, 2));
        }
        Assert.True(Damage.DamageModel.IsDead(victim));
    }

    /// <summary>
    /// A respawn is a fresh car (the original makes every new car through its entity set-up): a network melee's at a spawn
    /// point and a co-op seat's beside its teammate are primed there before they step. Skipped without an install.
    /// </summary>
    [Fact]
    public void ARespawnedCarIsPrimedWhereItComesBack()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        {
            var vfs = new AssetVfs(install);
            var asm = new ModelAssembler(vfs);
            var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!), asm,
                melee: [], meleeRules: new MeleeRules { Network = true }, seed: 7);
            var game = setup.Melee!;
            game.AddPlayer("vppirna1.vcf", "a");
            game.AddPlayer("vppirna1.vcf", "b");
            game.Start();
            var s = new SimSession(setup, SessionOptions.Network);
            foreach (var p in game.Players) s.AddDriver(p.Car);
            var b = game.Players[1];
            int renewed = 0;
            game.PlayerRenewed += v => { AssertPrimed(v, setup.World.Ground, "melee respawn"); renewed++; };
            Kill(setup, b.Car);
            s.Tick([]);
            for (int t = 0; t < 5.1f / s.Rate.TickSeconds; t++) s.Tick([]);
            Assert.Null(game.RequestRespawn(b));
            s.Tick([]);
            Assert.Equal(1, renewed);
        }
        {
            var vfs = new AssetVfs(install);
            var asm = new ModelAssembler(vfs);
            var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("t01")!), asm, coop: CoopSetup.Of(2));
            var c = setup.Script!.Host.Coop!;
            var s = new SimSession(setup);
            foreach (var seat in c.Seats) s.AddDriver(seat.Car);
            int back = 0;
            c.Happened += e => { if (e is CoopRespawned { Why: CoopReturn.Died } r) { AssertPrimed(c.Seats[r.Seat].Car, setup.World.Ground, "co-op respawn"); back++; } };
            Kill(setup, c.Seats[1].Car);
            for (int t = 0; t < 6f / s.Rate.TickSeconds && back == 0; t++) s.Tick([]);
            Assert.Equal(1, back);
        }
    }
}
