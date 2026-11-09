using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The snapshot's format and its refusals, on a world made in code (no install needed).</summary>
public class SnapshotFormatTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 3);
    }

    private static readonly VehicleBuild Build = new([120, 80, 80, 60], [60, 40, 40, 30], [80, 80, 80], 300, 150, 200, 1f, [1, 2]);

    private static Vehicle Car(double x, double z, bool player, CrtRand rand)
    {
        var v = new Vehicle { Pos = new Double3(x, 0.5, z), Build = Build };
        (v.FrontRight.Offset, v.FrontLeft.Offset, v.RearRight.Offset, v.RearLeft.Offset) =
            (new(0.7f, 0.35f, 1.3f), new(-0.7f, 0.35f, 1.3f), new(0.7f, 0.35f, -1.3f), new(-0.7f, 0.35f, -1.3f));
        foreach (var w in v.Wheels) { w.InitSuspension(); w.Grip = 1; w.Rolling = 1; }
        v.Health = VehicleDamage.Create(v, Build, player, rand);
        return v;
    }

    /// <summary>Three cars on a flat world, one the player's, with the damage model; built the same for any seed but the rand stream.</summary>
    private static SimSession World(uint seed, SessionBuild? build = null, SimRate? rate = null)
    {
        var rand = new CrtRand(seed);
        var world = new SimWorld(new Flat(), rand);
        world.Vehicles.AddRange([Car(0, 0, true, rand), Car(6, 30, false, rand), Car(-8, 60, false, rand)]);
        world.Player = world.Vehicles[0];
        var combat = new Combat(world, _ => null);
        _ = new DamageModel(world, combat);
        var s = new SimSession(world, combat, null, SessionOptions.SinglePlayer, rate) { Build = build };
        s.AddDriver(world.Player);
        return s;
    }

    private static void Run(SimSession s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            int t = s.Ticks;
            s.Tick([SnapshotRig.Input(t, 0)]);
            // the cars ram each other now and then
            if (t % 37 == 0) foreach (var v in s.World.Vehicles.Skip(1)) { v.Throttle = 0.9f; v.Steer = (t % 74 == 0) ? 0.3f : -0.3f; }
        }
    }

    [Fact]
    public void AWorldMadeInCodeRestoresExactlyAndSnapshotsBackTheSame()
    {
        var a = World(1);
        Run(a, 120);
        var snap = a.Snapshot();
        var dump = StateDump.Of(a);
        var b = World(99);
        b.Restore(snap);
        Assert.Null(StateDump.FirstDifference(dump, StateDump.Of(b)));
        Assert.Equal(snap, b.Snapshot());
        Assert.Equal(a.StateHash(), b.StateHash());
        Run(a, 200); Run(b, 200);
        Assert.Null(StateDump.FirstDifference(StateDump.Of(a), StateDump.Of(b)));
    }

    [Fact]
    public void ACarsPaintIsSavedAndRestored()
    {
        Assert.True(SnapshotHeader.CurrentVersion >= 13, "the paint is in format 13 and later");
        var paint = new I76.Formats.Imaging.CarPaint(new I76.Formats.Imaging.PaintColour(9, 8, 7), new I76.Formats.Imaging.PaintColour(1, 2, 3), I76.Formats.Imaging.PaintFinish.Matte);
        var a = World(1);
        a.World.Vehicles[1].Paint = paint;
        Run(a, 30);
        var snap = a.Snapshot();
        var b = World(99);
        b.Restore(snap);
        Assert.Equal(paint, b.World.Vehicles[1].Paint);
        Assert.Null(b.World.Vehicles[0].Paint);
        Assert.Equal(snap, b.Snapshot());
    }

    [Fact]
    public void TheHeaderSaysWhatToBuildAndCarriesTheHostsData()
    {
        var build = new SessionBuild { Mission = "t07", Install = 0x1234, Car = "vppirna1.vcf", Melee = ["a.vcf", "b.vcf"], Rules = new Missions.MeleeRules { Network = true, GoalValue = 5, IgnitionStaysOn = true } };
        var s = World(1, build, SimRate.R28);
        Run(s, 30);
        var snap = s.Snapshot([1, 2, 3]);
        var h = SnapshotHeader.Read(snap);
        Assert.Equal(SnapshotHeader.CurrentVersion, h.Version);
        Assert.Equal(build.Mission, h.Build.Mission);
        Assert.Equal(build.Install, h.Build.Install);
        Assert.Equal(build.Car, h.Build.Car);
        Assert.Equal(build.Melee, h.Build.Melee);
        Assert.Equal(build.Rules, h.Build.Rules);
        Assert.True(h.Build.Rules!.IgnitionStaysOn);   // the host's ignition lock (format 16)
        Assert.Equal(SimRate.R28, h.Rate);
        Assert.Equal(30, h.Ticks);
        Assert.Equal([1, 2, 3], h.Host);
    }

    [Fact]
    public void ASnapshotOfAnotherMissionInstallCarOrRateIsRefused()
    {
        var build = new SessionBuild { Mission = "t07", Install = 0x1234, Car = "vppirna1.vcf" };
        var s = World(1, build);
        Run(s, 10);
        var snap = s.Snapshot();
        string Why(SessionBuild? b, SimRate? rate = null) => Assert.Throws<SnapshotMismatchException>(() => World(2, b, rate).Restore(snap)).Message;
        Assert.Contains("mission t07", Why(build with { Mission = "t08" }));
        Assert.Contains("different install", Why(build with { Install = 0x4321 }));
        Assert.Contains("car", Why(build with { Car = "vcgcour1.vcf" }));
        Assert.Contains("20 Hz", Why(build, SimRate.R28));
        Assert.Contains("mission", Why(null));
        World(3, build).Restore(snap);   // the same build takes it
    }

    [Fact]
    public void AnotherFormatVersionOrABrokenSnapshotIsRefused()
    {
        var s = World(1);
        Run(s, 10);
        var snap = s.Snapshot();
        var other = (byte[])snap.Clone();
        other[SnapshotHeader.Magic.Length] = (byte)((SnapshotHeader.CurrentVersion + 1) << 1);   // the version, zigzagged
        Assert.Contains("format version", Assert.Throws<SnapshotMismatchException>(() => World(2).Restore(other)).Message);
        Assert.Throws<SnapshotFormatException>(() => World(2).Restore([1, 2, 3]));
        Assert.Throws<SnapshotFormatException>(() => World(2).Restore(snap[..^10]));
        var corrupt = (byte[])snap.Clone();
        corrupt[^5] ^= 0x5a;
        Assert.ThrowsAny<Exception>(() => World(2).Restore(corrupt));
    }

    /// <summary>A world whose cars are not those of the snapshot (one fewer) is refused, not half restored.</summary>
    [Fact]
    public void ASessionMadeUpDifferentlyIsRefused()
    {
        var s = World(1);
        Run(s, 10);
        var snap = s.Snapshot();
        var b = World(2);
        b.World.Vehicles.RemoveAt(2);
        Assert.Throws<SnapshotMismatchException>(() => b.Restore(snap));
    }
}
