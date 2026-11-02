using I76.Editor;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

public class EditorTests
{
    private const int T = TerrainTiles.Tile;

    /// <summary>A 2 x 2 cell map on two tiles: tile 0 at (0,0) and (1,1) (shared), tile 1 at (0,1); cell (1,0) empty. Every sample is `height` with nibble 0x3.</summary>
    private static TerrainDocument Doc(int height = 200, bool shared = true)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        grid[0] = 0; grid[1] = 1; if (shared) grid[MissionFile.ZmapGrid + 1] = 0;
        var ter = new byte[2 * T * T * 2];
        for (int i = 0; i < 2 * T * T; i++) { ushort v = (ushort)(height | 0x3000); ter[i * 2] = (byte)v; ter[i * 2 + 1] = (byte)(v >> 8); }
        return new TerrainDocument(new TerrainTiles(ter, 2, grid), "test");
    }

    private static BrushSettings Brush(BrushKind k, float radius = 40, float strength = 1, Falloff f = Falloff.Smooth) => new() { Kind = k, Radius = radius, Strength = strength, Falloff = f };

    [Fact]
    public void RevisionCountsEveryEditAndNeverGoesDown()
    {
        var doc = Doc();
        long r0 = doc.Revision;
        Assert.Equal(r0, doc.Revision);                      // reading changes nothing
        new BrushStroke(doc, Brush(BrushKind.Raise, radius: 50, strength: 0.5f)).Stamp(64, 64, 0.5);
        long r1 = doc.Revision;
        Assert.True(r1 > r0);
        doc.MarkSaved();                                     // a save clears the modified flag, not the history
        Assert.Equal(r1, doc.Revision);
        Assert.False(doc.Modified);
        doc.Flatten(300);
        Assert.True(doc.Revision > r1);
        var look = new MapLook("c.act", "a.map", "b.map", "", 12);
        doc.InitLook(look, look);
        long r2 = doc.Revision;
        doc.SetLook(look with { Hour = 13 });
        Assert.True(doc.Revision > r2);
    }

    [Fact]
    public void TheTerrainsContentHashFollowsTheHeightsAndComesBackWithThem()
    {
        // what a playtest keeps of the ground it built (the 1997 meshes, the distant terrain) is keyed by this: an edit must change it, an undo must give it back
        var doc = Doc();
        ulong h0 = doc.Tiles.ContentHash();
        Assert.Equal(h0, Doc().Tiles.ContentHash());          // the same ground, the same hash
        var before = doc.Snapshot();
        new BrushStroke(doc, Brush(BrushKind.Raise, radius: 50, strength: 0.5f)).Stamp(64, 64, 0.5);
        ulong h1 = doc.Tiles.ContentHash();
        Assert.NotEqual(h0, h1);
        Assert.True(doc.Restore(before));
        Assert.Equal(h0, doc.Tiles.ContentHash());
    }

    [Fact]
    public void FalloffIsOneAtTheCentreZeroAtTheRimAndFalls()
    {
        foreach (var f in Enum.GetValues<Falloff>())
        {
            Assert.Equal(1, BrushMath.Weight(f, 0), 6);
            Assert.Equal(0, BrushMath.Weight(f, 1), 6);
            Assert.Equal(0, BrushMath.Weight(f, 1.5), 6);
            double prev = 1;
            for (double t = 0; t <= 1; t += 0.05)
            {
                double w = BrushMath.Weight(f, t);
                Assert.InRange(w, 0, 1);
                Assert.True(w <= prev + 1e-9, $"{f} rises at {t}");
                prev = w;
            }
        }
        Assert.Equal(0.5, BrushMath.Weight(Falloff.Smooth, 0.5), 6);
        Assert.Equal(1, BrushMath.Weight(Falloff.Plateau, 0.45), 6);
        Assert.True(BrushMath.Weight(Falloff.Sharp, 0.5) < BrushMath.Weight(Falloff.Linear, 0.5));
    }

    [Fact]
    public void RaiseLiftsTheCentreMostAndNothingPastTheRadius()
    {
        var doc = Doc();
        var s = new BrushStroke(doc, Brush(BrushKind.Raise, radius: 50, strength: 0.5f));
        s.Stamp(64, 64, 0.5);
        int centre = doc.GetHeight(64, 64), mid = doc.GetHeight(68, 64), far = doc.GetHeight(80, 64);
        Assert.True(centre > 200);
        Assert.True(centre > mid && mid > 200);
        Assert.Equal(200, far);                 // 16 samples = 80 m, past the 50 m radius
        // 24 m/s * 0.5 * 0.5 s = 6 m = 60 raw at the centre
        Assert.Equal(260, centre);
    }

    [Fact]
    public void LowerMirrorsRaiseAndHeightsStayInTheSampleRange()
    {
        var doc = Doc(10);
        new BrushStroke(doc, Brush(BrushKind.Lower)).Stamp(64, 64, 10);
        Assert.Equal(0, doc.GetHeight(64, 64));
        var hi = Doc(4090);
        new BrushStroke(hi, Brush(BrushKind.Raise)).Stamp(64, 64, 10);
        Assert.Equal(4095, hi.GetHeight(64, 64));
    }

    [Fact]
    public void ANibbleIsNeverChanged()
    {
        var doc = Doc();
        foreach (var k in Enum.GetValues<BrushKind>().Where(k => k != BrushKind.Surface))   // (the surface brush sets the type: SurfaceTests)
        {
            var s = new BrushStroke(doc, Brush(k) is var b && k == BrushKind.Level ? new BrushSettings { Kind = k, Radius = 40, Strength = 1, TargetHeight = 33 } : Brush(k));
            s.Stamp(64, 64, 0.3);
        }
        doc.Locate(64, 64, out int tile, out int off);
        Assert.Equal(0x3000, doc.GetSample(tile, off) & 0xF000);
        doc.SetHeight(10, 10, 99999);
        Assert.Equal(4095, doc.GetHeight(10, 10));
        doc.Locate(10, 10, out tile, out off);
        Assert.Equal(0x3000, doc.GetSample(tile, off) & 0xF000);
    }

    [Fact]
    public void SmallStepsAccumulateInsteadOfRoundingAway()
    {
        var doc = Doc();
        var s = new BrushStroke(doc, Brush(BrushKind.Raise, strength: 0.02f));
        // 24 m/s * 0.02 / 60 = 0.008 m a frame: under a raw step (0.1 m), over many frames it must show
        for (int i = 0; i < 120; i++) s.Stamp(64, 64, 1 / 60.0);
        Assert.InRange(doc.GetHeight(64, 64), 200 + 8, 200 + 11);   // 120 frames = 2 s = 0.96 m
    }

    [Fact]
    public void FlattenPullsTowardTheHeightUnderTheFirstClick()
    {
        var doc = Doc();
        doc.SetHeight(64, 64, 500);   // a hill under the click
        var s = new BrushStroke(doc, Brush(BrushKind.Flatten, radius: 100, strength: 1));
        s.Stamp(64, 64, 0.01);        // takes 500
        for (int i = 0; i < 30; i++) s.Stamp(70, 64, 0.1);
        Assert.InRange(doc.GetHeight(70, 64), 495, 500);
        Assert.InRange(doc.GetHeight(60, 64), 490, 500);
    }

    [Fact]
    public void LevelPullsToTheTypedHeight()
    {
        var doc = Doc();
        var s = new BrushStroke(doc, new BrushSettings { Kind = BrushKind.Level, Radius = 60, Strength = 1, TargetHeight = 35.5f });
        for (int i = 0; i < 30; i++) s.Stamp(64, 64, 0.1);
        Assert.InRange(doc.GetHeight(64, 64), 354, 356);
    }

    [Fact]
    public void SmoothMovesASpikeTowardItsNeighboursAndKeepsTheTotalAboutTheSame()
    {
        var doc = Doc();
        doc.SetHeight(64, 64, 1000);
        var s = new BrushStroke(doc, Brush(BrushKind.Smooth, radius: 30, strength: 0.5f));
        for (int i = 0; i < 5; i++) s.Stamp(64, 64, 0.05);
        Assert.True(doc.GetHeight(64, 64) < 700);
        Assert.True(doc.GetHeight(65, 64) > 200);
        Assert.True(doc.GetHeight(65, 64) < 1000);
    }

    [Fact]
    public void NoiseIsDeterministicAndRoughens()
    {
        var a = Doc(); var b = Doc();
        new BrushStroke(a, Brush(BrushKind.Noise, strength: 0.5f)).Stamp(64, 64, 0.5);
        new BrushStroke(b, Brush(BrushKind.Noise, strength: 0.5f)).Stamp(64, 64, 0.5);
        var heights = new List<int>();
        for (int x = 56; x <= 72; x++) { Assert.Equal(a.GetHeight(x, 64), b.GetHeight(x, 64)); heights.Add(a.GetHeight(x, 64)); }
        Assert.True(heights.Max() - heights.Min() > 5);
        Assert.Equal(200, a.GetHeight(90, 64));
    }

    [Fact]
    public void AStrokeIsOneCommandAndUndoRestoresEverySampleIncludingNibbles()
    {
        var doc = Doc();
        var before = doc.Snapshot();
        var undo = new UndoStack(doc);
        var s = new BrushStroke(doc, Brush(BrushKind.Raise, strength: 0.4f));
        for (int i = 0; i < 20; i++) s.Stamp(64 + i, 64, 0.05);   // a dragged stroke
        undo.Push(s.End()!);
        Assert.Equal(1, undo.Count);
        var after = doc.Snapshot();
        Assert.NotEqual(before, after);
        Assert.True(undo.Undo());
        Assert.Equal(before, doc.Snapshot());
        Assert.False(undo.CanUndo);
        Assert.True(undo.Redo());
        Assert.Equal(after, doc.Snapshot());
        Assert.False(undo.Redo());
    }

    [Fact]
    public void UndoKeepsTheValueFromBeforeTheStrokeWhereAStrokeTouchesASampleTwice()
    {
        var doc = Doc();
        var undo = new UndoStack(doc);
        var s = new BrushStroke(doc, Brush(BrushKind.Raise));
        for (int i = 0; i < 10; i++) s.Stamp(64, 64, 0.1);
        undo.Push(s.End()!);
        undo.Undo();
        Assert.Equal(200, doc.GetHeight(64, 64));
    }

    [Fact]
    public void ANewStrokeAfterUndoDropsTheRedoHistory()
    {
        var doc = Doc();
        var undo = new UndoStack(doc);
        var a = new BrushStroke(doc, Brush(BrushKind.Raise)); a.Stamp(64, 64, 0.1); undo.Push(a.End()!);
        undo.Undo();
        Assert.True(undo.CanRedo);
        var b = new BrushStroke(doc, Brush(BrushKind.Lower)); b.Stamp(64, 64, 0.1); undo.Push(b.End()!);
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void AStrokeThatChangesNothingIsNotACommand()
    {
        var doc = Doc();
        var s = new BrushStroke(doc, Brush(BrushKind.Raise));
        s.Stamp(5000, 5000, 0.1);    // off the map
        Assert.Null(s.End());
        var flat = new BrushStroke(doc, Brush(BrushKind.Flatten));
        flat.Stamp(64, 64, 0.1);    // already flat
        Assert.Null(flat.End());
    }

    [Fact]
    public void BrushesStopAtTheMapsEdgeAndEmptyCells()
    {
        var doc = Doc();
        var s = new BrushStroke(doc, Brush(BrushKind.Raise, radius: 100));
        var dirty = s.Stamp(64, T - 3, 0.2);   // at the south edge of cell (0,0); (row 1, col 0) is empty
        Assert.NotNull(dirty);
        Assert.False(doc.Has(5, T + 5));
        Assert.Equal(0, doc.GetHeight(5, T + 5));
    }

    [Fact]
    public void ATileShownTwiceChangesOnceAndReportsBothCells()
    {
        var doc = Doc();
        var seen = new List<SampleRect>();
        doc.Changed += seen.Add;
        doc.SetHeight(10, 10, 300);                       // cell (0,0); tile 0 also at (1,1)
        Assert.Equal(300, doc.GetHeight(10 + T, 10 + T));
        Assert.Contains(new SampleRect(10, 10, 10, 10), seen);
        Assert.Contains(new SampleRect(10 + T, 10 + T, 10 + T, 10 + T), seen);
        // a brush at one cell changes each physical sample once
        var s = new BrushStroke(doc, Brush(BrushKind.Raise, radius: 20));
        s.Stamp(64, 64, 0.1);
        Assert.Equal(doc.GetHeight(64, 64), doc.GetHeight(64 + T, 64 + T));
        var cmd = s.End()!;
        Assert.Single(cmd.Tiles);                          // one tile touched
    }

    [Fact]
    public void ChangedReportsTheSamplesTouched()
    {
        var doc = Doc();
        SampleRect? got = null;
        doc.Changed += r => { if (r.X1 < T) got = got is { } g ? g.Union(r) : r; };   // cell (0,0)'s (tile 0 is also shown at (1,1))
        var d = new BrushStroke(doc, Brush(BrushKind.Raise, radius: 25)).Stamp(60, 60, 0.1);
        Assert.NotNull(d);
        Assert.True(got!.Value.Contains(60, 60));
        Assert.True(got.Value.X0 >= 55 && got.Value.X1 <= 65);
        Assert.Equal(d, got);
    }

    [Fact]
    public void FlattenMapClearsNibblesAndSetsOneHeight()
    {
        var doc = Doc();
        doc.Flatten(300);
        Assert.All(doc.Snapshot(), v => Assert.Equal(300, v));
    }

    [Fact]
    public void TheUndoStackDropsItsOldestPastTheLimit()
    {
        var doc = Doc();
        var undo = new UndoStack(doc, limit: 100);
        for (int i = 0; i < 20; i++)
        {
            var s = new BrushStroke(doc, Brush(BrushKind.Raise, radius: 12)); s.Stamp(30 + i * 3, 30, 0.2); undo.Push(s.End()!);
        }
        Assert.True(undo.Count < 20);
        Assert.True(undo.Count >= 1);
    }
}

