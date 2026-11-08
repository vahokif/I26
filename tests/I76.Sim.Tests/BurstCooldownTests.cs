using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>A weapon with a burst cooldown (none of the stock machine guns has one): the cooldown follows the last round of each burst.</summary>
public class BurstCooldownTests
{
    private const float Dt = 102 / 2048f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>Rounds fired per tick while the trigger is held for <paramref name="seconds"/>.</summary>
    private static List<int> Held(float rate, uint burst, float cooldown, float seconds)
    {
        var world = new SimWorld(new Flat(), new CrtRand(3));
        var car = new Vehicle { Pos = new(0, 2, 0) };
        world.Vehicles.Add(car);
        world.Player = car;
        var def = Armory.HandgunDef(1) with
        {
            Class = 2, Tier = 0, Kind = 1, Rate = rate, Burst = burst, Delay = cooldown, Speed = 300, Life = 2, Spread = 0, Ammo = 10000,
            Ordnance = new OrdnanceDef(1, 300, 1, 0, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = 1, Ammo = 10000,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        car.Armory = a;
        var c = new Combat(world, _ => null);
        var fired = new List<int>();
        for (int f = 0; f * Dt < seconds; f++)
        {
            int before = w.Ammo;
            w.Trigger = true;
            c.Frame(Dt);
            fired.Add(before - w.Ammo);
            c.Events.Clear(); c.Rounds.Clear();
        }
        return fired;
    }

    [Fact]
    public void ABurstOfThreeIsFollowedByTheCooldownBeforeTheNext()
    {
        var fired = Held(25, 3, 2f, 8f);
        // tick times of the rounds
        var times = new List<float>();
        for (int i = 0; i < fired.Count; i++) for (int k = 0; k < fired[i]; k++) times.Add(i * Dt);
        Assert.True(times.Count >= 6, $"{times.Count} rounds");
        for (int i = 3; i < times.Count; i += 3)
            Assert.True(times[i] - times[i - 1] >= 1.9f, $"burst gap {times[i] - times[i - 1]:0.00} s before round {i}");
        for (int i = 1; i < times.Count; i++)
            if (i % 3 != 0) Assert.True(times[i] - times[i - 1] < 0.3f, $"in-burst gap {times[i] - times[i - 1]:0.00} s before round {i}");
    }

    [Fact]
    public void ASingleRoundBurstWithARateFasterThanItsCooldownWaitsTheCooldown()
    {
        var fired = Held(25, 1, 1.5f, 6f);
        var times = new List<float>();
        for (int i = 0; i < fired.Count; i++) for (int k = 0; k < fired[i]; k++) times.Add(i * Dt);
        Assert.True(times.Count >= 3);
        for (int i = 1; i < times.Count; i++) Assert.True(times[i] - times[i - 1] >= 1.4f, $"gap {times[i] - times[i - 1]:0.00} s");
    }
}
