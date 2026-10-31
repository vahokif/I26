using System.Numerics;
using I76.Editor;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

public class RoadCurveTests
{
    [Fact]
    public void ResamplingKeepsTheEndsAndStepsAboutFiveApart()
    {
        foreach (float len in new[] { 12f, 23f, 100f, 333f })
        {
            var pts = RoadCurves.Resample([new Vector2(10, 10), new Vector2(10 + len, 10)]);
            Assert.True(pts.Count >= RoadCurves.MinPoints);
            Assert.Equal(new Vector2(10, 10), pts[0]);
            Assert.Equal(10 + len, pts[^1].X, 3);
            for (int i = 1; i < pts.Count; i++) Assert.InRange(Vector2.Distance(pts[i - 1], pts[i]), 3.3f, 6.7f);
            float first = Vector2.Distance(pts[0], pts[1]);
            for (int i = 2; i < pts.Count; i++) Assert.Equal(first, Vector2.Distance(pts[i - 1], pts[i]), 2);   // equal steps
        }
    }

    [Fact]
    public void AQuadraticBezierStartsAndEndsOnItsPointsAndLeansToTheControl()
    {
        var c = RoadCurves.Quadratic(new Vector2(0, 0), new Vector2(50, 100), new Vector2(100, 0));
        Assert.Equal(new Vector2(0, 0), c[0]);
        Assert.Equal(100, c[^1].X, 3);
        Assert.Equal(50, c[c.Count / 2].Y, 1);               // the midpoint of a quadratic is half-way to the control
        var cu = RoadCurves.Cubic(new Vector2(0, 0), new Vector2(0, 60), new Vector2(100, 60), new Vector2(100, 0));
        Assert.Equal(new Vector2(100, 0), cu[^1]);
        Assert.Equal(45, cu[cu.Count / 2].Y, 1);             // (3 * 60 + 3 * 60) / 8 = 45
    }

    [Fact]
    public void ASplinePassesThroughItsClicks()
    {
        Vector2[] clicks = [new(0, 0), new(40, 30), new(90, 10), new(130, 60), new(200, 55)];
        var s = RoadCurves.Spline(clicks);
        foreach (var k in clicks) Assert.Contains(s, p => Vector2.Distance(p, k) < 0.01f);
        Assert.Equal(clicks[^1], s[^1]);
    }

    [Fact]
    public void AnArcOfRadiusRHasMinRadiusR()
    {
        float r = 60;
        var arc = Enumerable.Range(0, 30).Select(i => new Vector2(MathF.Sin(i * 0.08f), MathF.Cos(i * 0.08f)) * r).ToList();
        Assert.Equal(r, RoadCurves.MinRadius(arc), 1);
        Assert.True(float.IsPositiveInfinity(RoadCurves.MinRadius([new(0, 0), new(5, 0), new(10, 0)])));
    }

    [Fact]
    public void RoundedCornersTurnNoMoreThanAStockRoadDoes()
    {
        // a right angle chained from two straights becomes an arc the road can follow: stock points turn under 9 degrees in 99 % of cases
        var r = RoadCurves.Resample(RoadCurves.Fillet([new(0, 0), new(100, 0), new(100, 100)], 32f));
        Assert.InRange(RoadCurves.MaxTurnDegrees(r), 1, 14);
        Assert.True(RoadCurves.MinRadius(r) > 20);
    }
}

public class RoadBuildTests
{
    private static List<Vector2> Arc(float r = 80, int n = 24) => Enumerable.Range(0, n).Select(i => new Vector2(MathF.Sin(i * 0.06f), MathF.Cos(i * 0.06f)) * r + new Vector2(1000, 1000)).ToList();

    [Fact]
    public void ARoadFollowsTheStockConventions()
    {
        var c = Arc();
        var road = RoadBuilder.Build(c, 1);
        Assert.Equal(1u, road.Kind);
        Assert.Equal(c.Count, road.Points.Count);
        for (int i = 0; i < c.Count; i++)
        {
            var p = road.Points[i];
            Assert.Equal(10f, Vector2.Distance(p.Left, p.Right), 3);                               // every stock road is exactly 10 wide
            Assert.True(Vector2.Distance(p.Centre, c[i]) < 1e-3f);
            Assert.Equal((p.Left.Y + p.Right.Y) / 2, p.CentreZ, 3);                                // the left y word is the mean z of the edges
            Assert.Contains(p.Flag, new[] { 0, 36 });
            // left is on the (-tz, tx) side of right for the direction of increasing index; edges square to the central difference
            var t = Vector2.Normalize((i == 0 ? c[1] - c[0] : i == c.Count - 1 ? c[i] - c[i - 1] : c[i + 1] - c[i - 1]));
            var w = Vector2.Normalize(p.Left - p.Right);
            Assert.True(Vector2.Dot(w, new Vector2(-t.Y, t.X)) > 0.9999f, $"point {i}");
        }
    }

