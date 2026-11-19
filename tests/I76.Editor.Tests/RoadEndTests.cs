using System.Numerics;
using I76.Editor;
using I76.Editor.Intersections;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;
using Xunit.Abstractions;

namespace I76.Editor.Tests;

/// <summary>The road tool's road ends (caps, turn-arounds, J strips) and the J strip a dirt road gets on an asphalt junction (docs/map-editor.md, Road ends).</summary>
public class RoadEndTests
{
    private static RoadEdit Line(Vector2 a, Vector2 b, uint kind = 0) => RoadBuilder.Build(RoadCurves.Resample([a, b]), kind);
    private static Vector2 P(float x, float z) => new(x, z);

    private static (RoadStore Roads, PieceStore Pieces, Junctions J) Setup(params RoadEdit[] roads)
    {
        var store = new RoadStore(roads);
        var pieces = new PieceStore();
        store.Pieces = pieces;
        return (store, pieces, new Junctions(store, pieces, _ => 50f));
    }

    private static IEditCommand Draw(RoadStore roads, Junctions j, RoadEdit r)
    {
        var snap = j.Take();
        var cmd = new RoadCommand("Road", roads, [new RoadOp.Insert(roads.Count, r)]).Apply();
        return Junctions.Join("Road", cmd, j.Settle(snap));
    }

    private static IEditCommand Edit(RoadStore roads, Junctions j, Func<IEditCommand> make)
    {
        var snap = j.Take();
        var cmd = make();
        return Junctions.Join("Edit", cmd, j.Settle(snap));
    }

    [Fact]
    public void ADirtRoadsEndTakesADirtCapOnItsEndAndUndoTakesItAway()
    {
        var road = Line(P(500, 500), P(500, 600), 1);
        var (roads, pieces, j) = Setup(road);
        Assert.Equal(RoadEnd.Open, j.EndOf(road, false)!.Value.End);
        Assert.Equal([RoadEnd.Open, RoadEnd.Cap], Junctions.EndChoices(1));
        var cmd = j.SetEnd(road, false, RoadEnd.Cap, out _)!;
        var cap = Assert.Single(pieces.Items);
        Assert.Equal("ijd27t1", cap.Def.Name);
        Assert.Equal(0x52u, cap.Data.ObjectType);
        // the road ends on the middle of the cap's near edge, the cap runs on beyond it
        var arm = cap.Arms.Single();
        Assert.True(Vector2.Distance(arm.Position, P(500, 600)) < 0.01f);
        Assert.True(Vector2.Dot(arm.Out, P(0, -1)) > 0.999f);
        Assert.True(cap.Origin.Y > 600);
        Assert.Equal(RoadEnd.Cap, j.EndOf(road, false)!.Value.End);
        Assert.Equal(RoadEnd.Open, j.EndOf(road, true)!.Value.End);
        cmd.Undo(null!);
        Assert.Empty(pieces.Items);
        cmd.Redo(null!);
        Assert.Single(pieces.Items);
        // back to open
        j.SetEnd(road, false, RoadEnd.Open, out _);
        Assert.Empty(pieces.Items);
        // a dirt road has no turn-around
        Assert.Null(j.SetEnd(road, false, RoadEnd.TurnAround, out string why));
        Assert.Contains("no turn-around", why);
    }

