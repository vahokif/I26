using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>A map that fills the 80x80 grid is drawn from blocks of the surface (BlockedSurface), not one array over all of it.</summary>
public sealed class BlockedSurfaceTests
{
    private const int T = TerrainTiles.Tile;

    /// <summary>cols x rows cells of rolling ground with a cliff band and a few needles, each cell its own tile.</summary>
    private static TerrainTiles Land(int cols, int rows, int gridCols = MissionFile.ZmapGrid)
    {
        int tiles = cols * rows;
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) grid[r * MissionFile.ZmapGrid + c] = (byte)(r * cols + c);
        var ter = new byte[tiles * T * T * 2];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                for (int z = 0; z < T; z++)
                    for (int x = 0; x < T; x++)
                    {
                        int gx = c * T + x, gz = r * T + z;
                        double h = 300 + 60 * Math.Sin(gx * 0.07) + 50 * Math.Cos(gz * 0.05);
                        if (gx > 300 && gx < 340) h += 400;   // a mesa wall: steep, so there is ground no car reaches
                        if ((gx * 7 + gz * 13) % 211 == 0) h += 250;   // needles
                        int i = ((r * cols + c) * T * T + z * T + x) * 2;
                        ushort v = (ushort)h;
                        ter[i] = (byte)v; ter[i + 1] = (byte)(v >> 8);
                    }
        return new TerrainTiles(ter, tiles, grid);
    }

    [Fact]
    public void ABlockGivesTheSurfaceAWholeMapGivesInsideTheMap()
    {
        var land = Land(5, 5);
        var whole = new TerrainSurface(land);   // seedless: every gentle cell open
        var blocked = new BlockedSurface(land);
        double worst = 0;
        int n = 0;
        // well inside the map (the halo and the map's edge repeat differ at its rim), across several blocks
        for (double fz = 80; fz <= 560; fz += 7.3)
            for (double fx = 80; fx <= 560; fx += 6.1)
            {
                var a = whole.Point(fx, fz, out double sa); var b = blocked.Point(fx, fz, out double sb);
                worst = Math.Max(worst, Math.Max(Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z), Math.Abs(a.Y - b.Y)));
                worst = Math.Max(worst, Math.Abs(sa - sb));
                Assert.Equal(whole.Open((int)fx, (int)fz), blocked.Open((int)fx, (int)fz));
                var na = whole.BroadNormal(fx, fz); var nb = blocked.BroadNormal(fx, fz);
                worst = Math.Max(worst, (na - nb).Length());
                n++;
            }
        Assert.True(worst < 1e-3, $"{n} points: blocked surface is {worst} off the whole one");
        Assert.True(blocked.BlockCount > 4);
    }

    [Fact]
    public void BlocksAreLetGoOfPastTheLimit()
    {
        var land = Land(5, 5);
        var blocked = new BlockedSurface(land, maxBlocks: 4);
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) blocked.Point(i * 256 + 100.5, j * 256 + 100.5, out _);
        Assert.True(blocked.BlockCount <= 4, $"{blocked.BlockCount} blocks held");
        // a block asked for again is made again, the same
        var again = blocked.Point(100.5, 100.5, out _);
        Assert.Equal(new TerrainSurface(land).Point(100.5, 100.5, out _).Y, again.Y, 3);
    }

    [Fact]
    public void OnlyAMapSpanningManyCellsIsHuge()
    {
        Assert.False(Land(5, 5).Huge);
        // 80 x 80 cells: every cell a tile of its own would be 6400; one tile over the whole grid is the fan maps' way
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        for (int i = 0; i < grid.Length; i++) grid[i] = (byte)(i % 7);
        var huge = new TerrainTiles(new byte[7 * T * T * 2], 7, grid);
        Assert.True(huge.Huge);
        Assert.Equal((byte)3, huge.TileAt(0, 3));
        Assert.Equal(huge.Sample(3 * T + 5, 9), huge.Sample(10 * T + 5, 9));   // one tile, many cells
    }
}
