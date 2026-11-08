using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Explosion templates are keyed by their XGEO name and the first file to register a key keeps it, as the original's:
/// the oil pump's xoilw1.xdf (100 / 25 m) carries the gas station's key X1_GASX1 (xgasx1.xdf, 100 / 85 m).
/// </summary>
public class EffectTemplateTests
{
    private static AssetVfs? Vfs() =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? new AssetVfs(new GameInstall(d)) : null;

    [Fact]
    public void TheFirstFileToRegisterAKeyDefinesItForBoth()
    {
        if (Vfs() is not { } vfs) return;
        var oilFirst = new EffectTemplates(vfs.TryRead);
        oilFirst.Register(["xoilw1.xdf", "xgasx1.xdf"]);
        Assert.Equal(25f, oilFirst["xgasx1.xdf"]!.SplashRadius);
        Assert.Same(oilFirst["xoilw1.xdf"], oilFirst["xgasx1.xdf"]);

        var gasFirst = new EffectTemplates(vfs.TryRead);
        Assert.Equal(85f, gasFirst["xgasx1.xdf"]!.SplashRadius);
        Assert.Equal(85f, gasFirst["xoilw1.xdf"]!.SplashRadius);
        Assert.Equal("xgasx1.xdf", gasFirst["xoilw1.xdf"]!.File);
    }

    [Fact]
    public void FilesWithTheirOwnKeysOrNoneStayTheirOwn()
    {
        if (Vfs() is not { } vfs) return;
        var t = new EffectTemplates(vfs.TryRead);
        t.Register(EffectTemplates.Preloaded);
        Assert.Equal(65f, t["xbldx1.xdf"]!.SplashRadius);
        Assert.Equal(10f, t["xcars1.xdf"]!.SplashRadius);
        Assert.Equal("xcont1.xdf", t["xcont1.xdf"]!.File);
        Assert.Null(t["nosuch.xdf"]);
    }

    [Theory]
    [InlineData("t05.msn", 85f)]    // a gas station (bfsince1) is placed before the oil pump: the pump blows 85 m wide
    [InlineData("s07.msn", 25f)]    // the oil pumps come first: the gas stations' blasts shrink to 25 m
    public void AMissionRegistersItsObjectsTemplatesInPlacementOrder(string msn, float radius)
    {
        var d = Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } e && Directory.Exists(e) ? e : null;
        if (d is null) return;
        var install = new GameInstall(d);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", msn)!), asm);
        Assert.Equal(radius, setup.Combat.Effects("xoilw1.xdf")!.SplashRadius);
        Assert.Equal(radius, setup.Combat.Effects("xgasx1.xdf")!.SplashRadius);
    }
}
