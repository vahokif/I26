using System.Numerics;
using I76.Formats.Bwd2;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The AI's sight test for guns and missiles: centre to centre, both raised 1.5, against the terrain and then the
/// static colliders, each part grown by 3 as the original's (structures, scenery, standing signs block; cars and
/// emplacements never). It used to run from the
/// muzzle to the target's centre against the terrain alone, so the AI shot through buildings.
/// </summary>
public class AiSightLineTests
{
    /// <summary>Flat ground with an optional ridge across z 15..25, and the given statics.</summary>
    private sealed class Ground(StaticWorld w, float ridge) : IGround, IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (z > 15 && z < 25 ? ridge : 0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public StaticWorld World { get; } = w;
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    /// <summary>An AI car at the origin facing +z with a machine gun at its origin, a still target car 40 ahead.</summary>
    private static (Combat C, StaticWorld S, Vehicle Shooter, Vehicle Target, Weapon W) Rig(float ridge = 0)
    {
        var statics = new StaticWorld();
        var world = new SimWorld(new Ground(statics, ridge), new CrtRand(1));
        var shooter = new Vehicle();
        var target = new Vehicle { Pos = new Double3(0, 0, 40) };
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        var def = Armory.HandgunDef(1) with
        {
            Class = 2, Tier = 0, Kind = 1, Speed = 300, Ammo = 100,
            Ordnance = new OrdnanceDef(1, 300, 0, 1, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("hp", 0, 1, 1, Ident), Style = 1, MountKind = 1, BaseRot = Rows.Identity,
            Hp = 45, MaxHp = 45, Damage = 15, Ammo = 100,
        };
        var a = new Armory();
        a.Weapons.Add(w); a.LoadOrder.Add(w); a.Selected = 0;
        shooter.Armory = a;
        return (new Combat(world, _ => null), statics, shooter, target, w);
    }

    [Fact]
    public void ABuildingBetweenTheCarsHoldsTheAisFire()
    {
        var (c, s, shooter, target, w) = Rig();
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        s.AddBox("shed", 2, new Vector3(-3, 0, 18), new Vector3(3, 4, 22));
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        Assert.False(c.CanSee(shooter, target));
    }

    [Fact]
    public void APartBlocksWithinThreeOfTheLineAndNotFarther()
    {
        // the original grows every part's bounds by 3 on every side: a low wall under the raised line (about 2.2) blocks,
        // and so does a shed whose side is 2.5 from the line; one 3.5 from it does not
        var (c, s, shooter, target, w) = Rig();
        var wall = s.AddBox("wall", 2, new Vector3(-3, 0, 18), new Vector3(3, 1.5f, 22));
        Assert.False(c.AiShouldFire(shooter, target, w, 1, 1));
        s.Objects.Remove(wall);
        var near = s.AddBox("shed", 2, new Vector3(2.5f, 0, 18), new Vector3(8, 4, 22));
        Assert.False(c.CanSee(shooter, target));
        s.Objects.Remove(near);
        s.AddBox("shed", 2, new Vector3(3.5f, 0, 18), new Vector3(9, 4, 22));
        Assert.True(c.CanSee(shooter, target));
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
    }

    [Fact]
    public void ASpherePartBlocksWithinItsRadiusPlusThree()
    {
        var (c, s, shooter, target, _) = Rig();
        var o = new StaticObject { Name = "plane", Type = 2, Centre = new Vector3(4, 1.5f, 20), Radius = 2, BoundRadius = 2, SphereRadius = 1.5f };
        o.Spheres.Add(new NodeSphere(new Vector3(3.5f, 1.5f, 20), 0.9f));   // 3.5 from the line: within 0.9 + 3
        s.Objects.Add(o);
        Assert.False(c.CanSee(shooter, target));
        o.Spheres[0] = new NodeSphere(new Vector3(3.5f, 1.5f, 20), 0.4f);   // 0.4 + 3: clear
        Assert.True(c.CanSee(shooter, target));
    }

    [Fact]
    public void AStandingSignBlocksAndAKnockedOneDoesNot()
    {
        var (c, s, shooter, target, _) = Rig();
        var sign = new StaticObject { Name = "ssign_1", Type = 4, Centre = new Vector3(0, 2, 20), Radius = 1, BoundRadius = 1.2f, SphereRadius = 0.9f };
        s.Objects.Add(sign);
        Assert.False(c.CanSee(shooter, target));
        sign.Knock();
        Assert.True(c.CanSee(shooter, target));
    }

    [Fact]
    public void CarsAndTheTargetStructureItselfNeverBlock()
    {
        var (c, s, shooter, target, w) = Rig();
        var other = new Vehicle { Pos = new Double3(0, 0, 20) };
        c.World.Vehicles.Add(other);
        Assert.True(c.CanSee(shooter, target));
        // (the gun's friendly scan holds for a car of its team field in the way, the target car in the shed too: that is
        // not the sight test)
        c.World.Vehicles.Remove(other); c.World.Vehicles.Remove(target);
        var shed = s.AddBox("shed", 2, new Vector3(-3, 0, 38), new Vector3(3, 4, 42));
        Assert.True(c.CanSee(shooter, shed));
        Assert.True(c.AiShouldFire(shooter, shed, shed.Centre, Vector3.UnitZ, w, 1, 1));
    }

    [Fact]
    public void TheTerrainTestRunsBetweenTheRaisedCentres()
    {
        // a 2 high ridge: under the raised line (about 2.2), above the old muzzle-to-centre line
        var (c, _, shooter, target, w) = Rig(ridge: 2f);
        Assert.True(c.AiShouldFire(shooter, target, w, 1, 1));
        var (c3, _, shooter3, target3, w3) = Rig(ridge: 3f);
        Assert.False(c3.AiShouldFire(shooter3, target3, w3, 1, 1));
    }
}
