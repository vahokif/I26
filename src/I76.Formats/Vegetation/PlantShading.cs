using System.Numerics;

namespace I76.Formats.Vegetation;

/// <summary>
/// Baked ambient occlusion for a plant (the vertex colour's alpha: 1 open sky, toward 0 deep in the crown or under it).
/// The full mesh's surfaces are splatted into a coarse density grid over its bounds (<see cref="Occluders"/>: foliage
/// cards by their alpha coverage, stems and blades by their area), and from each vertex a fixed set of rays over the
/// hemisphere of its normal is marched through it: the light a vertex gets from the sky is the rays' mean
/// transmittance, weighted by the cosine. A ray that reaches the ground under the plant ends on shaded ground, which
/// gives back little. So the inside and the base of a shrub come out dark and its rim bright, which the engine's own
/// light can't tell (the sky's light is the same everywhere, and the sun's shadow map reaches only the nearest shrubs
/// at High). The lighter meshes take the full mesh's values where they lie (<see cref="Transfer"/>), so a plant keeps
/// its shading when its tier changes.
/// </summary>
public static class PlantShading
{
    /// <summary>The share of a foliage card's area that blocks light (its texels' coverage, times 0.5 for random leaf angles).</summary>
    public const float FoliageCover = 0.12f;
    /// <summary>The same for stems, pads and blades (opaque; a tube's projected area is a quarter of its surface).</summary>
    public const float SolidCover = 0.2f;
    /// <summary>What shaded ground under the plant gives back to a ray that ends on it.</summary>
    public const float GroundBounce = 0.45f;
    /// <summary>The darkest a vertex gets (the engine's sky light still finds its way in through the gaps).</summary>
    public const float Floor = 0.2f;

    /// <summary>The rays: 32 directions spread evenly over the sphere (a Fibonacci spiral).</summary>
    private static readonly Vector3[] Dirs = MakeDirs(32);

