using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// The road-piece placements: ODEF types 0x50-0x5f, the intersections, junctions and runway ends. The original never makes such a
/// placement an object of the world (nothing collides with it, nothing damages it); it keeps one top-level node of the model, the
/// last in the file of the detail and condition drawn (a lot excepted), and draws that node alone over the ground: none of the
/// nodes under it, and nothing of a marker (flag 1). A building's lot (SGEO type 0x51) is taken out of the model as it loads
/// and drawn with the ground whatever the placement's type, so it shows too. Every stock piece is one node, a plane at the
/// origin's height, and the port paints its upward faces into the terrain's road layer instead of drawing it
/// (docs/deviations.md). Any other model placed with a road-piece type (a ramp, a bridge or a building a custom map gives the
/// type) is drawn as that node's mesh: a ramp's whole body, a building's lot (with its last node, usually a marker: nothing
/// else), but of most bridges nothing. The type is the placement's own: the same model can be a piece in one placement and a
/// ramp in another.
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

    /// <summary>
    /// The part of a model a road-piece placement shows: the last top-level node that is no lot, alone (nothing of it for a
    /// marker), and the top-level lots.
    /// </summary>
    public static AssembledModel Drawn(AssembledModel m)
    {
        var o = new AssembledModel { Definition = m.Definition, Kind = m.Kind, ObjectCode = m.ObjectCode };
        o.TmtNames.AddRange(m.TmtNames);
        o.VtfMaps.AddRange(m.VtfMaps);
        o.Lots.UnionWith(m.Lots);
        o.Markers.UnionWith(m.Markers);
        var lots = m.TopNodes.Where(m.Lots.Contains).ToList();
        var top = m.TopNodes.LastOrDefault(t => !m.Lots.Contains(t));
        if (top is not null) o.TopNodes.Add(top);
        o.TopNodes.AddRange(lots);
        foreach (var part in m.Parts)
        {
            bool own = part.NodeName.Equals(top, StringComparison.OrdinalIgnoreCase) && !m.Markers.Contains(part.NodeName);
            if (!own && !lots.Contains(part.NodeName, StringComparer.OrdinalIgnoreCase)) continue;
            o.Parts.Add(part);
            o.TopOf[part.NodeName] = part.NodeName;
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

    /// <summary>
    /// Whether a map draws its ground list: every building's lot (SGEO type 0x51, whatever the placement's class) and every
    /// road-piece placement. The original draws that list in its road pass, after the road strips, and the pass runs only when
    /// the map has road data, so a map without a single road segment shows no lot and no road piece (M15's donut stand stands
    /// on the bare arena). A road piece placed with another class id (Struct1, Struct2, Sign, Ramp, Bridge) is drawn with the
    /// structures, so it still shows, without its lot if it has one.
    /// </summary>
    public static bool GroundListDrawn(Mission.MissionFile msn) => msn.RoadSegments().Count > 0;

    /// <summary>
    /// Whether a placement's part is left out on a map that draws no ground list (<see cref="GroundListDrawn"/>): the whole of a
    /// road piece, and any placement's lot (its 'A' twin too).
    /// </summary>
    public static bool OffTheGroundList(uint type, AssembledModel m, string node, bool groundList) =>
        !groundList && (IsRoadPiece(type) || m.Lots.Contains(node));
}
