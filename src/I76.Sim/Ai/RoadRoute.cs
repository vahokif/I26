using System.Numerics;
using I76.Sim.Terrain;

namespace I76.Sim.Ai;

/// <summary>A planner node or waypoint: x, z and a flag (0 off-road, 1 road, 2 bridge).</summary>
public readonly record struct PathNode(float X, float Z, float Flag)
{
    public Vector2 P => new(X, Z);
}

/// <summary>
/// The AI's road routes and waypoint building.
/// There is no road graph: a leg follows a road only when the car and the
/// goal are on the same road; it walks the road point by
/// point in a lane 2.45 in from an edge. A*
/// paths are reduced to corners, string-pulled over open ground and snapped
/// to the same lanes where they run on a road.
/// </summary>
public static class RoadRoute
{
    public const float LaneInset = 2.45f;
    /// <summary>Within √111 of an edge of the nearest road point counts as on the road.</summary>
    public const float NearRoad = 111f;

    public static bool SameRoad(RoadPiece? a, RoadPiece? b) => a is not null && b is not null && a.Road == b.Road;

    public static bool SameRoadPoint(RoadPiece a, int ia, RoadPiece b, int ib) =>
        (a == b && ia == ib) || (a.Road == b.Road && Vector3.DistanceSquared(a[ia], b[ib]) < 0.001f);

    private static float D2(float x, float z, Vector3 q) => (x - q.X) * (x - q.X) + (z - q.Z) * (z - q.Z);

    private static bool OnRoad(RoadPiece s, int i, float x, float z) => !(D2(x, z, s.Points[i]) > NearRoad && D2(x, z, s.Left[i]) > NearRoad);

    /// <summary>
    /// The road route from the (rounded) start to the nav point,
    /// or null when either end is off the road or they are on different roads.
    /// </summary>
    public static List<PathNode>? TryRoute(RoadNetwork net, float sx, float sz, float gx, float gz, float detour = 0)
    {
        var (sSeg, sIdx) = net.Nearest(new Vector3(sx, 0, sz));
        if (sSeg is null || !OnRoad(sSeg, sIdx, sx, sz)) return null;
        var (gSeg, gIdx) = net.Nearest(new Vector3(gx, 0, gz));
        if (gSeg is null || !OnRoad(gSeg, gIdx, gx, gz)) return null;
        if (!SameRoad(sSeg, gSeg)) return null;
        var route = ExpandRoadPairs(net, [new(sx, sz, 1f), new(gx, gz, 1f)], detour, out bool refused);
        return refused ? null : route;
    }

    /// <summary>Each same-road pair of road nodes becomes a lane walk.</summary>
    public static List<PathNode> ExpandRoadPairs(RoadNetwork net, IReadOnlyList<PathNode> input) => ExpandRoadPairs(net, input, 0, out _);

    /// <summary>
    /// <paramref name="detour"/> is the port's own limit for a network melee's bots (0: none, the original's): a walk longer than that
    /// many times the straight line between the pair (plus 100) is not made, the pair left as two nodes and
    /// <paramref name="refused"/> set. A road that is not closed at a race's start line puts the next checkpoint a whole road away.
    /// </summary>
    public static List<PathNode> ExpandRoadPairs(RoadNetwork net, IReadOnlyList<PathNode> input, float detour, out bool refused)
    {
        refused = false;
        var o = new List<PathNode>();
        int n = input.Count, i = 0;
        while (i < n - 1)
        {
            if (input[i].Flag == 1f)
            {
                if (input[i + 1].Flag == 1f)
                {
                    var (sA, iA) = net.Nearest(new Vector3(input[i].X, 0, input[i].Z));
                    var (sB, iB) = net.Nearest(new Vector3(input[i + 1].X, 0, input[i + 1].Z));
                    if (SameRoad(sA, sB))
                    {
                        var walk = new List<PathNode>();
                        WalkRoad(sA!, iA, sB!, iB, walk);
                        if (detour > 0 && Length(walk) > detour * Vector2.Distance(input[i].P, input[i + 1].P) + 100f) refused = true;
                        else
                        {
                            o.AddRange(walk);
                            i += 2;
                            continue;
                        }
                    }
                }
                o.Add(input[i] with { Flag = 1f });
            }
            else o.Add(input[i]);
            i++;
        }
        if (n > 0) o.Add(input[n - 1]);
        return o;
    }

