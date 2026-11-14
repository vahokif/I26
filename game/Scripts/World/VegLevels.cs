namespace I76.Game.World;

/// <summary>
/// Graphic Detail's Vegetation, Low and High (docs/modern-vegetation.md): the density, where each kind of plant fades in
/// and out (the shrubs; the ground layer's larger plants, its forbs, weeds and rosettes; its grass tufts), where a plant
/// changes to its middle and its far mesh (per plant: tier 1 and 2), how near shrubs cast sun shadows, and where the shade
/// under the plants fades out. The tests read it.
/// </summary>
public static class VegLevels
{
    public sealed record Level(float Density, float ShrubFade, float ShrubEnd, float ForbFade, float ForbEnd, float GrassFade, float GrassEnd,
        float ShadowRange, float ShrubMid, float ShrubFar, float GroundMid, float GroundFar, float ShadeFade, float ShadeEnd)
    {
        /// <summary>How far the ground layer's chunks are wanted: the farther of its two kinds' ends.</summary>
        public float GroundEnd => MathF.Max(ForbEnd, GrassEnd);
    }

    /// <summary>
    /// How far past the headlamps' visible reach (from the lamps, <see cref="LampReach"/>) a bush may first be fully drawn
    /// (metres): the chase view's camera sits up to about this far behind the lamps at its usual zoom, so what fades in beyond
    /// the reach plus this does so in the dark, never in the beam.
    /// </summary>
    public const float LampMargin = 20f;

    /// <summary>The nearest a bush (a shrub, a forb, a rolling tumbleweed) may be less than fully drawn, for the lamps' visible reach (metres from the camera).</summary>
    public static float BushAppear(float lampReach) => lampReach + LampMargin;

    /// <summary>
    /// The share of the beam's light just ahead of the hood (<see cref="Near"/> metres from the lamp) below which a plant facing
    /// the lamps no longer shows lit at night: 7 % (the modern look's beam ends visibly at about 90 m by it).
    /// </summary>
    public const float VisibleShare = 0.07f, Near = 8f;

    /// <summary>
    /// How far a beam of <paramref name="range"/> metres under the distance fall-off <paramref name="attenuation"/> visibly lights
    /// what stands by the road (metres from the lamp): where its light drops to <see cref="VisibleShare"/> of its light at Near.
    /// </summary>
    public static float VisibleReach(float range, float attenuation)
    {
        float want = VisibleShare * Falloff(Near, range, attenuation), lo = Near, hi = range;
        for (int i = 0; i < 40; i++)
        {
            float m = 0.5f * (lo + hi);
            if (Falloff(m, range, attenuation) > want) lo = m; else hi = m;
        }
        return lo;
    }

    /// <summary>Godot's distance fall-off of an omni or spot light (the window (1 - (d/r)^4)^2 times d^-attenuation).</summary>
    public static float Falloff(float d, float range, float attenuation)
    {
        float nd = d / range;
        nd *= nd;
        nd *= nd;
        float w = MathF.Max(1f - nd, 0f);
        return w * w * MathF.Pow(MathF.Max(d, 0.0001f), -attenuation);
    }

    /// <summary>The modern look's headlamps' visible reach (CarNode's beam): about 90 m.</summary>
    public static float LampReach => VisibleReach(I76.Game.Sim.CarNode.HeadRange, I76.Game.Sim.CarNode.HeadAttenuation);

    // The levels' own reaches, the nearest they go: Low's shrubs and the ground layer's larger plants fade over 130-170 m
    // (the shrubs 120-160 m before, the larger ground plants 35-50 m: in the beam), High's over 190-250 m; a longer lamp
    // reach would push them out (For); the grass tufts keep 35-50 m (60-85 m) and grow in out of the sward as they fade in.
    public static readonly Level Low = new(0.6f, 130, 170, 130, 170, 35, 50, 0, 20, 50, 9, 22, 35, 50);
    public static readonly Level High = new(1f, 190, 250, 190, 250, 60, 85, 60, 30, 70, 12, 35, 55, 75);

    /// <summary>
    /// The Low (1) or High (2) level for the lamps' visible reach: each bush kind's fade moved out, band and all, until it starts
    /// at <see cref="BushAppear"/> (no nearer than the level's own). At night a shrub's far mesh (a quarter of its clusters) filled
    /// in to its middle one at 40-60 m in the beam: there no shrub draws its far mesh (no far band at all: one near the reach's end
    /// split the chunks there plant by plant every few metres).
    /// </summary>
    public static Level For(int level, float lampReach, bool night = false)
    {
        var l = level >= 2 ? High : Low;
        float appear = BushAppear(lampReach);
        float s = MathF.Max(0, appear - l.ShrubFade), f = MathF.Max(0, appear - l.ForbFade);
        l = l with { ShrubFade = l.ShrubFade + s, ShrubEnd = l.ShrubEnd + s, ForbFade = l.ForbFade + f, ForbEnd = l.ForbEnd + f };
        if (night) l = l with { ShrubFar = NoFar };
        return l;
    }

    /// <summary>A tier boundary no plant reaches.</summary>
    public const float NoFar = 1e5f;

    /// <summary>The tier boundary whose cross-fade band (TierBands) begins at <paramref name="start"/>.</summary>
    public static float FarFrom(float start)
    {
        float b = start;
        for (int i = 0; i < 8; i++) b = start + I76.Formats.Vegetation.TierBands.HalfWidth(b);
        return b;
    }

    /// <summary>The rolling tumbleweeds' range for a level and the lamps' visible reach: theirs, or far enough that they fade in beyond the lamps.</summary>
    public static float TumbleRange(int level, float lampReach) =>
        MathF.Max(I76.Formats.Vegetation.TumbleweedField.RangeFor(level), BushAppear(lampReach) + 45f);
}
