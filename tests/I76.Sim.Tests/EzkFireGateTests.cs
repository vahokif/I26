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
/// The AI's fire gate for the EZK mortar (class 3 subtype 3): a skill gate of two rand()%1000 against 3500 x skill, then
/// the time the target's line passes a line at a fixed 500 along the shooter's forward row (flipped only when the muzzle
/// points backwards), kept
/// within 0..0.2 s, the target then within 20 on the ground plane. It used to fall through and never fire.
/// </summary>
public class EzkFireGateTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>A shooter at the origin facing +z with an EZK on a fixed mount, and a still target.</summary>
    private static (Combat C, Vehicle Shooter, Vehicle Target, Weapon W) Rig(uint tier = 3, bool rear = false)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = new Vehicle();
        var target = new Vehicle();
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        var def = Armory.HandgunDef(1) with { Class = 3, Tier = tier, Kind = 7, Speed = 60, Ammo = 100 };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = rear ? Rows.AboutY(MathF.PI) : Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = (int)def.Damage, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        shooter.Armory = a;
        return (new Combat(world, _ => null), shooter, target, w);
    }

    [Fact]
    public void TheAiFiresAnEzkAtACarOnItsMuzzleLine()
    {
        var (c, shooter, target, w) = Rig();
        target.Pos = new Double3(0, 0, 50);                       // reached in 0.1 s at 500
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        target.Pos = new Double3(0, 0, -50);                      // behind a forward-mounted EZK: the line never turns to it
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        target.Pos = new Double3(15, 0, 50);                      // 15 to the side
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void ARearMountedEzkFiresBehindAndNotAhead()
    {
        // the shooter's forward row flipped because the muzzle points backwards
        var (c, shooter, target, w) = Rig(rear: true);
        target.Pos = new Double3(0, 0, -50);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        target.Pos = new Double3(0, 0, 50);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void TheLineIgnoresTheMuzzlesElevation()
    {
        // the EZK is laid up to 45 degrees; the line still runs level along the car's forward row
        var (c, shooter, target, w) = Rig();
        w.GunPitch = 0.7f;
        target.Pos = new Double3(0, 0, 50);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void ItHoldsBeyondTheTimeWindowOrTwentyAside()
    {
        var (c, shooter, target, w) = Rig();
        target.Pos = new Double3(0, 0, 150);                      // 0.3 s at 500
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        target.Pos = new Double3(25, 0, 50);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        // a car crossing ahead at 300: 30 aside now and on the line 0.1 s later, but the predictor's 0.09 s on top
        // carries it 27 past
        target.Pos = new Double3(-30, 0, 50);
        target.Vel = new Vector3(300, 0, 0);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        // slower, 10 aside at 60: 1.5 past at 0.19 s
        target.Pos = new Double3(-10, 0, 50);
        target.Vel = new Vector3(60, 0, 0);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void TheTargetIsPlacedByThePredictorWithItsExtraNinetyMilliseconds()
    {
        var (c, shooter, target, w) = Rig();
        // crossing at 300 from 60 aside: the lines meet at t = 0.1265 s. Along its line at t the car is 22.1 aside and
        // 13.2 short (24 m off); the predictor's t + 0.09 s puts it 4.9 aside (14 m off).
        target.Pos = new Double3(-60, 0, 50);
        target.Vel = new Vector3(300, 0, 0);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        // the same car turning hard (1 rad/s, its centre on its right) is carried back round its circle and is not fired at
        target.AngVel = new Vector3(0, -1, 0);
        target.Gear = 1;
        target.Rot = new Rows(new Vector3(0, 0, -1), Vector3.UnitY, new Vector3(1, 0, 0));
        target.Speed = 300;
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void TheSkillGateIsTwoDrawsAgainst3500TimesTheSkill()
    {
        var (c, shooter, target, w) = Rig();
        target.Pos = new Double3(0, 0, 50);
        // skill 0.1: the two draws must sum to 350 at most, about 6 % of the time
        int fired = Enumerable.Range(0, 1000).Count(_ => c.AiShouldFire(shooter, target, w, 0.1f, 1));
        Assert.InRange(fired, 30, 100);
        // skill 0.6: 2100 is above the largest sum, 1998
        Assert.All(Enumerable.Range(0, 100), _ => Assert.True(c.AiShouldFire(shooter, target, w, 0.6f, 1)));
    }

    [Fact]
    public void OtherMortarsKeepTheirLandingTest()
    {
        var (c, shooter, target, w) = Rig(tier: 1);
        target.Pos = new Double3(0, 0, 50);                       // the line test would fire; the landing point is 80 out
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        target.Pos = new Double3(0, 0, 80);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
    }
}
