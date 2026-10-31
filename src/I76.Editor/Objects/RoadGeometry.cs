using System.Numerics;
using I76.Editor.Roads;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Objects;

/// <summary>A road's centre line with its width at each point, for snapping and overlap tests (game frame, x and z).</summary>
public sealed class RoadPolyline
{
    public int Index { get; }
    public uint Kind { get; }
    public Vector2[] Centres { get; }
    public float[] HalfWidths { get; }
    public Vector2 BoundsMin { get; }
    public Vector2 BoundsMax { get; }

    public RoadPolyline(int index, RoadEdit road)
    {
        Index = index; Kind = road.Kind;
        Centres = [.. road.Points.Select(p => p.Centre)];
        HalfWidths = [.. road.Points.Select(p => Vector2.Distance(p.Left, p.Right) / 2)];
        var mn = new Vector2(float.MaxValue); var mx = new Vector2(float.MinValue);
        for (int i = 0; i < Centres.Length; i++)
        {
            mn = Vector2.Min(mn, Centres[i] - new Vector2(HalfWidths[i])); mx = Vector2.Max(mx, Centres[i] + new Vector2(HalfWidths[i]));
        }
        BoundsMin = mn; BoundsMax = mx;
    }
}

/// <summary>The nearest point on a road: where, which way the road runs there, how wide it is.</summary>
public readonly record struct RoadHit(int Road, int Segment, Vector2 Point, Vector2 Tangent, float HalfWidth, float Distance)
{
    /// <summary>The unit vector across the road, to its left when it runs along the tangent (game x, z: left of +z is -x... it is the tangent turned a quarter counter-clockwise on the map).</summary>
    public Vector2 Normal => new(-Tangent.Y, Tangent.X);
}

public static class RoadGeometry
{
    public static List<RoadPolyline> Build(IEnumerable<RoadEdit> roads) => [.. roads.Select((r, i) => new RoadPolyline(i, r)).Where(p => p.Centres.Length >= 2)];

    /// <summary>The nearest point on any road centre line within <paramref name="range"/> metres of <paramref name="p"/>, or null.</summary>
    public static RoadHit? Nearest(IReadOnlyList<RoadPolyline> roads, Vector2 p, float range)
    {
        RoadHit? best = null;
        float bestD = range;
        foreach (var r in roads)
        {
            if (p.X < r.BoundsMin.X - range || p.X > r.BoundsMax.X + range || p.Y < r.BoundsMin.Y - range || p.Y > r.BoundsMax.Y + range) continue;
            for (int i = 0; i + 1 < r.Centres.Length; i++)
            {
                var a = r.Centres[i]; var ab = r.Centres[i + 1] - a;
                float len2 = ab.LengthSquared();
                if (len2 < 1e-6f) continue;
                float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
                var q = a + ab * t;
                float d = Vector2.Distance(p, q);
                if (d >= bestD) continue;
                bestD = d;
                best = new RoadHit(r.Index, i, q, ab / MathF.Sqrt(len2), r.HalfWidths[i] + (r.HalfWidths[i + 1] - r.HalfWidths[i]) * t, d);
            }
        }
        return best;
    }

    /// <summary>An open end of a road: where it stops, which way it points out of the road, and the road.</summary>
    public readonly record struct OpenEnd(Vector2 Point, Vector2 Out, float Distance, RoadHit Hit);

    /// <summary>The nearest open end (first or last point) of a road within <paramref name="range"/> of <paramref name="p"/>, or null.</summary>
    public static OpenEnd? NearestEnd(IReadOnlyList<RoadPolyline> roads, Vector2 p, float range)
    {
        OpenEnd? best = null;
        foreach (var r in roads)
        {
            int n = r.Centres.Length;
            if (n < 2 || Vector2.DistanceSquared(r.Centres[0], r.Centres[n - 1]) < 1) continue;   // a loop has no end
            for (int e = 0; e < 2; e++)
            {
                var pt = e == 0 ? r.Centres[0] : r.Centres[n - 1];
                float d = Vector2.Distance(p, pt);
                if (d > range || best is { } b && d >= b.Distance) continue;
                var dir = e == 0 ? r.Centres[0] - r.Centres[1] : r.Centres[n - 1] - r.Centres[n - 2];
                if (dir.LengthSquared() < 1e-6f) continue;
                dir = Vector2.Normalize(dir);
                int seg = e == 0 ? 0 : n - 2;
                best = new OpenEnd(pt, dir, d, new RoadHit(r.Index, seg, pt, e == 0 ? -dir : dir, r.HalfWidths[e == 0 ? 0 : n - 1], d));
            }
        }
        return best;
    }

