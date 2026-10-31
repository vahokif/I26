using Godot;

namespace I76.Game.World;

/// <summary>
/// The modern terrain's ground as procedural noise fitted to the mission's
/// ground texture, so it never repeats (the texture repeats every 40 units
/// and the tiling shows from afar). The ground textures are noise to begin
/// with; what is measured from one:
/// <list type="bullet">
/// <item>its colours by brightness: the pixels sorted by luminance and
/// averaged in equal bins, a palette the shader indexes with a uniformly
/// distributed noise value, which reproduces the texture's histogram and how
/// its colour shifts from dark to light;</item>
/// <item>its detail at each scale: the standard deviation of each band of a
/// Laplacian pyramid of its luminance, which become the amplitudes of the
/// fBm octaves whose cells are 1, 2, 4… texels wide; two octaves past the
/// texture's own size are extrapolated from the trend of the last two, so
/// there is variation broader than one repeat.</item>
/// </list>
/// terrain_modern.gdshader draws it (procedural = true), one texture read
/// of a baked noise tile (NoiseTexture) per octave.
/// </summary>
public static class GroundNoise
{
    public const int PaletteSize = 32;
    public const int MaxOctaves = 12;

    public sealed record Fit(Color[] Palette, float[] Amplitudes, float FinestCell, float PaletteSlope);

