using System.Numerics;
using I76.Editor;
using I76.Editor.Intersections;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;
using Xunit.Abstractions;

namespace I76.Editor.Tests;

public class IntersectionRuleTests
{
    private static Vector2 Dir(float deg) => new(MathF.Sin(deg * MathF.PI / 180), MathF.Cos(deg * MathF.PI / 180));

    [Theory]
    [InlineData(90, PieceShape.T)]
    [InlineData(-90, PieceShape.T)]
    [InlineData(80, PieceShape.T)]
    [InlineData(104, PieceShape.T)]
    [InlineData(41, PieceShape.ForkRight)]
    [InlineData(48, PieceShape.ForkRight)]
    [InlineData(-41, PieceShape.ForkLeft)]
    [InlineData(-35, PieceShape.ForkLeft)]
    [InlineData(139, PieceShape.ForkLeft)]    // a stem leaving backward at 41 off the road is a fork the other way along it
    [InlineData(-139, PieceShape.ForkRight)]
    public void AStemsAngleToTheThroughRoadPicksTheShape(float stemDegrees, PieceShape shape)
    {
        // the through road runs +z; the stem leaves the junction at the given heading
        var c = JunctionRules.Classify(Dir(0), Dir(stemDegrees));
        Assert.NotNull(c);
        Assert.Equal(shape, c!.Value.Shape);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(25)]
    [InlineData(60)]
    [InlineData(65)]
    [InlineData(170)]
    public void OtherAnglesGetNoPiece(float stemDegrees) => Assert.Null(JunctionRules.Classify(Dir(0), Dir(stemDegrees)));

    [Fact]
    public void ForksLeanForwardAlongTheThroughDirectionAndTheSideDecidesRightOrLeft()
    {
        var c = JunctionRules.Classify(Dir(180), Dir(41))!.Value;        // road given the other way: the through direction turns to lean with the branch
        Assert.True(Vector2.Dot(c.Through, Dir(41)) > 0);
        Assert.Equal(PieceShape.ForkRight, c.Shape);                      // facing the way it leans (north), a branch to the north-east is on the right
    }

    [Fact]
    public void CrossingsMustBeSquareEnough()
    {
        Assert.True(JunctionRules.CrossingIsSquare(Dir(0), Dir(90)));
        Assert.True(JunctionRules.CrossingIsSquare(Dir(0), Dir(-80)));
        Assert.False(JunctionRules.CrossingIsSquare(Dir(0), Dir(60)));
        Assert.False(JunctionRules.CrossingIsSquare(Dir(0), Dir(170)));
    }

