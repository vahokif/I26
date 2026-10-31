using System.Numerics;
using I76.Sim.Terrain;

namespace I76.Sim.Ai;

/// <summary>
/// The planner's A* (PlanPath):
/// a 10-unit grid, 8 neighbours, step cost by the terrain class (the surface
/// id, top 3 bits of the height cell): 2/3/4 cost 0.5 and mark the node as
/// road, 6/7 cost 0.5, anything else 15 (plus 1e6 on an impassable cell while
/// avoid is on), ×√2 diagonally; an octile heuristic in world units, about
/// 20× the road cost, so the search runs greedy. 100 expansions a call;
/// done within 11 of the goal, or when the 1000-node pool runs out.
/// <para>
/// The structures are the original's: every node seen is in a tree by grid key
/// (z·100000 + x), and the open list is an AVL tree ordered by f (a double), then
/// the key.
/// A neighbour seen before is a duplicate: it goes back to the pool and the node
/// seen takes its place among the children. If that way is cheaper (its g and
/// step), the node is re-parented and the new g pushed down its subtree.
/// A relaxed node still in the open tree keeps its place there:
/// the original never reinserts it.
/// </para>
/// </summary>
public sealed partial class PathSearch
{
    private sealed class Node(int x, int z)
    {
        public readonly int X = x, Z = z;
        /// <summary>The grid key.</summary>
        public int Key => Z * 100000 + X;
        /// <summary>g: the cost to the parent (its g plus its step); step: this node's own step; h: the estimate; f: the sum.</summary>
        public double G, Step, H, F;
        /// <summary>The parent node.</summary>
        public Node? Parent;
        /// <summary>The children (8, or 9 with a bridge link); null until expanded.</summary>
        public Node?[]? Children;
        /// <summary>Road: a road node; Link: a bridge link was taken from here.</summary>
        public bool Road, Link;
        /// <summary>In the key tree (the start node is not).</summary>
        public bool Seen;
        /// <summary>The open tree's links and balance: −1 left-heavy, 0 even, +1 right-heavy.</summary>
        public Node? L, R;
        public int Bal;
        public bool Open;
    }

    /// <summary>
    /// The node pool (0x80 bytes a node): 1000 nodes. A search holds one node for
    /// every grid point it has seen (duplicates go back to the pool) and the start, and an expansion first takes all 8
    /// neighbours (9 with a bridge link) from it; when the pool runs dry (the original returns 0) the search ends at the
    /// node being expanded. A node exists before it is expanded, so a search lasts at most 10 frames of 100 expansions,
    /// and the one planner token moves on.
    /// </summary>
    public const int PoolSize = 1000;
    public const int Budget = 100;
    /// <summary>The map's extent in world units (0x2800 height samples at 0.2 per unit); outside it the class is 5.</summary>
    private const float MapSize = 0x2800 / 0.2f;
    private const double Sqrt2 = 1.4142135381698608, InvSqrt2 = 0.7071067932881648;

    /// <summary>The nav point; PlanPath updates it on every call while the search runs (the original reads it afresh).</summary>
    public Vector2 Goal { get; set; }
    /// <summary>Expansions so far.</summary>
    public int Expansions { get; private set; }
    public bool Finished => _end is not null;

    /// <summary>Every live node in the order the pool gave them out (the start first).</summary>
    private readonly List<Node> _all = [];
    /// <summary>The key tree: a key's node, if seen.</summary>
    private readonly Dictionary<int, Node> _seen = [];
    /// <summary>The open tree's root.</summary>
    private Node? _root;
    private Node? _start, _end;
    private readonly int _sx, _sz;

    private readonly BridgeLinks? _links;
    private PathSearch(Vector2 goal, BridgeLinks? links, int sx, int sz) { Goal = goal; _links = links; _sx = sx; _sz = sz; }

    /// <summary>The start is the position rounded to a multiple of 10 (div by 10, +10 when the remainder is over 5).</summary>
    public static int Round(float v)
    {
        int i = (int)v;
        int r = i % 10;
        return i - r + (r > 5 ? 10 : 0);
    }

    /// <summary>A new search; its first <see cref="Step"/> makes the start node and its neighbours.</summary>
    public static PathSearch Begin(Vector2 start, Vector2 goal, BridgeLinks? links = null) =>
        new(goal, links, Round(start.X), Round(start.Y));

    /// <summary>Class at a node: the surface id, 5 off the map.</summary>
    public static int Class(IGround g, float x, float z) =>
        x < 0 || z < 0 || x > MapSize || z > MapSize ? 5 : g.SurfaceId(x, z);

    /// <summary>Class part: the cost of one 10-unit step onto (x, z), before the diagonal factor.</summary>
    public static float StepCost(IGround g, float x, float z, bool avoid, out bool road)
    {
        int c = Class(g, x, z);
        road = c is 2 or 3 or 4;
        if (road || c is 6 or 7) return 0.5f;
        return 15f + (avoid && g.Impassable(x, z) ? 1e6f : 0f);
    }