    [Fact]
    public void AClosedRoadEndsOnItsFirstPointExactly()
    {
        var c = Enumerable.Range(0, 40).Select(i => new Vector2(MathF.Sin(i * MathF.Tau / 39), MathF.Cos(i * MathF.Tau / 39)) * 100 + new Vector2(500, 500)).ToList();
        c[^1] = c[0];
        var road = RoadBuilder.Build(c, 0, closed: true);
        Assert.True(RoadBuilder.IsClosed(road));
        Assert.Equal(road.Points[0].Raw, road.Points[^1].Raw);
    }

    [Fact]
    public void AWrittenRoadRoundTripsThroughTheMissionFile()
    {
        var (doc, bytes) = Blank();
        var road = RoadBuilder.Build(Arc(), 2);
        doc.Roads.Add(road);
        var back = MapDocument.Parse(doc.ToBytes());
        var r = Assert.Single(back.Roads);
        Assert.Equal(2u, r.Kind);
        Assert.Equal(road.Points.Count, r.Points.Count);
        for (int i = 0; i < r.Points.Count; i++) Assert.Equal(road.Points[i].Raw, r.Points[i].Raw);
        // everything else of the file is as it was
        doc.Roads.Clear();
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void ExtendingAtTheEndKeepsTheRoadsOwnPointsByteForByte()
    {
        var road = RoadBuilder.Build(Arc(), 0);
        var pointsBefore = road.Points.Select(p => (byte[])p.Raw.Clone()).ToList();
        var join = road.Points[^1].Centre;
        var heading = RoadBuilder.Heading(road, road.Points.Count - 1);
        var extra = Enumerable.Range(1, 6).Select(i => join + heading * 5 * i).ToList();
        var longer = RoadBuilder.Extend(road, false, extra);
        Assert.Equal(road.Points.Count + 6, longer.Points.Count);
        for (int i = 0; i < pointsBefore.Count; i++) Assert.Equal(pointsBefore[i], longer.Points[i].Raw);
        // straight on: the new points continue the old heading and the width
        Assert.Equal(10f, Vector2.Distance(longer.Points[^1].Left, longer.Points[^1].Right), 3);
        Assert.True(Vector2.Dot(RoadBuilder.Heading(longer, longer.Points.Count - 1), heading) > 0.999f);
    }

    [Fact]
    public void ExtendingAtTheStartPutsTheNewPointsInFrontAndKeepsTheDirectionOfIndex()
    {
        var road = RoadBuilder.Build(Enumerable.Range(0, 10).Select(i => new Vector2(100 + i * 5, 200)).ToList(), 0);   // runs +x
        var heading = RoadBuilder.Heading(road, 0);
        var outward = -heading;
        var extra = Enumerable.Range(1, 4).Select(i => road.Points[0].Centre + outward * 5 * i).ToList();
        var longer = RoadBuilder.Extend(road, true, extra);
        Assert.Equal(14, longer.Points.Count);
        Assert.Equal(road.Points[0].Raw, longer.Points[4].Raw);
        Assert.Equal(80, longer.Points[0].Centre.X, 2);
        for (int i = 0; i < longer.Points.Count; i++)
        {
            Assert.True(Vector2.Dot(RoadBuilder.Heading(longer, i), heading) > 0.999f, $"point {i}");   // left stays left
            Assert.Equal(10f, Vector2.Distance(longer.Points[i].Left, longer.Points[i].Right), 3);
        }
    }

    [Fact]
    public void ReversingSwapsTheEdges()
    {
        var road = RoadBuilder.Build(Arc(), 0);
        var rev = RoadBuilder.Reversed(road);
        Assert.Equal(road.Points.Count, rev.Points.Count);
        Assert.True(Vector2.Distance(rev.Points[0].Centre, road.Points[^1].Centre) < 1e-3f);
        Assert.True(Vector2.Dot(RoadBuilder.Heading(rev, 3), -RoadBuilder.Heading(road, road.Points.Count - 4)) > 0.99f);
    }

    internal static (MapDocument Doc, byte[] Bytes) Blank()
    {
        var p = MapProject.CreateBlank("rd1", "Road One", ".cbt", 2, 2, 20);
        var bytes = p.Mission.ToBytes();
        return (MapDocument.Parse(bytes), bytes);
    }

    [Fact]
    public void BuildingMatchesTheStockRoadsOfAnInstall()
    {
        // rebuild stock roads from their own centre lines: the edges land where the original tool put them, to a few centimetres
        foreach (var (dir, ext) in Installs())
        {
            int roads = 0, points = 0, close = 0;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)).Take(8))
            {
                var doc = MapDocument.Load(f);
                foreach (var r in doc.Roads.Where(r => r.Points.Count > 6 && !RoadBuilder.IsClosed(r)))
                {
                    var rebuilt = RoadBuilder.Build(RoadBuilder.Centres(r), r.Kind);
                    for (int i = 1; i < r.Points.Count - 1; i++)
                    {
                        points++;
                        if (Vector2.Distance(rebuilt.Points[i].Left, r.Points[i].Left) < 0.35f && Vector2.Distance(rebuilt.Points[i].Right, r.Points[i].Right) < 0.35f) close++;
                    }
                    roads++;
                }
            }
            Assert.True(roads > 3, dir);
            Assert.True(close > points * 0.97, $"{dir}: only {close} of {points} edges within 35 cm");
        }
    }

    private static IEnumerable<(string Dir, string Ext)> Installs()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } a && Directory.Exists(Path.Combine(a, "miss16"))) yield return (Path.Combine(a, "miss16"), ".msn");
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } b && Directory.Exists(Path.Combine(b, "miss16"))) yield return (Path.Combine(b, "miss16"), ".cbt");
    }
}

