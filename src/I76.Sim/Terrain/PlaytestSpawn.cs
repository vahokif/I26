using System.Numerics;
using I76.Sim.Collision;

namespace I76.Sim.Terrain;

/// <summary>Where a playtest's car goes: the point, or why the cursor's ray found none.</summary>
public readonly record struct SpawnPlan(bool Ok, Vector3 At, string? Why)
{
    public static SpawnPlan Fail(string why) => new(false, default, why);
}

/// <summary>
/// The map editor's playtest puts the car where the mouse points: the camera's ray through the cursor goes down to the first ground, road or
/// drive-on structure (a bridge's deck) it meets, and the car stands there if a car can: on the map, on ground a car may drive on (not water or
/// a cliff) and not inside or behind a building (a collider the ray meets first, or one the car's footprint is in).
/// </summary>
public static class PlaytestSpawn
{
    /// <summary>Half the car's width/length, how far from the point the footprint is checked.</summary>
    public const float CarReach = 2.6f;

    /// <param name="onMap">Whether the ground point (x, z) is on the map (the editor's tiles).</param>
    public static SpawnPlan FromRay(IGround ground, StaticWorld? statics, Func<double, double, bool> onMap, Vector3 origin, Vector3 dir, double maxDistance = 12000)
    {
        if (dir.LengthSquared() < 1e-6f) return SpawnPlan.Fail("no direction");
        dir = Vector3.Normalize(dir);
        double Above(double t)
        {
            double x = origin.X + dir.X * t, y = origin.Y + dir.Y * t, z = origin.Z + dir.Z * t;
            return y - ground.Query(x, y, z).Height;
        }
        if (Above(0) < 0) return SpawnPlan.Fail("the camera is under the ground");
        double t0 = 0, hit = -1;
        while (t0 < maxDistance)
        {
            double step = System.Math.Max(1.5, t0 * 0.012), t1 = t0 + step;
            if (Above(t1) < 0)
            {
                double lo = t0, hi = t1;
                for (int i = 0; i < 18; i++) { double mid = (lo + hi) / 2; if (Above(mid) < 0) hi = mid; else lo = mid; }
                hit = (lo + hi) / 2;
                break;
            }
            t0 = t1;
        }
        if (hit < 0) return SpawnPlan.Fail("the cursor is on the sky");
        float hx = (float)(origin.X + dir.X * hit), hz = (float)(origin.Z + dir.Z * hit);
        if (!onMap(hx, hz)) return SpawnPlan.Fail("the cursor is off the map");
        float hy = ground.Query(hx, origin.Y + dir.Y * hit, hz).Height;
        // a building the ray meets before the ground
        if (statics is not null && RayStatics(statics, origin, dir, hit - 1.0) is not null) return SpawnPlan.Fail("the cursor is on a building");
        for (int i = -1; i < 8; i++)
        {
            double x = hx, z = hz;
            if (i >= 0) { double a = i * System.Math.PI / 4; x += System.Math.Cos(a) * CarReach; z += System.Math.Sin(a) * CarReach; }
            if (!onMap(x, z)) return SpawnPlan.Fail("the cursor is at the map's edge");
            bool onStructure = ground.Query(x, hy, z).Height > ground.BareHeight(x, z) + 0.5f;   // a deck carries the car over water or a cliff
            if (!onStructure && ground.Impassable(x, z)) return SpawnPlan.Fail("the ground there is impassable (water or a cliff)");
            if (statics is not null && Inside(statics, new Vector3((float)x, hy + 0.5f, (float)z))) return SpawnPlan.Fail("the cursor is inside a building");
        }
        return new SpawnPlan(true, new Vector3(hx, hy, hz), null);
    }

