namespace I76.Net.Hud;

/// <summary>A rectangle in window pixels (the game layer turns it into Godot's).</summary>
public readonly record struct HudRect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
}

/// <summary>
/// Where the network game's readouts sit, as plain maths (tested without Godot). The 1997 look keeps the original's
/// windows at their 640x480 places (the console and the score window at (50, 150), the chat entry at (50, 360)),
/// scaled by the window's height and measured from the window's left edge, so on a wide
/// window they sit at the left as on the original's screen instead of inside a centred 4:3 box. The modern look scales
/// with the window's height against 1080 lines, inside a safe margin.
/// </summary>
public static class HudLayout
{
    /// <summary>The 1997 look's scale: window lines per 480 (never under a half).</summary>
    public static float ClassicScale(float h) => Math.Max(0.5f, h / 480f);

    /// <summary>The original's console window: (50, 150) of 640x480, 80 columns wide.</summary>
    public static HudRect ClassicConsole(float w, float h, int lines)
    {
        float k = ClassicScale(h);
        return new HudRect(50 * k, 150 * k, Math.Min(w - 50 * k, 640 * k), lines * ClassicLine * k);
    }

    /// <summary>The original's chat entry window: (50, 360) of 640x480, one line.</summary>
    public static HudRect ClassicEntry(float w, float h)
    {
        float k = ClassicScale(h);
        return new HudRect(50 * k, 360 * k, Math.Min(w - 50 * k, 640 * k), 1.3f * ClassicLine * k);
    }

    /// <summary>The original's score window: the console's place, nine lines; the port's pages run longer.</summary>
    public static HudRect ClassicBoard(float w, float h, int lines) => ClassicConsole(w, h, lines);

    /// <summary>The score window's tab stops in 640x480 pixels (16, 270, 350, 430), and the port's ping and status after them.</summary>
    public static readonly float[] ClassicTabs = [0, 16, 270, 350, 430, 500, 560];

    /// <summary>A console line's height at 480 lines, and the face's size.</summary>
    public const float ClassicLine = 20, ClassicFont = 18;

    /// <summary>The modern look's scale: window lines per 1080 (a 4K window draws twice as large, a 720p one two thirds).</summary>
    public static float ModernScale(float w, float h) => Math.Max(0.5f, Math.Min(h / 1080f, w / 1440f));

    /// <summary>
    /// The platform's safe area (desktop coordinates: on a window on a second monitor it starts at that monitor's origin) as the window sees it,
    /// taking the window's own position off; null where it does not sit on this window (another monitor's, or none reported).
    /// </summary>
    public static HudRect? PlatformSafe(HudRect? area, float windowX, float windowY, float w, float h)
    {
        if (area is not { } a || a.W <= 0 || a.W > w + 1) return null;
        var rel = new HudRect(a.X - windowX, a.Y - windowY, a.W, a.H);
        return rel.X < w * 0.25f && rel.Y < h * 0.25f && rel.Right > w * 0.75f && rel.Bottom > h * 0.75f ? rel : null;
    }

    /// <summary>The modern look's safe frame: 2.5% of the shorter side in from every edge, more if the platform reports a safe area further in.</summary>
    public static HudRect Safe(float w, float h, HudRect? platform = null)
    {
        float m = 0.025f * Math.Min(w, h);
        float x = m, y = m, r = w - m, b = h - m;
        if (platform is { } p)
        {
            x = Math.Max(x, p.X); y = Math.Max(y, p.Y);
            r = Math.Min(r, p.Right); b = Math.Min(b, p.Bottom);
        }
        return new HudRect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    /// <summary>
    /// The 2D instruments that hang from the top of the frame, in window pixels (the
    /// 640x480 frame scaled with the window's height and centred): the radar (0,0,174x119), the weapons housing
    /// (190,0,183x127) and the damage panel (392,0,241x123); with <paramref name="mirror"/> (the 2D dash only) the
    /// rear-view mirror too, (384,125,256x64). The bottom ones (speedometer, gear, specials, compass) are not the top
    /// columns' business. With <paramref name="dash3d"/> (the 3D cockpit, whose instruments are the car's model) the same places
    /// as measured on its default dash: the radar's bezel reaches 31 units further left, and the mirror sits at (463,138,225x72).
    /// </summary>
    public static HudRect[] TopInstruments(float w, float h, bool mirror, bool dash3d = false)
    {
        float k = h / 480f, ox = (w - 640 * k) / 2;
        HudRect R(float x, float y, float rw, float rh) => new(ox + x * k, y * k, rw * k, rh * k);
        var r = new List<HudRect> { dash3d ? R(-31, 0, 205, 119) : R(0, 0, 174, 119), R(190, 0, 183, 127), R(392, 0, 241, 123) };
        if (mirror) r.Add(dash3d ? R(463, 138, 225, 72) : R(384, 125, 256, 64));
        return [.. r];
    }

    /// <summary>
    /// Where a column spanning [<paramref name="x0"/>, <paramref name="x1"/>] may start: the safe frame's top, or just
    /// under the lowest of the <paramref name="blocks"/> that overlap it sideways (by <paramref name="gap"/>), so a
    /// column beside the instruments, or on a side with none drawn, goes right to the top.
    /// </summary>
    public static float ColumnTop(IEnumerable<HudRect> blocks, float x0, float x1, float safeTop, float gap)
    {
        float top = safeTop;
        foreach (var b in blocks)
            if (b.X < x1 && b.Right > x0) top = Math.Max(top, b.Bottom + gap);
        return top;
    }

    /// <summary>The lowest bottom edge of all <paramref name="blocks"/> (plus <paramref name="gap"/>), or the safe top when none: the centred prompt's place.</summary>
    public static float UnderAll(IEnumerable<HudRect> blocks, float safeTop, float gap) => ColumnTop(blocks, float.MinValue, float.MaxValue, safeTop, gap);

    /// <summary>Splits <paramref name="n"/> rows into columns of at most <paramref name="perColumn"/> (balanced), returning each column's count.</summary>
    public static int[] Columns(int n, int perColumn, int maxColumns = 2)
    {
        int cols = Math.Clamp((n + perColumn - 1) / Math.Max(1, perColumn), 1, maxColumns);
        var r = new int[cols];
        for (int i = 0; i < cols; i++) r[i] = n / cols + (i < n % cols ? 1 : 0);
        return r;
    }
}
