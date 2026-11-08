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

/// <summary>Pressing the trigger again while the weapon's interval or burst cooldown still runs gives the player the dry click.</summary>
public class TriggerRepressClickTests
{
    private const float Dt = 102 / 2048f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>One gun of 1 shot a second; <paramref name="pattern"/> is the trigger per tick ('#' held, '.' released).</summary>
    private static (int Shots, int Clicks) Run(string pattern, bool player = true)
    {
        var world = new SimWorld(new Flat(), new CrtRand(3));
        var other = new Vehicle { Pos = new(50, 2, 0) };
        var car = new Vehicle { Pos = new(0, 2, 0) };
        world.Vehicles.Add(other); world.Vehicles.Add(car);
        world.Player = player ? car : other;
        var def = Armory.HandgunDef(1) with
        {
            Class = 2, Tier = 0, Kind = 1, Rate = 1, Burst = 1, Delay = 0, Speed = 300, Life = 2, Spread = 0, Ammo = 100,
            Ordnance = new OrdnanceDef(1, 300, 1, 0, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = 1, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        car.Armory = a;
        var c = new Combat(world, _ => null);
        int shots = 0, clicks = 0;
        foreach (char t in pattern)
        {
            w.Trigger = t == '#';
            c.Frame(Dt);
            shots += c.Events.OfType<ShotFired>().Count();
            clicks += c.Events.OfType<WeaponClick>().Count(e => e.Sound == "wclick.wav");
            c.Events.Clear(); c.Rounds.Clear();
        }
        return (shots, clicks);
    }

    [Fact]
    public void PressReleasePressWithinTheIntervalClicksOnceAndFiresNothing() =>
        Assert.Equal((1, 1), Run("#..#.."));

    [Fact]
    public void AHeldTriggerNeverClicksWhileTheIntervalRuns() =>
        Assert.Equal((1, 0), Run("########"));

    [Fact]
    public void EachFreshPressDuringTheIntervalClicks() =>
        Assert.Equal((1, 2), Run("#.#.#"));

    [Fact]
    public void AnAiCarGetsNoClick() => Assert.Equal((1, 0), Run("#..#..", player: false));
}
