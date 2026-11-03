using I76.Sim.Missions;

namespace I76.Net;

/// <summary>
/// The network game's console lines in the original's words (through lang.txt; the Nitro Pack where it differs):
/// joins and leaves, kills, chat,
/// and the end's line on each machine.
/// </summary>
public static class NetLines
{
    public static string Join(bool nitro, bool teams, string name, string team) =>
        !nitro ? $"*** {name} has joined the game" : teams ? $"*** A {team} team member has joined the game!" : $"*** Player {name} has joined the game";

    public static string Leave(bool nitro, string name) => nitro ? $"*** Player {name} has left the game" : $"*** {name} has left the game";

    /// <summary>"*** %s has been killed" with no killer, else "*** %1:s was killed by %2:s" (one's own weapon too: the killer is oneself).</summary>
    public static string Kill(string victim, string? killer) => killer is null ? $"*** {victim} has been killed" : $"*** {victim} was killed by {killer}";

    public static string Chat(string from, string text) => $"From {from}: {text}";

    /// <summary>The Nitro session popup's GAME TYPE: Combat or Team Combat, Race, n Team C.T.F.</summary>
    public static string SessionType(SessionInfo g) => g.Coop ? (g.Lobby ? "Co-op lobby" : "Co-op") : g.Kind switch
    {
        MeleeKind.Race => "Race",
        MeleeKind.Ctf => g.Teams is >= 2 and <= 4 ? $"{g.Teams} Team C.T.F." : "Unknown",
        MeleeKind.Combat => g.TeamPlay ? "Team Combat" : "Combat",
        _ => "Unknown",
    };

    /// <summary>
    /// A listed game's AREA OF PLAY: the host's name for a custom map (the asker's copy, if any, may be another), else the asker's
    /// own name for it (<paramref name="local"/>: the base game's table or the file's WRLD name; empty when it lacks the arena), else
    /// the host's, else the file's stem.
    /// </summary>
    public static string AreaName(SessionInfo g, string? local)
    {
        string host = g.ArenaName.Trim();
        if (g.CustomMap && host.Length > 0) return host;
        if (local?.Trim() is { Length: > 0 } l) return l;
        return host.Length > 0 ? host : Path.GetFileNameWithoutExtension(g.Arena);
    }

    /// <summary>
    /// A listed game's name on the roster and its popup: what the host typed in GAME NAME, or the arena's name (<see cref="AreaName"/>)
    /// for a game listed with none or with a mission file's name (the area's name is what the host form names a game by).
    /// </summary>
    public static string GameName(SessionInfo g, string? local)
    {
        string n = g.Name.Trim();
        return n.Length == 0 || IsMissionFile(n, g.Arena) ? AreaName(g, local) : n;
    }

    /// <summary>A name that is the arena's file name or its stem, or any mission file's name (a stem with .cbt, .rac, .lvl ...).</summary>
    private static bool IsMissionFile(string name, string arena) =>
        name.Equals(arena, StringComparison.OrdinalIgnoreCase) || name.Equals(Path.GetFileNameWithoutExtension(arena), StringComparison.OrdinalIgnoreCase)
        || (I76.Formats.Vfs.GameInstall.IsMissionFile(name) && !name.Contains(' '));

    /// <summary>The same popup's LIMIT: "%d Laps", "%d Captures", "%d Points", "%d Kills" or "%d Minutes", else "No Limits!".</summary>
    public static string SessionLimit(SessionInfo g)
    {
        if (g.Coop) return "The mission's end";
        if (g.GoalValue > 0)
            switch (g.Goal)
            {
                case MeleeGoal.Laps: return $"{g.GoalValue} Laps";
                case MeleeGoal.Captures: return $"{g.GoalValue} Captures";
                case MeleeGoal.Points: return $"{g.GoalValue} Points";
                case MeleeGoal.Kills: return $"{g.GoalValue} Kills";
            }
        // the time limit in seconds; a nonsense one from the wire is none
        if (g.TimeLimit > 0 && g.TimeLimit < 1e7f) return $"{Math.Max(1, (int)Math.Round(g.TimeLimit / 60))} Minutes";
        return "No Limits!";
    }

    /// <summary>The host left and another took over (the same words in both games).</summary>
    public static string NewHost(string? name) => name is null ? "*** You are now the host!" : $"*** {name} is now the host!";

    /// <summary>The gap while the successor takes over (I26's: the original's peers carried on without one).</summary>
    public const string Migrating = "*** Migrating host...";

    /// <summary>
    /// The end's line on this machine: the base game's "maximum score" lines for a points goal ("*** You have reached the
    /// maximum score! You win!", "*** %s has reached the maximum score! You lose, chump!", the "Team %s" forms), else Nitro's
    /// "*** %s has reached %d points/kills/captures! %s" or "*** %s has completed %d laps! %s" with "You win!" or "You lose,
    /// chump!" by this player's own slot or team. Null on a time limit: its line is the server's ("*** Time limit has
    /// elapsed!") and every machine is a winner (the Nitro Pack); <paramref name="won"/> is then true.
    /// </summary>
    public static string? End(EndEvent e, bool nitro, MeleeGoal goal, bool teams, int mySlot, int myTeam, string winnerName, string teamName, out bool won)
    {
        won = e.TimeUp || (teams ? e.Team == myTeam : e.Winner == mySlot);
        if (e.TimeUp) return null;
        string verdict = won ? "You win!" : "You lose, chump!";
        if (!nitro && goal == MeleeGoal.Points)
            return teams ? $"*** Team {teamName} has reached the maximum score! {verdict}"
                : won ? "*** You have reached the maximum score! You win!" : $"*** {winnerName} has reached the maximum score! You lose, chump!";
        string who = teams ? teamName : winnerName;
        return goal switch
        {
            MeleeGoal.Laps => $"*** {who} has completed {e.Count} laps! {verdict}",
            MeleeGoal.Captures => $"*** {who} has reached {e.Count} captures! {verdict}",
            MeleeGoal.Kills => $"*** {who} has reached {e.Count} kills! {verdict}",
            _ => $"*** {who} has reached {e.Count} points! {verdict}",
        };
    }
}
