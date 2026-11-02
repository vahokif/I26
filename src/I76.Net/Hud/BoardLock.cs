namespace I76.Net.Hud;

/// <summary>
/// The score window's key. Held, the window is up while it is (the original's). A tap, let go within <see cref="Tap"/>, locks
/// it up (the port's, #107): it stays until the next press of the key (or Esc), with a pointer to pick rows. A press while
/// locked unlocks; the window then stays only while that press is held, so a tap shuts it.
/// </summary>
public sealed class BoardLock
{
    /// <summary>The longest press that counts as a tap, in seconds.</summary>
    public const double Tap = 0.3;

    public bool Locked { get; private set; }
    private bool _down, _wasLocked;
    private double _downAt;

    /// <summary>The key's state at time <paramref name="t"/> (seconds); returns whether the window is up.</summary>
    public bool Update(bool held, double t)
    {
        if (held && !_down)
        {
            _down = true;
            _downAt = t;
            _wasLocked = Locked;
            Locked = false;
        }
        else if (!held && _down)
        {
            _down = false;
            if (!_wasLocked && t - _downAt <= Tap) Locked = true;
        }
        return _down || Locked;
    }

    /// <summary>Esc, or the game's end: the lock goes (a press still held keeps the window up until it is let go).</summary>
    public void Close() => Locked = false;
}

/// <summary>The locked score window's cursor, kept by slot so that a reordering of the rows keeps it on its player.</summary>
public static class BoardCursor
{
    /// <summary>The slot of the player row under (<paramref name="x"/>, <paramref name="y"/>) among the rows as drawn, or -1.</summary>
    public static int RowAt(IReadOnlyList<(HudRect Rect, int Slot)> rows, float x, float y)
    {
        foreach (var (r, slot) in rows)
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return slot;
        return -1;
    }

    /// <summary>
    /// The cursor moved <paramref name="delta"/> player rows from <paramref name="slot"/> on this page (team rows skipped). Off the
    /// page's end it asks for the next or previous page (<c>Page</c> +1 or -1, <c>Slot</c> -1: the caller takes that page's first or
    /// last row); with no cursor yet it takes the first row (down) or the last (up).
    /// </summary>
    public static (int Slot, int Page) Step(IReadOnlyList<ScoreLine> lines, int slot, int delta)
    {
        var players = lines.Where(l => l.Kind == ScoreLineKind.Player).Select(l => l.Slot).ToList();
        if (players.Count == 0) return (-1, 0);
        int i = players.IndexOf(slot);
        if (i < 0) return (delta >= 0 ? players[0] : players[^1], 0);
        int n = i + delta;
        if (n < 0) return (-1, -1);
        if (n >= players.Count) return (-1, 1);
        return (players[n], 0);
    }

    /// <summary>The first (<paramref name="last"/> false) or last player row of a page, or -1.</summary>
    public static int End(IReadOnlyList<ScoreLine> lines, bool last)
    {
        var players = lines.Where(l => l.Kind == ScoreLineKind.Player).ToList();
        return players.Count == 0 ? -1 : (last ? players[^1] : players[0]).Slot;
    }

    /// <summary>Whether the host may boot the player on this row: another player's row, on the listen host's own machine.</summary>
    public static bool CanBoot(bool host, ScoreLine l) => host && l.Kind == ScoreLineKind.Player && !l.Me && l.Slot >= 0;
}
