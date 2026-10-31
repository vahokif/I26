using System.Numerics;
using I76.Formats.Geo;

namespace I76.Formats.Assembly;

/// <summary>
/// The burning scenery wrecks (akwreck2) draw their fire and smoke as sprite planes inside the model: xfr1 faces (the flame,
/// 6 m) and xsb1 faces (the smoke column, 15 m tall, 13 m wide and not centred on the wreck). The modern look leaves both
/// out and puffs its own smoke from the flame's base.
/// </summary>
public static class WreckSmoke
{
    public static bool IsSmokeFace(GeoFace f) => f.Unk_0x20 != 0 && f.Texture.StartsWith("XSB1", StringComparison.OrdinalIgnoreCase);
    public static bool IsFlameFace(GeoFace f) => f.Unk_0x20 != 0 && f.Texture.StartsWith("XFR1", StringComparison.OrdinalIgnoreCase);

    /// <summary>A face the modern look draws out: the original's sprite smoke and flame.</summary>
    public static bool IsSpriteFace(GeoFace f) => IsSmokeFace(f) || IsFlameFace(f);

    private static List<Vector3> Corners(AssembledModel m, Func<GeoFace, bool> pick)
    {
        var pts = new List<Vector3>();
        foreach (var part in m.Parts)
            foreach (var f in part.Mesh.Faces.Where(pick))
                foreach (var v in f.Vertices)
                    if ((uint)v.Index < part.Mesh.Vertices.Length) pts.Add(part.ToModel.Apply(part.Mesh.Vertices[v.Index]));
        return pts;
    }

    /// <summary>
    /// Where the modern smoke rises, in the model's frame (engine axes): the base centre of the flame planes (the
    /// burning spot), else of the smoke planes. Height and Width are the smoke planes' (the plume to match), FlameHeight and FlameWidth the flame planes' (0 without any); null when
    /// the model has no sprite smoke.
    /// </summary>
    public static (Vector3 Base, float Height, float Width, float FlameHeight, float FlameWidth)? Measure(AssembledModel m)
    {
        var smoke = Corners(m, IsSmokeFace);
        if (smoke.Count == 0) return null;
        var src = Corners(m, IsFlameFace) is { Count: > 0 } flame ? flame : smoke;
        float low = src.Min(q => q.Y);
        var bottom = src.Where(q => q.Y <= low + 0.5f).ToList();
        float lowS = smoke.Min(q => q.Y), hi = smoke.Max(q => q.Y);
        float w = MathF.Max(smoke.Max(q => q.X) - smoke.Min(q => q.X), smoke.Max(q => q.Z) - smoke.Min(q => q.Z));
        var fl = Corners(m, IsFlameFace);
        float fh = fl.Count == 0 ? 0 : fl.Max(q => q.Y) - fl.Min(q => q.Y);
        float fw = fl.Count == 0 ? 0 : MathF.Max(fl.Max(q => q.X) - fl.Min(q => q.X), fl.Max(q => q.Z) - fl.Min(q => q.Z));
        return (new Vector3(bottom.Average(q => q.X), low, bottom.Average(q => q.Z)), hi - lowS, w, fh, fw);
    }
}
