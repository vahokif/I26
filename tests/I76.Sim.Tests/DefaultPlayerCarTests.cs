using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A mission started without a car from the shell (the command line, the mission list): the original drives the vppirna1
/// it seeds at start-up and never loads the mission's own car for the player. Every stock I'76 mission places vppirna1
/// for the player; the Nitro scenarios place others (P01 vdrampg2, P03 vjsovrn1, P10 vsvan1).
/// </summary>
public class DefaultPlayerCarTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Theory]
    [InlineData("p01.msn", "vdrampg2.vcf")]
    [InlineData("p03.msn", "vjsovrn1.vcf")]
    [InlineData("p10.msn", "vsvan1.vcf")]
    public void WithoutAShellCarThePlayerDrivesThePiranhaNotTheMissionsCar(string msn, string placed)
    {
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission(msn)!);
        Assert.Contains(w.Placements, p => p.Object.IsPlayer && placed.Equals(p.Definition, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("vppirna1.vcf", MissionSetup.Build(vfs, w, asm).World.Player!.Definition, ignoreCase: true);
        // a car from the shell still takes the player's place
        var given = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission(msn)!), asm, "vppirna2.vcf");
        Assert.Equal("vppirna2.vcf", given.World.Player!.Definition, ignoreCase: true);
    }

    [Fact]
    public void ACoopGamesMissionsOwnCarIsThePlacements()
    {
        // the co-op lobby's "Mission's own car" is seat 0 without a car: Nitro P01 keeps its vdrampg2, not the Piranha
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("p01.msn")!);
        var s = MissionSetup.Build(vfs, w, asm, coop: new CoopSetup([null, null]));
        Assert.Equal("vdrampg2.vcf", s.World.Player!.Definition, ignoreCase: true);
        Assert.All(s.World.Humans, h => Assert.Equal("vdrampg2.vcf", h.Definition, ignoreCase: true));
    }
}
