using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Geo;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A part whose SGEO flags select a sphere (0x4000: its SGEO radius x 1.0; 0x5000: x 0.75) collides as that sphere, as
/// the original's scenery tree does: the stock ones are ajplane1's fuselages (the Nitro Pack's P18) and the CTF flag
/// bases, which the car's collide slot ignores (part type 0x5b, like the flag's pole, 0x5a) and which stay without one.
/// </summary>
public class SpherePartTests
{
    private sealed class FlatWorld(StaticWorld w) : IGround, IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public StaticWorld World => w;
    }

    private static readonly float[] Level = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>One small part (a 0.2 m triangle) whose SGEO sphere is 3 m round (0, 1, 0).</summary>
    private static AssembledModel Ball()
    {
        var m = new AssembledModel { Definition = "ball.sdf", Kind = "sdf" };
        m.Parts.Add(new ModelPart("ball", new GeoMesh { Vertices = [new(0, 1, 0), new(0.2f, 1, 0), new(0, 1.2f, 0)] }, Affine.Identity));
        return m;
    }

    private static StaticObject Placed(StaticWorld w, uint cls, uint partType = 2)
    {
        var o = StaticWorld.Build("ball", 2, Level, new Vector3(0, 0, 40), Ball(), nodeClass: _ => cls, nodeType: _ => partType,
            nodeSphere: _ => (new Vector3(0, 1, 0), 3f))!;
        w.Objects.Add(o);
        return o;
    }

    [Theory]
    [InlineData(0x4000u, 3f)]
    [InlineData(0x5000u, 2.25f)]
    public void ASpherePartGetsItsSgeoSphere(uint cls, float radius)
    {
        var o = Placed(new StaticWorld(), cls);
        var s = Assert.Single(o.Spheres);
        Assert.Equal(new Vector3(0, 1, 40), s.Centre);
        Assert.Equal(radius, s.Radius, 4);
        Assert.Empty(o.Boxes);
        Assert.True(o.Solid);
        Assert.True(o.Radius >= radius);
    }

    /// <summary>
    /// A CTF flag's parts get no collider whatever their class: the car's collide slot ignores node types 0x5a (the pole
    /// and pennant, a box in a1flagN) and 0x5b (the base, a sphere), so a car drives through the flag.
    /// </summary>
    [Theory]
    [InlineData(0x4000u, 0x5bu)]
    [InlineData(0x2000u, 0x5au)]
    [InlineData(0x3000u, 0x5au)]
    [InlineData(0x2000u, 0x5bu)]
    public void AFlagsPartsGetNone(uint cls, uint partType)
    {
        var o = Placed(new StaticWorld(), cls, partType);
        Assert.Empty(o.Spheres);
        Assert.Empty(o.Boxes);
        Assert.Empty(o.Polys);
        Assert.Empty(o.MeshBounds);
    }

    private static Vehicle Car()
    {
        var v = new Vehicle();
        foreach (var (wh, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            wh.Grip = 0.5f; wh.Rolling = 1f; wh.Offset = new Vector3(x, 0.33f, z);
            wh.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f; v.P.Mass = 1500;
        v.BoxX = [-0.9f, -0.5f, 0.5f, 0.9f]; v.BoxY = [0, 0.3f, 1f, 1.4f]; v.BoxZ = [-2.1f, -1f, 1f, 2.1f];
        v.Vel = new Vector3(0, 0, 15); v.Speed = 15;
        return v;
    }

    [Fact]
    public void ACarBouncesOffASpherePart()
    {
        var w = new StaticWorld();
        Placed(w, 0x4000);
        var sim = new SimWorld(new FlatWorld(w), new CrtRand(1));
        var v = Car();
        sim.Vehicles.Add(v); sim.Player = v;
        bool hit = false;
        for (int i = 0; i < 80; i++)
        {
            sim.Step.ObjectHits.Clear();
            sim.Frame(102f / 2048f);
            hit |= sim.Step.ObjectHits.Exists(h => h.Name == "ball");
        }
        Assert.True(hit, "the car met nothing");
        // its front never got inside the sphere (centre z 40, radius 3)
        Assert.True(v.Pos.Z + 2.1 < 37.5, $"the car went into the sphere: front at {v.Pos.Z + 2.1}");
    }

    /// <summary>A recorder of the cars' scenery contacts (the ram damage's way in).</summary>
    private sealed class Contacts : IStepDamage
    {
        public readonly List<string> Met = [];
        public void Timer(Vehicle v, float dt) { }
        public void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal) { }
        public void Landing(Vehicle v, Vector3 impactVel, Vector3 n) { }
        public void FallKill(Vehicle v) { }
        public void Rough(Vehicle v, float dt) { }
        public void StaticContact(Vehicle v, StaticObject o, Contact rec) => Met.Add(o.Name);
    }

    /// <summary>
    /// a1flagN's shape: the base disc (type 0x5b, a sphere, 4 m round) and the pole and pennant (type 0x5a, a box: a plate
    /// 1.8 m wide and 3.4 m high across the car's way). A car driven through it at 15 m/s neither bounces nor meets it (no
    /// contact, so no ram damage), and the CTF game's stub sphere still gives it the flag.
    /// </summary>
    [Fact]
    public void ACarDrivesThroughAFlagAndTakesIt()
    {
        var m = new AssembledModel { Definition = "a1flag2.sdf", Kind = "sdf" };
        m.Parts.Add(new ModelPart("A11_BAS1", new GeoMesh { Vertices = [new(-4, 0, -3.5f), new(4, 0, -3.5f), new(4, 0.1f, 3.5f), new(-4, 0.1f, 3.5f)] }, Affine.Identity));
        m.Parts.Add(new ModelPart("A11_FLG1", new GeoMesh { Vertices = [new(-0.9f, 0.1f, 0), new(0.9f, 0.1f, 0), new(0.9f, 3.5f, 0), new(-0.9f, 3.5f, 0)] }, Affine.Identity));
        var w = new StaticWorld();
        var o = StaticWorld.Build("a1flag2", 2, Level, new Vector3(0, 0, 40), m,
            nodeClass: n => n == "A11_BAS1" ? 0x4000u : 0x2000u, nodeType: n => n == "A11_BAS1" ? 0x5bu : 0x5au,
            nodeSphere: _ => (Vector3.Zero, 4f))!;
        w.Objects.Add(o);
        var damage = new Contacts();
        var sim = new SimWorld(new FlatWorld(w), new CrtRand(1));
        sim.Step.Damage = damage;
        var v = Car();
        sim.Vehicles.Add(v); sim.Player = v;
        var ctf = new CtfGame();
        ctf.AddFlag(2, new Double3(0, 0, 40));
        var red = new MeleePlayer { Car = v, Entity = new Entity("plyr", [], 1, 0x10u | 1u << 16), CarFile = "x.vcf", Name = "red" };
        var green = new MeleePlayer { Car = new Vehicle { Pos = new Double3(500, 0, 500) }, Entity = new Entity("plyr", [], 1, 0x10u | 2u << 16), CarFile = "x.vcf", Name = "green" };
        float slowest = float.MaxValue;
        for (int i = 0; i < 80; i++)
        {
            sim.Step.ObjectHits.Clear();
            sim.Frame(102f / 2048f);
            ctf.Update([red, green], _ => true, _ => 2f);
            Assert.DoesNotContain(sim.Step.ObjectHits, h => h.Name == "a1flag2");
            if (System.Math.Abs(v.Pos.Z - 40) < 6) slowest = MathF.Min(slowest, v.Vel.Z);
        }
        Assert.Empty(damage.Met);
        Assert.True(v.Pos.Z > 46, $"the car stopped at the flag: at z {v.Pos.Z}");
        Assert.True(slowest > 10, $"the car bounced or was stopped at the flag: {slowest} m/s");
        Assert.Equal(CtfFlag.Carried, ctf.Flags[0].State);
        Assert.Same(red, ctf.Flags[0].Carrier);
    }

    [Fact]
    public void ARayMeetsTheSphereAtItsSurface()
    {
        var o = Placed(new StaticWorld(), 0x4000);
        var h = StaticWorld.RaySpheres(o.Spheres, new Vector3(0, 1, 30), new Vector3(0, 0, 20), 1f);
        Assert.NotNull(h);
        Assert.Equal(7f / 20f, h!.Value.T, 4);
        Assert.Equal(-Vector3.UnitZ, h.Value.N);
    }
}
