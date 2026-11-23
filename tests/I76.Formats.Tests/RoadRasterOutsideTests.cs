using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Which chunks of the ground past the map's tiles carry a road layer (RoadRaster.OutsideChunks): the original draws roads, lots and
/// road pieces with no zone or tile test, so where one reaches a ZMAP cell marked empty, or past the grid, it is drawn there too. A chunk
/// is reached by bounds, as the bake culls; chunks on the map's tiles are the terrain's own.
/// </summary>
public class RoadRasterOutsideTests
{
    private const float Size = 160f;   // a chunk: 32 samples of 5 m

    /// <summary>A map of two tiles side by side at grid cells (row 1, col 1) and (row 1, col 2), every other cell empty.</summary>
    private static TerrainTiles Map()
    {
        var grid = Enumerable.Repeat(MissionFile.ZmapEmpty, MissionFile.ZmapGrid * MissionFile.ZmapGrid).ToArray();
        grid[1 * MissionFile.ZmapGrid + 1] = 0;
        grid[1 * MissionFile.ZmapGrid + 2] = 1;
        return new TerrainTiles(new byte[2 * TerrainTiles.Tile * TerrainTiles.Tile * 2], 2, grid);
    }

    // a tile is 128 samples, 640 m, four chunks a side: the map covers x 640..1920, z 640..1280
    private static RoadRaster.FlatQuad Quad(Vector2 min, Vector2 max) => new(min, new(max.X, min.Y), max, new(min.X, max.Y), 0, min, max);
    private static RoadRaster.FlatTri Tri(Vector2 min, Vector2 max) =>
        new(min, new(max.X, min.Y), max, Vector2.Zero, Vector2.UnitX, Vector2.One, 0, min, max);

    [Fact]
    public void RoadsOnTheTilesReachNoOutsideChunk()
    {
        var quads = new[] { Quad(new(700, 700), new(1800, 720)), Quad(new(650, 650), new(1910, 1270)) };
        Assert.Empty(RoadRaster.OutsideChunks(Map(), quads, [Tri(new(1000, 1000), new(1100, 1100))]));
        Assert.Empty(RoadRaster.OutsideChunks(Map(), [], []));
    }

    [Fact]
    public void ARoadOverTheTilesEdgeReachesTheEmptyCellsChunks()
    {
        // from the map's west edge out to x 500 (the chunk of samples 96, in the empty cell at column 0) along z 700..720
        var got = RoadRaster.OutsideChunks(Map(), [Quad(new(500, 700), new(700, 720))], []);
        Assert.Equal([(96, 128)], got);
        // a lot that pokes over the south edge and past the grid's corner: below z 0 the chunks are past the grid
        got = RoadRaster.OutsideChunks(Map(), [], [Tri(new(-10, -170), new(170, 650))]);
        Assert.Contains((-32, -64), got);
        Assert.Contains((32, 96), got);
        Assert.Contains((32, 128), got);   // column 0's cell at row 1: empty
        Assert.DoesNotContain((128, 128), got);   // the map's own (x 640.. is on its tiles; the lot stops at 170)
        Assert.Equal(got.Distinct().Count(), got.Count);
        Assert.Equal(got.OrderBy(o => o.Z0).ThenBy(o => o.X0), got);
    }

    [Fact]
    public void ReachIsByBoundsAsTheBakeCulls()
    {
        // a quad whose bounds end on a chunk's first metre reaches it (Bake paints max >= origin); one whose bounds start on a chunk's
        // first metre does not reach the chunk before
        Assert.Equal([(0, 0), (32, 0)], RoadRaster.OutsideChunks(Map(), [Quad(new(10, 10), new(Size, 20))], []));
        Assert.Equal([(32, 0)], RoadRaster.OutsideChunks(Map(), [Quad(new(Size, 10), new(Size + 20, 20))], []));
    }

    [Fact]
    public void AnEmptyCellInsideTheMapIsOutside()
    {
        var map = Map();
        map.Grid[1 * MissionFile.ZmapGrid + 2] = MissionFile.ZmapEmpty;   // the second tile's cell emptied
        var got = RoadRaster.OutsideChunks(map, [Quad(new(1250, 900), new(1300, 910))], []);
        Assert.Equal([(256, 160)], got);   // x 1280..1440, z 800..960: the emptied cell's first chunk
    }
}
