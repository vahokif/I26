using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Stopping distances against the real game:
/// Auto Melee m04, the Piranha on the flat run along +x from x 4172.5, a fixed 36 ms step; the engine started on frames
/// 70-80, full throttle from frame 100 to the release, then the brake or the handbrake alone. In a melee or a network
/// game multiplies the brake (2300/mass) by the car's BRAK part (the Piranha's 1.3); I26 had left it out,
/// and its melee and network cars stopped 21-23% later with the brake.
/// </summary>
public class BrakeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Theory]
    [InlineData(222, false, 31.1)]   // 60 mph
    [InlineData(222, true, 35.9)]
    [InlineData(425, false, 78.4)]   // 100 mph
    [InlineData(425, true, 98.0)]
    public void AMeleeCarStopsAsTheOriginals(int release, bool handbrake, double real)
    {
        if (InstallDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("m04")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), melee: new List<string>(), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(28));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        v.Pos = new Double3(4172.5, 119.9, 49552.5);
        v.Rot = new Rows(new Vector3(0, 0, -1), Vector3.UnitY, Vector3.UnitX);
        v.Vel = Vector3.Zero;
        double x0 = 0, speed0 = 0, dist = double.NaN;
        for (int f = 1; f <= release + 200 && double.IsNaN(dist); f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= 100 && f < release) i.Buttons = CarButtons.ThrottleUp;
            if (f >= release) i.Buttons = handbrake ? CarButtons.EBrake : CarButtons.ThrottleDown;
            if (f == release) (x0, speed0) = (v.Pos.X, v.Speed);
            s.Tick([i]);
            setup.Damage?.Events.Clear();
            if (f > release && new Vector2(v.Vel.X, v.Vel.Z).Length() < 0.1f) dist = v.Pos.X - x0;
        }
        output.WriteLine($"from {speed0 * 2.2369:0.0} mph, {(handbrake ? "handbrake" : "brake")}: {dist:0.0} m (the original {real} m)");
        Assert.InRange(dist, real * 0.95, real * 1.03);
    }

    /// <summary>
    /// A braking car that comes to rest keeps the braking acceleration for that frame's weight transfer: the pitch does
    /// not move on the stop frame (measured in the original, 34 stops, retail 1.0 on an emulated Pentium) and the nose
    /// rises on the next, when the acceleration is 0.
    /// </summary>
    [Fact]
    public void ABrakingCarsNoseHoldsOnTheStopFrameAndRisesOnTheNext()
    {
        if (InstallDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("m04")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(28));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        v.Pos = new Double3(4172.5, 119.9, 49552.5);
        v.Rot = new Rows(new Vector3(0, 0, -1), Vector3.UnitY, Vector3.UnitX);
        v.Vel = Vector3.Zero;
        double Pitch() => System.Math.Asin(v.Rot.Forward.Y) * 180 / System.Math.PI;
        var pitch = new List<double>();
        var moving = new List<bool>();
        for (int f = 1; f <= 400; f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= 100 && f < 222) i.Buttons = CarButtons.ThrottleUp;
            if (f >= 222) i.Buttons = CarButtons.ThrottleDown;
            s.Tick([i]);
            setup.Damage?.Events.Clear();
            pitch.Add(Pitch()); moving.Add(v.Vel != Vector3.Zero);
            if (f > 222 && !moving[^1]) break;
        }
        int stop = moving.Count - 1;
        Assert.True(stop > 0 && !moving[stop] && moving[stop - 1], "the car came to rest");
        s.Tick([new CarInput { Buttons = CarButtons.ThrottleDown }]);
        double next = Pitch();
        output.WriteLine($"pitch deg: before {pitch[stop - 1]:0.000}, stop frame {pitch[stop]:0.000}, next {next:0.000}");
        Assert.InRange(pitch[stop] - pitch[stop - 1], -0.01, 0.01);
        Assert.True(next - pitch[stop] > 0.1, $"the nose rises on the next frame: {pitch[stop]:0.000} -> {next:0.000}");
    }

    /// <summary>A mission ignores the BRAK part (with neither a melee nor a network session): 2300/mass.</summary>
    [Fact]
    public void AMissionCarsBrakeIsTheMassAlone()
    {
        if (InstallDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var mission = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var melee = VehicleFactory.Create(vfs, "vppirna1.vcf", meleeBrakes: true);
        Assert.Equal(2300f / mission.P.Mass, mission.P.BrakeStrength, 5);
        Assert.Equal(1.3f * mission.P.BrakeStrength, melee.P.BrakeStrength, 4);
    }
}