    [Fact]
    public void AnAsphaltRoadsCapIsAJStripAndADirtCapItsTurnAroundIsInTheMapsLook()
    {
        var road = Line(P(500, 500), P(600, 500), 0);
        var (roads, pieces, j) = Setup(road);
        // the map's look: one stock asphalt piece of look 3 elsewhere
        pieces.Insert(0, pieces.Make(PieceCatalogue.Lookup("ita23_1")!, new PiecePose(P(100, 100), 0), 50));
        j.SetEnd(road, true, RoadEnd.Cap, out _);
        var ends = pieces.Items.Skip(1).ToList();
        Assert.Equal(["ija2371", "ijd27t1"], ends.Select(p => p.Def.Name));
        Assert.Equal(0x50u, ends[0].Data.ObjectType);
        // the strip's near edge on the road's end, the cap on the strip's far edge, 20 m on
        Assert.True(Vector2.Distance(ends[0].Arms.First().Position, P(500, 500)) < 0.01f);
        Assert.True(Vector2.Distance(ends[1].Arms.First().Position, P(480, 500)) < 0.01f);
        Assert.Equal(RoadEnd.Cap, j.EndOf(road, true)!.Value.End);
        // the turn-around replaces both
        j.SetEnd(road, true, RoadEnd.TurnAround, out _);
        var turn = Assert.Single(pieces.Items.Skip(1));
        Assert.Equal("ica23_1", turn.Def.Name);
        Assert.True(Vector2.Distance(turn.Arms.Single().Position, P(500, 500)) < 0.01f);
        Assert.True(Vector2.Distance(turn.Origin, P(490, 500)) < 0.01f);                   // the octagon's middle, 10 m on
        // turning to dirt: the strip alone, and its far edge is an arm a road can be drawn on from
        j.SetEnd(road, true, RoadEnd.ToDirt, out _);
        Assert.Equal("ija2371", Assert.Single(pieces.Items.Skip(1)).Def.Name);
        Assert.Contains(j.FreeArms(), a => Vector2.Distance(new Vector2(a.Position.X, a.Position.Z), P(480, 500)) < 0.01f);
        Assert.Equal(RoadEnd.ToDirt, j.EndOf(road, true)!.Value.End);
        // a dirt road drawn on from it: the strip is joined at both ends, the dirt road's end is not an open one
        var dirt = Line(P(480, 500), P(400, 500), 1);
        Draw(roads, j, dirt);
        Assert.Null(j.EndOf(dirt, true));
        Assert.Equal(RoadEnd.ToDirt, j.EndOf(road, true)!.Value.End);
    }

    [Fact]
    public void BulldozingOrRetypingACappedRoadSettlesItsEnd()
    {
        var road = Line(P(500, 500), P(600, 500), 1);
        var (roads, pieces, j) = Setup(road);
        j.SetEnd(road, false, RoadEnd.Cap, out _);
        // to planks: the planks cap
        Edit(roads, j, () => RoadEditing.Retype(null, roads, 0, 2));
        Assert.Equal("ijw29t1", Assert.Single(pieces.Items).Def.Name);
        Assert.Equal(0x53u, pieces[0].Data.ObjectType);
        // to asphalt: a J strip and a dirt cap
        Edit(roads, j, () => RoadEditing.Retype(null, roads, 0, 0));
        Assert.Equal(["ija2171", "ijd27t1"], pieces.Items.Select(p => p.Def.Name));
        Assert.Equal(RoadEnd.Cap, j.EndOf(roads[0], false)!.Value.End);
        // a turn-around retyped to dirt becomes the dirt cap
        j.SetEnd(roads[0], false, RoadEnd.TurnAround, out _);
        Edit(roads, j, () => RoadEditing.Retype(null, roads, 0, 1));
        Assert.Equal("ijd27t1", Assert.Single(pieces.Items).Def.Name);
        // bulldozed: the cap goes with it, and undo brings both back
        j.SetEnd(roads[0], true, RoadEnd.Cap, out _);
        Assert.Equal(2, pieces.Count);
        var step = Edit(roads, j, () => RoadEditing.Bulldoze(null, roads, 0));
        Assert.Empty(pieces.Items); Assert.Equal(0, roads.Count);
        step.Undo(null!);
        Assert.Equal(2, pieces.Count); Assert.Equal(1, roads.Count);
        // an asphalt road's J strip and cap go together
        Edit(roads, j, () => RoadEditing.Retype(null, roads, 0, 0));
        Assert.Equal(4, pieces.Count);
        Edit(roads, j, () => RoadEditing.Bulldoze(null, roads, 0));
        Assert.Empty(pieces.Items);
    }

