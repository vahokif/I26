using I76.Net;
using I76.Net.Hud;
using I76.Sim.Missions;

namespace I76.Game.Net;

/// <summary>
/// I76_NET_HUD_DEMO=players[,what...]: the network HUD filled with made-up players and lines, for captures of each
/// state without staging it in a real game (docs/dev-hooks.md). What: board (the players' window held), teams (the
/// teams' window held), teamplay, ctf, race, chat (a conversation), typing (the entry open), dead, ready (dead, the
/// respawn open), end-win, end-lose, timeup, final, invite, boot, host (this machine hosts), page2, pad, me:NAME (your own
/// row's name).
/// </summary>
public sealed class NetHudDemo
{
    private readonly int _players;
    private readonly HashSet<string> _what;

    private readonly string? _me;
    private NetHudDemo(int players, IEnumerable<string> what)
    {
        _players = Math.Clamp(players, 1, 64); _what = [.. what];
        _me = _what.FirstOrDefault(w => w.StartsWith("me:", StringComparison.Ordinal)) is { Length: > 3 } m ? m[3..] : null;
    }

    public static NetHudDemo? FromEnvironment()
    {
        if (System.Environment.GetEnvironmentVariable("I76_NET_HUD_DEMO") is not { Length: > 0 } v) return null;
        var parts = v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new NetHudDemo(parts.Length > 0 && int.TryParse(parts[0], out int n) ? n : 8, parts.Skip(1));
    }

    private bool Has(string w) => _what.Contains(w);

    private static readonly string[] Names =
    [
        "Groove Champion", "Taurus", "Skeeter", "Jade Wilson", "Natalie", "Antonio Malochio", "Sweet Wheels", "Boogie Down",
        "Funky Fresh", "Smokey", "Foxy Lady", "Dirty Harry", "Big Daddy", "Disco Inferno", "Sugar Ray", "Mr. Pink",
        "Cleopatra Jones", "Shaft", "Superfly", "Dolemite", "Coffy", "Slaughter", "Black Caesar", "Truck Turner",
        "Cotton Comes", "Willie Dynamite", "Hammer", "Detroit 9000", "Blacula", "Friday Foster", "Sheba Baby", "Hell Up",
        "Trouble Man", "Across 110th", "Cool Breeze", "Mean Johnny", "Gordon's War", "Three Tough", "Bucktown", "Abby",
        "Darktown Strutter", "Mack", "Penitentiary", "Disco Godfather", "Car Wash", "Cooley High", "Cornbread", "Uptown",
        "Let's Do It", "Sparkle", "Bingo Long", "Mahogany", "Lady Sings", "Wattstax", "Brothers", "Greased Lightning",
        "J.D.'s Revenge", "Monkey Hustle", "Petey Wheatstraw", "Black Belt", "TNT Jackson", "Savage Sisters", "Ebony", "Stryker",
    ];

