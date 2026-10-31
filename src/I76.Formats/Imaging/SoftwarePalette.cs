namespace I76.Formats.Imaging;

/// <summary>
/// The software renderer's colour: every pixel it writes is an index into the
/// mission's 8-bit palette, a texel run through one row of the mission's .lum, lum[row][texel]. A vertex lit
/// `light` (0..1) takes row (1 − light)·31.5 (the constant 8064 = 31.5·256 in 8.8 fixed point, the original's terrain leaf faces
/// the shaded span routines), interpolated across the face: row 0 the brightest,
/// row 7 the identity, rows 0..6 the texel mixed toward white, rows 8..31 darkened toward black, each entry the
/// palette colour nearest the shaded one.
///
/// The port draws RGB, so the look is rebuilt in two parts: <see cref="Ramp"/> fits each row as
/// colour·a + b (shared over the channels), which the 1997 shaders apply in place of the 3D card's grey ramp, and
/// <see cref="BuildLut"/> maps any colour to its nearest palette entry for the final pass.
/// </summary>
public static class SoftwarePalette
{
    /// <summary>The .lum row a vertex lit <paramref name="light"/> takes: (int)((1 − light)·31.5), 0..31.</summary>
    public static int Row(float light) => Math.Clamp((int)((1f - Math.Clamp(light, 0f, 1f)) * 31.5f), 0, 31);

    /// <summary>
    /// For light level j = 0..31 (light j/31, the index the 1997 shaders already use for the 3D card's ramp), the
    /// least-squares (a, b) with pal[lum[row][c]] ≈ a·pal[c] + b over every entry c, row = <see cref="Row"/>(j/31),
    /// both 0..1 (b in units of 255). Row 7, the identity, gives (1, 0).
    /// </summary>
    public static (float A, float B)[] Ramp(Palette palette, byte[] lum)
    {
        var ramp = new (float, float)[32];
        for (int j = 0; j < 32; j++)
        {
            int row = Row(j / 31f);
            // minimise Σ (a·x + b − y)² over the 768 channel values
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int n = 0;
            for (int c = 0; c < 256; c++)
            {
                int at = row * 256 + c;
                int to = at < lum.Length ? lum[at] : c;
                for (int k = 0; k < 3; k++)
                {
                    double x = palette.Rgb[c * 3 + k], y = palette.Rgb[to * 3 + k];
                    sx += x; sy += y; sxx += x * x; sxy += x * y; n++;
                }
            }
            double den = n * sxx - sx * sx;
            double a = den == 0 ? 1 : (n * sxy - sx * sy) / den;
            double b = (sy - a * sx) / n;
            ramp[j] = ((float)Math.Clamp(a, 0, 1), (float)Math.Clamp(b / 255, 0, 1));
        }
        return ramp;
    }

    /// <summary>The palette index the 1997 software sky's horizon band is drawn in (0xef, the last of the 8-bit sky entries).</summary>
    public const int SkyIndex = 0xef;

    /// <summary>
    /// The 1997 CD build's horizon band (drawn in mode 9):
    /// for row r = 0..31, palette[lum[r][0xef]]. The band is Gouraud-shaded in the row: row 0 at its foot, row 31 at its
    /// top; on every stock palette rows 0..6 step through the brighter sky entries 230..238 and rows 7..31 are 0xef itself.
    /// </summary>
    public static (byte R, byte G, byte B)[] SkyGradient(Palette palette, byte[] lum)
    {
        var o = new (byte, byte, byte)[32];
        for (int r = 0; r < 32; r++)
        {
            int at = r * 256 + SkyIndex;
            o[r] = palette[at < lum.Length ? lum[at] : SkyIndex];
        }
        return o;
    }

    /// <summary>
    /// The band's row at a view direction. <paramref name="s"/> is the direction's height over
    /// its view depth (y / depth, linear across the screen); the band's foot is the line where the lowest drawn ground meets
    /// the far clip (<paramref name="foot"/> = (lowest − eye height) / far) and its top where a plane 130 above the eye
    /// (50 + 80) meets it (<paramref name="top"/> = 130 / far). Each vertex carries t = 1 at the foot, 0 at the top,
    /// interpolated, and the span takes row (int)((1 − t)·31) (7936 = 31·256, 16.16 fixed point). -1: above the band.
    /// Below the foot it stays row 0.
    /// </summary>
    public static int GradientRow(float s, float foot, float top)
    {
        if (s > top) return -1;
        float t = top > foot ? Math.Clamp((top - s) / (top - foot), 0f, 1f) : 1f;
        return Math.Clamp((int)((1f - t) * 31f), 0, 31);
    }

    /// <summary>The band's top above the eye: the sky plane's 50 plus 80.</summary>
    public const float GradientTop = 130f;

