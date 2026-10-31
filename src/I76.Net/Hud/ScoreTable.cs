using I76.Sim.Missions;

namespace I76.Net.Hud;

/// <summary>How the score window orders its players: the original's slot order, or ranked (the modern look: best first, grouped by team in team play).</summary>
public enum ScoreOrder { Slot, Ranked }

/// <summary>What the score window is drawn for.</summary>
public sealed record ScoreContext(MeleeKind Kind, bool TeamPlay, int MySlot, bool Host, ScoreOrder Order, int PerPage)
{
    /// <summary>The player whose machine runs the game, when this machine knows it (its own, or one a migration named); -1 when not.</summary>
    public int HostSlot { get; init; } = -1;
}

public enum ScoreLineKind { Player, Team }

/// <summary>
/// A row of the score window. A player's: its mark (the host's own "H", the numbers the host's BOOT keys take, "&gt;" for
/// one's own row on another's machine), its rank (competition ranking: 1, 2, 2, 4), its counts and ping. A team's: the
/// team's totals (the modern look's group header, the 1997 look's team window).
/// </summary>
public sealed record ScoreLine(ScoreLineKind Kind, string Name, int Team, int Score, int Kills, int Deaths)
{
    public int Slot { get; init; } = -1;
    public string Mark { get; init; } = "";
    public int Rank { get; init; }
    public int Ping { get; init; }
    public bool Alive { get; init; } = true;
    public bool Me { get; init; }
    public bool IsHost { get; init; }
    /// <summary>One's own row shown although it is not on this page (kept in view).</summary>
    public bool Pinned { get; init; }
    /// <summary>A team's members on the whole board.</summary>
    public int Members { get; init; }
}

/// <summary>A page of the score window: its rows, which page of how many, the range of players it shows, and the rows the host's BOOT keys number on it.</summary>
public sealed record ScoreView(IReadOnlyList<ScoreLine> Lines, int Page, int Pages, int First, int Last, int Total, IReadOnlyList<(byte Slot, string Name)> Numbered);

/// <summary>
/// The score window's rows and pages (paged for 64 players). The players' window lists every
/// player; on the listen host's machine its own row is marked H and the others are numbered 1–9 a page for BOOT_n
/// (the original numbered 1–7 of eight), on another machine one's own row is kept in view. The teams' window sums
/// each team (captures are a team's own count, not a sum) and orders them best first.
/// </summary>
public static class ScoreTable
{
    /// <summary>The 1997 look's page: ten rows (the host: its own and nine numbered).</summary>
    public const int ClassicRows = 10;
    /// <summary>The modern look's page for a player who is not the host: two columns of sixteen.</summary>
    public const int ModernRows = 32;
    /// <summary>The host's numbered rows a page (BOOT_1..7 and the port's Ctrl+8, Ctrl+9), in both looks.</summary>
    public const int HostRows = 9;

    public static int PageCount(int rows, int per) => Math.Max(1, (rows + Math.Max(1, per) - 1) / Math.Max(1, per));

    /// <summary>The count a player is ranked by: the score in a combat game, laps in a race, captures in CTF (both in the kills field).</summary>
    public static int Metric(MeleeKind kind, int score, int kills) => kind == MeleeKind.Combat ? score : kills;

    /// <summary>Competition ranks (1, 2, 2, 4) of <paramref name="values"/>, best (largest) first.</summary>
    public static int[] Ranks(IReadOnlyList<int> values)
    {
        var r = new int[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            int better = 0;
            for (int j = 0; j < values.Count; j++) if (values[j] > values[i]) better++;
            r[i] = better + 1;
        }
        return r;
    }

    /// <summary>The teams' totals, best first (the teams' window, and the modern look's group headers).</summary>
    public static List<ScoreLine> Teams(IReadOnlyList<PlayerRow> rows, MeleeKind kind, Func<int, string> teamName)
    {
        var teams = rows.Where(p => p.Team > 0).GroupBy(p => (int)p.Team).Select(g => new ScoreLine(ScoreLineKind.Team, teamName(g.Key), g.Key,
                g.Sum(p => p.Score), kind == MeleeKind.Ctf ? g.Max(p => p.Kills) : g.Sum(p => p.Kills), g.Sum(p => p.Deaths)) { Members = g.Count() })
            .ToList();
        var ranks = Ranks(teams.Select(t => Metric(kind, t.Score, t.Kills)).ToList());
        return teams.Select((t, i) => t with { Rank = ranks[i] }).OrderBy(t => t.Rank).ThenBy(t => t.Team).ToList();
    }

