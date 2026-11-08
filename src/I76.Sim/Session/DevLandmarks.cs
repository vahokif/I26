using System.Numerics;
using I76.Formats.Mission;
using I76.Sim.Collision;
using I76.Sim.Terrain;

namespace I76.Sim.Session;

/// <summary>
/// The Developer panel's generated places (docs/dev-hooks.md, "Developer panel"): read from the mission's own terrain and
/// structures when the panel asks, never stored, so nothing of a map is written down anywhere.
/// <para>
/// Ground the player can drive to is the heightfield reached from the start over samples not flagged impassable (bit
/// 0x1000, the flag the AI's routes keep off) with no step between neighbouring samples (5 units apart) steeper than
/// <see cref="MaxStep"/>. The highest and lowest ground are taken from the open part of it: at least
/// <see cref="Clearance"/> from an impassable sample or the map's edge, and no steeper than <see cref="MaxSlope"/> there.
/// The canyon walls carry the flag along their crests, so their flanks, higher than any hill inside (T05), drop out with
/// it, as does a rim that stands at the map's edge. If what is left within half a tile of the edge is still higher than
/// the inside, that outer ring is dropped as well.
/// </para>
/// </summary>
public static class DevLandmarks
{
    /// <summary>The steepest rise between neighbouring samples the reach crosses, in height units over the 5-unit spacing (about 40 degrees).</summary>
    public const float MaxStep = 4.2f;
    /// <summary>How far the open ground keeps from an impassable sample or the map's edge, in units.</summary>
    public const float Clearance = 100f;
    /// <summary>The steepest open ground (over 10 units each way), in degrees: a car stops on it.</summary>
    public const float MaxSlope = 15f;

