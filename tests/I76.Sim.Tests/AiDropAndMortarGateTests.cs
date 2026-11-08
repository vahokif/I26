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
/// The AI's dropper (class 6) and mortar (class 3, subtypes other than the EZK) fire gates predict the target with the
/// target predictor: the gate's time + 0.09 s, along the turning circle when the target turns (ai.md, the dropper gate
/// and the target predictor's callers).
/// </summary>
public class AiDropAndMortarGateTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>A shooter at the origin facing +z with one weapon of the class on a fixed mount, and a target.</summary>
    private static (Combat C, Vehicle Shooter, Vehicle Target, Weapon W) Rig(uint cls, uint tier)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = new Vehicle();
        var target = new Vehicle();
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        var def = Armory.HandgunDef(1) with { Class = cls, Tier = tier, Speed = 60, Ammo = 100 };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = (int)def.Damage, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        shooter.Armory = a;
        return (new Combat(world, _ => null), shooter, target, w);
    }

    private static void Drive(Vehicle v, Vector3 at, float speed, float yawRate = 0)
    {
        v.Pos = Double3.From(at);
        v.Vel = new Vector3(0, 0, speed); v.Speed = speed;
        v.AngVel = new Vector3(0, yawRate, 0);
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(120f)]
    public void ADropperDropsOnAPursuerStraightBehindAt20ButNotAt32(float behind)
    {
        var (c, shooter, target, w) = Rig(6, 0);
        Drive(shooter, Vector3.Zero, 20);
        Drive(target, new Vector3(0, 0, -behind), 20);        // predicted 0.09 x 20 = 1.8 past the spot
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        Drive(target, new Vector3(0, 0, -behind), 27);        // 2.43 past
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        Drive(target, new Vector3(0, 0, -behind), 29);        // 2.61 past: beyond 2.5
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        Drive(target, new Vector3(0, 0, -behind), 32);        // 2.88 past
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Theory]
    [InlineData(20f, 0f, true)]
    [InlineData(20f, 1f, true)]       // 1.0 aside: 2.06 m from the spot at t + 0.09 s
    [InlineData(20f, 2f, false)]      // 2.0 aside: 2.0 on the straight line at t, 2.69 at t + 0.09 s
    [InlineData(36f, 0f, false)]      // 3.24 past the spot
    [InlineData(36f, 1f, false)]
    [InlineData(45f, 0f, false)]      // 4.05 past
    public void AStillDropperDropsOnAPursuerAsMeasured(float speed, float aside, bool drops)
    {
        // measured in the original (the 1998 build GOG ships, emulated Pentium): an Oil Slick car held still on a straight
        // road, the player's car launched at it and coasting from 5.5 s out; oil under it every second at 20 m/s straight on
        // and 1 m aside, none once it ran free at 36 or 45 m/s straight on or at 20 m/s 2 m aside (2 trials each); launched at 28 m/s it still dropped while
        // coasting at 27.5-27.9 m/s, 2.47-2.51 m from the spot at t + 0.09 s: the 2.5 m edge
        var (c, shooter, target, w) = Rig(6, 0);
        Drive(shooter, Vector3.Zero, 0);
        Drive(target, new Vector3(aside, 0, -4f * speed), speed);
        Assert.Equal(drops, c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void ADropperPredictsATurningPursuerAlongItsCircle()
    {
        var (c, shooter, target, w) = Rig(6, 0);
        Drive(shooter, Vector3.Zero, 20);
        // 20 m/s turning left at 0.2 rad/s (radius 100) from behind and to the right, its circle running through the
        // dropper: 73.95 m of chord, 3.70 s + 0.09 s, 0.7575 rad of arc. In a straight line it would pass 27 m aside.
        Drive(target, new Vector3(27.3455f, 0, -68.7119f), 20, 0.2f);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        Drive(target, new Vector3(27.3455f, 0, -68.7119f), 20, 0.04f);   // below 0.05 rad/s: the straight line
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        // the mirror, turning right: the original's predictor carries it back round its circle, so no drop
        Drive(target, new Vector3(-27.3455f, 0, -68.7119f), 20, -0.2f);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void AMortarLeadsTheTargetByItsTimePlusTheFixedLead()
    {
        // subtype 0: 0.3 s; the round lands 80 ahead of the still shooter, and a target crossing at 20 m/s is
        // predicted 0.39 s on (7.8 m), the release radius 20
        var (c, shooter, target, w) = Rig(3, 0);
        target.Pos = new Double3(-27, 0, 80); target.Vel = new Vector3(20, 0, 0); target.Speed = 20;
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));     // predicted 19.2 from the landing point
        target.Pos = new Double3(-28.5f, 0, 80);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));    // 20.7
        // subtype 2: 0.4 s (0.49 for the target, 9.8 m), radius sqrt(150) = 12.25
        (c, shooter, target, w) = Rig(3, 2);
        target.Pos = new Double3(-21.5f, 0, 80); target.Vel = new Vector3(20, 0, 0); target.Speed = 20;
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));     // 11.7
        target.Pos = new Double3(-22.5f, 0, 80);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));    // 12.7
    }
}