    /// <summary>This frame's made-up readouts.</summary>
    public NetHudFrame Frame(double t, bool host)
    {
        bool ctf = Has("ctf"), race = Has("race");
        bool teamPlay = Has("teamplay") || Has("teams") || ctf;
        var kind = ctf ? MeleeKind.Ctf : race ? MeleeKind.Race : MeleeKind.Combat;
        int teams = ctf ? 4 : 4;
        host = (host || Has("host")) && !Has("client");
        var f = new NetHudFrame
        {
            T = t, Kind = kind, Goal = ctf ? MeleeGoal.Captures : race ? MeleeGoal.Laps : MeleeGoal.Points, TeamPlay = teamPlay,
            GoalValue = ctf ? 5 : race ? 5 : 2000, TimeLimit = Has("timeup") || ctf ? 900 : 0, Clock = Has("timeup") ? 900 : 312.4f, HasScores = true,
            MySlot = 2, Ping = 48, Host = host, Pad = Has("pad"),
        };
        var rows = new List<PlayerRow>();
        var rng = new Random(_players * 7 + 1);
        for (int i = 0; i < _players; i++)
        {
            int team = teamPlay ? 1 + i % teams : 0;
            int kills = race ? rng.Next(0, 5) : ctf ? 0 : rng.Next(0, 16);
            int deaths = rng.Next(0, 12);
            int score = kind == MeleeKind.Combat ? kills * 100 - deaths * 20 + rng.Next(0, 5) * 10 : 0;
            if (i == 0 && kind == MeleeKind.Combat) { kills = 17; score = 1640; }
            if (i == 2 && kind == MeleeKind.Combat) { kills = 9; score = 850; deaths = 4; }
            rows.Add(new PlayerRow((byte)i, i == f.MySlot && _me is not null ? _me : Names[i], (byte)team, score, kills, deaths, (ushort)(24 + rng.Next(0, 160)), 100 + i, rng.Next(0, 6) != 0));
        }
        if (race) rows[Math.Min(2, rows.Count - 1)] = rows[Math.Min(2, rows.Count - 1)] with { Kills = 3 };
        if (ctf)
        {
            int[] caps = [3, 1, 2, 0];
            for (int i = 0; i < rows.Count; i++) rows[i] = rows[i] with { Kills = caps[rows[i].Team - 1] };
        }
        f.MyTeam = rows[Math.Min(2, rows.Count - 1)].Team;
        f.MySlot = Math.Min(2, rows.Count - 1);
        string Name(int slot) => rows[slot % rows.Count].Name;
        int Team(int slot) => rows[slot % rows.Count].Team;
        var all = rows.Select(p => ((int)p.Team, p.Score, p.Kills)).ToList();
        f.Leader = GoalCount.Leader(f.Goal, teamPlay, all);
        var me = rows[f.MySlot];
        f.Mine = teamPlay ? GoalCount.Leader(f.Goal, true, all.Where(r => r.Item1 == me.Team)) : f.Goal == MeleeGoal.Points ? me.Score : me.Kills;
        var counts = teamPlay ? ScoreTable.Teams(rows, kind, f.TeamName).Select(x => ScoreTable.Metric(kind, x.Score, x.Kills)).ToList()
            : rows.Select(p => ScoreTable.Metric(kind, p.Score, p.Kills)).ToList();
        f.Field = counts.Count;
        f.Place = ScoreTable.Place(teamPlay ? ScoreTable.Teams(rows, kind, f.TeamName).First(x => x.Team == me.Team) is var mt ? ScoreTable.Metric(kind, mt.Score, mt.Kills) : 0
            : ScoreTable.Metric(kind, me.Score, me.Kills), counts);
        f.GoalLine = NetHud.GoalLine(f.Goal, f.GoalValue, f.TimeLimit, f.Clock, true, f.Leader);
        if (ctf)
        {
            f.Flags.Add((1, 1, null, 3));
            f.Flags.Add((2, 4, Name(0), 1));
            f.Flags.Add((3, 2, null, 2));
            f.Flags.Add((4, 1, null, 0));
        }
        f.ScoresHint = Has("hint");
        var ctx = new ScoreContext(kind, teamPlay, f.MySlot, host, Classic && !teamPlay ? ScoreOrder.Slot : ScoreOrder.Ranked, Classic ? ScoreTable.ClassicRows : ScoreTable.ModernRows)
            { HostSlot = host ? f.MySlot : 0 };
        if (Has("board") || Has("teams")) f.Board = ScoreTable.Players(rows, ctx, Has("page2") ? 1 : 0, f.TeamName);
        // the feed: a burst of kills and joins, some about this player
        var feed = new HudFeed();
        double at = t - 6;
        void Add(FeedEntry e) { feed.Add(e); }
        string Kill(int v, int? k) => NetLines.Kill(Name(v), k is { } kk ? Name(kk) : null);
        // a wave of joins (64 players' worth would scroll everything away): the rate cap shows four and counts the rest
        for (int i = 0; i < (_players > 16 ? 9 : 1); i++)
            Add(new FeedEntry(FeedKind.Join, NetLines.Join(false, teamPlay, Name(5 + i * 3), f.TeamName(Team(5 + i * 3))), at - 1) { Who = Name(5 + i * 3), WhoTeam = Team(5 + i * 3) });
        Add(new FeedEntry(FeedKind.Kill, Kill(3, 0), at + 1.5) { Who = Name(0), What = Name(3), WhoTeam = Team(0), WhatTeam = Team(3) });
        Add(new FeedEntry(FeedKind.Kill, Kill(4, f.MySlot), at + 3.5) { Who = Name(f.MySlot), What = Name(4), WhoTeam = Team(f.MySlot), WhatTeam = Team(4), ByMe = true });
        Add(new FeedEntry(FeedKind.Kill, Kill(6, null), at + 4) { What = Name(6), WhatTeam = Team(6) });
        if (ctf) Add(new FeedEntry(FeedKind.Notice, $"*** {Name(0)} has taken the {f.TeamName(2)} flag!", at + 4.5));
        if (Has("dead") || Has("ready"))
            Add(new FeedEntry(FeedKind.Kill, Kill(f.MySlot, 1), at + 5) { Who = Name(1), What = Name(f.MySlot), WhoTeam = Team(1), WhatTeam = Team(f.MySlot), OnMe = true });
        Add(new FeedEntry(FeedKind.Leave, NetLines.Leave(false, Name(7)), at + 5.5) { Who = Name(7), WhoTeam = Team(7) });

        if (Has("chat") || Has("typing"))
        {
            (int from, string text)[] talk =
            [
                (0, "nice wheels, chump"), (f.MySlot, "come say that to my 50 cal"), (5, "anyone seen the flag?"), (-1, "Round ends in 5 minutes"),
                (1, "Taurus, cover the bridge"), (3, "on my way. that piranha is toast"),
            ];
            double ct = t - 9;
            foreach (var (from, text) in talk)
            {
                string who = from < 0 ? "Server" : Name(from);
                Add(new FeedEntry(FeedKind.Chat, NetLines.Chat(who, text), ct += 1.3) { Who = who, What = text, WhoTeam = from < 0 ? 0 : Team(from), ByMe = from == f.MySlot });
            }
        }
        if (Has("longchat"))
        {
            // long lines, an unbroken word, a long server line, and a long conversation (the history)
            (int from, string text)[] longTalk =
            [
                (0, "ok so the plan is we all meet at the gas station on the east side, then drive up the ridge road together and take the bridge before the others get there"),
                (f.MySlot, "wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww"),
                (5, "copy that"), (1, "who took my parking spot?"), (3, "sorry boss"), (-1, "Round ends in 5 minutes and the arena will be restarted with the next map in the rotation"),
                (0, "nice shot"), (4, "thx"), (2, "brb"), (6, "gg"),
            ];
            double lt = t - 25;
            foreach (var (from, text) in longTalk)
            {
                string who = from < 0 ? "Server" : Name(from);
                Add(new FeedEntry(FeedKind.Chat, NetLines.Chat(who, text), lt += 1.7) { Who = who, What = text, WhoTeam = from < 0 ? 0 : Team(from), ByMe = from == f.MySlot });
            }
            Add(new FeedEntry(FeedKind.Notice, "*** The host has changed the game's options: the game's simulation rate is 60 Hz, for everyone in the game", t - 2));
        }
        f.Feed = feed;
        // the keys the standings list, as in a game: chat and self-destruct always, the CB's while it is on
        f.ChatKey = "Backspace"; f.NewCarKey = "Ctrl+Delete"; f.ChatHint = f.NewCarHint = true;
        // the CB radio: "cb" you are transmitting and two others are on the air
        f.CbKey = "C";
        f.CbHint = true;
        if (Has("cb")) { f.CbTx = true; f.CbTalking = [(Name(2), Team(2)), (Name(5), Team(5))]; }
        if (Has("typing")) { f.ChatOpen = true; f.ChatText = Has("longchat") ? "this is a very long line that i am typing and it keeps going on and on past the end of the window so it has to scroll" : "meet me at the gas station"; }
        if (Has("dead")) f.Death = new NetHudFrame.DeathInfo(Name(1), Team(1), false, 3.4, 5, "Space", null);
        if (Has("ready")) f.Death = new NetHudFrame.DeathInfo(Name(1), Team(1), false, -1, 5, "Space", "Leprechaun Grifter");
        if (Has("end-win")) f.End = new NetHudFrame.EndInfo("*** You have reached the maximum score! You win!", true, false, Name(f.MySlot), Team(f.MySlot), 20, t - 1);
        if (Has("end-lose"))
            f.End = new NetHudFrame.EndInfo(race ? $"*** {Name(0)} has completed 5 laps! You lose, chump!" : $"*** {Name(0)} has reached the maximum score! You lose, chump!",
                false, false, Name(0), Team(0), 20, t - 1);
        if (Has("timeup")) f.End = new NetHudFrame.EndInfo(null, true, true, null, 0, 0, t - 1);
        if (Has("final"))
        {
            var st = rows.OrderByDescending(p => ScoreTable.Metric(kind, p.Score, p.Kills)).ToList();
            var ranks = ScoreTable.Ranks(st.Select(p => ScoreTable.Metric(kind, p.Score, p.Kills)).ToList());
            f.Final = new NetHudFrame.FinalInfo(st.Select((p, i) => new NetHudFrame.Standing(ranks[i], p.Name, p.Team, ScoreTable.Metric(kind, p.Score, p.Kills), p.Kills, p.Deaths, p.Slot == f.MySlot)).ToList(), 11.2);
            f.End ??= new NetHudFrame.EndInfo($"*** {Name(0)} has reached the maximum score! You lose, chump!", false, false, Name(0), Team(0), 20, t - 9);
        }
        if (Has("invite")) f.Prompt = new(NetHudFrame.PromptKind.Invite, "Cleopatra Jones");
        if (Has("boot")) f.Prompt = new(NetHudFrame.PromptKind.Boot, Name(5));
        return f;
    }

    /// <summary>The 1997 look is drawing (the score window then in slot order, ten rows a page).</summary>
    public bool Classic { get; set; }
}
