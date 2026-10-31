using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class MissionPaletteTests
{
    /// <summary>
    /// #76: Creeper Canyon's WRLD names "t013_16.ACT", which the game does not have; the cockpit's 8-bit art had no palette and the
    /// HUD drew white. The world falls back to t03_16.ACT (the .lum's stem), a palette that is there. Nitro's N58 has the same typo.
    /// </summary>
    [Theory]
    [InlineData("I76_INSTALL", "miss16", "m08.msn")]
    [InlineData("I76_NITRO_INSTALL", null, "n58.cbt")]
    public void CreeperCanyonsPaletteIsFound(string env, string? dir, string file)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } root || !Directory.Exists(root)) return;
        var inst = new GameInstall(root);
        var vfs = new AssetVfs(inst);
        var path = dir is null ? inst.ListMissions(".cbt").First(p => Path.GetFileName(p).Equals(file, StringComparison.OrdinalIgnoreCase)) : inst.Resolve(dir, file)!;
        var m = new MissionFile(path);
        Assert.False(vfs.Exists(m.Palette()!));   // the typo is in the data
        var name = MissionWorld.ResolvePalette(vfs, m);
        Assert.True(vfs.Exists(name!), name);
        Assert.Equal("t03_16.act", name, ignoreCase: true);
    }

    [Fact]
    public void AMissionWithItsPaletteKeepsIt()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } root || !Directory.Exists(root)) return;
        var inst = new GameInstall(root);
        var m = new MissionFile(inst.Resolve("miss16", "m01.msn")!);
        Assert.Equal(m.Palette(), MissionWorld.ResolvePalette(new AssetVfs(inst), m));
    }
}
