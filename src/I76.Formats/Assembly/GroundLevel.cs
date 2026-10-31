using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// A model's ground level: where a placement standing on the terrain has its
/// floor. The lowest vertex, unless a slab-sized upward face lies just above
/// it (a lot's slab with a small footing sunk under it), in which case the slab.
/// </summary>
public static class GroundLevel
{
    /// <summary>A lot's slab is at least this large (m^2); smaller upward faces don't set the ground level.</summary>
    public const float MinSlabArea = 4f;
    /// <summary>A slab sets the ground level only within this of the lowest vertex: a roof or a bridge deck is not a floor.</summary>
    public const float MaxAboveLowest = 0.5f;

    public static float LowestVertex(AssembledModel m) => Measure(m).Lowest;

    public static float Of(AssembledModel m)
    {
        var (lowest, slab) = Measure(m);
        return slab - lowest <= MaxAboveLowest ? slab : lowest;
    }

    /// <summary>The lowest vertex and the top of the lowest slab-sized upward face (infinity if none), in model space.</summary>
    public static (float Lowest, float Slab) Measure(AssembledModel m)
    {
        float slab = float.PositiveInfinity, any = float.PositiveInfinity;
        foreach (var part in m.Parts)
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                var pts = f.Vertices.Select(v => part.ToModel.Apply(part.Mesh.Vertices[v.Index])).ToArray();
                any = Math.Min(any, pts.Min(q => q.Y));
                var n = part.ToModel.Rotate(new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z));
                if (n.LengthSquared() < 0.5f || -n.Y / n.Length() < 0.9f) continue;
                var sum = Vector3.Zero;
                for (int k = 1; k + 1 < pts.Length; k++) sum += Vector3.Cross(pts[k] - pts[0], pts[k + 1] - pts[0]);
                if (sum.Length() * 0.5f >= MinSlabArea) slab = Math.Min(slab, pts.Max(q => q.Y));
            }
        return (float.IsInfinity(any) ? 0 : any, slab);
    }
}