    private static float Length(List<PathNode> w)
    {
        float len = 0;
        for (int k = 1; k < w.Count; k++) len += Vector2.Distance(w[k - 1].P, w[k].P);
        return len;
    }

    /// <summary>Which way along the road the goal point lies.</summary>
    public static int Direction(RoadPiece sA, int iA, RoadPiece sB, int iB)
    {
        if (sA == sB) return iB <= iA ? -1 : 1;
        var s = sA;
        while (!s.End && s.Next is not null) { if (s == sB) return 1; s = s.Next; }
        return s == sB ? 1 : -1;
    }

    /// <summary>The point-count distance (shared end points counted twice).</summary>
    public static int IndexDistance(int dir, RoadPiece sA, int iA, RoadPiece sB, int iB)
    {
        if (dir == 1)
        {
            if (sA == sB) return iB - iA;
            int d = sA.Count - iA;
            for (var p = sA.Next; p is not null && p != sB; p = p.Next) d += p.Count;
            return d + iB;
        }
        else
        {
            if (sA == sB) return iA - iB;
            int d = iA;
            var p = sA.Prev;
            if (p is not null)
            {
                for (; p is not null && p != sB; p = p.Prev) d += p.Count;
                d += sB.Count - iB;
            }
            return d;
        }
    }

    /// <summary>The road's first and last pieces, and whether its ends meet (within 9).</summary>
    public static bool IsLoop(RoadPiece s, out RoadPiece first, out RoadPiece last)
    {
        first = s;
        while (first.Prev is { End: false } pv) first = pv;
        last = s;
        while (!last.End && last.Next is not null) last = last.Next;
        var a = first[0]; var b = last[last.Count - 1];
        return (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) < 81f;
    }

    /// <summary>On a closed road, whether going the other way round is strictly shorter.</summary>
    public static bool OtherWayShorter(int nd, int dist, RoadPiece sA, int iA, RoadPiece last, RoadPiece first, RoadPiece sB, int iB)
    {
        if (sA == sB) return false;
        if (nd == 1)
        {
            int c = sA.Count - iA;
            var s = sA;
            while (!s.End && s.Next is not null) { s = s.Next; c += s.Count; if (c > dist) return false; }
            for (var p = first; p != sB; p = p.Next!) { c += p.Count; if (c > dist || p.Next is null) return false; }
            return c + iB < dist;
        }
        else
        {
            int c = iA;
            for (var s = sA.Prev; s is not null && !s.End; s = s.Prev) { c += s.Count; if (c > dist) return false; }
            for (var p = last; p != sB; p = p.Prev!) { c += p.Count; if (c > dist || p.Prev is null) return false; }
            return c + (sB.Count - iB) < dist;
        }
    }

    /// <summary>The lane point 2.45 in from the R edge (side 1, going up) or the L edge (side 0, going down).</summary>
    public static Vector2 LanePoint(RoadPiece s, int i, int side)
    {
        Vector2 Xz(Vector3 v) => new(v.X, v.Z);
        Vector2 d, e;
        if (side == 1)
        {
            e = Xz(s.Points[i]);
            d = i < s.Count - 1 ? Xz(s.Points[i + 1]) - e : e - Xz(s.Points[i - 1]);
            float l2 = d.LengthSquared();
            return l2 is > -1e-4f and < 1e-4f ? e + new Vector2(0, LaneInset) : e + LaneInset / MathF.Sqrt(l2) * new Vector2(-d.Y, d.X);
        }
        e = Xz(s.Left[i]);
        d = i > 0 ? e - Xz(s.Left[i - 1]) : Xz(s.Left[1]) - Xz(s.Left[0]);
        float m2 = d.LengthSquared();
        return m2 is > -1e-4f and < 1e-4f ? e + new Vector2(0, LaneInset) : e + LaneInset / MathF.Sqrt(m2) * new Vector2(d.Y, -d.X);
    }

