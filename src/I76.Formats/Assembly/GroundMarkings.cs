namespace I76.Formats.Assembly;

/// <summary>
/// The paint a ground texture carries, for the modern look: M15's ground (tm15?as_) is a tiled parking lot, flat
/// grey asphalt with darker tyre lanes and white stall lines, where every other stock ground is natural noise. The
/// modern terrain draws such a ground as asphalt with the texture's own layout (the same 40-unit repeat as the 1997
/// look) instead of fitting noise to it, so this finds out whether a texture is one and pulls it apart:
/// <list type="bullet">
/// <item>the base: the texture with its paint taken out (filled from the unpainted texels around it) and softened,
/// so lanes and patches keep their place and lose their stair-steps;</item>
/// <item>the paint as a field at a few times the texture's resolution: R the signed distance to the paint's edge
/// (negative inside), from the texture's paint coverage magnified (bilinearly, the ramps steepened) and cut at half, so lines stay sharp
/// at any distance and only the edges the texture draws are there; thin lines narrowed to a painted line's width
/// (their centres stay put); G the coverage that field draws, to be mipmapped for pixels wider than a line.</item>
/// </list>
/// A texture counts as painted when most of it is flat colour (drawn, not photographed or noise), a small share of
/// it is distinctly brighter than the rest, and that share runs in lines. Natural grounds, snow on rock included,
/// fail the first test by far (<see cref="Measure"/>).
/// </summary>
public sealed class GroundMarkings
{
    /// <summary>Changes with anything that changes the outputs.</summary>
    public const int Version = 1;
    /// <summary>The field's R spans ±Range source texels.</summary>
    public const float Range = 2f;
    /// <summary>Metres a painted line is drawn (where the texture's own lines are wider than this, one texel).</summary>
    public const float LineMetres = 0.13f;

    public int Width { get; private init; }
    public int Height { get; private init; }
    /// <summary>Field texels per source texel.</summary>
    public int Scale { get; private init; }
    /// <summary>RGBA8, Width × Height: the ground without its paint, softened.</summary>
    public byte[] Base { get; private init; } = [];
    /// <summary>RG8, (Width·Scale) × (Height·Scale): R = 0.5 + d / (2·Range), d the signed distance to the paint's edge in source texels; G the paint's coverage.</summary>
    public byte[] Field { get; private init; } = [];
    /// <summary>The paint's colour, sRGB 0..1.</summary>
    public (float R, float G, float B) Paint { get; private init; }
    /// <summary>The share of the texture the drawn paint covers.</summary>
    public float Coverage { get; private init; }
    /// <summary>Source texels each line edge moved in (to <see cref="LineMetres"/>).</summary>
    public float Narrowed { get; private init; }
    public Stats Measured { get; private init; } = null!;

    /// <param name="Flat">share of texels the same colour as their right and lower neighbours</param>
    /// <param name="Threshold">luminance (0..1) splitting paint from ground (Otsu)</param>
    /// <param name="Separation">between-class share of the luminance variance at that split</param>
    /// <param name="Ground">the median luminance below the split</param>
    /// <param name="PaintLevel">the mean luminance above it</param>
    /// <param name="Share">the share of texels above it</param>
    /// <param name="Linear">the share of those that lie in runs at least 4 long and twice as long as across</param>
    public sealed record Stats(float Flat, float Threshold, float Separation, float Ground, float PaintLevel, float Share, float Linear)
    {
        public bool Painted => Flat >= 0.5f && Separation >= 0.7f && PaintLevel - Ground >= 0.2f && Share is >= 0.004f and <= 0.35f && Linear >= 0.75f;
    }

