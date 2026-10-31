using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// Where the parts of an assembled model meet: each part's open edges (an edge only one of its faces uses), and for
/// every open-edge vertex how far it lies from the nearest vertex and the nearest open edge of the other parts, in
/// model space with each part's transform applied (the engine's float maths, <see cref="Affine"/>).
/// A vertex at 0 from another part's vertex is welded in all but name; one at 0 from an edge but not a vertex is a
/// T-junction; a small positive distance to both is a real hairline gap in the data.
/// </summary>
public static class PartSeams
{
    public readonly record struct Edge(Vector3 A, Vector3 B);

    public sealed record PartEdges(ModelPart Part, Vector3[] Placed, List<Edge> Open);

    public readonly record struct Near(string Part, Vector3 At, float ToVertex, float ToEdge, string VertexOf, string EdgeOf);

    /// <summary>A part's vertices in model space and its open edges (by position, so split vertices count as one).</summary>
    public static PartEdges Open(ModelPart part)
    {
        var g = part.Mesh;
        var placed = g.Vertices.Select(part.ToModel.Apply).ToArray();
        var key = new Dictionary<Vector3, int>();
        int Key(int i) { var p = placed[i]; if (!key.TryGetValue(p, out var k)) key[p] = k = key.Count; return k; }
        var count = new Dictionary<(int, int), (int Uses, Edge E)>();
        foreach (var f in g.Faces)
        {
            if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= placed.Length)) continue;
            for (int i = 0; i < f.Vertices.Length; i++)
            {
                int a = f.Vertices[i].Index, b = f.Vertices[(i + 1) % f.Vertices.Length].Index;
                int ka = Key(a), kb = Key(b);
                if (ka == kb) continue;
                var k = ka < kb ? (ka, kb) : (kb, ka);
                count[k] = count.TryGetValue(k, out var c) ? (c.Uses + 1, c.E) : (1, new Edge(placed[a], placed[b]));
            }
        }
        return new PartEdges(part, placed, count.Values.Where(c => c.Uses == 1).Select(c => c.E).ToList());
    }

    public static float SegmentDistance(Vector3 p, Edge e)
    {
        var d = e.B - e.A;
        float l = d.LengthSquared();
        float t = l < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - e.A, d) / l, 0, 1);
        return Vector3.Distance(p, e.A + d * t);
    }

    /// <summary>
    /// For each open-edge vertex of each part that has another part within <paramref name="reach"/>: its distance to
    /// the nearest vertex and the nearest open edge of the other parts (those not passing <paramref name="skip"/>).
    /// </summary>
    public static List<Near> Measure(IReadOnlyList<ModelPart> parts, float reach = 0.1f, Func<ModelPart, bool>? skip = null)
    {
        var all = parts.Where(p => skip?.Invoke(p) != true).Select(Open).ToList();
        var o = new List<Near>();
        for (int i = 0; i < all.Count; i++)
        {
            var mine = all[i].Open.SelectMany(e => new[] { e.A, e.B }).Distinct();
            foreach (var p in mine)
            {
                float bv = float.MaxValue, be = float.MaxValue; string vOf = "", eOf = "";
                for (int j = 0; j < all.Count; j++)
                {
                    if (j == i) continue;
                    foreach (var q in all[j].Placed)
                    {
                        float d = Vector3.Distance(p, q);
                        if (d < bv) { bv = d; vOf = all[j].Part.NodeName; }
                    }
                    foreach (var e in all[j].Open)
                    {
                        float d = SegmentDistance(p, e);
                        if (d < be) { be = d; eOf = all[j].Part.NodeName; }
                    }
                }
                if (Math.Min(bv, be) <= reach) o.Add(new Near(all[i].Part.NodeName, p, bv, be, vOf, eOf));
            }
        }
        return o;
    }
}
