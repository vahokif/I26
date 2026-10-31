using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// A part standing on the body by a hair: the Sheriff's light bar (CP*1SIRN) has a flat underside at the height of the
/// roof's very highest vertex, 1.388, while the roof is cambered and lies 1 cm under it at the middle and 5 cm under it
/// at the bar's ends, so a gap shows at the ends from the side (the report in #74: "not touching the top of the car").
/// The data is the original's. <see cref="Sinks"/> finds how far to lower each such part so that its underside meets the surface under it everywhere.
/// </summary>
public static class MountSeating
{
    /// <summary>The widest gap taken as a mount's seat rather than a part meaning to stand clear (a mirror's arm, a gun's barrel).</summary>
    public const float MaxGap = 0.08f;
    /// <summary>A gap this small is not seen: left alone.</summary>
    public const float MinGap = 0.003f;
    /// <summary>The vertices within this height of a part's lowest are its underside.</summary>
    public const float Underside = 0.01f;

    /// <summary>The parts seated: the light bars of the police cars (CP*1SIRN), which are what stand on the roof by a hair; a gun's base or a mirror is left as the data has it.</summary>
    public static bool IsBar(ModelPart p) => p.NodeName.Length >= 8 && p.NodeName[4..8].Equals("SIRN", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowers each light bar of the model (<see cref="IsBar"/>) onto the roof under it.</summary>
    public static void Seat(AssembledModel model)
    {
        if (!model.Parts.Any(IsBar)) return;
        foreach (var (pi, down) in Sinks(model, IsBar))
        {
            var p = model.Parts[pi];
            model.Parts[pi] = p with { ToModel = p.ToModel with { T = p.ToModel.T - new Vector3(0, down, 0) } };
        }
    }

    /// <summary>For each part (by index) that stands above the surface under its underside, how far down its underside would meet it everywhere (a gap of at most <see cref="MaxGap"/> at some vertex).</summary>
    public static Dictionary<int, float> Sinks(AssembledModel model, Func<ModelPart, bool> mounted)
    {
        var tris = new List<(int Part, Vector3 A, Vector3 B, Vector3 C)>();
        var verts = new List<Vector3[]>();
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var p = model.Parts[pi];
            var vs = p.Mesh.Vertices.Select(v => p.ToModel.Apply(v)).ToArray();
            verts.Add(vs);
            foreach (var f in p.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= vs.Length)) continue;
                for (int k = 1; k + 1 < f.Vertices.Length; k++)
                    tris.Add((pi, vs[f.Vertices[0].Index], vs[f.Vertices[k].Index], vs[f.Vertices[k + 1].Index]));
            }
        }
        var sinks = new Dictionary<int, float>();
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            if (!mounted(model.Parts[pi]) || verts[pi].Length == 0) continue;
            float low = verts[pi].Min(v => v.Y), gap = 0;
            foreach (var v in verts[pi].Where(v => v.Y <= low + Underside))
            {
                float? top = null;
                foreach (var (part, a, b, c) in tris)
                {
                    if (part == pi || Y(a, b, c, v.X, v.Z) is not { } y || y > v.Y + 0.001f) continue;
                    if (top is null || y > top) top = y;
                }
                if (top is { } t && v.Y - t is var g && g >= MinGap && g <= MaxGap) gap = Math.Max(gap, g);
            }
            if (gap > 0) sinks[pi] = gap;
        }
        return sinks;
    }

    /// <summary>The height of an upward-facing triangle over (x, z), if it lies over it.</summary>
    private static float? Y(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-9f) return null;
        float l1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d, l2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d, l3 = 1 - l1 - l2;
        const float e = -1e-4f;
        if (l1 < e || l2 < e || l3 < e) return null;
        return l1 * a.Y + l2 * b.Y + l3 * c.Y;
    }
}
