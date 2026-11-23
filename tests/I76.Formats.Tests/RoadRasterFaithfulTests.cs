using System.Numerics;
using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Which chunks the 1997 look bakes a road layer of its own for (RoadRaster.Differing, OwnFaithful): where a gap fill or a texture the
/// looks draw differently (a stock texture's slip, TextureRepairs) reaches. The loader and the repaint after a destroyed structure's
/// ground faces are painted out (RoadPainter) both ask it, so a repaint keeps the 1997 look's slip.
/// </summary>
public class RoadRasterFaithfulTests
{
    private static readonly RoadRaster.Tex Road = new(1, 1, [1, 2, 3, 255]), Fixed = new(1, 1, [4, 5, 6, 255]), Slip = new(1, 1, [0, 255, 0, 255]);

    private static RoadRaster.FlatTri Tri(int texture, Vector2 min, Vector2 max) =>
        new(min, new(max.X, min.Y), max, Vector2.Zero, Vector2.UnitX, Vector2.One, texture, min, max);

    private static RoadRaster.FlatQuad Quad(Vector2 min, Vector2 max) =>
        new(min, new(max.X, min.Y), max, new(min.X, max.Y), 0, min, max);

    [Fact]
    public void OneSetDrawsNothingDifferently()
    {
        var set = new Dictionary<int, RoadRaster.Tex?> { [0] = Road, [1] = Fixed };
        Assert.Empty(RoadRaster.Differing(set, set));
        // a copy with the same textures (the 1997 look's, of a mission with no slip) neither
        Assert.Empty(RoadRaster.Differing(set, new Dictionary<int, RoadRaster.Tex?>(set)));
    }

    [Fact]
    public void TheRepairedNumberDiffers()
    {
        var modern = new Dictionary<int, RoadRaster.Tex?> { [0] = Road, [1] = Fixed, [2] = null };
        var faithful = new Dictionary<int, RoadRaster.Tex?>(modern) { [1] = Slip };
        Assert.Equal(new[] { 1 }, RoadRaster.Differing(modern, faithful));
    }

    [Fact]
    public void AChunkHasItsOwnLayerWhereAFillOrARepairedTriangleReachesIt()
    {
        const float chunk = 160f;
        var differing = new HashSet<int> { 7 };
        var repaired = Tri(7, new(150, 10), new(170, 30));   // across the line between chunks (0,0) and (160,0)
        var plain = Tri(3, new(400, 10), new(420, 30));
        var fill = Quad(new(10, 330), new(20, 340));
        List<RoadRaster.FlatQuad> fills = [fill];
        List<RoadRaster.FlatTri> tris = [plain, repaired];
        Assert.True(RoadRaster.OwnFaithful(new(0, 0), chunk, fills, tris, differing));
        Assert.True(RoadRaster.OwnFaithful(new(160, 0), chunk, fills, tris, differing));
        Assert.True(RoadRaster.OwnFaithful(new(0, 320), chunk, fills, tris, differing));      // the fill's
        Assert.False(RoadRaster.OwnFaithful(new(320, 0), chunk, fills, tris, differing));     // the plain triangle's
        Assert.False(RoadRaster.OwnFaithful(new(0, 160), chunk, fills, tris, differing));
        // a repaired triangle painted out (texture -1, a destroyed structure's ground face) asks for nothing
        Assert.False(RoadRaster.OwnFaithful(new(0, 0), chunk, [], [repaired with { Texture = -1 }], differing));
        // one set (the map editor's): the triangle draws the same in both looks
        Assert.False(RoadRaster.OwnFaithful(new(0, 0), chunk, [], tris, new HashSet<int>()));
    }
}
