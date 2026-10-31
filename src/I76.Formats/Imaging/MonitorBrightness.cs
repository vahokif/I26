namespace I76.Formats.Imaging;

/// <summary>
/// Graphic Detail's Monitor Brightness (I76PLYR.DEF +0x4c, 1..10, 3 shipped) and the 3D card's display gamma
///
/// The palette curve: the original fills ten 256-byte tables (one per level, 256 bytes each) with
/// (byte)(255·(i/255)^(1/γ)), truncated by ftol, γ = 0.8, 0.9, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0, 2.2, 2.5 for levels 1..10;
/// It runs the palette through the level's table (0 or above 10: level 3, the identity).
/// The software renderer shows that palette, so on it the level is a display gamma.
///
/// The 3D card: ZGLIDE.DLL imports no gamma call (no grGammaCorrectionValue, guGammaCorrectionRGB or
/// grLoadGammaTable), so the screen shows the Glide driver's own ramp, 255·(i/255)^(1/γ) + 0.5, set in grSstWinOpen:
/// γ 1.3 from the Voodoo2 on (GLIDE_DEFAULT_GAMMA, what dgVoodoo's Glide ramp shows), 1.7 in the Voodoo Graphics
/// (SST-1) Glide 2.x. The port takes 1.3. ZDX5DRAW.DLL (Direct3D) sets none either: its frame is the palette colours
/// as they are.
/// </summary>
public static class MonitorBrightness
{
    /// <summary>The shipped level.</summary>
    public const int Default = 3;

    /// <summary>The Glide driver's default gamma, which the 3D card's frame is seen through.</summary>
    public const double GlideGamma = 1.3;

    /// <summary>The original's gammas, levels 1..10.</summary>
    private static readonly double[] Gammas = [0.8, 0.9, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0, 2.2, 2.5];

    /// <summary>The level the engine uses for <paramref name="level"/>: 0 or above 10 is 3.</summary>
    public static int Level(int level) => level is < 1 or > 10 ? Default : level;

    /// <summary>The palette curve's gamma for <paramref name="level"/> (1.0 at 3).</summary>
    public static double PaletteGamma(int level) => Gammas[Level(level) - 1];

    /// <summary>The level's palette table, as the original builds it.</summary>
    public static byte[] PaletteCurve(int level)
    {
        double k = 1.0 / PaletteGamma(level);
        var t = new byte[256];
        // + 1e-9: the run-time dump has level 3 as the identity, where doubles leave some i/255·255 a hair under i
        for (int i = 0; i < 256; i++) t[i] = (byte)(int)(Math.Pow(i / 255.0, k) * 255 + 1e-9);
        return t;
    }

    /// <summary>The Glide driver's default ramp (sst1InitGammaRGB at <see cref="GlideGamma"/>).</summary>
    public static byte[] GlideRamp()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)(int)(Math.Pow(i / 255.0, 1.0 / GlideGamma) * 255 + 0.5);
        return t;
    }

    /// <summary>
    /// The 1997 look's 3D card transfer curve: the level's palette curve, then the Glide ramp. Level 3 is the Glide
    /// ramp alone (the original's 3D card frame); the other levels add the software renderer's palette curve, which
    /// the original's 3D card path does not apply (docs/deviations.md).
    /// </summary>
    public static byte[] CardCurve(int level)
    {
        byte[] p = PaletteCurve(level), g = GlideRamp(), t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = g[p[i]];
        return t;
    }
}
