using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The default sides of a team game with bots (<see cref="TeamSides"/>): the people together, the bots against them.</summary>
public class TeamSidesTests
{
    private static (int, bool) P(int team) => (team, false);
    private static (int, bool) B(int team) => (team, true);

    [Fact]
    public void BotsGoToATeamNoPersonIsOnAndStayTogether()
    {
        // two people on team 1: every bot on team 2, however many (the old rule put every third on team 1)
        var members = new List<(int, bool)> { P(1), P(1) };
        for (int i = 0; i < 6; i++) members.Add(B(TeamSides.ForBot(members, 8)));
        Assert.All(members.Where(m => m.Item2), m => Assert.Equal(2, m.Item1));
        // people on team 3: the bots take team 1, the lowest free
        Assert.Equal(1, TeamSides.ForBot([P(3), P(3)], 8));
        // bots already on team 4 keep the new one with them
        Assert.Equal(4, TeamSides.ForBot([P(1), B(4), B(4)], 8));
        // a person who picked the bots' team: the bots stay one side, with them
        Assert.Equal(2, TeamSides.ForBot([P(1), P(2), B(2), B(2)], 8));
    }

    [Fact]
    public void TheListenHostsPickKeepsTheBotsOffItsTeamBeforeItSits()
    {
        Assert.Equal(1, TeamSides.ForBot([], 8, reserved: 2));
        Assert.Equal(2, TeamSides.ForBot([], 8, reserved: 1));
        Assert.Equal(2, TeamSides.ForBot([B(2)], 8, reserved: 1));
    }

    [Fact]
    public void ABotsOnlyGameSharesTheBotsOutOverTwoTeamsOrEveryCtfTeam()
    {
        var members = new List<(int, bool)>();
        for (int i = 0; i < 8; i++) members.Add(B(TeamSides.ForBot(members, 8)));
        Assert.Equal([4, 4], members.GroupBy(m => m.Item1).OrderBy(g => g.Key).Select(g => g.Count()));
        members.Clear();
        for (int i = 0; i < 6; i++) members.Add(B(TeamSides.ForBot(members, 3, spread: true)));
        Assert.Equal([2, 2, 2], members.GroupBy(m => m.Item1).OrderBy(g => g.Key).Select(g => g.Count()));
    }

    [Fact]
    public void InCtfTheBotsShareOutOverTheTeamsThePeopleLeftFree()
    {
        var members = new List<(int, bool)> { P(1) };
        for (int i = 0; i < 4; i++) members.Add(B(TeamSides.ForBot(members, 3, spread: true)));
        Assert.Equal([1, 2, 2], members.GroupBy(m => m.Item1).OrderBy(g => g.Key).Select(g => g.Count()));
        // people on both teams of a two-team arena: no free team, so the smaller side, as before
        Assert.Equal(2, TeamSides.ForBot([P(1), P(1), P(2)], 2, spread: true));
    }

    [Fact]
    public void APersonWhoAsksForNoneJoinsThePeople()
    {
        Assert.Equal(3, TeamSides.ForPerson([P(3), B(1), B(1), B(1)], 8));
        Assert.Equal(2, TeamSides.ForPerson([P(2), P(5), P(5), P(2), B(1)], 8));     // a tie: the lower
        Assert.Equal(6, TeamSides.ForPerson([B(1)], 8, reserved: 6));
        Assert.Equal(3, TeamSides.ForPerson([B(1), B(2)], 8));                         // nobody seated: a team without bots
        Assert.Equal(1, TeamSides.ForPerson([], 8));
        Assert.Equal(1, TeamSides.ForPerson([B(1), B(2)], 2));                         // every team has bots: team 1
    }
}