    /// <summary>Distance along the ray to the first solid collider (polygon, box or sphere) within <paramref name="max"/>, or null.</summary>
    public static float? RayStatics(StaticWorld statics, Vector3 o, Vector3 d, double max)
    {
        float best = float.MaxValue;
        foreach (var s in statics.Objects)
        {
            if (!s.Solid) continue;
            float reach = System.Math.Max(s.BoundRadius, s.Radius) + 2;
            var rel = s.Centre - o;
            float along = Vector3.Dot(rel, d);
            if (along < -reach || along - reach > max || (rel - d * along).LengthSquared() > reach * reach) continue;
            foreach (var p in s.Polys)
            {
                float den = Vector3.Dot(p.N, d);
                if (den > -1e-5f) continue;   // the front of a polygon only
                float t = -(Vector3.Dot(p.N, o) + p.D) / den;
                if (t < 0 || t > best || t > max) continue;
                if (InPoly(p, o + d * t)) best = t;
            }
            foreach (var b in s.Boxes)
            {
                var lo = b.Rot.ToLocal(o - b.Origin); var ld = b.Rot.ToLocal(d);
                if (RayBox(lo, ld, b.Min, b.Max) is { } t && t >= 0 && t < best && t <= max) best = t;
            }
            if (s.Type == 4 && s.SphereRadius > 0 && RaySphere(o, d, s.Centre, s.SphereRadius) is { } ts && ts >= 0 && ts < best && ts <= max) best = ts;
        }
        return best < float.MaxValue ? best : null;
    }

    /// <summary>Whether the point is inside a solid collider: an odd number of its polygons above it, in a box, or in a sphere.</summary>
    public static bool Inside(StaticWorld statics, Vector3 p)
    {
        foreach (var s in statics.Objects)
        {
            if (!s.Solid) continue;
            float reach = System.Math.Max(s.BoundRadius, s.Radius) + 1;
            if (new Vector2(p.X - s.Centre.X, p.Z - s.Centre.Z).LengthSquared() > reach * reach) continue;
            foreach (var b in s.Boxes)
            {
                var l = b.Rot.ToLocal(p - b.Origin);
                if (l.X >= b.Min.X && l.X <= b.Max.X && l.Y >= b.Min.Y && l.Y <= b.Max.Y && l.Z >= b.Min.Z && l.Z <= b.Max.Z) return true;
            }
            if (s.Type == 4 && s.SphereRadius > 0 && Vector3.Distance(p, s.Centre) < s.SphereRadius) return true;
            int up = 0;
            foreach (var q in s.Polys)
            {
                if (System.Math.Abs(q.N.Y) < 1e-4f) continue;
                float y = -(q.D + q.N.X * p.X + q.N.Z * p.Z) / q.N.Y;
                if (y > p.Y && y < p.Y + 200 && InPolyXz(q, p.X, p.Z)) up++;
            }
            if ((up & 1) == 1) return true;
        }
        return false;
    }

    /// <summary>Whether a point on the polygon's plane is inside it (its outline projected along the normal's largest axis).</summary>
    private static bool InPoly(Poly p, Vector3 at)
    {
        float ax = System.Math.Abs(p.N.X), ay = System.Math.Abs(p.N.Y), az = System.Math.Abs(p.N.Z);
        (float U, float V) Pr(Vector3 v) => ax >= ay && ax >= az ? (v.Y, v.Z) : ay >= az ? (v.X, v.Z) : (v.X, v.Y);
        var (u, w) = Pr(at);
        bool inside = false;
        int n = p.V.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (au, aw) = Pr(p.V[i]); var (bu, bw) = Pr(p.V[j]);
            if ((aw > w) != (bw > w) && u < (bu - au) * (w - aw) / (bw - aw) + au) inside = !inside;
        }
        return inside;
    }

    private static bool InPolyXz(Poly p, float x, float z)
    {
        bool inside = false;
        int n = p.V.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = p.V[i]; var b = p.V[j];
            if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
        }
        return inside;
    }

    private static float? RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx)
    {
        float t0 = 0, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z, hi = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (System.Math.Abs(di) < 1e-8f) { if (oi < lo || oi > hi) return null; continue; }
            float a = (lo - oi) / di, b = (hi - oi) / di;
            if (a > b) (a, b) = (b, a);
            t0 = System.Math.Max(t0, a); t1 = System.Math.Min(t1, b);
            if (t0 > t1) return null;
        }
        return t0;
    }

    private static float? RaySphere(Vector3 o, Vector3 d, Vector3 c, float r)
    {
        var m = o - c;
        float b = Vector3.Dot(m, d), cc = Vector3.Dot(m, m) - r * r;
        if (cc > 0 && b > 0) return null;
        float disc = b * b - cc;
        return disc < 0 ? null : System.Math.Max(0, -b - MathF.Sqrt(disc));
    }
}