    /// <summary>Lane points from the start's road point to the goal's, the short way round a closed road.</summary>
    public static void WalkRoad(RoadPiece sA, int iA, RoadPiece sB, int iB, List<PathNode> o)
    {
        int dir = Direction(sA, iA, sB, iB);
        int dist = IndexDistance(dir, sA, iA, sB, iB);
        bool wrap = false;
        bool loop = IsLoop(sA, out var first, out var last);
        if (loop && OtherWayShorter(-dir, dist, sA, iA, last, first, sB, iB)) { dir = -dir; wrap = true; }
        int side = dir == 1 ? 1 : 0;
        void Emit(RoadPiece s, int i) { var p = LanePoint(s, i, side); o.Add(new PathNode(p.X, p.Y, 1f)); }
        const int Guard = 100000;
        if (!wrap)
        {
            var s = sA; int i = iA;
            for (int g = 0; g < Guard; g++)
            {
                Emit(s, i);
                if (SameRoadPoint(s, i, sB, iB) || !RoadNetwork.NextPoint(ref s, dir, ref i)) return;
            }
            return;
        }
        if (dir == -1)
        {
            var s = sA; int i = iA;
            for (int g = 0; g < Guard; g++)
            {
                Emit(s, i);
                if (SameRoadPoint(s, i, first, 0) || !RoadNetwork.NextPoint(ref s, -1, ref i)) break;
            }
            int j = last.Count - 1;
            if (SameRoadPoint(first, 0, last, j)) j--;
            s = last; i = j;
            for (int g = 0; g < Guard; g++)
            {
                Emit(s, i);
                if (SameRoadPoint(s, i, sB, iB) || !RoadNetwork.NextPoint(ref s, -1, ref i)) return;
            }
        }
        else
        {
            var s = sA; int i = iA;
            for (int g = 0; g < Guard; g++)
            {
                Emit(s, i);
                if (SameRoadPoint(s, i, last, last.Count - 1) || !RoadNetwork.NextPoint(ref s, 1, ref i)) break;
            }
            int j = SameRoadPoint(last, last.Count - 1, first, 0) ? 1 : 0;
            s = first; i = j;
            for (int g = 0; g < Guard; g++)
            {
                Emit(s, i);
                if (SameRoadPoint(s, i, sB, iB) || !RoadNetwork.NextPoint(ref s, 1, ref i)) return;
            }
        }
    }

    // ---- the A* path build ----

    /// <summary>The direction code of a → b used by Corners and StringPull.</summary>
    public static int Dir(PathNode a, PathNode b)
    {
        int sx = MathF.Sign((int)b.X - (int)a.X), sz = MathF.Sign((int)b.Z - (int)a.Z);
        return (sx, sz) switch
        {
            (1, 1) => 1, (1, 0) => 6, (1, -1) => 4,
            (-1, 1) => 2, (-1, 0) => 7, (-1, -1) => 5,
            (0, 1) => 0, _ => 3,
        };
    }

    /// <summary>The 45° cone, its table quirk (D0 = 2 allows 3) included.</summary>
    public static bool Cone(int d0, int d) => d0 switch
    {
        0 => d is 0 or 1 or 2,
        1 => d is 0 or 1 or 6,
        2 => d is 2 or 3 or 7,
        3 => d is 3 or 4 or 5,
        4 => d is 3 or 4 or 6,
        5 => d is 3 or 5 or 7,
        6 => d is 1 or 4 or 6,
        7 => d is 2 or 5 or 7,
        _ => false,
    };

    /// <summary>The visibility test: the impassable cell bit sampled every 5 along, about 5× the length (sic).</summary>
    public static bool Visible(IGround g, PathNode a, PathNode b)
    {
        var d = b.P - a.P;
        float l2 = d.LengthSquared();
        float k = l2 is > -1e-4f and < 1e-4f ? 1f : 1f / MathF.Sqrt(l2);
        float lim = k + 1, t = 0;
        var p = a.P;
        for (int n = 0; t < lim && n < 100000; n++)
        {
            p += 5f * k * d;
            if (g.Impassable(p.X, p.Y)) return false;
            t += k;
        }
        return true;
    }

