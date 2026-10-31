using System.Numerics;
using I76.Formats.Assembly;

namespace I76.Formats.Tests;

/// <summary>
/// RoadRaster.Bake as it was before the row-span rasteriser (one bounding-box scan a triangle): the reference the
/// fast one must match byte for byte.
/// </summary>
internal static class RoadRasterReference
{
    public static byte[]? Bake(IReadOnlyList<RoadRaster.FlatQuad> quads, Vector2 origin, float size, int res,
        Func<int, RoadRaster.Tex?> texture, float feather = 0.06f, IReadOnlyList<RoadRaster.FlatTri>? tris = null)
    {
        byte[]? img = null;
        float px = size / res;
        var max = origin + new Vector2(size, size);
        foreach (var q in quads)
        {
            if (q.Max.X < origin.X || q.Max.Y < origin.Y || q.Min.X >= max.X || q.Min.Y >= max.Y) continue;
            if (texture(q.Texture) is not { } t) continue;
            img ??= new byte[res * res * 4];
            Paint(img, t, q.A, q.B, q.C, new(0, 0), new(1, 0), new(1, 1), feather, false);
            Paint(img, t, q.A, q.C, q.D, new(0, 0), new(1, 1), new(0, 1), feather, false);
        }
        // road-piece objects after the roads: an intersection covers the strips running into it
        foreach (var tr in tris ?? [])
        {
            if (tr.Max.X < origin.X || tr.Max.Y < origin.Y || tr.Min.X >= max.X || tr.Min.Y >= max.Y) continue;
            if (texture(tr.Texture) is not { } t) continue;
            img ??= new byte[res * res * 4];
            Paint(img, t, tr.A, tr.B, tr.C, tr.Ua, tr.Ub, tr.Uc, 0, true);
        }
        return img;

        void Paint(byte[] dst, RoadRaster.Tex t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 t0, Vector2 t1, Vector2 t2, float feather, bool wrap)
        {
                float det = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                if (MathF.Abs(det) < 1e-6f) return;
                var lo = Vector2.Min(Vector2.Min(p0, p1), p2);
                var hi = Vector2.Max(Vector2.Max(p0, p1), p2);
                int i0 = Math.Max(0, (int)MathF.Floor((lo.X - origin.X) / px)), i1 = Math.Min(res - 1, (int)MathF.Ceiling((hi.X - origin.X) / px));
                int j0 = Math.Max(0, (int)MathF.Floor((lo.Y - origin.Y) / px)), j1 = Math.Min(res - 1, (int)MathF.Ceiling((hi.Y - origin.Y) / px));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        var p = origin + new Vector2((i + 0.5f) * px, (j + 0.5f) * px);
                        float w1 = ((p.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p.Y - p0.Y)) / det;
                        float w2 = ((p1.X - p0.X) * (p.Y - p0.Y) - (p.X - p0.X) * (p1.Y - p0.Y)) / det;
                        float w0 = 1 - w1 - w2;
                        const float eps = -1e-4f;
                        if (w0 < eps || w1 < eps || w2 < eps) continue;
                        var uv = t0 * w0 + t1 * w1 + t2 * w2;
                        if (wrap) uv = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
                        var (r, g, b, a) = Sample(t, uv);
                        if (feather > 0)
                            a *= Math.Clamp(uv.X / feather, 0, 1) * Math.Clamp((1 - uv.X) / feather, 0, 1);
                        if (a <= 0) continue;
                        int o = (j * res + i) * 4;
                        // "over": later quads (intersections) paint on top
                        float ia = 1 - a, da = dst[o + 3] / 255f;
                        float oa = a + da * ia;
                        dst[o] = (byte)Math.Clamp((r * a + dst[o] / 255f * da * ia) / oa * 255f + 0.5f, 0, 255);
                        dst[o + 1] = (byte)Math.Clamp((g * a + dst[o + 1] / 255f * da * ia) / oa * 255f + 0.5f, 0, 255);
                        dst[o + 2] = (byte)Math.Clamp((b * a + dst[o + 2] / 255f * da * ia) / oa * 255f + 0.5f, 0, 255);
                        dst[o + 3] = (byte)Math.Clamp(oa * 255f + 0.5f, 0, 255);
                    }
        }
    }

    /// <summary>Bilinear, clamped to the edge; colour premultiplied so keyed texels do not bleed their colour.</summary>
    private static (float R, float G, float B, float A) Sample(RoadRaster.Tex t, Vector2 uv)
    {
        float x = Math.Clamp(uv.X * t.Width - 0.5f, 0, t.Width - 1), y = Math.Clamp(uv.Y * t.Height - 0.5f, 0, t.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, t.Width - 1), y1 = Math.Min(y0 + 1, t.Height - 1);
        float fx = x - x0, fy = y - y0;
        float r = 0, g = 0, b = 0, a = 0;
        void Acc(int xx, int yy, float w)
        {
            int o = (yy * t.Width + xx) * 4;
            float al = t.Rgba[o + 3] / 255f * w;
            r += t.Rgba[o] / 255f * al; g += t.Rgba[o + 1] / 255f * al; b += t.Rgba[o + 2] / 255f * al; a += al;
        }
        Acc(x0, y0, (1 - fx) * (1 - fy)); Acc(x1, y0, fx * (1 - fy)); Acc(x0, y1, (1 - fx) * fy); Acc(x1, y1, fx * fy);
        return a > 0 ? (r / a, g / a, b / a, a) : (0, 0, 0, 0);
    }
}
