using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>Co-op Damage (docs/deviations.md): the humans' cars of a co-op game take the host's factor of every hit; nobody else does.</summary>
public class CoopDamageTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 3);
    }

    private static readonly VehicleBuild Build = new([120, 80, 80, 60], [60, 40, 40, 30], [80, 80, 80], 300, 150, 200, 1f, [1, 2]);

    private static Vehicle Car(double x, bool human, CrtRand rand)
    {
        var v = new Vehicle { Pos = new Double3(x, 0.5, 0), Build = Build };
        foreach (var w in v.Wheels) w.InitSuspension();
        v.Health = VehicleDamage.Create(v, Build, human, rand);
        return v;
    }

    /// <summary>A human's car and an AI's, and what a 25-point round on each one's front armour takes from it.</summary>
    private static (int Human, int Ai) FrontArmourTaken(bool coop, float factor)
    {
        var rand = new CrtRand(1);
        var world = new SimWorld(new Flat(), rand) { Coop = coop };
        var (human, ai) = (Car(0, true, rand), Car(10, false, rand));
        world.Vehicles.AddRange([human, ai]);
        world.Player = human;
        world.Humans.Add(human);
        var dm = new DamageModel(world, new Combat(world, _ => null)) { CoopDamageFactor = factor };
        int Hit(Vehicle v)
        {
            int before = v.Health!.Armour[0];
            var p = new DamagePacket { Mask = 1, Kind = DamageKind.Round };
            p.Ch[0] = 25;
            dm.Apply(v, p, new Vector3(0, 0.5f, 2));
            return before - v.Health.Armour[0];
        }
        return (Hit(human), Hit(ai));
    }

    [Fact]
    public void AHumansCarTakesTheFactorOfARoundTruncated()
    {
        Assert.Equal((12, 25), FrontArmourTaken(coop: true, 0.5f));    // 12.5 truncated, as the original's own factor truncates 18.75 to 18
        Assert.Equal((18, 25), FrontArmourTaken(coop: true, 0.75f));
        Assert.Equal((37, 25), FrontArmourTaken(coop: true, 1.5f));
    }

    [Fact]
    public void TheOriginalsDamageAtOneAndOutsideCoop()
    {
        Assert.Equal((25, 25), FrontArmourTaken(coop: true, 1f));
        Assert.Equal((25, 25), FrontArmourTaken(coop: false, 0.5f));   // single player, a melee: never scaled
    }

    [Fact]
    public void TheFactorsOnOffer()
    {
        Assert.Equal(1f, new CoopSetup([null]).DamageFactor);
        Assert.Equal(0.5f, CoopSetup.ParseDamageFactor("0.5"));
        Assert.Equal(0.75f, CoopSetup.ParseDamageFactor("x0.75"));
        Assert.Equal(1.5f, CoopSetup.ParseDamageFactor("1.5"));
        Assert.Null(CoopSetup.ParseDamageFactor("0.6"));
        Assert.Null(CoopSetup.ParseDamageFactor("hard"));
    }

    /// <summary>The factor is part of the build: a snapshot names it, and a session with another refuses the snapshot.</summary>
    [Fact]
    public void TheSnapshotsBuildCarriesTheFactor()
    {
        SimSession Session(float factor)
        {
            var rand = new CrtRand(1);
            var world = new SimWorld(new Flat(), rand);
            world.Vehicles.Add(Car(0, true, rand));
            world.Player = world.Vehicles[0];
            var combat = new Combat(world, _ => null);
            _ = new DamageModel(world, combat);
            var s = new SimSession(world, combat, null, SessionOptions.SinglePlayer)
            {
                Build = new SessionBuild { Mission = "t01", Install = 1, Coop = new CoopSetup([null]) { DamageFactor = factor } },
            };
            s.AddDriver(world.Player);
            return s;
        }
        var snap = Session(0.5f).Snapshot();
        Assert.Equal(0.5f, SnapshotHeader.Read(snap).Build.Coop!.DamageFactor);
        Assert.Contains("co-op damage", Assert.Throws<SnapshotMismatchException>(() => Session(1f).Restore(snap)).Message);
        Session(0.5f).Restore(snap);
    }
}
