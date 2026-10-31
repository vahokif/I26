namespace I76.Formats.Input;

/// <summary>
/// A held mouse button on a list's arrow (or the add / remove driver buttons): the click acts at once, then after
/// <see cref="Delay"/> again every <see cref="Interval"/> while the button stays down over the same hotspot. The shell's
/// own timing (the original lists' repeat: 350 ms, then 100 ms); one place for every screen's arrows.
/// </summary>
public sealed class HoldRepeat
{
    public const double Delay = 0.35, Interval = 0.1;

    private int _id = -1;
    private double _at;

    /// <summary>The hotspot being held, or -1.</summary>
    public int Id => _id;

    /// <summary>A click landed on the repeating hotspot <paramref name="id"/>.</summary>
    public void Begin(int id) { _id = id; _at = Delay; }

    public void Stop() => _id = -1;

    /// <summary>
    /// One frame of <paramref name="dt"/> seconds with the button <paramref name="held"/> and the mouse over hotspot
    /// <paramref name="hit"/> (-1 for none). Returns how many times the hotspot acts this frame (0 or more: a slow frame
    /// owes several). Letting go or leaving the hotspot ends the repeat.
    /// </summary>
    public int Update(bool held, int hit, double dt)
    {
        if (_id < 0) return 0;
        if (!held || hit != _id) { _id = -1; return 0; }
        int n = 0;
        _at -= dt;
        while (_at <= 0) { n++; _at += Interval; }
        return n;
    }
}
