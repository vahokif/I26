namespace I76.Server;

/// <summary>
/// What the next round is when a melee ends and the server carries on (docs/multiplayer.md "Rounds"): the same arena
/// again, or the next one of <see cref="ServerOptions.Rotation"/>, wrapping round. Pure, so the order is testable
/// without an install.
/// </summary>
public static class RoundPlan
{
    /// <summary>An arena as named on the command line or in a rotation: without a ".msn", compared without case.</summary>
    public static string Normalize(string arena) =>
        arena.Trim() is var a && a.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a.Trim();

    /// <summary>"a,b,c" (or ';' separated) as a rotation; empty names are dropped.</summary>
    public static IReadOnlyList<string> ParseRotation(string list) =>
        [.. list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize).Where(a => a.Length > 0)];

    /// <summary>
    /// The arena after <paramref name="current"/>: the same one with no rotation; else the entry after it in the list
    /// (wrapping), or the list's first when the current arena is not in it (the arena the server started on).
    /// </summary>
    public static string NextArena(string current, IReadOnlyList<string> rotation)
    {
        current = Normalize(current);
        if (rotation.Count == 0) return current;
        int i = rotation.ToList().FindIndex(a => string.Equals(Normalize(a), current, StringComparison.OrdinalIgnoreCase));
        return Normalize(rotation[(i + 1) % rotation.Count]);
    }

    /// <summary>
    /// The options of the round after <paramref name="o"/>'s: <paramref name="o"/> with the next arena, its
    /// kind (a race or capture the flag arena names its own, and the goal that went with another kind is dropped), and
    /// a seed of its own (the last seed plus one, so a fixed --seed still replays). Scores start again by
    /// construction: it is a new game.
    /// </summary>
    public static ServerOptions Next(ServerOptions o)
    {
        string arena = NextArena(o.Arena, o.Rotation);
        var next = o with { Arena = arena, Seed = unchecked(o.Seed + 1) };
        if (string.Equals(arena, Normalize(o.Arena), StringComparison.OrdinalIgnoreCase)) return next;
        var (kind, teams) = ServerOptions.KindOf(arena);
        return next with
        {
            Kind = kind,
            Goal = kind == o.Kind ? o.Goal : I76.Sim.Missions.MeleeGoal.None,
            GoalValue = kind == o.Kind ? o.GoalValue : o.GoalValue > 0 ? DefaultLimit(kind) : 0,
            CtfTeams = kind == I76.Sim.Missions.MeleeKind.Ctf ? teams : o.CtfTeams,
        };
    }

    /// <summary>A limit for a round whose kind changed, when the host had set one (an unlimited game stays unlimited): the Nitro form's defaults.</summary>
    private static int DefaultLimit(I76.Sim.Missions.MeleeKind kind) => kind is I76.Sim.Missions.MeleeKind.Race or I76.Sim.Missions.MeleeKind.Ctf ? 3 : 0;
}
