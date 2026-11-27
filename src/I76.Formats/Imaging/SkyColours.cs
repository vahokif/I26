using System;
using System.Linq;
using System.Numerics;

namespace I76.Formats.Imaging;

/// <summary>
/// The modern sky's colours measured from a mission's sky texture (MissionBuilder): its open sky, its clouds and the
/// shade between, and how cloudy a texel is. Sky and cloud are told apart by colour, not brightness: the texture's pixels
/// fall in two groups, and the sky is the bluer one (blue over red), which holds for a day's blue over white clouds, a
/// night's navy under violet ones and a sunset's blue behind rust-red clouds darker than it (where the darker group, the
/// rule before, is the clouds). A texel's cloudiness is its place along the sky-to-cloud colour line, so clouds darker
/// than the sky are clouds too. Colours are linear RGB.
/// </summary>
public static class SkyColours
{
    /// <summary>
    /// The open sky's colour (the third of the pixels furthest toward it), the clouds' lit colour (the brightest third of
    /// the cloudiest third: for white clouds about their brightest, for clouds darker than the sky their lit tops and not
    /// their dark cores), the shade between (from half to seven tenths of the way), and the cloud end of the sky-to-cloud
    /// line (the sixth furthest toward the clouds), all linear.
    /// </summary>
    public readonly record struct Measured(Vector3 Sky, Vector3 Cloud, Vector3 Shade, Vector3 CloudEnd);

    /// <summary>Measures linear RGB pixels (three floats each).</summary>
    public static Measured Measure(ReadOnlySpan<float> rgb)
    {
        int n = rgb.Length / 3;
        if (n == 0) return new(new(0.2f, 0.3f, 0.6f), Vector3.One, new(0.6f), Vector3.One);
        var px = new Vector3[n];
        for (int i = 0; i < n; i++) px[i] = new(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
        static float Lum(Vector3 c) => 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;

        // two groups (k-means from the darkest third and the brightest sixth, the old guess)
        var byLum = px.OrderBy(Lum).ToArray();
        Vector3 a = Mean(byLum, 0, 0.33), b = Mean(byLum, 0.84, 1);
        for (int it = 0; it < 12; it++)
        {
            Vector3 sa = default, sb = default;
            int na = 0, nb = 0;
            foreach (var p in px)
                if (Vector3.DistanceSquared(p, a) <= Vector3.DistanceSquared(p, b)) { sa += p; na++; }
                else { sb += p; nb++; }
            if (na == 0 || nb == 0) break;
            a = sa / na; b = sb / nb;
        }
        // the sky is the bluer group; where the two are about as blue (grey clouds on a grey sky), the darker
        static float Blue(Vector3 c) => (c.Z - c.X) / MathF.Max(c.X + c.Y + c.Z, 1e-4f);
        bool aSky = MathF.Abs(Blue(a) - Blue(b)) > 0.03f ? Blue(a) > Blue(b) : Lum(a) <= Lum(b);
        Vector3 skyC = aSky ? a : b, cloudC = aSky ? b : a;
        var axis = cloudC - skyC;
        if (axis.LengthSquared() < 1e-6f) axis = new Vector3(0.299f, 0.587f, 0.114f);

        // the pixels along the sky-to-cloud line: the sky its first third, the clouds its last sixth
        var along = px.OrderBy(p => Vector3.Dot(p - skyC, axis)).ToArray();
        // the clouds' lit colour: the brightest third of the cloudiest third (not the pixels half sky, half cloud)
        var lit = along[(along.Length * 2 / 3)..].OrderBy(Lum).ToArray();
        return new(Mean(along, 0, 0.33), Mean(lit, 0.67, 1), Mean(along, 0.5, 0.7), Mean(along, 0.84, 1));

        static Vector3 Mean(Vector3[] sorted, double from, double to)
        {
            int s = (int)(sorted.Length * from), e = Math.Max(s + 1, (int)(sorted.Length * to));
            Vector3 sum = default;
            for (int i = s; i < e; i++) sum += sorted[i];
            return sum / (e - s);
        }
    }

    /// <summary>
    /// How far a colour is from the sky toward the clouds along the line between them (0 at <paramref name="sky"/>, 1 at
    /// <paramref name="cloud"/>), the flat sky's measure of cloud (sky_modern.gdshaderinc's density does the same).
    /// </summary>
    public static float Along(Vector3 c, Vector3 sky, Vector3 cloud)
    {
        var axis = cloud - sky;
        return Vector3.Dot(c - sky, axis) / MathF.Max(axis.LengthSquared(), 1e-6f);
    }

    /// <summary>The flat sky's ramp of <see cref="Along"/> into cover (before its noise): from a little short of the sky to a
    /// little past the clouds, eased.</summary>
    public static float Cover(Vector3 c, Vector3 sky, Vector3 cloud)
    {
        float t = Math.Clamp((Along(c, sky, cloud) + 0.1f) / 1.25f, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
