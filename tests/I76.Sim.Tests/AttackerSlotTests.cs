using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The attacker-slot count (ai.md, on deck): it answers full as soon as the count of other live attackers reaches the
/// target's cap and free otherwise, so a cap of 0 or 1 both allow one attacker.
/// </summary>
public class AttackerSlotTests
{
    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ACapOfZeroOrOneAllowsOneAttacker(int cap)
    {
        var world = new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1));
        Entity Car(string name, float x)
        {
            var v = new Vehicle { Pos = new Double3(x, 0, 0) };
            world.Vehicles.Add(v);
            return new Entity(name, [], 1, 0) { Vehicle = v };
        }
        var target = Car("player", 0);
        var a = Car("a", 50);
        var b = Car("b", -50);
        target.MaxAttackers = cap;
        var host = new ScriptHost(world, new List<Entity> { target, a, b },
            (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);
        var slot = typeof(Ptable).GetMethod("SlotAvailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool Free(Entity e) => (bool)slot.Invoke(host.Ptable, [e, target])!;
        Assert.True(Free(a));                 // nobody attacks yet
        b.AttackCommit = target;
        Assert.False(Free(a));                // b holds the one slot
        Assert.True(Free(b));                 // b does not count itself
    }
}