    [Fact]
    public void AnEndAtAJunctionOrAnotherRoadIsNotOpen()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(roads, j, Line(P(580, 600), P(500, 600)));
        var stem = roads.Roads.First(r => r.Points.Any(p => p.Centre.X > 560));
        bool atStart = Vector2.Distance(stem.Points[0].Centre, P(505, 600)) < 0.1f;
        Assert.Null(j.EndOf(stem, atStart));
        Assert.Null(j.SetEnd(stem, atStart, RoadEnd.TurnAround, out string why));
        Assert.NotEmpty(why);
        Assert.Equal(RoadEnd.Open, j.EndOf(stem, !atStart)!.Value.End);
        // a wide road takes no 10 m piece
        var wide = RoadBuilder.Build(RoadCurves.Resample([P(0, 0), P(0, 100)]), 0, width: 20);
        var (_, _, j2) = Setup(wide);
        Assert.Null(j2.EndOf(wide, true));
    }

    [Fact]
    public void ADirtStemOnAnAsphaltTOrXGetsAJStripAndTheDirtRoadEndsOnItsFarEdge()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(roads, j, Line(P(600, 600), P(500, 600), 1));
        Assert.Equal(["ita21_1", "ija2171"], pieces.Items.Select(p => p.Def.Name));
        var strip = pieces[1];
        var arms = strip.Arms.ToArray();
        Assert.True(Vector2.Distance(arms[0].Position, P(505, 600)) < 0.01f);    // on the T's stem arm
        Assert.True(Vector2.Distance(arms[1].Position, P(525, 600)) < 0.01f);
        var stem = roads.Roads.Single(r => r.Kind == 1);
        Assert.Contains(new[] { stem.Points[0].Centre, stem.Points[^1].Centre }, c => Vector2.Distance(c, P(525, 600)) < 0.02f);
        Assert.Empty(j.FreeArms());
        // bulldozing the dirt road takes the strip, then the T, and the through road is one again
        int si = Enumerable.Range(0, roads.Count).First(i => roads[i].Kind == 1);
        var step = Edit(roads, j, () => RoadEditing.Bulldoze(null, roads, si));
        Assert.Empty(pieces.Items);
        Assert.Equal(1, roads.Count);
        step.Undo(null!);
        Assert.Equal(2, pieces.Count); Assert.Equal(3, roads.Count);
        // an asphalt stem gets none; a dirt road crossing asphalt gets one on each of its arms
        var (r2, p2, j2) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r2, j2, Line(P(580, 600), P(500, 600)));
        Assert.Equal(["ita21_1"], p2.Items.Select(p => p.Def.Name));
        var (r3, p3, j3) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r3, j3, Line(P(400, 600), P(600, 600), 1));
        Assert.Equal(["ixa21_1", "ija2171", "ija2171"], p3.Items.Select(p => p.Def.Name));
        Assert.Equal(2, r3.Roads.Count(r => r.Kind == 1 && new[] { r.Points[0].Centre, r.Points[^1].Centre }.Any(c => MathF.Abs(MathF.Abs(c.X - 500) - 25) < 0.02f)));
        // a dirt stem too short for the strip gets the T alone
        var (r4, p4, j4) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r4, j4, Line(P(530, 600), P(500, 600), 1));
        Assert.Equal(["ita21_1"], p4.Items.Select(p => p.Def.Name));
    }

    [Fact]
    public void RoadEndsAreSavedAndReadBackAsTheRoadToolsPieces()
    {
        var project = MapProject.CreateBlank("rend1", "Road ends", ".cbt", 1, 1, 100);
        var a = Line(P(300, 300), P(300, 400), 0);
        var d = Line(P(400, 300), P(400, 400), 1);
        var w = Line(P(500, 300), P(500, 400), 2);
        var (roads, pieces, j) = Setup(a, d, w);
        j.SetEnd(a, false, RoadEnd.Cap, out _);
        j.SetEnd(a, true, RoadEnd.TurnAround, out _);
        j.SetEnd(d, true, RoadEnd.Cap, out _);
        j.SetEnd(w, false, RoadEnd.Cap, out _);
        roads.Insert(roads.Count, Line(P(0, 0), P(0, 40)));          // the roads dirty, as every edit leaves them
        roads.WriteTo(project.Mission);
        var back = MapDocument.Parse(project.Mission.ToBytes());
        var again = new PieceStore(back.Objects);
        Assert.Equal(pieces.Items.Select(p => (p.Def.Name, p.Data.ObjectType)), again.Items.Select(p => (p.Def.Name, p.Data.ObjectType)));
        var backRoads = new RoadStore([.. back.Roads]);
        var j2 = new Junctions(backRoads, again, _ => 50f);
        Assert.Equal(RoadEnd.Cap, j2.EndOf(backRoads[0], false)!.Value.End);
        Assert.Equal(RoadEnd.TurnAround, j2.EndOf(backRoads[0], true)!.Value.End);
        Assert.Equal(RoadEnd.Cap, j2.EndOf(backRoads[1], true)!.Value.End);
        Assert.Equal(RoadEnd.Cap, j2.EndOf(backRoads[2], false)!.Value.End);
    }

    [Fact]
    public void TheGroundUnderAnEndPieceTakesThePiecesSurface()
    {
        var doc = RoadEndDocs.Flat();
        var road = Line(P(300, 100), P(300, 200), 1);
        var (roads, pieces, j) = Setup(road);
        j.LevelDoc = doc;
        var cmd = j.SetEnd(road, false, RoadEnd.Cap, out _, conform: false)!;
        int Nib(int sx, int sz) => doc.GetSample(doc.Tiles.TileAt(sz / TerrainTiles.Tile, sx / TerrainTiles.Tile), (sz % TerrainTiles.Tile) * TerrainTiles.Tile + sx % TerrainTiles.Tile) >> 12;
        // the cap covers x 295-305, z 200-210
        Assert.Equal(0xe, Nib(60, 41));
        Assert.Equal(0, Nib(60, 44));
        cmd.Undo(doc);
        Assert.Equal(0, Nib(60, 41));
        var road2 = Line(P(600, 100), P(600, 200), 0);
        var (_, _, j2) = Setup(road2);
        j2.LevelDoc = doc;
        j2.SetEnd(road2, false, RoadEnd.TurnAround, out _, conform: false);
        Assert.Equal(0xc, Nib(120, 42));                                  // the turn-around's middle, 10 m on
    }
}

