using I76.Formats.Assembly;
using System.Numerics;

namespace I76.Editor.Roads;

public enum SnapKind { None, Grid, Angle, RoadEnd, RoadMiddle, Structure }

/// <param name="Point">Where the cursor lands (ground x, z).</param>
/// <param name="Road">For <see cref="SnapKind.RoadEnd"/> and <see cref="SnapKind.RoadMiddle"/>: the road's index in the store.</param>
/// <param name="Index">The point index on that road (the end point's, or the nearest point's for a middle).</param>
/// <param name="AtStart">For a road end: the first point (the road continues the other way) rather than the last.</param>
/// <param name="Structure">For <see cref="SnapKind.Structure"/>: the end of the deck or ramp met.</param>
/// <param name="Hint">Something to tell the player (a road ended on the middle of another).</param>
public readonly record struct SnapResult(Vector2 Point, SnapKind Kind, int Road = -1, int Index = -1, bool AtStart = false, StructureAttach.Attach? Structure = null, string? Hint = null);

public sealed class SnapOptions
{
    /// <summary>Snap the heading of a new piece to multiples of <see cref="AngleStep"/> from the previous piece's (or the world's z axis for the first).</summary>
    public bool Angle = true;
    public float AngleStep = 15f;
    public bool Grid;
    public float GridSize = 10f;
    /// <summary>How close the cursor must be to an end, a road or a structure to snap to it (ground metres).</summary>
    public float Radius = 10f;
    public bool Structures = true;
    public bool Roads = true;
}

/// <summary>
/// Where the cursor snaps while drawing. The strongest wins: a structure's end (bridge, jump or ramp), then the end of an existing
/// road (the new road extends it), then the middle of one (allowed, with a hint: the format has no junctions), then the angle
/// and the grid.
/// </summary>
public static class RoadSnap
{
    public const string JunctionHint = "Ends on the middle of another road. The game has no junctions: this road stays separate, and AI cars will not turn here.";

    public static SnapResult Snap(Vector2 cursor, SnapOptions o, RoadStore roads, IReadOnlyList<StructureAttach.Attach>? structures, Vector2? anchor, Vector2? refDir)
    {
        float r2 = o.Radius * o.Radius;
        if (o.Structures && structures is not null)
        {
            StructureAttach.Attach? best = null; float bd = r2;
            foreach (var s in structures)
            {
                if (!s.RoadLevel) continue;
                float d = Vector2.DistanceSquared(cursor, new Vector2(s.Position.X, s.Position.Z));
                if (d <= bd) { bd = d; best = s; }
            }
            if (best is { } hit) return new SnapResult(new Vector2(hit.Position.X, hit.Position.Z), SnapKind.Structure, Structure: hit);
        }
        if (o.Roads)
        {
            int br = -1, bi = -1; bool start = false; float bd = r2;
            for (int i = 0; i < roads.Count; i++)
            {
                var pts = roads[i].Points;
                if (pts.Count < 2) continue;
                bool loop = RoadBuilder.IsClosed(roads[i]);
                if (loop) continue;
                foreach (var (idx, isStart) in new[] { (0, true), (pts.Count - 1, false) })
                {
                    float d = Vector2.DistanceSquared(cursor, pts[idx].Centre);
                    if (d <= bd) { bd = d; br = i; bi = idx; start = isStart; }
                }
            }
            if (br >= 0) return new SnapResult(roads[br].Points[bi].Centre, SnapKind.RoadEnd, br, bi, start);

            // the middle of a road: the nearest point on any centre line, within the radius
            float md = r2; Vector2 mp = default; int mr = -1, mi = -1;
            for (int i = 0; i < roads.Count; i++)
            {
                var pts = roads[i].Points;
                for (int k = 0; k + 1 < pts.Count; k++)
                {
                    Vector2 a = pts[k].Centre, b = pts[k + 1].Centre, ab = b - a;
                    float len2 = ab.LengthSquared();
                    if (len2 < 1e-6f) continue;
                    float t = Math.Clamp(Vector2.Dot(cursor - a, ab) / len2, 0, 1);
                    var p = a + ab * t;
                    float d = Vector2.DistanceSquared(cursor, p);
                    if (d < md) { md = d; mp = p; mr = i; mi = t < 0.5f ? k : k + 1; }
                }
            }
            if (mr >= 0) return new SnapResult(mp, SnapKind.RoadMiddle, mr, mi, Hint: JunctionHint);
        }
        var q = cursor;
        var kind = SnapKind.None;
        if (o.Angle && anchor is { } a0)
        {
            var d = cursor - a0;
            float len = d.Length();
            if (len > 1e-3f)
            {
                float refAng = refDir is { } rd && rd.LengthSquared() > 1e-9f ? MathF.Atan2(rd.X, rd.Y) : 0;   // from +z toward +x
                float ang = MathF.Atan2(d.X, d.Y) - refAng;
                float step = o.AngleStep * MathF.PI / 180;
                ang = MathF.Round(ang / step) * step + refAng;
                if (o.Grid) len = MathF.Max(o.GridSize, MathF.Round(len / o.GridSize) * o.GridSize);
                q = a0 + new Vector2(MathF.Sin(ang), MathF.Cos(ang)) * len;
                kind = SnapKind.Angle;
            }
        }
        else if (o.Grid)
        {
            q = new Vector2(MathF.Round(cursor.X / o.GridSize) * o.GridSize, MathF.Round(cursor.Y / o.GridSize) * o.GridSize);
            kind = SnapKind.Grid;
        }
        return new SnapResult(q, kind);
    }

    /// <summary>The points where a centre line crosses an existing road's (not at a shared end): the game has no junction there, so a car cannot turn.</summary>
    public static List<Vector2> Crossings(IReadOnlyList<Vector2> path, RoadStore roads, int ignoreRoad = -1)
    {
        var o = new List<Vector2>();
        if (path.Count < 2) return o;
        float x0 = path.Min(p => p.X) - 1, z0 = path.Min(p => p.Y) - 1, x1 = path.Max(p => p.X) + 1, z1 = path.Max(p => p.Y) + 1;
        for (int r = 0; r < roads.Count; r++)
        {
            if (r == ignoreRoad) continue;
            var b = RoadStore.Bounds(roads[r]);
            if (b.Z < x0 || b.X > x1 || b.W < z0 || b.Y > z1) continue;
            var rp = roads[r].Points;
            for (int k = 0; k + 1 < rp.Count; k++)
                for (int i = 0; i + 1 < path.Count; i++)
                    if (Intersect(path[i], path[i + 1], rp[k].Centre, rp[k + 1].Centre, out var x)
                        && Vector2.Distance(x, path[0]) > 2 && Vector2.Distance(x, path[^1]) > 2
                        && (o.Count == 0 || Vector2.Distance(o[^1], x) > 4)) o.Add(x);
        }
        return o;
    }

    private static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 p)
    {
        p = default;
        var r = b - a; var s = d - c;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-6f) return false;
        var q = c - a;
        float t = (q.X * s.Y - q.Y * s.X) / den, u = (q.X * r.Y - q.Y * r.X) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        p = a + r * t;
        return true;
    }
}
