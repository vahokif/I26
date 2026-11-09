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
/// The turret slew: dt x k x 1.7 a step, k = 0.75 + 0.25 x hp / max offline and 1 in a network game (the 1998 build's, GOG's and
/// the Nitro Pack's); dt x h x 1.5 with h = hp / max offline and 1 in a network game in the 1997 CD build's (Turrets); a pitch step
/// is kept only while the barrel's sine of pitch stays under 0.95, so a target overhead drives the barrel up to about 71.8°.
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
    private static (Combat C, Weapon W) Rig(bool network, Vector3 at, int hp = 0, bool cd1997 = false)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = new Vehicle { Flags = VehicleFlags.EngineOn };
        var target = new Vehicle { Pos = Double3.From(at), Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        var def = Armory.HandgunDef(1) with { Class = 2, Tier = 0, Kind = 1, Speed = 300, Ammo = 100 };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 3, Ident), Style = 3, MountKind = 4, BaseRot = Rows.Identity,
            Hp = hp, MaxHp = 45, Damage = 15, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        shooter.Armory = a;
        var c = new Combat(world, _ => null) { NetSession = network, TurretSlew1997 = cd1997 };
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

    /// <summary>One frame's turn at a target off to the side, by the build's rule and the turret's health (of 45), offline.</summary>
    [Theory]
    [InlineData(false, 45, 1.7f)]
    [InlineData(false, 30, (0.75f + 0.25f * 30 / 45) * 1.7f)]
    [InlineData(false, 0, 0.75f * 1.7f)]
    [InlineData(true, 45, 1.5f)]
    [InlineData(true, 30, 30 / 45f * 1.5f)]
    [InlineData(true, 0, 0f)]   // the 1997 build has no floor: a wrecked turret stands
    public void EachBuildTurnsAtItsOwnRateByTheTurretsHealth(bool cd1997, int hp, float rate)
    {
        var (c, w) = Rig(false, new Vector3(50, 0, 0), hp, cd1997);
        c.Frame(Dt);
        Assert.Equal(Dt * rate, w.AimYaw, 5);
    }

    /// <summary>A network game takes 1 for the health term in both builds: a wrecked turret turns at the build's full rate.</summary>
    [Theory]
    [InlineData(false, 1.7f)]
    [InlineData(true, 1.5f)]
    public void OnlineEveryTurretTurnsAtTheBuildsFullRate(bool cd1997, float rate)
    {
        var (c, w) = Rig(true, new Vector3(50, 0, 0), 0, cd1997);
        c.Frame(Dt);
        Assert.Equal(Dt * rate, w.AimYaw, 5);
    }

    /// <summary>A helicopter's turrets turn four times as fast in both builds: 6.8 and 6.0.</summary>
    [Fact]
    public void AHelicoptersTurretsTurnFourTimesAsFast()
    {
        var (c, w) = Rig(true, new Vector3(50, 0, 0), 45);
        Assert.Equal(Dt * 6.8f, c.TurretSlew(w, heli: true, Dt), 5);
        c.TurretSlew1997 = true;
        Assert.Equal(Dt * 6f, c.TurretSlew(w, heli: true, Dt), 5);
    }

    /// <summary>
    /// The police tank's cannon (vstank2.vcf, gtptank: tier 15) mounts as a turret by the 1997 build's tier rule in either Turrets
    /// setting, and turns after its target in both: at full health 1.7 rad/s in the 1998 build's, 1.5 in the 1997 build's.
    /// </summary>
    [Theory]
    [InlineData(false, 1.7f)]
    [InlineData(true, 1.5f)]
    public void ThePoliceTanksCannonTurnsInBothBuilds(bool cd1997, float rate)
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var vfs = new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(dir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var tank = VehicleFactory.Create(vfs, "vstank2.vcf");
        tank.Flags |= VehicleFlags.EngineOn;
        var target = new Vehicle { Pos = new Double3(50, 0, 0), Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(tank); world.Vehicles.Add(target);
        var cannon = Assert.Single(tank.Armory!.Weapons, w => w.Def.Name == "Police Tank Cann");
        Assert.Equal(3u, cannon.Style);
        var c = new Combat(world, _ => null) { TurretSlew1997 = cd1997 };
        c.RadarTarget[tank] = target;
        float before = cannon.AimYaw;
        c.Frame(Dt);
        Assert.Equal(Dt * rate, MathF.Abs(cannon.AimYaw - before), 4);
    }
}
