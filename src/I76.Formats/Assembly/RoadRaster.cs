using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// Paints roads into a terrain layer: for a square of the world, every road
/// quad that crosses it is rasterised in the XZ plane with its texture
/// (the engine's quad UVs and texture choice, Roads), giving an RGBA image a
/// terrain shader can blend over the ground. This is the modern way to put a
/// road on terrain (a baked splat or virtual-texture layer) instead of a
/// second surface that has to win a depth fight with the first.
///
/// Rows run along +Z (game frame), columns along +X; pixel centres sit at
/// (i + 0.5) * size / res from the origin.
/// </summary>
public static class RoadRaster
{
    /// <summary>Changes with anything that changes a layer's pixels: cached layers (BakeCache) are keyed by it.</summary>
    public const int Version = 1;

    public sealed record Tex(int Width, int Height, byte[] Rgba);

    /// <summary>A quad flattened to XZ with its texture index, and its bounds for culling.</summary>
    public readonly record struct FlatQuad(Vector2 A, Vector2 B, Vector2 C, Vector2 D, int Texture, Vector2 Min, Vector2 Max);

    /// <summary>
    /// A textured triangle flattened to XZ with its own UVs (repeating): the
    /// top of a road-piece object (an intersection), painted over the roads.
    /// </summary>
    public readonly record struct FlatTri(Vector2 A, Vector2 B, Vector2 C, Vector2 Ua, Vector2 Ub, Vector2 Uc, int Texture, Vector2 Min, Vector2 Max);

    public static List<FlatQuad> Flatten(IEnumerable<RoadPiece> pieces)
    {
        var o = new List<FlatQuad>();
        foreach (var p in pieces)
            for (int j = 0; j + 1 < p.Points.Length; j++)
            {
                Vector2 F(Vector3 v) => new(v.X, v.Z);
                var a = F(p.Points[j].Right); var b = F(p.Points[j].Left);
                var c = F(p.Points[j + 1].Left); var d = F(p.Points[j + 1].Right);
                var min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
                var max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
                o.Add(new FlatQuad(a, b, c, d, Roads.TextureIndex(p.Kind, j, far: false), min, max));
            }
        return o;
    }

    /// <summary>
    /// Which quads and triangles may touch each square of a grid of <c>cell</c>-sized squares (origins at multiples
    /// of it): a chunk's bake then looks at its own few instead of every road in the mission. The lists keep the
    /// pieces' order (later ones paint over earlier ones) and are a superset; <see cref="Bake"/> still tests each.
    /// </summary>
    public sealed class Index
    {
        private readonly float _cell;
        private readonly Dictionary<(int, int), (List<int> Quads, List<int> Tris)> _cells = [];
        private static readonly List<int> None = [];

        public Index(IReadOnlyList<FlatQuad> quads, IReadOnlyList<FlatTri>? tris, float cell)
        {
            _cell = cell;
            for (int i = 0; i < quads.Count; i++) Add(quads[i].Min, quads[i].Max, i, false);
            for (int i = 0; i < (tris?.Count ?? 0); i++) Add(tris![i].Min, tris[i].Max, i, true);
        }