public class RoadSnapTests
{
    private static RoadStore Store() => new([
        RoadBuilder.Build(Enumerable.Range(0, 21).Select(i => new Vector2(1000 + i * 5, 2000)).ToList(), 0),   // runs +x, 1000..1100
        RoadBuilder.Build(Enumerable.Range(0, 21).Select(i => new Vector2(1500, 2000 + i * 5)).ToList(), 0)]);

    private static StructureAttach.Attach Deck(float x, float z, float y = 50) => new(new Vector3(x, y, z), new Vector2(-1, 0), 10, true);

    [Fact]
    public void AStructureBeatsARoadEndBeatsARoadsMiddle()
    {
        var o = new SnapOptions();
        var s = Store();
        var near = Deck(1103, 2002);
        var r = RoadSnap.Snap(new Vector2(1104, 2003), o, s, [near], null, null);
        Assert.Equal(SnapKind.Structure, r.Kind);
        Assert.Equal(new Vector2(1103, 2002), r.Point);
        r = RoadSnap.Snap(new Vector2(1104, 2003), o, s, null, null, null);
        Assert.Equal(SnapKind.RoadEnd, r.Kind);
        Assert.Equal(0, r.Road); Assert.False(r.AtStart); Assert.Equal(20, r.Index);
        Assert.Equal(new Vector2(1100, 2000), r.Point);
        r = RoadSnap.Snap(new Vector2(1050, 2004), o, s, null, null, null);
        Assert.Equal(SnapKind.RoadMiddle, r.Kind);
        Assert.Equal(2000, r.Point.Y, 3);
        Assert.NotNull(r.Hint);
        Assert.Contains("junction", r.Hint);
    }

    [Fact]
    public void AStructuresRaisedLipIsNotSnappedTo()
    {
        var lip = new StructureAttach.Attach(new Vector3(1103, 60, 2002), new Vector2(1, 0), 24, false) { Slope = -0.28f };
        Assert.False(lip.RoadLevel);
        var r = RoadSnap.Snap(new Vector2(1104, 2003), new SnapOptions { Roads = false }, Store(), [lip], null, null);
        Assert.NotEqual(SnapKind.Structure, r.Kind);
    }

    [Fact]
    public void AnglesSnapToFifteenDegreesFromThePreviousPiece()
    {
        var o = new SnapOptions { Angle = true, Roads = false };
        var anchor = new Vector2(0, 0);
        var prev = new Vector2(0, 1);                   // heading +z
        var cursor = new Vector2(MathF.Sin(33 * MathF.PI / 180), MathF.Cos(33 * MathF.PI / 180)) * 100;
        var r = RoadSnap.Snap(cursor, o, new RoadStore([]), null, anchor, prev);
        Assert.Equal(SnapKind.Angle, r.Kind);
        Assert.Equal(100, r.Point.Length(), 2);
        Assert.Equal(30, MathF.Atan2(r.Point.X, r.Point.Y) * 180 / MathF.PI, 2);
        // relative to a heading that is not an axis
        var h = new Vector2(MathF.Sin(10 * MathF.PI / 180), MathF.Cos(10 * MathF.PI / 180));
        var r2 = RoadSnap.Snap(cursor, o, new RoadStore([]), null, anchor, h);
        Assert.Equal(40, MathF.Atan2(r2.Point.X, r2.Point.Y) * 180 / MathF.PI, 2);    // 23 degrees past the heading rounds to 30 more: 10 + 30
        // off: the cursor stays
        Assert.Equal(cursor, RoadSnap.Snap(cursor, new SnapOptions { Angle = false, Roads = false }, new RoadStore([]), null, anchor, h).Point);
    }

