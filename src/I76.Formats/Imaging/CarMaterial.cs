namespace I76.Formats.Imaging;

/// <summary>
/// Material maps for the modern look's cars, made from their own textures at
/// load time (nothing is shipped). A car's textures are flat-lit paintings:
/// paint with its highlights painted in, windows, chrome, the engine, black
/// trim and rubber, decals, all in one texture and all opaque. Each texel is
/// weighed as paint, glass, metal or trim from its colour and from the car's
/// own paint colour (the commonest colour across its textures, so grey, black
/// and white cars keep their paint), and the weights blend the material:
///
///   paint  clear-coated, a fairly smooth base under a glossy coat
///   glass  smooth, a stronger reflection, no relief
///   metal  metallic, polished where the painting is smooth, duller where it
///          is busy (an engine's castings, a grille)
///   trim   dark: rough where busy (rubber, grilles), coated where smooth
///          (painted stripes, black panels)
///
/// The relief, at twice the texture's resolution so that it is not blocky:
/// on paint only its seams, thin lines darker than both sides, sunk in (the
/// painted-in highlights are no shape, and the pale streaks of them count as
/// paint rather than chrome); on metal and trim the fine detail both ways,
/// brightness less a blur of it (a grille's bars, an engine's castings).
/// </summary>
public static class CarMaterial
{
    /// <summary>A car's paint: hue in degrees, saturation and value (HSV), and whether it has a hue at all.</summary>
    public readonly record struct Paint(float Hue, float Sat, float Val, bool Chromatic);

    /// <summary>Maps at Scale× the texture: Orm = roughness, metallic, clear coat, specular (RGBA8); Normal = tangent space, RGB8 (x right, y down the texture).</summary>
    public sealed record Maps(int Width, int Height, byte[] Orm, byte[] Normal);

    public const int Scale = 2;

