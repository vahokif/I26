using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Driving off the map onto the outside ground (OutsideGround): a car at speed crosses the map's edge as it crosses any other line of
/// samples (no take-off, no landing, no impact, no loss of speed), and runs on out there; the AI drives out after a car; the upright
/// reset puts a car on the outside ground. The original reads 0 past the map's tiles: its edge cell falls to 0, a drop the old port
/// had too.
/// </summary>
public class OutsideGroundDriveTests(ITestOutputHelper log)
{
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

    private static void Put(MissionSetup s, Vehicle v, double x, double z, float yaw)
    {
        var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        v.Pos = new Double3(x, s.Ground.BareHeight(x, z), z);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        v.Vel = default; v.AngVel = default; v.Speed = 0;
    }

    /// <summary>
    /// Full throttle from 200 m inside the map, straight out across its edge (T01's south edge; N01's, a Nitro arena): the car crosses at
    /// speed with all four wheels down, no landing or impact anywhere near the edge, its vertical speed changing no more from one tick to
    /// the next there than on the map before it, and keeps its speed; 300 m out it is still on the ground and going.
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

    /// <summary>The upright reset off the map stands the car on the outside ground, on its slope.</summary>
    [Fact]
    public void TheUprightResetOffTheMapStandsTheCarOnTheOutsideGround()
    {
        if (Dir("I76_INSTALL") is null) return;
        var (setup, _, v) = Drive("I76_INSTALL", "t01", false, 20);
        double x = 2025, z = 33700;   // 860 m out, on the mountain's flank south of T01
        v.Pos = new Double3(x, 400, z);
        v.Rot = Rows.Concat(Rows.AboutZ(2.5f), Rows.AboutY(0.4f));
        v.Vel = new Vector3(3, -9, 2);
        UprightReset.Apply(setup.World, v);
        float g = setup.Ground.BareHeight(x, z);
        log.WriteLine($"reset at {x},{z}: ground {g:0.00} (the original's 0), car at {v.Pos.Y:0.00}, up {v.Rot.Up}");
        Assert.True(g > 50, "the outside ground there is the mountain's flank, not 0");
        Assert.Equal(g, (float)v.Pos.Y, 2);
        Assert.True(Vector3.Dot(v.Rot.Up, setup.Ground.BareNormal(x, z)) > 0.999f);
    }
}
