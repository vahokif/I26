using System.Numerics;
using I76.Formats.Bwd2;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The chase crosshair's question (<see cref="Combat.LineOfFire"/>): where a round fired now would land, by the flight's own hit
/// test, asked without firing. Its answer agrees with a real round's, and asking changes nothing.
/// </summary>
public class LineOfFireTests
{
    private const float Dt = 1 / 30f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>The 30cal MG's numbers (gmlight.gdf): 150 m/s, no gravity, 3.33 s, no spread so a real round flies the line exactly.</summary>
    private static GizmoDef Mg() => Armory.HandgunDef(1) with
    {
        Class = 2, Tier = 0, Kind = 1, Speed = 150, Life = 3.33f, Spread = 0, Rate = 10, Burst = 1, Delay = 0, Ammo = 100,
        Ordnance = new OrdnanceDef(1, 150, 0, 1, 0, ["", "", "", ""], ["", "", "", ""]),
    };

    private static (SimWorld World, Combat Combat, Vehicle Shooter, Weapon Gun) Rig(GizmoDef def, float pitch = 0)
    {
        var world = new SimWorld(new Flat(), new CrtRand(7));
        var car = new Vehicle { Pos = new(0, 0, 0) };
        world.Vehicles.Add(car);
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.AboutX(pitch),
            MuzzlePos = new Vector3(0, 1.2f, 2.2f), Hp = 45, MaxHp = 45, Damage = (int)def.Damage, Ammo = (int)def.Ammo,
        };
        var a = new Armory();
        a.Weapons.Add(w);
        a.LoadOrder.Add(w);
        a.Selected = 0;
        car.Armory = a;
        return (world, new Combat(world, _ => null), car, w);
    }

    private static Vehicle Target(SimWorld world, float x, float z)
    {
        var v = new Vehicle { Pos = new(x, 0, z) };
        world.Vehicles.Add(v);
        return v;
    }

    /// <summary>The victim of the first round fired with the trigger held for one frame and flown out.</summary>
    private static object? Fire(Combat c, Weapon w)
    {
        w.Trigger = true;
        for (int f = 0; f < 200 && (f == 0 || c.Rounds.Count > 0); f++) c.Frame(Dt);
        return c.Events.OfType<Impact>().FirstOrDefault()?.Victim;
    }

    [Fact]
    public void ACarOnTheLineIsHitAndARealRoundHitsItToo()
    {
        var (world, c, me, gun) = Rig(Mg());
        var them = Target(world, 0, 60);
        var line = Assert.IsType<FireLine>(c.LineOfFire(me, gun));
        Assert.True(line.Car);
        Assert.Same(them, line.Victim);
        // the muzzle at z 2.2, the target's box starting 2.1 short of its centre at 60
        Assert.Equal(60 - 2.1f - 2.2f, line.Distance, 2);
        Assert.Same(them, Fire(c, gun));
    }

    [Fact]
    public void ACarBesideTheLineIsNotAndARealRoundMissesIt()
    {
        var (world, c, me, gun) = Rig(Mg());
        Target(world, 1.2f, 60);   // its box runs from x 0.3: the line at x 0 passes it
        var line = Assert.IsType<FireLine>(c.LineOfFire(me, gun));
        Assert.False(line.Car);
        Assert.Null(line.Victim);
        Assert.Equal(Combat.Reach(gun.Def), line.Distance, 2);
        Assert.Null(Fire(c, gun));
    }

    [Fact]
    public void AGunPointedDownMeetsTheGroundWhereARoundWould()
    {
        // 1.2 m up, 5 degrees down: the ground about 13.8 m ahead
        float a = 5 * MathF.PI / 180;
        var (_, c, me, gun) = Rig(Mg(), pitch: Rows.AboutX(a).Forward.Y < 0 ? a : -a);
        var line = Assert.IsType<FireLine>(c.LineOfFire(me, gun));
        Assert.False(line.Car);
        Assert.Null(line.Victim);
        Assert.Equal(1.2f / MathF.Sin(5 * MathF.PI / 180), line.Distance, 1);
    }

    [Fact]
    public void AskingFiresNothingDrawsNoRandomNumberAndChangesNothing()
    {
        var (world, c, me, gun) = Rig(Mg());
        Target(world, 0, 60);
        uint seed = world.Rand.Seed;
        int ammo = gun.Ammo;
        for (int i = 0; i < 10; i++) c.LineOfFire(me, gun);
        Assert.Equal(seed, world.Rand.Seed);
        Assert.Empty(c.Rounds);
        Assert.Empty(c.Events);
        Assert.Equal(ammo, gun.Ammo);
    }

    [Fact]
    public void OnlyTheWeaponsWhoseRoundsFlyStraightHaveALine()
    {
        static GizmoDef K(uint kind, float g) => Mg() with { Kind = kind, Ordnance = new OrdnanceDef(kind, 150, g, 1, 0, ["", "", "", ""], ["", "", "", ""]) };
        // machine guns, cannons, the FireRite, the Ball O'Flame
        foreach (uint k in new uint[] { 1, 6, 2, 0x15 }) Assert.True(Combat.FliesStraight(K(k, 0)), $"kind {k}");
        // mortars and the howitzer (gravity), the .45, the heat and radar missiles, the flame weapons, the droppers
        foreach (var (k, g) in new (uint, float)[] { (4, 4), (5, 4), (7, 4), (0x16, 4), (0x12, 0.1f), (3, 0), (8, 0), (0x14, 0), (9, 0), (10, 0), (11, 0), (0xc, 1), (0xe, 1), (0xf, 1), (0x10, 1), (0x11, 1) })
            Assert.False(Combat.FliesStraight(K(k, g)), $"kind {k}");
        // a mortar asked anyway has no line
        var (_, c, me, gun) = Rig(K(4, 4));
        Assert.Null(c.LineOfFire(me, gun));
    }

    [Fact]
    public void TheReachIsTheRoundsFlightOverItsLife()
    {
        // the machine gun at a constant 150 m/s for 3.33 s
        Assert.Equal(150 * 3.33f, Combat.Reach(Mg()), 1);
        // the FireRite: 100 m/s towards 450 at half the rate, 2.22 s: 450·2.22 − 350·(1 − e^−1.11)/0.5
        var rocket = Mg() with { Kind = 2, Speed = 100, Life = 2.22f, Ordnance = new OrdnanceDef(2, 450, 0, 2, 0, ["", "", "", ""], ["", "", "", ""]) };
        Assert.Equal(450 * 2.22f - 350 * (1 - MathF.Exp(-1.11f)) / 0.5f, Combat.Reach(rocket), 1);
    }
}
