using Godot;
using I76.Game.Sim;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The smoke layer's depth read: each layer pixel takes the nearest scene surface over every scene texel it covers, so a
/// car's or a ridge's outline hides the layer pixels on it. The footprints leave no texel out at any size (the layer is
/// half the scene's size, rounded down), the three taps a pixel reads cover a footprint of up to three texels, and the
/// projection says which way the depth runs.
/// </summary>
public class SmokeDepthTests
{
    [Theory]
    [InlineData(1600, 2)]
    [InlineData(1577, 2)]   // an odd internal size: the layer is 788, the last texel column still covered
    [InlineData(901, 2)]
    public void TheFootprintsCoverEverySceneTexel(int scene, int scale)
    {
        int layer = scene / scale;
        var covered = new bool[scene];
        int next = 0;
        for (int px = 0; px < layer; px++)
        {
            var (lo, hi) = SmokeDepth.Footprint(px, layer, scene);
            Assert.True(lo <= next, $"pixel {px} starts at {lo}, texel {next} left out");
            Assert.True(hi - lo + 1 <= 3, $"pixel {px} covers {hi - lo + 1} texels");
            foreach (int t in SmokeDepth.Taps(lo, hi)) covered[t] = true;
            next = hi + 1;
        }
        Assert.Equal(scene, next);
        Assert.All(covered, Assert.True);
    }

    [Fact]
    public void TheTapsSpanTheFootprint()
    {
        Assert.Equal([5, 5, 5], SmokeDepth.Taps(5, 5));
        Assert.Equal([5, 6, 6], SmokeDepth.Taps(5, 6));
        Assert.Equal([5, 6, 7], SmokeDepth.Taps(5, 7));
    }

    [Fact]
    public void ForwardPlusDepthRunsNearerLarger()
    {
        var persp = Projection.CreatePerspective(70, 16f / 9f, 0.25f, 20000f, false);
        Assert.False(SmokeDepth.NearerIsLarger(persp));   // OpenGL's: -1 at the near plane, 1 at the far
        // the renderer's correction (reversed z, remapped to 0..1, y flipped), as the scene's view projection carries it
        var reversed = new Projection(new Vector4(1, 0, 0, 0), new Vector4(0, -1, 0, 0), new Vector4(0, 0, -0.5f, 0), new Vector4(0, 0, 0.5f, 1));
        Assert.True(SmokeDepth.NearerIsLarger(reversed * persp));
    }

    [Theory]
    [InlineData(RenderingServer.ViewportMsaa.Disabled, 1)]
    [InlineData(RenderingServer.ViewportMsaa.Msaa2X, 2)]
    [InlineData(RenderingServer.ViewportMsaa.Msaa4X, 4)]
    [InlineData(RenderingServer.ViewportMsaa.Msaa8X, 8)]
    public void WithMsaaTheLayerReadsEverySampleOfTheDepth(RenderingServer.ViewportMsaa msaa, int samples)
    {
        Assert.Equal(samples, SmokeDepth.Samples(msaa));
    }

    [Theory]
    [InlineData(RenderingServer.ViewportMsaa.Msaa8X, RenderingDevice.TextureSamples.Samples8, 8)]
    [InlineData(RenderingServer.ViewportMsaa.Msaa8X, RenderingDevice.TextureSamples.Samples4, 4)]   // a GPU without 8x for the depth's format: the backend made 4
    [InlineData(RenderingServer.ViewportMsaa.Msaa4X, RenderingDevice.TextureSamples.Samples2, 2)]
    [InlineData(RenderingServer.ViewportMsaa.Msaa4X, RenderingDevice.TextureSamples.Samples8, 4)]
    public void TheLayerReadsNoSamplePastTheOnesTheDepthWasMadeWith(RenderingServer.ViewportMsaa msaa, RenderingDevice.TextureSamples made, int samples)
    {
        Assert.Equal(samples, SmokeDepth.Samples(msaa, (int)made));
    }
}
