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
/// A class-4 breakable collides by one sphere per part (0.75 × the part's SGEO radius about its SGEO centre), as the
/// original's scenery tree builds it, not by one sphere round the whole object: a car between two posts of a
/// two-post sign touches neither.
/// </summary>
public class BreakableSphereTests
{
    private sealed class FlatWorld(StaticWorld w) : IGround, IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public StaticWorld World => w;
    }

    private static readonly float[] Level = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>Two posts 8 m apart (x −4 and +4), each a part whose SGEO sphere is 1 m round its foot + 1 m.</summary>
    private static StaticObject TwoPosts(StaticWorld w, float z)
    {
        var m = new AssembledModel { Definition = "sign.sdf", Kind = "sdf" };
        foreach (var (name, x) in new[] { ("posta", -4f), ("postb", 4f) })
            m.Parts.Add(new ModelPart(name, new GeoMesh { Vertices = [new(-0.1f, 0, 0), new(0.1f, 0, 0), new(0, 2.5f, 0)] },
                new Affine([1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3(x, 0, 0))));
        var o = StaticWorld.Build("sign", 4, Level, new Vector3(0, 0, z), m, nodeSphere: _ => (new Vector3(0, 1, 0), 1f))!;
        w.Objects.Add(o);
        return o;
    }

    [Fact]
    public void EachPartGetsItsOwnSphere()
    {
        var o = TwoPosts(new StaticWorld(), 20);
        Assert.Equal(2, o.Spheres.Count);
        Assert.Equal(new Vector3(-4, 1, 20), o.Spheres[0].Centre);
        Assert.Equal(new Vector3(4, 1, 20), o.Spheres[1].Centre);
        Assert.All(o.Spheres, s => Assert.Equal(0.75f, s.Radius, 5));
        // the whole object's sphere would reach the gap between them
        Assert.True(o.SphereRadius > 3f);
    }

    private static bool Knocks(float carX)
    {
        var w = new StaticWorld();
        TwoPosts(w, 20);
        var sim = new SimWorld(new FlatWorld(w), new CrtRand(1));
        var v = new Vehicle();
        foreach (var (wh, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            wh.Grip = 0.5f; wh.Rolling = 1f; wh.Offset = new Vector3(x, 0.33f, z);
            wh.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f;
        v.BoxX = [-0.9f, -0.5f, 0.5f, 0.9f]; v.BoxY = [0, 0.3f, 1f, 1.4f]; v.BoxZ = [-2.1f, -1f, 1f, 2.1f];
        v.Pos = new Double3(carX, 0, 0);
        v.Vel = new Vector3(0, 0, 20); v.Speed = 20;
        sim.Vehicles.Add(v); sim.Player = v;
        for (int i = 0; i < 40; i++)
        {
            sim.Step.ObjectHits.Clear();
            sim.Frame(102f / 2048f);
            if (sim.Step.ObjectHits.Exists(h => h.Name == "sign")) return true;
        }
        return false;
    }

    [Fact]
    public void ACarBetweenThePostsTouchesNeither() => Assert.False(Knocks(0));

    [Fact]
    public void ACarAtAPostKnocksTheSign() => Assert.True(Knocks(4));
}
