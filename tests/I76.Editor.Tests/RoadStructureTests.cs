using System.Numerics;
using I76.Editor;
using I76.Editor.Objects;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>The road tool's bridge and tunnel stretches: which pieces cover a span, the grade they are laid on, what they say about the ground, and the one undo step they are.</summary>
public class RoadStructureTests
{
    private const int T = TerrainTiles.Tile;

    private static CatalogueItem Deck(string name, float len, float maxY) =>
        new() { Name = name, Category = ObjectCategory.Structures, Type = 0xc, MinX = 0, MaxX = 10, MinY = -1.8f, MaxY = maxY, MinZ = 0, MaxZ = len };

    private static CatalogueItem Tunnel(string name, float x, float z0, float z1, float w, float d) =>
        new() { Name = name, Category = ObjectCategory.Structures, Type = 2, MinX = 0, MaxX = w, MinY = 0, MaxY = 50, MinZ = 0, MaxZ = d, TunnelX = x, TunnelZ0 = z0, TunnelZ1 = z1, TunnelWidth = 12 };

    private static readonly CatalogueItem B2 = Deck("bebridg2", 80.1f, 2.5f), B3 = Deck("bebridg3", 60.1f, 2.5f), Ba = Deck("bebridga", 80.1f, 2.5f), Bb = Deck("bebridgb", 60.1f, 2.5f),
        W1 = Deck("bewdbrg1", 80.1f, 7f), W2 = Deck("bewdbrg2", 60.1f, 7f), T1 = Deck("bebridg1", 100.1f, 13.3f), T4 = Deck("bebridg4", 160.1f, 13.3f);
    private static readonly CatalogueItem N1 = Tunnel("betunnl1", 40, 21, 69, 12, 105), N2 = Tunnel("betunnl2", -150, 21, 129, 12, 170), N3 = Tunnel("betunnl3", 170, 122.2f, 277.6f, 12, 420);
    private static readonly CatalogueItem Ramp = new() { Name = "aaramp1", Category = ObjectCategory.Structures, Type = 0xb, MinX = 0, MaxX = 20, MinZ = 0, MaxZ = 42, MaxY = 15.7f };
    private static readonly CatalogueItem Overpass = new() { Name = "beovrps1", Category = ObjectCategory.Structures, Type = 0xc, MinX = 0, MaxX = 30, MinZ = 0, MaxZ = 125, MaxY = 9 };
    private static readonly CatalogueItem Building = new() { Name = "bcaik8", Category = ObjectCategory.Structures, Type = 0xc, MinX = 0, MaxX = 10, MinZ = 0, MaxZ = 100, MaxY = 9 };
    private static readonly List<CatalogueItem> Cat = [B2, B3, Ba, Bb, W1, W2, T1, T4, N1, N2, N3, Ramp, Overpass, Building];

