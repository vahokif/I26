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

    /// <summary>Whether a placement's ground faces are painted rather than drawn: scenery (not vehicles, road pieces or drive-on structures). The loader stands every placement on the terrain (MissionWorld.GroundedPosition).</summary>
    public static bool Applies(Placement p) =>
        p.Definition is { } d && !d.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) && !RoadPieceObjects.IsRoadPiece(p.Object.ObjectType) && p.Object.ObjectType is not (0xb or 0xc or 0xd);   // drive-on structures keep their decks
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
