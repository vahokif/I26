using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Geo;
using I76.Formats.Mission;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A flat piece (an intersection's or a road's: one type-2 node) given the Sign class id. Its node takes the sign's
/// default collider class, a sphere part (0.75 of its SGEO radius), and a car meets a sphere part as the original does:
/// the part's centre is a point, and the wall is the car's own box grown by that radius on every side, the point
/// entering it within the frame through one of its faces. Measured in the original (T13's idisect1, 10 x 20 m, radius
/// 11.18, placed as a Sign; a run-up at 10 u/s): the car met it with its origin 10.67 m from the piece's centre (its box's
/// front 2.29 + 8.385) whether it came along the piece's 20 m side or its 10 m side, so 3.4 m short of the near edge one
/// way and 1.5 m inside it the other; the record's point was the box's front face normal (0, 0, 1); the car was thrown
/// back at 4 u/s and the node took the ram (HP 10000 to 9997). Put down on the piece's centre, the car drove off it and
/// out the far side with no contact at all.
/// </summary>
public class SignClassPieceTests
{
    private sealed class FlatWorld(StaticWorld w) : IGround, IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public StaticWorld World => w;
    }

    private static readonly float[] Level = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>idisect1's shape: one flat 10 x 20 m node (type 2), its SGEO sphere 11.18 round its middle, the node 5, 10 off the placement's origin.</summary>
    private static StaticObject Piece(StaticWorld w, Vector3 at)
    {
        var m = new AssembledModel { Definition = "piece.sdf", Kind = "sdf" };
        m.Parts.Add(new ModelPart("ij1_a211", new GeoMesh { Vertices = [new(-5, 0, -10), new(5, 0, -10), new(5, 0, 10), new(-5, 0, 10)] },
            new Affine([1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3(5, 0, 10))));
        var o = StaticWorld.Build("piece", 4, Level, at, m, nodeType: _ => 2, nodeIndex: _ => 0,
            nodeSphere: _ => (Vector3.Zero, 11.18f), signPart: _ => false)!;
        o.Nodes.Add(new StructureNode { Name = "ij1_a211", Type = 2, Hp = 10000, MaxHp = 10000, Walk = 1 });
        w.Objects.Add(o);
        return o;
    }

    /// <summary>T13's player car's box as the original had it: x ±0.99, y 0.26..1.36, z −2.37..2.29.</summary>
    private static Vehicle Car(Vector3 at, float yawDeg, float speed)
    {
        var v = new Vehicle();
        foreach (var (wh, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            wh.Grip = 0.5f; wh.Rolling = 1f; wh.Offset = new Vector3(x, 0.33f, z);
            wh.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f; v.P.Mass = 1500;
        v.BoxX = [-0.99f, -0.5f, 0.5f, 0.99f]; v.BoxY = [0, 0.26f, 1f, 1.36f]; v.BoxZ = [-2.37f, -1f, 1f, 2.29f];
        float a = yawDeg * MathF.PI / 180;
        var fwd = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        v.Pos = new Double3(at.X, at.Y, at.Z);
        v.Vel = fwd * speed; v.Speed = MathF.Abs(speed);
        return v;
    }

    private const float Grown = 2.29f + 0.75f * 11.18f;   // the car's front face grown by the piece's scaled radius

    private static (SimWorld Sim, StaticObject Piece, Vector3 Centre) Rig()
    {
        var w = new StaticWorld();
        var o = Piece(w, new Vector3(0, 0, 100));
        var sim = new SimWorld(new FlatWorld(w), new CrtRand(1));
        return (sim, o, Assert.Single(o.Spheres).Centre);
    }

    /// <summary>The record a car at that pose, at that speed, gets from the frame's scenery pass (dt 0.05).</summary>
    private static Contact Met(Vector3 carAt, float yawDeg, float speed = 10)
    {
        var (sim, _, _) = Rig();
        var v = Car(carAt, yawDeg, speed);
        sim.Vehicles.Add(v); sim.Player = v;
        sim.Step.StaticContacts(v, 0.05f);
        return v.Rec;
    }

    [Fact]
    public void TheNodeIsASpherePartOfThreeQuartersItsRadius()
    {
        var (_, o, c) = Rig();
        Assert.False(o.SignPlacement);
        var s = Assert.Single(o.Spheres);
        Assert.Equal(new Vector3(5, 0, 110), c);
        Assert.Equal(0.75f * 11.18f, s.Radius, 4);
        Assert.Equal(11.18f, s.Reach, 4);
    }

    [Fact]
    public void TheCarMeetsTheCentreWithItsGrownBoxFace()
    {
        // the grown front face 0.2 m short of the centre, closing at 10 u/s: met 0.02 s into the frame
        var rec = Met(new Vector3(5, 0, 110 - Grown - 0.2f), 0);
        Assert.True(rec.Hit);
        Assert.Equal(0.02f, rec.T, 3);
        Assert.Equal(Vector3.UnitZ, rec.C);                       // the entered face's normal, in the car's frame
        Assert.Equal(-Vector3.UnitZ, rec.N);                      // turned to the world and reversed: straight back
        Assert.Equal(new Vector3(0, 0, -10), rec.RelVel);
        // too far for this frame: nothing
        Assert.False(Met(new Vector3(5, 0, 110 - Grown - 0.6f), 0).Hit);
    }

    [Fact]
    public void TheWallIsTheCarsOutlineNotThePieces()
    {
        // the piece reaches 10 m along z and 5 m along x from its centre; the car meets it at the same distance both ways
        var alongZ = Met(new Vector3(5, 0, 110 - Grown - 0.2f), 0);
        var alongX = Met(new Vector3(5 - Grown - 0.2f, 0, 110), 90);
        Assert.True(alongZ.Hit && alongX.Hit);
        Assert.Equal(alongZ.T, alongX.T, 3);
        Assert.Equal(Vector3.UnitZ, alongX.C);
        Assert.Equal(-1, alongX.N.X, 4);
        // and as wide as the car's box grown (9.375 to either side), wherever the piece is; its corners are cut off by the
        // pre-test, a sphere of the car's radius and the part's whole radius about the part's origin
        Assert.True(Met(new Vector3(5 - 7f, 0, 110 - Grown - 0.2f), 0).Hit);
        Assert.False(Met(new Vector3(5 - 9.3f, 0, 110 - Grown - 0.2f), 0).Hit);
        Assert.False(Met(new Vector3(5 - 9.5f, 0, 110 - 1f), 0).Hit);        // beside it, past its face
        // and below it: the box grows downwards too
        Assert.True(Met(new Vector3(5, -7.5f, 110 - Grown - 0.2f), 0).Hit);
    }

    [Fact]
    public void ACarInsideTheBoxMeetsNothing()
    {
        Assert.False(Met(new Vector3(5, 0, 110), 0).Hit);
        Assert.False(Met(new Vector3(5, 0, 110 - Grown + 0.5f), 0).Hit);     // a hair inside its face
        Assert.False(Met(new Vector3(5, 0, 110 - Grown + 0.5f), 0, speed: -10).Hit);   // backing out
    }

    /// <summary>
    /// The rub: beside the wall, a car that turns swings its grown box round the point without the point ever entering a
    /// face along the car's velocity; then it is inside and meets nothing more.
    /// </summary>
    [Fact]
    public void TurningAgainstTheWallGetsTheCarInside()
    {
        var (sim, _, c) = Rig();
        // the centre 0.3 m off the grown box's left face, level with the car's front: driving along, it slides by
        var v = Car(new Vector3(c.X + 0.99f + 0.75f * 11.18f + 0.3f, 0, c.Z - 2.29f), 0, 2);
        sim.Vehicles.Add(v); sim.Player = v;
        sim.Step.StaticContacts(v, 0.05f);
        Assert.False(v.Rec.Hit);
        // turned 10 degrees towards it: the box swung round over it
        v.Rot = Car(default, -10, 0).Rot;
        v.Vel = v.Rot.Forward * 2;
        sim.Step.StaticContacts(v, 0.05f);
        Assert.False(v.Rec.Hit);
        Assert.True(MathF.Abs(v.Rot.ToLocal(c - v.Pos.ToVector3()).X) < 0.99f + 0.75f * 11.18f);   // inside
    }

    [Fact]
    public void ACarDrivenIntoItIsThrownBack()
    {
        var (sim, o, c) = Rig();
        var v = Car(new Vector3(c.X, 0, c.Z - 15), 0, 10);
        sim.Vehicles.Add(v); sim.Player = v;
        bool hit = false;
        float front = float.MinValue, back = 0;
        for (int i = 0; i < 40; i++)
        {
            sim.Step.ObjectHits.Clear();
            sim.Frame(0.05f);
            hit |= sim.Step.ObjectHits.Exists(h => h.Name == "piece");
            front = MathF.Max(front, (float)v.Pos.Z + 2.29f);
            back = MathF.Min(back, v.Vel.Z);
        }
        Assert.True(hit);
        Assert.True(back < -3, $"not thrown back: {back}");
        // stopped about 8.4 m short of the centre (within the frame it would have crossed), before the piece's near
        // edge, 5 m from it along z
        Assert.InRange(c.Z - front, 8.38f, 8.9f);
        Assert.False(o.Knocked);
    }

    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The measured run-ups, on T13 with its own car: idisect1 placed as a Sign, turned as T13 turns its own.</summary>
    [Fact]
    public void T13sIntersectionPiecePlacedAsASignStopsTheCarAtItsGrownBox()
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("t13.msn", ("idisect1", 4u, 5410, 46205, 90));
        var o = ClassIdTests.At(setup, "idisect1", 5420, 46200);
        Assert.Equal(4u, o.Type);
        Assert.False(o.SignPlacement);
        var c = Assert.Single(o.Spheres).Centre;
        Assert.Equal(5420, c.X, 1);
        Assert.Equal(46200, c.Z, 1);
        var player = setup.World.Player!;
        float grown = player.BoxZ[3] + 0.75f * 11.18f;
        var (hit, reached, _) = ClassIdTests.Ram(setup, s, new Vector2(c.X, c.Z - 30), new Vector2(0, 1), speed: 10);
        Assert.Equal("idisect1", hit);
        Assert.InRange(30 - reached, grown - 0.05f, grown + 0.6f);   // met from where it was at the start of that tick
        var (setup2, s2) = ClassIdTests.Build("t13.msn", ("idisect1", 4u, 5410, 46205, 90));
        var (hit2, reached2, _) = ClassIdTests.Ram(setup2, s2, new Vector2(c.X - 30, c.Z), new Vector2(1, 0), speed: 10);
        Assert.Equal("idisect1", hit2);
        Assert.InRange(30 - reached2, grown - 0.05f, grown + 0.6f);
        // from its centre: out the far side, nothing met
        var (setup3, s3) = ClassIdTests.Build("t13.msn", ("idisect1", 4u, 5410, 46205, 90));
        var (hit3, reached3, _) = ClassIdTests.Ram(setup3, s3, new Vector2(c.X, c.Z), new Vector2(0, 1), speed: 10);
        Assert.Null(hit3);
        Assert.True(reached3 > 15, $"the car got {reached3:0.0} m");
    }
}
