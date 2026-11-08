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
/// The turret slew: dt x k x 1.7 a step, k = 0.75 + 0.25 x hp / max offline and 1 in a network game; a pitch step is
/// kept only while the barrel's sine of pitch stays under 0.95, so a target overhead drives the barrel up to about 71.8°.
/// </summary>
public class TurretSlewTests
{
    private const float Dt = 102 / 2048f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>An AI car with a wrecked turret (hp 0 of 45), its radar on a still target at <paramref name="at"/>.</summary>
    private static (Combat C, Weapon W) Rig(bool network, Vector3 at)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = new Vehicle { Flags = VehicleFlags.EngineOn };
        var target = new Vehicle { Pos = Double3.From(at), Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        var def = Armory.HandgunDef(1) with { Class = 2, Tier = 0, Kind = 1, Speed = 300, Ammo = 100 };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 3, Ident), Style = 3, MountKind = 4, BaseRot = Rows.Identity,
            Hp = 0, MaxHp = 45, Damage = 15, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        shooter.Armory = a;
        var c = new Combat(world, _ => null) { NetSession = network };
        c.RadarTarget[shooter] = target;
        return (c, w);
    }

    [Fact]
    public void AWreckedTurretTurnsAtThreeQuartersOfflineAndFullOnline()
    {
        var (off, wo) = Rig(false, new Vector3(50, 0, 0));
        off.Frame(Dt);
        Assert.Equal(Dt * 0.75f * 1.7f, wo.AimYaw, 5);
        var (on, wn) = Rig(true, new Vector3(50, 0, 0));
        on.Frame(Dt);
        Assert.Equal(Dt * 1.7f, wn.AimYaw, 5);
    }

    [Fact]
    public void ATargetOverheadDrivesTheBarrelUpToTheCap()
    {
        var (c, w) = Rig(true, new Vector3(0, 100, 10));
        for (int i = 0; i < 40; i++) c.Frame(Dt);
        float cap = MathF.Asin(0.95f);
        Assert.True(w.AimPitch < cap, $"pitch {w.AimPitch}");
        Assert.True(w.AimPitch > cap - Dt * 1.7f, $"pitch {w.AimPitch} short of the cap {cap}");
    }
}
