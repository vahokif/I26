namespace I76.Formats.Imaging;

/// <summary>
/// A texture's window mask (a texel each glass or not) as straight-edged shapes: the outline of every window traced along the texel
/// edges, its stair-steps straightened (Douglas–Peucker within <c>Tolerance</c> texels: a pane is painted as a polygon, the steps are
/// the 128-texel grid's), then filled antialiased at <c>Scale</c> times the texture's size. The outline keeps to the texel boundary
/// between glass and frame, so a pillar a texel wide stays a texel wide.
/// </summary>
public static class GlassShape
{
    public const int Scale = 4;
    public const float Tolerance = 1.5f;
    public const float MinArea = 4f;

    /// <summary>Coverage 0..255 per texel of a (w·Scale)×(h·Scale) image (row-major) for a w×h mask (non-zero: glass).</summary>
    public static byte[] Rasterize(byte[] mask, int w, int h, int scale = Scale, float tolerance = Tolerance)
    {
        var loops = Shapes(mask, w, h, tolerance);
        int W = w * scale, H = h * scale;
        var cover = new float[W * H];
        // 4x4 samples a pixel; each sample row's crossings with every outline edge, filled even-odd
        const int sub = 4;
        var xs = new List<float>();
        for (int sy = 0; sy < H * sub; sy++)
        {
            float y = (sy + 0.5f) / (scale * sub);
            xs.Clear();
            foreach (var l in loops)
                for (int i = 0; i < l.Count; i++)
                {
                    var (ax, ay) = l[i];
                    var (bx, by) = l[(i + 1) % l.Count];
                    if ((ay <= y) == (by <= y)) continue;
                    xs.Add(ax + (y - ay) / (by - ay) * (bx - ax));
                }
            xs.Sort();
            int row = sy / sub;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                // the span [x0, x1) in sample columns
                int c0 = Math.Max(0, (int)MathF.Ceiling(xs[k] * scale * sub - 0.5f)), c1 = Math.Min(W * sub, (int)MathF.Ceiling(xs[k + 1] * scale * sub - 0.5f));
                for (int c = c0; c < c1; c++) cover[row * W + c / sub] += 1f / (sub * sub);
            }
        }
        var o = new byte[W * H];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)Math.Clamp((int)MathF.Round(cover[i] * 255), 0, 255);
        return o;
    }

    /// <summary>The reach of <see cref="Field"/>, in texels each side of the edge.</summary>
    public const float FieldReach = 6f;

    /// <summary>
    /// The glass as a distance field at <c>Scale</c> times the texture's size: 0.5 on the straightened edge, rising to 1 a
    /// <see cref="FieldReach"/> texels inside and falling to 0 as far outside (0.5 - signed distance / (2·reach), inside negative).
    /// The shader cuts it at one half for the glass and reads the band just outside it for a straight trim line.
    /// </summary>
    public static byte[] Field(byte[] mask, int w, int h, int scale = Scale, float tolerance = Tolerance)
    {
        var cover = Rasterize(mask, w, h, scale, tolerance);
        var loops = Shapes(mask, w, h, tolerance);
        int W = w * scale, H = h * scale;
        var d = new float[W * H];
        Array.Fill(d, FieldReach);
        int pad = (int)MathF.Ceiling(FieldReach * scale) + 1;
        foreach (var l in loops)
            for (int i = 0; i < l.Count; i++)
            {
                var (ax, ay) = l[i];
                var (bx, by) = l[(i + 1) % l.Count];
                int x0 = Math.Max(0, (int)(MathF.Min(ax, bx) * scale) - pad), x1 = Math.Min(W - 1, (int)(MathF.Max(ax, bx) * scale) + pad);
                int y0 = Math.Max(0, (int)(MathF.Min(ay, by) * scale) - pad), y1 = Math.Min(H - 1, (int)(MathF.Max(ay, by) * scale) + pad);
                float ex = bx - ax, ey = by - ay, len2 = MathF.Max(ex * ex + ey * ey, 1e-9f);
                for (int py = y0; py <= y1; py++)
                    for (int px = x0; px <= x1; px++)
                    {
                        float x = (px + 0.5f) / scale, y = (py + 0.5f) / scale;
                        float t = Math.Clamp(((x - ax) * ex + (y - ay) * ey) / len2, 0f, 1f);
                        float dd = MathF.Sqrt(Sq(x - ax - t * ex) + Sq(y - ay - t * ey));
                        if (dd < d[py * W + px]) d[py * W + px] = dd;
                    }
            }
        var o = new byte[W * H];
        for (int i = 0; i < o.Length; i++)
        {
            float sd = cover[i] >= 128 ? -d[i] : d[i];
            o[i] = (byte)Math.Clamp((int)MathF.Round((0.5f - sd / (2 * FieldReach)) * 255), 0, 255);
        }
        return o;
    }

    /// <summary>The straightened outlines <see cref="Rasterize"/> fills (texel units): a speck of glass or a pinhole in a pane (under
    /// <see cref="MinArea"/> texels) is the segmentation's noise and left out (a hole so left out is glass).</summary>
    public static List<List<(float X, float Y)>> Shapes(byte[] mask, int w, int h, float tolerance = Tolerance)
    {
        var loops = Outlines(Clean(mask, w, h), w, h).Where(l => MathF.Abs(Area(l)) >= MinArea).ToList();
        // the outer outlines' turning sense (the largest outline is one)
        float outerSign = loops.Count == 0 ? 1 : MathF.Sign(Area(loops.MaxBy(l => MathF.Abs(Area(l)))!));
        var res = new List<List<(float X, float Y)>>();
        foreach (var l in loops)
        {
            var st = Straighten(l, tolerance);
            if (st.Count < 3) continue;
            // a pane is painted convex: an outline nearly as big as its hull (a step in its edge, a notch, a wedge cut in by a dark line
            // across the glass) is drawn as the hull; a shape well short of it (an L, a wrap-round) keeps its own
            if (MathF.Sign(Area(l)) == outerSign && Hull(st) is { Count: >= 3 } hull && MathF.Abs(Area(hull)) <= HullSlack * MathF.Abs(Area(st)))
            {
                if (MathF.Sign(Area(hull)) != outerSign) hull.Reverse();
                st = hull;
            }
            res.Add(st);
        }
        return res;
    }

    /// <summary>How much bigger than a pane's outline its convex hull may be for the hull to stand for it.</summary>
    public const float HullSlack = 1.12f;

    /// <summary>A polygon's convex hull (Andrew's monotone chain).</summary>
    public static List<(float X, float Y)> Hull(List<(float X, float Y)> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<(float X, float Y)>();
        foreach (var q in p) { while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(q); }
        var upper = new List<(float X, float Y)>();
        for (int i = p.Count - 1; i >= 0; i--) { var q = p[i]; while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(q); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>
    /// The mask with its small notches filled and small spikes cut (what a 3x3 closing adds and a 3x3 opening takes away, but only in
    /// patches of at most <paramref name="small"/> texels and three either way): a bite out of a pane's edge goes, a pillar between two
    /// panes, a line however short, stays.
    /// </summary>
    public static byte[] Clean(byte[] mask, int w, int h, int small = 6)
    {
        bool[] M(byte[] m) => m.Select(v => v != 0).ToArray();
        bool[] Grow(bool[] m, bool value)
        {
            // dilate the value: a texel takes it if any of its 3x3 has it
            var o = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool any = false;
                    for (int dy = -1; dy <= 1 && !any; dy++)
                        for (int dx = -1; dx <= 1 && !any; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            if (m[yy * w + xx] == value) any = true;
                        }
                    o[y * w + x] = any ? value : !value;
                }
            return o;
        }
        var g = M(mask);
        var closed = Grow(Grow(g, true), false);
        Apply(g, closed, true);
        var opened = Grow(Grow(g, false), true);
        Apply(g, opened, false);
        return g.Select(b => b ? (byte)255 : (byte)0).ToArray();

        // the texels the morphology turned to `to`, taken over only in small connected patches
        void Apply(bool[] cur, bool[] morph, bool to)
        {
            var seen = new bool[w * h];
            var st = new Stack<int>();
            for (int s0 = 0; s0 < w * h; s0++)
            {
                if (seen[s0] || cur[s0] == to || morph[s0] != to) continue;
                var patch = new List<int>();
                seen[s0] = true; st.Push(s0);
                while (st.Count > 0)
                {
                    int p = st.Pop(); patch.Add(p);
                    int x = p % w, y = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int q = yy * w + xx;
                            if (!seen[q] && cur[q] != to && morph[q] == to) { seen[q] = true; st.Push(q); }
                        }
                }
                // small and compact (no more than three texels either way): a pillar or a thin strip, however short, is a line, not a bite
                int x0 = patch.Min(p => p % w), x1 = patch.Max(p => p % w), y0 = patch.Min(p => p / w), y1 = patch.Max(p => p / w);
                if (patch.Count <= small && x1 - x0 < 3 && y1 - y0 < 3) foreach (int p in patch) cur[p] = to;
            }
        }
    }

    /// <summary>The closed outlines of the glass, along texel edges (corner coordinates), each turning the same way round its region.</summary>
    public static List<List<(float X, float Y)>> Outlines(byte[] mask, int w, int h)
    {
        bool G(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x] != 0;
        // directed edges between glass and not, glass on one side throughout; keyed by start corner
        var from = new Dictionary<(int, int), List<(int, int)>>();
        void Edge(int x0, int y0, int x1, int y1) { if (!from.TryGetValue((x0, y0), out var l)) from[(x0, y0)] = l = []; l.Add((x1, y1)); }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!G(x, y)) continue;
                if (!G(x, y - 1)) Edge(x + 1, y, x, y);
                if (!G(x, y + 1)) Edge(x, y + 1, x + 1, y + 1);
                if (!G(x - 1, y)) Edge(x, y, x, y + 1);
                if (!G(x + 1, y)) Edge(x + 1, y + 1, x + 1, y);
            }
        var loops = new List<List<(float, float)>>();
        while (from.Count > 0)
        {
            var start = from.Keys.First();
            var loop = new List<(float, float)>();
            var at = start;
            while (from.TryGetValue(at, out var outs))
            {
                loop.Add((at.Item1, at.Item2));
                var next = outs[^1];
                outs.RemoveAt(outs.Count - 1);
                if (outs.Count == 0) from.Remove(at);
                at = next;
                if (at == start) break;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>
    /// A traced outline made straight: its true corners (where two straight runs of <paramref name="minRun"/> texels or more meet: a
    /// pane's corner, a pillar's end) stay exactly where they are, and between them the outline runs through the middle of each texel
    /// edge (<see cref="Midpoints"/>) and is simplified (Douglas–Peucker) so the stair-steps become straight lines.
    /// </summary>
    public static List<(float X, float Y)> Straighten(List<(float X, float Y)> loop, float tolerance, int minRun = 3)
    {
        int n = loop.Count;
        if (n < 4) return loop;
        // each texel edge's direction, and the straight runs it belongs to
        (int, int) Dir(int i) { var (ax, ay) = loop[i]; var (bx, by) = loop[(i + 1) % n]; return (Math.Sign(bx - ax), Math.Sign(by - ay)); }
        var dirs = Enumerable.Range(0, n).Select(Dir).ToArray();
        var runAfter = new int[n];
        var runBefore = new int[n];
        for (int i = 0; i < n; i++)
        {
            int k = 0; while (k < n && dirs[(i + k) % n] == dirs[i]) k++; runAfter[i] = k;
            int j = 0; while (j < n && dirs[(i - 1 - j + 2 * n) % n] == dirs[(i - 1 + n) % n]) j++; runBefore[i] = j;
        }
        // the outline through the edges' middles (point j: edge j's); at a true corner (vertex i) the middles of the two edges either
        // side of it (points i-1 and i) are kept as they are, so the corner stays square to half a texel and nothing jumps
        var mid = Midpoints(loop);
        var anchor = new bool[n];
        for (int i = 0; i < n; i++)
            if (dirs[i] != dirs[(i - 1 + n) % n] && runAfter[i] >= minRun && runBefore[i] >= minRun) { anchor[i] = true; anchor[(i - 1 + n) % n] = true; }
        int first = Array.IndexOf(anchor, true);
        if (first < 0) return Simplify(mid, tolerance);
        var res = new List<(float X, float Y)>();
        var chain = new List<(float X, float Y)> { mid[first] };
        for (int s = 1; s <= n; s++)
        {
            int i = (first + s) % n;
            chain.Add(mid[i]);
            if (!anchor[i]) continue;
            var d = Dp(chain, tolerance);
            d.RemoveAt(d.Count - 1);
            res.AddRange(d);
            chain = [mid[i]];
        }
        return res;
    }

    /// <summary>
    /// The outline through the middle of each texel edge instead of its corners: a stair-step then stands off a straight line by half as
    /// much (a steep edge across a narrow strip of texture, a side window's, would keep its teeth), while a straight run stays where it is.
    /// </summary>
    public static List<(float X, float Y)> Midpoints(List<(float X, float Y)> loop)
    {
        var o = new List<(float X, float Y)>(loop.Count);
        for (int i = 0; i < loop.Count; i++)
        {
            var (ax, ay) = loop[i];
            var (bx, by) = loop[(i + 1) % loop.Count];
            o.Add(((ax + bx) / 2, (ay + by) / 2));
        }
        return o;
    }

    /// <summary>A closed outline with its stair-steps straightened: Douglas–Peucker from its two farthest-apart corners.</summary>
    public static List<(float X, float Y)> Simplify(List<(float X, float Y)> loop, float tolerance)
    {
        if (loop.Count < 4) return loop;
        int a = 0, b = 0;
        float best = -1;
        for (int i = 0; i < loop.Count; i++)
        {
            float d = Sq(loop[i].X - loop[0].X) + Sq(loop[i].Y - loop[0].Y);
            if (d > best) { best = d; b = i; }
        }
        best = -1;
        for (int i = 0; i < loop.Count; i++)
        {
            float d = Sq(loop[i].X - loop[b].X) + Sq(loop[i].Y - loop[b].Y);
            if (d > best) { best = d; a = i; }
        }
        if (a > b) (a, b) = (b, a);
        var first = loop.GetRange(a, b - a + 1);
        var second = loop.GetRange(b, loop.Count - b).Concat(loop.GetRange(0, a + 1)).ToList();
        var res = Dp(first, tolerance);
        res.RemoveAt(res.Count - 1);
        var rest = Dp(second, tolerance);
        rest.RemoveAt(rest.Count - 1);
        res.AddRange(rest);
        return res;
    }

    private static List<(float X, float Y)> Dp(List<(float X, float Y)> pts, float tol)
    {
        if (pts.Count <= 2) return [.. pts];
        var (ax, ay) = pts[0];
        var (bx, by) = pts[^1];
        float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
        int idx = 0;
        float far = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            float d = len < 1e-6f ? MathF.Sqrt(Sq(pts[i].X - ax) + Sq(pts[i].Y - ay)) : MathF.Abs(dy * (pts[i].X - ax) - dx * (pts[i].Y - ay)) / len;
            if (d > far) { far = d; idx = i; }
        }
        if (far <= tol) return [pts[0], pts[^1]];
        var left = Dp(pts.GetRange(0, idx + 1), tol);
        var right = Dp(pts.GetRange(idx, pts.Count - idx), tol);
        left.RemoveAt(left.Count - 1);
        left.AddRange(right);
        return left;
    }

    /// <summary>A closed outline's signed area (shoelace), in square texels.</summary>
    public static float Area(List<(float X, float Y)> loop)
    {
        float a = 0;
        for (int i = 0; i < loop.Count; i++) { var (ax, ay) = loop[i]; var (bx, by) = loop[(i + 1) % loop.Count]; a += ax * by - bx * ay; }
        return a / 2;
    }

    private static float Sq(float v) => v * v;
}