    /// <summary>
    /// The start (where the player's car was placed), the highest and the lowest open ground (facing downhill and uphill
    /// along the slope there), the map's centre (the reached sample nearest the middle of its tiles), and each structure
    /// the mission places (a bridge, a ramp, a tower: ODEF types 0xb to 0xd) by its placement's name, from reached ground
    /// beside it at about its height, facing it. Only the start without a heightfield.
    /// </summary>
    public static IReadOnlyList<DevPoint> Of(SimSession s, (double X, double Z, float Heading)? start)
    {
        var list = new List<DevPoint>();
        if (start is { } st) list.Add(new DevPoint("start", st.X, st.Z, st.Heading));
        if (s.World.Ground is not WorldGround wg) return list;
        var p = s.World.Player;
        var seed = start is { } a ? (a.X, a.Z) : p is not null ? (p.Pos.X, p.Pos.Z) : ((double, double)?)null;
        if (seed is not { } sd || Reach(wg.Terrain.Tiles, sd.Item1, sd.Item2) is not { } r) return list;
        var t = wg.Terrain;

        var (hi, lo) = (-1, -1);
        for (int i = 0; i < r.Open.Length; i++)
        {
            if (!r.Open[i]) continue;
            if (hi < 0 || r.H[i] > r.H[hi]) hi = i;
            if (lo < 0 || r.H[i] < r.H[lo]) lo = i;
        }
        if (hi >= 0)
        {
            var (x, z) = r.World(hi);
            list.Add(new DevPoint($"highest ground ({r.H[hi] * 0.1f:0} up)", x, z, Slope(t, x, z, downhill: true)));
            (x, z) = r.World(lo);
            list.Add(new DevPoint($"lowest ground ({r.H[lo] * 0.1f:0} up)", x, z, Slope(t, x, z, downhill: false)));
        }
        if (r.Nearest(r.X0 + r.W / 2.0, r.Z0 + r.D / 2.0) is var mid and >= 0)
        {
            var (x, z) = r.World(mid);
            list.Add(new DevPoint("map centre", x, z, 0));
        }
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in wg.World.Objects)
        {
            if (!o.IsStructure) continue;
            int n = named[o.Name] = named.GetValueOrDefault(o.Name) + 1;
            // the reached sample nearest a ring just outside it, preferring one at the structure's own height (a bridge's road, not the canyon under it)
            double cx = o.Centre.X / 5.0, cz = o.Centre.Z / 5.0, ring = (o.Radius + 10) / 5.0;
            int best = -1;
            double bestScore = double.MaxValue;
            int i0 = System.Math.Max(0, (int)(cx - ring - 40) - r.X0), i1 = System.Math.Min(r.W - 1, (int)(cx + ring + 40) - r.X0);
            int k0 = System.Math.Max(0, (int)(cz - ring - 40) - r.Z0), k1 = System.Math.Min(r.D - 1, (int)(cz + ring + 40) - r.Z0);
            for (int k = k0; k <= k1; k++)
                for (int i = i0; i <= i1; i++)
                {
                    int c = k * r.W + i;
                    if (!r.Cells[c]) continue;
                    double dx = r.X0 + i - cx, dz = r.Z0 + k - cz, d = System.Math.Sqrt(dx * dx + dz * dz);
                    if (d < ring || d > ring + 40) continue;
                    double score = d - ring + System.Math.Abs(r.H[c] * 0.1 - o.Centre.Y) / 5.0;
                    if (score < bestScore) { bestScore = score; best = c; }
                }
            if (best < 0) continue;
            var (bx, bz) = r.World(best);
            string kind = o.Type == 0xc ? "bridge" : "structure";
            list.Add(new DevPoint($"{kind} {o.Name}{(n > 1 ? $" #{n}" : "")}", bx, bz, DevTools.HeadingOf(new Vector3(o.Centre.X - (float)bx, 0, o.Centre.Z - (float)bz))));
        }
        return list;
    }

    /// <summary>The heading along the terrain's slope at (x, z): down it, or up it; 0 on the flat.</summary>
    private static float Slope(TerrainGround t, double x, double z, bool downhill)
    {
        const double d = 10;
        float gx = t.Height(x + d, z) - t.Height(x - d, z), gz = t.Height(x, z + d) - t.Height(x, z - d);
        var g = new Vector3(gx, 0, gz);
        return g.LengthSquared() < 1e-6f ? 0 : DevTools.HeadingOf(downhill ? -g : g);
    }

    /// <summary>The samples of a box over the map's tiles (5 units apart): their heights, which are reached, and which of those are open.</summary>
    public sealed class Reached
    {
        public required int X0, Z0, W, D;
        public required bool[] Cells, Open;
        public required ushort[] H;
        public (double X, double Z) World(int i) => ((X0 + i % W) * 5.0, (Z0 + i / W) * 5.0);

        /// <summary>The reached sample nearest (sx, sz) in sample units; -1 if none.</summary>
        public int Nearest(double sx, double sz)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (!Cells[i]) continue;
                double dx = X0 + i % W - sx, dz = Z0 + i / W - sz, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
    }

    /// <summary>The ground reached from (x, z) (from the nearest passable sample if that is not one), and its open part; null if nothing is passable.</summary>
    public static Reached? Reach(TerrainTiles tiles, double x, double z)
    {
        int minR = MissionFile.ZmapGrid, maxR = -1, minC = MissionFile.ZmapGrid, maxC = -1;
        for (int row = 0; row < MissionFile.ZmapGrid; row++)
            for (int col = 0; col < MissionFile.ZmapGrid; col++)
                if (tiles.TileAt(row, col) != MissionFile.ZmapEmpty)
                { minR = System.Math.Min(minR, row); maxR = System.Math.Max(maxR, row); minC = System.Math.Min(minC, col); maxC = System.Math.Max(maxC, col); }
        if (maxR < 0) return null;
        const int T = TerrainTiles.Tile;
        int x0 = minC * T, z0 = minR * T, w = (maxC - minC + 1) * T, d = (maxR - minR + 1) * T, n = w * d;
        var h = new ushort[n];
        var ok = new bool[n];
        for (int k = 0; k < d; k++)
            for (int i = 0; i < w; i++)
                if (tiles.Sample(x0 + i, z0 + k) is { } v && (v & 0x1000) == 0) { ok[k * w + i] = true; h[k * w + i] = (ushort)(v & MissionFile.HeightMask); }

        // the start: its own sample, else the nearest passable one
        int si = (int)System.Math.Round(x / 5) - x0, sk = (int)System.Math.Round(z / 5) - z0;
        int start = si >= 0 && sk >= 0 && si < w && sk < d && ok[sk * w + si] ? sk * w + si : -1;
        if (start < 0)
            start = new Reached { X0 = x0, Z0 = z0, W = w, D = d, Cells = ok, Open = ok, H = h }.Nearest(x / 5, z / 5);
        if (start < 0) return null;

        var reached = new bool[n];
        int step = (int)(MaxStep * 10);   // in raw height units (0.1)
        var queue = new Queue<int>();
        reached[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int c = queue.Dequeue(), ci = c % w, ck = c / w;
            void Try(int ni, int nk)
            {
                if (ni < 0 || nk < 0 || ni >= w || nk >= d) return;
                int m = nk * w + ni;
                if (reached[m] || !ok[m] || System.Math.Abs(h[m] - h[c]) > step) return;
                reached[m] = true;
                queue.Enqueue(m);
            }
            Try(ci + 1, ck); Try(ci - 1, ck); Try(ci, ck + 1); Try(ci, ck - 1);
        }

        // the distance to the nearest impassable or missing sample (a 3-4 chamfer, in thirds of a sample)
        var dist = new int[n];
        const int Far = int.MaxValue / 2;
        for (int c = 0; c < n; c++) dist[c] = ok[c] ? Far : 0;
        for (int k = 0; k < d; k++)
            for (int i = 0; i < w; i++)
            {
                int c = k * w + i, v = dist[c];
                if (v == 0) continue;
                if (i > 0) v = System.Math.Min(v, dist[c - 1] + 3);
                if (k > 0)
                {
                    v = System.Math.Min(v, dist[c - w] + 3);
                    if (i > 0) v = System.Math.Min(v, dist[c - w - 1] + 4);
                    if (i < w - 1) v = System.Math.Min(v, dist[c - w + 1] + 4);
                }
                // the box's own edge counts as the map's
                if (i == 0 || k == 0) v = System.Math.Min(v, 3);
                dist[c] = v;
            }
        for (int k = d - 1; k >= 0; k--)
            for (int i = w - 1; i >= 0; i--)
            {
                int c = k * w + i, v = dist[c];
                if (v == 0) continue;
                if (i < w - 1) v = System.Math.Min(v, dist[c + 1] + 3);
                if (k < d - 1)
                {
                    v = System.Math.Min(v, dist[c + w] + 3);
                    if (i < w - 1) v = System.Math.Min(v, dist[c + w + 1] + 4);
                    if (i > 0) v = System.Math.Min(v, dist[c + w - 1] + 4);
                }
                if (i == w - 1 || k == d - 1) v = System.Math.Min(v, 3);
                dist[c] = v;
            }
        int clear = (int)(Clearance / 5 * 3);
        float maxGrad = MathF.Tan(MaxSlope * MathF.PI / 180f) * 20f * 10f;   // raw units over the 20 units between i - 2 and i + 2
        var open = new bool[n];
        for (int k = 2; k < d - 2; k++)
            for (int i = 2; i < w - 2; i++)
            {
                int c = k * w + i;
                if (!reached[c] || dist[c] < clear) continue;
                float gx = h[c + 2] - h[c - 2], gz = h[c + 2 * w] - h[c - 2 * w];
                open[c] = gx * gx + gz * gz <= maxGrad * maxGrad;
            }

        // the outer ring: within half a tile of a side with no tile beyond it, dropped if it is higher than the inside
        const int Band = T / 2;
        bool Rim(int c)
        {
            int gx = x0 + c % w, gz = z0 + c / w;
            return tiles.Sample(gx - Band, gz) is null || tiles.Sample(gx + Band, gz) is null || tiles.Sample(gx, gz - Band) is null || tiles.Sample(gx, gz + Band) is null;
        }
        int rimTop = -1, inTop = -1;
        for (int c = 0; c < n; c++)
        {
            if (!open[c]) continue;
            if (Rim(c)) rimTop = System.Math.Max(rimTop, h[c]); else inTop = System.Math.Max(inTop, h[c]);
        }
        if (inTop >= 0 && rimTop > inTop)
            for (int c = 0; c < n; c++)
                if (open[c] && Rim(c)) open[c] = false;
        return new Reached { X0 = x0, Z0 = z0, W = w, D = d, Cells = reached, Open = open, H = h };
    }
}