    [Fact]
    public void ThePieceChosenFollowsTheRoadKind()
    {
        Assert.Equal("ita21_1", PieceCatalogue.Pick(PieceShape.T, 0)!.Name);
        Assert.Equal("ita23_1", PieceCatalogue.Pick(PieceShape.T, 3, 3)!.Name);        // kind % 3, and the map's look
        Assert.Equal("itd27_1", PieceCatalogue.Pick(PieceShape.T, 1)!.Name);
        Assert.Equal("ixd27_1", PieceCatalogue.Pick(PieceShape.X, 4)!.Name);
        Assert.Null(PieceCatalogue.Pick(PieceShape.T, 2));                             // the game has no planks T or X
        Assert.Null(PieceCatalogue.Pick(PieceShape.X, 2));
        Assert.Equal("igw29_1", PieceCatalogue.Pick(PieceShape.ForkRight, 2)!.Name);
        Assert.Equal("ilw29_1", PieceCatalogue.Pick(PieceShape.ForkLeft, 2)!.Name);
        Assert.Equal(PieceShape.T, PieceCatalogue.Lookup("ITISECT1")!.Shape);          // the library names are the same pieces
        Assert.Equal(PieceShape.ForkLeft, PieceCatalogue.Lookup("#iyisect5")!.Shape);
        Assert.Null(PieceCatalogue.Lookup("ija2171"));
        Assert.Equal(0x52u, PieceCatalogue.Pick(PieceShape.T, 1)!.ObjectType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(90)]
    [InlineData(-155)]
    public void APiecesArmsLandOnTheRoadsItWasFittedTo(float throughDegrees)
    {
        var through = Dir(throughDegrees);
        var centre = new Vector2(1234.5f, -765.25f);
        foreach (var shape in new[] { PieceShape.T, PieceShape.X, PieceShape.ForkRight, PieceShape.ForkLeft })
        {
            var def = PieceCatalogue.Pick(shape, 0)!;
            var stem = shape == PieceShape.T ? new Vector2(through.Y, -through.X) : shape == PieceShape.X ? through : Dir(throughDegrees + (shape == PieceShape.ForkRight ? 41 : -41));
            var pose = JunctionRules.Fit(def, centre, through, stem);
            var arms = JunctionRules.WorldArms(def, pose);
            // the junction point is at the centre, the through arms are on the through line, facing apart
            Assert.Equal(0, Vector2.Distance(pose.ToWorld(def.Origin), centre), 3);
            var a0 = arms[0]; var a1 = arms[1];
            Assert.InRange(MathF.Abs(Cross(through, a0.Position - centre)), 0, 1e-2f);
            Assert.InRange(MathF.Abs(Cross(through, a1.Position - centre)), 0, 1e-2f);
            Assert.True(Vector2.Dot(a0.Out, a1.Out) < -0.999f);
            Assert.InRange(MathF.Abs(Vector2.Dot(a0.Out, through)), 0.999f, 1.001f);
            if (shape == PieceShape.T)
            {
                // the stem arm faces the side the stem is on, its edge 5 from the centre, square to the road
                Assert.True(Vector2.Dot(arms[2].Out, stem) > 0.999f);
                Assert.Equal(5, Vector2.Distance(arms[2].Position, centre), 3);
            }
            if (shape == PieceShape.X) { Assert.Equal(5, Vector2.Distance(arms[3].Position, centre), 3); Assert.True(Vector2.Dot(arms[2].Out, arms[3].Out) < -0.999f); }
            if (shape is PieceShape.ForkRight or PieceShape.ForkLeft) Assert.True(Vector2.Dot(arms[2].Out, stem) > 0.995f);
        }
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    [Fact]
    public void TheEnginesFrameIsTheOneThePoseAssumes()
    {
        // MapObject's yaw: forward (local +z) is (sin, cos), right (local +x) is (cos, -sin): through the loader's own transform (rows are the axes)
        foreach (float yaw in new[] { 0f, 0.7f, MathF.PI / 2, -2.2f })
        {
            var o = MapObject.Create("ita21_1", 0x50, new Vector3(100, 0, 200), yaw);
            var a = I76.Formats.Assembly.Affine.FromEngine(o.Rotation, o.Position);
            var p = new PiecePose(new Vector2(100, 200), yaw);
            foreach (var local in new[] { new Vector2(5, 0), new Vector2(10, 5), new Vector2(3, 8) })
            {
                var w = a.Apply(new Vector3(local.X, 0, local.Y));
                Assert.Equal(p.ToWorld(local).X, w.X, 3);
                Assert.Equal(p.ToWorld(local).Y, w.Z, 3);
            }
        }
    }
}

public class JunctionTests
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

    /// <summary>Draws a road the way the tool does (the store gets it) and settles it; the whole as one command.</summary>
    private static (IEditCommand Cmd, RoadEdit Road) Draw(RoadStore roads, Junctions j, RoadEdit r)
    {
        var snap = j.Take();
        var cmd = new RoadCommand("Road", roads, [new RoadOp.Insert(roads.Count, r)]).Apply();
        return (Junctions.Join("Road", cmd, j.Settle(snap)), r);
    }

    private static IEnumerable<(Vector2 At, Vector2 Body)> Ends(RoadStore s) => s.Roads.SelectMany(r => new[]
    {
        (r.Points[0].Centre, Vector2.Normalize(r.Points[1].Centre - r.Points[0].Centre)),
        (r.Points[^1].Centre, Vector2.Normalize(r.Points[^2].Centre - r.Points[^1].Centre)),
    });

    private static void AssertArmsMet(RoadStore s, PlacedPiece p, params int[] arms)
    {
        var ends = Ends(s).ToList();
        var a = p.Arms.ToArray();
        foreach (int i in arms)
            Assert.Contains(ends, e => Vector2.Distance(e.At, a[i].Position) < 0.02f && Vector2.Dot(e.Body, a[i].Out) > 0.999f);
    }

    [Fact]
    public void ARoadEndingOnTheMiddleOfAnotherMakesATJunctionAndTheRoadsMeetItsArms()
    {
        var q = Line(P(500, 500), P(500, 700));
        var (roads, pieces, j) = Setup(q);
        var (cmd, _) = Draw(roads, j, Line(P(580, 600), P(500, 600)));
        Assert.Single(pieces.Items);
        var t = pieces[0];
        Assert.Equal("ita21_1", t.Def.Name);
        Assert.Equal(0x50u, t.Data.ObjectType);
        Assert.Equal(50f, t.Data.Position.Y);                       // at the ground's height
        Assert.Equal(3, roads.Count);                                // the through road is cut in two, the stem is cut short
        AssertArmsMet(roads, t, 0, 1, 2);
        Assert.Equal(600f, t.Origin.Y, 2); Assert.Equal(500f, t.Origin.X, 2);
        // the stem's end is at the arm, 5 m from the through road's centre line, and square to it
        Assert.Equal(1, roads.Roads.Count(r => RoadBuilder.Centres(r).Any(c => Vector2.Distance(c, P(505, 600)) < 0.02f)));
        // one undo step takes all of it back, to the very roads there were
        var undo = new UndoStack(null!);
        undo.Push(cmd);
        cmd.Undo(null!);
        Assert.Equal(1, roads.Count); Assert.Same(q, roads[0]); Assert.Empty(pieces.Items);
        cmd.Redo(null!);
        Assert.Equal(3, roads.Count); Assert.Single(pieces.Items);
    }

    [Fact]
    public void TheStemMayComeFromEitherSideAndEitherEndOfTheDrawnRoad()
    {
        foreach (bool fromStem in new[] { true, false })
        {
            var q = Line(P(500, 500), P(500, 700));
            var (roads, pieces, j) = Setup(q);
            // drawn from the through road out (the start on it) or toward it
            var r = fromStem ? Line(P(500, 600), P(420, 600)) : Line(P(420, 600), P(500, 600));
            Draw(roads, j, r);
            Assert.Single(pieces.Items);
            AssertArmsMet(roads, pieces[0], 0, 1, 2);
            Assert.True(roads.Roads.All(x => RoadBuilder.Centres(x).All(c => Vector2.Distance(c, P(500, 600)) > 4.9f)), "no road runs over the piece's middle");
        }
    }

    [Fact]
    public void TwoRoadsCrossingMakeAnXAndEachIsCutTwice()
    {
        var q = Line(P(500, 500), P(500, 700));
        var (roads, pieces, j) = Setup(q);
        var (cmd, _) = Draw(roads, j, Line(P(420, 600), P(580, 600)));
        Assert.Single(pieces.Items);
        Assert.Equal("ixa21_1", pieces[0].Def.Name);
        Assert.Equal(4, roads.Count);
        AssertArmsMet(roads, pieces[0], 0, 1, 2, 3);
        cmd.Undo(null!);
        Assert.Equal(1, roads.Count); Assert.Empty(pieces.Items);
    }

    [Fact]
    public void ADirtRoadGetsTheDirtPieceAndTheMapsAsphaltLookIsKept()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700), 1));
        Draw(roads, j, Line(P(580, 600), P(500, 600), 1));
        Assert.Equal("itd27_1", pieces[0].Def.Name);
        Assert.Equal(0x52u, pieces[0].Data.ObjectType);
        // a map whose pieces have the 3rd asphalt look gets it for new ones
        var (r2, p2, j2) = Setup(Line(P(500, 500), P(500, 700)), Line(P(900, 500), P(900, 700)));
        p2.Insert(0, p2.Make(PieceCatalogue.Lookup("ixa23_1")!, new PiecePose(new Vector2(0, 0), 0), 0));
        Draw(r2, j2, Line(P(580, 600), P(500, 600)));
        Assert.Equal("ita23_1", p2.Items.Last().Def.Name);
    }

    [Fact]
    public void ABranchAtFortyOneDegreesMakesAForkOnTheSideItLeansTo()
    {
        foreach (var (stemDeg, shape) in new[] { (41f, PieceShape.ForkRight), (-41f, PieceShape.ForkLeft) })
        {
            var q = Line(P(500, 500), P(500, 700));
            var (roads, pieces, j) = Setup(q);
            var stem = P(500, 600) + new Vector2(MathF.Sin(stemDeg * MathF.PI / 180), MathF.Cos(stemDeg * MathF.PI / 180)) * 90;
            Draw(roads, j, Line(stem, P(500, 600)));
            Assert.Single(pieces.Items);
            Assert.Equal(shape, pieces[0].Def.Shape);
            AssertArmsMet(roads, pieces[0], 0, 1, 2);
        }
    }

    [Fact]
    public void OddAnglesShortStemsAndPlanksGetNoPiece()
    {
        // 65 degrees off the road: neither square nor a fork
        var (r1, p1, j1) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r1, j1, Line(P(500 + 90 * MathF.Sin(1.13f), 600 + 90 * MathF.Cos(1.13f)), P(500, 600)));
        Assert.Empty(p1.Items); Assert.Equal(2, r1.Count);
        // a stem of 8 m has no room for the lead-in beyond the arm
        var (r2, p2, j2) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r2, j2, Line(P(508, 600), P(500, 600)));
        Assert.Empty(p2.Items);
        // no planks T in the game
        var (r3, p3, j3) = Setup(Line(P(500, 500), P(500, 700), 2));
        Draw(r3, j3, Line(P(580, 600), P(500, 600), 2));
        Assert.Empty(p3.Items); Assert.Equal(2, r3.Count);
        // a road that merely ends at another's end is an extension, not a junction
        var (r4, p4, j4) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r4, j4, Line(P(500, 700), P(580, 700)));
        Assert.Empty(p4.Items);
    }

    [Fact]
    public void BulldozingTheStemDissolvesTheJunctionAndTheThroughRoadIsOneAgain()
    {
        var q = Line(P(500, 500), P(500, 700));
        var (roads, pieces, j) = Setup(q);
        Draw(roads, j, Line(P(580, 600), P(500, 600)));
        int stem = Enumerable.Range(0, roads.Count).First(i => RoadBuilder.Centres(roads[i]).Any(c => Vector2.Distance(c, P(505, 600)) < 0.02f) && roads[i].Points.Any(p => p.Centre.X > 540));
        var snap = j.Take();
        var bull = RoadEditing.Bulldoze(null, roads, stem);
        var follow = j.Settle(snap);
        Assert.NotNull(follow);
        Assert.Empty(pieces.Items);
        Assert.Equal(1, roads.Count);
        var c = RoadBuilder.Centres(roads[0]);
        Assert.Equal(500, c[0].X, 2); Assert.Equal(700, MathF.Max(c[0].Y, c[^1].Y), 2); Assert.Equal(500, MathF.Min(c[0].Y, c[^1].Y), 2);
        Assert.All(c, p => Assert.Equal(500, p.X, 2));                    // straight through where the piece was
        var step = Junctions.Join("Bulldoze", bull, follow);
        step.Undo(null!);
        Assert.Equal(3, roads.Count); Assert.Single(pieces.Items);
        step.Redo(null!);
        Assert.Equal(1, roads.Count); Assert.Empty(pieces.Items);
    }

    [Fact]
    public void BulldozingOneRoadOfAnXLeavesATOfTheThreeThatAreLeft()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(roads, j, Line(P(420, 600), P(580, 600)));
        Assert.Equal("ixa21_1", pieces[0].Def.Name);
        int east = Enumerable.Range(0, roads.Count).First(i => roads[i].Points.Any(p => p.Centre.X > 560));
        var snap = j.Take();
        RoadEditing.Bulldoze(null, roads, east);
        j.Settle(snap);
        Assert.Single(pieces.Items);
        Assert.Equal(PieceShape.T, pieces[0].Def.Shape);
        Assert.Equal(pieces[0].Id, 1 + 0 * pieces[0].Id);            // the same place in the piece list
        // its arms are the three that still have a road
        AssertArmsMet(roads, pieces[0], 0, 1, 2);
    }

    [Fact]
    public void RetypingTheThroughRoadChangesThePieceToTheNewKind()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(roads, j, Line(P(580, 600), P(500, 600)));
        var snap = j.Take();
        for (int i = 0; i < roads.Count; i++) if (roads[i].Points.All(p => MathF.Abs(p.Centre.X - 500) < 0.1f)) RoadEditing.Retype(null, roads, i, 1);
        j.Settle(snap);
        Assert.Equal("itd27_1", pieces[0].Def.Name);
        var snap2 = j.Take();
        for (int i = 0; i < roads.Count; i++) if (roads[i].Points.All(p => MathF.Abs(p.Centre.X - 500) < 0.1f)) RoadEditing.Retype(null, roads, i, 2);
        j.Settle(snap2);
        Assert.Empty(pieces.Items);                                    // planks have no T
    }

    [Fact]
    public void MovingTheStemsEndOffTheArmTakesThePieceAway()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(roads, j, Line(P(580, 600), P(500, 600)));
        int stem = Enumerable.Range(0, roads.Count).First(i => roads[i].Points.Any(p => p.Centre.X > 560));
        var snap = j.Take();
        var road = roads[stem];
        int end = Vector2.Distance(road.Points[0].Centre, P(505, 600)) < 0.1f ? 0 : road.Points.Count - 1;
        var moved = RoadOps.MovePoint(road, end, P(505, 640), 0);
        roads.Replace(stem, moved);
        j.Settle(snap);
        Assert.Empty(pieces.Items);
        Assert.Equal(1, roads.Roads.Count(r => r.Points.Any(p => p.Centre.Y > 690) && r.Points.Any(p => p.Centre.Y < 510)));    // the through road is whole again
    }

    [Fact]
    public void FreeArmsAreOfferedAsEndsToSnapTo()
    {
        var (roads, pieces, j) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(roads, j, Line(P(580, 600), P(500, 600)));
        Assert.Empty(j.FreeArms());
        // an X where a road was bulldozed away gives its free arm
        var (r2, p2, j2) = Setup(Line(P(500, 500), P(500, 700)));
        Draw(r2, j2, Line(P(420, 600), P(580, 600)));
        int east = Enumerable.Range(0, r2.Count).First(i => r2[i].Points.Any(p => p.Centre.X > 560));
        var s = j2.Take();
        RoadEditing.Bulldoze(null, r2, east);
        j2.Settle(s);
        Assert.Empty(j2.FreeArms());                                 // it became a T, nothing dangling
        // a lone piece is all free arms
        var p3 = new PieceStore();
        p3.Insert(0, p3.Make(PieceCatalogue.Lookup("ita21_1")!, new PiecePose(new Vector2(0, 0), 0), 10));
        var arms = new Junctions(new RoadStore([]), p3, _ => 10f).FreeArms();
        Assert.Equal(3, arms.Count);
        Assert.All(arms, a => { Assert.True(a.RoadLevel); Assert.Equal(10f, a.Width); Assert.Equal(10f, a.Position.Y); });
        Assert.Contains(arms, a => Vector2.Distance(new Vector2(a.Position.X, a.Position.Z), P(5, 0)) < 1e-3f && Vector2.Dot(a.Direction, new Vector2(0, -1)) > 0.999f);
    }

    private static TerrainDocument FlatDoc(int height = 500)
    {
        const int T = TerrainTiles.Tile;
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;     // four tiles in a row: x 0 to 2560 m, z 0 to 640
        var ter = new byte[4 * T * T * 2];
        for (int i = 0; i < 4 * T * T; i++) { ter[i * 2] = (byte)height; ter[i * 2 + 1] = (byte)(height >> 8); }
        return new TerrainDocument(new TerrainTiles(ter, 4, grid), "test");
    }

    [Fact]
    public void ThePieceGivesTheGroundUnderItTheStockIntersectionSurfaceAndUndoTakesItBack()
    {
        var doc = FlatDoc();
        var (roads, pieces, j) = Setup(Line(P(300, 100), P(300, 300)));
        j.LevelDoc = doc;
        var snap = j.Take();
        var cmd0 = new RoadCommand("Road", roads, [new RoadOp.Insert(roads.Count, Line(P(380, 200), P(300, 200)))]).Apply();
        var follow = j.Settle(snap, conform: false)!;
        var cmd = Junctions.Join("Road", cmd0, follow);
        int Nib(int sx, int sz) => doc.GetSample(doc.Tiles.TileAt(sz / TerrainTiles.Tile, sx / TerrainTiles.Tile), (sz % TerrainTiles.Tile) * TerrainTiles.Tile + sx % TerrainTiles.Tile) >> 12;
        // the piece covers x 295-305, z 195-205: the samples (5 m apart) on or inside it
        int n = 0;
        for (int sz = 36; sz <= 46; sz++) for (int sx = 56; sx <= 66; sx++)
        {
            float x = sx * 5f, z = sz * 5f;
            // the engine reads the nearest sample under a point, so the samples nearest the footprint's points (the 3 x 3 of them) carry it
            bool inside = x >= 295 && x <= 305 && z >= 195 && z <= 205;
            Assert.Equal(inside ? 0xc : 0, Nib(sx, sz));
            if (inside) n++;
        }
        Assert.Equal(9, n);
        Assert.Equal(500, doc.GetHeight(60, 40));            // heights untouched (conform off)
        cmd.Undo(doc);
        for (int sz = 36; sz <= 46; sz++) for (int sx = 56; sx <= 66; sx++) Assert.Equal(0, Nib(sx, sz));
        cmd.Redo(doc);
        Assert.Equal(0xc, Nib(60, 40));   // x 300, z 200
        // dirt is 0xe
        Assert.Equal(0xe, Junctions.SurfaceNibble(1));
        Assert.Equal(0xe, Junctions.SurfaceNibble(2));
    }

    [Fact]
    public void ThePiecesOfASavedMapComeBackAsTheyWere()
    {
        var project = MapProject.CreateBlank("isect1", "Isect test", ".cbt", 1, 1, 100);
        var (roads, pieces, j) = Setup(Line(P(300, 300), P(300, 500)));
        var snapshot = j.Take();
        Draw(roads, j, Line(P(380, 400), P(300, 400)));
        Draw(roads, j, Line(P(300, 440), P(380, 440)));
        _ = snapshot;
        Assert.Equal(2, pieces.Count);
        Assert.Equal(5, roads.Count);
        roads.WriteTo(project.Mission);
        var back = MapDocument.Parse(project.Mission.ToBytes());
        Assert.Equal(5, back.Roads.Count);
        var again = new PieceStore(back.Objects);
        Assert.Equal(2, again.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(pieces[i].Def.Name, again[i].Def.Name);
            Assert.True(again[i].Origin.X > 0);
            Assert.Equal(pieces[i].Data.Position, again[i].Data.Position);
            Assert.Equal(pieces[i].Data.Yaw, again[i].Data.Yaw, 4);
            Assert.Equal(pieces[i].Data.ObjectType, again[i].Data.ObjectType);
        }
        // the objects the file had stay, in place, and the pieces made here are at the end; saving again is stable
        var objs = project.Mission.Objects.Select(o => o.Name).ToList();
        Assert.Equal("ita21_1", objs[^2]); Assert.Equal("ita21_1", objs[^1]);
        // reading the pieces back and changing one keeps its slot: removing it from the store removes it from the file
        again.RemoveAt(0);
        again.WriteTo(back);
        Assert.Equal(1, back.Objects.Count(o => o.Name == "ita21_1"));
    }
}

