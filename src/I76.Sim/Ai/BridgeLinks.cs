using System.Numerics;
using I76.Sim.Collision;

namespace I76.Sim.Ai;

/// <summary>
/// The bridge tree, its use as the A*'s 9th
/// neighbour and the bridge pass of the waypoint build.
/// </summary>
public sealed class BridgeLinks
{
    private readonly record struct Entry(int FarX, int FarZ, StaticObject Bridge);

    // the tree is keyed z + 100000·x and matched within 1.0; the coordinates are integers, so an
    // exact key. A key entered twice goes below the first, which a lookup meets first: first wins
    private readonly Dictionary<long, Entry> _tree = [];
    public bool Any => _tree.Count > 0;

    private static long Key(int x, int z) => z + 100000L * x;

    /// <summary>At a grid node: the bridge entered there, none when its object is destroyed.</summary>
    private Entry? Find(int x, int z) =>
        _tree.TryGetValue(Key(x, z), out var e) && !e.Bridge.Destroyed ? e : null;

    /// <summary>The original's 9th neighbour: the far end of the bridge entered at this grid node.</summary>
    public bool TryFar(int x, int z, out (int X, int Z) far)
    {
        if (Find(x, z) is { } e) { far = (e.FarX, e.FarZ); return true; }
        far = default;
        return false;
    }

    /// <summary>
    /// For every type-0xc object: the deck node's bounds,
    /// 5 beyond each end along its local z, through its world transform; the ends rounded outward to the
    /// 10-unit grid; then every 5 across the width (from the min corner's side toward the max's, while more
    /// than 2.5 short of one step past it) a pair of entries, each end linking to the other. Only a deck
    /// lying within 0.9 of a world axis is entered ("bridge not orthoginal" otherwise).
    /// </summary>
    public static BridgeLinks From(StaticWorld world)
    {
        var l = new BridgeLinks();
        foreach (var o in world.Objects)
            if (o.Type == 0xc && o.Deck is { } d) l.Enter(o, d);
        return l;
    }

    // The original's point transform: rows are the local axes, the translation added last
    private static Vector3 ToWorld(NodeFrame d, Vector3 p) => new(
        (float)((double)d.Rot.Right.X * p.X + (double)d.Rot.Up.X * p.Y + (double)d.Rot.Forward.X * p.Z + d.Origin.X),
        (float)((double)d.Rot.Right.Y * p.X + (double)d.Rot.Up.Y * p.Y + (double)d.Rot.Forward.Y * p.Z + d.Origin.Y),
        (float)((double)d.Rot.Right.Z * p.X + (double)d.Rot.Up.Z * p.Y + (double)d.Rot.Forward.Z * p.Z + d.Origin.Z));

    // ftol truncates; the remainder takes the dividend's sign
    private static float Down(float v) { int i = (int)v; return i - i % 10; }
    private static float Up(float v) { int i = (int)v; return 10 - i % 10 + i; }
    // Floor(v + 0.5)
    private static int R(float v) => (int)System.Math.Floor((double)v + 0.5);

    private void Insert(float x, float z, StaticObject o, float farX, float farZ) =>
        _tree.TryAdd(Key(R(x), R(z)), new Entry(R(farX), R(farZ), o));

    private void Enter(StaticObject o, NodeFrame d)
    {
        var a = ToWorld(d, d.Min with { Z = d.Min.Z - 5f });
        var b = ToWorld(d, d.Max with { Z = d.Max.Z + 5f });
        float fz = d.Rot.Forward.Z, fx = d.Rot.Forward.X;
        bool alongZ;
        float endA, endB, lat, latB;
        if (fz > 0.9f) { alongZ = true; endA = Down(a.Z); endB = Up(b.Z); lat = a.X; latB = b.X; }
        else if (fz < -0.9f) { alongZ = true; endA = Up(a.Z); endB = Down(b.Z); lat = a.X; latB = b.X; }
        else if (fx > 0.9f) { alongZ = false; endA = Down(a.X); endB = Up(b.X); lat = a.Z; latB = b.Z; }
        else if (fx < -0.9f) { alongZ = false; endA = Up(a.X); endB = Down(b.X); lat = a.Z; latB = b.Z; }
        else return;    // "bridge not orthoginal"
        float step = lat < latB ? 5f : -5f;
        while (System.Math.Abs((double)step + latB - lat) > 2.5)
        {
            if (alongZ)
            {
                Insert(lat, endA, o, lat, endB);
                Insert(lat, endB, o, lat, endA);
            }
            else
            {
                Insert(endA, lat, o, endB, lat);
                Insert(endB, lat, o, endA, lat);
            }
            lat += step;
        }
    }