    [Fact]
    public void TheGridSnapsAPointAndRoundsALength()
    {
        var o = new SnapOptions { Angle = false, Grid = true, GridSize = 10, Roads = false };
        Assert.Equal(new Vector2(120, 40), RoadSnap.Snap(new Vector2(123, 37), o, new RoadStore([]), null, null, null).Point);
        var both = new SnapOptions { Angle = true, Grid = true, GridSize = 10, Roads = false };
        var r = RoadSnap.Snap(new Vector2(0, 47), both, new RoadStore([]), null, Vector2.Zero, new Vector2(0, 1));
        Assert.Equal(50, r.Point.Length(), 2);
    }

    [Fact]
    public void CrossingsAreFoundButSharedEndsAreNot()
    {
        var s = Store();
        var cross = RoadSnap.Crossings([new Vector2(1050, 1900), new Vector2(1050, 2100)], s);
        Assert.Single(cross);
        Assert.Empty(RoadSnap.Crossings([new Vector2(1100, 2000), new Vector2(1200, 2050)], s));
    }
}

public class RoadGradeTests
{
    private static List<Vector2> Line(int n) => Enumerable.Range(0, n).Select(i => new Vector2(i * 5, 0)).ToList();

    [Fact]
    public void AProfileNeverClimbsMoreThanTheLimit()
    {
        var c = Line(60);
        float Ground(Vector2 p) => 20 + 1.0f * p.X * 0.3f;               // a 0.3 slope
        var h = RoadGrade.Profile(c, Ground, new RoadGrade.Settings(0.10f, 30));
        Assert.True(RoadGrade.MaxAbs(RoadGrade.Grades(c, h)) <= 0.1001f);
        // a gentle hill the limit does not touch stays close to the ground
        float Hill(Vector2 p) => 20 + 3 * MathF.Sin(p.X / 60);
        var g = RoadGrade.Profile(c, Hill, new RoadGrade.Settings(0.10f, 30));
        for (int i = 0; i < c.Count; i++) Assert.InRange(MathF.Abs(g[i] - Hill(c[i])), 0, 1.2f);
    }

    [Fact]
    public void PinnedHeightsStayAndTheGradeBlendsIntoThem()
    {
        var c = Line(40);
        var pins = new Dictionary<int, float> { [0] = 50f, [39] = 60f };
        var h = RoadGrade.Profile(c, _ => 50f, new RoadGrade.Settings(0.10f, 30), pins);
        Assert.Equal(50f, h[0]); Assert.Equal(60f, h[39]);
        Assert.True(RoadGrade.MaxAbs(RoadGrade.Grades(c, h)) <= 0.1001f);
        for (int i = 1; i < h.Length; i++) Assert.True(h[i] >= h[i - 1] - 0.05f, $"point {i}");   // one smooth climb, no dip
        // pins that cannot be met at the limit (30 up in 195) still come out as given, as steep as they must
        var steep = RoadGrade.Profile(c, _ => 50f, new RoadGrade.Settings(0.10f, 30), new Dictionary<int, float> { [0] = 50f, [39] = 80f });
        Assert.Equal(50f, steep[0]); Assert.Equal(80f, steep[39]);
    }

    [Fact]
    public void GradeBandsAreGreenYellowRed()
    {
        Assert.Equal(0, RoadGrade.Band(0.03f, 0.1f));
        Assert.Equal(1, RoadGrade.Band(-0.08f, 0.1f));
        Assert.Equal(2, RoadGrade.Band(0.15f, 0.1f));
    }
}

public class RoadTerrainTests
{
    private const int T = TerrainTiles.Tile;