public class SurfaceRefreshTests
{
    private const int T = TerrainTiles.Tile;

    private static TerrainDocument Hills(int tiles = 2)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < tiles; i++) grid[i] = (byte)i;
        var ter = new byte[tiles * T * T * 2];
        for (int t = 0; t < tiles; t++)
            for (int z = 0; z < T; z++)
                for (int x = 0; x < T; x++)
                {
                    int gx = t * T + x;
                    ushort h = (ushort)(300 + 40 * Math.Sin(gx * 0.11) + 30 * Math.Cos(z * 0.13));   // gentle: 0.1 m units
                    int i = (t * T * T + z * T + x) * 2;
                    ter[i] = (byte)h; ter[i + 1] = (byte)(h >> 8);
                }
        return new TerrainDocument(new TerrainTiles(ter, tiles, grid), "hills");
    }

    [Fact]
    public void ARefreshedSurfaceDrawsWhereAFreshOneDoesOnGentleGround()
    {
        var doc = Hills();
        var surf = new TerrainSurface(doc.Tiles);
        // a gentle bump
        var brush = new BrushSettings { Kind = BrushKind.Raise, Radius = 60, Strength = 0.3f };
        var s = new BrushStroke(doc, brush);
        SampleRect? dirty = null;
        for (int i = 0; i < 6; i++) { var r = s.Stamp(100, 60, 0.1); if (r is { } rr) dirty = dirty is { } d ? d.Union(rr) : rr; }
        Assert.NotNull(dirty);
        var vis = surf.Refresh(doc.Tiles, dirty!.Value.X0, dirty.Value.Z0, dirty.Value.X1, dirty.Value.Z1);
        var fresh = new TerrainSurface(doc.Tiles);
        double worst = 0;
        for (double fz = vis.Z0; fz <= vis.Z1; fz += 0.7)
            for (double fx = vis.X0; fx <= vis.X1; fx += 0.7)
            {
                var a = surf.Point(fx, fz, out _); var b = fresh.Point(fx, fz, out _);
                worst = Math.Max(worst, Math.Abs(a.Y - b.Y));
            }
        Assert.True(worst < 0.01, $"the refreshed surface is {worst} m off a fresh one");
        // and the surface did change where the bump is
        var before = new TerrainSurface(Hills().Tiles);
        Assert.True(Math.Abs(surf.Point(100, 60, out _).Y - before.Point(100, 60, out _).Y) > 1);
    }

    [Fact]
    public void ARefreshOutsideTheEditChangesNothing()
    {
        var doc = Hills();
        var surf = new TerrainSurface(doc.Tiles);
        var far = surf.Point(200, 100, out _);
        doc.SetHeight(20, 20, 900);
        surf.Refresh(doc.Tiles, 20, 20, 20, 20);
        Assert.Equal(far, surf.Point(200, 100, out _));
    }

    [Fact]
    public void ASnapshotCommandUndoesAWholeMapEdit()
    {
        var doc = Hills();
        var before = doc.Snapshot();
        doc.Flatten(250);
        var cmd = SnapshotCommand.Between("flat", before, doc.Snapshot())!;
        var undo = new UndoStack(doc);
        undo.Push(cmd);
        undo.Undo();
        Assert.Equal(before, doc.Snapshot());
        undo.Redo();
        Assert.All(doc.Snapshot(), v => Assert.Equal(250, v));
        Assert.Null(SnapshotCommand.Between("same", before, before));
    }
}

