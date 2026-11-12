using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Exe;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #176, the Nitro Pack's Mother: the chased car evades the player until it is beaten, then sits; its turret swung back
/// onto the player. The original's AI order writes the order's target into the AI's current target whatever it is, none
/// for sit, hide and goto, and an AI car's radar holds that target, so its turrets go back to straight ahead.
/// </summary>
public class TargetlessOrderTests
{
    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>A tactics table whose 34 states do nothing: enough to run the order driver without an install.</summary>
    private static PtEntry[] Blank() =>
        [.. Enumerable.Range(0, 34).Select(i => new PtEntry(i, $"state{i}", 0, 0, 0, [], [], []))];

    private static (ScriptHost Host, Combat Combat, Entity Car, Entity Player, Entity Gun) World()
    {
        var world = new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1));
        Entity Make(string name, float x, Action<Vehicle>? set = null)
        {
            var v = new Vehicle { Pos = new Double3(x, 0, 0) };
            set?.Invoke(v);
            world.Vehicles.Add(v);
            return new Entity(name, [], 1, 0) { Vehicle = v };
        }
        var player = Make("groove", 0);
        world.Player = player.Vehicle;
        var car = Make("mother", 60);
        var gun = Make("gun", -60, v => v.Emplacement = new EmplacementState());
        var host = new ScriptHost(world, [player, car, gun], (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null) { Tactics = Blank() };
        var combat = new Combat(world, _ => null);
        host.Combat = combat;
        return (host, combat, car, player, gun);
    }

    [Theory]
    [InlineData(AiOrder.Sit)]
    [InlineData(AiOrder.Hide)]
    [InlineData(AiOrder.Goto)]
    public void AnOrderWithNoTargetLeavesTheRadarEmpty(AiOrder order)
    {
        var (host, combat, car, player, _) = World();
        host.Order(car, player, AiOrder.Guard, null, 0);              // an order at the player: the radar holds it
        Assert.Same(player.Vehicle, combat.RadarTarget[car.Vehicle!]);
        host.Order(car, null, order, order == AiOrder.Goto ? [new Vector3(60, 0, 100)] : null, 30);
        Assert.Null(combat.RadarTarget.GetValueOrDefault(car.Vehicle!));
        Assert.Null(combat.AimTargetFor(car.Vehicle!, 4));              // the turrets: straight ahead
    }

    /// <summary>The player's own radar and an emplacement's (which always holds the player) are not the order's.</summary>
    [Fact]
    public void ThePlayersAndAnEmplacementsRadarAreLeftAlone()
    {
        var (host, combat, _, player, gun) = World();
        combat.RadarTarget[gun.Vehicle!] = player.Vehicle;
        host.Order(gun, null, AiOrder.Sit, null, 0);
        Assert.Same(player.Vehicle, combat.RadarTarget[gun.Vehicle!]);
        var other = new Vehicle();
        combat.RadarTarget[player.Vehicle!] = other;
        host.Order(player, null, AiOrder.Sit, null, 0);               // the script does not hold the camera: dropped
        Assert.Same(other, combat.RadarTarget[player.Vehicle!]);
    }
}
