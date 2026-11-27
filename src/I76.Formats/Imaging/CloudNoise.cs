using System;
using System.Linq;
using System.Threading.Tasks;

namespace I76.Formats.Imaging;

/// <summary>
/// The modern look's volumetric clouds (Graphic Detail's Clouds, sky_volumetric.gdshader): the setting, the tiling 3D noise
/// that shapes them and the cover map drawn from the mission's sky texture.
/// </summary>
public static class CloudNoise
{
    public const int Flat = 0, Volumetric = 1;
    public static readonly string[] Names = ["Flat", "Volumetric"];

    /// <summary>A stored word as a setting: "flat" (any case) is Flat; anything else, or nothing, Volumetric, the default.</summary>
    public static int Parse(string? word) => word?.Trim().ToLowerInvariant() == "flat" ? Flat : Volumetric;
    public static string Word(int mode) => mode == Volumetric ? "volumetric" : "flat";

    public const int ShapeSize = 128, DetailSize = 64;

    /// <summary>
    /// The shape noise, <see cref="ShapeSize"/>³ bytes (x fastest, then y, then z), tiling on every axis: billowy Perlin
    /// noise remapped by Worley noise (Perlin-Worley), then remapped again by a Worley FBM a step finer, so the cells read as
    /// round lumps with Perlin's spread between them. Its finest cells are 8 texels across: anything finer is a texel or
    /// two, and sampled linearly it shows the lattice as facets (the erosion noise adds the fine detail).
    /// </summary>
    public static byte[] Shape(int size = ShapeSize)
    {
        var w = new[] { 4, 8, 16 }.Select(c => new WorleyGrid(c)).ToArray();
        return Volume(size, (x, y, z) =>
        {
            float perlin = MathF.Abs(PerlinFbm(x, y, z, 4, 3) * 2f - 1f);   // billows
            float w4 = w[0].At(x, y, z), w8 = w[1].At(x, y, z), w16 = w[2].At(x, y, z);
            float pw = Remap(1f - perlin, 0f, 1f, w4 * 0.625f + w8 * 0.25f + w16 * 0.125f, 1f);   // Perlin-Worley: Perlin with Worley's cells as its floor
            float low = w8 * 0.7f + w16 * 0.3f;
            return Remap(pw, low - 1f, 1f, 0f, 1f);
        });
    }