    private static (float H, float S, float V) Hsv(byte r8, byte g8, byte b8)
    {
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b)), c = mx - mn;
        float h = c <= 1e-5f ? 0 : mx == r ? 60 * (((g - b) / c + 6) % 6) : mx == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        return (h, mx <= 1e-5f ? 0 : c / mx, mx);
    }

    private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    private static float HueGap(float a, float b) { float d = MathF.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    /// <summary>
    /// The commonest colour over a car's textures (opaque texels): in 15° hue bins where saturated, in brightness
    /// bins where not, the mean of the fullest bin.
    /// </summary>
    public static Paint DominantPaint(IEnumerable<(byte[] Rgba, int W, int H)> textures)
    {
        var count = new double[24 + 8];
        var sum = new (double H, double S, double V, double X, double Y)[32];
        foreach (var (p, w, h) in textures)
            for (int i = 0; i < w * h; i++)
            {
                if (p[i * 4 + 3] == 0) continue;
                var (hu, s, v) = Hsv(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
                bool chroma = s >= 0.3f && v >= 0.15f;
                int bin = chroma ? (int)(hu / 15) % 24 : 24 + Math.Min(7, (int)(v * 8));
                count[bin]++;
                var a = sum[bin];
                double rad = hu * Math.PI / 180;
                sum[bin] = (a.H, a.S + s, a.V + v, a.X + Math.Cos(rad), a.Y + Math.Sin(rad));
            }
        int best = 0;
        for (int i = 1; i < count.Length; i++) if (count[i] > count[best]) best = i;
        if (count[best] == 0) return new Paint(0, 0, 0.5f, false);
        var m = sum[best];
        double n = count[best];
        float hue = (float)((Math.Atan2(m.Y, m.X) * 180 / Math.PI + 360) % 360);
        return new Paint(hue, (float)(m.S / n), (float)(m.V / n), best < 24);
    }

    public static Maps Build(byte[] rgba, int w, int h, Paint paint)
    {
        int n = w * h;
        var lum = new float[n];
        var cls = new (float Paint, float Glass, float Metal, float Trim)[n];
        for (int i = 0; i < n; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            lum[i] = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            var (hu, s, v) = Hsv(r, g, b);
            float dark = 1 - Smooth(0.08f, 0.18f, v);
            // the car's own paint, and anything else strongly coloured (decals, stripes, the odd red part)
            float near = paint.Chromatic
                ? (1 - Smooth(18, 40, HueGap(hu, paint.Hue))) * Smooth(0.12f, 0.28f, s) * Smooth(0.05f, 0.12f, v)
                : (1 - Smooth(0.08f, 0.2f, MathF.Abs(v - paint.Val))) * (1 - Smooth(0.12f, 0.25f, s));
            float coloured = Smooth(0.28f, 0.45f, s) * (1 - dark);
            float pw = MathF.Max(near, coloured);
            // blue-grey and only a little saturated: the windows
            float blue = 1 - Smooth(25, 50, HueGap(hu, 205));
            float gl = blue * Smooth(0.05f, 0.12f, s) * (1 - Smooth(0.35f, 0.5f, s)) * Smooth(0.12f, 0.25f, v) * (1 - pw);
            float rest = MathF.Max(0, 1 - pw - gl);
            cls[i] = (pw, gl, rest * (1 - dark), rest * dark);
        }
        // fine detail: brightness less its blur, and how busy the painting is about each texel
        var blur = Blur(lum, w, h);
        var hp = new float[n];
        for (int i = 0; i < n; i++) hp[i] = lum[i] - blur[i];
        var busy = Blur(hp.Select(MathF.Abs).ToArray(), w, h);
        // the paint's painted-in highlights (and white decals): pale, brighter than the paint or simply bright,
        // smooth (painted chrome is busy with detail), with paint about them; paint, not chrome
        var around = Box(cls.Select(c => c.Paint).ToArray(), w, h, 4);
        var pale = new float[n];
        var hls = new float[n];
        for (int i = 0; i < n; i++)
        {
            var (_, s, v) = Hsv(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            float bright = MathF.Max(Smooth(0.45f, 0.75f, v), Smooth(0.1f, 0.3f, v - paint.Val));
            pale[i] = bright * (1 - Smooth(0.3f, 0.5f, s)) * (1 - Math.Clamp(cls[i].Glass * 2, 0, 1));   // the windows stay glass
            hls[i] = pale[i] * Smooth(0.12f, 0.3f, around[i]) * (1 - Smooth(0.03f, 0.07f, busy[i]));
        }
        // a highlight's rim is a step, and busy: grown out from its smooth middle over the pale texels next to it
        for (int pass = 0; pass < 2; pass++)
        {
            var grown = (float[])hls.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            m = MathF.Max(m, hls[((y + dy) % h + h) % h * w + ((x + dx) % w + w) % w]);
                    int i = y * w + x;
                    grown[i] = MathF.Max(hls[i], m * pale[i]);
                }
            hls = grown;
        }
        for (int i = 0; i < n; i++)
        {
            float hl = hls[i];
            var c = cls[i];
            if (hl > c.Paint)
            {
                float k = (1 - hl) / MathF.Max(1 - c.Paint, 1e-4f);
                cls[i] = (hl, c.Glass * k, c.Metal * k, c.Trim * k);
            }
        }
        // the paint's relief is its seams alone: what is darker than both sides and a texel or two wide (a black
        // top-hat, the 3×3 closing less the image). The painted-in highlights are broad or bright, and their edges
        // are steps, which a closing leaves as they are, so they raise nothing.
        var seams = Closing3(lum, w, h);
        for (int i = 0; i < n; i++) seams[i] -= lum[i];

        int W = w * Scale, H = h * Scale;
        var orm = new byte[W * H * 4];
        var height = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                // bilinear, texel centres, wrapping as the textures repeat
                float fx = (x + 0.5f) / Scale - 0.5f, fy = (y + 0.5f) / Scale - 0.5f;
                int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
                float tx = fx - x0, ty = fy - y0;
                float pw = 0, gl = 0, me = 0, tr = 0, hh = 0, bz = 0;
                for (int k = 0; k < 4; k++)
                {
                    int xx = ((x0 + (k & 1)) % w + w) % w, yy = ((y0 + (k >> 1)) % h + h) % h;
                    float wt = ((k & 1) == 1 ? tx : 1 - tx) * ((k >> 1) == 1 ? ty : 1 - ty);
                    int j = yy * w + xx;
                    var c = cls[j];
                    pw += c.Paint * wt; gl += c.Glass * wt; me += c.Metal * wt; tr += c.Trim * wt;
                    // relief per class: seams in the paint, more on metal and trim, none on glass
                    hh += (-seams[j] * c.Paint * 0.8f + hp[j] * (c.Metal * 1.1f + c.Trim * 0.9f)) * wt;
                    bz += busy[j] * wt;
                }
                float sum = MathF.Max(pw + gl + me + tr, 1e-4f);
                pw /= sum; gl /= sum; me /= sum; tr /= sum;
                float busyness = Smooth(0.015f, 0.08f, bz);
                float metalRough = 0.12f + 0.4f * busyness;
                // dark and smooth is most often a painted stripe or a black panel, under the same coat as the paint;
                // dark and busy is rubber, a grille, the underside
                float trimRough = 0.45f + 0.4f * busyness;
                float rough = pw * 0.4f + gl * 0.04f + me * metalRough + tr * trimRough;
                float metallic = me * 0.9f;
                float coat = pw + tr * (1 - busyness) * 0.8f;
                float spec = pw * 0.5f + gl * 1.0f + me * 0.5f + tr * 0.3f;
                int o = (y * W + x) * 4;
                orm[o] = (byte)(Math.Clamp(rough, 0, 1) * 255);
                orm[o + 1] = (byte)(Math.Clamp(metallic, 0, 1) * 255);
                orm[o + 2] = (byte)(Math.Clamp(coat, 0, 1) * 255);
                orm[o + 3] = (byte)(Math.Clamp(spec, 0, 1) * 255);
                height[y * W + x] = hh;
            }
        // the normal from the height's slope (a texel of a car texture is a centimetre or two)
        var nrm = new byte[W * H * 3];
        const float strength = 6f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float At(int dx, int dy) => height[((y + dy + H) % H) * W + (x + dx + W) % W];
                float sx = (At(1, -1) + 2 * At(1, 0) + At(1, 1)) - (At(-1, -1) + 2 * At(-1, 0) + At(-1, 1));
                float sy = (At(-1, 1) + 2 * At(0, 1) + At(1, 1)) - (At(-1, -1) + 2 * At(0, -1) + At(1, -1));
                float nx = -sx * strength, ny = -sy * strength, nz = 1;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                int o = (y * W + x) * 3;
                nrm[o] = (byte)((nx / len * 0.5f + 0.5f) * 255);
                nrm[o + 1] = (byte)((ny / len * 0.5f + 0.5f) * 255);
                nrm[o + 2] = (byte)((nz / len * 0.5f + 0.5f) * 255);
            }
        return new Maps(W, H, orm, nrm);
    }

    /// <summary>The mean over the (2r+1)² square about each texel, wrapping.</summary>
    private static float[] Box(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var o = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -r; d <= r; d++) s += src[y * w + ((x + d) % w + w) % w];
                tmp[y * w + x] = s / (2 * r + 1);
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -r; d <= r; d++) s += tmp[((y + d) % h + h) % h * w + x];
                o[y * w + x] = s / (2 * r + 1);
            }
        return o;
    }

    /// <summary>The greatest then the least over each 3×3 neighbourhood (a closing), wrapping.</summary>
    private static float[] Closing3(float[] src, int w, int h)
    {
        float[] Pass(float[] a, Func<float, float, float> pick)
        {
            var o = new float[a.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = a[y * w + x];
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            m = pick(m, a[((y + dy) % h + h) % h * w + ((x + dx) % w + w) % w]);
                    o[y * w + x] = m;
                }
            return o;
        }
        return Pass(Pass(src, MathF.Max), MathF.Min);
    }

    /// <summary>A 5-tap binomial blur each way (σ about 1 texel), wrapping.</summary>
    private static float[] Blur(float[] src, int w, int h)
    {
        ReadOnlySpan<float> k = [1 / 16f, 4 / 16f, 6 / 16f, 4 / 16f, 1 / 16f];
        var tmp = new float[src.Length];
        var o = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -2; d <= 2; d++) s += k[d + 2] * src[y * w + ((x + d) % w + w) % w];
                tmp[y * w + x] = s;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -2; d <= 2; d++) s += k[d + 2] * tmp[((y + d) % h + h) % h * w + x];
                o[y * w + x] = s;
            }
        return o;
    }
}
