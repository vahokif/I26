using System.Numerics;
using I76.Editor.Roads;

namespace I76.Editor.Geo;

/// <summary>A road ready for the map: centres 5 m apart in map coordinates, the kind (0 asphalt, 1 dirt), the OSM rank (0 biggest) and whether it is a bridge or a tunnel.</summary>
public sealed record PlannedRoad(long WayId, List<Vector2> Centres, uint Kind, int Rank, bool Structure, bool Closed, string Highway, bool Tunnel = false)
{
    public bool Bridge => Structure && !Tunnel;
}

/// <summary>Why ways did not become roads, and how many.</summary>
public sealed class RoadImportStats
{
    public int Ways, Wanted, NotForCars, OutsideMap, TooShort, Split;
    public Dictionary<string, int> LeftOutByHighway = [];
    public Dictionary<string, int> ImportedByHighway = [];
}

/// <summary>OSM ways into road centre lines the road tool's maths accept (docs/map-editor.md "Roads").</summary>
public static class RoadImport
{
    /// <summary>Clip margin: a road's edges (5 m) and a little more must lie inside the map.</summary>
    public const float EdgeMargin = 12f;
    public const float SimplifyMetres = 1.0f;
    public const float FilletRadius = 30f;
    public const float MinLength = 12f;

    public static List<PlannedRoad> Convert(IEnumerable<OsmWay> ways, GeoGrid grid, bool all, RoadImportStats? stats = null)
    {
        stats ??= new RoadImportStats();
        var o = new List<PlannedRoad>();
        foreach (var w in ways)
        {
            stats.Ways++;
            if (OsmRoads.Classify(w, all) is not var (kind, rank)) { stats.NotForCars++; Count(stats.LeftOutByHighway, w.Highway); continue; }
            stats.Wanted++;
            var pts = w.Geometry.Select(g => { var (x, z) = grid.ToMap(g.Lat, g.Lon); return new Vector2((float)x, (float)z); }).ToList();
            bool closedWay = w.Nodes.Length > 3 && w.Nodes[0] == w.Nodes[^1] || Vector2.Distance(pts[0], pts[^1]) < 0.5f && pts.Count > 3;
            var runs = Clip(pts, EdgeMargin, EdgeMargin, (float)grid.Width - EdgeMargin, (float)grid.Height - EdgeMargin);
            if (runs.Count == 0) { stats.OutsideMap++; Count(stats.LeftOutByHighway, w.Highway); continue; }
            int made = 0;
            foreach (var run in runs)
                foreach (var piece in Shape(run, out bool wasSplit))
                {
                    if (wasSplit) stats.Split++;
                    if (RoadCurves.Length(piece) < MinLength || piece.Count < RoadCurves.MinPoints) continue;
                    bool closed = closedWay && runs.Count == 1 && piece.Count > 4 && Vector2.Distance(piece[0], piece[^1]) < 0.5f;
                    if (closed) piece[^1] = piece[0];
                    o.Add(new PlannedRoad(w.Id, piece, kind, rank, w.Structure, closed, w.Highway, w.IsTunnel));
                    made++;
                }
            if (made == 0) { stats.TooShort++; Count(stats.LeftOutByHighway, w.Highway); }
            else Count(stats.ImportedByHighway, w.Highway);
        }
        // the biggest roads first; within a rank, in way order
        return [.. o.OrderBy(r => r.Rank).ThenBy(r => r.WayId)];
    }

    private static void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    /// <summary>
    /// A polyline as road centres: simplified, its corners rounded, cut into steps of about 5 m, and split where a bend is still too tight
    /// for a 10 m road (the quads would fold: <see cref="RoadPlanner.FoldRadius"/>).
    /// </summary>
    public static List<List<Vector2>> Shape(IReadOnlyList<Vector2> run, out bool split)
    {
        split = false;
        var s = Simplify(run, SimplifyMetres);
        if (s.Count < 2) return [];
        var rounded = Round(s, FilletRadius, 4f);
        var centres = RoadCurves.Resample(rounded);
        var o = new List<List<Vector2>>();
        Split(centres, o, ref split, 0);
        return o;
    }

