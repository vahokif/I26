using System.Numerics;

namespace I76.Editor.Roads;

/// <summary>
/// The height profile of a road. The engine puts a road on the ground (every road point's y is the terrain's height there at
/// load), so a road's height IS the terrain's: the editor makes the ground under the road follow a profile worth driving
/// (<see cref="RoadTerrain"/>). The profile starts from the ground along the centre line, is smoothed, and is held to a maximum grade
/// by moving points toward each other until no step is steeper than the limit; points that must stay (the ground at a road
/// being extended, the deck of a bridge) are pins that do not move.
/// </summary>
public static class RoadGrade
{
    /// <summary>
    /// Rise over run: the median stock road's steepest 5 % of steps climb 0.095 (per-road 95th percentile over 1372 roads: median 0.095,
    /// 75th percentile 0.19, 90th 0.33). 0.10 keeps a drivable road without flattening the hills.
    /// </summary>
    public const float DefaultMaxGrade = 0.10f;
    /// <summary>The ground's own grade is shown green up to this share of the limit, yellow up to the limit, red beyond.</summary>
    public const float YellowFrom = 0.6f;

    public sealed record Settings(float MaxGrade = DefaultMaxGrade, float SmoothMetres = 30f);

    /// <summary>
    /// The profile: one height (metres) per centre. <paramref name="ground"/> gives the terrain's height at a point; <paramref name="pins"/>
    /// fixes the heights of some points by index.
    /// </summary>
    public static float[] Profile(IReadOnlyList<Vector2> centres, Func<Vector2, float> ground, Settings? settings = null, IReadOnlyDictionary<int, float>? pins = null)
    {
        settings ??= new Settings();
        int n = centres.Count;
        var h = new float[n];
        for (int i = 0; i < n; i++) h[i] = pins is not null && pins.TryGetValue(i, out float pv) ? pv : ground(centres[i]);
        if (n < 2) return h;
        var ds = new float[n - 1];
        for (int i = 0; i < n - 1; i++) ds[i] = Math.Max(0.5f, Vector2.Distance(centres[i], centres[i + 1]));

        bool Pinned(int i) => pins is not null && pins.ContainsKey(i);
        // smooth: [1 2 1] passes, sigma = SmoothMetres / 2 over the mean step
        float step = ds.Average();
        double sigma = settings.SmoothMetres / 2 / step;
        int passes = (int)Math.Round(2 * sigma * sigma);
        var tmp = new float[n];
        for (int p = 0; p < passes; p++)
        {
            for (int i = 0; i < n; i++)
            {
                if (Pinned(i)) { tmp[i] = h[i]; continue; }
                float a = h[Math.Max(i - 1, 0)], c = h[Math.Min(i + 1, n - 1)];
                tmp[i] = (a + 2 * h[i] + c) / 4;
            }
            (h, tmp) = (tmp, h);
        }
        Limit(h, ds, settings.MaxGrade, Pinned);
        return h;
    }

    /// <summary>Moves neighbouring points together until no step climbs more than <paramref name="maxGrade"/> (free points share the excess; a pinned point does not move). Returns whether it settled.</summary>
    public static bool Limit(float[] h, float[] ds, float maxGrade, Func<int, bool> pinned)
    {
        int n = h.Length;
        for (int pass = 0; pass < 4000; pass++)
        {
            bool moved = false;
            for (int dir = 0; dir < 2; dir++)
                for (int k = 0; k < n - 1; k++)
                {
                    int i = dir == 0 ? k : n - 2 - k;
                    float dh = h[i + 1] - h[i], lim = maxGrade * ds[i];
                    float excess = Math.Abs(dh) - lim;
                    if (excess <= 1e-4f) continue;
                    float sign = Math.Sign(dh);
                    bool pa = pinned(i), pb = pinned(i + 1);
                    if (pa && pb) continue;
                    moved = true;
                    if (pa) h[i + 1] -= sign * excess;
                    else if (pb) h[i] += sign * excess;
                    else { h[i] += sign * excess / 2; h[i + 1] -= sign * excess / 2; }
                }
            if (!moved) return true;
        }
        return false;
    }

    /// <summary>Signed rise over run of each step (n - 1 values).</summary>
    public static float[] Grades(IReadOnlyList<Vector2> centres, IReadOnlyList<float> h)
    {
        var g = new float[Math.Max(0, centres.Count - 1)];
        for (int i = 0; i < g.Length; i++) g[i] = (h[i + 1] - h[i]) / Math.Max(0.5f, Vector2.Distance(centres[i], centres[i + 1]));
        return g;
    }

    public static float MaxAbs(IReadOnlyList<float> g) { float m = 0; foreach (float v in g) m = Math.Max(m, Math.Abs(v)); return m; }

    /// <summary>The ground's grade along the centre line, lightly averaged over three steps so one noisy sample does not flash red.</summary>
    public static float[] NaturalGrades(IReadOnlyList<Vector2> centres, Func<Vector2, float> ground)
    {
        var h = centres.Select(ground).ToArray();
        var g = Grades(centres, h);
        var o = new float[g.Length];
        for (int i = 0; i < g.Length; i++)
        {
            float s = 0; int c = 0;
            for (int k = Math.Max(0, i - 1); k <= Math.Min(g.Length - 1, i + 1); k++) { s += Math.Abs(g[k]); c++; }
            o[i] = s / c;
        }
        return o;
    }

    /// <summary>0 green, 1 yellow, 2 red for a grade against the limit.</summary>
    public static int Band(float grade, float maxGrade) => Math.Abs(grade) <= maxGrade * YellowFrom ? 0 : Math.Abs(grade) <= maxGrade ? 1 : 2;
}