    /// <summary>The step onto <paramref name="to"/> from <paramref name="from"/>, ×√2 when both x and z differ (a bridge's far end too).</summary>
    private static double Cost(IGround g, Node to, Node from, bool avoid)
    {
        double c = StepCost(g, to.X, to.Z, avoid, out to.Road);
        return to.X != from.X && to.Z != from.Z ? c * Sqrt2 : c;
    }

    /// <summary>From the pool, or null when it is spent.</summary>
    private Node? Alloc(int x, int z, Node? parent)
    {
        if (_all.Count >= PoolSize) return null;
        var n = new Node(x, z) { Parent = parent };
        if (parent is not null) n.G = n.F = parent.G + parent.Step;
        _all.Add(n);
        return n;
    }

    private void Free(Node n) => _all.RemoveAt(_all.LastIndexOf(n));

    private static readonly (int, int)[] Around = [(-10, -10), (0, -10), (10, -10), (-10, 0), (10, 0), (-10, 10), (0, 10), (10, 10)];

    /// <summary>The 8 neighbours (by rows of z), then a bridge's far end; false when the pool runs out.</summary>
    private bool Expand(Node cur)
    {
        var kids = new Node?[9];
        int k = 0;
        foreach (var (dx, dz) in Around)
            if ((kids[k++] = Alloc(cur.X + dx, cur.Z + dz, cur)) is null) { cur.Children = kids[..(k - 1)]; return false; }
        if (_links is { } bl && bl.TryFar(cur.X, cur.Z, out var far))
        {
            cur.Link = true;
            if ((kids[8] = Alloc(far.X, far.Z, cur)) is null) { cur.Children = kids[..8]; return false; }
            cur.Children = kids;
            return true;
        }
        cur.Link = false;
        cur.Children = kids[..8];
        return true;
    }

    /// <summary>The octile distance to the goal, from float operands as the original's.</summary>
    private double Octile(Node n)
    {
        float dx = MathF.Abs(Goal.X - n.X), dz = MathF.Abs(Goal.Y - n.Z);
        float mx = MathF.Max(dx, dz), mn = MathF.Min(dx, dz);
        return ((double)mx - mn) + mn * 1.414;
    }

    private bool Near(Node n)
    {
        float dx = n.X - Goal.X, dz = n.Z - Goal.Y;
        return dz * dz + dx * dx < 121f;
    }

    /// <summary>Up to 100 expansions; true when the search is over (goal reached, the open tree spent, or the pool spent).</summary>
    public bool Step(IGround g, bool avoid)
    {
        if (_end is not null) return true;
        if (_start is null)
        {
            // the first call: the start node (in neither tree) expanded at once, its neighbours costed in float
            _start = Alloc(_sx, _sz, null)!;
            _start.Road = Class(g, _sx, _sz) is >= 2 and <= 4;
            Expand(_start);
            foreach (var c in _start.Children!)
            {
                if (c is null) continue;
                c.G = _start.Step + _start.G;
                c.Step = Cost(g, c, _start, avoid);
                float h = (float)Octile(c);
                c.H = h;
                c.F = (float)c.G + h + (float)c.Step;
                Insert(c);
                c.Seen = true;
                _seen[c.Key] = c;
            }
        }
        int k = 0;
        bool spent = false;
        var cur = PopMin();
        while (true)
        {
            if (_root is null || Near(cur) || k >= Budget) break;
            if (!Expand(cur)) { spent = true; break; }
            var kids = cur.Children!;
            for (int i = 0; i < kids.Length; i++)
            {
                var c = kids[i]!;
                c.G = cur.Step + cur.G;
                c.Step = Cost(g, c, cur, avoid);
                c.F = c.H + c.G + c.Step;
                if (_seen.TryGetValue(c.Key, out var found))
                {
                    if (found.Step + found.G > c.Step + c.G)
                    {
                        found.Parent = cur;
                        Relax(found, cur, 0);
                    }
                    kids[i] = found;
                    Free(c);
                }
                else
                {
                    c.H = Octile(c);
                    c.F = c.Step + c.G + c.H;
                    Insert(c);
                    c.Seen = true;
                    _seen[c.Key] = c;
                }
            }
            k++;
            cur = PopMin();
        }
        Expansions += k;
        // a call that stops for its 100 expansions loses the node it popped last; with the open tree spent the search
        // ends there (the original's next call would pop from the empty tree)
        if (!Near(cur) && !spent && _root is not null) return false;
        _end = cur;
        return true;
    }

