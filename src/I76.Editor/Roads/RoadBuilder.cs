using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>The road looks the engine has: RSEG kind % 3 picks a texture family (Roads.TextureTable).</summary>
public enum RoadKind : uint { Asphalt = 0, Dirt = 1, Planks = 2 }

/// <summary>
/// Centre lines into RSEG roads, to the conventions every stock road follows (surveyed over the 1501 roads of both installs):
/// the edges are 10 apart (9.996 to 10.004; a wide road 20, see <see cref="WideWidth"/>), perpendicular to the central difference of the neighbouring centres (the first and last
/// to the step beside them), the left edge on the (-tz, tx) side of the right one for the direction of increasing index, points about 5
/// apart, a point's left y word the mean z of its edges, and a closed loop ending on its first point again.
/// </summary>
public static class RoadBuilder
{
    public const float Width = 10f, HalfWidth = 5f;
    /// <summary>
    /// The wide road: the original's map builder lays a road as "2 Lanes" or "4 Lanes" and writes the edges 10 or 20 apart, with the same kinds
    /// and textures (the four-lane roads never shipped: no stock road is wider than 10). The engine takes the edges as they are: it draws
    /// each quad from them, one texture across the road, and puts both edges on the ground, so a 20 wide road draws and drives like any other.
    /// </summary>
    public const float WideWidth = 20f;
    /// <summary>The widths the road tool offers: standard and wide.</summary>
    public static readonly float[] Widths = [Width, WideWidth];

    public static string WidthName(float width) => IsWideWidth(width) ? "Wide" : "Standard";
    /// <summary>Whether a width is the wide road's rather than the standard (anything over 15).</summary>
    public static bool IsWideWidth(float width) => width > (Width + WideWidth) / 2;

    /// <summary>A road's width: the median distance between its edges (the stock's 10, a wide road's 20); <see cref="Width"/> for a road with no points.</summary>
    public static float WidthOf(RoadEdit r)
    {
        if (r.Points.Count == 0) return Width;
        var w = r.Points.Select(p => Vector2.Distance(p.Left, p.Right)).Order().ToArray();
        float m = w[w.Length / 2];
        return m > 0.5f ? m : Width;
    }

    /// <summary>Whether the road is a standard one (about 10 wide: what the intersection pieces, bridges and ramps are made for).</summary>
    public static bool IsStandard(RoadEdit r) => !IsWideWidth(WidthOf(r));
    /// <summary>The right edge's y word: 0 or 36 in the stock files (the engine overwrites it, nothing reads it); the stock pattern is a few 0s, then 36s.</summary>
    public const int FlagBody = 36;

    public static string KindName(uint kind) => (kind % 3) switch { 0 => "Asphalt", 1 => "Dirt", _ => "Planks" };

