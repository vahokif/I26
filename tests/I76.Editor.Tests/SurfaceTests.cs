using I76.Editor;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>The surface brush: a hard-edged disc of one surface id (.ter bits 13-15), the height and the impassable flag kept; one undo step; saved and read back.</summary>
public class SurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-editor-surface-" + Guid.NewGuid().ToString("N"));
    private string Addon => Path.Combine(_root, "addon");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static BrushSettings Paint(int id, float radius = 40) => new() { Kind = BrushKind.Surface, Radius = radius, SurfaceId = id };

    private (TerrainDocument Doc, string Path) Open()
    {
        var (_, files) = NewMap.CreateFlat(Addon, "surf1", "Surface One", 2, 10, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        return (new TerrainDocument(MapProject.Load(mission).Terrain, "surf1", "Surface One", mission, Addon), mission);
    }

    private const int Cx = 4 * 128 + 64, Cz = 72 * 128 + 64;

    private static int Id(IMapDocument d, int sx, int sz) { d.Locate(sx, sz, out int t, out int o); return SurfaceTypes.Of(d.GetSample(t, o)); }

    [Fact]
    public void ADiscIsPaintedHardEdgedAndTheHeightAndFlagAreKept()
    {
        var (doc, _) = Open();
        doc.Locate(Cx + 1, Cz, out int t1, out int o1);
        doc.SetSample(t1, o1, (ushort)(doc.GetSample(t1, o1) | 0x1000));   // an impassable sample inside the disc
        int h = doc.GetHeight(Cx, Cz);
        var s = new BrushStroke(doc, Paint(3, radius: 25));   // 5 samples
        Assert.NotNull(s.Stamp(Cx, Cz, 0.1));
        Assert.Equal(3, Id(doc, Cx, Cz));
        Assert.Equal(3, Id(doc, Cx + 5, Cz));
        Assert.Equal(0, Id(doc, Cx + 6, Cz));
        Assert.Equal(0, Id(doc, Cx + 4, Cz + 4));   // 5.66 samples away: outside
        Assert.Equal(h, doc.GetHeight(Cx, Cz));
        Assert.Equal(0x1000, doc.GetSample(t1, o1) & 0x1000);
        Assert.Null(s.Stamp(Cx, Cz, 0.1));          // nothing left to change
    }

    [Fact]
    public void AStrokeIsOneUndoStepAndRedoRepaints()
    {
        var (doc, _) = Open();
        var before = doc.Snapshot();
        var undo = new UndoStack(doc);
        var s = new BrushStroke(doc, Paint(6, radius: 30));
        s.Stamp(Cx, Cz, 0.1); s.Stamp(Cx + 8, Cz, 0.1);
        undo.Push(s.End()!);
        var painted = doc.Snapshot();
        Assert.NotEqual(before, painted);
        Assert.Equal(6, Id(doc, Cx + 12, Cz));
        Assert.True(undo.Undo());
        Assert.Equal(before, doc.Snapshot());
        Assert.True(undo.Redo());
        Assert.Equal(painted, doc.Snapshot());
    }

    [Fact]
    public void PaintedSurfacesAreSaved()
    {
        var (doc, path) = Open();
        var s = new BrushStroke(doc, Paint(5, radius: 30));
        s.Stamp(Cx, Cz, 0.1);
        new BrushStroke(doc, Paint(2, radius: 10)).Stamp(Cx + 20, Cz, 0.1);
        Assert.True(doc.Modified);
        doc.Save();
        var back = MapProject.Load(path).Terrain;
        for (int dx = -6; dx <= 6; dx++)
            Assert.Equal(doc.Tiles.Sample(Cx + dx, Cz), back.Sample(Cx + dx, Cz));
        Assert.Equal(5, (back.Sample(Cx, Cz) ?? 0) >> 13 & 7);
        Assert.Equal(2, (back.Sample(Cx + 20, Cz) ?? 0) >> 13 & 7);
        Assert.Equal(0, (back.Sample(Cx + 40, Cz) ?? 0) >> 13 & 7);
        Assert.Equal(100, back.Height(Cx, Cz));   // untouched heights
    }

    [Fact]
    public void EveryClassHasANameAColourAndWithKeepsTheRest()
    {
        Assert.Equal(SurfaceTypes.Count, SurfaceTypes.All.Length);
        Assert.Equal(SurfaceTypes.Count, SurfaceTypes.All.Select(i => i.Name).Distinct().Count());
        Assert.Equal(SurfaceTypes.Count, SurfaceTypes.All.Select(i => (i.R, i.G, i.B)).Distinct().Count());
        for (int id = 0; id < SurfaceTypes.Count; id++)
        {
            Assert.Equal(id, SurfaceTypes.All[id].Id);
            ushort w = SurfaceTypes.With(0x1abc, id);
            Assert.Equal(id, SurfaceTypes.Of(w));
            Assert.Equal(0x1abc & 0x1fff, w & 0x1fff);
        }
    }
}