    /// <summary>
    /// Rounds the corners of a polyline with a quadratic arc of about <paramref name="radius"/> (as <see cref="RoadCurves.Fillet"/> does), but no
    /// corner takes more of a leg than is its own: a leg between two corners is shared, so each may use half of it (the road tool's fillet lets
    /// the first corner use 95 % of the first leg and the next half of it again, which doubles back on a short leg).
    /// </summary>
    public static List<Vector2> Round(IReadOnlyList<Vector2> poly, float radius, float minTurnDegrees)
    {
        int n = poly.Count;
        if (n < 3) return [.. poly];
        float Cap(int leg)
        {
            int users = (leg >= 1 ? 1 : 0) + (leg + 1 <= n - 2 ? 1 : 0);
            return Vector2.Distance(poly[leg], poly[leg + 1]) * 0.98f / Math.Max(1, users);
        }
        var o = new List<Vector2> { poly[0] };
        for (int i = 1; i < n - 1; i++)
        {
            var d0 = poly[i] - poly[i - 1]; var d1 = poly[i + 1] - poly[i];
            float l0 = d0.Length(), l1 = d1.Length();
            if (l0 < 1e-3f || l1 < 1e-3f) { o.Add(poly[i]); continue; }
            d0 /= l0; d1 /= l1;
            float turn = MathF.Acos(Math.Clamp(Vector2.Dot(d0, d1), -1f, 1f));
            if (turn < 0.02f || turn * 180 / MathF.PI < minTurnDegrees) { o.Add(poly[i]); continue; }
            float setback = MathF.Min(radius * MathF.Tan(turn / 2), MathF.Min(Cap(i - 1), Cap(i)));
            o.AddRange(RoadCurves.Quadratic(poly[i] - d0 * setback, poly[i], poly[i] + d1 * setback, 24));
        }
        o.Add(poly[^1]);
        return o;
    }

    private static void Split(List<Vector2> pts, List<List<Vector2>> into, ref bool split, int depth)
    {
        if (pts.Count < RoadCurves.MinPoints) return;
        int at = Tightest(pts, out float radius);
        if (radius >= RoadPlanner.FoldRadius || depth > 8 || at < 2 || at > pts.Count - 3) { into.Add(pts); return; }
        split = true;
        Split(pts.GetRange(0, at + 1), into, ref split, depth + 1);
        Split(pts.GetRange(at, pts.Count - at), into, ref split, depth + 1);
    }

    private static int Tightest(List<Vector2> pts, out float radius)
    {
        radius = float.PositiveInfinity; int at = -1;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            float r = RoadCurves.MinRadius([pts[i - 1], pts[i], pts[i + 1]]);
            if (r < radius) { radius = r; at = i; }
        }
        return at;
    }

    /// <summary>Douglas-Peucker: the points of a polyline that matter to within <paramref name="tolerance"/> metres.</summary>
    public static List<Vector2> Simplify(IReadOnlyList<Vector2> p, float tolerance)
    {
        if (p.Count < 3) return [.. p];
        var keep = new bool[p.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float best = 0; int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistToSegment(p[i], p[a], p[b]);
                if (d > best) { best = d; at = i; }
            }
            if (at >= 0 && best > tolerance) { keep[at] = true; stack.Push((a, at)); stack.Push((at, b)); }
        }
        var o = new List<Vector2>();
        for (int i = 0; i < p.Count; i++) if (keep[i] && (o.Count == 0 || Vector2.DistanceSquared(o[^1], p[i]) > 1e-6f)) o.Add(p[i]);
        return o;
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        float t = l2 < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>The parts of a polyline inside a rectangle (a polyline that leaves and comes back is two runs).</summary>
    public static List<List<Vector2>> Clip(IReadOnlyList<Vector2> pts, float x0, float z0, float x1, float z1)
    {
        var runs = new List<List<Vector2>>();
        List<Vector2>? cur = null;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            if (!ClipSegment(pts[i], pts[i + 1], x0, z0, x1, z1, out var a, out var b)) { cur = null; continue; }
            if (cur is null || Vector2.DistanceSquared(cur[^1], a) > 1e-4f) { cur = [a]; runs.Add(cur); }
            cur.Add(b);
        }
        return runs.Where(r => r.Count >= 2).ToList();
    }

    private static bool ClipSegment(Vector2 p, Vector2 q, float x0, float z0, float x1, float z1, out Vector2 a, out Vector2 b)
    {
        float t0 = 0, t1 = 1, dx = q.X - p.X, dz = q.Y - p.Y;
        a = p; b = q;
        bool Edge(float pp, float qq) // pp * t <= qq
        {
            if (Math.Abs(pp) < 1e-9f) return qq >= 0;
            float t = qq / pp;
            if (pp < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        if (!(Edge(-dx, p.X - x0) && Edge(dx, x1 - p.X) && Edge(-dz, p.Y - z0) && Edge(dz, z1 - p.Y))) return false;
        a = new Vector2(p.X + t0 * dx, p.Y + t0 * dz); b = new Vector2(p.X + t1 * dx, p.Y + t1 * dz);
        return true;
    }
}