    private static TerrainDocument Hilly()
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;   // a 4-tile strip
        var ter = new byte[4 * T * T * 2];
        for (int t = 0; t < 4; t++)
            for (int z = 0; z < T; z++)
                for (int x = 0; x < T; x++)
                {
                    int gx = t * T + x;
                    ushort h = (ushort)(600 + 90 * Math.Sin(gx * 0.07) + 60 * Math.Cos(z * 0.09) + ((gx * 7 + z * 13) % 11) * 3);
                    int i = (t * T * T + z * T + x) * 2;
                    ter[i] = (byte)h; ter[i + 1] = (byte)(h >> 8); ter[i + 1] |= 0x30;
                }
        return new TerrainDocument(new TerrainTiles(ter, 4, grid), "hilly");
    }

    [Fact]
    public void ARoadsEdgesLieOnItsProfile()
    {
        var doc = Hilly();
        var centres = RoadCurves.Resample(RoadCurves.Spline([new(200, 200), new(700, 330), new(1400, 280), new(2200, 450)]));
        var h = RoadGrade.Profile(centres, p => RoadTerrain.Ground(doc, p.X, p.Y));
        var rec = new HeightRecorder(doc);
        var res = RoadTerrain.Conform(doc, rec, centres, h);
        Assert.True(res.Samples > 100);
        // the engine puts a road's edges at the bilinear ground: they must land on the profile, so the road is neither floating nor buried
        var road = RoadBuilder.Build(centres, 0);
        double worst = 0, cross = 0;
        for (int i = 0; i < road.Points.Count; i++)
        {
            double hl = RoadTerrain.Ground(doc, road.Points[i].Left.X, road.Points[i].Left.Y);
            double hr = RoadTerrain.Ground(doc, road.Points[i].Right.X, road.Points[i].Right.Y);
            double hc = RoadTerrain.Ground(doc, centres[i].X, centres[i].Y);
            worst = Math.Max(worst, Math.Max(Math.Abs(hl - h[i]), Math.Max(Math.Abs(hr - h[i]), Math.Abs(hc - h[i]))));
            cross = Math.Max(cross, Math.Abs(hl - hr));
        }
        Assert.True(worst < 0.35, $"edges off the profile by {worst:0.00} m");
        Assert.True(cross < 0.45, $"cross slope {cross:0.00} m");
        // and the road's actual grade is within the limit (the quads span 5 apart, so allow the sampling)
        var along = RoadGrade.Grades(centres, centres.Select(p => RoadTerrain.Ground(doc, p.X, p.Y)).ToArray());
        Assert.True(RoadGrade.MaxAbs(along) <= RoadGrade.DefaultMaxGrade + 0.05f, $"grade {RoadGrade.MaxAbs(along):0.000}");
    }

    [Fact]
    public void TheShoulderBlendsBackToTheOldGround()
    {
        var doc = Hilly();
        var before = doc.Snapshot();
        var centres = Enumerable.Range(0, 41).Select(i => new Vector2(300 + i * 5, 400)).ToList();
        var h = centres.Select(_ => 90f).ToList();
        RoadTerrain.Conform(doc, new HeightRecorder(doc), centres, h);
        var shoulder = new RoadTerrain.Settings();
        // far outside the reach: unchanged. Inside the flat strip: the profile
        Assert.Equal(before, SnapshotWithout(doc, before, 300, 500, 400, shoulder.Flat + shoulder.Shoulder + 12));
        Assert.Equal(900, doc.GetHeight(80, 80));
        // the shoulder lies between the old ground and the road: a monotone blend away from the road
        double d0 = Math.Abs(doc.GetHeight(80, 80 + 3) - 900.0), d1 = Math.Abs(doc.GetHeight(80, 80 + 4) - 900.0);
        Assert.True(d1 >= d0);
    }

    // snapshots equal outside the rectangle's reach: returns `before` when the samples outside the corridor are unchanged, else the current snapshot
    private static ushort[] SnapshotWithout(TerrainDocument doc, ushort[] before, float x0, float x1, float z, float reach)
    {
        var now = doc.Snapshot();
        for (int i = 0; i < now.Length; i++)
        {
            int tile = i / (T * T), off = i % (T * T);
            int sx = tile * T + off % T, sz = off / T;
            bool inside = sx * 5 >= x0 - reach && sx * 5 <= x1 + reach && Math.Abs(sz * 5 - z) <= reach;
            if (inside) now[i] = before[i];
        }
        return now;
    }

    [Fact]
    public void ARoadAndItsGroundAreOneUndoStepAndRedoRestoresBoth()
    {
        var doc = Hilly();
        var store = doc.Roads;
        var snap0 = doc.Snapshot();
        var plan = RoadPlanner.Plan(RoadCurves.Quadratic(new(300, 300), new(900, 600), new(1500, 300)), 0, null, null, store);
        Assert.Null(plan.Problem);
        var (cmd, res) = RoadPlanner.Commit(doc, store, plan, conform: true)!.Value;
        Assert.Equal(1, store.Count);
        Assert.True(store.Dirty && doc.Modified);
        var snap1 = doc.Snapshot();
        Assert.NotEqual(snap0, snap1);
        Assert.True(res!.Samples > 0);
        var undo = new UndoStack(doc);
        undo.Push(cmd);
        undo.Undo();
        Assert.Equal(0, store.Count);
        Assert.Equal(snap0, doc.Snapshot());
        undo.Redo();
        Assert.Equal(1, store.Count);
        Assert.Equal(snap1, doc.Snapshot());
    }

    [Fact]
    public void WithoutConformingTheGroundIsLeftAlone()
    {
        var doc = Hilly();
        var snap0 = doc.Snapshot();
        var plan = RoadPlanner.Plan(RoadCurves.Quadratic(new(300, 300), new(900, 600), new(1500, 300)), 0, null, null, doc.Roads);
        RoadPlanner.Commit(doc, doc.Roads, plan, conform: false);
        Assert.Equal(snap0.Select(v => v & 0x0fff), doc.Snapshot().Select(v => v & 0x0fff));   // (the surface nibble is laid either way: RoadSurfaceTests)
        Assert.Equal(1, doc.Roads.Count);
    }
}

