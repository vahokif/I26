namespace I76.Formats.Imaging;

/// <summary>
/// The respray colour math: a paint mask by colour clustering, and a recolour that keeps the shading.
/// Everything here works on RGB8 texels, which is what the port draws from (the 16-bit .m16 textures); the 8-bit palette
/// look is a nearest-palette pass over the result.
/// </summary>
public static class ResprayPaint
{
    public readonly record struct Hsv(float H, float S, float V);

    public static Hsv ToHsv(float r, float g, float b)
    {
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b)), c = mx - mn;
        float h = c <= 1e-5f ? 0 : mx == r ? 60 * (((g - b) / c + 6) % 6) : mx == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        return new Hsv(h, mx <= 1e-5f ? 0 : c / mx, mx);
    }

    public static (float R, float G, float B) FromHsv(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360;
        float c = v * s, x = c * (1 - MathF.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        return (r + m, g + m, b + m);
    }

    public static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;
    public static float HueGap(float a, float b) { float d = MathF.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    /// <summary>A texel's cluster: a chromatic hue bin of 20° (0..17, red centred on bin 0), or 18 dark, 19 mid, 20 light neutral.</summary>
    public static int Cluster(byte r8, byte g8, byte b8)
    {
        var (h, s, v) = ToHsv(r8 / 255f, g8 / 255f, b8 / 255f);
        if (s >= 0.30f && v >= 0.10f) return (int)(((h + 10) % 360) / 20);
        return v < 0.18f ? 18 : v < 0.55f ? 19 : 20;
    }

    public static string ClusterName(int c) => c < 18 ? $"hue{c * 20}" : c == 18 ? "dark" : c == 19 ? "grey" : "light";

    /// <summary>The colour distribution of a scheme's body textures: texels per cluster (opaque only), and the mean HSV of each cluster.</summary>
    public sealed class Stats
    {
        public double[] Count = new double[21];
        public double[] H = new double[21], S = new double[21], V = new double[21], X = new double[21], Y = new double[21];
        public double Total;
        public void Add(byte[] rgb, int n)
        {
            for (int i = 0; i < n; i++)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                int c = Cluster(r, g, b);
                var (h, s, v) = ToHsv(r / 255f, g / 255f, b / 255f);
                Count[c]++; Total++; S[c] += s; V[c] += v; X[c] += Math.Cos(h * Math.PI / 180); Y[c] += Math.Sin(h * Math.PI / 180);
            }
        }
        public Hsv Mean(int c) => new((float)((Math.Atan2(Y[c], X[c]) * 180 / Math.PI + 360) % 360), (float)(S[c] / Math.Max(1, Count[c])), (float)(V[c] / Math.Max(1, Count[c])));
        /// <summary>The clusters by texel count, fullest first.</summary>
        public IEnumerable<int> Ranked() => Enumerable.Range(0, 21).Where(c => Count[c] > 0).OrderByDescending(c => Count[c]);
    }

    /// <summary>A paint colour as the mask sees it: mean hue, saturation, value (HSV) and luma of the cluster, and whether it has a hue.</summary>
    public readonly record struct PaintRef(float H, float S, float V, float Y, bool Chromatic);

    /// <summary>
    /// Mask weight 0..1 per texel for paint of the given reference: a chromatic paint takes texels within a hue band (±28°, full within ±14°) that are
    /// saturated and not much brighter or darker than the paint's own range; a neutral paint takes unsaturated texels in the paint's brightness band.
    /// </summary>
    public static float[] Mask(byte[] rgb, int n, PaintRef p, float bandLo = 0.35f, float bandHi = 2.6f)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
        {
            var (h, s, v) = ToHsv(rgb[i * 3] / 255f, rgb[i * 3 + 1] / 255f, rgb[i * 3 + 2] / 255f);
            float y = Luma(rgb[i * 3] / 255f, rgb[i * 3 + 1] / 255f, rgb[i * 3 + 2] / 255f);
            float rel = y / MathF.Max(p.Y, 1e-3f);
            if (p.Chromatic)
            {
                float hg = HueGap(h, p.H);
                float hw = 1 - Smooth(24, 52, hg);
                float sw = Smooth(0.12f, 0.30f, s);
                float vw = Smooth(0.03f, 0.10f, v);
                w[i] = hw * sw * vw;
            }
            else
            {
                float sw = 1 - Smooth(0.15f, 0.30f, s);
                float lw = p.Y < 0.2f
                    ? 1 - Smooth(MathF.Max(0.2f, p.Y * 4), MathF.Max(0.3f, p.Y * 6), y)   // black or dark grey: everything dark and unsaturated
                    : Smooth(p.Y * 0.6f, p.Y * 0.8f, y);                                  // white or silver: everything about as light
                w[i] = sw * lw;
            }
        }
        return w;
    }

    public static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>The reference paint from the stats: the fullest chromatic cluster (with its two neighbours folded in), else the fullest neutral.</summary>
    public static PaintRef Dominant(Stats st, IEnumerable<int>? skip = null)
    {
        var sk = skip?.ToHashSet() ?? [];
        double best = 0; int bc = -1;
        for (int c = 0; c < 18; c++)
        {
            if (sk.Contains(c)) continue;
            double n = st.Count[c] + st.Count[(c + 1) % 18] * 0.5 + st.Count[(c + 17) % 18] * 0.5;
            if (n > best) { best = n; bc = c; }
        }
        double neutralMax = Enumerable.Range(18, 3).Where(c => !sk.Contains(c)).Select(c => st.Count[c]).DefaultIfEmpty(0).Max();
        // a chromatic paint wins if it has at least 12% of the texels (cars with grey bodies keep the neutral)
        if (bc >= 0 && best >= 0.12 * st.Total && best >= 0.5 * neutralMax) return RefOf(st, bc, true);
        var ncs = Enumerable.Range(18, 3).Where(c => !sk.Contains(c)).OrderByDescending(c => st.Count[c]).ToList();
        if (ncs.Count == 0) return new PaintRef(0, 0, 0, 0, false);
        int nc = ncs[0];
        return RefOf(st, nc, false);
    }

    public static PaintRef NeutralRef(Stats st, int c) => RefOf(st, c, false);

    /// <summary>The primary paint of a scheme's statistics and its secondary, if it has one: the next chromatic cluster (at least 6% of the texels) or, for a neutral primary, the opposite extreme.</summary>
    public static (PaintRef Primary, PaintRef? Secondary) Scheme(Stats st)
    {
        var pr = Dominant(st);
        PaintRef? sec = null;
        if (st.Total > 0)
        {
            int c1 = pr.Chromatic ? (int)(((pr.H + 10) % 360) / 20) : -1;
            var skip = pr.Chromatic ? new[] { c1, (c1 + 1) % 18, (c1 + 17) % 18, 18, 19, 20 } : Enumerable.Range(18, 3).ToArray();
            var cand = Dominant(st, skip);
            int cc = cand.Chromatic ? (int)(((cand.H + 10) % 360) / 20) : 18;
            double mass = cand.Chromatic ? (st.Count[cc] + st.Count[(cc + 1) % 18] + st.Count[(cc + 17) % 18]) / st.Total : 0;
            if (cand.Chromatic && mass >= 0.06) sec = cand;
            else if (!pr.Chromatic)
            {
                // a two-tone neutral car (a police car's black and white): the other extreme, if it is a real share of the texels
                bool dark = pr.Y < 0.2f;
                int other = dark ? 20 : 18;
                if (st.Count[other] / st.Total >= (dark ? 0.15 : 0.30)) sec = NeutralRef(st, other);
            }
        }
        return (pr, sec);
    }


    private static PaintRef RefOf(Stats st, int c, bool chromatic)
    {
        if (!chromatic) return new PaintRef(0, 0, (float)(st.V[c] / Math.Max(1, st.Count[c])), 0, false) with { Y = (float)(st.V[c] / Math.Max(1, st.Count[c])) };
        double n = 0, s = 0, v = 0, x = 0, y = 0;
        foreach (int k in new[] { c, (c + 1) % 18, (c + 17) % 18 })
        { n += st.Count[k]; s += st.S[k]; v += st.V[k]; x += st.X[k]; y += st.Y[k]; }
        float h = (float)((Math.Atan2(y, x) * 180 / Math.PI + 360) % 360);
        float sm = (float)(s / n), vm = (float)(v / n);
        var (r, g, b) = FromHsv(h, sm, vm);
        return new PaintRef(h, sm, vm, Luma(r, g, b), true);
    }

    /// <summary>A target paint: its colour (RGB 0..1), and how glossy/metallic its flake should read (a steeper highlight curve here; the shader's job in the game).</summary>
    public readonly record struct Target(string Name, float R, float G, float B, float Gamma = 1f);

    /// <summary>
    /// Recolour: each masked texel keeps its shade relative to the paint's mean luma (the painting's highlights and shadows) and takes the target
    /// colour at that shade. The shade scales the target, rolling off to white where it would clip (a highlight), and a dark target stays dark.
    /// </summary>
    /// <summary>Added to a texel's and the paint's luma before their ratio, so that black paint, which has no shade to scale, can still take a colour.</summary>
    public const float Floor = 0.05f;

    public static byte[] Recolour(byte[] rgb, int n, float[] w, PaintRef src, Target t, float brightness = 1f)
    {
        var o = (byte[])rgb.Clone();
        float ty = Luma(t.R, t.G, t.B) * brightness;
        var (th, ts, tv) = ToHsv(t.R, t.G, t.B);
        for (int i = 0; i < n; i++)
        {
            if (w[i] <= 0.001f) continue;
            float r = rgb[i * 3] / 255f, g = rgb[i * 3 + 1] / 255f, b = rgb[i * 3 + 2] / 255f;
            float y = Luma(r, g, b);
            float shade = MathF.Pow((y + Floor) / (src.Y + Floor), t.Gamma);
            // the target colour scaled by shade; the overflow past 1 goes to white as a highlight
            float cr = t.R * brightness * shade, cg = t.G * brightness * shade, cb = t.B * brightness * shade;
            float over = MathF.Max(0, MathF.Max(cr, MathF.Max(cg, cb)) - 1);
            float k = MathF.Min(over * 0.5f, 1);
            cr = MathF.Min(cr, 1) * (1 - k) + k; cg = MathF.Min(cg, 1) * (1 - k) + k; cb = MathF.Min(cb, 1) * (1 - k) + k;
            float a = w[i];
            o[i * 3] = (byte)Math.Clamp((r * (1 - a) + cr * a) * 255 + 0.5f, 0, 255);
            o[i * 3 + 1] = (byte)Math.Clamp((g * (1 - a) + cg * a) * 255 + 0.5f, 0, 255);
            o[i * 3 + 2] = (byte)Math.Clamp((b * (1 - a) + cb * a) * 255 + 0.5f, 0, 255);
        }
        return o;
    }
}