public class SaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-editor-save-" + Guid.NewGuid().ToString("N"));
    private string Addon => Path.Combine(_root, "addon");
    private string Stock => Path.Combine(_root, "miss16");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private (TerrainDocument Doc, string Path) Open(string dir, string stem = "flat1")
    {
        // a map as a stock install has it: written by the writers, read back as the game reads it
        var (_, files) = NewMap.CreateFlat(dir, stem, "Flat One", 2, 10, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        var project = MapProject.Load(mission);
        return (new TerrainDocument(project.Terrain, stem, "Flat One", mission, Addon), mission);
    }

    [Fact]
    public void ANewFlatMapIsFlatAtTheHeightWithASpawnAndLoadsBack()
    {
        var (project, files) = NewMap.CreateFlat(Addon, "flat2", "Flat Two", 2, 12.5, nitro: true);
        Assert.Equal(2, files.Count);
        var back = MapProject.Load(files.First(f => f.EndsWith(".cbt")));
        Assert.Equal(4, back.Terrain.TileCount);
        Assert.Equal(125, back.Terrain.Height(4 * 128 + 5, 72 * 128 + 5));
        Assert.Equal("Flat Two", back.Mission.ArenaName);
        var spawn = Assert.Single(back.Mission.Spawns);
        var b = back.WorldBounds();
        Assert.InRange(spawn.Position.X, b.X, b.Z);
        Assert.InRange(spawn.Position.Z, b.Y, b.W);
        Assert.Equal(".lvl", Path.GetExtension(NewMap.CreateFlat(Addon, "flat3", "Flat Three", 1, 5, nitro: false).Files.First(f => f.EndsWith(".lvl"))));
        Assert.Throws<ArgumentException>(() => NewMap.CreateFlat(Addon, "far too long", "x", 1, 5, true));
    }

    [Fact]
    public void ASavedMapInTheSaveFolderIsWrittenInPlaceWithTheEditedHeights()
    {
        var (doc, path) = Open(Addon);
        Assert.False(doc.NeedsSaveAs);
        var undo = new UndoStack(doc);
        var s = new BrushStroke(doc, new BrushSettings { Kind = BrushKind.Raise, Radius = 60, Strength = 0.5f });
        s.Stamp(4 * 128 + 64, 72 * 128 + 64, 0.5);
        undo.Push(s.End()!);
        Assert.True(doc.Modified);
        int edited = doc.GetHeight(4 * 128 + 64, 72 * 128 + 64);
        Assert.True(edited > 100);
        var written = doc.Save();
        Assert.False(doc.Modified);
        Assert.Contains(path, written);
        var back = MapProject.Load(path);
        Assert.Equal(edited, back.Terrain.Height(4 * 128 + 64, 72 * 128 + 64));
        Assert.Equal(100, back.Terrain.Height(4 * 128 + 5, 72 * 128 + 5));   // untouched ground as it was
        Assert.Equal("Flat One", back.Mission.ArenaName);
    }

    private static readonly MapLook Chosen = new("t02_16.act", "nk_6cld0.map", "tp081sd6.map", "p13_16.hzd", 6);

    [Fact]
    public void AChosenLookIsSavedWithTheMapReadBackAndUndoneAsOneStep()
    {
        var (doc, path) = Open(Addon);
        var file = MapLook.Of(WorldSettings.Read(new MissionFile(path))!);
        doc.InitLook(file, null);
        Assert.False(doc.Modified);
        int rebuilt = 0;
        doc.LookChanged += () => rebuilt++;
        var undo = new UndoStack(doc);
        // what the editor's APPLY does: an undo step, and the document's look (which has the world rebuilt)
        undo.Push(new LookCommand("map style: ground", file, Chosen));
        doc.SetLook(Chosen);
        Assert.Equal((1, true), (rebuilt, doc.Modified));
        doc.SetLook(Chosen);                                                         // the same look again: nothing
        Assert.Equal(1, rebuilt);
        Assert.True(undo.Undo());
        Assert.Equal((file, 2, false), (doc.Look, rebuilt, doc.Modified));
        Assert.True(undo.Redo());
        Assert.Equal(Chosen, doc.Look);

        doc.Save();
        Assert.False(doc.Modified);
        var back = MapProject.Load(path);
        Assert.Equal(Chosen, back.Mission.Look);
        var w = WorldSettings.Read(new MissionFile(path))!;
        Assert.Equal(("tp081sd6.map", "nk_6cld0.map", "p13_16.hzd"), (w.Ground, w.Sky, w.Horizon));
        Assert.Equal("Flat One", back.Mission.ArenaName);                            // the rest of the map as it was
    }

    [Fact]
    public void AMapSavedWithoutTouchingItsStyleKeepsItsFileBytes()
    {
        var (doc, path) = Open(Addon);
        doc.InitLook(MapLook.Of(WorldSettings.Read(new MissionFile(path))!), null);
        var before = File.ReadAllBytes(path);
        doc.Save();
        Assert.Equal(before, File.ReadAllBytes(path));
        // a look carried across a reload that equals the file's is no edit
        doc.InitLook(doc.FileLook, doc.FileLook! with { Ground = doc.FileLook.Ground.ToUpperInvariant() });
        Assert.False(doc.Modified);
    }

    [Fact]
    public void ALookSavedUnderANewNameLeavesTheStockFileAlone()
    {
        var (doc, stockPath) = Open(Stock, "stock2");
        var before = File.ReadAllBytes(stockPath);
        doc.InitLook(MapLook.Of(WorldSettings.Read(new MissionFile(stockPath))!), null);
        doc.SetLook(Chosen);
        doc.Save("looked", "Looked");
        Assert.Equal(before, File.ReadAllBytes(stockPath));
        Assert.Equal(Chosen, MapProject.Load(Path.Combine(Addon, "looked.cbt")).Mission.Look);
        Assert.False(doc.Modified);
    }

    [Fact]
    public void AStockMapIsNeverWrittenInPlaceItIsSavedAsACopy()
    {
        var (doc, stockPath) = Open(Stock, "stock1");
        var before = File.ReadAllBytes(stockPath);
        doc.SetHeight(4 * 128 + 10, 72 * 128 + 10, 700);
        Assert.True(doc.NeedsSaveAs);
        Assert.Throws<InvalidOperationException>(() => doc.Save());
        var written = doc.Save("mycopy", "My Copy");
        Assert.Equal(before, File.ReadAllBytes(stockPath));                       // the stock files are as they were
        Assert.All(written, f => Assert.Equal(Addon, Path.GetDirectoryName(f)));
        var back = MapProject.Load(Path.Combine(Addon, "mycopy.cbt"));
        Assert.Equal(700, back.Terrain.Height(4 * 128 + 10, 72 * 128 + 10));
        Assert.Equal("My Copy", back.Mission.ArenaName);
        Assert.Equal("mycopy.ter", back.Mission.TerrainName);
        // the document is the copy now: saving again writes the copy in place
        Assert.False(doc.NeedsSaveAs);
        Assert.Equal("mycopy", doc.BaseMission);
        Assert.Equal("My Copy", doc.Name);
        doc.SetHeight(4 * 128 + 11, 72 * 128 + 11, 710);
        doc.Save();
        Assert.Equal(710, MapProject.Load(Path.Combine(Addon, "mycopy.cbt")).Terrain.Height(4 * 128 + 11, 72 * 128 + 11));
        Assert.Equal(before, File.ReadAllBytes(stockPath));
    }

    [Fact]
    public void ACopyOfAStoryMissionIsSavedAsAMeleeMapTheListsRead()
    {
        // Crater is m01.msn: a copy keeps .msn and neither game's melee list reads it (#96)
        var (_, files) = NewMap.CreateFlat(Stock, "m01", "The Crater", 2, 10, nitro: false);
        string lvl = files.First(f => f.EndsWith(".lvl")), msn = Path.ChangeExtension(lvl, ".msn");
        File.Move(lvl, msn);
        var project = MapProject.Load(msn);
        var doc = new TerrainDocument(project.Terrain, "m01", "The Crater", msn, Addon);
        var written = doc.Save("mycrat", "My Crater");
        Assert.Contains(written, f => f.EndsWith("mycrat.lvl"));
        Assert.False(File.Exists(Path.Combine(Addon, "mycrat.msn")));
        Assert.Equal(".lvl", Path.GetExtension(doc.SourcePath));
    }

    [Fact]
    public void SaveAsRefusesBadNames()
    {
        var (doc, _) = Open(Stock, "stock2");
        Assert.Throws<ArgumentException>(() => doc.Save("way too long a name", "x"));
        Assert.Throws<ArgumentException>(() => doc.Save("ok", "a listed name over fifteen characters"));
        Assert.Throws<ArgumentException>(() => doc.Save("bad name", "x"));
        Assert.False(Directory.Exists(Addon) && Directory.GetFiles(Addon).Length > 0);
    }

    [Fact]
    public void UserMapsListsTheMapsInTheSaveFolder()
    {
        NewMap.CreateFlat(Addon, "aaa", "First Map", 1, 5, true);
        NewMap.CreateFlat(Addon, "bbb", "Second Map", 1, 5, false);
        File.WriteAllText(Path.Combine(Addon, "notes.txt"), "x");
        var maps = NewMap.UserMaps(Addon);
        Assert.Equal(["aaa", "bbb"], maps.Select(m => m.Stem));
        Assert.Equal(["First Map", "Second Map"], maps.Select(m => m.Listed));
        Assert.Empty(NewMap.UserMaps(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void ATileShownInTwoCellsSavesOnce()
    {
        var (doc, path) = Open(Addon, "twice");
        doc.SetHeight(4 * 128 + 3, 72 * 128 + 3, 333);
        doc.Save();
        Assert.Equal(333, MapProject.Load(path).Terrain.Height(4 * 128 + 3, 72 * 128 + 3));
    }
}

public class MapLookCatalogTests
{
    [Fact]
    public void TheCatalogListsTheArchivesGroundsSkiesHorizonsAndPalettes()
    {
        foreach (var env in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var install = new I76.Formats.Vfs.GameInstall(dir);
            var vfs = new I76.Formats.Vfs.AssetVfs(install);
            var cat = MapLookCatalog.Scan(vfs);
            // every one is a name the WRLD fields accept, and the maps that use them say so
            Assert.All(cat.Grounds, c => Assert.Null(MapLook.Problem(nameof(MapLook.Ground), c.Name)));
            Assert.All(cat.Skies, c => Assert.Null(MapLook.Problem(nameof(MapLook.Sky), c.Name)));
            Assert.All(cat.Horizons, c => Assert.Null(MapLook.Problem(nameof(MapLook.Horizon), c.Name)));
            Assert.All(cat.Palettes, c => Assert.Null(MapLook.Problem(nameof(MapLook.Palette), c.Name)));
            Assert.All(cat.Horizons, c => Assert.Equal(HorizonList.Panels, c.Panels!.Length));
            Assert.True(cat.Grounds.Count >= 15 && cat.Skies.Count >= 20 && cat.Horizons.Count >= 8 && cat.Palettes.Count >= 20, $"{cat.Grounds.Count} {cat.Skies.Count} {cat.Horizons.Count} {cat.Palettes.Count}");
            // what a stock mission names is in the catalogue (a horizon by any of its twins)
            foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(I76.Formats.Mission.MapFileSet.IsStockMission))
            {
                if (WorldSettings.Read(new MissionFile(f)) is not { } w) continue;
                Assert.NotNull(MapLookCatalog.Find(cat.Grounds, w.Ground));
                Assert.NotNull(MapLookCatalog.Find(cat.Skies, w.Sky));
                // (M08 names a palette, t013_16.act, that no archive holds: the engine's own typo)
                if (vfs.Exists(w.Palette)) Assert.NotNull(MapLookCatalog.Find(cat.Palettes, w.Palette));
                if (w.Horizon.Length > 0) Assert.NotNull(MapLookCatalog.Find(cat.Horizons, w.Horizon));
            }
            if (install.FindMission("t01") is { } t01 && I76.Formats.Mission.MapFileSet.IsStockMission(t01)) Assert.Contains(cat.Grounds, c => c.UsedBy.Contains("T01"));
        }
    }

    [Fact]
    public void An8BitGroundBringsItsPaletteAndA16BitOneLeavesItAlone()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var install = new I76.Formats.Vfs.GameInstall(dir);
        var vfs = new I76.Formats.Vfs.AssetVfs(install);
        var cat = MapLookCatalog.Scan(vfs);
        // the 16-bit ones carry their own colours; every 8-bit one a stock map uses has that map's palette
        Assert.All(cat.Grounds.Where(c => c.Name.StartsWith("tp", StringComparison.OrdinalIgnoreCase)), c => Assert.Null(c.Palettes));
        Assert.All(cat.Skies.Where(c => c.Name.StartsWith("nk_6", StringComparison.OrdinalIgnoreCase)), c => Assert.Null(c.Palettes));
        Assert.Contains("t04.act", MapLookCatalog.Find(cat.Grounds, "tt031sd_.map")!.Palettes!);
        // a stock map's own look is left as it is
        foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(I76.Formats.Mission.MapFileSet.IsStockMission))
            if (WorldSettings.Read(new MissionFile(f)) is { } w && vfs.Exists(w.Palette)) Assert.Equal(MapLook.Of(w), cat.Suited(MapLook.Of(w)));
        var t01 = MapLook.Of(WorldSettings.Read(new MissionFile(install.FindMission("t01")!))!);
        // T04's grey 8-bit ground on T01 brings a palette made for it; a 16-bit snow ground keeps T01's
        var grey = cat.Suited(t01 with { Ground = "tt031sd_.map" });
        Assert.Contains(grey.Palette, MapLookCatalog.Find(cat.Grounds, "tt031sd_.map")!.Palettes!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(t01.Palette, cat.Suited(t01 with { Ground = "tp181sw6.map" }).Palette);
        // and back to a 16-bit ground, the palette stays (it colours nothing then)
        Assert.Equal(grey.Palette, cat.Suited(grey with { Ground = t01.Ground }).Palette);
    }
}

public class ViewStashTests
{
    private sealed class View { public bool Disposed; }

    private static ViewStash<View> Stash() => new(v => v.Disposed = true);

    [Fact]
    public void AViewComesBackOnlyForTheRevisionItWasBuiltAt()
    {
        var stash = Stash();
        var v = new View();
        stash.Hold(v, "i76|t01", 7);
        Assert.True(stash.Holds);
        Assert.Same(v, stash.Take("i76|t01", 7));
        Assert.False(v.Disposed);
        Assert.False(stash.Holds);

        var w = new View();
        stash.Hold(w, "i76|t01", 7);
        Assert.Null(stash.Take("i76|t01", 8));      // an edit since: not reused, freed
        Assert.True(w.Disposed);

        var x = new View();
        stash.Hold(x, "i76|t01", 8);
        Assert.Null(stash.Take("nitro|t01", 8));    // another install or map
        Assert.True(x.Disposed);
    }

    [Fact]
    public void HoldingAnotherOrDroppingFreesTheFirst()
    {
        var stash = Stash();
        var a = new View(); var b = new View();
        stash.Hold(a, "k", 1);
        stash.Hold(b, "k", 1);
        Assert.True(a.Disposed);
        Assert.False(b.Disposed);
        stash.Drop();
        Assert.True(b.Disposed);
        Assert.Null(stash.Take("k", 1));
    }

    [Fact]
    public void AnEditMadeAfterTheViewWasKeptShowsInTheRevisionAndSurvivesTheReturn()
    {
        // the editor's way: keep the view at the document's revision, play, come back: same revision, same view; edit, play, come back: a new one
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        grid[0] = 0;
        int n = TerrainTiles.Tile * TerrainTiles.Tile;
        var ter = new byte[n * 2];
        var doc = new TerrainDocument(new TerrainTiles(ter, 1, grid), "t");
        var stash = Stash();
        var view = new View();
        stash.Hold(view, "k", doc.Revision);
        Assert.Same(view, stash.Take("k", doc.Revision));
        stash.Hold(view, "k", doc.Revision);
        doc.SetHeight(3, 3, 400);                    // an edit in between (it cannot be made during a playtest; the guard is for any path that does)
        Assert.Null(stash.Take("k", doc.Revision));
        Assert.True(view.Disposed);
    }
}
