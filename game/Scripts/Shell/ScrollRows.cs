using I76.Formats.Input;

namespace I76.Game.Shell;

/// <summary>
/// The port's scrolling list on a notepad sheet (Graphic Detail's every row; Play Options' own rows, in the sheet's free lines
/// under the original's): each entry (a label, a value, a check strip) sits in a list slot, <see cref="Shown"/> slots show from
/// <see cref="Scroll"/>, one to a ruled line of <see cref="LineH"/> px from <see cref="Top"/>. Pure, so it is tested without the engine.
/// </summary>
public sealed class ScrollRows<T> where T : class
{
    /// <summary>The first line's y.</summary>
    public int Top { get; set; }
    /// <summary>How many lines show.</summary>
    public int Shown { get; set; } = 1;
    public int LineH { get; set; } = 23;
    /// <summary>The first slot showing.</summary>
    public int Scroll { get; private set; }
    private readonly List<(T It, int Slot, int Dy)> _rows = [];

    /// <summary>An entry in slot <paramref name="slot"/>, drawn <paramref name="dy"/> px below its line's y.</summary>
    public T Add(T it, int slot, int dy = 0)
    {
        _rows.Add((it, slot, dy));
        return it;
    }

    public int Slots => _rows.Count == 0 ? 0 : _rows.Max(r => r.Slot) + 1;
    public int MaxScroll => Math.Max(0, Slots - Shown);

    /// <summary>Scrolls by <paramref name="by"/> slots, held to the ends: true when it moved.</summary>
    public bool ScrollBy(int by)
    {
        int to = Math.Clamp(Scroll + by, 0, MaxScroll);
        if (to == Scroll) return false;
        Scroll = to;
        return true;
    }

    /// <summary>Every entry's y and whether it shows, at the current scroll.</summary>
    public IEnumerable<(T It, int Y, bool Shows)> Layout()
    {
        Scroll = Math.Clamp(Scroll, 0, MaxScroll);
        foreach (var (it, slot, dy) in _rows)
        {
            int line = slot - Scroll;
            yield return (it, Top + LineH * line + dy, line >= 0 && line < Shown);
        }
    }

    /// <summary>The slot on visible line <paramref name="line"/> (0 the top one), or -1 past the list's end.</summary>
    public int SlotOn(int line) => line >= 0 && line < Shown && Scroll + line < Slots ? Scroll + line : -1;

    /// <summary>The slot of an entry, or -1 when it is not in the list.</summary>
    public int SlotOf(T it)
    {
        foreach (var r in _rows) if (r.It == it) return r.Slot;
        return -1;
    }

    /// <summary>A pad press past an entry: -1 or 1 (a row up or down) when it is on the top or bottom line and the press leaves the list that way, else 0.</summary>
    public int EdgeStep(T it, NavDir d)
    {
        int slot = SlotOf(it);
        if (slot < 0) return 0;
        int line = slot - Scroll;
        return d == NavDir.Up && line == 0 ? -1 : d == NavDir.Down && line == Shown - 1 ? 1 : 0;
    }

    /// <summary>The keys' scroll: Up and Down a row, Page Up and Page Down a window.</summary>
    public int KeyStep(bool up, bool down, bool pageUp, bool pageDown) =>
        (down ? 1 : 0) - (up ? 1 : 0) + (pageDown ? Shown : 0) - (pageUp ? Shown : 0);

    /// <summary>The scroll mark, none when everything shows: a track (from Top, H high) with a thumb (at ThumbAt, Thumb high), an arrow over it when there is more above and under it when there is more below.</summary>
    public (int Top, int H, int ThumbAt, int Thumb, bool MoreAbove, bool MoreBelow)? Mark()
    {
        if (MaxScroll == 0) return null;
        int top = Top + 6, h = LineH * Shown - 18;
        int thumb = Math.Max(10, h * Shown / Slots);
        return (top, h, top + (h - thumb) * Scroll / MaxScroll, thumb, Scroll > 0, Scroll < MaxScroll);
    }
}
