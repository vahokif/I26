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

/// <summary>Play Options Unlim. Ammo: the fire start and the burst clamp have no player test, the ammo count only changes for the player.</summary>
public class UnlimitedAmmoAiTests
{
    private const float Dt = 102 / 2048f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    private static (int Shots, int Ammo) Fire(bool unlimited, bool player, uint burst = 1, float delay = 0)
    {
        var world = new SimWorld(new Flat(), new CrtRand(3));
        var other = new Vehicle { Pos = new(50, 2, 0) };
        var car = new Vehicle { Pos = new(0, 2, 0) };
        world.Vehicles.Add(other); world.Vehicles.Add(car);
        world.Player = player ? car : other;
        var def = Armory.HandgunDef(1) with
        {
            Class = 2, Tier = 0, Kind = 1, Rate = 10, Burst = burst, Delay = delay, Speed = 300, Life = 2, Spread = 0, Ammo = 100,
            Ordnance = new OrdnanceDef(1, 300, 1, 0, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = 1, Ammo = 0,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        car.Armory = a;
        var c = new Combat(world, _ => null) { UnlimitedAmmo = unlimited };
        int shots = 0;
        for (int f = 0; f < 40; f++)
        {
            w.Trigger = true;
            c.Frame(Dt);
            shots += c.Events.OfType<ShotFired>().Count();
            c.Events.Clear(); c.Rounds.Clear();
        }
        return (shots, w.Ammo);
    }

    [Fact]
    public void AnAiCarWithNoAmmoKeepsFiringWhileUnlimAmmoIsOn() => Assert.True(Fire(true, false).Shots > 0);

    [Fact]
    public void AnAiCarWithNoAmmoStopsWhenUnlimAmmoIsOff() => Assert.Equal(0, Fire(false, false).Shots);

    [Fact]
    public void ThePlayerWithNoAmmoFiresWithTheLatchAndItsCountStays()
    {
        var (shots, ammo) = Fire(true, true);
        Assert.True(shots > 0);
        Assert.Equal(0, ammo);
    }

    [Fact]
    public void AnAiCarWithNoAmmoFiresFullSalvosWhileUnlimAmmoIsOn()
    {
        // the burst clamp is skipped for every car under the latch: a 3-round weapon at 0 fires its whole salvo, as the
        // player's does, not one round a press
        int ai = Fire(true, false, burst: 3, delay: 1).Shots, player = Fire(true, true, burst: 3, delay: 1).Shots;
        Assert.Equal(player, ai);
        Assert.True(ai >= 2 * Fire(true, false, burst: 1, delay: 1).Shots, $"{ai} rounds");
    }
}
