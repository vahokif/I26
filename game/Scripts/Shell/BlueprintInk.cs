using System.Runtime.CompilerServices;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The car blueprints (the melee forms' front and side views) on the port's own paper (the drawn host form). The art carries the paper
/// it was drawn on round its drawing, and the original puts it down whole on a form of that same paper, so its faint strokes and its
/// shading show as tones a little darker than the paper. Here the sheet under it is another paper, so each pixel darkens the sheet by
/// what it darkens the art's own paper about it (its colour over that paper's, channel by channel): the strokes, faint or solid, and
/// the shading keep their tones, and the art's paper, within its own grain, leaves the sheet as it is. Pure: no engine.
/// </summary>
public static class BlueprintInk
{
    /// <summary>How far round a pixel the art's own paper is looked for (the paper's mottle is wider, a stroke narrower).</summary>
    public const int Reach = 6;
    /// <summary>Brightness against the paper about it (0..1): above Paper the art's own paper and its grain, below Stroke wholly a stroke, between them partly.</summary>
    public const float Paper = 0.95f, Stroke = 0.85f;
    /// <summary>How much darker than the art's typical paper a pixel may be and still be paper (brightness, 0..255).</summary>
    private const float PaperBand = 12f;

    private static float Lum(float r, float g, float b) => r * 0.299f + g * 0.587f + b * 0.114f;

    /// <summary>
    /// The art's own paper about each pixel (r, g, b per pixel): the mean of the paper-bright pixels within <see cref="Reach"/>
    /// (those no more than a little darker than the art's typical paper), or the art's paper overall where there are none.
    /// </summary>
    public static float[] PaperUnder(byte[] pixels, bool[] opaque, int w, int h, byte[] palette)
    {
        int n = w * h;
        var lum = new float[n];
        var hist = new int[256];
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (!opaque[i]) continue;
            int p = pixels[i] * 3;
            lum[i] = Lum(palette[p], palette[p + 1], palette[p + 2]);
            hist[Math.Clamp((int)lum[i], 0, 255)]++;
            count++;
        }
        var paper = new float[n * 3];
        if (count == 0) return paper;
        int median = 0;
        for (int seen = 0; median < 256 && (seen += hist[median]) * 2 < count; median++) { }
        float floor = median - PaperBand;
        // summed tables of the paper pixels' channels and count, for the box about each pixel
        var sum = new double[4][];
        for (int k = 0; k < 4; k++) sum[k] = new double[(w + 1) * (h + 1)];
        double gr = 0, gg = 0, gb = 0, gn = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, p = pixels[i] * 3;
                bool isPaper = opaque[i] && lum[i] >= floor;
                double r = isPaper ? palette[p] : 0, g = isPaper ? palette[p + 1] : 0, b = isPaper ? palette[p + 2] : 0, c = isPaper ? 1 : 0;
                gr += r; gg += g; gb += b; gn += c;
                int o = (y + 1) * (w + 1) + x + 1, up = y * (w + 1) + x + 1, left = (y + 1) * (w + 1) + x, diag = y * (w + 1) + x;
                sum[0][o] = sum[0][up] + sum[0][left] - sum[0][diag] + r;
                sum[1][o] = sum[1][up] + sum[1][left] - sum[1][diag] + g;
                sum[2][o] = sum[2][up] + sum[2][left] - sum[2][diag] + b;
                sum[3][o] = sum[3][up] + sum[3][left] - sum[3][diag] + c;
            }
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - Reach), y1 = Math.Min(h, y + Reach + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - Reach), x1 = Math.Min(w, x + Reach + 1), i = (y * w + x) * 3;
                double Box(double[] s) => s[y1 * (w + 1) + x1] - s[y0 * (w + 1) + x1] - s[y1 * (w + 1) + x0] + s[y0 * (w + 1) + x0];
                double c = Box(sum[3]);
                if (c > 0) { paper[i] = (float)(Box(sum[0]) / c); paper[i + 1] = (float)(Box(sum[1]) / c); paper[i + 2] = (float)(Box(sum[2]) / c); }
                else if (gn > 0) { paper[i] = (float)(gr / gn); paper[i + 1] = (float)(gg / gn); paper[i + 2] = (float)(gb / gn); }
            }
        }
        return paper;
    }

    /// <summary>
    /// How much of a pixel's darkening against its paper is put down (0..1): none within the paper's grain (brighter than
    /// <see cref="Paper"/> of it), all of it from <see cref="Stroke"/> down, in between in proportion.
    /// </summary>
    public static float Weight(float lum, float paperLum) =>
        paperLum <= 0 ? 1f : Math.Clamp((Paper - lum / paperLum) / (Paper - Stroke), 0f, 1f);

    /// <summary>
    /// The art over the sheet: <paramref name="under"/> (the sheet's palette indices under the art, w x h) with each of the art's
    /// pixels darkening it as it darkens its own paper, in the palette's nearest colours. Transparent pixels and the art's paper
    /// leave the sheet's.
    /// </summary>
    /// <param name="paper">The art's <see cref="PaperUnder"/>, when it is kept; <paramref name="near"/> the palette's nearest colours found so far.</param>
    public static byte[] Composite(byte[] pixels, bool[] opaque, int w, int h, byte[] under, byte[] palette, float[]? paper = null, Dictionary<int, byte>? near = null)
    {
        paper ??= PaperUnder(pixels, opaque, w, h, palette);
        near ??= [];
        var outp = (byte[])under.Clone();
        for (int i = 0; i < w * h; i++)
        {
            if (!opaque[i]) continue;
            int p = pixels[i] * 3, u = under[i] * 3;
            float pr = paper[i * 3], pg = paper[i * 3 + 1], pb = paper[i * 3 + 2];
            float a = Weight(Lum(palette[p], palette[p + 1], palette[p + 2]), Lum(pr, pg, pb));
            if (a <= 0) continue;
            float Ch(int c, float pc) => palette[u + c] * (1 - a + a * Math.Min(1f, pc > 0 ? palette[p + c] / pc : 1f));
            int r = (int)MathF.Round(Ch(0, pr)), g = (int)MathF.Round(Ch(1, pg)), b = (int)MathF.Round(Ch(2, pb));
            int key = r << 16 | g << 8 | b;
            if (!near.TryGetValue(key, out var idx)) near[key] = idx = Nearest(palette, r, g, b);
            outp[i] = idx;
        }
        return outp;
    }

    private static byte Nearest(byte[] pal, int r, int g, int b)
    {
        int best = 0, bd = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b, d = dr * dr + dg * dg + db * db;
            if (d < bd) { bd = d; best = i; }
        }
        return (byte)best;
    }

    // the art's paper per sprite and palette, and each palette's nearest colours, kept while they live (the form redraws on every change)
    private sealed record Kept(byte[] Palette, float[] Paper);
    private static readonly ConditionalWeakTable<Sprite, Kept> Papers = [];
    private static readonly ConditionalWeakTable<byte[], Dictionary<int, byte>> Nears = [];

    /// <summary>The sprite over the frame's sheet at (x, y), as <see cref="Composite"/> has it (clipped to the frame).</summary>
    public static void Draw(ShellFrame frame, Sprite? s, int x, int y)
    {
        if (s is null) return;
        var pal = frame.Palette;
        if (!Papers.TryGetValue(s, out var kept) || kept.Palette != pal)
        {
            kept = new Kept(pal, PaperUnder(s.Pixels, s.Opaque, s.Width, s.Height, pal));
            Papers.AddOrUpdate(s, kept);
        }
        var under = new byte[s.Width * s.Height];
        var now = frame.Pixels;
        for (int r = 0; r < s.Height; r++)
            for (int c = 0; c < s.Width; c++)
            {
                int xx = x + c, yy = y + r;
                if (xx >= 0 && xx < ShellFrame.W && yy >= 0 && yy < ShellFrame.H) under[r * s.Width + c] = now[yy * ShellFrame.W + xx];
            }
        var done = Composite(s.Pixels, s.Opaque, s.Width, s.Height, under, pal, kept.Paper, Nears.GetValue(pal, _ => []));
        for (int r = 0; r < s.Height; r++)
            for (int c = 0; c < s.Width; c++)
            {
                int i = r * s.Width + c;
                if (done[i] != under[i]) frame.Fill(x + c, y + r, 1, 1, done[i]);
            }
    }
}
