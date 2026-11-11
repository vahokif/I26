using I76.Formats.Assembly;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The road layer with a texture pack: its upscales of the roads, the road pieces' tops and the ground decals bake the modern look's
/// layer; without any, nothing changes (one set of layers, upstream's). Tiny synthetic textures only.
/// </summary>
public class HdRoadLayerTests
{
    private static RoadRaster.Tex Flat(int w, int h, byte v) { var b = new byte[w * h * 4]; Array.Fill(b, v); return new RoadRaster.Tex(w, h, b); }

    [Fact]
    public void WithoutAnUpscaleThereIsNoSecondSet()
    {
        var roads = new Dictionary<int, RoadRaster.Tex?> { [0] = Flat(4, 4, 10), [1] = Flat(1, 1, 20) };
        // no pack: every member is served as the original, at scale 1
        Assert.Null(MissionBuilder.HdRoadTextures(roads, [(0, "road.map"), (1, "#102030")], m => (roads[0]!, 1)));
        // a member the install lacks, and a flat colour, are never looked up
        Assert.Null(MissionBuilder.HdRoadTextures(roads, [(0, null), (1, "#102030")], m => throw new InvalidOperationException(m)));
    }

    [Fact]
    public void AnUpscaleTakesItsOwnIndexAndTheRestStayTheOriginals()
    {
        var road = Flat(4, 4, 10); var top = Flat(2, 2, 30); var up = Flat(16, 16, 11);
        var roads = new Dictionary<int, RoadRaster.Tex?> { [0] = road, [1] = top, [2] = Flat(1, 1, 20) };
        var hd = MissionBuilder.HdRoadTextures(roads, [(0, "road.map"), (1, "top.map"), (2, "#102030")],
            m => m == "road.map" ? (up, 4) : (top, 1));
        Assert.NotNull(hd);
        Assert.Same(up, hd![0]);
        Assert.Same(top, hd[1]);
        Assert.Same(roads[2], hd[2]);
        // the originals' own set is left as it was, for the 1997 look
        Assert.Same(road, roads[0]);
    }

    [Fact]
    public void AnUpscaledRoadBakesTheSameRoadAtTwiceTheResolution()
    {
        // a 4-texel road and its 4x upscale, flat colour: a quad baked at 2048 from the upscale covers the same ground as at 1024
        var quad = new RoadRaster.FlatQuad(new(10, 0), new(0, 0), new(0, 40), new(10, 40), 0, new(0, 0), new(10, 40));
        var lo = RoadRaster.Bake([quad], new(0, 0), 80, 64, _ => Flat(4, 4, 200))!;
        var hi = RoadRaster.Bake([quad], new(0, 0), 80, 128, _ => Flat(16, 16, 200))!;
        int Covered(byte[] l) { int n = 0; for (int i = 3; i < l.Length; i += 4) if (l[i] > 127) n++; return n; }
        Assert.InRange(Covered(hi), Covered(lo) * 4 - 2 * 128, Covered(lo) * 4 + 2 * 128);   // within a texel row of the edges
    }
}
