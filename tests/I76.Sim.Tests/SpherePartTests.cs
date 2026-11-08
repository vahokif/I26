using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Geo;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A part whose SGEO flags select a sphere (0x4000: its SGEO radius x 1.0; 0x5000: x 0.75) collides as that sphere, as
/// the original's scenery tree does: the stock ones are ajplane1's fuselages (the Nitro Pack's P18) and the CTF flag
/// bases, which the car's collide slot ignores (part type 0x5b) and which stay without one.
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

    [Fact]
    public void AFlagBaseGetsNone()
    {
        var o = Placed(new StaticWorld(), 0x4000, partType: 0x5b);
        Assert.Empty(o.Spheres);
    }

    [Fact]
    public void ACarBouncesOffASpherePart()
    {
        var w = new StaticWorld();
        Placed(w, 0x4000);
        var sim = new SimWorld(new FlatWorld(w), new CrtRand(1));
        var v = new Vehicle();
        foreach (var (wh, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            wh.Grip = 0.5f; wh.Rolling = 1f; wh.Offset = new Vector3(x, 0.33f, z);
            wh.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f; v.P.Mass = 1500;
        v.BoxX = [-0.9f, -0.5f, 0.5f, 0.9f]; v.BoxY = [0, 0.3f, 1f, 1.4f]; v.BoxZ = [-2.1f, -1f, 1f, 2.1f];
        v.Vel = new Vector3(0, 0, 15); v.Speed = 15;
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
