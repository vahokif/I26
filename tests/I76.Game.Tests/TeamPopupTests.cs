using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Game.Shell;
using I76.Sim.Missions;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// CHOOSE A TEAM: team play opens the eight-team popup; a CTF game opens the Nitro Pack's popup of the arena's 2, 3 or 4
/// teams by their CTF names, host and joiner alike, so no row picks a team the arena has not.
/// </summary>
public class TeamPopupTests
{
    [Fact]
    public void TeamPlayOpensTheEightTeams()
    {
        Assert.Equal(0, TeamPopup.ChoiceFor(MeleeKind.Combat, teamPlay: true, ctfTeams: 2, coop: false));
        Assert.Equal(0, TeamPopup.ChoiceFor(null, teamPlay: true, ctfTeams: 2, coop: false));
        Assert.Equal(0, TeamPopup.ChoiceFor(MeleeKind.Race, teamPlay: true, ctfTeams: 2, coop: false));
        var l = TeamPopup.LayoutOf(0);
        Assert.Null(l.NativeSprite);
        Assert.Equal((191, 83, 259, 314), (l.X, l.Y, l.W, l.H));
        Assert.Equal(["Grey Hounds", "Puce Panthers", "Crimson Kings", "Yellow Jackets", "Aqua Marines", "Mauve Mayhem", "Mr. Brown's Clowns", "Men In Black"], l.Names);
    }

    [Fact]
    public void NoTeamPlayAndCoopOpenNone()
    {
        Assert.Null(TeamPopup.ChoiceFor(MeleeKind.Combat, teamPlay: false, ctfTeams: 2, coop: false));
        Assert.Null(TeamPopup.ChoiceFor(null, teamPlay: false, ctfTeams: 4, coop: false));
        Assert.Null(TeamPopup.ChoiceFor(MeleeKind.Combat, teamPlay: true, ctfTeams: 2, coop: true));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ACtfGameOpensItsArenasTeams(int teams)
    {
        // the original opens it for every CTF game, before it looks at team play
        Assert.Equal(teams, TeamPopup.ChoiceFor(MeleeKind.Ctf, teamPlay: true, ctfTeams: teams, coop: false));
        Assert.Equal(teams, TeamPopup.ChoiceFor(MeleeKind.Ctf, teamPlay: false, ctfTeams: teams, coop: false));
        var l = TeamPopup.LayoutOf(teams);
        Assert.Equal(48 - teams, l.NativeSprite);            // Nitro bank 30: 46 two teams, 45 three, 44 four
        Assert.Equal((205, 136, 231, 208), (l.X, l.Y, l.W, l.H));
        Assert.Equal(new[] { "Red Dawn", "Gang Green", "Purple Reign", "Black Plague" }.Take(teams), l.Names);
        // row n picks team n + 1, named as the game names it in play
        for (int i = 0; i < l.Names.Count; i++) Assert.Equal(MeleeGame.TeamName(MeleeKind.Ctf, i + 1), l.Names[i]);
        // the rows sit inside the popup above DONE and CANCEL
        var rows = TeamPopup.Row.Take(l.Names.Count).ToList();
        Assert.All(rows, r => Assert.True(r.X1 < l.W && r.Y1 < l.Done.Y0 && r.Y1 < l.Cancel.Y0));
        Assert.True(l.Done.X1 < l.Cancel.X0 && l.Cancel.X1 < l.W && l.Done.Y1 < l.H);
    }

    [Fact]
    public void ACtfTeamCountOutsideTwoToFourIsClamped()
    {
        Assert.Equal(2, TeamPopup.ChoiceFor(MeleeKind.Ctf, true, 0, false));
        Assert.Equal(4, TeamPopup.ChoiceFor(MeleeKind.Ctf, true, 5, false));
        Assert.Equal(4, TeamPopup.LayoutOf(9).Names.Count);
    }

    [Fact]
    public void TheNitroArtIsThere()
    {
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var bank = ShellDatabase.Open(new GameInstall(dir).FindFile("database.tvf")!).BankNative(30);
        foreach (int teams in new[] { 2, 3, 4 })
        {
            var l = TeamPopup.LayoutOf(teams);
            var sp = bank[l.NativeSprite!.Value]!;
            Assert.Equal((l.W, l.H), (sp.Width, sp.Height));
        }
        Assert.Equal((259, 314), (bank[18]!.Width, bank[18]!.Height));   // the eight-team popup, base bank 36 sprite 19
    }
}
