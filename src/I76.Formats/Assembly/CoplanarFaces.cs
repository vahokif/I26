using System.Numerics;
using I76.Formats.Geo;

namespace I76.Formats.Assembly;

/// <summary>
/// Faces of an assembled model lying in the plane of an earlier part's face, same facing, and covering part of it:
/// a car's tail lamps on its back, bcywhse3's panels on its wall, the signs on their boards. The original draws them
/// after the face under them; a depth buffer cannot order the two and they fight, so the mesh lifts these 1.2 cm off
/// it (MeshBuilder).
/// "Covering" means the polygons overlap in area, not just their bounding boxes: a car's panels meet edge to edge in
/// one plane (the Piranha's front fender and door, PP11BDYF and PP11BDYM, share the diagonal from (0.905, 1.007, 0.773)
/// to (0.965, 0.748, 1.173) exactly), and lifting one of them 1.2 cm opened a dark seam along every such edge.
/// </summary>
public static class CoplanarFaces
{
    public readonly record struct Planar(int Part, GeoFace Face, Vector3 N, float D, Vector3 Min, Vector3 Max, Vector3[] Points);

    /// <summary>How far apart two planes may be and still count as one.</summary>
    public const float PlaneTolerance = 0.01f;

    /// <summary>
    /// The share of the smaller face the overlap must cover. Over the stock models every coplanar pair covers either
    /// under 0.25 % (faces meeting at an edge, their planes a hair apart) or over 0.7 % (a lamp or window on its wall).
    /// </summary>
    public const float MinCover = 0.005f;

    public static HashSet<(int, GeoFace)> Overlays(AssembledModel model, Func<ModelPart, bool>? include = null, Vector3 origin = default)
    {
        var all = Faces(model, include, origin);
        var lifted = new HashSet<(int, GeoFace)>();
        foreach (var a in all)
            foreach (var b in all)
            {
                if (b.Part >= a.Part || Vector3.Dot(a.N, b.N) < 0.999f || MathF.Abs(a.D - b.D) > PlaneTolerance) continue;
                if (!BoxesOverlap(a, b) || !Cover(a, b)) continue;
                lifted.Add((a.Part, a.Face));
                break;
            }
        return lifted;
    }

    public static List<Planar> Faces(AssembledModel model, Func<ModelPart, bool>? include = null, Vector3 origin = default)
    {
        var all = new List<Planar>();
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            if (include is not null && !include(part)) continue;
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                var pts = f.Vertices.Select(v => part.ToModel.Apply(part.Mesh.Vertices[v.Index]) - origin).ToArray();
                var n = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
                if (n.LengthSquared() < 1e-10f) continue;
                n = Vector3.Normalize(n);
                all.Add(new Planar(pi, f, n, Vector3.Dot(n, pts[0]), pts.Aggregate(Vector3.Min), pts.Aggregate(Vector3.Max), pts));
            }
        }
        return all;
    }

    /// <summary>The boxes overlap on two axes (a plane's own box is flat along its normal): the quick test before <see cref="Cover"/>.</summary>
    private static bool BoxesOverlap(Planar a, Planar b)
    {
        var ov = Vector3.Min(a.Max, b.Max) - Vector3.Max(a.Min, b.Min);
        return (ov.X > PlaneTolerance ? 1 : 0) + (ov.Y > PlaneTolerance ? 1 : 0) + (ov.Z > PlaneTolerance ? 1 : 0) >= 2;
    }

    /// <summary>Whether the two faces, seen along a's normal, overlap by at least <see cref="MinCover"/> of the smaller one.</summary>
    public static bool Cover(Planar a, Planar b)
    {
        var pa = Project(a.Points, a.N);
        var pb = Project(b.Points, a.N);
        float aa = MathF.Abs(Area(pa)), ab = MathF.Abs(Area(pb));
        if (aa < 1e-8f || ab < 1e-8f) return false;
        float common = MathF.Abs(Area(Clip(pa, pb)));
        return common >= MinCover * MathF.Min(aa, ab);
    }

    private static List<Vector2> Project(Vector3[] pts, Vector3 n)
    {
        var u = Vector3.Normalize(MathF.Abs(n.X) < 0.9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY));
        var v = Vector3.Cross(n, u);
        return pts.Select(p => new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v))).ToList();
    }

    public static float Area(IReadOnlyList<Vector2> p)
    {
        float s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    /// <summary>The part of <paramref name="subject"/> inside the convex <paramref name="clip"/> (Sutherland-Hodgman); GEO faces are convex fans.</summary>
    public static List<Vector2> Clip(List<Vector2> subject, List<Vector2> clip)
    {
        float sign = Area(clip) >= 0 ? 1 : -1;
        var output = subject;
        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            var c0 = clip[i]; var c1 = clip[(i + 1) % clip.Count];
            float Side(Vector2 p) => sign * ((c1.X - c0.X) * (p.Y - c0.Y) - (c1.Y - c0.Y) * (p.X - c0.X));
            var input = output;
            output = new List<Vector2>(input.Count + 2);
            for (int k = 0; k < input.Count; k++)
            {
                var p = input[k]; var q = input[(k + 1) % input.Count];
                float sp = Side(p), sq = Side(q);
                if (sp >= 0) output.Add(p);
                if (sp >= 0 != sq >= 0) output.Add(p + (q - p) * (sp / (sp - sq)));
            }
        }
        return output;
    }
}
