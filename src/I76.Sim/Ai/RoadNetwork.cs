using System.Numerics;
using I76.Formats;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Sim.Ai;

/// <summary>
/// A runtime road piece (the segment list the road segment list walks):
/// the road it belongs to (the RSEG), the next and previous pieces,
/// the "last piece of its road" flag, the point count and the points
/// (the file record's second edge, y from the terrain).
/// </summary>
public sealed class RoadPiece
{
    public int Road { get; init; }
    /// <summary>The R edge, y from the terrain: what the road code measures and steps along.</summary>
    public required Vector3[] Points { get; init; }
    /// <summary>The L edge, y from the terrain; 10 from R in every shipped mission.</summary>
    public required Vector3[] Left { get; init; }
    /// <summary>The AABB midpoint over both edges.</summary>
    public Vector2 Centre { get; init; }
    public RoadPiece? Next { get; set; }
    public RoadPiece? Prev { get; set; }
    public bool End { get; set; }
    public int Count => Points.Length;
    public Vector3 this[int i] => Points[i];
}

/// <summary>A vehicle's road record: the road war's lane model.</summary>
public sealed class RoadPos
{
    /// <summary>Point index in the piece.</summary>
    public int Idx;
    /// <summary>Direction of travel along the road, +1 increasing index, −1 decreasing.</summary>
    public int Dir = 1;
    /// <summary>Lane band 0..5.</summary>
    public int Band;
    /// <summary>Relative class 0..3.</summary>
    public int Cls;
    /// <summary>Zeroed by the flee enters; no reader found.</summary>
    public float A960;
    /// <summary>Signed lateral offset from the road line.</summary>
    public float Off;
    /// <summary>Road_flee's heading output.</summary>
    public float Heading;
    /// <summary>The road piece.</summary>
    public RoadPiece? Seg;
}

/// <summary>The mission's roads as the AI walks them (the pieces, linked in load order).</summary>
public sealed class RoadNetwork
{
    public IReadOnlyList<RoadPiece> Pieces { get; }

    public RoadNetwork(IReadOnlyList<RoadPiece> pieces) => Pieces = pieces;

    public static RoadNetwork Build(MissionFile msn, Func<double, double, float> height)
    {
        var o = new List<RoadPiece>();
        int road = 0;
        foreach (var seg in msn.RoadSegments())
        {
            Vector3 Edge(RoadPoint p, int at)
            {
                float x = Bin.F32(p.Raw, at), z = Bin.F32(p.Raw, at + 8);
                return new Vector3(x, height(x, z), z);
            }
            var right = seg.Points.Select(p => Edge(p, 12)).ToArray();
            var left = seg.Points.Select(p => Edge(p, 0)).ToArray();
            var runs = Roads.Split(right.Length).ToList();
            for (int k = 0; k < runs.Count; k++)
            {
                var r = right.AsSpan(runs[k].Start, runs[k].Count).ToArray();
                var l = left.AsSpan(runs[k].Start, runs[k].Count).ToArray();
                var all = r.Concat(l).ToArray();
                float minX = all.Min(q => q.X), maxX = all.Max(q => q.X), minZ = all.Min(q => q.Z), maxZ = all.Max(q => q.Z);
                o.Add(new RoadPiece { Road = road, Points = r, Left = l, Centre = new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2), End = k == runs.Count - 1 });
            }
            road++;
        }
        for (int i = 0; i < o.Count; i++)
        {
            if (i > 0) o[i].Prev = o[i - 1];
            if (i + 1 < o.Count) o[i].Next = o[i + 1];
        }
        return new RoadNetwork(o);
    }

    /// <summary>
    /// The nearest-road-point lookup, x/z only. Phase 1 keeps up to
    /// eight candidate pieces by their bounds centre, with the original's
    /// quirk: the threshold is the farthest one held, even while fewer than
    /// eight are held. Phase 2 takes the nearest R-edge point among them
    /// (ties to the later one).
    /// </summary>
    public (RoadPiece? Seg, int Idx) Nearest(Vector3 p)
    {
        var cand = new List<(double D, RoadPiece P)>(8);
        double thr = 1e30;
        foreach (var s in Pieces)
        {
            double dx = p.X - s.Centre.X, dz = p.Z - s.Centre.Y;
            double d = dx * dx + dz * dz;
            if (d > thr) continue;
            if (cand.Count < 8) cand.Add((d, s)); else cand[0] = (d, s);
            cand.Sort((a, b) => b.D.CompareTo(a.D));
            thr = cand[0].D;
        }
        RoadPiece? best = null; int bi = 0; float bd = 1e30f;
        foreach (var (_, s) in cand)
            for (int i = 0; i < s.Points.Length; i++)
            {
                float dx = s.Points[i].X - p.X, dz = s.Points[i].Z - p.Z;
                float d = dx * dx + dz * dz;
                if (d <= bd) { bd = d; best = s; bi = i; }
            }
        return (best, bi);
    }

    public void Resolve(Vector3 p, RoadPos r) => (r.Seg, r.Idx) = Nearest(p);

    /// <summary>
    /// One point along the road in direction dir, following the
    /// next/previous piece unless the road ends there; zero-length steps
    /// (the shared piece ends) are skipped. False at the end of the road.
    /// </summary>
    public static bool NextPoint(ref RoadPiece seg, int dir, ref int idx)
    {
        var start = seg[idx];
        for (int n = 0; n < 64; n++)
        {
            idx += dir;
            if (idx >= seg.Count)
            {
                if (seg.End || seg.Next is null) { idx -= dir; return false; }
                seg = seg.Next; idx = 0;
            }
            else if (idx < 0)
            {
                if (seg.Prev is null || seg.Prev.End) { idx -= dir; return false; }
                seg = seg.Prev; idx = seg.Count - 1;
            }
            if ((seg[idx] - start).LengthSquared() >= 0.001f) return true;
        }
        return false;
    }
}