    /// <summary>Whether any part of the footprint lies on a road's surface.</summary>
    public static bool FootprintOnRoad(Footprint f, IReadOnlyList<RoadPolyline> roads)
    {
        var c = f.Centre; float rad = f.Radius;
        foreach (var r in roads)
        {
            if (c.X < r.BoundsMin.X - rad || c.X > r.BoundsMax.X + rad || c.Y < r.BoundsMin.Y - rad || c.Y > r.BoundsMax.Y + rad) continue;
            for (int i = 0; i + 1 < r.Centres.Length; i++)
            {
                var a = r.Centres[i]; var b = r.Centres[i + 1];
                if (Vector2.DistanceSquared(a, c) > (rad + 60) * (rad + 60)) continue;
                float hw = MathF.Min(r.HalfWidths[i], r.HalfWidths[i + 1]) - 0.5f;
                int n = Math.Max(1, (int)(Vector2.Distance(a, b) / 2));
                for (int k = 0; k <= n; k++)
                    if (f.DistanceTo(Vector2.Lerp(a, b, (float)k / n)) < hw) return true;
            }
        }
        return false;
    }
}

public static class DeckCover
{
    /// <summary>How many metres of the deck's line have no road under them (sampled every 3 m: a point counts when the nearest road's centre line is within its half width plus 1 m): what a tunnel laid on a road that stops short has.</summary>
    public static float Gap(Deck deck, IReadOnlyList<RoadPolyline> roads)
    {
        float missing = 0, step = 3f;
        var axis = deck.Axis;
        for (float t = 0; t <= deck.Length; t += step)
        {
            var p = deck.Start + axis * t;
            if (RoadGeometry.Nearest(roads, p, 12f) is not { } hit || hit.Distance > hit.HalfWidth + 1f) missing += step;
        }
        return missing;
    }
}

/// <summary>A road-carrying structure's deck: the line from its entry end to its exit end, and the width.</summary>
public readonly record struct Deck(Vector2 Start, Vector2 End, float Width)
{
    public Vector2 Axis => Vector2.Normalize(End - Start);
    public float Length => Vector2.Distance(Start, End);

    /// <summary>The deck of a footprint whose model runs along its z: the ends at the middle of the box's near and far faces.</summary>
    public static Deck Of(Footprint f)
    {
        float cx = (f.Min.X + f.Max.X) / 2;
        return new Deck(f.ToWorld(new(cx, f.Min.Y)), f.ToWorld(new(cx, f.Max.Y)), f.Max.X - f.Min.X);
    }

    /// <summary>The deck as a rectangle on the ground, grown across by <paramref name="slack"/> metres.</summary>
    public Footprint Zone(float slack)
    {
        var axis = Axis;
        return new Footprint(Start, MathF.Atan2(axis.X, axis.Y), new Vector2(-Width / 2 - slack, 0), new Vector2(Width / 2 + slack, Length));
    }
}

/// <summary>Cutting a road where a structure carries it. The stock maps end the road at a bridge's or ramp's ends: the structure is the road there.</summary>
public static class RoadSplit
{
    /// <summary>
    /// The roads with every stretch that runs along the deck removed and its ends cut at the deck's ends (the roads on either side
    /// get an end point there). Roads that cross the deck at an angle (an overpass) are left alone, and so are the roads' objects:
    /// nothing in <paramref name="roads"/> is changed, the result shares the roads that were not cut. <paramref name="cuts"/> counts the stretches removed.
    /// </summary>
    public static List<RoadOp> Ops(IReadOnlyList<RoadEdit> roads, Deck deck, out int cuts)
    {
        var after = CutRoads(roads, deck, out cuts);
        // each old road becomes zero, one or two roads: the changes, highest index first so that the indices stay good while they are applied
        var ops = new List<RoadOp>();
        for (int i = roads.Count - 1; i >= 0; i--)
        {
            var pieces = after[i];
            if (pieces.Count == 1 && ReferenceEquals(pieces[0], roads[i])) continue;
            if (pieces.Count == 0) { ops.Add(new RoadOp.Remove(i, roads[i])); continue; }
            ops.Add(new RoadOp.Replace(i, roads[i], pieces[0]));
            for (int k = 1; k < pieces.Count; k++) ops.Add(new RoadOp.Insert(i + k, pieces[k]));
        }
        return ops;
    }

