using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's distant ring (HorizonTerrain) never shows above the playable ground over the map. Its polar cells (up to
/// ~100 m across at the map's far edge) straddle the map's edge, and a cell's outside corner at a wall's height used to draw a slab
/// of flat-shaded ring through the terrain (t01 up to 43 m, t05 116 m). The shader now draws the ring over a tile only where it is
/// under HorizonTerrain.FloorGrid, the lowest ground near each 10 m texel less a margin, so this holds the grid at least 1 m under both the simulation's ground and the drawn (modern) surface, which leaves it by up to 28 m on cliff faces.
/// </summary>
public class HorizonRingTests(ITestOutputHelper log)
{
    [InstallTheory]
    [InlineData("t01")]
    [InlineData("t05")]
    [InlineData("s01")]
    public void FloorGridIsUnderTheGroundOverTilesAndOpenOutside(string mission)
    {
        var vfs = TestInstall.Vfs;
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), TestInstall.Install.Resolve("miss16", mission + ".msn")!);
        var surf = new TerrainSurface(w.Terrain, TerrainSurface.Seeds(w));
        var (r0, r1, c0, c1) = w.Terrain.Bounds();
        float tw = TerrainTiles.Tile * TerrainNode.SampleSize;
        var bounds = (c0 * tw, r0 * tw, (c1 + 1) * tw, (r1 + 1) * tw);
        var tiles = new List<(float, float, float, float)>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (w.Terrain.TileAt(r, c) != MissionFile.ZmapEmpty) tiles.Add((c * tw, r * tw, (c + 1) * tw, (r + 1) * tw));
        var grid = HorizonTerrain.FloorGrid(w, surf, tiles.ToArray(), bounds, System.Numerics.Vector2.Zero, out _);
        int cols = (int)MathF.Ceiling((bounds.Item3 - bounds.Item1) / HorizonTerrain.FloorTexel);
        var rnd = new Random(7);
        int inside = 0, open = 0; double worst = double.MinValue;
        for (int k = 0; k < 40000; k++)
        {
            float x = bounds.Item1 + (float)rnd.NextDouble() * (bounds.Item3 - bounds.Item1), z = bounds.Item2 + (float)rnd.NextDouble() * (bounds.Item4 - bounds.Item2);
            float f = grid[(int)((z - bounds.Item2) / HorizonTerrain.FloorTexel) * cols + (int)((x - bounds.Item1) / HorizonTerrain.FloorTexel)];
            int r = (int)MathF.Floor(z / tw), c = (int)MathF.Floor(x / tw);
            if (w.Terrain.TileAt(r, c) == MissionFile.ZmapEmpty) { open++; Assert.True(f > 1e8f, $"open ground at {x:0},{z:0} has a floor of {f}"); continue; }
            inside++;
            worst = Math.Max(worst, f - Math.Min(w.BilinearHeight(x, z), surf.Point(x / TerrainNode.SampleSize, z / TerrainNode.SampleSize, out _).Y));
        }
        log.WriteLine($"{mission}: {inside} points over tiles, {open} off them; the floor is at most {worst:0.00} m from the lower of the ground and the drawn surface (negative: under it)");
        Assert.True(inside > 0);
        Assert.True(worst <= -1, $"the floor reaches {worst:0.0} m from {mission}'s ground");
    }
}