public class IntersectionWithObjectsTests
{
    [Fact]
    public void TheObjectLayerAndThePieceStoreWriteTheFileTogetherWithoutDuplicatesOrLostPieces()
    {
        var project = MapProject.CreateBlank("isect2", "Isect two", ".cbt", 1, 1, 100);
        var stock = MapObject.Create("ita21_1", 0x50, new Vector3(100, 10, 100), 0);
        var after = MapObject.Create("afence1", 3, new Vector3(50, 10, 50), 0);
        project.Mission.Objects.AddRange([stock, MapObject.Create("regen", 1, new Vector3(5, 10, 5), 0), after]);
        // the editor: the layer holds every object, the piece store the pieces
        var roads = new RoadStore([]);
        var layer = new I76.Editor.Objects.ObjectLayer(project.Mission, _ => null, roads);
        var pieces = new PieceStore(project.Mission.Objects);
        roads.Pieces = pieces;
        Assert.Single(pieces.Items);
        // the road tool dissolves the stock piece and places another elsewhere (in one edit)
        var old = pieces[0];
        pieces.RemoveAt(0);
        pieces.Insert(0, pieces.Make(PieceCatalogue.Lookup("ixd27_1")!, new PiecePose(new Vector2(300, 300), 0.5f), 10));
        roads.Insert(0, RoadBuilder.Build(RoadCurves.Resample([new Vector2(0, 0), new Vector2(0, 40)]), 0));       // an edit that makes the roads dirty, as every junction edit does
        // saving, in MapSaver's order
        var saved = MapDocument.Parse(project.Mission.ToBytes());
        layer.ApplyTo(saved);
        roads.WriteTo(saved);
        var names = saved.Objects.Select(o => o.Name).ToList();
        Assert.Equal(1, names.Count(n => n == "ixd27_1"));
        Assert.DoesNotContain("ita21_1", names);                // the stock piece the road tool took away stays away
        Assert.Contains("regen", names); Assert.Contains("afence1", names);
        Assert.Equal(3, saved.Objects.Count);
        _ = old;
        // and the pieces are what a reader finds
        Assert.Equal("ixd27_1", new PieceStore(saved.Objects)[0].Def.Name);
    }

