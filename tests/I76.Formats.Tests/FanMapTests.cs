using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vegetation;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// Needs two fan-made maps unzipped somewhere under I76_FAN_MAPS (nr380_AB.lvl with nr380_AB.ter, and nearly73.LVL with
/// nearly73.ter) and the base install (I76_INSTALL). Tests only; third-party content: never commit it.
/// </summary>
public sealed class FanMapFactAttribute : FactAttribute
{
    public static string? Dir =>
        Environment.GetEnvironmentVariable("I76_FAN_MAPS") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public static string? Find(string name) =>
        Dir is null ? null : Directory.EnumerateFiles(Dir, name, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();

    public FanMapFactAttribute()
    {
        if (Dir is null || TestInstall.Dir is null) Skip = "set I76_FAN_MAPS to the unzipped fan maps (and I76_INSTALL) to run";
    }
}

public sealed class FanMapTests(ITestOutputHelper log)
{
    /// <summary>The mission file and the terrain it names, side by side in a temp folder (the maps' zips keep them apart): the mission's path.</summary>
    private static string Stage(string file)
    {
        var msn = FanMapFactAttribute.Find(file) ?? throw new FileNotFoundException(file);
        var dir = Path.Combine(Path.GetTempPath(), $"i76-fanmaps-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        var to = Path.Combine(dir, Path.GetFileName(msn));
        File.Copy(msn, to, true);
        var ter = new MissionFile(msn).TerrainName() ?? throw new InvalidDataException($"{file} names no terrain");
        File.Copy(FanMapFactAttribute.Find(ter) ?? throw new FileNotFoundException(ter), Path.Combine(dir, ter), true);
        return to;
    }

    private static MissionWorld Load(string lvl, string? install = null)
    {
        var vfs = new AssetVfs(new GameInstall(install ?? TestInstall.Dir!));
        return new MissionWorld(vfs, new ModelAssembler(vfs), Stage(lvl));
    }

    [FanMapFact]
    public void Nr380SurfaceFitsInMemory()
    {
        var w = Load("nr380_AB.lvl");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetTotalMemory(true);
        var surf = new TerrainSurface(w.Terrain, TerrainSurface.Seeds(w));
        long held = (GC.GetTotalMemory(true) - before) >> 20;
        log.WriteLine($"surface {sw.ElapsedMilliseconds} ms, held {held} MB");
        Assert.True(held < 400, $"nr380's surface holds {held} MB");   // its bounds are 6272 x 1024 samples; a map of 6400 cells would hold 5 GB
        GC.KeepAlive(surf);
    }

    [FanMapFact]
    public void BothMapsLoadWithTheirTerrain()
    {
        // nr380: 78 tiles over 80 cells (one tile stands in three); nearly73: 7 tiles over all 6400 cells
        foreach (var (f, tiles, huge) in new[] { ("nr380_AB.lvl", 78, false), ("nearly73.LVL", 7, true) })
        {
            var w = Load(f);
            Assert.Equal(tiles, w.Terrain.TileCount);
            Assert.Equal(0, w.Terrain.MissingTiles);
            Assert.Equal(huge, w.Terrain.Huge);
            Assert.False(string.IsNullOrEmpty(WorldSettings.ArenaName(w.Mission)));
            Assert.True(w.Placements.Count > 100);
            var (n, grid) = w.Mission.TerrainGrid();
            Assert.All(grid, id => Assert.True(id == MissionFile.ZmapEmpty || id < n));
            // the map editor opens it and writes it back as it was
            var project = I76.Formats.Mission.Edit.MapProject.Load(Stage(f), new GameInstall(TestInstall.Dir!));
            Assert.Equal(tiles, project.Terrain.TileCount);
            Assert.Equal(File.ReadAllBytes(Stage(f)), project.ToFiles()[project.MissionFileName]);
            Assert.Equal(File.ReadAllBytes(w.TerrainPath), project.ToFiles()[project.TerrainFileName]);
        }
    }

    [FanMapFact]
    public void ThePlantsOfAMapThatFillsTheGridStandOnABlockedSurface()
    {
        var w = Load("nearly73.LVL");
        var surf = new BlockedSurface(w.Terrain);
        var ground = new I76.Formats.Vegetation.SurfaceTerrain(surf, w.Terrain);
        int has = 0;
        for (int j = 0; j < 50; j++) for (int i = 0; i < 50; i++) if (ground.Has(25000 + i * 4, 25000 + j * 4)) { has++; Assert.InRange(ground.Height(25000 + i * 4, 25000 + j * 4), -1f, 20f); }
        Assert.Equal(2500, has);
        // its sward is read off the samples, not off the surface (which would make every block of the map)
        var sward = I76.Formats.Vegetation.SwardMap.Build(new I76.Formats.Vegetation.VegetationPlacer(I76.Formats.Vegetation.Biomes.For(w.Mission.Name, WorldSettings.Read(w.Mission)?.Ground)?.Biome ?? throw new InvalidOperationException("no biome"),
            new I76.Formats.Vegetation.VegetationParams(), new I76.Formats.Vegetation.TilesTerrain(w.Terrain), new I76.Formats.Vegetation.VegMask(), 1), w.Terrain);
        Assert.True(sward.Width <= 4096 && sward.Width > 0, $"{sward.Width} texels across");
        Assert.Equal(SwardMap_Huge, sward.TexelSize);
    }

    private const float SwardMap_Huge = I76.Formats.Vegetation.SwardMap.HugeTexel;
}
