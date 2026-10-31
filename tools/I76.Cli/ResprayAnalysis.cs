using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>The measurements behind the respray: a model's schemes, their colour clusters, and the mask against cross-scheme truth.</summary>
public static class ResprayAnalysis
{
    public sealed record Rgb(int W, int H, byte[] Data, bool[] Key);

    public static Rgb? LoadRgb(TextureResolver res, string member)
    {
        if (res.Load(member) is not { } t) return null;
        var a = TextureReader.ToRgba(t, null, false);
        var d = new byte[t.Width * t.Height * 3];
        for (int i = 0; i < t.Width * t.Height; i++) { d[i * 3] = a[i * 4]; d[i * 3 + 1] = a[i * 4 + 1]; d[i * 3 + 2] = a[i * 4 + 2]; }
        return new Rgb(t.Width, t.Height, d, t.Indices.Select(x => x == TextureReader.TransparentIndex).ToArray());
    }

    public sealed record SchemeResult(string Vtf, ResprayPaint.Stats Stats, ResprayPaint.PaintRef Paint, ResprayPaint.PaintRef? Second, double SecondMass,
        int Textures, double TruthPaint, double Precision, double Recall, double UnchangedPaintMass);

    /// <summary>Body textures per scheme (parts of the car's own object code, mode and cutout as the game draws them).</summary>
    public static List<SchemeResult> Analyse(AssetVfs vfs, ModelSchemes ms)
    {
        var res = new TextureResolver(vfs);
        var cache = new Dictionary<string, Rgb?>(StringComparer.OrdinalIgnoreCase);
        Rgb? Get(string m) { if (!cache.TryGetValue(m, out var r)) cache[m] = r = LoadRgb(res, m); return r; }
        var paintSlots = ms.PaintSlots().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var o = new List<SchemeResult>();
        foreach (var v in ms.Vtfs)
        {
            var body = ResprayImg.BodyTextures(ms, v);
            var sp = PaintOf(res, ms, v);
            var st = sp.Stats; var pr = sp.Primary; var sec = sp.Secondary;
            int nt = sp.Body.Count;
            double secMass = 0;
            if (sec is { } sq) secMass = sq.Chromatic ? 0 : 0;
            // truth: texels that differ from another scheme's, over the paint slots the schemes share the shape of
            double truth = 0, tp = 0, fp = 0, fn = 0, total = 0, unchangedPaint = 0, unchangedTotal = 0;
            foreach (var b in body)
            {
                if (b.Cutout || Get(b.Member) is not { } t) continue;
                int n = t.W * t.H;
                var w = ResprayPaint.Mask(t.Data, n, pr);
                if (sec is { } s2) { var w2 = ResprayPaint.Mask(t.Data, n, s2); for (int i = 0; i < n; i++) w[i] = MathF.Max(w[i], w2[i]); }
                if (!paintSlots.Contains(b.Slot))
                {
                    unchangedTotal += n; for (int i = 0; i < n; i++) if (w[i] > 0.5f) unchangedPaint++;
                    continue;
                }
                var others = ms.Vtfs.Where(x => x != v).Select(x => ms.Resolved[x].GetValueOrDefault(b.Slot)).Where(x => x is not null && !x.Equals(b.Member, StringComparison.OrdinalIgnoreCase))
                    .Select(x => Get(x!)).Where(x => x is not null && x.W == t.W && x.H == t.H).ToList();
                if (others.Count == 0) continue;
                for (int i = 0; i < n; i++)
                {
                    bool differs = others.Any(q => Math.Abs(q!.Data[i * 3] - t.Data[i * 3]) + Math.Abs(q.Data[i * 3 + 1] - t.Data[i * 3 + 1]) + Math.Abs(q.Data[i * 3 + 2] - t.Data[i * 3 + 2]) > 36);
                    bool m = w[i] > 0.5f;
                    total++; if (differs) truth++;
                    if (m && differs) tp++; else if (m) fp++; else if (differs) fn++;
                }
            }
            o.Add(new SchemeResult(v, st, pr, sec, secMass, nt, total > 0 ? truth / total : double.NaN, tp + fp > 0 ? tp / (tp + fp) : double.NaN,
                tp + fn > 0 ? tp / (tp + fn) : double.NaN, unchangedTotal > 0 ? unchangedPaint / unchangedTotal : double.NaN));
        }
        return o;
    }

    public static readonly ResprayPaint.Target[] Targets =
    [
        new("red", 0.78f, 0.04f, 0.05f), new("white", 0.92f, 0.92f, 0.90f), new("black", 0.05f, 0.05f, 0.06f), new("metallic blue", 0.10f, 0.25f, 0.85f, 1.35f),
    ];

    /// <summary>The paint of a scheme: its body textures (those not drawn keyed), the colour statistics, the primary and secondary paint.</summary>
    public sealed record SchemePaint(List<(string Member, Rgb Tex)> Body, ResprayPaint.Stats Stats, ResprayPaint.PaintRef Primary, ResprayPaint.PaintRef? Secondary);

    public static SchemePaint PaintOf(TextureResolver res, ModelSchemes ms, string vtf)
    {
        var body = new List<(string, Rgb)>();
        var st = new ResprayPaint.Stats();
        foreach (var b in ResprayImg.BodyTextures(ms, vtf))
        {
            if (b.Cutout || LoadRgb(res, b.Member) is not { } t) continue;
            body.Add((b.Member, t)); st.Add(t.Data, t.W * t.H);
        }
        var (pr, sec) = ResprayPaint.Scheme(st);
        return new SchemePaint(body, st, pr, sec);
    }

    /// <summary>The scheme's body textures recoloured: the primary paint to <paramref name="primary"/>, the secondary (if any and asked) to <paramref name="secondary"/>.</summary>
    public static Dictionary<string, Rgb> Recolour(SchemePaint sp, ResprayPaint.Target primary, ResprayPaint.Target? secondary = null)
    {
        var o = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase);
        foreach (var (m, t) in sp.Body)
        {
            int n = t.W * t.H;
            var d = ResprayPaint.Recolour(t.Data, n, ResprayPaint.Mask(t.Data, n, sp.Primary), sp.Primary, primary);
            if (secondary is { } s2 && sp.Secondary is { } sr) d = ResprayPaint.Recolour(d, n, ResprayPaint.Mask(t.Data, n, sr), sr, s2);
            o[m] = t with { Data = d };
        }
        return o;
    }
}
