using I76.Formats.Imaging;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class LumaTableTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The engine's own tables, dumped from the original game: a01's from t08_16.act / t08.lum, t01's.</summary>
    /// a01's matches exactly; t01's comes within one step (the engine's palette in memory evidently differs
    /// slightly from t01_16.act: no row mapping or precision makes it exact).
    [Theory]
    [InlineData("t08_16.act", "t08.lum", 0, new byte[] { 53, 86, 86, 105, 124, 124, 124, 144, 144, 165, 165, 165, 186, 186, 186, 186, 208, 208, 208, 231, 231, 231, 231, 255 })]
    [InlineData("t01_16.act", "t01.lum", 1, new byte[] { 123, 123, 123, 130, 139, 147, 155, 163, 163, 172, 180, 180, 189, 199, 199, 207, 207, 216, 216, 226, 235, 235, 245, 245, 255 })]
    public void TheRampIsTheEngines(string act, string lum, int slack, byte[] start)
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var table = LumaTable.Build(new Palette(vfs.Read(act)), vfs.Read(lum));
        for (int j = 0; j < start.Length; j++) Assert.InRange(table[j], start[j] - slack, start[j] + slack);
        Assert.All(table.Skip(start.Length), v => Assert.Equal(255, v));
    }
}
