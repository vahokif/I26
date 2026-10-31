using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// Short gaps in the missions' road data, closed for the modern look.
///
/// Many road segments stop a few units short of what they run into: dozens of RSEG starts, in the melee arenas, the
/// trip and the scenarios, begin one road step (about 5 units) before the arm of the intersection piece they leave
/// (M06's west junction at 2280, 48545; M03, M04, M09, T02, T06, ...), and some segments that meet end to end at an
/// angle leave a notch on the outside of the bend (M06 at 2460, 49090). The original draws the same strips and the
/// same pieces, so it shows the same strips of bare ground across the road (checked in the real game at M06's
/// junction); the 1997 look keeps them.
///
/// <see cref="Fill"/> finds every segment end that runs, on all of its lanes, into road (a road quad or a piece
/// top) within <see cref="MaxGap"/> along the road's direction, and gives it a quad over the gap: the end's edge
/// carried forward by the longest lane's gap plus <see cref="Overlap"/>.
/// </summary>
public static class RoadGaps
{
    /// <summary>A gap longer than this (in units, along the road) is left alone: under one road step and a half.</summary>
    public const float MaxGap = 8f;
    /// <summary>Gaps shorter than this are not worth a quad (the layer's feathered edges cover them).</summary>
    public const float MinGap = 0.2f;
    /// <summary>How far a filling quad reaches into what it runs into.</summary>
    public const float Overlap = 0.5f;
    /// <summary>Where across the road the gap is measured (0 right edge, 1 left edge).</summary>
    private static readonly float[] Lanes = [0.15f, 0.5f, 0.85f];
    private const float Step = 0.1f, Cell = 10f;

    /// <summary>
    /// The quads that close the short gaps at the ends of <paramref name="segments"/> (whole RSEGs, Roads.Segments),
    /// as two-point pieces of the segment's kind, ordered like the segment (so u runs across the road the same way),
    /// heights from <paramref name="height"/>. What counts as road: <paramref name="quads"/> (the segments' own,
    /// RoadRaster.Flatten) and <paramref name="tris"/> (the piece tops painted with them).
    /// </summary>
    public static List<RoadPiece> Fill(IReadOnlyList<RoadPiece> segments, IReadOnlyList<RoadRaster.FlatQuad> quads,
        IReadOnlyList<RoadRaster.FlatTri> tris, Func<double, double, double> height)
    {
        var grid = new Dictionary<(int, int), List<(Vector2 A, Vector2 B, Vector2 C)>>();
        void Add(Vector2 a, Vector2 b, Vector2 c)
        {
            if (MathF.Abs(Cross(b - a, c - a)) < 1e-4f) return;   // no area (a repeated road point): covers nothing
            var mn = Vector2.Min(Vector2.Min(a, b), c); var mx = Vector2.Max(Vector2.Max(a, b), c);
            int x0 = (int)MathF.Floor(mn.X / Cell), x1 = (int)MathF.Floor(mx.X / Cell);
            int z0 = (int)MathF.Floor(mn.Y / Cell), z1 = (int)MathF.Floor(mx.Y / Cell);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 4096) return;   // a degenerate sliver across the map
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = [];
                    l.Add((a, b, c));
                }
        }
        foreach (var q in quads) { Add(q.A, q.B, q.C); Add(q.A, q.C, q.D); }
        foreach (var t in tris) Add(t.A, t.B, t.C);
        bool Covered(Vector2 p)
        {
            if (!grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l)) return false;
            foreach (var (a, b, c) in l) if (Inside(p, a, b, c)) return true;
            return false;
        }

        var o = new List<RoadPiece>();
        foreach (var seg in segments)
        {
            var pts = seg.Points;
            if (pts.Length < 2) continue;
            foreach (bool start in new[] { true, false })
            {
                int end = start ? 0 : pts.Length - 1, step = start ? 1 : -1;
                var e = pts[end];
                var m = Mid(e);
                // the road's direction out of this end: from the nearest point that is not on top of it
                Vector2 d = default;
                for (int k = end + step; k >= 0 && k < pts.Length; k += step)
                    if (Vector2.DistanceSquared(m, Mid(pts[k])) > 1e-4f) { d = Vector2.Normalize(m - Mid(pts[k])); break; }
                if (d == default) continue;
                float gap = 0;
                foreach (var lane in Lanes)
                {
                    var p = Vector2.Lerp(XZ(e.Right), XZ(e.Left), lane);
                    float hit = -1;
                    for (float t = Step; t <= MaxGap + Step; t += Step)
                        if (Covered(p + d * t)) { hit = t - Step; break; }
                    if (hit < 0) { gap = -1; break; }   // a lane that runs into nothing: a dead end, or not a gap
                    gap = MathF.Max(gap, hit);
                }
                if (gap < MinGap) continue;
                float ext = gap + Overlap;
                RoadEdge Moved(RoadEdge r) => new(Lift(XZ(r.Left) + d * ext), Lift(XZ(r.Right) + d * ext));
                Vector3 Lift(Vector2 v) => new(v.X, (float)height(v.X, v.Y), v.Y);
                o.Add(new RoadPiece(seg.Kind, start ? [Moved(e), e] : [e, Moved(e)]));
            }
        }
        return o;
    }

    private static Vector2 XZ(Vector3 v) => new(v.X, v.Z);
    private static Vector2 Mid(RoadEdge e) => (XZ(e.Left) + XZ(e.Right)) * 0.5f;

    private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
}