    private static Vector3[] MakeDirs(int n)
    {
        var d = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float y = 1 - 2 * (i + 0.5f) / n, r = MathF.Sqrt(1 - y * y), a = i * 2.3999632f;
            d[i] = new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
        }
        return d;
    }

    /// <summary>A plant's light-blocking matter: an extinction per metre in cells of <see cref="Cell"/> metres from <see cref="Origin"/>.</summary>
    public sealed class Occluders
    {
        public Vector3 Origin;
        public float Cell;
        public int Nx, Ny, Nz;
        public float[] Density = [];

        public int Index(Vector3 p)
        {
            var q = (p - Origin) / Cell;
            int x = (int)MathF.Floor(q.X), y = (int)MathF.Floor(q.Y), z = (int)MathF.Floor(q.Z);
            if ((uint)x >= (uint)Nx || (uint)y >= (uint)Ny || (uint)z >= (uint)Nz) return -1;
            return (z * Ny + y) * Nx + x;
        }
    }

    /// <summary>The mesh's surfaces as an occlusion grid over its bounds (cells of a 14th of its largest side).</summary>
    public static Occluders Grid(PlantMesh m)
    {
        var (mn, mx) = m.Bounds();
        var ext = mx - mn;
        float size = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
        float cs = MathF.Max(size / 14f, 0.03f);
        var g = new Occluders
        {
            Origin = mn - new Vector3(cs), Cell = cs,
            Nx = (int)MathF.Ceiling(ext.X / cs) + 3, Ny = (int)MathF.Ceiling(ext.Y / cs) + 3, Nz = (int)MathF.Ceiling(ext.Z / cs) + 3,
        };
        var grid = g.Density = new float[g.Nx * g.Ny * g.Nz];
        // the surfaces' blocking area into the cells: each triangle spread over points within it (an R2 sequence)
        var idx = m.Indices;
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            var a = m.Positions[idx[t]]; var b = m.Positions[idx[t + 1]]; var c = m.Positions[idx[t + 2]];
            float area = 0.5f * Vector3.Cross(b - a, c - a).Length();
            if (area < 1e-9f) continue;
            float k = (m.Extra[idx[t]].Y > 0.5f ? FoliageCover : SolidCover) * area;
            int samples = Math.Clamp((int)MathF.Ceiling(area / (cs * cs) * 2), 1, 48);
            float share = k / samples;
            for (int s = 0; s < samples; s++)
            {
                float u = (0.5f + s * 0.7548777f) % 1f, v = (0.5f + s * 0.5698403f) % 1f;
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                int ci = g.Index(a + (b - a) * u + (c - a) * v);
                if (ci >= 0) grid[ci] += share;
            }
        }
        // to an extinction per metre, smoothed by a [1 2 1] filter along each axis
        float inv = 1f / (cs * cs * cs);
        for (int i = 0; i < grid.Length; i++) grid[i] *= inv;
        Smooth(grid, g.Nx, g.Ny, g.Nz, 1, g.Nx);
        Smooth(grid, g.Nx, g.Ny, g.Nz, g.Nx, g.Ny);
        Smooth(grid, g.Nx, g.Ny, g.Nz, g.Nx * g.Ny, g.Nz);
        return g;
    }

    /// <summary>Bakes the occlusion into the mesh's colours' alpha, in place, against <paramref name="occluders"/> (default: its own), and returns the mesh.</summary>
    public static PlantMesh Bake(PlantMesh m, Occluders? occluders = null)
    {
        if (m.VertexCount == 0) return m;
        var g = occluders ?? Grid(m);
        float cs = g.Cell, step = cs;
        for (int i = 0; i < m.VertexCount; i++)
        {
            var n = m.Normals[i];
            var p0 = m.Positions[i] + n * (cs * 0.5f);
            float sum = 0, wsum = 0;
            foreach (var d in Dirs)
            {
                float w = Vector3.Dot(d, n);
                if (w <= 0) continue;
                float tau = 0, tr = 1;
                for (float t = 0; ; t += step)
                {
                    var p = p0 + d * t;
                    if (p.Y < 0) { tr = GroundBounce; break; }   // shaded ground under the plant
                    int ci = g.Index(p);
                    if (ci < 0) break;                               // out of the plant: open sky (or sunlit ground)
                    tau += g.Density[ci] * step;
                }
                sum += w * tr * MathF.Exp(-tau);
                wsum += w;
            }
            float ao = wsum > 0 ? sum / wsum : 1f;
            m.Colors[i] = m.Colors[i] with { W = MathF.Max(Floor, ao) };
        }
        return m;
    }

    /// <summary>
    /// The shade the plant throws on the ground under it, from the sky (the renderer darkens the ground there, as the
    /// cars' drop shadows do): <see cref="PlantMesh.GroundShade"/> the share of the sky's light lost at its foot (the mean
    /// over its middle and four points round it), <see cref="PlantMesh.GroundRadius"/> how far out its matter reaches
    /// (the horizontal distance four in five of its vertices lie within). Sets both on <paramref name="m"/> and returns it.
    /// </summary>
    public static PlantMesh GroundShade(PlantMesh m, Occluders g)
    {
        if (m.VertexCount == 0) return m;
        var dist = m.Positions.Select(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z)).Order().ToArray();
        float r = MathF.Max(0.1f, dist[(int)(dist.Length * 0.8f)]);
        float lost = 0;
        Vector3[] at = [new(0, 0.02f, 0), new(0.35f * r, 0.02f, 0), new(-0.35f * r, 0.02f, 0), new(0, 0.02f, 0.35f * r), new(0, 0.02f, -0.35f * r)];
        foreach (var p0 in at)
        {
            float sum = 0, wsum = 0;
            foreach (var d in Dirs)
            {
                if (d.Y <= 0) continue;
                float tau = 0, step = g.Cell * 0.75f;
                for (float t = 0; ; t += step)
                {
                    int ci = g.Index(p0 + d * t);
                    if (ci < 0) break;
                    tau += g.Density[ci] * step;
                }
                sum += d.Y * MathF.Exp(-tau);
                wsum += d.Y;
            }
            lost += 1 - sum / wsum;
        }
        m.GroundShade = lost / at.Length;
        m.GroundRadius = r;
        return m;
    }

    /// <summary>
    /// A lighter mesh's occlusion taken from the full mesh's where it lies: the full mesh's vertices' values averaged
    /// into the occluder grid's cells (its foliage and the rest apart), the empty cells filled from their neighbours,
    /// read trilinearly. Rays from the lighter mesh's own fewer, larger clusters would reach out of the crown more
    /// and come out lighter, and the plant would brighten as it changes tier.
    /// </summary>
    public static PlantMesh Transfer(PlantMesh full, Occluders g, PlantMesh lighter)
    {
        var fields = new float[2][];
        for (int f = 0; f < 2; f++)
        {
            bool foliage = f == 1;
            var sum = new float[g.Density.Length];
            var cnt = new int[g.Density.Length];
            for (int i = 0; i < full.VertexCount; i++)
            {
                if (full.Extra[i].Y > 0.5f != foliage) continue;
                int ci = g.Index(full.Positions[i]);
                if (ci < 0) continue;
                sum[ci] += full.Colors[i].W; cnt[ci]++;
            }
            fields[f] = Fill(sum, cnt, g) ?? [];
        }
        var ao = new float[lighter.VertexCount];
        for (int i = 0; i < lighter.VertexCount; i++)
        {
            int f = lighter.Extra[i].Y > 0.5f ? 1 : 0;
            var field = fields[f].Length > 0 ? fields[f] : fields[1 - f];
            var at = lighter.ShadeAt.TryGetValue(i, out var sa) ? sa : lighter.Positions[i];
            ao[i] = field.Length == 0 ? 1f : Math.Clamp(Trilinear(field, g, at), Floor, 1f);
            lighter.Colors[i] = lighter.Colors[i] with { W = ao[i] };
        }
        // the larger clusters still reach out past the crown's rim: the foliage's values curved (a power) until it is
        // as dark on average, by area, as the full mesh's
        float w0 = Weighted(full, true);
        if (!float.IsNaN(w0) && !float.IsNaN(Weighted(lighter, true)))
        {
            void Apply(float k)
            {
                for (int i = 0; i < lighter.VertexCount; i++)
                    if (lighter.Extra[i].Y > 0.5f) lighter.Colors[i] = lighter.Colors[i] with { W = MathF.Max(Floor, MathF.Pow(ao[i], k)) };
            }
            float lo = MathF.Log(0.5f), hi = MathF.Log(3f);
            for (int it = 0; it < 12; it++)
            {
                float mid = 0.5f * (lo + hi);
                Apply(MathF.Exp(mid));
                if (Weighted(lighter, true) > w0) lo = mid; else hi = mid;
            }
            Apply(MathF.Exp(0.5f * (lo + hi)));
        }
        return lighter;
    }

    /// <summary>The cells' means, the empty ones filled from their filled neighbours pass by pass; null when none is filled.</summary>
    private static float[]? Fill(float[] sum, int[] cnt, Occluders g)
    {
        var v = new float[sum.Length];
        var have = new bool[sum.Length];
        int filled = 0;
        for (int i = 0; i < v.Length; i++) if (cnt[i] > 0) { v[i] = sum[i] / cnt[i]; have[i] = true; filled++; }
        if (filled == 0) return null;
        int[] off = [1, -1, g.Nx, -g.Nx, g.Nx * g.Ny, -g.Nx * g.Ny];
        while (filled < v.Length)
        {
            var add = new List<(int, float)>();
            for (int z = 0; z < g.Nz; z++)
                for (int y = 0; y < g.Ny; y++)
                    for (int x = 0; x < g.Nx; x++)
                    {
                        int i = (z * g.Ny + y) * g.Nx + x;
                        if (have[i]) continue;
                        float s = 0; int n = 0;
                        for (int k = 0; k < 6; k++)
                        {
                            bool inside = k switch { 0 => x + 1 < g.Nx, 1 => x > 0, 2 => y + 1 < g.Ny, 3 => y > 0, 4 => z + 1 < g.Nz, _ => z > 0 };
                            if (inside && have[i + off[k]]) { s += v[i + off[k]]; n++; }
                        }
                        if (n > 0) add.Add((i, s / n));
                    }
            if (add.Count == 0) break;
            foreach (var (i, val) in add) { v[i] = val; have[i] = true; }
            filled += add.Count;
        }
        return v;
    }

    /// <summary>The field at p, trilinear between the cells' centres, clamped to the grid.</summary>
    private static float Trilinear(float[] field, Occluders g, Vector3 p)
    {
        var q = (p - g.Origin) / g.Cell - new Vector3(0.5f);
        q = Vector3.Clamp(q, Vector3.Zero, new Vector3(g.Nx - 1, g.Ny - 1, g.Nz - 1));
        int x0 = Math.Min((int)q.X, Math.Max(0, g.Nx - 2)), y0 = Math.Min((int)q.Y, Math.Max(0, g.Ny - 2)), z0 = Math.Min((int)q.Z, Math.Max(0, g.Nz - 2));
        int x1 = Math.Min(x0 + 1, g.Nx - 1), y1 = Math.Min(y0 + 1, g.Ny - 1), z1 = Math.Min(z0 + 1, g.Nz - 1);
        float tx = q.X - x0, ty = q.Y - y0, tz = q.Z - z0;
        float F(int x, int y, int z) => field[(z * g.Ny + y) * g.Nx + x];
        float a = F(x0, y0, z0) + (F(x1, y0, z0) - F(x0, y0, z0)) * tx, b = F(x0, y1, z0) + (F(x1, y1, z0) - F(x0, y1, z0)) * tx;
        float c = F(x0, y0, z1) + (F(x1, y0, z1) - F(x0, y0, z1)) * tx, d = F(x0, y1, z1) + (F(x1, y1, z1) - F(x0, y1, z1)) * tx;
        float e = a + (b - a) * ty, h = c + (d - c) * ty;
        return e + (h - e) * tz;
    }

    /// <summary>The area-weighted mean occlusion of the mesh's foliage (or of everything else); NaN for none.</summary>
    public static float Weighted(PlantMesh m, bool foliage)
    {
        double s = 0, a = 0;
        for (int t = 0; t + 2 < m.Indices.Count; t += 3)
        {
            int i = m.Indices[t], j = m.Indices[t + 1], k = m.Indices[t + 2];
            if (m.Extra[i].Y > 0.5f != foliage) continue;
            float ar = 0.5f * Vector3.Cross(m.Positions[j] - m.Positions[i], m.Positions[k] - m.Positions[i]).Length();
            s += ar * (m.Colors[i].W + m.Colors[j].W + m.Colors[k].W) / 3; a += ar;
        }
        return a > 0 ? (float)(s / a) : float.NaN;
    }

    private static void Smooth(float[] g, int nx, int ny, int nz, int stride, int len)
    {
        // along one axis: every line of `len` cells `stride` apart, starting where that axis' coordinate is 0
        var line = new float[len];
        int total = nx * ny * nz;
        for (int start = 0; start < total; start++)
        {
            if (start / stride % len != 0) continue;
            for (int k = 0; k < len; k++) line[k] = g[start + k * stride];
            for (int k = 0; k < len; k++)
            {
                float l = k > 0 ? line[k - 1] : 0, r = k < len - 1 ? line[k + 1] : 0;
                g[start + k * stride] = 0.25f * l + 0.5f * line[k] + 0.25f * r;
            }
        }
    }
}
