using System;
using System.Collections.Generic;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// Names for the port's own rows of the WEAPONS RESTRICTIONS popup, set in the art's own face: DATABASE.MW2 bank 30 sprite 43
/// prints its labels in an antialiased bold Arial-like 12 px (no shell font record has it: the form fonts are 10 to 15 px,
/// all but one unantialiased), so each character is cut out of the art's own words and laid over the parchment.
/// The table holds coordinates only, measured from bank 30 sprite 43: the source column, the top of its 14-row band (one above the caps), its
/// width, where the cut starts against the pen it was printed at (tenths of a pixel) and the pen's advance (hundredths; the art
/// spaces its words by fractional advances). The pixels are read from the art at run time.
/// </summary>
public static class RestrictionLabels
{
    private const int Rows = 14;
    private const byte InkIndex = 225; // the art's solid ink (a checkbox's edge)
    private const float Parchment = 180f, InkLum = 43f; // brightness of the art's paper and of its ink
    private const int SpaceAdv = 333;

    // char, source x, band top y, width, offset from its pen (tenths), advance (hundredths of a pixel)
    private static readonly (char C, int X, int Y, int W, int Dx, int Adv)[] Cuts =
    [
 ('\'', 147, 259, 3, 3, 285),
        ('-', 137, 114, 4, 0, 400),
        ('.', 432, 99, 2, 6, 333),
        ('0', 405, 99, 6, 3, 667),
        ('2', 414, 131, 6, 2, 667),
        ('3', 398, 99, 7, 0, 667),
        ('4', 115, 146, 7, -1, 667),
        ('5', 398, 115, 7, 0, 667),
        ('6', 407, 131, 7, -1, 667),
        ('7', 397, 131, 7, -1, 667),
        ('A', 115, 114, 9, 0, 867),
        ('B', 114, 259, 9, 0, 867),
        ('C', 86, 371, 9, 0, 867),
        ('D', 167, 98, 8, 4, 867),
        ('E', 376, 226, 8, 3, 800),
        ('F', 116, 98, 7, 7, 733),
        ('G', 175, 130, 9, 6, 933),
        ('H', 171, 114, 8, 7, 867),
        ('I', 124, 114, 3, 3, 333),
        ('K', 386, 275, 8, 8, 867),
        ('L', 114, 210, 7, 0, 733),
        ('M', 127, 114, 10, 0, 1000),
        ('N', 141, 114, 8, 0, 867),
        ('O', 85, 307, 10, -3, 933),
        ('P', 403, 243, 7, 5, 800),
        ('R', 141, 98, 9, 4, 867),
        ('S', 179, 114, 8, 0, 800),
        ('T', 199, 243, 8, -6, 733),
        ('U', 146, 146, 8, 2, 867),
        ('W', 366, 243, 12, -5, 1133),
        ('Z', 374, 275, 8, -5, 733),
        ('a', 151, 130, 7, -1, 667),
        ('b', 439, 259, 7, -1, 733),
        ('c', 202, 98, 7, 0, 667),
        ('d', 144, 130, 7, 2, 733),
        ('e', 131, 98, 6, 4, 667),
        ('g', 125, 210, 7, 3, 733),
        ('h', 132, 210, 7, 0, 733),
        ('i', 123, 98, 3, 4, 333),
        ('k', 126, 307, 7, 0, 667),
        ('l', 221, 114, 2, 7, 333),
        ('m', 164, 210, 10, 0, 1067),
        ('n', 160, 114, 7, 3, 733),
        ('o', 195, 98, 7, 4, 733),
        ('p', 133, 339, 7, 3, 733),
        ('r', 126, 98, 5, 0, 467),
        ('s', 203, 114, 7, -7, 667),
        ('t', 152, 98, 5, -6, 400),
        ('u', 154, 227, 7, 0, 733),
        ('v', 136, 243, 7, -2, 667),
        ('w', 204, 210, 10, -7, 933),
        ('x', 105, 339, 7, -3, 667),
        ('y', 142, 243, 8, -9, 667)
    ];

    // Characters the art never prints, and the closest it does (Rot: turned half a turn, Mirror: flipped across)
    private static readonly Dictionary<char, (char From, bool Rot, bool Mirror)> Fallback = new()
    {
        ['1'] = ('I', false, false), ['8'] = ('B', false, false), ['9'] = ('6', true, false),
        ['j'] = ('i', false, false), ['q'] = ('p', false, true), ['z'] = ('Z', false, false),
        ['J'] = ('I', false, false), ['Q'] = ('O', false, false), ['V'] = ('U', false, false), ['X'] = ('K', false, false), ['Y'] = ('T', false, false),
        [','] = ('.', false, false), [':'] = ('.', false, false), [';'] = ('.', false, false), ['!'] = ('I', false, false), ['?'] = ('.', false, false),
        ['/'] = ('I', false, false), ['('] = ('I', false, false), [')'] = ('I', false, false), ['&'] = ('B', false, false),
        ['+'] = ('-', false, false), ['_'] = ('-', false, false),
    };

