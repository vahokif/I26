using System.Collections.Concurrent;
using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// The road layers are a cache key's worth of pixels: the row-span rasteriser with its chunk index must paint every
/// chunk of a mission exactly as the bounding-box scan before it did (RoadRasterReference), byte for byte.
/// </summary>
public class RoadRasterTests(ITestOutputHelper log)
{
    [InstallTheory]
    [InlineData("t01")]
    [InlineData("t02")]
    [InlineData("m05")]
    public void LayersMatchTheBoundingBoxScanByteForByte(string mission) => Compare(TestInstall.Install, TestInstall.Vfs, mission);

    /// <summary>A big Nitro map (P17: 40 tiles, 5891 road quads), when I76_NITRO_INSTALL names the Nitro Pack.</summary>
    [Fact]
    public void NitroLayersMatchTheBoundingBoxScanByteForByte()
    {
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var install = new GameInstall(dir);
        Compare(install, new AssetVfs(install), "p17");
    }

    private void Compare(GameInstall install, AssetVfs vfs, string mission)
    {
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), install.FindMission(mission)!);
        var pal = new Palette(vfs.TryRead(w.PaletteName!)!);
        var resolver = new TextureResolver(vfs);
        var flat = RoadRaster.Flatten(Roads.Build(w.Mission, w.BilinearHeight));
        var names = Roads.TextureList(vfs.TryRead, w.Mission.Name);
        var tex = names.Select(n => resolver.FindByStem(n) is { } m && resolver.Load(m) is { } t ? new RoadRaster.Tex(t.Width, t.Height, TextureReader.ToRgba(t, pal)) : null).ToList();
        // piece tops stand-ins (the game makes them from road-piece models): triangles over the roads, repeating UVs,
        // a small keyed texture, painted with no feather after the quads
        var rng = new Random(76);
        var key = new byte[8 * 8 * 4];
        rng.NextBytes(key);
        for (int i = 3; i < key.Length; i += 4) key[i] = (byte)(rng.Next(4) == 0 ? 0 : 255);
        tex.Add(new RoadRaster.Tex(8, 8, key));
        var tris = new List<RoadRaster.FlatTri>();
        for (int i = 0; i < flat.Count; i += 7)
        {
            var q = flat[i];
            var off = new Vector2(rng.NextSingle() * 6 - 3, rng.NextSingle() * 6 - 3);
            Vector2 a = q.A + off, b = q.B + off, c = q.C + off;
            tris.Add(new RoadRaster.FlatTri(a, b, c, new(0, 0), new(rng.NextSingle() * 3, 0), new(rng.NextSingle() * 3, 2.5f), tex.Count - 1,
                Vector2.Min(Vector2.Min(a, b), c), Vector2.Max(Vector2.Max(a, b), c)));
        }
        var origins = new List<Vector2>();
        for (int row = 0; row < 80; row++)
            for (int col = 0; col < 80; col++)
                if (w.Terrain.TileAt(row, col) != Mission.MissionFile.ZmapEmpty)
                    for (int cz = 0; cz < 128; cz += 32)
                        for (int cx = 0; cx < 128; cx += 32)
                            origins.Add(new Vector2((col * 128 + cx) * 5f, (row * 128 + cz) * 5f));
        var index = new RoadRaster.Index(flat, tris, 160f);
        Func<int, RoadRaster.Tex?> lookup = i => i < tex.Count ? tex[i] : null;
        int painted = 0;
        var bad = new ConcurrentBag<string>();
        Parallel.ForEach(origins, o =>
        {
            var want = RoadRasterReference.Bake(flat, o, 160f, 1024, lookup, tris: tris);
            var got = RoadRaster.Bake(flat, o, 160f, 1024, lookup, tris: tris, index: index);
            if (want is null != got is null || want is not null && !want.AsSpan().SequenceEqual(got)) bad.Add($"{o}");
            if (want is not null) Interlocked.Increment(ref painted);
        });
        log.WriteLine($"{mission}: {origins.Count} chunks, {painted} painted, {flat.Count} quads, {tris.Count} triangles");
        Assert.True(painted > 0);
        Assert.Empty(bad);
    }
}