    private static TerrainDocument Doc(Func<int, int, int> raw)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;
        var ter = new byte[4 * T * T * 2];
        for (int t = 0; t < 4; t++)
            for (int z = 0; z < T; z++)
                for (int x = 0; x < T; x++)
                {
                    int h = Math.Clamp(raw(t * T + x, z), 0, 4095);
                    int i = (t * T * T + z * T + x) * 2;
                    ter[i] = (byte)h; ter[i + 1] = (byte)(h >> 8);
                }
        return new TerrainDocument(new TerrainTiles(ter, 4, grid), "test");
    }

    /// <summary>Flat at 50 m with a valley across z = 300 m (x runs 100..400 samples... a trench 30 m deep, 200 m wide, running along x).</summary>
    private static TerrainDocument Valley() => Doc((sx, sz) =>
    {
        float z = sz * 5f;
        float depth = z is > 250 and < 450 ? 300 * MathF.Sin((z - 250) / 200 * MathF.PI) : 0;
        return 500 - (int)depth;
    });

    private static ObjectLayer Layer(out MapProject project, RoadStore? roads = null)
    {
        project = MapProject.CreateBlank("st", "Stretch", ".cbt", 2, 2, 100);
        return new ObjectLayer(project.Mission, n => n + ".sdf", roads ?? new RoadStore([])) { Ground = (_, _) => 0 };
    }

    private static StretchQuery Q(IMapDocument doc, BuildMode mode, Vector2 start, Vector2 aim, CatalogueItem? only = null, float? h = null) =>
        new() { Catalogue = Cat, Mode = mode, Start = start, Aim = aim, Ground = p => RoadTerrain.Ground(doc, p.X, p.Y), Only = only, StartHeight = h };

    // ---- the pieces -------------------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheLevelBridgeDecksAndTheTunnelsAreOffered()
    {
        Assert.Equal(new[] { "bebridg2", "bebridg3", "bebridga", "bebridgb", "bewdbrg1", "bewdbrg2", "bebridg1", "bebridg4" }.Order(), StretchPieces.For(Cat, BuildMode.Bridge).Select(i => i.Name).Order());
        Assert.DoesNotContain(StretchPieces.For(Cat, BuildMode.Bridge), i => i.Name is "aaramp1" or "beovrps1" or "bcaik8");   // a ramp, the 30 m wide overpass, a building the maps give the bridge type
        Assert.Equal(["betunnl1", "betunnl2", "betunnl3"], StretchPieces.For(Cat, BuildMode.Tunnel).Select(i => i.Name));
        Assert.Equal(48f, StretchPieces.Length(N1), 3); Assert.Equal(108f, StretchPieces.Length(N2), 3); Assert.Equal(155.4f, StretchPieces.Length(N3), 2);
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 2, 2 }, StretchPieces.For(Cat, BuildMode.Bridge).Select(StretchPieces.Style).OrderBy(x => x));
    }

    [Theory]
    [InlineData(70, 1, 60.1)]       // 60 and 80 are as near (the nearer wins)
    [InlineData(140, 2, 140.2)]     // 80 + 60 in one style
    [InlineData(160, 1, 160.1)]     // the truss deck is exactly that
    [InlineData(300, 3, 300.3)]     // 100 + 100 + 100 (truss) is nearer than 80 + 80 + 80 + 60
    public void AutomaticChoiceSnapsTheSpanToWholePiecesOfOneStyle(float span, int count, float total)
    {
        var t = StretchPieces.Tile(StretchPieces.For(Cat, BuildMode.Bridge), null, span)!;
        Assert.NotNull(t);
        Assert.Equal(total, t.Length, 1);
        Assert.Equal(t.Length, t.Pieces.Sum(StretchPieces.Length), 3);
        Assert.Single(t.Pieces.Select(StretchPieces.Style).Distinct());     // never mixed
        Assert.Equal(count, t.Pieces.Count);
        Assert.True(MathF.Abs(t.Error) <= 31);
        Assert.Equal(t.Length - span, t.Error, 3);
    }

    [Fact]
    public void AForcedModelIsRepeatedAndAShortSpanHasNoPiece()
    {
        var b = StretchPieces.Tile(StretchPieces.For(Cat, BuildMode.Bridge), T4, 300)!;
        Assert.Equal(2, b.Pieces.Count); Assert.All(b.Pieces, p => Assert.Equal("bebridg4", p.Name));
        Assert.Equal(320.2f, b.Length, 1);
        Assert.Null(StretchPieces.Tile(StretchPieces.For(Cat, BuildMode.Bridge), null, 25));
        Assert.Null(StretchPieces.Tile(StretchPieces.For(Cat, BuildMode.Bridge), W1, 30));
        // a tunnel is one piece, the nearest tube; it is not chained however long the span
        var tun = StretchPieces.For(Cat, BuildMode.Tunnel);
        Assert.Equal("betunnl1", StretchPieces.Tile(tun, null, 55)!.Pieces.Single().Name);
        Assert.Equal("betunnl2", StretchPieces.Tile(tun, null, 100)!.Pieces.Single().Name);
        Assert.Equal("betunnl3", StretchPieces.Tile(tun, null, 400)!.Pieces.Single().Name);
        Assert.Equal("betunnl3", StretchPieces.Tile(tun, N3, 100)!.Pieces.Single().Name);
        Assert.Null(StretchPieces.Tile(tun, null, 10));
    }

    [Fact]
    public void TheRotationOfAPitchedPieceIsATurnWhoseHeadingIsStillItsYaw()
    {
        var r = RoadStructures.Rotation(0.7f, 0.05f);
        Vector3 right = new(r[0], r[1], r[2]), up = new(r[3], r[4], r[5]), fwd = new(r[6], r[7], r[8]);
        Assert.Equal(1, right.Length(), 4); Assert.Equal(1, up.Length(), 4); Assert.Equal(1, fwd.Length(), 4);
        Assert.Equal(0, Vector3.Dot(right, up), 4); Assert.Equal(0, Vector3.Dot(up, fwd), 4); Assert.Equal(0, Vector3.Dot(right, fwd), 4);
        Assert.Equal(MathF.Sin(0.05f), fwd.Y, 4);
        var o = MapObject.Create("x", 0xc, default, 0); o.Rotation = r;
        Assert.Equal(0.7f, o.Yaw, 4);
        Assert.Equal(RoadStructures.Rotation(0.3f, 0), new MapObject(Create(0.3f)).Rotation);
        static byte[] Create(float y) { var o = MapObject.Create("x", 2, default, y); return o.Payload; }
    }

    // ---- planning -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ABridgeOverAValleyRunsOnOneGradeFromBankToBankInWholePieces()
    {
        var doc = Valley();
        // z runs 200 .. 520 across the valley (floor at z = 350, 30 m down): 320 m drawn
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 200), new Vector2(400, 520)));
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(plan.Tiling!.Length * MathF.Cos(MathF.Asin(plan.Grade * plan.Tiling.Length / plan.Run)), plan.Run, 2);
        Assert.Equal(1, plan.Dir.Y, 4);
        // pieces end to end along the line: each starts where the last ended
        for (int i = 0; i + 1 < plan.Pieces.Count; i++)
        {
            var a = plan.Pieces[i]; var b = plan.Pieces[i + 1];
            Assert.Equal(b.Along, a.Along + a.Length * MathF.Cos(a.Pitch), 2);
            Assert.Equal(b.StartHeight, a.StartHeight + a.Length * MathF.Sin(a.Pitch), 2);   // the decks meet in height
            Assert.Equal(a.Pitch, b.Pitch, 5);                                              // one grade end to end
        }
        // the deck's start is the ground at the start, the far end the ground there (the grade allows it): level here (both banks are 50 m)
        Assert.Equal(50f, plan.StartHeight, 1); Assert.Equal(50f, plan.EndHeight, 1);
        Assert.InRange(plan.Grade, -0.001f, 0.001f);
        Assert.Contains(plan.Notes, w => w.Contains("above the ground"));      // the deck is up to 30 m over the valley floor: piers of ground
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("below the ground"));
        Assert.Empty(plan.Warnings);                                               // a pier is a note, not a warning
        // the plan's end is on the line, whole pieces away
        Assert.Equal(plan.Start.X, plan.End.X, 3);
        Assert.Equal(plan.Run, plan.End.Y - plan.Start.Y, 2);
        Assert.Equal(plan.Samples.Count, plan.Deck.Count);
    }

    [Fact]
    public void TheGradeFollowsTheFarBankAndIsHeldToTheLimit()
    {
        var doc = Doc((sx, sz) => 500 + (int)(sz * 5 * 0.12f * 10));   // rising 12 % along z
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 100), new Vector2(400, 300)));
        Assert.True(plan.Ok);
        Assert.Equal(RoadStructures.BridgeMaxGrade, plan.Pieces[0].Pitch is var p ? MathF.Sin(p) : 0, 4);
        Assert.True(plan.Pieces.All(x => MathF.Abs(MathF.Sin(x.Pitch)) <= RoadStructures.BridgeMaxGrade + 1e-4f));
        Assert.Contains(plan.Warnings, w => w.Contains("grade is held"));
        // a gentle slope is followed to the far end
        var gentle = Doc((sx, sz) => 500 + (int)(sz * 5 * 0.03f * 10));
        var g = RoadStructures.Plan(Q(gentle, BuildMode.Bridge, new Vector2(400, 100), new Vector2(400, 300)));
        Assert.Equal(RoadTerrain.Ground(gentle, g.End.X, g.End.Y), g.EndHeight, 0.3f);
        Assert.Equal(0.03f, MathF.Sin(g.Pieces[0].Pitch), 3);
        Assert.DoesNotContain(g.Warnings, w => w.Contains("grade is held"));
    }

    [Fact]
    public void ADeckBuriedInAHillIsWarnedAboutAndAStartHeightIsKept()
    {
        var hill = Doc((sx, sz) => 500 + (sz * 5 is > 200 and < 300 ? 150 : 0));      // a 15 m ridge across the line
        var plan = RoadStructures.Plan(Q(hill, BuildMode.Bridge, new Vector2(400, 100), new Vector2(400, 400)));
        Assert.Contains(plan.Warnings, w => w.Contains("below the ground"));
        var pinned = RoadStructures.Plan(Q(hill, BuildMode.Bridge, new Vector2(400, 100), new Vector2(400, 400), h: 52f));
        Assert.Equal(52f, pinned.StartHeight);
        Assert.Equal(52f, pinned.Pieces[0].StartHeight);
    }

    [Fact]
    public void AHeadingFromADeckEndKeepsTheSpanStraightOnAndSlantedOnesAreWarnedAbout()
    {
        var doc = Valley();
        var q = new StretchQuery { Catalogue = Cat, Mode = BuildMode.Bridge, Start = new Vector2(400, 200), Aim = new Vector2(450, 400), Ground = p => RoadTerrain.Ground(doc, p.X, p.Y), Heading = new Vector2(0, 1) };
        var plan = RoadStructures.Plan(q);
        Assert.Equal(400f, plan.End.X, 3);                 // along the heading, not toward the cursor
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("axes"));
        var slant = RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 200), new Vector2(560, 360)));
        Assert.Contains(slant.Warnings, w => w.Contains("45 degrees off the map's axes") || w.Contains("degrees off the map's axes"));
        Assert.False(RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 200), new Vector2(400, 215))).Ok);
        Assert.Equal("Drag out the length of the bridge", RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 200), new Vector2(400, 200.5f))).Problem);
        var off = RoadStructures.Plan(new StretchQuery { Catalogue = Cat, Mode = BuildMode.Bridge, Start = new Vector2(400, 200), Aim = new Vector2(400, 400), Ground = _ => 50, OnMap = p => p.Y < 300 });
        Assert.Equal("The bridge runs off the map", off.Problem);
    }

    [Fact]
    public void ATunnelIsOneLevelPieceWithItsTubeOnTheLine()
    {
        var doc = Doc((sx, sz) => 500 + (int)(sx * 5 / 20f));        // a gentle rise along x
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Tunnel, new Vector2(300, 300), new Vector2(420, 300)));
        Assert.True(plan.Ok, plan.Problem);
        var p = plan.Pieces.Single();
        Assert.Equal("betunnl2", p.Item.Name);              // 120 m drawn: the 108 m tube
        Assert.Equal(0, p.Pitch);
        Assert.Equal(108f, plan.Run, 3);
        Assert.Equal(new Vector2(408, 300).X, plan.End.X, 3);
        Assert.Equal(plan.StartHeight, plan.EndHeight);
        // the tube's centre line runs on the stretch from portal to portal
        var fp = Footprint.Of(p.Item, new Vector3(p.Origin.X, 0, p.Origin.Y), p.Yaw);
        var a = fp.ToWorld(new Vector2(p.Item.TunnelX, p.Item.TunnelZ0)); var b = fp.ToWorld(new Vector2(p.Item.TunnelX, p.Item.TunnelZ1));
        Assert.True(Vector2.Distance(a, plan.Start) < 1e-2f, $"{a} {plan.Start}"); Assert.True(Vector2.Distance(b, plan.End) < 1e-2f, $"{b} {plan.End}");
        Assert.All(plan.Deck, h => Assert.Equal(plan.StartHeight, h));
    }

    [Fact]
    public void ATunnelThatWouldStandOnFillOrCutDeepIsWarnedAbout()
    {
        var pit = Doc((sx, sz) => 500 - (sx * 5 is > 200 and < 700 ? 150 : 0));         // the hill is a hollow
        var plan = RoadStructures.Plan(Q(pit, BuildMode.Tunnel, new Vector2(300, 300), new Vector2(408, 300), h: 50f));
        Assert.Contains(plan.Warnings, w => w.Contains("fill") && w.Contains("sticks out"));
        var hill = Doc((sx, sz) => 500 + 300);
        var cut = RoadStructures.Plan(Q(hill, BuildMode.Tunnel, new Vector2(300, 300), new Vector2(408, 300), h: 50f));
        Assert.Contains(cut.Warnings, w => w.Contains("deep cut"));
        var flat = RoadStructures.Plan(Q(Doc((_, _) => 500), BuildMode.Tunnel, new Vector2(300, 300), new Vector2(408, 300)));
        Assert.Empty(flat.Warnings);
    }

    // ---- making it -------------------------------------------------------------------------------------------------------

    private static (Vector3 Start, Vector3 End) DeckOf(EditObject e)
    {
        var item = Cat.Single(c => c.Name == e.Name);
        var r = e.Data.Rotation;
        var ends = item.DeckEnds();
        Vector3 W(StructureAttach.Attach a) => e.Position + a.Position.X * new Vector3(r[0], r[1], r[2]) + a.Position.Y * new Vector3(r[3], r[4], r[5]) + a.Position.Z * new Vector3(r[6], r[7], r[8]);
        return (W(ends[0]), W(ends[1]));
    }

    [Fact]
    public void PlacingABridgeBringsTheOriginsToTheDeckAndTheDecksJoinEndToEnd()
    {
        var doc = Valley();
        var layer = Layer(out _);
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 200), new Vector2(400, 520)));
        var before = doc.Snapshot();
        var res = RoadStructures.Commit(doc, layer, layer.Roads, plan, true, new RoadGrade.Settings(), new RoadTerrain.Settings())!;
        Assert.Equal(plan.Pieces.Count, res.Objects.Count);
        Assert.True(res.Piers >= 1);
        EditObject? prev = null;
        foreach (var e in res.Objects)
        {
            // the game stands the origin on the ground: the record's y IS the ground there, so the deck is where the plan put it
            Assert.Equal(RoadTerrain.Ground(doc, e.Position.X, e.Position.Z), e.Position.Y, 0.001f);
            var (s, f) = DeckOf(e);
            if (prev is not null) { var (_, pf) = DeckOf(prev); Assert.True(Vector3.Distance(pf, s) < 0.15f, $"gap {Vector3.Distance(pf, s):0.00} between {prev.Name} and {e.Name}"); }
            Assert.Equal(0xcu, e.Data.ObjectType);
            prev = e;
        }
        var (s0, _) = DeckOf(res.Objects[0]); var (_, fN) = DeckOf(res.Objects[^1]);
        Assert.True(Vector2.Distance(new Vector2(s0.X, s0.Z), plan.Start) < 0.05f);
        Assert.True(Vector2.Distance(new Vector2(fN.X, fN.Z), plan.End) < 0.2f, $"{fN} {plan.End}");
        Assert.Equal(plan.EndHeight, fN.Y, 0.2f);
        // the deck clears the ground along its length wherever there is a valley: the floor is untouched between the piers
        Assert.Equal(20f, RoadTerrain.Ground(doc, 400, 350), 1);
        Assert.NotEqual(before, doc.Snapshot());
        // one undo step brings everything back: the ground, the objects
        var undo = new UndoStack(doc);
        undo.Push(res.Command);
        Assert.Equal(plan.Pieces.Count, layer.Items.Count);
        undo.Undo();
        Assert.Empty(layer.Items);
        Assert.Equal(before, doc.Snapshot());
        undo.Redo();
        Assert.Equal(plan.Pieces.Count, layer.Items.Count);
        Assert.Equal(RoadTerrain.Ground(doc, res.Objects[1].Position.X, res.Objects[1].Position.Z), layer.Items[1].Position.Y, 0.001f);
    }

    [Fact]
    public void AGradedBridgeIsPitchedPieceByPieceAndLeavesLevelGroundAloneWhereItAlreadyIsRight()
    {
        var doc = Doc((sx, sz) => 500 + (int)(sz * 5 * 0.03f * 10));           // a 3 % slope rising along z
        var layer = Layer(out _);
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 100), new Vector2(400, 300)));
        var res = RoadStructures.Commit(doc, layer, layer.Roads, plan, true, new RoadGrade.Settings(), new RoadTerrain.Settings())!;
        foreach (var e in res.Objects)
        {
            var (s, f) = DeckOf(e);
            Assert.Equal(0.03f, (f.Y - s.Y) / Vector2.Distance(new(s.X, s.Z), new(f.X, f.Z)), 0.005f);
        }
        // the ground follows the grade: nothing needed raising
        Assert.Equal(0, res.Piers);
    }

    [Fact]
    public void ABridgeCutsTheRoadItReplacesAtItsEndsAsOneStep()
    {
        var doc = Doc((_, _) => 500);
        var layer = Layer(out var project);
        var roads = new RoadStore([RoadBuilder.Build(Enumerable.Range(0, 81).Select(i => new Vector2(400, 100 + i * 5)).ToList(), 1)]);
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Bridge, new Vector2(400, 150), new Vector2(400, 290)));
        var res = RoadStructures.Commit(doc, layer, roads, plan, true, new RoadGrade.Settings(), new RoadTerrain.Settings())!;
        Assert.Equal(1, res.Cuts);
        Assert.Equal(2, roads.Count);                                 // before it and after it
        Assert.True(Vector2.Distance(roads[0].Points[^1].Centre, plan.Start) < 1f);
        Assert.True(Vector2.Distance(roads[1].Points[0].Centre, plan.End) < 1f);
        var undo = new UndoStack(doc); undo.Push(res.Command);
        undo.Undo();
        Assert.Equal(1, roads.Count); Assert.Empty(layer.Items);
        undo.Redo();
        Assert.Equal(2, roads.Count);
        // with CUT ROADS off the road stays
        var roads2 = new RoadStore([RoadBuilder.Build(Enumerable.Range(0, 81).Select(i => new Vector2(400, 100 + i * 5)).ToList(), 1)]);
        var layer2 = Layer(out _);
        var res2 = RoadStructures.Commit(Doc((_, _) => 500), layer2, roads2, plan, false, new RoadGrade.Settings(), new RoadTerrain.Settings())!;
        Assert.Equal(0, res2.Cuts); Assert.Equal(1, roads2.Count);
    }

    [Fact]
    public void PlacingATunnelLevelsTheGroundLaysTheRoadThroughAtOneHeightAndUndoesAsOneStep()
    {
        var doc = Doc((sx, sz) => 500 + (int)(sx * 5 / 10f));            // a 10 % rise along x: 108 m is 10.8 m
        var layer = Layer(out _);
        var plan = RoadStructures.Plan(Q(doc, BuildMode.Tunnel, new Vector2(300, 300), new Vector2(408, 300)));
        var before = doc.Snapshot();
        var res = RoadStructures.Commit(doc, layer, layer.Roads, plan, true, new RoadGrade.Settings(), new RoadTerrain.Settings(), roadKind: 1)!;
        Assert.NotNull(res.Road);
        Assert.Single(layer.Roads.Roads);
        var road = layer.Roads[0];
        // the road runs from portal to portal and the ground under it is the one level
        Assert.True(Vector2.Distance(road.Points[0].Centre, plan.Start) < 1f);
        Assert.True(Vector2.Distance(road.Points[^1].Centre, plan.End) < 1f);
        foreach (var pt in road.Points) Assert.Equal(plan.StartHeight, RoadTerrain.Ground(doc, pt.Centre.X, pt.Centre.Y), 0.15f);
        var e = res.Objects.Single();
        Assert.Equal(plan.StartHeight, e.Position.Y, 0.15f);
        Assert.Equal(0, e.Data.Rotation[7]);                             // level
        var undo = new UndoStack(doc); undo.Push(res.Command);
        undo.Undo();
        Assert.Equal(before, doc.Snapshot()); Assert.Empty(layer.Items); Assert.Equal(0, layer.Roads.Count);
        undo.Redo();
        Assert.Equal(1, layer.Roads.Count); Assert.Single(layer.Items);
    }
}
