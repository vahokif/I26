using System.Numerics;
using I76.Formats.Geo;

namespace I76.Formats.Assembly;

/// <summary>
/// Where a structure's lamps are, for the modern look's night lights: its self-lit faces (<see cref="FaceLight.SelfLit"/>)
/// that look down, a lamp's lens or a canopy's lit underside. The original has no light but the cars' (its night lights
/// are headlamps only): these faces are simply drawn at full brightness, and what lies under them (a pad, the pumps) is
/// self-lit too. The modern look lights them for real: a light where each such face is, pointing the way it looks, one
/// for every <see cref="Spacing"/> metres or so of a large face (a canopy) and one for a small lamp's faces together.
/// Ours, not the original's.
/// </summary>
public static class SelfLitLamps
{
    /// <summary>A lamp: where it shines from and the way (model frame, game axes), its share of the faces' area (m²) and the face it came from.</summary>
    public sealed record Lamp(Vector3 At, Vector3 Facing, float Area, ModelPart Part, GeoFace Face);

    /// <summary>A face looks down when its outward normal's y is below minus this (a canopy's slanted edge, 30 degrees, counts).</summary>
    public const float Down = 0.7f;

    /// <summary>The most a lamp of a large face lights round it: a face is cut into cells at most this wide, a lamp in each.</summary>
    public const float Spacing = 10f;

    /// <summary>Lamps nearer than this are one (the three faces of a street lamp's lens).</summary>
    public const float Merge = 3f;

    /// <summary>
    /// The lamps of <paramref name="model"/>: its parts that <paramref name="include"/> keeps, their self-lit faces looking down that
    /// <paramref name="lit"/> keeps (the caller's test of the face's colour: a black face is no lamp).
    /// </summary>
    public static List<Lamp> Of(AssembledModel model, Func<ModelPart, bool>? include = null, Func<ModelPart, GeoFace, bool>? lit = null)
    {
        var raw = new List<Lamp>();
        foreach (var part in model.Parts)
        {
            if (include is not null && !include(part)) continue;
            var g = part.Mesh;
            foreach (var f in g.Faces)
            {
                if (f.Vertices.Length < 3 || !FaceLight.SelfLit(f) || f.Vertices.Any(v => (uint)v.Index >= g.Vertices.Length)) continue;
                // the GEO's plane normal points in: the outward one is its negation
                var n = -part.ToModel.Rotate(new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z));
                if (n.LengthSquared() < 1e-6f) continue;
                n = Vector3.Normalize(n);
                if (n.Y > -Down || lit?.Invoke(part, f) == false) continue;
                var pts = f.Vertices.Select(v => part.ToModel.Apply(g.Vertices[v.Index])).ToArray();
                foreach (var (at, area) in Cells(pts, n)) raw.Add(new Lamp(at, n, area, part, f));
            }
        }
        return MergeNear(raw);
    }

    /// <summary>A polygon cut into cells at most <see cref="Spacing"/> wide along its own longest edge and across it: each cell's middle that lies in it, with the polygon's area shared among them.</summary>
    internal static List<(Vector3 At, float Area)> Cells(Vector3[] pts, Vector3 n)
    {
        float area = 0;
        for (int k = 1; k + 1 < pts.Length; k++) area += 0.5f * Vector3.Cross(pts[k] - pts[0], pts[k + 1] - pts[0]).Length();
        var centre = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Length;
        // the plane's axes: along the longest edge, and across it
        var longest = Enumerable.Range(0, pts.Length).Select(i => pts[(i + 1) % pts.Length] - pts[i]).MaxBy(e => e.LengthSquared());
        if (longest.LengthSquared() < 1e-8f) return [(centre, area)];
        var u = Vector3.Normalize(longest - n * Vector3.Dot(longest, n));
        var v = Vector3.Cross(n, u);
        var p2 = pts.Select(p => new Vector2(Vector3.Dot(p - centre, u), Vector3.Dot(p - centre, v))).ToArray();
        float u0 = p2.Min(p => p.X), u1 = p2.Max(p => p.X), v0 = p2.Min(p => p.Y), v1 = p2.Max(p => p.Y);
        int nu = Math.Max(1, (int)MathF.Ceiling((u1 - u0) / Spacing)), nv = Math.Max(1, (int)MathF.Ceiling((v1 - v0) / Spacing));
        var inside = new List<Vector3>();
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                var c = new Vector2(u0 + (u1 - u0) * (i + 0.5f) / nu, v0 + (v1 - v0) * (j + 0.5f) / nv);
                if (Inside(p2, c)) inside.Add(centre + u * c.X + v * c.Y);
            }
        if (inside.Count == 0) inside.Add(centre);
        return inside.Select(p => (p, area / inside.Count)).ToList();
    }

    private static bool Inside(Vector2[] poly, Vector2 p)
    {
        bool @in = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                @in = !@in;
        return @in;
    }

    /// <summary>Lamps within <see cref="Merge"/> of one another taken as one: at their area-weighted middle, facing their weighted mean way, the area summed.</summary>
    private static List<Lamp> MergeNear(List<Lamp> lamps)
    {
        var groups = new List<List<Lamp>>();
        foreach (var l in lamps)
        {
            var g = groups.FirstOrDefault(g => g.Any(o => Vector3.Distance(o.At, l.At) < Merge));
            if (g is null) groups.Add([l]); else g.Add(l);
        }
        return groups.Select(g =>
        {
            float a = g.Sum(l => l.Area);
            var at = g.Aggregate(Vector3.Zero, (s, l) => s + l.At * l.Area) / MathF.Max(a, 1e-6f);
            var facing = g.Aggregate(Vector3.Zero, (s, l) => s + l.Facing * l.Area);
            facing = facing.LengthSquared() > 1e-8f ? Vector3.Normalize(facing) : -Vector3.UnitY;
            var first = g.MaxBy(l => l.Area)!;
            return new Lamp(a > 0 ? at : g[0].At, facing, a, first.Part, first.Face);
        }).ToList();
    }
}
