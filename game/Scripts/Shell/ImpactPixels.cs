using Godot;

namespace I76.Game.Shell;

/// <summary>
/// An ImpactLabels.Spec drawn into the shell's 640x480 indexed frame, for the 1997 look and for machines whose layer has no
/// Impact: the glyph outlines of the same Impact (HdAssets.Impact), slanted and placed as ImpactLayer places them, filled
/// without anti-aliasing (3x3 samples a pixel, half or more covered is ink), as the originals' sprites are. Null without Impact.
/// </summary>
public static class ImpactPixels
{
    /// <summary>The ink's pixels of the label in a box of w by h (row-major, true = ink), or null when there is no Impact.</summary>
    public static bool[]? Mask(ImpactLabels.Spec spec, int w, int h)
    {
        if (HdAssets.Impact(TextServer.FontAntialiasing.None, TextServer.Hinting.None) is not { } font) return null;
        var rids = font.GetRids();
        if (rids.Count == 0) return null;
        var ts = TextServerManager.GetPrimaryInterface();
        int px = Math.Max(1, (int)MathF.Round(spec.Size));
        float k = spec.Size / px;
        float Width(string t) => font.GetStringSize(t, HorizontalAlignment.Left, -1, px).X;
        float Adv(int i) => (i < spec.Text.Length ? Width(spec.Text[..(i + 1)]) - Width(spec.Text[i].ToString()) : Width(spec.Text)) * k
            + i * spec.Track + spec.Text[..i].Count(c => c == ' ') * spec.WordSpace;
        float pen = spec.Centre ? (w - Adv(spec.Text.Length)) / 2 : spec.X;
        float tan = MathF.Tan(spec.Slant * MathF.PI / 180f);
        const int S = 3;
        var cover = new int[w * h];
        for (int i = 0; i < spec.Text.Length; i++)
        {
            if (spec.Text[i] == ' ') continue;
            long gi = ts.FontGetGlyphIndex(rids[0], px, spec.Text[i], 0);
            var g = ts.FontGetGlyphContours(rids[0], px, gi);
            if (g["points"].As<Vector3[]>() is not { Length: > 0 } pts || g["contours"].As<int[]>() is not { } ends) continue;
            var polys = new List<List<Vector2>>();
            int start = 0;
            foreach (int end in ends)
            {
                polys.Add(Flatten(pts, start, end, spec.Baseline, tan, pen + Adv(i), k));
                start = end + 1;
            }
            Fill(polys, cover, w, h, S);
        }
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = cover[i] * 2 >= S * S;
        return mask;
    }

    /// <summary>One contour of TrueType points (z: 1 on the curve, 0 a quadratic control, 2 cubic) as a polygon in box pixels (y down).</summary>
    private static List<Vector2> Flatten(Vector3[] pts, int from, int to, float baseline, float tan, float x0, float k)
    {
        var raw = new List<(Vector2 P, bool On)>();
        for (int i = from; i <= to; i++) raw.Add((new Vector2(pts[i].X, pts[i].Y), pts[i].Z > 0.5f));
        // between two controls lies an implied on-curve point
        var all = new List<(Vector2 P, bool On)>();
        for (int i = 0; i < raw.Count; i++)
        {
            var a = raw[i]; var b = raw[(i + 1) % raw.Count];
            all.Add(a);
            if (!a.On && !b.On) all.Add(((a.P + b.P) / 2, true));
        }
        int s0 = all.FindIndex(p => p.On);
        if (s0 < 0) return [];
        var o = new List<Vector2>();
        Vector2 Place(Vector2 p)
        {
            // font units are pixels at the integer size, y down (up is negative); the slant leans the letter about its baseline
            float y = baseline + p.Y * k;
            return new Vector2(x0 + p.X * k + tan * (baseline - y), y);
        }
        int n = all.Count;
        for (int j = 0; j < n; j++)
        {
            var cur = all[(s0 + j) % n]; var nxt = all[(s0 + j + 1) % n];
            if (cur.On) o.Add(Place(cur.P));
            if (!nxt.On && cur.On)
            {
                var ctl = nxt.P; var end = all[(s0 + j + 2) % n].P;
                for (int t = 1; t < 8; t++)
                {
                    float u = t / 8f;
                    o.Add(Place((1 - u) * (1 - u) * cur.P + 2 * u * (1 - u) * ctl + u * u * end));
                }
            }
        }
        return o;
    }

    /// <summary>Non-zero scanline fill at S x S samples a pixel, counting the samples inside into cover.</summary>
    private static void Fill(List<List<Vector2>> polys, int[] cover, int w, int h, int S)
    {
        for (int sy = 0; sy < h * S; sy++)
        {
            float y = (sy + 0.5f) / S;
            var xs = new List<(float X, int Dir)>();
            foreach (var poly in polys)
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                    if ((a.Y <= y) == (b.Y <= y)) continue;
                    xs.Add((a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X), b.Y > a.Y ? 1 : -1));
                }
            xs.Sort((p, q) => p.X.CompareTo(q.X));
            int wind = 0;
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                wind += xs[i].Dir;
                if (wind == 0) continue;
                int x0 = (int)MathF.Ceiling(xs[i].X * S - 0.5f), x1 = (int)MathF.Ceiling(xs[i + 1].X * S - 0.5f);
                for (int sx = Math.Max(0, x0); sx < Math.Min(w * S, x1); sx++) cover[(sy / S) * w + sx / S]++;
            }
        }
    }
}
