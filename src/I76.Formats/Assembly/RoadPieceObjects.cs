using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// The road-piece placements: ODEF types 0x50-0x5f, the intersections, junctions and runway ends. The original never makes such a
/// placement an object of the world (nothing collides with it, nothing damages it); it keeps one top-level node of the model, the
/// last in the file of the detail and condition drawn, with the nodes under it, and draws that over the ground. Every stock piece
/// is that one node, a plane at the origin's height, and the port paints its upward faces into the terrain's road layer instead of
/// drawing it (docs/deviations.md). Any other model placed with a road-piece type (a ramp or a bridge a custom map gives the type)
/// is drawn as that node's mesh: a ramp's whole body, but of most bridges only the marker node that ends the file, which shows nothing. The type is the
/// placement's own: the same model can be a piece in one placement and a ramp in another.
/// </summary>
public static class RoadPieceObjects
{
    /// <summary>A painted piece's vertices are all within this (m) of its origin's height.</summary>
    public const float FlatBand = 0.1f;

    /// <summary>
    /// The same pieces ship as ODEF type 0x50, 0x52 or 0x53 (m04's ita21_1 is both 0x50 and 0x52, igd27_1 both 0x52 and 0x53),
    /// so the whole 0x50 row counts.
    /// </summary>
    public static bool IsRoadPiece(uint type) => (type & 0xf0) == 0x50;

    /// <summary>The part of a model a road-piece placement shows: the last top-level node and the nodes under it (none, when that node has no geometry).</summary>
    public static AssembledModel Drawn(AssembledModel m)
    {
        var o = new AssembledModel { Definition = m.Definition, Kind = m.Kind, ObjectCode = m.ObjectCode };
        o.TmtNames.AddRange(m.TmtNames);
        o.VtfMaps.AddRange(m.VtfMaps);
        if (m.TopNodes.Count == 0) return o;
        var top = m.TopNodes[^1];
        o.TopNodes.Add(top);
        foreach (var part in m.Parts)
            if (m.TopOf.TryGetValue(part.NodeName, out var t) && t.Equals(top, StringComparison.OrdinalIgnoreCase))
            {
                o.Parts.Add(part);
                o.TopOf[part.NodeName] = top;
            }
        return o;
    }

    /// <summary>Whether a face is one the paint lays down: outward (the -plane) within about 53 degrees of up.</summary>
    public static bool IsTop(ModelPart part, Geo.GeoFace f)
    {
        var n = part.ToModel.Rotate(new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z));
        return n.LengthSquared() >= 0.5f && -n.Y / n.Length() >= 0.6f;
    }

    /// <summary>Whether the paint reproduces a (drawn) model: it has faces, every one a top, every vertex within <see cref="FlatBand"/> of the origin's height.</summary>
    public static bool IsFlat(AssembledModel m)
    {
        bool any = false;
        foreach (var part in m.Parts)
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                if (!IsTop(part, f)) return false;
                foreach (var v in f.Vertices)
                    if (MathF.Abs(part.ToModel.Apply(part.Mesh.Vertices[v.Index]).Y) > FlatBand) return false;
                any = true;
            }
        return any;
    }

    /// <summary>Whether a placement of this type and model is painted into the road layer (its mesh not drawn).</summary>
    public static bool Painted(uint type, AssembledModel? model) => IsRoadPiece(type) && model is not null && IsFlat(Drawn(model));
}
