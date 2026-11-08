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
/// The AI gun fire gate's friendly scan (ai.md, friendly scan): cars sharing the shooter's team field, team 0 included,
/// within 100 m on the ground of the shooter; the round's closest approach, t clamped to 0 and only below 4 s; a miss
/// under 8 holds fire, under 2 when the friend is within 8 of the shooter. No random draw.
/// </summary>
public class AiFriendlyScanTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>A shooter at the origin facing +z with a 120 m/s gun, a still target 50 ahead, and a friend.</summary>
    private static (Combat C, SimWorld World, Vehicle Shooter, Vehicle Target, Vehicle Friend, Weapon W) Rig()
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = new Vehicle();
        var target = new Vehicle { Pos = new Double3(0, 0, 50) };
        var friend = new Vehicle { Pos = new Double3(0, 0, 25) };
        world.Vehicles.Add(shooter); world.Vehicles.Add(target); world.Vehicles.Add(friend);
        var def = Armory.HandgunDef(1);
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = (int)def.Damage, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        shooter.Armory = a;
        return (new Combat(world, _ => null), world, shooter, target, friend, w);
    }

    [Fact]
    public void ATeamZeroShooterHoldsForATeamZeroCarOnTheLine()
    {
        var (c, _, shooter, target, friend, w) = Rig();
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));    // everyone team 0: the friend 25 ahead holds it
        c.Team = v => v == friend ? 2 : 0;
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));     // another team: no hold
        c.Team = v => v == shooter || v == friend ? 1 : 2;
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void OnlyAClosestApproachWithinFourSecondsCounts()
    {
        var (c, _, shooter, target, friend, w) = Rig();
        friend.Pos = new Double3(0, 0, 60);
        friend.Vel = new Vector3(0, 0, 110);                       // the round closes at 10 m/s: on it after 6 s
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        friend.Vel = new Vector3(0, 0, 90);                        // 30 m/s: after 2 s
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void AFriendNearTheShooterHoldsOnlyWithinTwo()
    {
        var (c, _, shooter, target, friend, w) = Rig();
        friend.Pos = new Double3(3, 0, 2);                         // 3.6 from the shooter, the round passes 3 from it
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        friend.Pos = new Double3(1.5f, 0, 3);
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        friend.Pos = new Double3(0, 0, -1.5f);                     // just behind the muzzle: t clamped to 0, 1.5 away
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        friend.Pos = new Double3(101, 0, 0);                       // beyond 100 on the ground
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void TheScanDrawsNoRandomNumber()
    {
        var (c1, w1, s1, t1, f1, g1) = Rig();
        f1.Pos = new Double3(50, 0, 0);                            // a friend the round passes far from
        Assert.True(c1.AiShouldFire(s1, t1, g1, 1, 1));
        var (c2, w2, s2, t2, f2, g2) = Rig();
        w2.Vehicles.Remove(f2);
        Assert.True(c2.AiShouldFire(s2, t2, g2, 1, 1));
        Assert.Equal(w2.Rand.Next(), w1.Rand.Next());
    }
}
