using I76.Formats.Assembly;
using I76.Formats.Geo;

namespace I76.Game.World;

/// <summary>
/// The flat parts of buildings and props that lie on the ground: house lots,
/// aprons, driveways, slabs. The original drew them over the terrain
/// in its painter's order. Here a flat face on an uneven heightfield fights
/// the terrain for depth, so (like the road pieces) they are painted into the
/// terrain's road layer and left out of the object's mesh, for placements
/// that stand on the ground.
/// </summary>
public static class GroundDecals
{
    /// <summary>A face is a ground decal when its outward normal points up and every vertex is within this of the model's lowest point.</summary>
    public const float Band = 0.08f;


    /// <summary>The model's ground level (see <see cref="GroundLevel"/>): the lowest vertex, or a slab-sized upward face within half a metre above it.</summary>
    public static float LowestY(AssembledModel m) => GroundLevel.Of(m);

    public static bool IsGroundFace(ModelPart part, GeoFace f, float lowest)
    {
        if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) return false;
        var n = part.ToModel.Rotate(new System.Numerics.Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z));
        if (n.LengthSquared() < 0.5f || -n.Y / n.Length() < 0.9f) return false;     // outward is −plane
        return f.Vertices.All(v => part.ToModel.Apply(part.Mesh.Vertices[v.Index]).Y <= lowest + Band);
    }

    /// <summary>
    /// Of a model's wreck (its 'A' parts for the dead nodes): the faces that are painted into the terrain already, the ones
    /// the intact model was painted with at load. Those are a live part's own ground faces and a dead part's whose intact
    /// twin ('_' for 'A') had ground faces (a lot). A dead part whose twin lay on nothing (a phone booth's or a ticket
    /// box's scorch mark, a pipeline's flattened pipe, a plane's burnt hull) is drawn: the paint never had it.
    /// </summary>
    public static Func<ModelPart, GeoFace, bool> Painted(AssembledModel intact)
    {
        float lowest = LowestY(intact);
        var grounded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in intact.Parts)
            if (part.Mesh.Faces.Any(f => IsGroundFace(part, f, lowest))) grounded.Add(part.NodeName);
        static string Twin(string n) => n.Length > 4 && n[3] is 'A' or 'a' ? n[..3] + "_" + n[4..] : n;
        return (part, f) => grounded.Contains(Twin(part.NodeName)) && IsGroundFace(part, f, lowest);
    }

    /// <summary>
    /// Of a destroyed placement: whether a part's ground faces, painted into the terrain at load, go. The original drew them
    /// with their node, so they go with it when it dies into nothing or into a twin that lays nothing on the ground: a
    /// tunnel's road strips, a roof's footprint. A dead part whose twin lies on the ground (a lot) keeps them,
    /// as <see cref="Painted"/> leaves that twin's out of the wreck. <paramref name="wreck"/> is what is drawn now (each dead
    /// node's twin, the live ones whole), null for nothing.
    /// </summary>
    public static Func<string, bool> PaintedOut(AssembledModel intact, AssembledModel? wreck, Func<string, bool> dead)
    {
        float lowest = LowestY(intact);
        bool Lies(string name) => wreck?.Parts.FirstOrDefault(x => x.NodeName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } w
            && w.Mesh.Faces.Any(f => IsGroundFace(w, f, lowest));
        static string Twin(string n) => n.Length > 4 ? n[..3] + "A" + n[4..] : n;
        return part => dead(part) && !Lies(Twin(part)) && !Lies(part);
    }

    /// <summary>
    /// Whether a placement's ground faces are painted rather than drawn: scenery (not vehicles or road pieces), whatever its
    /// class id. The loader stands every placement on the terrain (MissionWorld.GroundedPosition). A drive-on structure (the Ramp
    /// and Bridge class ids) keeps its decks, which lie above its lowest point (no stock ramp or bridge has a ground face but
    /// bcaik8's lot); what a map places as one on the ground (a gas station so cars drive on its roof, a factory building whose
    /// floor is at the ground, an intersection piece) has its lot and floors painted as a grounded building's, not drawn as faces
    /// fighting the terrain for depth.
    /// </summary>
    public static bool Applies(Placement p) =>
        p.Definition is { } d && !d.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) && !RoadPieceObjects.IsRoadPiece(p.Object.ObjectType);

    /// <summary>
    /// The faces of a placement painted into the terrain's road layer, with the model they are of (a road piece's drawn node,
    /// RoadPieceObjects.Drawn): a grounded placement's ground faces (<see cref="Applies"/>), a flat road piece's every face, none
    /// of a refused placement. Without a ground list (<paramref name="groundList"/> false, RoadPieceObjects.GroundListDrawn: a
    /// map with no road data) no road piece's and no lot's. The caller keeps the upward ones. <paramref name="lowest"/> gives a
    /// placement's model's ground level (a cache), else <see cref="LowestY"/>.
    /// </summary>
    public static IEnumerable<(AssembledModel Model, ModelPart Part, GeoFace Face)> PaintedFaces(Placement p, AssembledModel? model, bool groundList,
        Func<Placement, AssembledModel, float>? lowest = null)
    {
        if (p.Refused || model is null) yield break;
        bool piece = RoadPieceObjects.IsRoadPiece(p.Object.ObjectType), decals = Applies(p);
        if (!decals && (!piece || !groundList)) yield break;
        // a road piece is painted only where the paint is its whole look (RoadPieceObjects): a ramp given the type is drawn
        if (!decals && !RoadPieceObjects.IsFlat(model = RoadPieceObjects.Drawn(model))) yield break;
        float ground = decals ? lowest?.Invoke(p, model) ?? LowestY(model) : 0;
        // no marker's footprint (it is never drawn), and no lot without a ground list
        foreach (var part in model.Parts)
        {
            if (!model.Drawn(part) || RoadPieceObjects.OffTheGroundList(p.Object.ObjectType, model, part.NodeName, groundList)) continue;
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                if (decals && !IsGroundFace(part, f, ground)) continue;
                yield return (model, part, f);
            }
        }
    }
}

/// <summary>
/// How the scenery's placements share meshes (<see cref="ScenerySpawner"/>): by definition, the road pieces apart (each
/// placement by its own type: a model can be a piece in one placement and a ramp in another), then the grounded scenery apart
/// (its mesh without the ground faces, <see cref="GroundDecals"/>).
/// </summary>
public static class SceneryGroup
{
    public const string Piece = "|piece", Ground = "|ground";

    public static string Key(Placement p) =>
        (p.Definition ?? "") + (RoadPieceObjects.IsRoadPiece(p.Object.ObjectType) ? Piece : GroundDecals.Applies(p) ? Ground : "");
}
