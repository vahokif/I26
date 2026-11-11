using I76.Formats.Mission;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class MissionTitlesTests
{
    private static void EveryStockMissionHasATitle(GameInstall install)
    {
        // the maps the game ships, not what a player added beside them
        var all = MissionTitles.Stock(install).Where(m => install.FindMission(m.Stem) is { } f && MapFileSet.IsStockMission(f)).ToList();
        Assert.NotEmpty(all);
        foreach (var m in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Title), $"{m.Stem} has no title");
            Assert.EndsWith($"({m.Stem.ToUpperInvariant()})", m.Label);
        }
        Assert.Equal(all.Count, all.Select(m => m.Stem).Distinct().Count());
    }

    [InstallFact]
    public void EveryBaseMissionHasATitleAndTheGroupsComeInTheGamesOrder()
    {
        EveryStockMissionHasATitle(TestInstall.Install);
        var all = MissionTitles.Stock(TestInstall.Install);
        Assert.Equal("Jorczak's Peak (M14)", all.First(m => m.Stem == "m14").Label);
        Assert.Equal("The Crater (M01)", all.First(m => m.Group == MissionTitles.Melee).Label);
        Assert.Equal(MissionTitles.Campaign, all[0].Group);
        Assert.True(all.FindIndex(m => m.Stem == "t02") < all.FindIndex(m => m.Stem == "t10"));
    }

    [NitroFact]
    public void EveryNitroMissionHasATitle()
    {
        var nitro = new GameInstall(NitroFactAttribute.Dir!);
        EveryStockMissionHasATitle(nitro);
        var all = MissionTitles.Stock(nitro);
        Assert.Equal("A New Hope (P01)", all.First(m => m.Stem == "p01").Label);
        Assert.Equal("Evening Stroll (N01)", all.First(m => m.Stem == "n01").Label);
    }

    [InstallFact]
    public void TheBaseCoopListIsTheStoryMissionsInTheGamesOrder()
    {
        var list = MissionTitles.Coop(TestInstall.Install).Select(m => m.Stem).ToList();
        // the campaign (A01, T01-T17), then the scenarios in the shell's own order; no melee arena
        string[] t = [.. Enumerable.Range(1, 17).Select(i => $"t{i:00}")];
        Assert.Equal(["a01", .. t, .. MissionTitles.BaseScenarios.Select(s => s.Msn)], list);
        Assert.Equal("3. The Two Samurai", MissionTitles.Coop(TestInstall.Install).First(m => m.Stem == "t03").Title);
        Assert.Equal("17. The Truth Is Out There", MissionTitles.Coop(TestInstall.Install).First(m => m.Stem == "t17").Title);
        Assert.Equal("Oil Well Well", MissionTitles.CoopTitle(TestInstall.Install, "S03"));
        Assert.Equal("M01", MissionTitles.CoopTitle(TestInstall.Install, "m01"));
    }

    [InstallFact]
    public void TheCoopListIsReadOncePerMissionFolder()
    {
        // the internet roster, a game's popup and the co-op lobby name a co-op game by its mission's title every time they draw (several
        // times a second while answers come in): reading every stock mission's script for it each time stalled the shell (the lag a co-op
        // game listed online gave everyone on the roster)
        using var scratch = new ScratchInstall(TestInstall.Dir!, null);
        var install = scratch.Install;
        var first = MissionTitles.CoopList(install);
        Assert.Contains(first, m => m.Stem == "t05");
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("1. Meet Taurus", MissionTitles.CoopTitle(install, "t01"));
            Assert.Same(first, MissionTitles.CoopList(install));
        }
        // the copy handed out is the caller's own
        var copy = MissionTitles.Coop(install);
        copy.Clear();
        Assert.Equal(first, MissionTitles.Coop(install));
        // a change to the folder is read again
        File.Delete(Path.Combine(install.MissionDir!, Directory.EnumerateFiles(install.MissionDir!).Select(Path.GetFileName).First(f => f!.Equals("t05.msn", StringComparison.OrdinalIgnoreCase))!));
        var again = MissionTitles.CoopList(install);
        Assert.NotSame(first, again);
        Assert.DoesNotContain(again, m => m.Stem == "t05");
        Assert.Equal("T05", MissionTitles.CoopTitle(install, "t05"));
    }

    [NitroFact]
    public void TheNitroCoopListIsTheCampaignInTheGamesOrder()
    {
        var nitro = new GameInstall(NitroFactAttribute.Dir!);
        var list = MissionTitles.Coop(nitro);
        var stems = list.Select(m => m.Stem).ToList();
        Assert.Equal(20, list.Count);
        Assert.All(stems, s => Assert.Contains(s[0], "bp"));
        Assert.Equal(Enumerable.Range(1, 19).Select(i => $"p{i:00}").Append("b01").Order().ToList(), stems.Order().ToList());
        Assert.Equal("A New Hope", list.First(m => m.Stem == "p01").Title);
    }
}
