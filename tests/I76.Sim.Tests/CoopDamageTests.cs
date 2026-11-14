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
    public void AHumansCarTakesTheFactorOfARoundItsFractionCarried()
    {
        Assert.Equal((12, 25), FrontArmourTaken(coop: true, 0.5f));    // 12, the 0.5 carried to the car's next packet
        Assert.Equal((18, 25), FrontArmourTaken(coop: true, 0.75f));
        Assert.Equal((37, 25), FrontArmourTaken(coop: true, 1.5f));
    }

    /// <summary>What a human's car loses (armour, chassis and parts: a flame goes past the armour to the parts) to a flame's 1-point packets every tick for <paramref name="seconds"/>.</summary>
    private static int FlameLoss(float factor, double seconds)
    {
        var rand = new CrtRand(1);
        var world = new SimWorld(new Flat(), rand) { Coop = true };
        var human = Car(0, true, rand);
        world.Vehicles.Add(human);
        world.Player = human;
        world.Humans.Add(human);
        var dm = new DamageModel(world, new Combat(world, _ => null)) { CoopDamageFactor = factor };
        int Left() => human.Health!.Armour.Sum() + human.Health.Chassis.Sum() + human.Health.Slots.Sum(s => s?.Hp ?? 0);
        int before = Left();
        int ticks = SimRate.Default.TicksFor((float)seconds);
        for (int t = 0; t < ticks; t++)
        {
            var p = new DamagePacket { Mask = 4, Kind = DamageKind.Flame };
            p.Ch[2] = 1;     // FlameTouch's floor: a flame's tick never does less than one point
            dm.Apply(human, p, new Vector3(0, 0.5f, 2));
        }
        return before - Left();
    }

    [Fact]
    public void AFlamesOnePointTicksBurnAtTheFactorNotNone()
    {
        int full = FlameLoss(1f, 5), half = FlameLoss(0.5f, 5), most = FlameLoss(0.75f, 5), more = FlameLoss(1.5f, 5);
        // about the factor of x1's loss (the parts the loop picks differ a little); truncated per tick it was none at x0.5 and x0.75, x1 at x1.5
        Assert.True(full > 0);
        Assert.InRange(half, full * 0.4, full * 0.6);
        Assert.InRange(most, full * 0.65, full * 0.85);
        Assert.InRange(more, full * 1.3, full * 1.7);
    }

    [Fact]
    public void TheCarryIsPerCarAndSaved()
    {
        var carry = new float[4];
        var p = new DamagePacket { Mask = 4 };
        p.Ch[2] = 1;
        Assert.Equal(0, DamageModel.Scaled(p, 0.5f, carry).Ch[2]);
        Assert.Equal(0.5f, carry[2]);
        Assert.Equal(1, DamageModel.Scaled(p, 0.5f, carry).Ch[2]);
        Assert.Equal(0f, carry[2]);
        Assert.Equal(0, DamageModel.Scaled(p, 0.5f, null).Ch[2]);   // a car without a damage state: truncated

        var rand = new CrtRand(1);
        var v = Car(0, true, rand);
        v.Health!.CoopCarry[2] = 0.25f;
        var w = new SaveWriter(new SaveRefs());
        VehicleSave.Write(w, v);
        var back = Car(0, true, new CrtRand(1));
        VehicleSave.Read(new SaveReader(w.ToArray(), new SaveRefs()), back);
        Assert.Equal(0.25f, back.Health!.CoopCarry[2]);
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
