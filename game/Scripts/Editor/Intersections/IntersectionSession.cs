using I76.Editor.Intersections;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Game.Editor.Intersections;

/// <summary>
/// The map's intersection pieces across the editor's scenes (a playtest rebuilds the world from the file, the pieces made since
/// are carried here, as the roads are by <see cref="EditorSession"/>): read from the file once, held, and put into each world's
/// placements so the loader paints them (a playtest, the editor's own view) like any stock piece.
/// </summary>
public static class IntersectionSession
{
    public static PieceStore? Pieces { get; private set; }

    public static void End() => Pieces = null;

    /// <summary>The hook EditorSession.Prepare runs on a freshly read world: the pieces of the file (first time), tied to the road store, and into the world's placements.</summary>
    public static void Prepare(MissionWorld world, RoadStore roads)
    {
        Pieces ??= new PieceStore(MapDocument.Parse(world.Mission.Data).Objects);
        roads.Pieces = Pieces;
        Sync(world);
    }

    /// <summary>The world's road-piece placements of the catalogue's names are the store's pieces (the others, a J connector or a runway end, stay).</summary>
    public static void Sync(MissionWorld world)
    {
        if (Pieces is null) return;
        world.Placements.RemoveAll(p => PieceCatalogue.IsPieceType(p.Object.ObjectType) && PieceCatalogue.Lookup(p.Object.Name) is not null);
        foreach (var piece in Pieces.Items)
        {
            var o = piece.Data;
            world.Placements.Add(new Placement(new PlacedObject(o.Name, o.NameFlags, o.Rotation, o.Position, (byte[])o.Payload.Clone()), piece.Def.Name + ".sdf"));
        }
    }
}
