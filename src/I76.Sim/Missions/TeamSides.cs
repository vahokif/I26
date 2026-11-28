namespace I76.Sim.Missions;

/// <summary>
/// The sides a team game with bots takes by default (docs/multiplayer.md "Bots", "Teams"): the people together, the bots against
/// them on a team no person is on. A person's own pick (CHOOSE A TEAM) always wins; these are the defaults for a bot, which has
/// no pick, and for a person who asks for none. Teams are 1..<c>teams</c>; a member is its team and whether it is a bot.
/// </summary>
public static class TeamSides
{
    /// <summary>
    /// A person's team when they ask for none, and the row CHOOSE A TEAM starts on: the team most of the people are on (the
    /// lowest of a tie), else <paramref name="reserved"/> (the listen host's own pick, before it is seated), else the lowest team
    /// no bot is on, else team 1.
    /// </summary>
    public static int ForPerson(IEnumerable<(int Team, bool Bot)> members, int teams, int reserved = 0)
    {
        var at = members.Where(m => m.Team >= 1 && m.Team <= teams).ToList();
        var people = at.Where(m => !m.Bot).GroupBy(m => m.Team).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        if (people is not null) return people.Key;
        if (reserved >= 1 && reserved <= teams) return reserved;
        for (int t = 1; t <= teams; t++)
            if (!at.Any(m => m.Team == t)) return t;
        return 1;
    }

    /// <summary>
    /// A bot's team. The people's teams are the seated people's and <paramref name="reserved"/>'s; the bot goes to a team none
    /// of them is on: in combat and race the one the bots already hold (the lowest such), else the lowest free one, so the bots
    /// are one side (a person who picked the bots' team has them stay there, with that person); in capture the flag (<paramref name="spread"/>) the free team with the fewest players, so every base has
    /// defenders. With no people known (a bots-only game) the bots share out over <paramref name="spread"/> ? all teams : teams 1
    /// and 2, the fewest first; with no team free of people, over all teams the same way.
    /// </summary>
    public static int ForBot(IEnumerable<(int Team, bool Bot)> members, int teams, int reserved = 0, bool spread = false)
    {
        var at = members.Where(m => m.Team >= 1 && m.Team <= teams).ToList();
        var people = at.Where(m => !m.Bot).Select(m => m.Team).ToHashSet();
        if (reserved >= 1 && reserved <= teams) people.Add(reserved);
        int Fewest(IEnumerable<int> of) => of.OrderBy(t => at.Count(m => m.Team == t)).ThenBy(t => t).First();
        if (people.Count == 0) return Fewest(Enumerable.Range(1, spread || teams < 2 ? teams : 2));
        var free = Enumerable.Range(1, teams).Where(t => !people.Contains(t)).ToList();
        if (free.Count == 0) return Fewest(Enumerable.Range(1, teams));
        if (spread) return Fewest(free);
        if (free.FirstOrDefault(t => at.Any(m => m.Bot && m.Team == t)) is var held && held > 0) return held;
        // a person picked the bots' team: the bots stay one side, with them
        var most = at.Where(m => m.Bot).GroupBy(m => m.Team).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        return most?.Key ?? free[0];
    }
}
