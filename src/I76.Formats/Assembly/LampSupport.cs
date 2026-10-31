using System.Numerics;
using I76.Formats.Geo;

namespace I76.Formats.Assembly;

/// <summary>
/// Whether a car's lamp faces still sit on its body. A damaged zone swaps
/// its body part for a crushed one (the Piranha's PP13BDYB), but the lamp
/// parts it swaps to are the intact ones under another name (PP12TLGT is
/// PP11TLGT byte for byte but the name), so the lamps stay where the intact
/// body held them and hang in the air off the crushed one. Ours, not read
/// from the engine: a lamp face floats once the body behind some point of it
/// has fallen back more than <see cref="Slack"/> from where the intact body
/// was (a few cars' intact lamps overhang their bodies, so the intact body
/// is the measure, not the face itself). Over the stock cars, every lamp
/// face is held at damage levels 1 and 2; at 3, 430 of the 504 tail lamp
/// faces float, most of them 8 cm or more.
/// </summary>
public static class LampSupport
{
    /// <summary>
    /// Which rear lamp faces a car shows: the brake lamp's lit faces (BLGT, node veh+0x3a4) while braking;
    /// the tail lamp (TLGT) is the headlamps' child node, so only with the lights on and the front zone holding half its
    /// armour and chassis (<paramref name="frontOk"/>). With the lights and the brake off neither shows: the lamp is the
    /// body texture's own unlit lens. The port hides TLGT while BLGT shows over it (8 mm apart, they z-fight); a car with no
    /// BLGT (<paramref name="hasBrake"/> false) shows nothing more when braking.
    /// </summary>
    public static (bool Brake, bool Tail) RearFaces(bool braking, bool lights, bool frontOk, bool hasBrake)
    {
        bool brake = braking && hasBrake;
        return (brake, lights && frontOk && !brake);
    }

    /// <summary>How much farther back than the intact body the damaged one may lie behind a lamp face and still hold it (m).</summary>
    public const float Slack = 0.05f;

    /// <summary>How far in front of the face a ray starts (the body may stand a little proud of an inset lamp).</summary>
    private const float Lead = 0.03f;

    /// <summary>The faces of <paramref name="m"/>'s lamp parts that its body no longer holds where <paramref name="intact"/>'s did.</summary>
    public static HashSet<(ModelPart Part, GeoFace Face)> Floating(AssembledModel m, AssembledModel intact, Func<ModelPart, bool> lamps, Func<ModelPart, bool> body)
    {
        var o = new HashSet<(ModelPart, GeoFace)>();
        var tris = Triangles(m, body);
        var was = Triangles(intact, body);
        if (was.Count == 0) return o;
        var centre = Centre(was);
        foreach (var part in m.Parts.Where(lamps))
            foreach (var f in part.Mesh.Faces)
            {
                float before = Gap(part, f, was, centre);
                if (float.IsPositiveInfinity(before)) continue;   // the intact body did not hold it either
                if (Gap(part, f, tris, centre) - before > Slack) o.Add((part, f));
            }
        return o;
    }

    /// <summary>The included parts' faces as triangles, in the model's frame.</summary>
    public static List<(Vector3 A, Vector3 B, Vector3 C)> Triangles(AssembledModel m, Func<ModelPart, bool> include)
    {
        var o = new List<(Vector3, Vector3, Vector3)>();
        foreach (var part in m.Parts.Where(include))
        {
            foreach (var f in part.Mesh.Faces)
            {
                var v = Corners(part, f);
                for (int k = 1; k + 1 < v.Length; k++) o.Add((v[0], v[k], v[k + 1]));
            }
        }
        return o;
    }

    /// <summary>A face's middle (the mean of its corners), in the model's frame.</summary>
    public static Vector3 Middle(ModelPart part, GeoFace f)
    {
        var v = Corners(part, f);
        return v.Length == 0 ? Vector3.Zero : v.Aggregate(Vector3.Zero, (a, b) => a + b) / v.Length;
    }

    private static Vector3[] Corners(ModelPart part, GeoFace f) =>
        [.. f.Vertices.Where(x => (uint)x.Index < part.Mesh.Vertices.Length).Select(x => part.ToModel.Apply(part.Mesh.Vertices[x.Index]))];

    /// <summary>The middle of the triangles' bounds.</summary>
    public static Vector3 Centre(List<(Vector3 A, Vector3 B, Vector3 C)> tris)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var (a, b, c) in tris) { lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(b, c))); hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(b, c))); }
        return (lo + hi) * 0.5f;
    }

    /// <summary>
    /// The farthest the body lies behind any of the face's points (its middle
    /// and its corners drawn a third of the way in), along the face's inward
    /// normal; +∞ where nothing of the body is behind a point.
    /// </summary>
    public static float Gap(ModelPart part, GeoFace f, List<(Vector3 A, Vector3 B, Vector3 C)> body, Vector3 centre)
    {
        var v = Corners(part, f);
        if (v.Length < 3) return 0;
        var mid = Middle(part, f);
        var n = Vector3.Zero;
        for (int k = 1; k + 1 < v.Length; k++) n += Vector3.Cross(v[k] - v[0], v[k + 1] - v[0]);
        if (n.LengthSquared() < 1e-12f) return 0;
        n = Vector3.Normalize(n);
        if (Vector3.Dot(n, mid - centre) < 0) n = -n;   // outward, away from the car's middle
        float worst = 0;
        foreach (var p in v.Select(c => Vector3.Lerp(c, mid, 1f / 3)).Append(mid))
        {
            float t = Nearest(p + n * Lead, -n, body);
            worst = MathF.Max(worst, t - Lead);
            if (float.IsPositiveInfinity(worst)) break;
        }
        return worst;
    }

    /// <summary>The distance along the ray to the nearest triangle (either side), or +∞.</summary>
    private static float Nearest(Vector3 o, Vector3 d, List<(Vector3 A, Vector3 B, Vector3 C)> tris)
    {
        float best = float.PositiveInfinity;
        foreach (var (a, b, c) in tris)
        {
            var e1 = b - a; var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) continue;
            float inv = 1 / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(d, q) * inv;
            if (w < 0 || u + w > 1) continue;
            float t = Vector3.Dot(e2, q) * inv;
            if (t >= 0 && t < best) best = t;
        }
        return best;
    }
}