    /// <summary>Then the reversal: the start-to-goal nodes where the direction or the road/link flag changes.</summary>
    public static List<PathNode> Corners(IReadOnlyList<(int X, int Z, bool Road, bool Link)> goalToStart)
    {
        static float Flag((int X, int Z, bool Road, bool Link) n) => n.Link ? 2f : n.Road ? 1f : 0f;
        var o = new List<PathNode>();
        int prevDir = -1; int prevRoad = -1, prevLink = -1;
        for (int k = 0; k + 1 < goalToStart.Count; k++)
        {
            var p = goalToStart[k]; var q = goalToStart[k + 1];
            int d = Dir(new PathNode(p.X, p.Z, 0), new PathNode(q.X, q.Z, 0));
            int pr = p.Road ? 1 : 0, qr = q.Road ? 1 : 0, pl = p.Link ? 1 : 0, ql = q.Link ? 1 : 0;
            bool keep = !(d == prevDir && pr == prevRoad && qr == pr && pl == prevLink && ql == pl);
            if (keep) o.Add(new PathNode(p.X, p.Z, Flag(p)));
            prevDir = d; prevRoad = pr; prevLink = pl;
        }
        var s = goalToStart[^1];
        o.Add(new PathNode(s.X, s.Z, Flag(s)));
        o.Reverse();
        return o;
    }

    /// <summary>Over open ground, jump to the farthest node in a 45° cone that is visible.</summary>
    public static List<PathNode> StringPull(IGround g, IReadOnlyList<PathNode> input)
    {
        var o = new List<PathNode> { input[0] };
        int n = input.Count, i = 0;
        if (n < 2) return o;
        while (true)
        {
            int j = i + 1;
            int d0 = Dir(input[i], input[j]);
            if (input[j].Flag == 0f && j + 1 < n)
            {
                int k = j + 1;
                while (k < n && input[k - 1].Flag == 0f && Cone(d0, Dir(input[k - 1], input[k]))) { j++; k++; }
            }
            while (j > i)
            {
                if (j == i + 1 || input[j].Flag is 1f or 2f) break;
                if (Visible(g, input[i], input[j])) break;
                j--;
            }
            o.Add(input[j]);
            i = j;
            if (i >= n - 1) return o;
        }
    }

    /// <summary>Each run of road nodes becomes (entry, farthest node still on the entry's road).</summary>
    public static List<PathNode> RoadRuns(RoadNetwork net, IReadOnlyList<PathNode> input)
    {
        var seg = input.Select(p => net.Nearest(new Vector3(p.X, 0, p.Z)).Seg).ToArray();
        var o = new List<PathNode>();
        int n = input.Count, i = 0;
        while (i < n)
        {
            if (input[i].Flag == 1f && i < n - 1)
            {
                o.Add(input[i]);
                if (input[i + 1].Flag != 1f) { i++; continue; }
                int k = i + 1;
                while (k < n - 1 && input[k + 1].Flag == 1f) k++;
                if (SameRoad(seg[i], seg[k]) || k == i + 1) { o.Add(input[k]); i = k + 1; }
                else
                {
                    k--;
                    while (k != i && !SameRoad(seg[i], seg[k])) k--;
                    if (k != i) o.Add(input[k]);
                    i = k + 1;
                }
            }
            else { o.Add(input[i]); i++; }
        }
        return o;
    }

    /// <summary>
    /// One node gives one waypoint, two give two; otherwise
    /// Corners → StringPull → RoadRuns → ExpandRoadPairs →
    /// the bridge pass (<see cref="BridgeLinks"/>).
    /// </summary>
    public static List<PathNode> BuildWaypoints(RoadNetwork? net, IGround g, IReadOnlyList<(int X, int Z, bool Road, bool Link)> goalToStart, BridgeLinks? bridges = null, float detour = 0)
    {
        // the 1- and 2-node cases write the road flag straight into the waypoints, not the link
        static PathNode N((int X, int Z, bool Road, bool Link) n) => new(n.X, n.Z, n.Road ? 1f : 0f);
        if (goalToStart.Count == 1) return [N(goalToStart[0])];
        if (goalToStart.Count == 2) return [N(goalToStart[1]), N(goalToStart[0])];
        var a = StringPull(g, Corners(goalToStart));
        if (net is not null) a = ExpandRoadPairs(net, RoadRuns(net, a), detour, out _);
        return bridges is { Any: true } ? bridges.Pass(a) : a;
    }
}
