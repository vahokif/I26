using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The radar's contact list and the T/E/Q keys.</summary>
public class TargetingTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static (Combat C, Vehicle Me, Dictionary<Vehicle, int> Teams) Setup(params (double X, double Z, int Team)[] others)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var me = new Vehicle();
        world.Vehicles.Add(me);
        world.Player = me;
        var teams = new Dictionary<Vehicle, int> { [me] = 1 };
        foreach (var (x, z, t) in others)
        {
            var v = new Vehicle { Pos = new Double3(x, 0, z) };
            world.Vehicles.Add(v);
            teams[v] = t;
        }
        var c = new Combat(world, _ => null);
        c.Team = v => teams.GetValueOrDefault(v);
        return (c, me, teams);
    }

    private static Vehicle Car(Combat c, int i) => c.World.Vehicles[i];

    [Fact]
    public void TargetIsDroppedOnDeathEngineOffAndPast600m()
    {
        var (c, me, _) = Setup((0, 100, 2), (0, 200, 2), (0, 300, 2));
        foreach (var i in new[] { 1, 2, 3 }) c.RadarTarget[me] = Car(c, i);
        c.Frame(0.03f);
        Assert.Equal(Car(c, 3), c.RadarTarget[me]);

        Car(c, 3).Flags |= VehicleFlags.Dead;
        c.Frame(0.03f);
        Assert.Null(c.RadarTarget[me]);

        c.RadarTarget[me] = Car(c, 2);
        Car(c, 2).Flags &= ~VehicleFlags.EngineOn;
        c.Frame(0.03f);
        Assert.Null(c.RadarTarget[me]);

        c.RadarTarget[me] = Car(c, 1);
        Car(c, 1).Pos = new Double3(0, 0, 600.5);
        c.Frame(0.03f);
        Assert.Null(c.RadarTarget[me]);
        // range is planar: height does not count
        Car(c, 1).Pos = new Double3(0, 900, 590);
        c.RadarTarget[me] = Car(c, 1);
        c.Frame(0.03f);
        Assert.Equal(Car(c, 1), c.RadarTarget[me]);
    }

    [Fact]
    public void ESkipsOwnTeamAndTeamZeroOffline_AndRestartsAfterFiveSeconds()
    {
        var (c, me, _) = Setup((0, 50, 1), (0, 100, 0), (0, 200, 2), (0, 300, 2));
        var a = Car(c, 3); var b = Car(c, 4);
        Assert.Equal(a, c.NextTarget(me));   // own team (50 m) and team 0 (100 m) are skipped
        c.RadarTarget[me] = a;
        c.Frame(1f);
        Assert.Equal(b, c.NextTarget(me));   // within 5 s: on to the next
        c.RadarTarget[me] = b;
        c.Frame(5.5f);
        Assert.Equal(a, c.NextTarget(me));   // idle for over 5 s: from the nearest again

        // a melee alone does not open team 0 to E (the original tests the network session only)
        c.MeleeActive = true;
        c.RadarTarget[me] = null;
        c.Frame(6f);
        Assert.Equal(a, c.NextTarget(me));
        c.NetSession = true;
        c.RadarTarget[me] = null;
        c.Frame(6f);
        Assert.Equal(Car(c, 2), c.NextTarget(me));
    }

    [Fact]
    public void TExcludesTeamZeroOffline()
    {
        var (c, me, _) = Setup((0, 100, 0), (0, 200, 2));
        Assert.Equal(Car(c, 2), c.NearestEnemy(me));
        // T opens team 0 in a melee (the melee flag) as well as in a network session
        c.MeleeActive = true;
        Assert.Equal(Car(c, 1), c.NearestEnemy(me));
        c.MeleeActive = false;
        c.NetSession = true;
        Assert.Equal(Car(c, 1), c.NearestEnemy(me));
        // a pick that is not a contact clears the target
        Car(c, 1).Flags &= ~VehicleFlags.EngineOn;
        Assert.Null(c.NearestEnemy(me));
    }

    [Fact]
    public void QUsesFifteenDegreesAnd650m_AnyTeam()
    {
        // 15 degrees at 200 m ahead: 53.6 m sideways is the edge
        var (c, me, _) = Setup((50, 200, 1), (57, 200, 2));
        Assert.Equal(Car(c, 1), c.FrontalTarget(me));   // an ally, 14 degrees
        Car(c, 1).Pos = new Double3(60, 0, 200);
        Assert.Null(c.FrontalTarget(me));               // 16.7 degrees
        // a contact needs 600 m planar; the arc reaches 650 m, so 601..650 m is picked but then dropped
        var (c2, me2, _) = Setup((0, 590, 2));
        Assert.Equal(Car(c2, 1), c2.FrontalTarget(me2));
        Car(c2, 1).Pos = new Double3(0, 0, 620);
        Assert.Null(c2.FrontalTarget(me2));
        c2.Frame(0.03f);
        Assert.Equal(Car(c2, 1), c2.TargetFor(me2, 1)); // a fixed gun still sees it (650 m)
        Car(c2, 1).Pos = new Double3(0, 0, 660);
        c2.Frame(0.03f);
        Assert.Null(c2.TargetFor(me2, 1));
    }

    [Fact]
    public void ArcsPickCarsOnly()
    {
        // The check takes object type 1: a helicopter or a gun emplacement in the arc is passed over
        var (c, me, _) = Setup((0, 50, 2), (0, 100, 2));
        Car(c, 1).Heli = new HeliState();
        c.Frame(0.03f);
        Assert.Equal(Car(c, 2), c.TargetFor(me, 1));
        Assert.Equal(Car(c, 2), c.FrontalTarget(me));
        Car(c, 2).Emplacement = new EmplacementState();
        c.Frame(0.03f);
        Assert.Null(c.TargetFor(me, 1));
        Assert.Null(c.FrontalTarget(me));
    }

    [Fact]
    public void AnAiFixedGunTakesItsOrderTargetOutsideANetworkGame()
    {
        // The AI's radar-target update writes the AI's order target into every arc slot; the original's routine never runs for it
        var (c, me, _) = Setup((0, 50, 2), (0, 0, 2), (300, -300, 1));
        var ai = Car(c, 2); var order = Car(c, 3);
        ai.Pos = new Double3(0, 0, 20);   // the player 20 m behind it, a car 30 m ahead of it
        c.RadarTarget[ai] = order;
        c.Frame(0.03f);
        foreach (int k in new[] { 1, 2, 8, 0x10 }) Assert.Equal(order, c.TargetFor(ai, k));
        Assert.Equal(order, c.TargetFor(ai, 4));        // the turret: the radar target, the same order
        Assert.Null(c.TargetFor(ai, 5));                // a kind the original has no slot for
        // in a network session every car's radar fills its own arcs
        c.NetSession = true;
        c.Frame(0.03f);
        Assert.Equal(Car(c, 1), c.TargetFor(ai, 1));
        Assert.Null(c.TargetFor(ai, 8));               // no handgun arm raised: no side arc
    }
}