    /// <summary>Unit(b − a) · unit(p − a) in x/z, 1 when either is shorter than 0.01.</summary>
    public static float Ahead(float ax, float az, float bx, float bz, float px, float pz)
    {
        float ux = bx - ax, uz = bz - az;
        float l = ux * ux + uz * uz;
        if (l > -1e-4f && l < 1e-4f) return 1f;
        double iu = 1.0 / System.Math.Sqrt(l);
        if (iu > 100.0) return 1f;
        float wx = px - ax, wz = pz - az;
        float m = wx * wx + wz * wz;
        if (m > -1e-4f && m < 1e-4f) return 1f;
        double iw = 1.0 / System.Math.Sqrt(m);
        if (iw > 100.0) return 1f;
        return (float)(iw * wz * (float)(iu * uz) + iw * wx * (float)(iu * ux));
    }

    /// <summary>
    /// A node flagged 2 (a node the A* expanded at a bridge end, whether or not the path
    /// took the link) and the node after it are both moved across the deck: which side of the deck's
    /// origin the node lies along its forward axis (r1) and across it (r2, beyond ±0.01) pick a shift of
    /// ±1.55, ±2.45 or ±7.55 along the deck's right axis. Then the node, points every 5 units (flag 2) while
    /// more than 5 from the next, and the next with its own flag; the next is consumed. The last node is
    /// always appended, as moved (so a pair ending the list leaves it twice).
    /// </summary>
    public List<PathNode> Pass(IReadOnlyList<PathNode> input)
    {
        var a = input.ToArray();
        var o = new List<PathNode>();
        int n = a.Length;
        if (n == 0) return o;
        int i = 0;
        while (i < n - 1)
        {
            // the original reads the tree record unchecked (a destroyed bridge would fault); here the node passes through
            if (a[i].Flag == 2f && Find((int)a[i].X, (int)a[i].Z) is { } e && e.Bridge.Deck is { } d)
            {
                var f = d.Rot.Forward; var r = d.Rot.Right;
                float px = d.Origin.X, pz = d.Origin.Z;
                float r1 = Ahead(px, pz, px + f.X, pz + f.Z, a[i].X, a[i].Z);
                float r2 = Ahead(px, pz, px + r.X, pz + r.Z, a[i].X, a[i].Z);
                float k = r1 < 0f
                    ? (r2 < -0.01f ? -7.55f : r2 > 0.01f ? 2.45f : -1.55f)
                    : (r2 > 0.01f ? 7.55f : r2 < -0.01f ? -2.45f : 1.55f);
                PathNode Shift(PathNode p) => p with { X = (float)(p.X - (double)r.X * k), Z = (float)(p.Z - (double)r.Z * k) };
                a[i] = Shift(a[i]);
                a[i + 1] = Shift(a[i + 1]);
                var p0 = a[i]; var p1 = a[i + 1];
                float dx = p1.X - p0.X, dz = p1.Z - p0.Z;
                double l2 = (double)dx * dx + (double)dz * dz;
                float k5 = l2 > -1e-4 && l2 < 1e-4 ? 1e6f : (float)(1.0 / System.Math.Sqrt(l2) * 5.0);
                o.Add(p0);
                float sx = (float)(k5 * (double)dx), sz = (float)(k5 * (double)dz);
                double qx = p0.X, qz = p0.Z;
                static double D2(PathNode e, double x, double z) => (e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z);
                if (25.0 < D2(p1, qx, qz))
                    do
                    {
                        qx += sx; qz += sz;
                        o.Add(new PathNode((float)qx, (float)qz, 2f));
                    } while (25.0 < D2(p1, qx, qz));
                o.Add(p1);
                i += 2;
            }
            else
            {
                o.Add(a[i]);
                i++;
            }
        }
        o.Add(a[n - 1]);
        return o;
    }
}