/// <summary>Stock maps' road ends against the rules (needs I76_INSTALL / I76_NITRO_INSTALL; skipped silently without).</summary>
public class StockRoadEndTests(ITestOutputHelper output)
{
    private static IEnumerable<string> Missions()
    {
        foreach (var env in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } a)
                foreach (var dir in new[] { "miss16", "miss8" }.Select(d => Path.Combine(a, d)).Where(Directory.Exists))
                    foreach (var f in Directory.EnumerateFiles(dir).Where(MapFileSet.IsStockMission).Order())
                        yield return f;
    }

    /// <summary>
    /// Every stock cap, turn-around and J strip with a road ending on its near edge, and every J strip on an asphalt T's or X's arm with its
    /// dirt road: the road tool's end choice (or junction rule) for that road puts the same piece in the same place, turned the same way.
    /// </summary>
    [Fact]
    public void TheRoadEndChoicePutsThePiecesWhereTheStockMapsHaveThem()
    {
        var tried = new Dictionary<string, (int Tried, int Ok)>();
        var misses = new List<string>();
        void Count(string what, bool ok, string miss)
        {
            var (t, o) = tried.GetValueOrDefault(what);
            tried[what] = (t + 1, o + (ok ? 1 : 0));
            if (!ok && misses.Count < 16) misses.Add(miss);
        }
        foreach (var f in Missions())
        {
            var doc = MapDocument.Load(f);
            var store = new PieceStore(doc.Objects);
            var roads = new RoadStore([.. doc.Roads.Where(r => r.Points.Count >= 3)]);
            var j = new Junctions(roads, store, _ => 0);
            foreach (var p in store.Items.Where(p => p.Def.IsEnd))
            {
                var arm = p.Arms.First();
                // the road whose end is on the piece's near edge, heading into the road
                var hit = roads.Roads.SelectMany(r => new[] { (r, true), (r, false) })
                    .Where(e => Poly.Of(e.r).Length >= 12 && Vector2.Distance(Junctions.EndFrame(e.r, e.Item2).At, arm.Position) <= 1.5f && Vector2.Dot(Junctions.EndFrame(e.r, e.Item2).Body, arm.Out) > 0.95f).ToList();
                if (hit.Count != 1) continue;
                var (road, atStart) = hit[0];
                RoadEnd want = p.Def.Shape switch
                {
                    PieceShape.TurnAround => RoadEnd.TurnAround,
                    PieceShape.Cap => RoadEnd.Cap,
                    _ => store.Items.Any(c => c.Def.Shape == PieceShape.Cap && Vector2.Distance(c.Arms.First().Position, p.Arms.Last().Position) < 1.5f) ? RoadEnd.Cap : RoadEnd.ToDirt,
                };
                // a stock cap on an asphalt road with no strip, a turn-around on dirt: the rules give those roads other pieces
                if (!Junctions.EndChoices(road.Kind).Contains(want) || p.Def.Shape is PieceShape.Cap or PieceShape.Strip && (road.Kind % 3 == 0) != (p.Def.Shape == PieceShape.Strip)) continue;
                var made = j.EndPieces(road, atStart, want, p.Def.Variant);
                var mine = made.FirstOrDefault();
                string what = $"{p.Def.Shape} on {RoadBuilder.KindName(road.Kind).ToLowerInvariant()}";
                float dyaw = mine is null ? 99 : MathF.Abs(PiecePose.Wrap(mine.Pose.Yaw - p.Pose.Yaw)) * 180 / MathF.PI;
                // the octagon is the same turned half round (stock roads meet either of its two edges)
                bool sym = p.Def.Shape == PieceShape.TurnAround && dyaw > 175;
                float dpos = mine is null ? 99 : sym ? Vector2.Distance(mine.Pose.ToWorld(mine.Def.Origin), p.Origin) : Vector2.Distance(mine.Pose.Origin, p.Pose.Origin);
                bool ok = mine is not null && mine.Def.Shape == p.Def.Shape && dpos < 1f && (dyaw < 3 || sym);
                Count(what, ok, $"{Path.GetFileName(f)} {p.Def.Name} at ({p.Origin.X:0},{p.Origin.Y:0}): {(mine is null ? "none" : $"{mine.Def.Name} off {dpos:0.0} m, {dyaw:0} degrees")}");
            }
            // J strips on an asphalt T's stem: the T taken apart (the through road joined, the dirt road run on to the junction) and the rules asked
            var ends = roads.Roads.SelectMany(r => new[] { (r, true), (r, false) }).Where(e => Poly.Of(e.r).Length >= 8).ToList();
            foreach (var t in store.Items.Where(p => p.Def.Shape == PieceShape.T && p.Def.Kind == 0))
            {
                var arms = t.Arms.ToArray();
                (RoadEdit Road, bool AtStart)? At((Vector2 Position, Vector2 Out) a)
                {
                    var h = ends.Where(e => Vector2.Distance(Junctions.EndFrame(e.r, e.Item2).At, a.Position) <= 1.5f && Vector2.Dot(Junctions.EndFrame(e.r, e.Item2).Body, a.Out) > 0.9f).ToList();
                    return h.Count == 1 ? h[0] : null;
                }
                var strip = store.Items.FirstOrDefault(s => s.Def.Shape == PieceShape.Strip && Vector2.Distance(s.Arms.First().Position, arms[2].Position) < 1.5f);
                if (strip is null || At(arms[0]) is not { } a0 || At(arms[1]) is not { } a1 || At(strip.Arms.Last()) is not { } a2 || a2.Road.Kind % 3 != 1) continue;
                var ca = RoadBuilder.Centres(a0.Road); if (a0.AtStart) ca.Reverse();
                var cb = RoadBuilder.Centres(a1.Road); if (!a1.AtStart) cb.Reverse();
                var q = RoadBuilder.Build([.. ca, .. cb], a0.Road.Kind);
                var c2 = RoadBuilder.Centres(a2.Road); if (a2.AtStart) c2.Reverse();
                c2.Add(t.Origin);
                var r = RoadBuilder.Build(c2, a2.Road.Kind);
                var prop = new Junctions(new RoadStore([q, r]), new PieceStore(), _ => 0).Detect(r, null, t.Def.Variant).FirstOrDefault();
                var x = prop?.Extras.FirstOrDefault();
                float dyaw = x is null ? 99 : MathF.Abs(PiecePose.Wrap(x.Pose.Yaw - strip.Pose.Yaw)) * 180 / MathF.PI;
                float dpos = x is null ? 99 : Vector2.Distance(x.Pose.ToWorld(x.Def.Origin), strip.Origin);
                Count("Strip on a T's stem", x is not null && dpos < 3 && dyaw < 5, $"{Path.GetFileName(f)} {strip.Def.Name} on {t.Def.Name} at ({strip.Origin.X:0},{strip.Origin.Y:0}): {(x is null ? $"none ({prop?.Describe ?? "no piece"})" : $"off {dpos:0.0} m, {dyaw:0} degrees")}");
            }
        }
        foreach (var (k, v) in tried.OrderBy(k => k.Key)) output.WriteLine($"{k}: {v.Ok} of {v.Tried} as the stock map has it");
        foreach (var m in misses) output.WriteLine("  " + m);
        if (tried.Count == 0) return;
        foreach (var (k, v) in tried) Assert.True(v.Ok >= v.Tried * 0.95, $"{k}: {v.Ok}/{v.Tried}");
    }
}

