using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// The modern look's ground over walls and towers far taller than any stock map's, up to the most the format holds
/// (12 bits, 409.5 m): it stands as tall as the simulation's (and the 1997 look's), within the surface's freedom.
/// </summary>
public class TallTerrainTests(ITestOutputHelper log)
{
    private const int T = TerrainTiles.Tile;
    private const int Floor = 100;   // raw: 10 m

    /// <summary>One tile of level ground with the given raw heights painted over rectangles (x0, z0, x1, z1 inclusive).</summary>
    private static TerrainTiles Tile(params (int X0, int Z0, int X1, int Z1, int Raw)[] blocks)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        grid[0] = 0;
        var ter = new byte[T * T * 2];
        for (int z = 0; z < T; z++)
            for (int x = 0; x < T; x++)
            {
                int h = Floor;
                foreach (var b in blocks) if (x >= b.X0 && x <= b.X1 && z >= b.Z0 && z <= b.Z1) h = b.Raw;
                ter[(z * T + x) * 2] = (byte)h; ter[(z * T + x) * 2 + 1] = (byte)(h >> 8);
            }
        return new TerrainTiles(ter, 1, grid);
    }

    /// <summary>The highest the drawn ground stands over a rectangle of samples, on the finest level's grid (1.25 m).</summary>
    private static double DrawnTop(TerrainSurface s, int x0, int z0, int x1, int z1)
    {
        double top = double.MinValue;
        for (double fz = z0 - 1; fz <= z1 + 1; fz += 0.25)
            for (double fx = x0 - 1; fx <= x1 + 1; fx += 0.25) top = Math.Max(top, s.Point(fx, fz, out _).Y);
        return top;
    }

    public static TheoryData<int, int> Walls => new()
    {
        // thickness in samples, raw height: thin walls of a custom map, up to the format's top (4095 = 409.5 m)
        { 1, 3000 }, { 2, 3000 }, { 2, 4095 }, { 3, 1500 }, { 6, 2000 }, { 10, 4095 }, { 2, 600 },
    };

    [Theory]
    [MemberData(nameof(Walls))]
    public void AWallStandsAsTallAsTheSimulationsAtAnyHeight(int thick, int raw)
    {
        // a wall across the tile, ground a car reaches on both sides (a seed in the middle of each)
        var tiles = Tile((60, 10, 60 + thick - 1, 117, raw));
        var s = new TerrainSurface(tiles, [new(30.5f, 64.5f), new(100.5f, 64.5f)]);
        double sim = raw * MissionWorld.HeightScale;
        double top = DrawnTop(s, 60, 30, 60 + thick - 1, 98);
        log.WriteLine($"wall {thick} samples thick, {sim:0.0} m: drawn top {top:0.0} m");
        Assert.InRange(top, sim - TerrainSurface.MaxFree, sim + TerrainSurface.MaxFree);
        // and no point further off the simulation's height than HeightRange allows for (the chunks' boxes, which culling reads):
        // the freedom, and the relief along the normal on top
        double worst = 0;
        for (double fz = 30; fz <= 98; fz += 0.25)
            for (double fx = 55; fx <= 60 + thick + 4; fx += 0.25)
                worst = Math.Max(worst, Math.Abs(s.Point(fx, fz, out _).Y - s.Bilinear(fx, fz)));
        log.WriteLine($"  most off the simulation's: {worst:0.0} m");
        Assert.True(worst <= 2 * TerrainSurface.MaxFree, $"{worst}");
        var (lo, hi) = s.HeightRange(56, 32, 32);
        Assert.True(lo <= Floor * MissionWorld.HeightScale && hi >= top, $"{lo}..{hi} against {top}");
    }

    [Fact]
    public void ATowerWithARoadOnTopKeepsItsTop()
    {
        // two towers 160 m tall, 4 samples a side (3x3 level cells on top), a road (seeds) over both, a gap between them for a bridge
        var tiles = Tile((40, 40, 43, 43, 1700), (40, 52, 43, 55, 1700));
        var s = new TerrainSurface(tiles, [new(20.5f, 64.5f), new(41.5f, 41.5f), new(41.5f, 53.5f)]);
        Assert.True(s.Open(41, 41) && s.Open(41, 53));
        double sim = 170;
        // the deck lands on the towers' tops: the drawn top over each, and at its middle the simulation's
        Assert.InRange(DrawnTop(s, 40, 40, 43, 43), sim - 0.1, sim + TerrainSurface.MaxFree);
        Assert.InRange(s.Point(41.5, 53.5, out _).Y, sim - 0.1, sim + 0.1);
        // a narrower tower no road reaches (2 samples a side, a single level cell on top too steep at its rim to be gentle)
        var thin = new TerrainSurface(Tile((40, 40, 41, 41, 1700)), [new(20.5f, 64.5f)]);
        double top = DrawnTop(thin, 40, 40, 41, 41);
        log.WriteLine($"thin tower 170 m: drawn top {top:0.0} m");
        Assert.InRange(top, sim - TerrainSurface.MaxFree, sim + TerrainSurface.MaxFree);
    }
}