    /// <summary>
    /// A volume shrunk by <paramref name="factor"/> on each axis, each texel the mean of the block it covers: the shape noise
    /// for far clouds, its finest billows (under a pixel there, and sparkling) averaged away.
    /// </summary>
    public static byte[] Shrink(byte[] v, int n, int factor)
    {
        int m = n / factor;
        var o = new byte[m * m * m];
        for (int z = 0; z < m; z++)
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    int sum = 0;
                    for (int k = 0; k < factor; k++)
                        for (int j = 0; j < factor; j++)
                            for (int i = 0; i < factor; i++)
                                sum += v[((z * factor + k) * n + y * factor + j) * n + x * factor + i];
                    o[(z * m + y) * m + x] = (byte)((sum + factor * factor * factor / 2) / (factor * factor * factor));
                }
        return o;
    }

    /// <summary>The erosion noise, <see cref="DetailSize"/>³ bytes: a Worley FBM, its edges eaten into the shape's; its finest cells 8 texels across.</summary>
    public static byte[] Detail(int size = DetailSize)
    {
        var w = new[] { 2, 4, 8 }.Select(c => new WorleyGrid(c)).ToArray();
        return Volume(size, (x, y, z) => w[0].At(x, y, z) * 0.625f + w[1].At(x, y, z) * 0.25f + w[2].At(x, y, z) * 0.125f);
    }

    /// <summary>The noise over the cube, stretched so its 1st to 99th percentiles fill the bytes (the remaps leave it bunched near the top).</summary>
    private static byte[] Volume(int n, Func<float, float, float, float> f)
    {
        var v = new float[n * n * n];
        Parallel.For(0, n, z =>
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    v[(z * n + y) * n + x] = f((x + 0.5f) / n, (y + 0.5f) / n, (z + 0.5f) / n);
        });
        var sorted = (float[])v.Clone();
        Array.Sort(sorted);
        float lo = sorted[sorted.Length / 100], hi = sorted[sorted.Length - 1 - sorted.Length / 100];
        var data = new byte[v.Length];
        for (int i = 0; i < v.Length; i++)
            data[i] = (byte)Math.Clamp((int)MathF.Round((v[i] - lo) / MathF.Max(hi - lo, 1e-6f) * 255f), 0, 255);
        return data;
    }

    private static float Remap(float v, float lo, float hi, float nlo, float nhi) => nlo + (v - lo) / MathF.Max(hi - lo, 1e-4f) * (nhi - nlo);

    // ---- tiling noise on the unit cube ----

    private static uint Hash(int x, int y, int z, int seed)
    {
        uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ (uint)z * 0xcb1ab31fu ^ (uint)seed * 0x9e3779b9u;
        h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
        return h;
    }
    private static float Rand(int x, int y, int z, int seed) => (Hash(x, y, z, seed) & 0xffffff) / 16777216f;
    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    /// <summary>Worley noise with a given number of cells a side, a feature point in each, inverted (1 at a point, 0 a cell away), tiling.</summary>
    public sealed class WorleyGrid
    {
        private readonly int _n;
        private readonly float[] _p;
        public WorleyGrid(int cells)
        {
            _n = cells;
            _p = new float[cells * cells * cells * 3];
            for (int z = 0; z < cells; z++)
                for (int y = 0; y < cells; y++)
                    for (int x = 0; x < cells; x++)
                    {
                        int i = ((z * cells + y) * cells + x) * 3;
                        _p[i] = Rand(x, y, z, cells); _p[i + 1] = Rand(x, y, z, cells + 101); _p[i + 2] = Rand(x, y, z, cells + 202);
                    }
        }

        public float At(float x, float y, float z)
        {
            int n = _n;
            float px = x * n, py = y * n, pz = z * n;
            int cx = (int)MathF.Floor(px), cy = (int)MathF.Floor(py), cz = (int)MathF.Floor(pz);
            float best = float.MaxValue;
            for (int k = -1; k <= 1; k++)
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        int o = ((Wrap(cz + k, n) * n + Wrap(cy + j, n)) * n + Wrap(cx + i, n)) * 3;
                        float fx = cx + i + _p[o] - px, fy = cy + j + _p[o + 1] - py, fz = cz + k + _p[o + 2] - pz;
                        best = MathF.Min(best, fx * fx + fy * fy + fz * fz);
                    }
            return 1f - MathF.Min(MathF.Sqrt(best), 1f);
        }
    }

    private static float Grad(int x, int y, int z, int period, float dx, float dy, float dz)
    {
        // one of the twelve edge directions of a cube
        switch (Hash(Wrap(x, period), Wrap(y, period), Wrap(z, period), 7) % 12)
        {
            case 0: return dx + dy; case 1: return -dx + dy; case 2: return dx - dy; case 3: return -dx - dy;
            case 4: return dx + dz; case 5: return -dx + dz; case 6: return dx - dz; case 7: return -dx - dz;
            case 8: return dy + dz; case 9: return -dy + dz; case 10: return dy - dz; default: return -dy - dz;
        }
    }
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Perlin gradient noise with a period of <paramref name="period"/> lattice cells across the cube, about -1..1.</summary>
    public static float Perlin(float x, float y, float z, int period)
    {
        float px = x * period, py = y * period, pz = z * period;
        int ix = (int)MathF.Floor(px), iy = (int)MathF.Floor(py), iz = (int)MathF.Floor(pz);
        float fx = px - ix, fy = py - iy, fz = pz - iz;
        float u = Fade(fx), v = Fade(fy), w = Fade(fz);
        float G(int a, int b, int c) => Grad(ix + a, iy + b, iz + c, period, fx - a, fy - b, fz - c);
        return Lerp(Lerp(Lerp(G(0, 0, 0), G(1, 0, 0), u), Lerp(G(0, 1, 0), G(1, 1, 0), u), v),
                    Lerp(Lerp(G(0, 0, 1), G(1, 0, 1), u), Lerp(G(0, 1, 1), G(1, 1, 1), u), v), w);
    }

    /// <summary>Perlin FBM, 0..1 about 0.5.</summary>
    public static float PerlinFbm(float x, float y, float z, int period, int octaves)
    {
        float s = 0f, a = 1f, norm = 0f;
        for (int o = 0; o < octaves; o++) { s += a * Perlin(x, y, z, period << o); norm += a; a *= 0.5f; }
        return Math.Clamp(s / norm * 0.7f + 0.5f, 0f, 1f);
    }

    // ---- the cover ----

    /// <summary>
    /// The cover map from the sky texture (RGB bytes, sRGB, <paramref name="w"/> × <paramref name="h"/>, tiling): the flat
    /// sky's own cloud density laid out over one repeat of the texture (sky_modern.gdshaderinc's density), so the volumetric
    /// clouds take its shapes, wisps and frayed edges. The texture's linear colour after the same small blur, its place
    /// between the open sky's colour and the clouds' (<paramref name="sky"/> and <paramref name="cloud"/>, linear, from
    /// <see cref="SkyColours.Measure"/>) ramped (<see cref="SkyColours.Cover"/>), enlarged <paramref name="scale"/> times each way by a cubic B-spline (the stock skies' 128 texels sampled
    /// linearly left creases that showed as facets in the clouds), then moved by the same large lumps and eaten at its edges by
    /// the same fine, warped noise as the flat sky's, <paramref name="detail"/> its strength (the flat sky's detail): value
    /// noise on the flat sky's lattices (5 and 17 cells a repeat), made to tile with the texture. One value a texel, 0..1.
    /// </summary>
    public static float[] Cover(ReadOnlySpan<byte> rgb, int w, int h, System.Numerics.Vector3 sky, System.Numerics.Vector3 cloud, float detail = 0.35f, int scale = 8)
    {
        var lin = new System.Numerics.Vector3[w * h];
        for (int i = 0; i < w * h; i++) lin[i] = new(ToLinear(rgb[i * 3]), ToLinear(rgb[i * 3 + 1]), ToLinear(rgb[i * 3 + 2]));
        // the flat sky's blur is 0.006 of the texture each way: about a texel and a half on the stock 256 skies
        int r = Math.Max(1, (int)MathF.Round(0.006f * w));
        var cover = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                System.Numerics.Vector3 L(int dx, int dy) => lin[Wrap(y + dy, h) * w + Wrap(x + dx, w)];
                var c = L(0, 0) * 0.4f + (L(r, 0) + L(-r, 0) + L(0, r) + L(0, -r)) * 0.15f;
                cover[y * w + x] = SkyColours.Cover(c, sky, cloud);
            }
        var b = Enlarge(cover, w, h, scale);
        int W = w * scale, H = h * scale;
        Parallel.For(0, H, y =>
        {
            float v = (y + 0.5f) / H;
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W;
                float bs = Math.Clamp(b[y * W + x], 0f, 1f);
                // billows: large lumps inside, fine erosion at the edges
                float n = Fbm2(u * 5f, v * 5f, 5, 5, 11);
                float e = Fbm2(u * 17f + n, v * 17f + n, 17, 4, 23);
                float edge = 1f - MathF.Abs(bs * 2f - 1f);
                b[y * W + x] = Math.Clamp(bs + (n - 0.5f) * detail * 1.6f + (e - 0.5f) * detail * 1.4f * edge, 0f, 1f);
            }
        });
        return b;
    }

    /// <summary>Value noise on a lattice of <paramref name="period"/> cells a side, tiling, 0..1 (sky_modern's vnoise).</summary>
    public static float ValueNoise2(float x, float y, int period, int seed)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
        float R(int i, int j) => Rand(Wrap(ix + i, period), Wrap(iy + j, period), 0, seed);
        return Lerp(Lerp(R(0, 0), R(1, 0), ux), Lerp(R(0, 1), R(1, 1), ux), uy);
    }

    /// <summary>sky_modern's fbm, tiling: octaves of <see cref="ValueNoise2"/>, each twice as fine and half as strong (0 .. about 1).</summary>
    public static float Fbm2(float x, float y, int period, int octaves, int seed)
    {
        float s = 0f, a = 0.5f;
        for (int o = 0; o < octaves; o++) { s += a * ValueNoise2(x * (1 << o), y * (1 << o), period << o, seed + o); a *= 0.5f; }
        return s;
    }

    /// <summary>RGB bytes (tiling) enlarged <paramref name="scale"/> times each way by <see cref="Enlarge"/>, channel by channel.</summary>
    public static byte[] EnlargeRgb(ReadOnlySpan<byte> rgb, int w, int h, int scale)
    {
        int W = w * scale, H = h * scale;
        var o = new byte[W * H * 3];
        var ch = new float[w * h];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < w * h; i++) ch[i] = rgb[i * 3 + c];
            var e = Enlarge(ch, w, h, scale);
            for (int i = 0; i < W * H; i++) o[i * 3 + c] = (byte)Math.Clamp((int)MathF.Round(e[i]), 0, 255);
        }
        return o;
    }

    /// <summary>A tiling image enlarged <paramref name="scale"/> times each way by a cubic B-spline through its texels' centres.</summary>
    public static float[] Enlarge(float[] src, int w, int h, int scale)
    {
        int W = w * scale, H = h * scale;
        var dst = new float[W * H];
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        for (int y = 0; y < H; y++)
        {
            float fy = (y + 0.5f) / scale - 0.5f;
            int iy = (int)MathF.Floor(fy);
            Weights(fy - iy, wy);
            for (int x = 0; x < W; x++)
            {
                float fx = (x + 0.5f) / scale - 0.5f;
                int ix = (int)MathF.Floor(fx);
                Weights(fx - ix, wx);
                float s = 0f;
                for (int j = 0; j < 4; j++)
                {
                    int row = Wrap(iy - 1 + j, h) * w;
                    for (int i = 0; i < 4; i++) s += src[row + Wrap(ix - 1 + i, w)] * wx[i] * wy[j];
                }
                dst[y * W + x] = s;
            }
        }
        return dst;

        static void Weights(float t, Span<float> k)
        {
            float t2 = t * t, t3 = t2 * t;
            k[0] = (1f - 3f * t + 3f * t2 - t3) / 6f;
            k[1] = (4f - 6f * t2 + 3f * t3) / 6f;
            k[2] = (1f + 3f * t + 3f * t2 - 3f * t3) / 6f;
            k[3] = t3 / 6f;
        }
    }

    /// <summary>A Gaussian blur of a tiling image, <paramref name="sigma"/> in texels.</summary>
    public static float[] Blur(float[] src, int w, int h, float sigma)
    {
        int r = Math.Max(1, (int)MathF.Ceiling(sigma * 3f));
        var k = new float[2 * r + 1];
        float sum = 0f;
        for (int i = -r; i <= r; i++) sum += k[i + r] = MathF.Exp(-i * i / (2f * sigma * sigma));
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new float[w * h];
        var dst = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float a = 0f;
                for (int i = -r; i <= r; i++) a += src[y * w + Wrap(x + i, w)] * k[i + r];
                tmp[y * w + x] = a;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float a = 0f;
                for (int i = -r; i <= r; i++) a += tmp[Wrap(y + i, h) * w + x] * k[i + r];
                dst[y * w + x] = a;
            }
        return dst;
    }

    private static float ToLinear(byte b)
    {
        float c = b / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }
}