internal static class RoadEndDocs
{
    public static TerrainDocument Flat(int height = 500)
    {
        const int T = TerrainTiles.Tile;
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;
        var ter = new byte[4 * T * T * 2];
        for (int i = 0; i < 4 * T * T; i++) { ter[i * 2] = (byte)height; ter[i * 2 + 1] = (byte)(height >> 8); }
        return new TerrainDocument(new TerrainTiles(ter, 4, grid), "test");
    }
}

/// <summary>The palette's ROAD PIECES: which names it has, and how they are placed (quarter turns, centred on a road along them, the corner on the grid).</summary>
public class HandPieceTests
{
    private static I76.Editor.Objects.CatalogueItem Item(string name, float w, float d) =>
        new() { Name = name, Category = I76.Editor.Objects.ObjectCategory.RoadPieces, Type = 0x50, MinX = 0, MaxX = w, MinZ = 0, MaxZ = d, MaxY = 0.01f };

    [Fact]
    public void TheHandPlacedPiecesAreTheRoadPiecesTabAndTheRoadToolsAreNot()
    {
        foreach (var n in new[] { "rmrwmid1", "rerwend4", "ima21_1", "ima23_2", "iga22_2", "ila21_2", "iyd27_1" })
            Assert.Equal(I76.Editor.Objects.ObjectCategory.RoadPieces, I76.Editor.Objects.ObjectCatalogue.Classify(n, n == "iyd27_1" ? 0x52u : 0x50u, false, 10, 10));
        foreach (var n in new[] { "iyisect8", "idisect1", "ita21_1", "ica21_1", "ijd27t1", "ijd28t1" })
            Assert.Null(I76.Editor.Objects.ObjectCatalogue.Classify(n, 0x52, false, 10, 10));
        Assert.Equal(0x52u, I76.Editor.Objects.ObjectCatalogue.HandPieces["iyd27_1"]);
        Assert.All(I76.Editor.Objects.ObjectCatalogue.HandPieces.Keys, n => Assert.Null(PieceCatalogue.Lookup(n)));
    }

