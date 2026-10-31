namespace I76.Formats.Input;

public enum NavDir { None, Up, Right, Down, Left }

/// <summary>A clickable region in the shell's 640x480 coordinates.</summary>
public readonly record struct NavRect(int X, int Y, int W, int H)
{
    public float CenterX => X + W / 2f;
    public float CenterY => Y + H / 2f;
}

/// <summary>
/// The pad's spatial menu navigation (the port's): which region a direction moves the focus to. Pure, over rectangles.
/// A region is a candidate when its centre lies in the pressed direction from the focused one's; among the candidates the
/// nearest wins (those within 45 degrees of the direction first), the offset across the direction counting double and a gap across it more, so that a neighbour in line
/// beats a nearer one off to the side. With nothing in that direction the focus stays (no wrapping).
/// </summary>
public static class PadNav
{
    /// <summary>The index of the region to focus: from <paramref name="from"/> (or -1 for none yet) in <paramref name="dir"/>; -1 when there is none.</summary>
    public static int Pick(IReadOnlyList<NavRect> rects, int from, NavDir dir)
    {
        if (rects.Count == 0 || dir == NavDir.None) return -1;
        if (from < 0 || from >= rects.Count)
        {
            // nothing focused yet: the first in reading order
            int first = 0;
            for (int i = 1; i < rects.Count; i++)
                if (rects[i].CenterY * 4 + rects[i].CenterX < rects[first].CenterY * 4 + rects[first].CenterX) first = i;
            return first;
        }
        var f = rects[from];
        int best = -1;
        float bestScore = float.MaxValue;
        bool inCone = false;
        for (int i = 0; i < rects.Count; i++)
        {
            if (i == from) continue;
            var r = rects[i];
            float dx = r.CenterX - f.CenterX, dy = r.CenterY - f.CenterY;
            bool vertical = dir is NavDir.Up or NavDir.Down;
            float along = dir switch { NavDir.Up => -dy, NavDir.Down => dy, NavDir.Left => -dx, _ => dx };
            float across = vertical ? dx : dy;
            if (along <= 0.5f) continue;                      // not in that direction
            float lo = vertical ? r.X : r.Y, hi = lo + (vertical ? r.W : r.H), flo = vertical ? f.X : f.Y, fhi = flo + (vertical ? f.W : f.H);
            float gap = Math.Max(0, Math.Max(lo - fhi, flo - hi));
            // sideways, rows that overlap are in line (left-aligned labels of different lengths have centres far apart): only their
            // gap counts in full, the centres' offset a little
            float side = gap > 0 ? gap + 0.25f * Math.Abs(across) : 0.25f * Math.Abs(across);
            float score = along + 2 * side + 4 * gap;
            // a region within 45 degrees of the direction beats any outside it
            bool cone = side <= along;
            if (cone && !inCone) { inCone = true; bestScore = float.MaxValue; }
            if (cone != inCone) continue;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }
}

/// <summary>
/// A pad's menu input with the sticks and auto-repeat: the D-pad and the left stick (past <see cref="Threshold"/>) give a
/// direction, repeating after <see cref="Delay"/> every <see cref="Interval"/> seconds while held; A, B and Start are on the
/// press only. After <see cref="Settle"/> nothing happens until everything is released (the press that opened the screen).
/// </summary>
public sealed class PadRepeater
{
    public const float Threshold = 0.5f;
    public const double Delay = 0.35, Interval = 0.12;

    [Flags] public enum Btn { None = 0, Accept = 1, Back = 2, Start = 4 }

    public readonly record struct Step(NavDir Dir, Btn Buttons)
    {
        public bool Any => Dir != NavDir.None || Buttons != Btn.None;
    }

    private NavDir _dir;
    private double _next;
    private Btn _prevButtons;
    private bool _settle;

    /// <summary>The direction a pad is held in: the D-pad first, else the left stick's stronger axis past the threshold.</summary>
    public static NavDir Held(in PadRaw p)
    {
        if (p.Has(PadButton.DpadUp)) return NavDir.Up;
        if (p.Has(PadButton.DpadDown)) return NavDir.Down;
        if (p.Has(PadButton.DpadLeft)) return NavDir.Left;
        if (p.Has(PadButton.DpadRight)) return NavDir.Right;
        float ax = Math.Abs(p.LeftX), ay = Math.Abs(p.LeftY);
        if (Math.Max(ax, ay) < Threshold) return NavDir.None;
        return ay >= ax ? (p.LeftY < 0 ? NavDir.Up : NavDir.Down) : (p.LeftX < 0 ? NavDir.Left : NavDir.Right);
    }

    private static Btn Buttons(in PadRaw p) =>
        (p.Has(PadButton.A) ? Btn.Accept : 0) | (p.Has(PadButton.B) ? Btn.Back : 0) | (p.Has(PadButton.Start) ? Btn.Start : 0);

    /// <summary>Everything the pad gives this frame. <paramref name="dt"/> is the seconds since the last call.</summary>
    public Step Update(in PadRaw now, double dt)
    {
        var dir = Held(now);
        var btn = Buttons(now);
        var edge = btn & ~_prevButtons;
        _prevButtons = btn;
        if (_settle)
        {
            if (dir == NavDir.None && btn == Btn.None) _settle = false;
            _dir = dir;
            return default;
        }
        var fire = NavDir.None;
        if (dir != _dir) { _dir = dir; _next = Delay; fire = dir; }
        else if (dir != NavDir.None && (_next -= dt) <= 0) { _next += Interval; if (_next < 0) _next = Interval; fire = dir; }
        return new Step(fire, edge);
    }

    /// <summary>The buttons still down do nothing until all are up.</summary>
    public void Settle() => _settle = true;
}