public class RoadPlannerTests
{
    private static (TerrainDocument Doc, RoadStore Roads) Flat()
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;
        var ter = new byte[4 * TerrainTiles.Tile * TerrainTiles.Tile * 2];
        for (int i = 0; i < ter.Length; i += 2) { ter[i] = 0xc8; ter[i + 1] = 0x30; }   // 20.0 m, nibble 3
        var doc = new TerrainDocument(new TerrainTiles(ter, 4, grid), "flat");
        return (doc, doc.Roads);
    }

    private static List<Vector2> Straight(Vector2 a, Vector2 b) => [a, b];

    private static void Make(TerrainDocument doc, List<Vector2> path, SnapResult? start = null, SnapResult? end = null, uint kind = 0)
    {
        var plan = RoadPlanner.Plan(path, kind, start, end, doc.Roads);
        Assert.Null(plan.Problem);
        Assert.NotNull(RoadPlanner.Commit(doc, doc.Roads, plan, true));
    }

    [Fact]
    public void DrawingFromTheEndOfARoadExtendsThatRoad()
    {
        var (doc, roads) = Flat();
        Make(doc, Straight(new(300, 300), new(500, 300)));
        var first = roads[0];
        int n = first.Points.Count;
        var end = RoadSnap.Snap(new Vector2(503, 302), new SnapOptions { Angle = false }, roads, null, null, null);
        Assert.Equal(SnapKind.RoadEnd, end.Kind);
        Make(doc, Straight(end.Point, new(500, 500)), start: end);
        Assert.Equal(1, roads.Count);                                        // extended, not a second road
        Assert.True(roads[0].Points.Count > n + 10);
        for (int i = 0; i < n; i++) Assert.Equal(first.Points[i].Raw, roads[0].Points[i].Raw);   // the old points untouched
    }

    [Fact]
    public void DrawingTowardTheStartOfARoadExtendsItBackwards()
    {
        var (doc, roads) = Flat();
        Make(doc, Straight(new(300, 300), new(500, 300)));
        int n = roads[0].Points.Count;
        var s = RoadSnap.Snap(new Vector2(299, 301), new SnapOptions { Angle = false }, roads, null, null, null);
        Assert.True(s.AtStart);
        Make(doc, Straight(new(300, 100), s.Point), end: s);
        Assert.Equal(1, roads.Count);
        Assert.True(roads[0].Points.Count > n + 10);
        Assert.True(RoadBuilder.Centres(roads[0])[0].Y < 150);                // the new points are in front
        for (int i = 0; i < roads[0].Points.Count; i++) Assert.Equal(10f, Vector2.Distance(roads[0].Points[i].Left, roads[0].Points[i].Right), 3);
    }

    [Fact]
    public void JoiningTheOtherEndOfTheSameRoadClosesALoop()
    {
        var (doc, roads) = Flat();
        Make(doc, RoadCurves.Cubic(new(400, 400), new(400, 700), new(900, 700), new(900, 400)));
        var a = RoadSnap.Snap(new Vector2(900, 400), new SnapOptions { Angle = false }, roads, null, null, null);
        var b = RoadSnap.Snap(new Vector2(400, 400), new SnapOptions { Angle = false }, roads, null, null, null);
        Assert.NotEqual(a.AtStart, b.AtStart);
        Make(doc, RoadCurves.Cubic(a.Point, new(900, 200), new(400, 200), b.Point), start: a, end: b);
        Assert.Equal(1, roads.Count);
        Assert.True(RoadBuilder.IsClosed(roads[0]));
        Assert.Equal(roads[0].Points[0].Raw, roads[0].Points[^1].Raw);
    }

    [Fact]
    public void EndingOnTheMiddleOfARoadMakesASeparateRoadWithAHint()
    {
        var (doc, roads) = Flat();
        Make(doc, Straight(new(300, 300), new(700, 300)));
        var end = RoadSnap.Snap(new Vector2(500, 303), new SnapOptions { Angle = false }, roads, null, null, null);
        Assert.Equal(SnapKind.RoadMiddle, end.Kind);
        Assert.NotNull(end.Hint);
        Make(doc, Straight(new(500, 600), end.Point), end: end);
        Assert.Equal(2, roads.Count);
        Assert.Equal(new Vector2(500, 300), roads[1].Points[^1].Centre);
    }

    [Fact]
    public void AStructuresEndPinsTheRoadsHeightAndHeadingThere()
    {
        var (doc, roads) = Flat();
        // a deck 30 m up whose west end is at (1000, 500) facing west: the road arrives heading east, and the ground at the end is the deck's height
        var deck = new StructureAttach.Attach(new Vector3(1000, 28, 500), new Vector2(-1, 0), 10, true);
        var end = RoadSnap.Snap(new Vector2(1002, 503), new SnapOptions { Angle = false }, roads, [deck], null, null);
        Assert.Equal(SnapKind.Structure, end.Kind);
        var plan = RoadPlanner.Plan(Straight(new(500, 500), end.Point), 0, null, end, roads);
        Assert.Equal(28f, plan.Pins[plan.Centres.Count - 1]);
        Assert.True(Vector2.Dot(plan.EndDir!.Value, new Vector2(1, 0)) > 0.999f);
        RoadPlanner.Commit(doc, roads, plan, true);
        var last = roads[0].Points[^1];
        Assert.Equal(new Vector2(1000, 500), last.Centre);
        Assert.Equal(28.0, RoadTerrain.Ground(doc, 1000, 500), 1);                                   // the ground meets the deck
        Assert.Equal(1000, last.Left.X, 2);                                                           // square to the deck's end edge
        Assert.Equal(1000, last.Right.X, 2);
        // and the grade into it stays within the limit
        var g = RoadGrade.Grades(RoadBuilder.Centres(roads[0]), RoadBuilder.Centres(roads[0]).Select(p => RoadTerrain.Ground(doc, p.X, p.Y)).ToArray());
        Assert.True(RoadGrade.MaxAbs(g) <= 0.16f);
    }

    [Fact]
    public void ATooTightBendIsRefused()
    {
        var (doc, roads) = Flat();
        var plan = RoadPlanner.Plan(RoadCurves.Spline([new(300, 300), new(330, 300), new(330, 308), new(300, 308)]), 0, null, null, roads);
        Assert.NotNull(plan.Problem);
        Assert.Null(RoadPlanner.Commit(doc, roads, plan, true));
        Assert.Equal(0, roads.Count);
        Assert.NotNull(RoadPlanner.Plan([new(0, 0), new(6, 0)], 0, null, null, roads).Problem);
    }

    [Fact]
    public void MovingASinglePointAndABendingDragKeepTheRoadTidy()
    {
        var road = RoadBuilder.Build(Enumerable.Range(0, 30).Select(i => new Vector2(100 + i * 5, 100)).ToList(), 0);
        var one = RoadOps.MovePoint(road, 15, new Vector2(175, 120), 0);
        Assert.Equal(new Vector2(175, 120), one.Points[15].Centre);
        Assert.Equal(road.Points[10].Raw, one.Points[10].Raw);                         // the others are the same bytes
        Assert.Equal(road.Points[20].Raw, one.Points[20].Raw);
        var soft = RoadOps.MovePoint(road, 15, new Vector2(175, 120), 5);
        Assert.Equal(new Vector2(175, 120), soft.Points[15].Centre);
        Assert.True(soft.Points[13].Centre.Y > 100 && soft.Points[13].Centre.Y < 120);
        Assert.Equal(road.Points[8].Raw, soft.Points[8].Raw);
        for (int i = 0; i < soft.Points.Count; i++) Assert.Equal(10f, Vector2.Distance(soft.Points[i].Left, soft.Points[i].Right), 3);
    }

    [Fact]
    public void InsertDeleteSplitRetype()
    {
        var road = RoadBuilder.Build(Enumerable.Range(0, 12).Select(i => new Vector2(100 + i * 5, 100)).ToList(), 0);
        var ins = RoadOps.InsertPoint(road, 4, new Vector2(122.5f, 100));
        Assert.Equal(13, ins.Points.Count);
        Assert.Equal(122.5f, ins.Points[5].Centre.X, 3);
        var del = RoadOps.DeletePoint(road, 5)!;
        Assert.Equal(11, del.Points.Count);
        Assert.Null(RoadOps.DeletePoint(RoadBuilder.Build([new(0, 0), new(5, 0), new(10, 0)], 0), 1));
        var (a, b) = RoadOps.Split(road, 6)!.Value;
        Assert.Equal(7, a.Points.Count); Assert.Equal(6, b.Points.Count);
        Assert.Equal(a.Points[^1].Raw, b.Points[0].Raw);
        Assert.Null(RoadOps.Split(road, 1));
        Assert.Equal(2u, RoadOps.Retype(road, 2).Kind);
    }

    [Fact]
    public void ARoadEditsSavedRoundTripThroughTheMapFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "i26-roads-" + Guid.NewGuid().ToString("N"));
        try
        {
            var addon = Path.Combine(dir, "addon");
            var (_, files) = NewMap.CreateFlat(addon, "rd2", "Road Two", 2, 20, nitro: true);
            string mission = files.First(f => f.EndsWith(".cbt"));
            var project = MapProject.Load(mission);
            var store = RoadStore.FromMission(File.ReadAllBytes(mission));
            var doc = new TerrainDocument(project.Terrain, "rd2", "Road Two", mission, addon, roads: store);
            var plan = RoadPlanner.Plan(RoadCurves.Quadratic(new(2700, 46200), new(3000, 46500), new(3400, 46200)), 1, null, null, store);
            RoadPlanner.Commit(doc, store, plan, true);
            Assert.True(doc.Modified);
            doc.Save();
            Assert.False(doc.Modified);
            var back = MapProject.Load(mission);
            var r = Assert.Single(back.Mission.Roads);
            Assert.Equal(1u, r.Kind);
            Assert.Equal(store[0].Points.Count, r.Points.Count);
            Assert.Equal(store[0].Points[7].Raw, r.Points[7].Raw);
            // unchanged roads: a second save with nothing changed leaves the file as it is
            var bytes = File.ReadAllBytes(mission);
            doc.Save();
            Assert.Equal(bytes, File.ReadAllBytes(mission));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class StructureAttachTests
{
    private static Vector3[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => [a, b, c, d];

    [Fact]
    public void ALevelDeckHasTwoRoadLevelEndsAtItsSquareEdges()
    {
        // a deck 80 long (x 100..180) and 10 wide (z 45..55) at height 12
        var ends = StructureAttach.Ends([Quad(new(100, 12, 45), new(180, 12, 45), new(180, 12, 55), new(100, 12, 55))]);
        Assert.Equal(2, ends.Count);
        var west = ends.Single(e => e.Direction.X < 0);
        Assert.Equal(new Vector3(100, 12, 50), west.Position);
        Assert.Equal(10, west.Width, 3);
        Assert.True(west.RoadLevel);
        Assert.All(ends, e => Assert.True(e.RoadLevel));
        Assert.Equal(0, west.Slope, 3);
    }

    [Fact]
    public void ARampHasAFootToJoinAndALipToAvoid()
    {
        // a ramp 25 long rising 7 along +x, 10 wide at the foot and 24 at the lip (aaramp4)
        var ends = StructureAttach.Ends([
            Quad(new(0, 8, 45), new(25, 15, 38), new(25, 15, 62), new(0, 8, 55))]);
        Assert.Equal(2, ends.Count);
        var foot = ends.Single(e => e.Low);
        var lip = ends.Single(e => !e.Low);
        Assert.Equal(new Vector3(0, 8, 50), foot.Position);
        Assert.Equal(-1, foot.Direction.X, 3);
        Assert.True(foot.RoadLevel);
        Assert.Equal(7f / 25, foot.Slope, 2);
        Assert.False(lip.RoadLevel);
        Assert.Equal(24, lip.Width, 2);
    }

    [Fact]
    public void ADeckAlongZAndATinyTopAreHandled()
    {
        var ends = StructureAttach.Ends([Quad(new(45, 3, 0), new(55, 3, 0), new(55, 3, 60), new(45, 3, 60))]);
        Assert.Equal(2, ends.Count);
        Assert.Contains(ends, e => e.Direction.Y < -0.99f && e.Position.Z == 0);
        Assert.Empty(StructureAttach.Ends([Quad(new(0, 0, 0), new(3, 0, 0), new(3, 0, 3), new(0, 0, 3))]));
        Assert.Empty(StructureAttach.Ends([]));
    }

    [Fact]
    public void ADiagonalDeckFindsItsAxis()
    {
        var a = MathF.PI / 6;
        var along = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)); var across = new Vector3(-MathF.Sin(a), 0, MathF.Cos(a));
        var o = new Vector3(100, 5, 100);
        var ends = StructureAttach.Ends([Quad(o - across * 5, o - across * 5 + along * 60, o + across * 5 + along * 60, o + across * 5)]);
        Assert.Equal(2, ends.Count);
        var e = ends.Single(x => Vector2.Dot(x.Direction, new Vector2(along.X, along.Z)) < 0);
        Assert.Equal(o.X, e.Position.X, 1); Assert.Equal(o.Z, e.Position.Z, 1);
        Assert.Equal(10, e.Width, 1);
    }
}
