using System.Numerics;
using I76.Editor;
using I76.Editor.Intersections;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>
/// Wide roads (<see cref="RoadBuilder.WideWidth"/>, the original builder's "4 Lanes": the edges 20 apart, the kinds and textures of any road):
/// written to the stock conventions at their width, kept at it by every edit, the ground and the bend limit scaled to it, no intersection
/// pieces, and read back by the game's road builder as they were written.
/// </summary>
public class WideRoadTests
{
    private const float W = RoadBuilder.WideWidth;

    private static List<Vector2> Arc(float r = 120, int n = 24) => Enumerable.Range(0, n).Select(i => new Vector2(MathF.Sin(i * 0.05f), MathF.Cos(i * 0.05f)) * r + new Vector2(1000, 1000)).ToList();

    private static TerrainDocument Ground(Func<int, int, ushort> height)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;   // a 4-tile strip
        const int T = TerrainTiles.Tile;
        var ter = new byte[4 * T * T * 2];
        for (int t = 0; t < 4; t++)
            for (int z = 0; z < T; z++)
                for (int x = 0; x < T; x++)
                {
                    ushort h = height(t * T + x, z);
                    int i = (t * T * T + z * T + x) * 2;
                    ter[i] = (byte)h; ter[i + 1] = (byte)((h >> 8) & 0x0f);
                }
        return new TerrainDocument(new TerrainTiles(ter, 4, grid), "wide");
    }

    private static TerrainDocument Flat() => Ground((_, _) => 200);
    private static TerrainDocument Hilly() => Ground((gx, z) => (ushort)(600 + 90 * Math.Sin(gx * 0.07) + 60 * Math.Cos(z * 0.09) + ((gx * 7 + z * 13) % 11) * 3));

    private static void AssertWidth(RoadEdit r, float width)
    {
        for (int i = 0; i < r.Points.Count; i++) Assert.Equal(width, Vector2.Distance(r.Points[i].Left, r.Points[i].Right), 3);
    }

    [Fact]
    public void AWideRoadFollowsTheStockConventionsAtTwentyWide()
    {
        var c = Arc();
        var road = RoadBuilder.Build(c, 0, width: W);
        Assert.Equal(W, RoadBuilder.WidthOf(road), 3);
        Assert.False(RoadBuilder.IsStandard(road));
        Assert.True(RoadBuilder.IsStandard(RoadBuilder.Build(c, 0)));
        for (int i = 0; i < c.Count; i++)
        {
            var p = road.Points[i];
            Assert.Equal(W, Vector2.Distance(p.Left, p.Right), 3);
            Assert.True(Vector2.Distance(p.Centre, c[i]) < 1e-3f);                                  // the same centre line as a standard road
            Assert.Equal((p.Left.Y + p.Right.Y) / 2, p.CentreZ, 3);
            Assert.Contains(p.Flag, new[] { 0, 36 });
            var t = Vector2.Normalize(i == 0 ? c[1] - c[0] : i == c.Count - 1 ? c[i] - c[i - 1] : c[i + 1] - c[i - 1]);
            Assert.True(Vector2.Dot(Vector2.Normalize(p.Left - p.Right), new Vector2(-t.Y, t.X)) > 0.9999f, $"point {i}");
        }
    }

    [Fact]
    public void AWideRoadRoundTripsThroughTheMissionFileAndTheGameReadsItsEdges()
    {
        var (doc, _) = RoadBuildTests.Blank();
        var road = RoadBuilder.Build(Arc(), 1, width: W);
        doc.Roads.Add(road);
        var bytes = doc.ToBytes();
        var back = Assert.Single(MapDocument.Parse(bytes).Roads);
        Assert.Equal(1u, back.Kind);
        for (int i = 0; i < road.Points.Count; i++) Assert.Equal(road.Points[i].Raw, back.Points[i].Raw);
        Assert.Equal(W, RoadBuilder.WidthOf(back), 3);
        // the game's builder (both looks draw and paint these quads) takes the edges as written: 20 apart, on the ground
        var seg = Assert.Single(I76.Formats.Assembly.Roads.Segments(new MissionFile("rd1.cbt", bytes), (_, _) => 7.0));
        for (int i = 0; i < seg.Points.Length; i++)
        {
            Assert.Equal(W, Vector3.Distance(seg.Points[i].Left, seg.Points[i].Right), 3);
            Assert.Equal(7f, seg.Points[i].Left.Y);
        }
        var quads = RoadRaster.Flatten(I76.Formats.Assembly.Roads.Build(new MissionFile("rd1.cbt", bytes), (_, _) => 0));
        Assert.All(quads, q => Assert.Equal(W, Vector2.Distance(q.A, q.B), 2));   // right_j to left_j: one texture across the whole width
    }

    [Fact]
    public void EditsKeepTheRoadsWidth()
    {
        var road = RoadBuilder.Build(Enumerable.Range(0, 30).Select(i => new Vector2(100 + i * 5, 100)).ToList(), 0, width: W);
        AssertWidth(RoadOps.MovePoint(road, 15, new Vector2(175, 130), 5), W);
        AssertWidth(RoadOps.MovePoint(road, 0, new Vector2(95, 110), 0, new Vector2(1, 0)), W);         // an end turned square to a structure
        AssertWidth(RoadOps.InsertPoint(road, 4, new Vector2(122.5f, 100)), W);
        AssertWidth(RoadOps.DeletePoint(road, 5)!, W);
        AssertWidth(RoadBuilder.Reversed(road), W);
        var join = road.Points[^1].Centre; var h = RoadBuilder.Heading(road, road.Points.Count - 1);
        AssertWidth(RoadBuilder.Extend(road, false, Enumerable.Range(1, 5).Select(i => join + h * 5 * i).ToList()), W);
        var first = road.Points[0].Centre;
        AssertWidth(RoadBuilder.Extend(road, true, Enumerable.Range(1, 5).Select(i => first - RoadBuilder.Heading(road, 0) * 5 * i).ToList()), W);
        var (a, b) = RoadOps.Split(road, 10)!.Value;
        AssertWidth(a, W); AssertWidth(b, W);
        AssertWidth(RoadOps.Retype(road, 2), W);
    }

    [Fact]
    public void ChangingARoadsWidthKeepsItsCentresHeadingsAndFlags()
    {
        var c = Arc();
        var standard = RoadBuilder.Build(c, 0);
        var wide = RoadOps.Retype(standard, 1, W);
        Assert.Equal(1u, wide.Kind);
        AssertWidth(wide, W);
        for (int i = 0; i < c.Count; i++)
        {
            Assert.True(Vector2.Distance(wide.Points[i].Centre, standard.Points[i].Centre) < 1e-3f);
            Assert.True(Vector2.Dot(RoadBuilder.Heading(wide, i), RoadBuilder.Heading(standard, i)) > 0.99999f);
            Assert.Equal(standard.Points[i].Flag, wide.Points[i].Flag);
        }
        // and back: the standard road again, to a millimetre
        var back = RoadOps.Retype(wide, 0, RoadBuilder.Width);
        for (int i = 0; i < c.Count; i++)
        {
            Assert.True(Vector2.Distance(back.Points[i].Left, standard.Points[i].Left) < 1e-3f, $"point {i}");
            Assert.True(Vector2.Distance(back.Points[i].Right, standard.Points[i].Right) < 1e-3f, $"point {i}");
        }
        // the same width: the bytes are kept
        var same = RoadOps.Retype(standard, 2, RoadBuilder.Width);
        for (int i = 0; i < c.Count; i++) Assert.Equal(standard.Points[i].Raw, same.Points[i].Raw);
        // a closed loop stays closed
        var loop = Enumerable.Range(0, 40).Select(i => new Vector2(MathF.Sin(i * MathF.Tau / 39), MathF.Cos(i * MathF.Tau / 39)) * 100 + new Vector2(500, 500)).ToList();
        loop[^1] = loop[0];
        var wideLoop = RoadBuilder.Rewidth(RoadBuilder.Build(loop, 0, closed: true), W);
        Assert.Equal(wideLoop.Points[0].Raw, wideLoop.Points[^1].Raw);
    }

    [Fact]
    public void ThePlannerMakesAWideRoadAndAnExtensionKeepsItsRoadsWidth()
    {
        var doc = Flat();
        var plan = RoadPlanner.Plan(RoadCurves.Resample([new(300, 300), new(500, 300)]), 0, null, null, doc.Roads, W);
        Assert.Null(plan.Problem);
        Assert.Equal(W, plan.Width);
        RoadPlanner.Commit(doc, doc.Roads, plan, true);
        AssertWidth(doc.Roads[0], W);
        // drawn on from its end with the panel at the standard width: the extension is of the road it extends
        var end = RoadSnap.Snap(new Vector2(502, 301), new SnapOptions { Angle = false }, doc.Roads, null, null, null);
        Assert.Equal(SnapKind.RoadEnd, end.Kind);
        var ext = RoadPlanner.Plan(RoadCurves.Resample([end.Point, new(500, 500)]), 0, end, null, doc.Roads);
        Assert.Equal(W, ext.Width);
        RoadPlanner.Commit(doc, doc.Roads, ext, true);
        Assert.Equal(1, doc.Roads.Count);
        AssertWidth(doc.Roads[0], W);
    }

    [Fact]
    public void TheBendLimitScalesWithTheWidth()
    {
        Assert.Equal(RoadPlanner.FoldRadius, RoadPlanner.FoldRadiusFor(RoadBuilder.Width));
        Assert.Equal(11f, RoadPlanner.FoldRadiusFor(W));
        // a hairpin of radius about 8: a standard road takes it, a wide one would fold over itself on the inside
        var path = RoadCurves.Resample(Enumerable.Range(0, 97).Select(i => new Vector2(500 + 8.5f * MathF.Sin(i * MathF.PI / 96), 500 + 8.5f * MathF.Cos(i * MathF.PI / 96))).ToList());
        float r = RoadCurves.MinRadius(path);
        Assert.InRange(r, 7f, 10f);
        var roads = Flat().Roads;
        Assert.Null(RoadPlanner.Plan(path, 0, null, null, roads).Problem);
        Assert.NotNull(RoadPlanner.Plan(path, 0, null, null, roads, W).Problem);
    }

    [Fact]
    public void AWideRoadsEdgesLieOnItsProfileAndTheGroundIsLevelledAcrossIt()
    {
        var doc = Hilly();
        var path = RoadCurves.Spline([new(200, 200), new(700, 330), new(1400, 280), new(2200, 450)]);
        var plan = RoadPlanner.Plan(path, 0, null, null, doc.Roads, W);
        Assert.Null(plan.Problem);
        var h = RoadGrade.Profile(plan.Centres, p => RoadTerrain.Ground(doc, p.X, p.Y));
        RoadPlanner.Commit(doc, doc.Roads, plan, true);
        var road = doc.Roads[0];
        double worst = 0, cross = 0;
        for (int i = 0; i < road.Points.Count; i++)
        {
            var p = road.Points[i];
            double hl = RoadTerrain.Ground(doc, p.Left.X, p.Left.Y), hr = RoadTerrain.Ground(doc, p.Right.X, p.Right.Y);
            double hc = RoadTerrain.Ground(doc, p.Centre.X, p.Centre.Y);
            // the quarter lines too: the engine draws one quad across, so the ground under the middle of each half must be on it as well
            double q1 = RoadTerrain.Ground(doc, (p.Left.X * 3 + p.Right.X) / 4, (p.Left.Y * 3 + p.Right.Y) / 4);
            double q3 = RoadTerrain.Ground(doc, (p.Left.X + p.Right.X * 3) / 4, (p.Left.Y + p.Right.Y * 3) / 4);
            foreach (var g in new[] { hl, hr, hc, q1, q3 }) worst = Math.Max(worst, Math.Abs(g - h[i]));
            cross = Math.Max(cross, Math.Abs(hl - hr));
        }
        Assert.True(worst < 0.4, $"the ground across the road is off its profile by {worst:0.00} m");
        Assert.True(cross < 0.45, $"cross slope {cross:0.00} m");
    }

    [Fact]
    public void TheSurfaceBandIsAsFarPastAWideRoadsEdgesAsAStandardOnes()
    {
        Assert.Equal(RoadSurface.Band, RoadSurface.BandFor(RoadBuilder.Width));
        Assert.Equal(15f, RoadSurface.BandFor(W));
        var doc = Flat();
        var plan = RoadPlanner.Plan(RoadCurves.Resample([new(300, 400), new(700, 400)]), 1, null, null, doc.Roads, W);
        RoadPlanner.Commit(doc, doc.Roads, plan, false);
        int Nib(float x, float z) => (doc.Tiles.Sample((int)MathF.Round(x / 5), (int)MathF.Round(z / 5)) ?? 0) >> 12;
        for (float x = 310; x <= 690; x += 5)
        {
            Assert.Equal(6, Nib(x, 400 + 15)); Assert.Equal(6, Nib(x, 400 - 15));
            Assert.Equal(0, Nib(x, 400 + 20)); Assert.Equal(0, Nib(x, 400 - 20));
        }
        // narrowed again: the outer band goes back to the ground beside it, in the same edit
        var cmd = RoadEditing.Retype(doc, doc.Roads, 0, 1, RoadBuilder.Width);
        Assert.Equal(6, Nib(500, 410));
        Assert.Equal(0, Nib(500, 415));
        AssertWidth(doc.Roads[0], RoadBuilder.Width);
        cmd.Undo(doc);
        AssertWidth(doc.Roads[0], W);
        Assert.Equal(6, Nib(500, 415));
    }

    [Fact]
    public void NoIntersectionPieceGoesOnAWideRoad()
    {
        static RoadEdit Line(Vector2 a, Vector2 b, float width = RoadBuilder.Width) => RoadBuilder.Build(RoadCurves.Resample([a, b]), 0, width: width);
        foreach (var (through, stem) in new[] { (W, RoadBuilder.Width), (RoadBuilder.Width, W), (W, W) })
        {
            var store = new RoadStore([Line(new(500, 500), new(500, 700), through)]);
            var j = new Junctions(store, new PieceStore(), _ => 50f);
            Assert.Empty(j.Detect(Line(new(580, 600), new(500, 600), stem)));        // ends on its middle: a T for two standard roads
            Assert.Empty(j.Detect(Line(new(400, 600), new(600, 600), stem)));        // crosses it: an X for two standard roads
        }
        var plain = new Junctions(new RoadStore([Line(new(500, 500), new(500, 700))]), new PieceStore(), _ => 50f);
        Assert.NotEmpty(plain.Detect(Line(new(580, 600), new(500, 600))));
    }

    [Fact]
    public void WideningARoadAtAPieceTakesThePieceAwayAsBulldozingItWould()
    {
        static RoadEdit Line(Vector2 a, Vector2 b) => RoadBuilder.Build(RoadCurves.Resample([a, b]), 0);
        var store = new RoadStore([Line(new(500, 500), new(500, 700))]);
        var pieces = new PieceStore();
        store.Pieces = pieces;
        var j = new Junctions(store, pieces, _ => 50f);
        var snap = j.Take();
        new RoadCommand("Road", store, [new RoadOp.Insert(store.Count, Line(new(580, 600), new(500, 600)))]).Apply();
        j.Settle(snap);
        Assert.Single(pieces.Items);
        Assert.Equal(3, store.Count);
        // the stem made wide: the T goes, and the through road it cut is one road again
        int stem = Enumerable.Range(0, store.Count).Single(i => store[i].Points.Any(p => p.Centre.X > 540));
        snap = j.Take();
        RoadEditing.Retype(null, store, stem, 0, W);
        j.Settle(snap);
        Assert.Empty(pieces.Items);
        Assert.Equal(2, store.Count);
        Assert.Single(store.Roads, r => !RoadBuilder.IsStandard(r));
    }
}