    /// <summary>
    /// A node listed under <paramref name="parent"/> that has another parent moves to it
    /// when that makes its f smaller (its step rescaled by √2 or 1/√2 when the diagonality changes); a node whose
    /// parent this is takes the parent's g and passes it on to the children listed under it.
    /// </summary>
    private void Relax(Node n, Node parent, int depth)
    {
        if (n.Parent is not { } p || depth > PoolSize) return;
        bool moved = false;
        if (p != parent)
        {
            double g2 = parent.G + parent.Step, step = n.Step;
            if (n.X == p.X || n.Z == p.Z)
            {
                if (n.X != parent.X && n.Z != parent.Z) step *= Sqrt2;
            }
            else if (n.X == parent.X || n.Z == parent.Z) step *= InvSqrt2;
            double f2 = n.H + step + g2;
            if (f2 < n.F) { n.G = g2; n.F = f2; n.Step = step; n.Parent = parent; moved = true; }
        }
        if (n.Parent != parent) return;
        if (!moved)
        {
            n.G = parent.G + parent.Step;
            n.F = n.Step + n.H + n.G;
        }
        if (n.Children is { } kids)
            foreach (var c in kids)
                if (c is not null) Relax(c, n, depth + 1);
    }

    // ---- the open tree: an AVL ordered by (f, key), comparing the nodes' f as it is now ----

    private static bool Less(Node a, Node b) => a.F < b.F || (a.F == b.F && a.Key < b.Key);

    private void Insert(Node n)
    {
        n.L = n.R = null; n.Bal = 0; n.Open = true;
        Ins(ref _root, n);
    }

    /// <summary>Insert, with the rotations: equal goes right; true when the subtree grew.</summary>
    private static bool Ins(ref Node? t, Node n)
    {
        if (t is null) { t = n; return true; }
        if (!Less(n, t))
        {
            if (!Ins(ref t.R, n)) return false;
            if (t.Bal < 0) { t.Bal = 0; return false; }
            if (t.Bal == 0) { t.Bal = 1; return true; }
            t = RightHeavy(t);
            return false;
        }
        if (!Ins(ref t.L, n)) return false;
        if (t.Bal > 0) { t.Bal = 0; return false; }
        if (t.Bal == 0) { t.Bal = -1; return true; }
        t = LeftHeavy(t);
        return false;
    }

    /// <summary>A node two heavier on the right after an insert: the RR or RL rotation.</summary>
    private static Node RightHeavy(Node t)
    {
        var r = t.R!;
        if (r.Bal > 0)
        {
            t.R = r.L; r.L = t;
            t.Bal = 0; r.Bal = 0;
            return r;
        }
        return Double(t, r, r.L!, right: true);
    }

    /// <summary>A node two heavier on the left after an insert: the LL or LR rotation.</summary>
    private static Node LeftHeavy(Node t)
    {
        var l = t.L!;
        if (l.Bal < 0)
        {
            t.L = l.R; l.R = t;
            t.Bal = 0; l.Bal = 0;
            return l;
        }
        return Double(t, l, l.R!, right: false);
    }

    private static Node Double(Node t, Node c, Node m, bool right)
    {
        if (right)
        {
            c.L = m.R; m.R = c; t.R = m.L; m.L = t;
            t.Bal = m.Bal > 0 ? -1 : 0;
            c.Bal = m.Bal < 0 ? 1 : 0;
        }
        else
        {
            c.R = m.L; m.L = c; t.L = m.R; m.R = t;
            t.Bal = m.Bal < 0 ? 1 : 0;
            c.Bal = m.Bal > 0 ? -1 : 0;
        }
        m.Bal = 0;
        return m;
    }

    private Node PopMin()
    {
        if (_root is null) return _end ?? _start!;
        Node min = null!;
        PopMin(ref _root, ref min);
        min.L = min.R = null; min.Open = false;
        return min;
    }

    /// <summary>The leftmost node out; true when the subtree shrank (the original rebalances).</summary>
    private static bool PopMin(ref Node? t, ref Node min)
    {
        if (t!.L is null) { min = t; t = t.R; return true; }
        if (!PopMin(ref t.L, ref min)) return false;
        if (t.Bal < 0) { t.Bal = 0; return true; }
        if (t.Bal == 0) { t.Bal = 1; return false; }
        var r = t.R!;
        if (r.Bal >= 0)
        {
            t.R = r.L; r.L = t;
            bool shrank = r.Bal > 0;
            if (shrank) { t.Bal = 0; r.Bal = 0; }
            else { t.Bal = 1; r.Bal = -1; }
            t = r;
            return shrank;
        }
        t = Double(t, r, r.L!, right: true);
        return true;
    }

    /// <summary>The found path's nodes from the end back to the start (x, z, road, bridge link).</summary>
    public List<(int X, int Z, bool Road, bool Link)> Chain()
    {
        var o = new List<(int, int, bool, bool)>();
        for (var n = _end; n is not null && o.Count <= PoolSize; n = n.Parent) o.Add((n.X, n.Z, n.Road, n.Link));
        return o;
    }
}