    /// <summary>
    /// The software renderer's drop shadow: a remap of the pixel under it, built per palette entry by the 1997 build
    /// (drawn by mode 0xc0 from the shadow faces) and by the GOG build
    /// (also mode 0xc0): the colour to HSV, its
    /// value v stepped 16 times by min(0.35, 0.95 − v)/16 and then lowered by 0.6, so v − 0.25 up to v = 0.6 and 0.35 above
    /// it, at least 0; hue and saturation kept, so the colour scales by v'/v. Unrounded; the table then
    /// takes the nearest palette entry (<see cref="ShadowTable"/>). The 3D card drew the faces black instead.
    /// </summary>
    public static (float R, float G, float B) Shadow(float r, float g, float b)
    {
        float v = Math.Max(r, Math.Max(g, b));
        if (v <= 1e-4f) return (0, 0, 0);
        float w = Math.Max(ShadowValue(v), 0f) / v;
        return (r * w, g * w, b * w);
    }

    /// <summary>The value a colour of value <paramref name="v"/> (0..1) takes in shadow: v − 0.25 to 0.6, then 0.35; at least 0.</summary>
    public static float ShadowValue(float v) => Math.Max(v + 0.35f > 0.95f ? 0.35f : v - 0.25f, 0f);

    /// <summary>The software renderer's shadow table: each entry's <see cref="Shadow"/> colour as its nearest palette entry.</summary>
    public static byte[] ShadowTable(Palette palette)
    {
        var o = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = palette[i];
            var (sr, sg, sb) = Shadow(r / 255f, g / 255f, b / 255f);
            o[i] = (byte)Nearest(palette, (int)MathF.Round(sr * 255), (int)MathF.Round(sg * 255), (int)MathF.Round(sb * 255));
        }
        return o;
    }

    /// <summary>
    /// The 1997 CD build's radar blip colour for a contact's colour byte (the 1997 build draws
    /// a table indexed by the byte, which it fills): entry i is the palette colour nearest
    /// (0.388 − 0.00152·i, 0.776 − 0.00217·i, 0.388 − 0.00152·i), a green that darkens as the byte climbs, and entry 255 the
    /// nearest yellow. The byte runs as in the later build (0 at the sweep, +3 a second to 0x69), so every blip is green,
    /// bright as the sweep passes. The GOG build writes the byte itself, an index into the mission palette.
    /// </summary>
    public static (float R, float G, float B) RadarGreen(int i) => i >= 255
        ? (1f, 1f, 0f)
        : (0.3882353f - 0.0015224913f * i, 0.7764706f - 0.0021683967f * i, 0.3882353f - 0.0015224913f * i);

    /// <summary><see cref="RadarGreen"/> as the palette entry the 1997 table holds (nearest in RGB).</summary>
    public static int RadarBlip(Palette palette, int i)
    {
        var (r, g, b) = RadarGreen(i);
        return Nearest(palette, (int)MathF.Round(r * 255), (int)MathF.Round(g * 255), (int)MathF.Round(b * 255));
    }

    /// <summary>The palette entry nearest (r, g, b), squared RGB distance, the lowest index on a tie.</summary>
    public static int Nearest(Palette palette, int r, int g, int b)
    {
        int best = 0, bestD = int.MaxValue;
        var p = palette.Rgb;
        for (int i = 0; i < 256; i++)
        {
            int dr = p[i * 3] - r, dg = p[i * 3 + 1] - g, db = p[i * 3 + 2] - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) (best, bestD) = (i, d);
        }
        return best;
    }

    /// <summary>
    /// An n×n×n lookup of the nearest palette colour, RGB8, laid out as a square texture of n×n tiles: tile
    /// t = blue (left to right, then down, tilesPerRow = √n per row), inside it x = red, y = green. Cell i on an
    /// axis stands for the value round(255·i/(n−1)). n must be a perfect square (16, 64).
    /// </summary>
    public static byte[] BuildLut(Palette palette, int n, out int side)
    {
        int tiles = (int)Math.Round(Math.Sqrt(n));
        if (tiles * tiles != n) throw new ArgumentException("n must be a perfect square", nameof(n));
        side = n * tiles;
        var o = new byte[side * side * 3];
        int s = side;
        var p = palette.Rgb;
        Parallel.For(0, n, bi =>
        {
            int b = (int)Math.Round(255.0 * bi / (n - 1));
            int tx = bi % tiles * n, ty = bi / tiles * n;
            for (int gi = 0; gi < n; gi++)
            {
                int g = (int)Math.Round(255.0 * gi / (n - 1));
                for (int ri = 0; ri < n; ri++)
                {
                    int r = (int)Math.Round(255.0 * ri / (n - 1));
                    int i = Nearest(palette, r, g, b);
                    int at = ((ty + gi) * s + tx + ri) * 3;
                    o[at] = p[i * 3]; o[at + 1] = p[i * 3 + 1]; o[at + 2] = p[i * 3 + 2];
                }
            }
        });
        return o;
    }
}
