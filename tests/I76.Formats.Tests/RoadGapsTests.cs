using System.Numerics;
using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The modern look's fills over short gaps in the road data (RoadGaps): M06's west junction.</summary>
public class RoadGapsTests
{
    private static double Flat(double x, double z) => 10;

    /// <summary>A road along +X from x = <paramref name="x0"/>, 10 wide, a point every 5 units.</summary>
    private static RoadPiece Road(float x0, float x1)
    {
        var pts = new List<RoadEdge>();
        for (float x = x0; x <= x1 + 1e-3f; x += 5) pts.Add(new RoadEdge(new Vector3(x, 10, 5), new Vector3(x, 10, -5)));
        return new RoadPiece(1, [.. pts]);
    }

    /// <summary>A piece top: the rectangle [x0, x1] × [z0, z1] as two triangles.</summary>
    private static List<RoadRaster.FlatTri> Top(float x0, float x1, float z0 = -5, float z1 = 5)
    {
        Vector2 a = new(x0, z0), b = new(x1, z0), c = new(x1, z1), d = new(x0, z1);
        return [new(a, b, c, default, default, default, 0, a, c), new(a, c, d, default, default, default, 0, a, c)];
    }

    private static List<RoadPiece> Fill(RoadPiece road, List<RoadRaster.FlatTri> tops) =>
        RoadGaps.Fill([road], RoadRaster.Flatten([road]), tops, Flat);

    [Fact]
    public void AStartOneStepShortOfAPieceGetsAQuadReachingIntoIt()
    {
        var road = Road(10, 50);
        var fills = Fill(road, Top(-20, 5));
        var f = Assert.Single(fills);
        Assert.Equal(2, f.Points.Length);
        Assert.Equal(road.Kind, f.Kind);
        // ordered like the segment: the new edge first, then the segment's own first edge
        Assert.Equal(road.Points[0], f.Points[1]);
        Assert.InRange(f.Points[0].Left.X, 5 - RoadGaps.Overlap - 0.15f, 5 - RoadGaps.Overlap + 0.15f);
        Assert.Equal(f.Points[0].Left.X, f.Points[0].Right.X, 3);
        Assert.Equal(5f, f.Points[0].Left.Z);
        Assert.Equal(10f, f.Points[0].Left.Y);
        // the far end runs into nothing: a dead end, left alone
        Assert.DoesNotContain(fills, p => p.Points.Any(e => e.Left.X > 50));
    }

    [Fact]
    public void AnEndIsFilledForward()
    {
        var road = Road(0, 40);
        var f = Assert.Single(Fill(road, Top(45, 70)));
        Assert.Equal(road.Points[^1], f.Points[0]);
        Assert.InRange(f.Points[1].Left.X, 45 + RoadGaps.Overlap - 0.15f, 45 + RoadGaps.Overlap + 0.15f);
    }

    [Fact]
    public void JoinedLongAndPartialGapsAreLeftAlone()
    {
        Assert.Empty(Fill(Road(10, 50), Top(-20, 10)));                    // already touching
        Assert.Empty(Fill(Road(10, 50), Top(-20, 10 - RoadGaps.MaxGap - 1)));   // too far: not a slip of one step
        Assert.Empty(Fill(Road(10, 50), Top(-20, 5, 0, 5)));               // half the road runs into nothing
    }

    /// <summary>
    /// M06's west junction (igd27_1 at 2280, 48545): the diagonal segment toward 2560, 48160 starts at 2256.5, 48521.5,
    /// about 5 units short of the piece's arm, and the original shows the strip of ground (checked in the real game).
    /// The fill covers it and reaches into the piece top; the two segments meeting end to end at 2460, 49090 get one
    /// over the notch at their bend.
    /// </summary>
    [InstallFact]
    public void M06sWestJunctionAndBendAreClosed()
    {
        var vfs = TestInstall.Vfs;
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, TestInstall.Install.FindMission("m06")!);
        var tops = new List<RoadRaster.FlatTri>();
        // the piece tops as the game paints them (MissionBuilder.RoadPieceTops): the road pieces' upward faces
        foreach (var p in w.Placements)
        {
            if ((p.Object.ObjectType & 0xf0) != 0x50 || p.Definition is null || asm.Assemble(p.Definition) is not { } model) continue;
            var place = Affine.FromEngine(p.Object.Rotation, w.GroundedPosition(p));
            foreach (var part in model.Parts)
                foreach (var f in part.Mesh.Faces)
                {
                    if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                    var n = place.Rotate(part.ToModel.Rotate(new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z)));
                    if (n.LengthSquared() < 0.5f || -n.Y / n.Length() < 0.6f) continue;
                    var xz = f.Vertices.Select(v => place.Apply(part.ToModel.Apply(part.Mesh.Vertices[v.Index]))).Select(v => new Vector2(v.X, v.Z)).ToArray();
                    for (int k = 1; k + 1 < xz.Length; k++)
                        tops.Add(new(xz[0], xz[k], xz[k + 1], default, default, default, 0, Vector2.Min(Vector2.Min(xz[0], xz[k]), xz[k + 1]), Vector2.Max(Vector2.Max(xz[0], xz[k]), xz[k + 1])));
                }
        }
        Assert.NotEmpty(tops);
        var segments = Roads.Segments(w.Mission, w.BilinearHeight);
        var fills = RoadGaps.Fill(segments, RoadRaster.Flatten(Roads.Build(w.Mission, w.BilinearHeight)), tops, w.BilinearHeight);
        static Vector2 Mid(RoadEdge e) => new((e.Left.X + e.Right.X) / 2, (e.Left.Z + e.Right.Z) / 2);

        var junction = Assert.Single(fills, f => Vector2.Distance(Mid(f.Points[1]), new(2256.5f, 48521.5f)) < 0.5f);
        float len = Vector2.Distance(Mid(junction.Points[0]), Mid(junction.Points[1]));
        Assert.InRange(len, 4.5f + RoadGaps.Overlap, 5.5f + RoadGaps.Overlap);
        // along the diagonal toward the piece
        var dir = Vector2.Normalize(Mid(junction.Points[0]) - Mid(junction.Points[1]));
        Assert.True(Vector2.Dot(dir, Vector2.Normalize(new Vector2(1, 1))) > 0.99f);

        Assert.Contains(fills, f => f.Points.Any(e => Vector2.Distance(Mid(e), new(2460, 49090)) < 1f) &&
            Vector2.Distance(Mid(f.Points[0]), Mid(f.Points[1])) is > 3f and < 6f);
        // nothing near-joined is stretched: every fill is a short one
        Assert.All(fills, f => Assert.True(Vector2.Distance(Mid(f.Points[0]), Mid(f.Points[1])) <= RoadGaps.MaxGap + RoadGaps.Overlap + 0.2f));
    }
}
