using System;
using System.Collections.Generic;
using System.Globalization;

namespace I76.Game.Boot;

/// <summary>
/// Graphic Detail's Resolution: the window's size. "Desktop" is the game as it starts (fullscreen at the desktop's size);
/// any other choice is a window of that size, centred, applied at once. Pure: the sizes offered and the shell.cfg text; the
/// engine calls are in <see cref="WindowApplier"/>.
/// </summary>
public static class WindowSizes
{
    /// <summary>Common window sizes, smallest first.</summary>
    public static readonly (int W, int H)[] Common =
    [
        (800, 600), (1024, 768), (1280, 720), (1280, 960), (1366, 768), (1600, 900), (1920, 1080), (2560, 1080), (2560, 1440), (3440, 1440), (3840, 2160),
    ];

    /// <summary>The window sizes offered on a desktop of this size: those that fit within it and are smaller than it (the desktop's own is the Desktop choice).</summary>
    public static List<(int W, int H)> Offered(int desktopW, int desktopH)
    {
        var l = new List<(int W, int H)>();
        foreach (var c in Common) if (c.W <= desktopW && c.H <= desktopH && (c.W != desktopW || c.H != desktopH)) l.Add(c);
        return l;
    }

    /// <summary>The shell.cfg [port] resolution text ("1920x1080").</summary>
    public static string Format(int w, int h) => $"{w}x{h}";

    /// <summary>A shell.cfg value back to a size: null (the desktop) for an empty, malformed or implausible value (under 320x200, over 16384).</summary>
    public static (int W, int H)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Trim().ToLowerInvariant().Split('x');
        if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)) return null;
        return w is >= 320 and <= 16384 && h is >= 200 and <= 16384 ? (w, h) : null;
    }

    /// <summary>The choice after the current one: the desktop, then each size offered, then the desktop again. A size not offered here (a saved one from another screen) goes on to the next larger.</summary>
    public static (int W, int H)? Next((int W, int H)? now, int desktopW, int desktopH)
    {
        var o = Offered(desktopW, desktopH);
        if (now is null) return o.Count > 0 ? o[0] : null;
        int i = o.IndexOf(now.Value);
        if (i >= 0) return i + 1 < o.Count ? o[i + 1] : null;
        foreach (var c in o) if (c.W * c.H > now.Value.W * now.Value.H) return c;
        return null;
    }

    /// <summary>The label of a choice: "Desktop" or "1920x1080".</summary>
    public static string Label((int W, int H)? size) => size is { } s ? Format(s.W, s.H) : "Desktop";

    /// <summary>A size held to the screen it opens on.</summary>
    public static (int W, int H) Fit((int W, int H) size, int screenW, int screenH) => (Math.Min(size.W, Math.Max(screenW, 320)), Math.Min(size.H, Math.Max(screenH, 200)));
}