    /// <summary>
    /// The road through <paramref name="centres"/>. <paramref name="startDir"/> and <paramref name="endDir"/>, when given, fix the heading
    /// of the first and last point (to meet a structure's end square-on, or to continue the road it extends). <paramref name="closed"/>
    /// makes the last point the first one again, with the same edges. <paramref name="width"/>: the distance between the edges.
    /// </summary>
    public static RoadEdit Build(IReadOnlyList<Vector2> centres, uint kind, Vector2? startDir = null, Vector2? endDir = null, bool closed = false, float width = Width)
    {
        var pts = new List<RoadEditPoint>(centres.Count);
        int n = centres.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 t;
            if (closed && n > 2 && (i == 0 || i == n - 1)) t = Dir(centres[1] - centres[n - 2]);
            else if (i == 0) t = startDir is { } sd ? Dir(sd) : Dir(centres[1] - centres[0]);
            else if (i == n - 1) t = endDir is { } ed ? Dir(ed) : Dir(centres[i] - centres[i - 1]);
            else t = Dir(centres[i + 1] - centres[i - 1]);
            pts.Add(Point(centres[i], t, i < 2 ? 0 : FlagBody, width));
        }
        if (closed && pts.Count > 3) pts[^1] = pts[0].Clone();
        return new RoadEdit(kind, pts);
    }

    /// <summary>One road point at <paramref name="c"/> heading <paramref name="t"/> (unit): the edges at c ± half the width across (5 for a standard road), left on (-tz, tx).</summary>
    public static RoadEditPoint Point(Vector2 c, Vector2 t, int flag = FlagBody, float width = Width)
    {
        var side = new Vector2(-t.Y, t.X) * (width / 2);
        var p = new RoadEditPoint(c + side, c - side) { Flag = flag };
        p.CentreZ = (p.Left.Y + p.Right.Y) / 2;
        return p;
    }

    private static Vector2 Dir(Vector2 d) => d.LengthSquared() < 1e-12f ? new Vector2(0, 1) : Vector2.Normalize(d);

    /// <summary>The centre line of a road (the edges' midpoints).</summary>
    public static List<Vector2> Centres(RoadEdit r) => r.Points.Select(p => p.Centre).ToList();

    /// <summary>The heading at a point from the road's own edges: the direction of increasing index (the right-to-left edge turned back).</summary>
    public static Vector2 Heading(RoadEdit r, int i)
    {
        var w = r.Points[i].Left - r.Points[i].Right;           // (-tz, tx) times the width
        return w.LengthSquared() < 1e-9f ? Dir(r.Points[Math.Min(i + 1, r.Points.Count - 1)].Centre - r.Points[Math.Max(i - 1, 0)].Centre)
                                         : Vector2.Normalize(new Vector2(w.Y, -w.X));
    }

    /// <summary>Whether the road comes back to its first point (a closed loop: the stock has 6 such roads with the last point exactly the first).</summary>
    public static bool IsClosed(RoadEdit r) => r.Points.Count > 3 && Vector2.Distance(r.Points[0].Centre, r.Points[^1].Centre) < 1f;

    /// <summary>The same road as it runs the other way (the lane the AI drives, and the left and right edges, swap with it).</summary>
    public static RoadEdit Reversed(RoadEdit r)
    {
        var c = Centres(r); c.Reverse();
        return Build(c, r.Kind, closed: IsClosed(r), width: WidthOf(r));
    }

    /// <summary>
    /// <paramref name="road"/> continued from its last point (or, with <paramref name="atStart"/>, its first) along <paramref name="extra"/>
    /// (centres after the join point, which is not repeated). The road's own points are kept as they are; the new points follow the
    /// road's heading at the join, and its width. Extending at the start puts the new points before it, so the new road runs the other way round the join.
    /// </summary>
    public static RoadEdit Extend(RoadEdit road, bool atStart, IReadOnlyList<Vector2> extra, Vector2? endDir = null)
    {
        var pts = road.Points.Select(p => p.Clone()).ToList();
        var add = new List<Vector2>(extra);
        if (!atStart)
        {
            var join = pts[^1].Centre;
            var all = new List<Vector2> { join }; all.AddRange(add);
            var ext = BuildTail(all, Heading(road, pts.Count - 1), endDir, WidthOf(road));
            pts.AddRange(ext.Skip(1));
            // back on its own first point: a closed loop, ending exactly as it starts
            if (pts.Count > 3 && Vector2.Distance(pts[^1].Centre, pts[0].Centre) < 1f) pts[^1] = pts[0].Clone();
        }
        else
        {
            // built outward from the first point: reversed travel direction, then flipped back
            var join = pts[0].Centre;
            var all = new List<Vector2> { join }; all.AddRange(add);
            var ext = BuildTail(all, -Heading(road, 0), endDir is { } e ? e : null, WidthOf(road));
            var flipped = ext.Skip(1).Reverse().Select(p => Flip(p)).ToList();
            pts.InsertRange(0, flipped);
            if (pts.Count > 3 && Vector2.Distance(pts[0].Centre, pts[^1].Centre) < 1f) pts[0] = pts[^1].Clone();
        }
        return new RoadEdit(road.Kind, pts) { Tail = (byte[])road.Tail.Clone() };
    }

    private static List<RoadEditPoint> BuildTail(IReadOnlyList<Vector2> centres, Vector2 startDir, Vector2? endDir, float width)
    {
        var r = Build(centres, 0, startDir, endDir, width: width);
        return r.Points;
    }

    /// <summary>
    /// The same road at another width: each point keeps its centre and its heading (from its own edges) and its flag word, the edges move
    /// in or out to <paramref name="width"/>. The road's own points are otherwise as they were.
    /// </summary>
    public static RoadEdit Rewidth(RoadEdit road, float width)
    {
        var pts = road.Points.Select((p, i) => Point(p.Centre, Heading(road, i), p.Flag, width)).ToList();
        if (IsClosed(road) && pts.Count > 3) pts[^1] = pts[0].Clone();
        return new RoadEdit(road.Kind, pts) { Tail = (byte[])road.Tail.Clone() };
    }

    /// <summary>The point as seen running the other way: the same centre, the edges swapped.</summary>
    private static RoadEditPoint Flip(RoadEditPoint p)
    {
        var f = new RoadEditPoint(p.Right, p.Left) { Flag = p.Flag };
        f.CentreZ = (f.Left.Y + f.Right.Y) / 2;
        return f;
    }
}