    /// <param name="image">The ground texture.</param>
    /// <param name="repeatUnits">The world units one repeat of it covers.</param>
    public static Fit Measure(Image image, float repeatUnits)
    {
        var img = (Image)image.Duplicate();
        if (img.IsCompressed()) img.Decompress();
        img.ClearMipmaps();
        img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        int n = w * h;
        var lum = new float[n];
        for (int i = 0; i < n; i++)
            lum[i] = (0.299f * data[i * 4] + 0.587f * data[i * 4 + 1] + 0.114f * data[i * 4 + 2]) / 255f;

        // the palette: equal-count luminance bins, each its pixels' mean colour
        var order = Enumerable.Range(0, n).OrderBy(i => lum[i]).ToArray();
        var palette = new Color[PaletteSize];
        for (int k = 0; k < PaletteSize; k++)
        {
            int a = k * n / PaletteSize, b = (k + 1) * n / PaletteSize;
            float r = 0, g = 0, bl = 0;
            for (int j = a; j < b; j++) { int i = order[j]; r += data[i * 4]; g += data[i * 4 + 1]; bl += data[i * 4 + 2]; }
            float c = 255f * System.Math.Max(1, b - a);
            palette[k] = new Color(r / c, g / c, bl / c);
        }
        float lo = Lum(palette[0]), hi = Lum(palette[^1]);

        // the detail per octave: each Laplacian band's spread (the texture tiles, so the pyramid wraps)
        int size = System.Math.Min(w, h);
        var cur = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) cur[y * size + x] = lum[y * w + x];
        var amps = new List<float>();
        while (size > 1 && amps.Count < MaxOctaves - 2)
        {
            int half = size / 2;
            var down = new float[half * half];
            for (int y = 0; y < half; y++)
                for (int x = 0; x < half; x++)
                    down[y * half + x] = 0.25f * (cur[2 * y * size + 2 * x] + cur[2 * y * size + 2 * x + 1] + cur[(2 * y + 1) * size + 2 * x] + cur[(2 * y + 1) * size + 2 * x + 1]);
            double s = 0, s2 = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double band = cur[y * size + x] - Upsample(down, half, x * 0.5f - 0.25f, y * 0.5f - 0.25f);
                    s += band; s2 += band * band;
                }
            int m = size * size;
            amps.Add((float)System.Math.Sqrt(System.Math.Max(0, s2 / m - s / m * (s / m))));
            cur = down;
            size = half;
        }
        // past the texture's own size (which it cannot show): two more octaves on the trend of its largest, never growing
        if (amps.Count >= 2)
        {
            float ratio = System.Math.Min(1f, amps[^1] / System.Math.Max(amps[^2], 1e-5f));
            for (int k = 0; k < 2; k++) amps.Add(amps[^1] * ratio);
        }
        return new Fit(palette, amps.ToArray(), repeatUnits / w, hi - lo);
    }

    private static float Lum(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;

    /// <summary>Bilinear read of a wrapping square grid.</summary>
    private static float Upsample(float[] g, int n, float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float At(int xx, int yy) => g[((yy % n + n) % n) * n + ((xx % n + n) % n)];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    public const int NoiseCells = 64, NoiseTexels = 8;
    private static ImageTexture? _noise;

    /// <summary>
    /// One tileable tile of value noise (quintic, NoiseCells lattice cells a
    /// side, NoiseTexels texels a cell), mipmapped, RGBA8: R the value in -1..1
    /// as 0.5 + v/2, G and B its gradient per cell as 0.5 + g/8 (at most about 3.75). The terrain shader reads each octave of
    /// the ground and the bump from it, at its own scale and turn, instead
    /// of hashing noise per pixel.
    /// </summary>
    public static ImageTexture NoiseTexture()
    {
        if (_noise is not null) return _noise;
        var rng = new System.Random(76);
        var lat = new float[NoiseCells * NoiseCells];
        for (int i = 0; i < lat.Length; i++) lat[i] = (float)(rng.NextDouble() * 2 - 1);
        float L(int x, int y) => lat[(y & (NoiseCells - 1)) * NoiseCells + (x & (NoiseCells - 1))];
        int size = NoiseCells * NoiseTexels;
        var px = new float[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // the texel's centre in cells
                float gx = (x + 0.5f) / NoiseTexels, gy = (y + 0.5f) / NoiseTexels;
                int ix = (int)MathF.Floor(gx), iy = (int)MathF.Floor(gy);
                float fx = gx - ix, fy = gy - iy;
                float ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10), uy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
                float dux = 30 * fx * fx * (fx * (fx - 2) + 1), duy = 30 * fy * fy * (fy * (fy - 2) + 1);
                float a = L(ix, iy), b = L(ix + 1, iy), c = L(ix, iy + 1), d = L(ix + 1, iy + 1);
                float k1 = b - a, k2 = c - a, k3 = a - b - c + d;
                int o = (y * size + x) * 4;
                px[o] = a + k1 * ux + k2 * uy + k3 * ux * uy;
                px[o + 1] = dux * (k1 + k3 * uy);
                px[o + 2] = duy * (k2 + k3 * ux);
                px[o + 3] = 1;
            }
        // RGBA8, not half floats: a 32-bit texel filters at full rate (terrain_modern.gdshader, noise_decode)
        var b8 = new byte[size * size * 4];
        static byte Q(float v) => (byte)System.Math.Clamp(System.Math.Round(v * 255), 0, 255);
        for (int i = 0; i < size * size; i++)
        {
            b8[i * 4] = Q(px[i * 4] * 0.5f + 0.5f);
            b8[i * 4 + 1] = Q(px[i * 4 + 1] / 8 + 0.5f);
            b8[i * 4 + 2] = Q(px[i * 4 + 2] / 8 + 0.5f);
            b8[i * 4 + 3] = 255;
        }
        var img = Image.CreateFromData(size, size, false, Image.Format.Rgba8, b8);
        img.GenerateMipmaps();
        return _noise = ImageTexture.CreateFromImage(img);
    }

    /// <summary>The baked noise tile for the terrain shader (the bump uses it with or without a fit).</summary>
    public static void ApplyNoise(ShaderMaterial m)
    {
        m.SetShaderParameter("noise_tex", NoiseTexture());
        m.SetShaderParameter("noise_cells", (float)NoiseCells);
        m.SetShaderParameter("noise_texels", (float)NoiseTexels);
    }

    public static void Apply(ShaderMaterial m, Fit f)
    {
        var img = Image.CreateEmpty(PaletteSize, 1, false, Image.Format.Rgb8);
        for (int k = 0; k < PaletteSize; k++) img.SetPixel(k, 0, f.Palette[k]);
        m.SetShaderParameter("palette", ImageTexture.CreateFromImage(img));
        m.SetShaderParameter("procedural", true);
        var amps = new float[MaxOctaves];
        System.Array.Copy(f.Amplitudes, amps, System.Math.Min(MaxOctaves, f.Amplitudes.Length));
        m.SetShaderParameter("octave_amp", amps);
        m.SetShaderParameter("octaves", f.Amplitudes.Length);
        m.SetShaderParameter("finest_cell", f.FinestCell);
        m.SetShaderParameter("palette_slope", f.PaletteSlope);
    }
}
