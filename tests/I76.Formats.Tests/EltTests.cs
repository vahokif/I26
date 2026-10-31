using I76.Formats.Cockpit;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class EltTests
{
    [Fact]
    public void ParsesSourcesDestinationsAndLabels()
    {
        var e = EltLayout.Parse("dst zrad.map\n    label range_pos 139 4 0 0\nsrc zgeare.map\n    label arrow 0 0 12 6\r\n");
        Assert.Equal(2, e.Blocks.Count);
        Assert.False(e.Blocks[0].IsSource);
        Assert.True(e.Blocks[1].IsSource);
        Assert.Equal(("zgeare.map", new EltLabel("arrow", 0, 0, 12, 6)), e.Find("ARROW"));
        Assert.Null(e.Find("nope"));
    }

    [Fact]
    public void ReadsThePiranhasCockpit()
    {
        var dir = System.Environment.GetEnvironmentVariable("I76_INSTALL");
        if (dir is null || !Directory.Exists(dir)) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var e = EltLayout.Parse(System.Text.Encoding.Latin1.GetString(vfs.TryRead("vpit_1.elt")!));
        Assert.Equal(("zgear101.map", new EltLabel("park", 55, 21, 12, 6)), e.Find("park"));
        Assert.Equal(38, e.Blocks.Single(b => b.Texture == "zneedle6.map").Labels.Count);
    }

    [Fact]
    public void ReadsTheVlocKinds()
    {
        var dir = System.Environment.GetEnvironmentVariable("I76_INSTALL");
        if (dir is null || !Directory.Exists(dir)) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var vdf = new I76.Formats.Bwd2.Bwd2File(vfs.TryRead("vppirnha.vdf")!);
        var vloc = vdf.Locations().Where(l => l.Tag == "VLOC").ToList();
        // 40 the eye, 35 the speedo needle, 36 the tacho needle
        Assert.Equal([40, 42, 35, 36, 38], vloc.Select(l => l.Index));
        Assert.Equal(-0.487f, vloc[2].Transform.Translation[0], 3);
    }
}
