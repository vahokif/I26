using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The Ball O'Flame's fireball (issue 77): a camera-facing card playing XFS1_101..106 in a loop.</summary>
public class FireballCardTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void TheFireballLoopsSixFramesAtSixAFrameASecond()
    {
        Assert.Equal("XFS1_101", FireballCard.Frame(0));
        Assert.Equal("XFS1_101", FireballCard.Frame(0.16f));
        Assert.Equal("XFS1_102", FireballCard.Frame(0.17f));
        Assert.Equal("XFS1_106", FireballCard.Frame(0.99f));
        Assert.Equal("XFS1_101", FireballCard.Frame(1.0f));
        Assert.Equal("XFS1_103", FireballCard.Frame(2.4f));
    }

    /// <summary>The ordnance model is a single textured quad whose table the frames come from; all six frames are in the install.</summary>
    [Fact]
    public void TheBallOFlamesRoundIsAQuadOfTheFireTable()
    {
        if (NitroDir is null) return;
        var vfs = new AssetVfs(new GameInstall(NitroDir));
        var g = GizmoDef.Read("gsball", vfs.Read("gsball.gdf"))!;
        Assert.Equal(21u, g.Kind);
        var node = Assert.Single(g.OrdnanceGeometry);
        Assert.Equal("MBALFIRE", node.Name, ignoreCase: true);
        var geo = new ModelAssembler(vfs).Geo("MBALFIRE")!;
        Assert.Equal(4, geo.Vertices.Length);
        Assert.Equal("XFS1_101.TMT", Assert.Single(geo.Faces).Texture, ignoreCase: true);
        for (int i = 0; i < FireballCard.Frames; i++)
            Assert.True(vfs.Exists(FireballCard.Frame(i / FireballCard.FramesPerSecond + 0.01f) + ".vqm"), $"frame {i}");
    }
}
