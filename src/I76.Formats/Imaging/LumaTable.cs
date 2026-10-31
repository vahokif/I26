namespace I76.Formats.Imaging;

/// <summary>
/// The engine's vertex-light ramp (dumped at run time from the original game): from the mission's palette and its .lum (32
/// remap rows of 256 indices, row 0 the brightest), the brightest palette
/// entry among 0..254 by luma 0.3R + 0.59G + 0.11B (the first maximum), then
/// for each light level j = 0..31 the luma of that entry's remap in row 31 − j,
/// as Lmax·(L/Lmax)^1.4. Every primitive builder lights a vertex with
/// LUMA[round(31·light)]: light 0 is the floor (53 on a01, 123 on t01), never
/// black, and about 0.74 already saturates.
/// </summary>
public static class LumaTable
{
    public static byte[] Build(Palette palette, byte[] lum)
    {
        // x87 throughout with single-precision constants; only the maximum is kept as a float
        double Luma(int i)
        {
            var (r, g, b) = palette[i];
            return g * (double)0.59f + r * (double)0.3f + b * (double)0.11f;
        }
        int brightest = 0;
        float max = -1;
        for (int i = 0; i < 255; i++)
            if (Luma(i) > max) (brightest, max) = (i, (float)Luma(i));
        var table = new byte[32];
        for (int j = 0; j < 32; j++)
        {
            int at = (31 - j) * 256 + brightest;
            double l = at < lum.Length ? Luma(lum[at]) : max;
            float v = (float)(Math.Pow(l / max, 1.4) * max);
            table[j] = (byte)Math.Clamp((int)v, 0, 255);
        }
        return table;
    }

    /// <summary>The palette index the drop shadows are drawn in.</summary>
    public const int ShadowIndex = 0xfe;

    /// <summary>
    /// The drop shadows' colour, 0..1 per channel in the palette's own terms: the original draws them untextured in
    /// palette colour 0xfe with every vertex lit 0, so the plugin is handed that colour scaled by LUMA[0], whole
    /// bytes. t01_16: (16,16,16) at LUMA 123 is 7/255 (27 through the original game's γ 1.6, as measured); t17_16 (M01) at 53
    /// is 3/255 (19 measured at γ 1.7); a night palette's floor gives black (t06: (13,13,20) at 12, as M04 shows).
    /// </summary>
    public static (float R, float G, float B) ShadowColour(Palette palette, byte[] table)
    {
        var (r, g, b) = palette[ShadowIndex];
        float C(int c) => c * table[0] / 255 / 255f;
        return (C(r), C(g), C(b));
    }
}
