namespace I76.Formats.Vegetation;

/// <summary>
/// Where a plant's detail tiers cross-fade (docs/modern-vegetation.md): around each tier boundary (the middle tier's
/// start, the far tier's start) lies a band, a few metres either side, in which a plant is drawn in both tiers with
/// complementary dithers (vegetation.gdshader), so the swap never pops. This is which tiers a plant at a distance
/// belongs to; the shader works out how much of each.
/// </summary>
public static class TierBands
{
    public const int Full = 1, Middle = 2, Far = 4;

    /// <summary>The band's half width (metres) about a boundary: it grows with distance, where a swap is smaller on the screen.</summary>
    public static float HalfWidth(float boundary) => MathF.Max(1.5f, 0.2f * boundary);

    /// <summary>The band about a boundary: where the nearer tier is fully drawn up to, and the farther one from.</summary>
    public static (float Start, float End) Band(float boundary) => (boundary - HalfWidth(boundary), boundary + HalfWidth(boundary));

    /// <summary>
    /// The tiers (flags) that draw a plant at ground distance <paramref name="d"/> from the eye, given where the middle and
    /// far tiers begin; <paramref name="slack"/> widens each tier's reach (metres: how far the eye may have moved since
    /// the plants were sorted).
    /// </summary>
    public static int Tiers(float d, float mid, float far, float slack = 0)
    {
        var (m0, m1) = Band(mid);
        var (f0, f1) = Band(far);
        int t = 0;
        if (d < m1 + slack) t |= Full;
        if (d >= m0 - slack && d < f1 + slack) t |= Middle;
        if (d >= f0 - slack) t |= Far;
        return t;
    }

    /// <summary>The tiers any plant lying between <paramref name="near"/> and <paramref name="farthest"/> from the eye needs (a chunk's nearest and farthest distances).</summary>
    public static int TiersBetween(float near, float farthest, float mid, float far)
    {
        var (m0, m1) = Band(mid);
        var (f0, f1) = Band(far);
        int t = 0;
        if (near < m1) t |= Full;
        if (farthest >= m0 && near < f1) t |= Middle;
        if (farthest >= f0) t |= Far;
        return t;
    }

    /// <summary>How far into the nearer tier's fade out a plant at <paramref name="d"/> is about a boundary: 0 before the band, 1 after (the shader's smoothstep).</summary>
    public static float Progress(float d, float boundary)
    {
        var (a, b) = Band(boundary);
        float x = Math.Clamp((d - a) / (b - a), 0f, 1f);
        return x * x * (3 - 2 * x);
    }
}
