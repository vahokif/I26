using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>
/// The ground's surface nibble under roads. Surveyed over the stock maps (<c>i76 roadsurf</c>, 32 base and 62 Nitro maps): the samples
/// within 10 m of a road's centre line carry 0x4 (kind 0, asphalt), 0x6 (kind 1, dirt) or 0x8 (kind 2, planks) (99 % of them between 0
/// and 10 m; beyond that a smooth fall-off, about 90 % at 10 to 11 m, half at 12 to 15 m, a few % past 15 m), the band stops at a road's end (2.5 m
/// past it at most), and it runs on under a bridge, a ramp or a tunnel. Bit 12 (impassable) is never set under a road. Under the intersection
/// pieces the nibble is 0xc or 0xe instead (<see cref="Intersections.Junctions.SurfaceNibble"/>): a road never overwrites those.
/// </summary>
public static class RoadSurface
{
    /// <summary>How far from the centre line the stock band reaches (metres).</summary>
    public const float Band = 10f;
    /// <summary>How far past a road's end it still reaches.</summary>
    public const float EndMargin = 2.5f;

    public static int Nibble(uint kind) => 4 + 2 * (int)(kind % 3);

    private static int Nib(IMapDocument doc, int sx, int sz) => doc.Locate(sx, sz, out int tile, out int off) ? doc.GetSample(tile, off) >> 12 : 0;

    private static bool IsPiece(int nib) => nib is 0xc or 0xe;

    /// <summary>The samples the band of this road covers.</summary>
    public static HashSet<(int X, int Z)> Cells(RoadEdit road)
    {
        var cells = new HashSet<(int, int)>();
        var p = road.Points;
        if (p.Count == 0) return cells;
        bool closed = RoadBuilder.IsClosed(road);
        float s = (float)Units.SampleSize;
        for (int i = 0; i < Math.Max(1, p.Count - 1); i++)
        {
            Vector2 a = p[i].Centre, b = p[Math.Min(i + 1, p.Count - 1)].Centre;
            var ab = b - a; float len = ab.Length();
            Vector2 dir = len < 1e-6f ? Vector2.Zero : ab / len;
            bool first = i == 0 && !closed, last = i >= p.Count - 2 && !closed;
            int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - Band - EndMargin) / s), x1 = (int)Math.Ceiling((Math.Max(a.X, b.X) + Band + EndMargin) / s);
            int z0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - Band - EndMargin) / s), z1 = (int)Math.Ceiling((Math.Max(a.Y, b.Y) + Band + EndMargin) / s);
            for (int sz = z0; sz <= z1; sz++)
                for (int sx = x0; sx <= x1; sx++)
                {
                    var q = new Vector2(sx * s, sz * s);
                    float along = len < 1e-6f ? 0 : Vector2.Dot(q - a, dir);
                    if ((first && along < -EndMargin) || (last && along > len + EndMargin)) continue;
                    float t = len < 1e-6f ? 0 : Math.Clamp(along / len, 0, 1);
                    if (Vector2.Distance(q, a + ab * t) <= Band) cells.Add((sx, sz));
                }
        }
        return cells;
    }

    /// <summary>
    /// Brings the ground under a road edit in line: the samples of <paramref name="after"/>'s band take its kind's nibble (the piece
    /// ground stays), and the samples only <paramref name="before"/> covered, that no road in <paramref name="others"/> covers either,
    /// go back to the ground beside the road (the commonest nibble of the neighbouring samples outside every road's band, else 0).
    /// Either road may be null (a new road has no before; a bulldozed one no after). Writes through <paramref name="rec"/>, so it is part
    /// of the same undo step.
    /// </summary>
    public static void Update(IMapDocument doc, HeightRecorder rec, RoadEdit? before, RoadEdit? after, IEnumerable<RoadEdit> others)
    {
        var now = after is null ? [] : Cells(after);
        if (after is not null)
        {
            int nib = Nibble(after.Kind);
            foreach (var (x, z) in now)
            {
                if (!doc.Has(x, z)) continue;
                int cur = Nib(doc, x, z);
                if (IsPiece(cur) || cur == nib) continue;
                rec.SetNibble(x, z, nib);
            }
        }
        if (before is null) return;
        var gone = Cells(before);
        gone.ExceptWith(now);
        if (gone.Count == 0) return;
        // what the other roads still cover (only those whose band can reach the vacated samples); the later road wins, as in drawing
        var covered = new Dictionary<(int, int), int>();
        foreach (var c in now) covered[c] = Nibble(after!.Kind);
        foreach (var r in others)
            if (r.Points.Count > 0 && Near(r, gone))
                foreach (var c in Cells(r)) covered[c] = Nibble(r.Kind);
        var reset = new List<((int X, int Z) C, int Nib)>();
        foreach (var kv in covered)
            if (gone.Contains(kv.Key) && doc.Has(kv.Key.Item1, kv.Key.Item2) && !IsPiece(Nib(doc, kv.Key.Item1, kv.Key.Item2))) reset.Add((kv.Key, kv.Value));
        gone.ExceptWith(covered.Keys);
        foreach (var c in gone)
        {
            if (!doc.Has(c.X, c.Z)) continue;
            int cur = Nib(doc, c.X, c.Z);
            if (IsPiece(cur) || (cur & 0xe) is not (4 or 6 or 8)) continue;   // not a road's nibble: whatever the ground is, leave it
            var votes = new Dictionary<int, int>();
            for (int dz = -3; dz <= 3; dz++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    var n = (c.X + dx, c.Z + dz);
                    if (!doc.Has(n.Item1, n.Item2) || covered.ContainsKey(n) || gone.Contains(n)) continue;
                    int v = Nib(doc, n.Item1, n.Item2);
                    votes[v] = votes.GetValueOrDefault(v) + 1;
                }
            reset.Add((c, votes.Count == 0 ? 0 : votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key));
        }
        foreach (var (c, nib) in reset) rec.SetNibble(c.X, c.Z, nib);
    }

    private static bool Near(RoadEdit r, HashSet<(int X, int Z)> cells)
    {
        float s = (float)Units.SampleSize, reach = Band + EndMargin + 2 * s;
        float x0 = cells.Min(c => c.X) * s - reach, x1 = cells.Max(c => c.X) * s + reach, z0 = cells.Min(c => c.Z) * s - reach, z1 = cells.Max(c => c.Z) * s + reach;
        return r.Points.Any(p => p.Centre.X >= x0 && p.Centre.X <= x1 && p.Centre.Y >= z0 && p.Centre.Y <= z1);
    }
}
