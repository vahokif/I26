namespace I76.Net.Hud;

/// <summary>What a line of the network game's console is, for the looks that draw them apart (the modern look's kill feed and chat).</summary>
public enum FeedKind { Kill, Join, Leave, Chat, Notice, Own }

/// <summary>
/// One console line: the original's words (<see cref="Text"/>, which the 1997 look prints) and its parts for a look that
/// draws them its own way: the killer and victim (or the speaker and the words) with their teams, and whether the line
/// is about this player.
/// </summary>
public sealed record FeedEntry(FeedKind Kind, string Text, double At)
{
    /// <summary>A kill's killer (null: none), a chat line's speaker, a join's or leave's player.</summary>
    public string? Who { get; init; }
    /// <summary>A kill's victim, a chat line's words.</summary>
    public string? What { get; init; }
    public int WhoTeam { get; init; }
    public int WhatTeam { get; init; }
    /// <summary>This player is the killer (or the speaker).</summary>
    public bool ByMe { get; init; }
    /// <summary>This player is the victim.</summary>
    public bool OnMe { get; init; }
}

/// <summary>
/// The console's lines (both looks' feed): kills, joins, leaves, chat and the server's *** lines, newest last. Joins,
/// leaves and kills are rate-capped (the port's, for 64 players): at most <see cref="Burst"/> a <see cref="Window"/>
/// seconds show as lines, the rest counted into one "and N more" line; chat and notices never are.
/// </summary>
public sealed class HudFeed
{
    public const int Burst = 4;
    public const double Window = 2;
    /// <summary>How long a line stays (the 1997 look's ten seconds; the modern look fades over the last part).</summary>
    public const double Life = 10;
    /// <summary>Chat lines stay longer than the other lines in both looks (long enough to be read after a glance away), and while the entry is open the history shows.</summary>
    public const double ChatLife = 30;
    /// <summary>The most lines kept, and the most chat lines (kept past their time for the history shown while typing).</summary>
    public const int Keep = 100, KeepChat = 40;

    private readonly List<FeedEntry> _lines = [];
    private double _windowAt = double.NegativeInfinity;
    private int _inWindow;
    public int More { get; private set; }
    public double MoreAt { get; private set; }

    public IReadOnlyList<FeedEntry> Lines => _lines;

    /// <summary>Adds a line at time <paramref name="now"/>; returns false when the rate cap counted it instead.</summary>
    public bool Add(FeedEntry e)
    {
        if (e.Kind is FeedKind.Kill or FeedKind.Join or FeedKind.Leave)
        {
            if (e.At - _windowAt >= Window) { _windowAt = e.At; _inWindow = 0; }
            if (++_inWindow > Burst) { More++; MoreAt = e.At; return false; }
        }
        _lines.Add(e);
        if (_lines.Count > Keep) _lines.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// Drops what has outlived its time: lines other than chat after <see cref="ChatLife"/> (each look shows its own span),
    /// chat beyond the last <see cref="KeepChat"/>, the "more" count after <see cref="Life"/>.
    /// </summary>
    public void Expire(double now)
    {
        _lines.RemoveAll(l => l.Kind != FeedKind.Chat && now - l.At > ChatLife);
        int chat = _lines.Count(l => l.Kind == FeedKind.Chat);
        for (int i = 0; chat > KeepChat && i < _lines.Count;)
            if (_lines[i].Kind == FeedKind.Chat) { _lines.RemoveAt(i); chat--; } else i++;
        if (More > 0 && now - MoreAt > Life) More = 0;
    }

    /// <summary>The last <paramref name="max"/> lines younger than <paramref name="life"/> that <paramref name="which"/> takes, oldest first.</summary>
    public List<FeedEntry> Recent(double now, double life, int max, Func<FeedEntry, bool>? which = null)
    {
        var r = new List<FeedEntry>();
        for (int i = _lines.Count - 1; i >= 0 && r.Count < max; i--)
        {
            var l = _lines[i];
            if (now - l.At > life) continue;
            if (which is null || which(l)) r.Add(l);
        }
        r.Reverse();
        return r;
    }

    /// <summary>A line's opacity: whole until the last <paramref name="fade"/> seconds of its <paramref name="life"/>, then down to nothing.</summary>
    public static float Alpha(double age, double life, double fade = 1.5) =>
        age <= life - fade ? 1f : age >= life ? 0f : (float)((life - age) / fade);
}
