using System.Numerics;
using I76.Sim.Terrain;

namespace I76.Sim.Ai;

/// <summary>
/// A coarse route across an arena for a network melee's bots (docs/multiplayer.md "Bots"): the way from one point to another over the
/// terrain's impassable cells (<see cref="IGround.Impassable"/>, the map the AI's own planner reads), found on a grid of 12 m cells
/// and handed to the AI as a list of points about 70 m apart, which its planner (PlanPath: the road route, else the A*) then plans leg
/// by leg. It answers the planner's limit, not its map: the A* has a pool of 1000 nodes, so a goal behind a long wall, as the
/// two bases of a CTF arena often are, is never found, and the car circles where it started. A bot asks for it only when it has
/// stopped getting nearer: the planner's own road routes are the faster way where it works.
/// </summary>
public static class GridRoute
{
    /// <summary>The cell's side, metres; a cell is open when its middle and the middles of its four edges are not impassable (a car is 2 to 3 m wide).</summary>
    public const float Cell = 12f;
    /// <summary>The points a route is cut into: no two farther apart than this.</summary>
    public const float Spacing = 70f;
    private const int MaxCells = 120_000;

    /// <summary>
    /// The points from <paramref name="from"/> to <paramref name="to"/> (not including the start, ending at <paramref name="to"/>), or
    /// null when the grid finds none (a goal walled in): the caller then goes on as before.
    /// </summary>
    public static List<Vector3>? Between(IGround g, Vector3 from, Vector3 to)
    {
        float dist = Vector2.Distance(new(from.X, from.Z), new(to.X, to.Z));
        // the box round both ends, with room to go round: the distance again each way
        float pad = MathF.Max(1500f, dist);
        float x0 = MathF.Min(from.X, to.X) - pad, z0 = MathF.Min(from.Z, to.Z) - pad;
        float x1 = MathF.Max(from.X, to.X) + pad, z1 = MathF.Max(from.Z, to.Z) + pad;
        // the cells grow when the box is big, so the grid is never more than MaxCells
        float cell = MathF.Max(Cell, MathF.Sqrt((x1 - x0) * (z1 - z0) / MaxCells));
        int w = (int)((x1 - x0) / cell) + 1, h = (int)((z1 - z0) / cell) + 1;
        var open = new sbyte[w * h];       // 0 unknown, 1 open, -1 blocked
        bool Open(int cx, int cz)
        {
            if (cx < 0 || cz < 0 || cx >= w || cz >= h) return false;
            ref sbyte v = ref open[cz * w + cx];
            if (v == 0)
            {
                double x = x0 + (cx + 0.5) * cell, z = z0 + (cz + 0.5) * cell, r = cell / 2;
                v = !g.Impassable(x, z) && !g.Impassable(x - r, z) && !g.Impassable(x + r, z) && !g.Impassable(x, z - r) && !g.Impassable(x, z + r) ? (sbyte)1 : (sbyte)-1;
            }
            return v > 0;
        }
        (int X, int Z) CellOf(Vector3 p) => (System.Math.Clamp((int)((p.X - x0) / cell), 0, w - 1), System.Math.Clamp((int)((p.Z - z0) / cell), 0, h - 1));
        (int X, int Z)? Nearest((int X, int Z) c)
        {
            if (Open(c.X, c.Z)) return c;
            for (int r = 1; r <= 4; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dz)) == r && Open(c.X + dx, c.Z + dz)) return (c.X + dx, c.Z + dz);
            return null;
        }
        if (Nearest(CellOf(from)) is not { } s || Nearest(CellOf(to)) is not { } t) return null;

        // A* over the open cells, eight ways, a diagonal only past two open corners
        var gScore = new float[w * h];
        Array.Fill(gScore, float.MaxValue);
        var came = new int[w * h];
        var closed = new bool[w * h];
        var heap = new PriorityQueue<int, float>();
        int si = s.Z * w + s.X, ti = t.Z * w + t.X;
        gScore[si] = 0; came[si] = -1;
        float Hh(int i)
        {
            int dx = System.Math.Abs(i % w - t.X), dz = System.Math.Abs(i / w - t.Z);
            return cell * (System.Math.Max(dx, dz) + 0.4142f * System.Math.Min(dx, dz));
        }
        heap.Enqueue(si, Hh(si));
        bool found = false;
        while (heap.TryDequeue(out int cur, out _))
        {
            if (closed[cur]) continue;
            closed[cur] = true;
            if (cur == ti) { found = true; break; }
            int cx = cur % w, cz = cur / w;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (!Open(nx, nz)) continue;
                    if (dx != 0 && dz != 0 && (!Open(cx + dx, cz) || !Open(cx, cz + dz))) continue;
                    int ni = nz * w + nx;
                    if (closed[ni]) continue;
                    float ng = gScore[cur] + (dx != 0 && dz != 0 ? 1.4142f : 1f) * cell;
                    if (ng >= gScore[ni]) continue;
                    gScore[ni] = ng; came[ni] = cur;
                    heap.Enqueue(ni, ng + Hh(ni));
                }
        }
        if (!found) return null;
        var cells = new List<Vector2>();
        for (int i = ti; i != -1; i = came[i]) cells.Add(new Vector2(x0 + (i % w + 0.5f) * cell, z0 + (i / w + 0.5f) * cell));
        cells.Reverse();
        cells[0] = new Vector2(from.X, from.Z);
        cells[^1] = new Vector2(to.X, to.Z);

        // string-pull: from each point on to the farthest one a straight line of open cells reaches
        bool Clear(Vector2 a, Vector2 b)
        {
            float len = Vector2.Distance(a, b);
            int n = System.Math.Max(1, (int)(len / (cell / 2)));
            for (int k = 1; k < n; k++)
            {
                var p = Vector2.Lerp(a, b, k / (float)n);
                if (!Open((int)((p.X - x0) / cell), (int)((p.Y - z0) / cell))) return false;
            }
            return true;
        }
        var pulled = new List<Vector2> { cells[0] };
        int at = 0;
        while (at < cells.Count - 1)
        {
            int far = at + 1;
            for (int k = cells.Count - 1; k > at + 1; k--)
                if (Clear(cells[at], cells[k])) { far = k; break; }
            pulled.Add(cells[far]);
            at = far;
        }
        // points about Spacing apart, the ground's height under each, the goal last
        var route = new List<Vector3>();
        for (int k = 1; k < pulled.Count; k++)
        {
            var a = pulled[k - 1]; var b = pulled[k];
            int n = System.Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / Spacing));
            for (int i = 1; i <= n; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)n);
                route.Add(new Vector3(p.X, g.BareHeight(p.X, p.Y), p.Y));
            }
        }
        route[^1] = to;
        return route;
    }
}