        private void Add(Vector2 min, Vector2 max, int i, bool tri)
        {
            // one cell of slack each way: the bake's own test decides
            int x0 = (int)MathF.Floor(min.X / _cell) - 1, x1 = (int)MathF.Floor(max.X / _cell) + 1;
            int z0 = (int)MathF.Floor(min.Y / _cell) - 1, z1 = (int)MathF.Floor(max.Y / _cell) + 1;
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 1 << 16) { x0 = Math.Max(x0, -128); x1 = Math.Min(x1, 128); z0 = Math.Max(z0, -128); z1 = Math.Min(z1, 128); }
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!_cells.TryGetValue((x, z), out var c)) _cells[(x, z)] = c = ([], []);
                    (tri ? c.Tris : c.Quads).Add(i);
                }
        }

        /// <summary>The quads and triangles (indices, in order) that may touch the square at <paramref name="origin"/>.</summary>
        public (List<int> Quads, List<int> Tris) At(Vector2 origin)
        {
            var key = ((int)MathF.Round(origin.X / _cell), (int)MathF.Round(origin.Y / _cell));
            return _cells.TryGetValue(key, out var c) ? c : (None, None);
        }
    }

    /// <summary>
    /// The layer for the square [origin, origin + size)², or null if no road touches it.
    /// <paramref name="feather"/> fades alpha over that fraction of the road's width at each edge.
    /// With an <paramref name="index"/> (of the same quads and triangles, cell = <paramref name="size"/>), only those it lists are looked at.
    /// </summary>
    public static byte[]? Bake(IReadOnlyList<FlatQuad> quads, Vector2 origin, float size, int res,
        Func<int, Tex?> texture, float feather = 0.06f, IReadOnlyList<FlatTri>? tris = null, Index? index = null)
    {
        byte[]? img = null;
        float px = size / res;
        var max = origin + new Vector2(size, size);
        var near = index?.At(origin);
        int nq = near?.Quads.Count ?? quads.Count;
        for (int k = 0; k < nq; k++)
        {
            var q = quads[near is { } n ? n.Quads[k] : k];
            if (q.Max.X < origin.X || q.Max.Y < origin.Y || q.Min.X >= max.X || q.Min.Y >= max.Y) continue;
            if (texture(q.Texture) is not { } t) continue;
            img ??= new byte[res * res * 4];
            Paint(img, origin, px, res, t, q.A, q.B, q.C, new(0, 0), new(1, 0), new(1, 1), feather, false);
            Paint(img, origin, px, res, t, q.A, q.C, q.D, new(0, 0), new(1, 1), new(0, 1), feather, false);
        }
        // road-piece objects after the roads: an intersection covers the strips running into it
        int nt = near?.Tris.Count ?? tris?.Count ?? 0;
        for (int k = 0; k < nt; k++)
        {
            var tr = tris![near is { } n ? n.Tris[k] : k];
            if (tr.Max.X < origin.X || tr.Max.Y < origin.Y || tr.Min.X >= max.X || tr.Min.Y >= max.Y) continue;
            if (texture(tr.Texture) is not { } t) continue;
            img ??= new byte[res * res * 4];
            Paint(img, origin, px, res, t, tr.A, tr.B, tr.C, tr.Ua, tr.Ub, tr.Uc, 0, true);
        }
        return img;
    }

    /// <summary>
    /// One triangle into the layer. Each row is scanned only over the span where the triangle can be (its edges'
    /// crossings, widened by two texels), and every pixel in it gets the same test and arithmetic as a scan of the
    /// whole bounding box would: the layer is the same to the bit, only the pixels that test out are not visited.
    /// </summary>
    private static void Paint(byte[] dst, Vector2 origin, float px, int res, Tex t, Vector2 p0, Vector2 p1, Vector2 p2,
        Vector2 t0, Vector2 t1, Vector2 t2, float feather, bool wrap)
    {
        float det = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(det) < 1e-6f) return;
        var lo = Vector2.Min(Vector2.Min(p0, p1), p2);
        var hi = Vector2.Max(Vector2.Max(p0, p1), p2);
        int i0 = Math.Max(0, (int)MathF.Floor((lo.X - origin.X) / px)), i1 = Math.Min(res - 1, (int)MathF.Ceiling((hi.X - origin.X) / px));
        int j0 = Math.Max(0, (int)MathF.Floor((lo.Y - origin.Y) / px)), j1 = Math.Min(res - 1, (int)MathF.Ceiling((hi.Y - origin.Y) / px));
        if (i0 > i1 || j0 > j1) return;
        float e2x = p2.X - p0.X, e2y = p2.Y - p0.Y, e1x = p1.X - p0.X, e1y = p1.Y - p0.Y;
        // spans only for a well-shaped triangle (its area against its longest edge's square): a sliver scans its box
        double l2 = Math.Max(Math.Max(e1x * (double)e1x + e1y * (double)e1y, e2x * (double)e2x + e2y * (double)e2y),
            (p2.X - p1.X) * (double)(p2.X - p1.X) + (p2.Y - p1.Y) * (double)(p2.Y - p1.Y));
        bool spans = Math.Abs(det) > 1e-3 * l2;
        const float eps = -1e-4f;
        byte[] tex = t.Rgba;
        int tw = t.Width, th = t.Height;
        for (int j = j0; j <= j1; j++)
        {
            float py = origin.Y + (j + 0.5f) * px;
            float dy = py - p0.Y;
            int a0 = i0, a1 = i1;
            if (spans && !RowSpan(origin.X, px, p0.X, dy, e1x, e1y, e2x, e2y, det, ref a0, ref a1)) continue;
            for (int i = a0; i <= a1; i++)
            {
                float pxx = origin.X + (i + 0.5f) * px;
                float w1 = ((pxx - p0.X) * e2y - e2x * dy) / det;
                float w2 = (e1x * dy - (pxx - p0.X) * e1y) / det;
                float w0 = 1 - w1 - w2;
                if (w0 < eps || w1 < eps || w2 < eps) continue;
                float ux = t0.X * w0 + t1.X * w1 + t2.X * w2;
                float uy = t0.Y * w0 + t1.Y * w1 + t2.Y * w2;
                if (wrap) { ux -= MathF.Floor(ux); uy -= MathF.Floor(uy); }
                // bilinear, clamped to the edge; colour premultiplied so keyed texels do not bleed their colour
                float x = Math.Clamp(ux * tw - 0.5f, 0, tw - 1), y = Math.Clamp(uy * th - 0.5f, 0, th - 1);
                int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, tw - 1), y1 = Math.Min(y0 + 1, th - 1);
                float fx = x - x0, fy = y - y0;
                float r = 0, g = 0, b = 0, a = 0;
                int o = (y0 * tw + x0) * 4;
                float al = tex[o + 3] / 255f * ((1 - fx) * (1 - fy));
                r += tex[o] / 255f * al; g += tex[o + 1] / 255f * al; b += tex[o + 2] / 255f * al; a += al;
                o = (y0 * tw + x1) * 4;
                al = tex[o + 3] / 255f * (fx * (1 - fy));
                r += tex[o] / 255f * al; g += tex[o + 1] / 255f * al; b += tex[o + 2] / 255f * al; a += al;
                o = (y1 * tw + x0) * 4;
                al = tex[o + 3] / 255f * ((1 - fx) * fy);
                r += tex[o] / 255f * al; g += tex[o + 1] / 255f * al; b += tex[o + 2] / 255f * al; a += al;
                o = (y1 * tw + x1) * 4;
                al = tex[o + 3] / 255f * (fx * fy);
                r += tex[o] / 255f * al; g += tex[o + 1] / 255f * al; b += tex[o + 2] / 255f * al; a += al;
                if (a > 0) { r /= a; g /= a; b /= a; }
                else { r = g = b = a = 0; }
                if (feather > 0)
                    a *= Math.Clamp(ux / feather, 0, 1) * Math.Clamp((1 - ux) / feather, 0, 1);
                if (a <= 0) continue;
                int d = (j * res + i) * 4;
                // "over": later quads (intersections) paint on top
                float ia = 1 - a, da = dst[d + 3] / 255f;
                float oa = a + da * ia;
                dst[d] = (byte)Math.Clamp((r * a + dst[d] / 255f * da * ia) / oa * 255f + 0.5f, 0, 255);
                dst[d + 1] = (byte)Math.Clamp((g * a + dst[d + 1] / 255f * da * ia) / oa * 255f + 0.5f, 0, 255);
                dst[d + 2] = (byte)Math.Clamp((b * a + dst[d + 2] / 255f * da * ia) / oa * 255f + 0.5f, 0, 255);
                dst[d + 3] = (byte)Math.Clamp(oa * 255f + 0.5f, 0, 255);
            }
        }
    }

    /// <summary>
    /// Narrows [a0, a1] to the pixels of the row (pixel centres origin + (i + ½)·px, at dy from p0) where all three
    /// barycentrics can pass the test, from each one's line along the row in double precision with a margin of slack
    /// (in the barycentric and two texels each side) far beyond the float evaluation's error. False if none can.
    /// </summary>
    private static bool RowSpan(float ox, float px, float p0x, float dy, float e1x, float e1y, float e2x, float e2y, float det, ref int a0, ref int a1)
    {
        // w1 = (x - p0x)·e2y/det - e2x·dy/det, w2 = -(x - p0x)·e1y/det + e1x·dy/det, w0 = 1 - w1 - w2: each A·(x - p0x) + B
        double d = det;
        double A1 = e2y / d, B1 = -e2x * (double)dy / d;
        double A2 = -e1y / d, B2 = e1x * (double)dy / d;
        double A0 = -A1 - A2, B0 = 1 - B1 - B2;
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        const double floor = -1e-4 - 1e-3;
        if (!Limit(A0, B0, ref lo, ref hi) || !Limit(A1, B1, ref lo, ref hi) || !Limit(A2, B2, ref lo, ref hi)) return false;
        // u = x - p0x  ->  pixel i with centre ox + (i + 0.5)·px
        if (!double.IsNegativeInfinity(lo)) a0 = (int)Math.Max(a0, Math.Floor((lo + p0x - ox) / px - 0.5) - 2);
        if (!double.IsPositiveInfinity(hi)) a1 = (int)Math.Min(a1, Math.Ceiling((hi + p0x - ox) / px - 0.5) + 2);
        return a0 <= a1;

        static bool Limit(double A, double B, ref double lo, ref double hi)
        {
            // A·u + B >= floor
            if (Math.Abs(A) < 1e-9) return B >= floor - 1e-3;
            double u = (floor - B) / A;
            if (A > 0) lo = Math.Max(lo, u); else hi = Math.Min(hi, u);
            return lo <= hi + 1e-9;
        }
    }
}