    private static float[] Luminance(byte[] rgba, int n)
    {
        var lum = new float[n];
        for (int i = 0; i < n; i++) lum[i] = (0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2]) / 255f;
        return lum;
    }

    /// <summary>What decides whether a texture is painted ground.</summary>
    public static Stats Measure(byte[] rgba, int w, int h)
    {
        int n = w * h;
        var lum = Luminance(rgba, n);
        int flat = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, r = y * w + (x + 1) % w, d = (y + 1) % h * w + x;
                if (Same(rgba, i, r) && Same(rgba, i, d)) flat++;
            }
        // Otsu's split of the luminance histogram
        var hist = new int[256];
        foreach (var l in lum) hist[Math.Clamp((int)MathF.Round(l * 255), 0, 255)]++;
        double total = 0, sumAll = 0;
        for (int k = 0; k < 256; k++) { total += hist[k]; sumAll += k * (double)hist[k]; }
        double mean = sumAll / total, varAll = 0;
        for (int k = 0; k < 256; k++) varAll += hist[k] * (k - mean) * (k - mean);
        varAll /= total;
        double best = -1, wB = 0, sumB = 0;
        int split = 0;
        for (int k = 0; k < 255; k++)
        {
            wB += hist[k]; sumB += k * (double)hist[k];
            double wF = total - wB;
            if (wB == 0 || wF == 0) continue;
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = wB * wF * (mB - mF) * (mB - mF) / (total * total);
            if (between > best) { best = between; split = k; }
        }
        float thr = (split + 0.5f) / 255f;
        var low = lum.Where(l => l <= thr).OrderBy(l => l).ToArray();
        var high = lum.Where(l => l > thr).ToArray();
        float ground = low.Length > 0 ? low[low.Length / 2] : 0f;
        float paint = high.Length > 0 ? high.Average() : 1f;
        // runs of paint across and along each painted texel
        int linear = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (lum[y * w + x] <= thr) continue;
                int rh = Run(lum, w, h, x, y, 1, 0, thr), rv = Run(lum, w, h, x, y, 0, 1, thr);
                if (Math.Max(rh, rv) >= 4 && Math.Max(rh, rv) >= 2 * Math.Min(rh, rv)) linear++;
            }
        return new Stats(flat / (float)n, thr, varAll > 0 ? (float)(best / varAll) : 0f, ground, paint,
            high.Length / (float)n, high.Length > 0 ? linear / (float)high.Length : 0f);
    }

    private static bool Same(byte[] p, int a, int b) => p[a * 4] == p[b * 4] && p[a * 4 + 1] == p[b * 4 + 1] && p[a * 4 + 2] == p[b * 4 + 2];

    /// <summary>The texels above <paramref name="thr"/> in a line through (x, y) along (dx, dy), wrapping, at most the texture's size.</summary>
    private static int Run(float[] lum, int w, int h, int x, int y, int dx, int dy, float thr)
    {
        int len = dx != 0 ? w : h, run = 1;
        for (int s = 1; run < len && lum[(y + dy * s) % h * w + (x + dx * s) % w] > thr; s++) run++;
        for (int s = 1; run < len && lum[((y - dy * s) % h + h) % h * w + ((x - dx * s) % w + w) % w] > thr; s++) run++;
        return run;
    }

    /// <summary>
    /// The texture's paint and base, or null for a natural ground.
    /// </summary>
    /// <param name="rgba">the ground texture, RGBA8, tiling</param>
    /// <param name="repeatMetres">the metres one repeat covers (40: 8 samples)</param>
    public static GroundMarkings? Find(byte[] rgba, int w, int h, float repeatMetres)
    {
        var st = Measure(rgba, w, h);
        if (!st.Painted) return null;
        int n = w * h;
        var lum = Luminance(rgba, n);
        float span = st.PaintLevel - st.Ground;

        // the base: texels more than a quarter painted filled from their unpainted neighbours, ring by ring
        var known = new bool[n];
        var col = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            known[i] = (lum[i] - st.Ground) / span <= 0.25f;
            for (int c = 0; c < 3; c++) col[i * 3 + c] = rgba[i * 4 + c] / 255f;
        }
        var todo = Enumerable.Range(0, n).Where(i => !known[i]).ToList();
        while (todo.Count > 0)
        {
            var fill = new List<(int I, float R, float G, float B)>();
            foreach (int i in todo)
            {
                int x = i % w, y = i / w, k = 0;
                float r = 0, g = 0, b = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = (y + dy + h) % h * w + (x + dx + w) % w;
                        if (!known[j]) continue;
                        r += col[j * 3]; g += col[j * 3 + 1]; b += col[j * 3 + 2]; k++;
                    }
                if (k > 0) fill.Add((i, r / k, g / k, b / k));
            }
            if (fill.Count == 0) break;
            foreach (var f in fill) { known[f.I] = true; col[f.I * 3] = f.R; col[f.I * 3 + 1] = f.G; col[f.I * 3 + 2] = f.B; }
            todo.RemoveAll(i => known[i]);
        }
        // each texel's paint coverage against its own base
        var cover = new float[n];
        for (int i = 0; i < n; i++)
        {
            float bl = 0.299f * col[i * 3] + 0.587f * col[i * 3 + 1] + 0.114f * col[i * 3 + 2];
            cover[i] = Math.Clamp((lum[i] - bl) / Math.Max(st.PaintLevel - bl, 0.05f), 0f, 1f);
        }
        // the paint's colour: the texels (nearly) fully painted
        double pr = 0, pg = 0, pb = 0; int pn = 0;
        for (int i = 0; i < n; i++)
            if (cover[i] >= 0.75f) { pr += rgba[i * 4]; pg += rgba[i * 4 + 1]; pb += rgba[i * 4 + 2]; pn++; }
        var paintCol = pn > 0 ? ((float)(pr / pn / 255), (float)(pg / pn / 255), (float)(pb / pn / 255)) : (1f, 1f, 1f);
        var soft = Blur(col, w, h, 1.0f);
        var baseRgba = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++) baseRgba[i * 4 + c] = (byte)Math.Clamp(MathF.Round(soft[i * 3 + c] * 255), 0, 255);
            baseRgba[i * 4 + 3] = 255;
        }

        // the field: coverage magnified bilinearly, cut at half, its signed distance; the lines narrowed
        // an odd scale puts a field texel's centre on every source texel's centre, where a one-texel line is deepest
        int s = Math.Clamp(512 / Math.Max(w, h), 1, 8) | 1;
        int fw = w * s, fh = h * s;
        var inside = new bool[fw * fh];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
                inside[y * fw + x] = Bilinear(cover, w, h, (x + 0.5f) / s - 0.5f, (y + 0.5f) / s - 0.5f, Sharpness) >= 0.5f;
        var sd = SignedDistance(inside, fw, fh, (int)MathF.Ceiling(Range * s) + 2);
        float texelMetres = repeatMetres / w;
        float narrow = Math.Max(0f, (texelMetres - LineMetres) / 2f / texelMetres);
        var field = new byte[fw * fh * 2];
        double covered = 0;
        for (int i = 0; i < fw * fh; i++)
        {
            float d = sd[i] / s + narrow;   // source texels
            field[i * 2] = (byte)Math.Clamp(MathF.Round(127.5f + d / Range * 127.5f), 0, 255);
            float c = Math.Clamp(0.5f - d * s, 0f, 1f);
            field[i * 2 + 1] = (byte)MathF.Round(c * 255);
            covered += c;
        }
        return new GroundMarkings
        {
            Width = w, Height = h, Scale = s, Base = baseRgba, Field = field, Paint = paintCol,
            Coverage = (float)(covered / (fw * fh)), Narrowed = narrow, Measured = st,
        };
    }

    /// <summary>
    /// How much steeper than linear the magnification's ramp between two texel centres is: a line keeps its width and
    /// place, but where two meet the corner stays nearly square (plain bilinear rounds it with a fillet a texel long)
    /// and a diagonal edge stays smooth.
    /// </summary>
    private const float Sharpness = 3f;

    private static float Bilinear(float[] g, int w, int h, float x, float y, float sharp)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = Math.Clamp((x - x0 - 0.5f) * sharp + 0.5f, 0f, 1f), fy = Math.Clamp((y - y0 - 0.5f) * sharp + 0.5f, 0f, 1f);
        float At(int xx, int yy) => g[(yy % h + h) % h * w + (xx % w + w) % w];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    /// <summary>A separable Gaussian blur of an RGB float image that tiles.</summary>
    private static float[] Blur(float[] src, int w, int h, float sigma)
    {
        int r = (int)MathF.Ceiling(3 * sigma);
        var k = new float[2 * r + 1];
        float ks = 0;
        for (int i = -r; i <= r; i++) ks += k[i + r] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < k.Length; i++) k[i] /= ks;
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float a = 0;
                    for (int i = -r; i <= r; i++) a += k[i + r] * src[(y * w + (x + i + w) % w) * 3 + c];
                    tmp[(y * w + x) * 3 + c] = a;
                }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float a = 0;
                    for (int i = -r; i <= r; i++) a += k[i + r] * tmp[((y + i + h) % h * w + x) * 3 + c];
                    dst[(y * w + x) * 3 + c] = a;
                }
        return dst;
    }

    /// <summary>
    /// The signed distance (texels, negative inside) from each texel's centre to the edge between inside and outside,
    /// taken as half a texel short of the nearest centre on the other side; the image tiles, distances past
    /// <paramref name="pad"/> texels are capped there.
    /// </summary>
    private static float[] SignedDistance(bool[] inside, int w, int h, int pad)
    {
        var dIn = Distance(inside, w, h, pad, false);    // from each inside texel to the nearest outside one
        var dOut = Distance(inside, w, h, pad, true);    // and the other way
        var sd = new float[w * h];
        for (int i = 0; i < sd.Length; i++)
            sd[i] = inside[i] ? -(MathF.Min(dIn[i], pad) - 0.5f) : MathF.Min(dOut[i], pad) - 0.5f;
        return sd;
    }

    /// <summary>Euclidean distance to the nearest texel whose inside flag is <paramref name="target"/> (Felzenszwalb and Huttenlocher), on the image padded by wrapping.</summary>
    private static float[] Distance(bool[] inside, int w, int h, int pad, bool target)
    {
        int pw = w + 2 * pad, ph = h + 2 * pad;
        const float Inf = 1e20f;
        var g = new float[pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                g[y * pw + x] = inside[((y - pad) % h + h) % h * w + ((x - pad) % w + w) % w] == target ? 0f : Inf;
        var f = new float[Math.Max(pw, ph)];
        var d = new float[Math.Max(pw, ph)];
        var v = new int[Math.Max(pw, ph)];
        var z = new float[Math.Max(pw, ph) + 1];
        for (int x = 0; x < pw; x++)
        {
            for (int y = 0; y < ph; y++) f[y] = g[y * pw + x];
            Dt1(f, ph, d, v, z);
            for (int y = 0; y < ph; y++) g[y * pw + x] = d[y];
        }
        for (int y = 0; y < ph; y++)
        {
            Array.Copy(g, y * pw, f, 0, pw);
            Dt1(f, pw, d, v, z);
            Array.Copy(d, 0, g, y * pw, pw);
        }
        var o = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) o[y * w + x] = MathF.Sqrt(g[(y + pad) * pw + x + pad]);
        return o;
    }

    /// <summary>The 1D squared distance transform of sampled function f (lower envelope of parabolas).</summary>
    private static void Dt1(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = 0;
        v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s = Meet(f, q, v[k]);
            while (s <= z[k]) { k--; s = Meet(f, q, v[k]); }
            k++;
            v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    private static float Meet(float[] f, int q, int p) => (f[q] + q * (float)q - (f[p] + p * (float)p)) / (2f * (q - p));
}
