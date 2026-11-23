using I76.Formats.Assembly;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The road layer with a texture pack: its upscales of the roads, the road pieces' tops and the ground decals bake the modern look's
/// layer; without any, nothing changes (one set of layers, as before). Tiny synthetic textures only.
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
    public void ARepairedStockTextureKeepsItsRepairedOriginalOverAPacksUpscale()
    {
        // the factory forecourt's 16-bit copy has a green and blue slip (TextureRepairs) that a pack's upscale of the file carries
        Assert.False(I76.Game.Assets.TextureFactory.DrawsUpscale("CA_1LT_6.M16", _ => true));
        Assert.False(I76.Game.Assets.TextureFactory.DrawsUpscale("addon\\ca_1lt_6.m16", _ => true));
        Assert.True(I76.Game.Assets.TextureFactory.DrawsUpscale("CA_1LT_1.M16", _ => true));
        Assert.False(I76.Game.Assets.TextureFactory.DrawsUpscale("CA_1LT_1.M16", _ => false));
    }

    [Fact]
    public void TheOutsideGroundsChunksTakeTheSameLayersAsTheMapsOwn()
    {
        // two chunks of the map's own and one past its tiles (an empty ZMAP cell) that a road reaches; the 1997 look's own layer where a
        // gap fill reaches (the outside chunk), its Software renderer's where a repaired texture does (the map's second)
        List<(int X0, int Z0)> map = [(0, 0), (32, 0)], outside = [(-32, 0)], all = [(0, 0), (32, 0), (-32, 0)];
        bool Own97(int x0, int z0) => x0 != 0;
        bool Software(int x0, int z0) => x0 == 32;
        // no pack: as before, the outside chunk baked with the map's by the same rules
        var plain = MissionBuilder.PlanRoadLayers(map, outside, pack: false, Own97, Software);
        Assert.Equal(all, plain.Modern);
        Assert.Equal(MissionBuilder.RoadLayerResolution, plain.Res);
        Assert.Equal([(32, 0), (-32, 0)], plain.Faithful);
        Assert.Equal([(32, 0)], plain.Software);
        Assert.Empty(plain.Deferred);
        // a pack's roads: the modern look's layer of every chunk, the outside one too, from its upscales, denser; the 1997 look's of every
        // chunk, the outside one too, from the originals the first time the look is asked for, and none made with the mission
        var pack = MissionBuilder.PlanRoadLayers(map, outside, pack: true, Own97, Software);
        Assert.Equal(all, pack.Modern);
        Assert.Equal(MissionBuilder.RoadLayerResolutionHd, pack.Res);
        Assert.Empty(pack.Faithful);
        Assert.Empty(pack.Software);
        Assert.Equal(all, pack.Deferred);
        // a map that streams its chunks bakes none with the mission (each as it streams in) and has no outside layers
        var huge = MissionBuilder.PlanRoadLayers([], [], pack: true, Own97, Software);
        Assert.Empty(huge.Modern);
        Assert.Empty(huge.Deferred);
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