    [Fact]
    public void ARoadPieceTurnsByQuarterTurnsCentresOnARoadAlongItAndPutsItsCornerOnTheGrid()
    {
        var runway = Item("rmrwmid1", 20, 200);
        var free = I76.Editor.Objects.Snapping.Place(runway, new Vector2(103, 251), 37 * MathF.PI / 180, I76.Editor.Objects.SnapMode.Grid, 5, null);
        Assert.Equal(0, free.Yaw, 4);
        Assert.Equal(0, free.Origin.X % 5, 3); Assert.Equal(0, free.Origin.Y % 5, 3);
        Assert.Equal(MathF.PI / 2, I76.Editor.Objects.Snapping.StepFor(runway), 5);
        // a road along x: a start line turned to run along it sits on its centre line
        var road = RoadBuilder.Build(RoadCurves.Resample([new Vector2(0, 500), new Vector2(400, 500)]), 0);
        var lines = I76.Editor.Objects.RoadGeometry.Build([road]);
        var start = Item("ima21_1", 10, 10);
        var on = I76.Editor.Objects.Snapping.Place(start, new Vector2(203, 504), MathF.PI / 2, I76.Editor.Objects.SnapMode.Roads, 5, lines);
        Assert.True(on.OnRoad);
        var centre = on.Footprint(start).Centre;
        Assert.Equal(500, centre.Y, 2); Assert.Equal(203, centre.X, 1);
        // a road at a slant: the grid
        var slant = RoadBuilder.Build(RoadCurves.Resample([new Vector2(0, 0), new Vector2(300, 300)]), 0);
        var off = I76.Editor.Objects.Snapping.Place(start, new Vector2(150, 150), 0, I76.Editor.Objects.SnapMode.Roads, 5, I76.Editor.Objects.RoadGeometry.Build([slant]));
        Assert.False(off.OnRoad);
    }
}