    // Lower-case letters the art never prints, made from its capital squeezed to the x-height (caps rows 1..9 of the band
    // into rows 3..9, as its 'x' and 'e' sit), with a lower-case advance: "Howitzer"
    private static readonly Dictionary<char, (char From, int Adv)> Small = new() { ['z'] = ('Z', 667) };

    private static readonly Dictionary<char, (int X, int Y, int W, int Dx, int Adv)> ByChar = BuildTable();

    private static Dictionary<char, (int X, int Y, int W, int Dx, int Adv)> BuildTable()
    {
        var d = new Dictionary<char, (int X, int Y, int W, int Dx, int Adv)>();
        foreach (var c in Cuts) d[c.C] = (c.X, c.Y, c.W, c.Dx, c.Adv);
        return d;
    }

    /// <summary>The pen's advance for a character, in pixels.</summary>
    public static float Advance(char c)
    {
        if (ByChar.TryGetValue(c, out var g)) return g.Adv / 100f;
        if (Small.TryGetValue(c, out var sm)) return sm.Adv / 100f;
        return Fallback.TryGetValue(c, out var f) && ByChar.TryGetValue(f.From, out var h) ? h.Adv / 100f : SpaceAdv / 100f;
    }

    /// <summary>
    /// Draws <paramref name="s"/> with its pen at art column <paramref name="penX"/> and the caps' top row at art row
    /// <paramref name="capTop"/> (the caps' top row), into the frame where the art's top-left is
    /// (ox, oy). Each pixel is the art's own ink blended over the art's paper at the place it lands.
    /// </summary>
    public static void Draw(ShellFrame frame, Sprite art, int ox, int oy, float penX, int capTop, string s)
    {
        var pal = frame.Palette;
        var cache = new Dictionary<(byte, int), byte>();
        float pen = penX;
        foreach (char ch in s)
        {
            bool rot = false, mirror = false, small = false;
            char src = ch;
            if (!ByChar.ContainsKey(ch) && Small.TryGetValue(ch, out var sm)) { src = sm.From; small = true; }
            else if (!ByChar.ContainsKey(ch) && Fallback.TryGetValue(ch, out var f)) { src = f.From; rot = f.Rot; mirror = f.Mirror; }
            if (ByChar.TryGetValue(src, out var g))
            {
                int left = (int)MathF.Round(pen + g.Dx / 10f);
                for (int r = 0; r < Rows; r++)
                {
                    int sr = rot ? 12 - r : r; // a half turn is about the caps' rows (2..10)
                    if (small) { if (r < 3 || r > 9) continue; sr = 1 + (int)MathF.Round((r - 3) * 8 / 6f); }
                    int ya = capTop - 1 + r;
                    if (sr < 0 || sr >= Rows || ya < 0 || ya >= art.Height) continue;
                    for (int c = 0; c < g.W; c++)
                    {
                        int sc = rot || mirror ? g.W - 1 - c : c;
                        int xa = left + c;
                        if (xa < 0 || xa >= art.Width) continue;
                        byte sp = art.Pixels[(g.Y + sr) * art.Width + g.X + sc];
                        float l = (pal[sp * 3] + pal[sp * 3 + 1] + pal[sp * 3 + 2]) / 3f;
                        float a = Math.Clamp((Parchment - l) / (Parchment - InkLum), 0f, 1f);
                        if (a < 0.1f) continue;
                        frame.Fill(ox + xa, oy + ya, 1, 1, Blend(art.Pixels[ya * art.Width + xa], a, pal, cache));
                    }
                }
            }
            pen += Advance(ch);
        }
    }

    private static byte Blend(byte under, float a, byte[] pal, Dictionary<(byte, int), byte> cache)
    {
        int q = (int)MathF.Round(a * 16);
        if (q >= 16) return InkIndex;
        if (cache.TryGetValue((under, q), out var hit)) return hit;
        float t = q / 16f;
        float r = pal[under * 3] * (1 - t) + pal[InkIndex * 3] * t, g = pal[under * 3 + 1] * (1 - t) + pal[InkIndex * 3 + 1] * t, b = pal[under * 3 + 2] * (1 - t) + pal[InkIndex * 3 + 2] * t;
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            float dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b, dd = dr * dr + dg * dg + db * db;
            if (dd < bd) { bd = dd; best = i; }
        }
        return cache[(under, q)] = (byte)best;
    }
}