    /// <summary>The result of cutting every road, road by road: the pieces each road became (the road itself, if it was not cut).</summary>
    public static List<List<RoadEdit>> CutRoads(IReadOnlyList<RoadEdit> roads, Deck deck, out int cuts)
    {
        cuts = 0;
        var zone = deck.Zone(1.0f);
        var axis = deck.Axis;
        var result = new List<List<RoadEdit>>(roads.Count);
        foreach (var road in roads)
        {
            var pts = road.Points;
            var inside = new bool[pts.Count];
            bool any = false;
            for (int i = 0; i < pts.Count; i++) { inside[i] = zone.Contains(pts[i].Centre, -0.25f); any |= inside[i]; }
            if (!any) { result.Add([road]); continue; }
            // runs of inside points that run along the deck
            var pieces = new List<List<RoadEditPoint>>();
            var cur = new List<RoadEditPoint>();
            bool changed = false;
            int k = 0;
            while (k < pts.Count)
            {
                if (!inside[k]) { cur.Add(pts[k]); k++; continue; }
                int j = k;
                while (j + 1 < pts.Count && inside[j + 1]) j++;
                var dir = j > k ? pts[j].Centre - pts[k].Centre : (k > 0 ? pts[k].Centre - pts[k - 1].Centre : (j + 1 < pts.Count ? pts[j + 1].Centre - pts[j].Centre : axis));
                if (dir.LengthSquared() < 1e-6f || MathF.Abs(Vector2.Dot(Vector2.Normalize(dir), axis)) < 0.6f)
                {
                    for (int m = k; m <= j; m++) cur.Add(pts[m]);   // crossing: leave it
                    k = j + 1;
                    continue;
                }
                changed = true; cuts++;
                // the piece before ends at the deck end nearer to it, the piece after starts at the other one
                bool hasBefore = cur.Count > 0, hasAfter = j + 1 < pts.Count;
                float aBefore = hasBefore ? Vector2.Dot(cur[^1].Centre - deck.Start, axis) : 0;
                float aAfter = hasAfter ? Vector2.Dot(pts[j + 1].Centre - deck.Start, axis) : 0;
                float half = deck.Length / 2;
                float fromAlong, toAlong;
                if (hasBefore && hasAfter) { fromAlong = aBefore <= aAfter ? 0 : deck.Length; toAlong = deck.Length - fromAlong; }
                else if (hasBefore) { fromAlong = aBefore < half ? 0 : deck.Length; toAlong = deck.Length - fromAlong; }
                else { toAlong = aAfter < half ? 0 : deck.Length; fromAlong = deck.Length - toAlong; }
                if (hasBefore) Cap(cur, deck.Start + axis * fromAlong);
                if (cur.Count >= 2) pieces.Add(cur);
                cur = [];
                if (hasAfter)
                {
                    var at = deck.Start + axis * toAlong;
                    var first = pts[j + 1];
                    if (Vector2.Distance(first.Centre, at) < 1f) { var c = first.Clone(); c.MoveTo(at); cur.Add(c); }
                    else { var e = first.Clone(); e.MoveTo(at); cur.Add(e); cur.Add(first); }
                }
                k = j + 2;
            }
            if (cur.Count >= 2) pieces.Add(cur);
            if (!changed) { result.Add([road]); continue; }
            result.Add([.. pieces.Select(piece => new RoadEdit(road.Kind, piece))]);
        }
        return result;
    }

    /// <summary>Puts an end point of the piece at <paramref name="at"/>: the last point moves there if it is within a metre, else a copy of it is added there.</summary>
    private static void Cap(List<RoadEditPoint> piece, Vector2 at)
    {
        var last = piece[^1];
        if (Vector2.Distance(last.Centre, at) < 1f) { var c = last.Clone(); c.MoveTo(at); piece[^1] = c; return; }
        var e = last.Clone(); e.MoveTo(at); piece.Add(e);
    }
}
