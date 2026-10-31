using System.Numerics;

namespace I76.Editor.Roads;

/// <summary>
/// Curves for the road tool, in the ground plane (Vector2 = game x, z): a quadratic or cubic Bezier, a spline through clicks,
/// rounded corners for a chain of straight pieces, and the resampling of any of them into equally spaced road points.
/// </summary>
public static class RoadCurves
{
    /// <summary>The stock roads' point spacing: the median step of 202 000 points is 5.09 units, the 10th percentile 5.00, the 99th 8.3.</summary>
    public const float Spacing = 5f;
    /// <summary>The least road: a road of two points has no piece in the engine (the original drops a single quad).</summary>
    public const int MinPoints = 3;

    public static List<Vector2> Quadratic(Vector2 a, Vector2 control, Vector2 b, int steps = 128)
    {
        var o = new List<Vector2>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps, u = 1 - t;
            o.Add(u * u * a + 2 * u * t * control + t * t * b);
        }
        return o;
    }

    public static List<Vector2> Cubic(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, int steps = 160)
    {
        var o = new List<Vector2>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps, u = 1 - t;
            o.Add(u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b);
        }
        return o;
    }

    /// <summary>A centripetal Catmull-Rom spline through the points (it does not overshoot or loop on uneven clicks), each span flattened into <paramref name="perSpan"/> steps.</summary>
    public static List<Vector2> Spline(IReadOnlyList<Vector2> pts, bool closed = false, int perSpan = 24)
    {
        var o = new List<Vector2>();
        int n = pts.Count;
        if (n < 2) return [.. pts];
        if (n == 2) return [pts[0], pts[1]];
        Vector2 P(int i) => closed ? pts[((i % n) + n) % n] : pts[Math.Clamp(i, 0, n - 1)];
        int spans = closed ? n : n - 1;
        for (int s = 0; s < spans; s++)
        {
            Vector2 p0 = P(s - 1), p1 = P(s), p2 = P(s + 1), p3 = P(s + 2);
            // a clamped end repeats a point: mirror it instead, so the end tangent follows the last span
            if (!closed && s == 0) p0 = p1 * 2 - p2;
            if (!closed && s == spans - 1) p3 = p2 * 2 - p1;
            float t0 = 0, t1 = t0 + Knot(p0, p1), t2 = t1 + Knot(p1, p2), t3 = t2 + Knot(p2, p3);
            for (int i = 0; i < perSpan; i++)
            {
                float t = t1 + (t2 - t1) * i / perSpan;
                var a1 = Lerp(p0, p1, t0, t1, t); var a2 = Lerp(p1, p2, t1, t2, t); var a3 = Lerp(p2, p3, t2, t3, t);
                var b1 = Lerp(a1, a2, t0, t2, t); var b2 = Lerp(a2, a3, t1, t3, t);
                o.Add(Lerp(b1, b2, t1, t2, t));
            }
        }
        o.Add(closed ? pts[0] : pts[^1]);
        return o;
    }

    private static float Knot(Vector2 a, Vector2 b) => MathF.Max(MathF.Sqrt(Vector2.Distance(a, b)), 1e-3f);
    private static Vector2 Lerp(Vector2 a, Vector2 b, float ta, float tb, float t) => tb - ta < 1e-6f ? a : a + (b - a) * ((t - ta) / (tb - ta));

    /// <summary>
    /// Rounds the corners of a polyline (straight pieces chained with Shift): each interior vertex is replaced by a quadratic arc
    /// of about <paramref name="radius"/>, as large as the two pieces beside it allow (half of each). A road turns at most 9 degrees
    /// a point in 99 % of the stock points, which is a radius of 32 at 5 spacing.
    /// </summary>
    public static List<Vector2> Fillet(IReadOnlyList<Vector2> poly, float radius = 32f, float minTurnDegrees = 0f)
    {
        var o = new List<Vector2>();
        int n = poly.Count;
        if (n < 3) return [.. poly];
        o.Add(poly[0]);
        for (int i = 1; i < n - 1; i++)
        {
            var d0 = poly[i] - poly[i - 1]; var d1 = poly[i + 1] - poly[i];
            float l0 = d0.Length(), l1 = d1.Length();
            if (l0 < 1e-3f || l1 < 1e-3f) { o.Add(poly[i]); continue; }
            d0 /= l0; d1 /= l1;
            float turn = MathF.Acos(Math.Clamp(Vector2.Dot(d0, d1), -1f, 1f));
            if (turn < 0.02f || turn * 180 / MathF.PI < minTurnDegrees) { o.Add(poly[i]); continue; }
            float setback = MathF.Min(radius * MathF.Tan(turn / 2), MathF.Min(l0, l1) * (i == 1 || i == n - 2 ? 0.95f : 0.5f));
            var a = poly[i] - d0 * setback; var b = poly[i] + d1 * setback;
            foreach (var p in Quadratic(a, poly[i], b, 24)) o.Add(p);
        }
        o.Add(poly[^1]);
        return o;
    }

    public static float Length(IReadOnlyList<Vector2> poly)
    {
        float l = 0;
        for (int i = 1; i < poly.Count; i++) l += Vector2.Distance(poly[i - 1], poly[i]);
        return l;
    }

    /// <summary>
    /// Equal steps along the polyline, the ends kept: a whole number of steps of <paramref name="spacing"/> or a little more or
    /// less (a 23 long curve is 5 steps of 4.6). At least <see cref="MinPoints"/> points; an empty list for a polyline with no length.
    /// </summary>
    public static List<Vector2> Resample(IReadOnlyList<Vector2> poly, float spacing = Spacing)
    {
        float total = Length(poly);
        if (poly.Count < 2 || total < 1e-3f) return [];
        int steps = Math.Max(MinPoints - 1, (int)MathF.Round(total / spacing));
        var o = new List<Vector2>(steps + 1) { poly[0] };
        float step = total / steps, want = step, run = 0;
        int seg = 1;
        for (int k = 1; k < steps; k++)
        {
            while (seg < poly.Count - 1 && run + Vector2.Distance(poly[seg - 1], poly[seg]) < want)
            { run += Vector2.Distance(poly[seg - 1], poly[seg]); seg++; }
            float l = Vector2.Distance(poly[seg - 1], poly[seg]);
            float f = l < 1e-6f ? 0 : (want - run) / l;
            o.Add(Vector2.Lerp(poly[seg - 1], poly[seg], Math.Clamp(f, 0, 1)));
            want += step;
        }
        o.Add(poly[^1]);
        return o;
    }

    /// <summary>The smallest circumradius over consecutive triples (the turn of the road: a quad folds on itself inside a bend tighter than half the width). Infinity for a straight road.</summary>
    public static float MinRadius(IReadOnlyList<Vector2> pts)
    {
        float min = float.PositiveInfinity;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i], c = pts[i + 1];
            float ab = Vector2.Distance(a, b), bc = Vector2.Distance(b, c), ca = Vector2.Distance(c, a);
            float area2 = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y));   // twice the triangle's area
            if (area2 < 1e-6f || ab < 1e-3f || bc < 1e-3f) continue;
            min = MathF.Min(min, ab * bc * ca / (2 * area2));
        }
        return min;
    }

    /// <summary>The largest turn between consecutive steps, in degrees (stock: 99 % of points turn under 9).</summary>
    public static float MaxTurnDegrees(IReadOnlyList<Vector2> pts)
    {
        float max = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var a = pts[i] - pts[i - 1]; var b = pts[i + 1] - pts[i];
            if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) continue;
            float ang = MathF.Abs(MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b))) * 180 / MathF.PI;
            max = MathF.Max(max, ang);
        }
        return max;
    }
}
