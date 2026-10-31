using System.Numerics;
using I76.Editor;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Editor.Tests;

/// <summary>The ground's surface nibble under roads the tool draws, moves, retypes and bulldozes (the stock pattern: docs/map-format-writing.md, "The ground under a road").</summary>
public class RoadSurfaceTests(ITestOutputHelper output)
{
    private static TerrainDocument Flat(int nibble = 0)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;
        var ter = new byte[4 * TerrainTiles.Tile * TerrainTiles.Tile * 2];
        for (int i = 0; i < ter.Length; i += 2) { ter[i] = 0xc8; ter[i + 1] = (byte)(nibble << 4); }
        return new TerrainDocument(new TerrainTiles(ter, 4, grid), "flat");
    }

    private static int Nib(TerrainDocument d, int sx, int sz) => (d.Tiles.Sample(sx, sz) ?? 0) >> 12;
    private static int At(TerrainDocument d, float x, float z) => Nib(d, (int)MathF.Round(x / 5), (int)MathF.Round(z / 5));

    private static RoadCommand Draw(TerrainDocument doc, Vector2 a, Vector2 b, uint kind = 0, bool conform = true)
    {
        var plan = RoadPlanner.Plan(RoadCurves.Resample([a, b]), kind, null, null, doc.Roads);
        Assert.Null(plan.Problem);
        return RoadPlanner.Commit(doc, doc.Roads, plan, conform)!.Value.Command;
    }

    [Theory]
    [InlineData(0u, 4)]
    [InlineData(1u, 6)]
    [InlineData(2u, 8)]
    [InlineData(3u, 4)]
    public void ANewRoadGetsItsKindsNibbleOverTheBandAndNothingBeyondIt(uint kind, int nib)
    {
        var doc = Flat();
        Draw(doc, new(300, 400), new(700, 400), kind);
        for (float x = 310; x <= 690; x += 5)
        {
            Assert.Equal(nib, At(doc, x, 400));
            Assert.Equal(nib, At(doc, x, 400 + 10));
            Assert.Equal(nib, At(doc, x, 400 - 10));
            Assert.Equal(0, At(doc, x, 400 + 15));
            Assert.Equal(0, At(doc, x, 400 - 15));
        }
        Assert.Equal(0, At(doc, 290, 400));                 // the band stops at the road's end
        Assert.Equal(0, At(doc, 710, 400));
        Assert.Equal(nib, At(doc, 300, 400));
    }

    [Fact]
    public void TheHeightsAndTheLowBitOfTheNibbleAreNotTouched()
    {
        var doc = Flat(nibble: 5);                           // impassable-flag ground, as a stock map has at its edges
        Draw(doc, new(300, 400), new(700, 400), conform: false);
        Assert.Equal(4, At(doc, 500, 400));                  // a road is never impassable
        Assert.Equal(0xc8, doc.GetHeight(100, 80));
    }

    [Fact]
    public void WithoutConformingTheHeightsAreStillLeftAloneButTheSurfaceIsLaid()
    {
        var doc = Flat();
        var h0 = doc.Snapshot().Select(s => s & 0x0fff).ToArray();
        Draw(doc, new(300, 400), new(700, 400), conform: false);
        Assert.Equal(h0, doc.Snapshot().Select(s => s & 0x0fff).ToArray());
        Assert.Equal(4, At(doc, 500, 400));
    }

    [Fact]
    public void UndoRestoresTheOriginalGroundAndRedoLaysTheRoadAgain()
    {
        var doc = Flat(nibble: 3);
        var snap0 = doc.Snapshot();
        var undo = new UndoStack(doc);
        undo.Push(Draw(doc, new(300, 400), new(700, 400)));
        var snap1 = doc.Snapshot();
        Assert.NotEqual(snap0, snap1);
        undo.Undo();
        Assert.Equal(snap0, doc.Snapshot());
        Assert.Equal(3, At(doc, 500, 400));
        undo.Redo();
        Assert.Equal(snap1, doc.Snapshot());
    }

    [Fact]
    public void BulldozingGivesTheGroundBesideTheRoadBackAndUndoLaysTheRoadAgain()
    {
        var doc = Flat(nibble: 3);
        var undo = new UndoStack(doc);
        undo.Push(Draw(doc, new(300, 400), new(700, 400)));
        var withRoad = doc.Snapshot();
        Assert.Equal(4, At(doc, 500, 400));
        undo.Push(RoadEditing.Bulldoze(doc, doc.Roads, 0));
        Assert.Equal(0, doc.Roads.Count);
        for (float x = 290; x <= 710; x += 5) for (float z = 380; z <= 420; z += 5) Assert.Equal(3, At(doc, x, z));
        undo.Undo();
        Assert.Equal(withRoad, doc.Snapshot());
        Assert.Equal(1, doc.Roads.Count);
    }

    [Fact]
    public void RetypingChangesTheNibbleInTheSameStep()
    {
        var doc = Flat();
        var undo = new UndoStack(doc);
        undo.Push(Draw(doc, new(300, 400), new(700, 400)));
        var snap = doc.Snapshot();
        undo.Push(RoadEditing.Retype(doc, doc.Roads, 0, 1));
        Assert.Equal(6, At(doc, 500, 400));
        Assert.Equal(6, At(doc, 500, 410));
        undo.Undo();
        Assert.Equal(snap, doc.Snapshot());
        Assert.Equal(0u, doc.Roads[0].Kind);
    }

    [Fact]
    public void MovingARoadAwayClearsWhatItVacatedAndPaintsWhereItWent()
    {
        var doc = Flat();
        var undo = new UndoStack(doc);
        undo.Push(Draw(doc, new(300, 400), new(700, 400)));
        var snap = doc.Snapshot();
        var road = doc.Roads[0];
        int mid = road.Points.Count / 2;
        var moved = RoadOps.MovePoint(road, mid, new Vector2(500, 520), 6);
        undo.Push(RoadEditing.Replace(doc, doc.Roads, 0, moved, 0, road.Points.Count - 1, conform: false));
        Assert.Equal(0, At(doc, 500, 400));                   // the bend left the old line
        Assert.Equal(4, At(doc, 500, 520));
        Assert.Equal(4, At(doc, 300, 400));                   // the ends stayed
        undo.Undo();
        Assert.Equal(snap, doc.Snapshot());
    }

    [Fact]
    public void AnotherRoadsBandSurvivesWhenOneIsBulldozed()
    {
        var doc = Flat();
        Draw(doc, new(300, 400), new(700, 400), kind: 0);
        Draw(doc, new(500, 300), new(500, 500), kind: 1);     // crosses the first
        Assert.Equal(6, At(doc, 500, 400));                   // the later one wins at the crossing
        RoadEditing.Bulldoze(doc, doc.Roads, 1);
        Assert.Equal(4, At(doc, 500, 400));                   // the first road's ground is still its own
        Assert.Equal(4, At(doc, 400, 400));
        Assert.Equal(0, At(doc, 500, 300));
    }

    [Fact]
    public void GroundUnderAnIntersectionPieceKeepsItsNibble()
    {
        var doc = Flat();
        int sx = 100, sz = 80;
        Assert.True(doc.Locate(sx, sz, out int tile, out int off));
        doc.SetSample(tile, off, (ushort)(doc.GetSample(tile, off) | 0xc000));
        Draw(doc, new(300, 400), new(700, 400));
        Assert.Equal(0xc, Nib(doc, sx, sz));
        Assert.Equal(4, Nib(doc, sx + 1, sz));
    }

    [Fact]
    public void StockRoadsHaveTheirKindsNibbleOverTheBandWeLay()
    {
        int cells = 0, ok = 0, piece = 0, roads = 0;
        foreach (var (env, ext) in new[] { ("I76_INSTALL", ".msn"), ("I76_NITRO_INSTALL", ".cbt") })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var install = new GameInstall(dir);
            foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(I76.Formats.Mission.MapFileSet.IsStockMission).Order())
            {
                MapProject proj;
                try { proj = MapProject.Load(f, install); } catch { continue; }
                foreach (var r in proj.Mission.Roads)
                {
                    if (r.Points.Count < 2) continue;
                    roads++;
                    var edit = new RoadEdit(r.Kind, r.Points.Select(p => RoadBuilder.Point(p.Centre, new Vector2(0, 1))).ToList());
                    int want = RoadSurface.Nibble(r.Kind);
                    foreach (var (x, z) in RoadSurface.Cells(edit))
                    {
                        if (proj.Terrain.Sample(x, z) is not { } s) continue;
                        cells++;
                        int n = s >> 12;
                        if (n == want) ok++; else if (n is 0xc or 0xe) { piece++; ok++; }
                    }
                }
            }
        }
        if (cells == 0) return;
        output.WriteLine($"{roads} stock roads, {cells} band samples, {ok} with the kind's nibble or a piece's ({piece} pieces)");
        Assert.True(ok >= 0.9 * cells, $"{ok} of {cells}");
    }
}