    [Fact]
    public void RoadPiecesAreNotInTheObjectPalette()
    {
        foreach (var d in PieceCatalogue.All)
            foreach (uint type in new[] { 0x50u, 0x52u, 0x53u, 3u })
                Assert.Null(I76.Editor.Objects.ObjectCatalogue.Classify(d.Name, type, false, 10, 10));
        Assert.NotNull(I76.Editor.Objects.ObjectCatalogue.Classify("afence1", 3, false, 10, 2));
    }
}

/// <summary>The stock maps against the rules (needs I76_INSTALL / I76_NITRO_INSTALL; skipped silently without).</summary>
public class StockIntersectionTests(ITestOutputHelper output)
{
    private static IEnumerable<string> Missions()
    {
        foreach (var (env, ext) in new[] { ("I76_INSTALL", ".msn"), ("I76_NITRO_INSTALL", ".cbt") })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } a)
                foreach (var dir in new[] { "miss16", "miss8" }.Select(d => Path.Combine(a, d)).Where(Directory.Exists))
                    foreach (var f in Directory.EnumerateFiles(dir).Where(I76.Formats.Mission.MapFileSet.IsStockMission).Order())
                        yield return f;
    }

    private static List<(RoadEdit Road, bool AtStart, Vector2 At, Vector2 Body)> Ends(IEnumerable<RoadEdit> roads)
    {
        var o = new List<(RoadEdit, bool, Vector2, Vector2)>();
        foreach (var r in roads)
        {
            if (r.Points.Count < 3) continue;
            var poly = Poly.Of(r);
            if (poly.Length < 8) continue;
            o.Add((r, true, poly.P[0], Vector2.Normalize(poly.At(6) - poly.P[0])));
            o.Add((r, false, poly.P[^1], Vector2.Normalize(poly.At(poly.Length - 6) - poly.P[^1])));
        }
        return o;
    }

    [Fact]
    public void StockPiecesHaveRoadEndsOnTheArmsTheCatalogueSays()
    {
        int pieces = 0, full = 0, arms = 0, met = 0;
        var byShape = new Dictionary<PieceShape, (int Pieces, int Arms, int Met)>();
        foreach (var f in Missions())
        {
            var doc = MapDocument.Load(f);
            var store = new PieceStore(doc.Objects);
            var ends = Ends(doc.Roads);
            foreach (var p in store.Items)
            {
                if (p.Def.Shape == PieceShape.Split) continue;
                pieces++;
                int m = 0, n = 0;
                foreach (var a in p.Arms)
                {
                    n++;
                    // the branch of a fork: stock roads leave 5 m beyond the stub, at about 45 degrees
                    if (ends.Any(e => Vector2.Distance(e.At, a.Position) <= 1.5f && Vector2.Dot(e.Body, a.Out) > 0.9f) ||
                        (p.Def.Shape is PieceShape.ForkLeft or PieceShape.ForkRight && n == 3 && ends.Any(e => Vector2.Distance(e.At, a.Position) <= 10f && Vector2.Dot(e.Body, a.Out) > 0.9f))) m++;
                }
                arms += n; met += m;
                if (m == n) full++;
                var (bp, ba, bm) = byShape.GetValueOrDefault(p.Def.Shape);
                byShape[p.Def.Shape] = (bp + 1, ba + n, bm + m);
            }
        }
        foreach (var (s, v) in byShape) output.WriteLine($"{s}: {v.Pieces} pieces, {v.Met}/{v.Arms} arms with a road end on them");
        output.WriteLine($"all: {pieces} pieces, {full} with a road at every arm, {met}/{arms} arms");
        if (pieces == 0) return;
        Assert.True(met >= arms * 0.8, $"{met}/{arms} arms have a road end (to within 1.5 m) on them");
    }

    /// <summary>
    /// The inverse of placing: take a stock piece with roads at all its arms, undo it (the roads that met at the through arms become one
    /// road, the stem runs on to the junction point) and ask the rules what they would place there.
    /// </summary>
    [Fact]
    public void TheRulesPlaceTheStockPiecesWhereAndAsTheMapsHaveThem()
    {
        int tried = 0, shapeOk = 0, poseOk = 0;
        var misses = new List<string>();
        foreach (var f in Missions())
        {
            var doc = MapDocument.Load(f);
            var store = new PieceStore(doc.Objects);
            var ends = Ends(doc.Roads);
            foreach (var p in store.Items)
            {
                if (p.Def.Shape is PieceShape.Split) continue;
                var arms = p.Arms.ToArray();
                (RoadEdit Road, bool AtStart)? At(int i)
                {
                    var hits = ends.Where(e => Vector2.Distance(e.At, arms[i].Position) <= (i == 2 && p.Def.Shape is PieceShape.ForkLeft or PieceShape.ForkRight ? 10f : 1.5f) && Vector2.Dot(e.Body, arms[i].Out) > 0.9f).ToList();
                    return hits.Count == 1 ? (hits[0].Road, hits[0].AtStart) : null;
                }
                var a0 = At(0); var a1 = At(1); var a2 = At(2);
                var a3 = p.Def.Shape == PieceShape.X ? At(3) : null;
                if (a0 is null || a1 is null || a2 is null || (p.Def.Shape == PieceShape.X && a3 is null)) continue;
                var roads = new[] { a0.Value.Road, a1.Value.Road, a2.Value.Road }.Concat(a3 is { } t3 ? [t3.Road] : []).ToList();
                if (roads.Distinct().Count() != roads.Count) continue;
                tried++;
                // the through road: the two ends joined over the gap
                var ca = RoadBuilder.Centres(a0.Value.Road); if (a0.Value.AtStart) ca.Reverse();
                var cb = RoadBuilder.Centres(a1.Value.Road); if (!a1.Value.AtStart) cb.Reverse();
                var q = RoadBuilder.Build([.. ca, .. cb], a0.Value.Road.Kind);
                // the stem, drawn to the junction point (and, for an X, the crossing road drawn straight through)
                RoadEdit r;
                if (p.Def.Shape == PieceShape.X)
                {
                    var c2 = RoadBuilder.Centres(a2.Value.Road); if (a2.Value.AtStart) c2.Reverse();
                    var c3 = RoadBuilder.Centres(a3!.Value.Road); if (!a3.Value.AtStart) c3.Reverse();
                    r = RoadBuilder.Build([.. c2, .. c3], a2.Value.Road.Kind);
                }
                else
                {
                    var c2 = RoadBuilder.Centres(a2.Value.Road); if (a2.Value.AtStart) c2.Reverse();
                    c2.Add(p.Origin);
                    r = RoadBuilder.Build(c2, a2.Value.Road.Kind);
                }
                var rs = new RoadStore([q, r]);
                var j = new Junctions(rs, new PieceStore(), _ => 0);
                var prop = j.Detect(r, null, p.Def.Variant).FirstOrDefault();
                bool shape = prop is not null && prop.Def.Shape == p.Def.Shape;
                float dyaw = prop is null ? 99 : MathF.Abs(PiecePose.Wrap(prop.Pose.Yaw - p.Pose.Yaw)) * 180 / MathF.PI;
                bool pose = shape && Vector2.Distance(prop!.Pose.Origin, p.Pose.Origin) < 3f && (dyaw < 5 || p.Def.Shape == PieceShape.X && (dyaw is > 85 and < 95 or > 175 and < 185 || dyaw < 5));
                if (shape) shapeOk++;
                if (pose) poseOk++;
                else if (misses.Count < 12) misses.Add($"{Path.GetFileName(f)} {p.Def.Name} at ({p.Origin.X:0},{p.Origin.Y:0}): {(prop is null ? "no piece" : $"{prop.Def.Name}, off {Vector2.Distance(prop.Pose.Origin, p.Pose.Origin):0.0} m, {dyaw:0} degrees")}");
            }
        }
        output.WriteLine($"{tried} stock pieces with every arm met: the rules choose the same shape for {shapeOk}, and the same place and heading (3 m, 5 degrees) for {poseOk}");
        foreach (var m in misses) output.WriteLine("  " + m);
        if (tried == 0) return;
        Assert.True(shapeOk >= tried * 0.98, $"{shapeOk}/{tried} shapes");
        Assert.True(poseOk >= tried * 0.98, $"{poseOk}/{tried} poses");
    }
}
