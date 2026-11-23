using I76.Editor;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>The map editor's surface table: the presets, the names the surface tool shows for edited rows, undo, and saving.</summary>
public class SurfaceTableEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-editor-surftable-" + Guid.NewGuid().ToString("N"));
    private string Addon => Path.Combine(_root, "addon");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private (TerrainDocument Doc, string Path) Open(string name = "stab1")
    {
        var (_, files) = NewMap.CreateFlat(Addon, name, "Surface Table", 2, 10, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        var project = MapProject.Load(mission);
        var doc = new TerrainDocument(project.Terrain, name, "Surface Table", mission, Addon);
        doc.InitSurfaces(project.Mission.Surfaces, null);
        return (doc, mission);
    }

    [Fact]
    public void PresetsSetTheirFieldsAndKeepTheRest()
    {
        var sand = SurfaceTable.Stock[1];   // grip 1.5, rolling 0.1, damage 3
        var ice = SurfacePresets.BlackIce.Apply(1, sand);
        Assert.Equal(new SurfaceRow(0.2f, 0.01f, 0, 0, 3), ice);
        Assert.Equal(new SurfaceRow(0.5f, 0.01f, 0, 0, 3), SurfacePresets.Ice.Apply(1, sand));
        var hurt = SurfacePresets.Damaging.Apply(1, sand);
        Assert.Equal(sand with { Damage = 200 }, hurt);
        Assert.Equal(999999999, SurfacePresets.Stripping.Apply(0, SurfaceTable.Stock[0]).Damage);
        Assert.Equal(new SurfaceRow(1.0f, 0.5f, 0.5f, 0, 14), SurfacePresets.Rough.Apply(0, SurfaceTable.Stock[0]));
        Assert.Equal(1.2f, SurfacePresets.Snow.Apply(3, SurfaceTable.Stock[3]).Grip);
        Assert.Equal(0, SurfacePresets.Harmless.Apply(5, SurfaceTable.Stock[5]).Damage);
        for (int id = 0; id < SurfaceTable.Count; id++)
            Assert.True(SurfacePresets.Stock.Apply(id, ice).Same(SurfaceTable.Stock[id]));
        Assert.Equal(SurfacePresets.All.Length, SurfacePresets.All.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void TheToolsNamesFollowTheTable()
    {
        for (int id = 0; id < SurfaceTable.Count; id++) Assert.Equal(SurfaceTypes.All[id].Name, SurfacePresets.Label(id, SurfaceTable.Stock));
        Assert.Equal("DIRT ROAD", SurfacePresets.Label(3, (SurfaceTable?)null));
        var t = SurfaceTable.Stock
            .With(0, SurfacePresets.Ice.Apply(0, SurfaceTable.Stock[0]))
            .With(1, SurfacePresets.Damaging.Apply(1, SurfaceTable.Stock[1]))
            .With(2, SurfacePresets.Stripping.Apply(2, SurfacePresets.BlackIce.Apply(2, SurfaceTable.Stock[2])))
            .With(3, SurfacePresets.Snow.Apply(3, SurfaceTable.Stock[3]))
            .With(5, SurfacePresets.Rough.Apply(5, SurfaceTable.Stock[5]));
        Assert.Equal("ICE", SurfacePresets.Label(0, t));
        Assert.Equal("DAMAGING", SurfacePresets.Label(1, t));
        Assert.Equal("ICE + STRIPPING", SurfacePresets.Label(2, t));
        Assert.Equal("DIRT ROAD", SurfacePresets.Label(3, t));   // snow: less grip than stock, but stock maps have 1.0 to 1.2 too
        Assert.Equal("PLANKS", SurfacePresets.Label(4, t));
        Assert.Equal("GRAVEL", SurfacePresets.Label(5, t));
        // a stock map's own odd row (Mojave Sprint's dirt, grip 1.1 and 2 a second) is not ice nor damaging
        Assert.Equal("DIRT", SurfacePresets.Label(0, new SurfaceRow(1.1f, 0.03f, 0, 0, 2)));
        Assert.Equal("ICE", SurfacePresets.Label(0, new SurfaceRow(0.99f, 0.03f, 0, 0, 99)));
        Assert.Equal("DAMAGING", SurfacePresets.Label(0, new SurfaceRow(1f, 0.03f, 0, 0, 100)));
    }

    [Fact]
    public void AnEditIsOneUndoStepAndMarksTheMapModified()
    {
        var (doc, _) = Open();
        Assert.False(doc.Modified);
        long rev = doc.Revision;
        int fired = 0;
        doc.SurfacesChanged += () => fired++;
        var undo = new UndoStack(doc);
        var before = doc.Surfaces!;
        var after = before.With(4, SurfacePresets.Ice.Apply(4, before[4]));
        undo.Push(new SurfaceTableCommand("surface planks: ice", before, after));
        doc.SetSurfaces(after);
        Assert.True(doc.Modified);
        Assert.True(doc.Revision > rev);
        Assert.Equal(1, fired);
        Assert.True(undo.Undo());
        Assert.Equal(before, doc.Surfaces);
        Assert.False(doc.Modified);
        Assert.True(undo.Redo());
        Assert.Equal(after, doc.Surfaces);
        Assert.Equal(3, fired);
        doc.SetSurfaces(after);   // the same table: no change
        Assert.Equal(3, fired);
    }

    [Fact]
    public void TheTableIsSavedAndReadBack()
    {
        var (doc, path) = Open();
        var t = doc.Surfaces!.With(0, SurfacePresets.Ice.Apply(0, doc.Surfaces[0])).With(5, SurfacePresets.Stripping.Apply(5, doc.Surfaces[5]));
        doc.SetSurfaces(t);
        doc.Save();
        Assert.False(doc.Modified);
        Assert.Equal(t, doc.FileSurfaces);
        Assert.Equal(t, MapProject.Load(path).Mission.Surfaces);
        Assert.Equal(t, SurfaceTable.Of(WorldSettings.Read(new MissionFile(path))!));
        // saved again without an edit: the same bytes
        var bytes = File.ReadAllBytes(path);
        var again = new TerrainDocument(MapProject.Load(path).Terrain, "stab1", "Surface Table", path, Addon);
        again.InitSurfaces(MapProject.Load(path).Mission.Surfaces, null);
        Assert.False(again.Modified);
        again.Save();
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void AStockMapSavedUnderANewNameCarriesItsEditedTable()
    {
        var (doc, path) = Open("stab2");
        var original = doc.Surfaces;
        // the document read from one folder and saved into another: a copy under a new name, as a stock map's first save
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var copy = new TerrainDocument(MapProject.Load(path).Terrain, "stab2", "Surface Table", path, other);
        copy.InitSurfaces(MapProject.Load(path).Mission.Surfaces, null);
        var t = copy.Surfaces!.With(1, SurfacePresets.Damaging.Apply(1, copy.Surfaces[1]));
        copy.SetSurfaces(t);
        copy.Save("stab3", "Surface Copy");
        Assert.Equal(t, MapProject.Load(copy.SourcePath!).Mission.Surfaces);
        Assert.Equal(original, MapProject.Load(path).Mission.Surfaces);   // the original untouched
    }
}