    /// <summary>The players' window, page <paramref name="page"/> (clamped to the pages there are).</summary>
    public static ScoreView Players(IReadOnlyList<PlayerRow> rows, ScoreContext c, int page, Func<int, string> teamName)
    {
        var kind = c.Kind;
        bool grouped = c.Order == ScoreOrder.Ranked && c.TeamPlay;
        // ranks: over everyone, or within the team in team play
        var rank = new Dictionary<byte, int>();
        foreach (var g in grouped ? rows.GroupBy(p => (int)p.Team) : rows.GroupBy(_ => 0))
        {
            var list = g.ToList();
            var r = Ranks(list.Select(p => Metric(kind, p.Score, p.Kills)).ToList());
            for (int i = 0; i < list.Count; i++) rank[list[i].Slot] = r[i];
        }
        var teams = Teams(rows, kind, teamName);
        var teamOrder = teams.Select((t, i) => (t.Team, i)).ToDictionary(x => x.Team, x => x.i);
        IEnumerable<PlayerRow> ordered = rows.OrderBy(p => p.Slot);
        if (c.Order == ScoreOrder.Ranked)
            ordered = rows.OrderBy(p => grouped ? teamOrder.GetValueOrDefault(p.Team, int.MaxValue) : 0)
                .ThenBy(p => rank[p.Slot]).ThenByDescending(p => p.Kills).ThenBy(p => p.Deaths).ThenBy(p => p.Slot);
        var all = ordered.ToList();
        var position = all.Select((p, i) => (p.Slot, i)).ToDictionary(x => x.Slot, x => x.i);
        // the host pages the others (its own row besides); another machine pages everyone, its own row kept in view
        var paged = c.Host ? all.Where(p => p.Slot != c.MySlot).ToList() : all;
        int per = c.Host ? HostRows : Math.Max(1, c.PerPage);
        int pages = PageCount(paged.Count, per);
        page = Math.Clamp(page, 0, pages - 1);
        var on = paged.Skip(page * per).Take(per).ToList();
        var numbered = c.Host ? on.Select(p => (p.Slot, p.Name)).ToList() : [];
        bool pinned = false;
        if (all.FirstOrDefault(p => p.Slot == c.MySlot) is { } me && !on.Contains(me))
        {
            pinned = !c.Host;
            on.Add(me);
            on.Sort((a, b) => position[a.Slot].CompareTo(position[b.Slot]));
        }
        var lines = new List<ScoreLine>();
        int lastTeam = -1;
        foreach (var p in on)
        {
            if (grouped && p.Team != lastTeam)
            {
                lastTeam = p.Team;
                if (teams.FirstOrDefault(t => t.Team == p.Team) is { } t) lines.Add(t);
            }
            bool own = p.Slot == c.MySlot;
            string mark = c.Host ? (own ? "H" : $"{numbered.FindIndex(n => n.Slot == p.Slot) + 1}") : own ? ">" : "";
            lines.Add(new ScoreLine(ScoreLineKind.Player, p.Name, p.Team, p.Score, p.Kills, p.Deaths)
            {
                Slot = p.Slot, Mark = mark, Rank = rank[p.Slot], Ping = p.Ping, Alive = p.Alive, Me = own,
                IsHost = c.Host ? own : p.Slot == c.HostSlot, Pinned = own && pinned,
            });
        }
        int first = paged.Count == 0 ? 0 : page * per + 1, last = Math.Min(paged.Count, (page + 1) * per);
        return new ScoreView(lines, page, pages, first, last, paged.Count, numbered);
    }

    /// <summary>The column titles for the kind: a race's "Laps" and CTF's "Captures" in the score's place, with no kills column (the Nitro Pack's windows).</summary>
    public static (string Score, string Kills, string Deaths) Heads(MeleeKind kind) => kind switch
    {
        MeleeKind.Race => ("Laps", "", "Deaths"),
        MeleeKind.Ctf => ("Captures", "", "Deaths"),
        _ => ("Score", "Kills", "Deaths"),
    };

    /// <summary>A place among <paramref name="count"/> values (competition ranking), for the race's position: this player's count against everyone's.</summary>
    public static int Place(int mine, IEnumerable<int> everyone) => 1 + everyone.Count(v => v > mine);
}
