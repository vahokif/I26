using Godot;
using I76.Editor;
using I76.Editor.Objects;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Vfs;

namespace I76.Game.Editor;

/// <summary>
/// The map editor's state that outlives a scene: the game reloads its scene for every mission, so a playtest (a reload into
/// the mission and another back) carries the edited heights, the undo history and the camera here. Nothing is written to the
/// install until the map menu's Save.
/// </summary>
public static class EditorSession
{
    /// <summary>The next scene load is the editor (or, with <see cref="Playtest"/>, a playtest of the map being edited).</summary>
    public static bool Active { get; private set; }
    public static bool Playtest { get; set; }
    /// <summary>Where the playtest's car stands: game x, z and the heading in degrees from +Z toward +X.</summary>
    public static float[]? PlaytestAt { get; set; }
    /// <summary>The ray through the editor's cursor when the playtest was asked for (game frame): the car goes where it meets the ground, if a car can stand there.</summary>
    public static (System.Numerics.Vector3 Origin, System.Numerics.Vector3 Dir)? PlaytestRay { get; set; }
    /// <summary>Why there is no <see cref="PlaytestRay"/> (the cursor was not over the 3D view): the playtest says so when the car stands at <see cref="PlaytestAt"/>.</summary>
    public static string? PlaytestNoRay { get; set; }

    /// <summary>The map being edited: its mission name (a stem, or a file name).</summary>
    public static string Mission { get; private set; } = "";
    /// <summary>The install the maps are read from and saved into (set by the game before the editor starts).</summary>
    public static GameInstall? Install { get; set; }

    /// <summary>The edited heights across a scene reload.</summary>
    public static ushort[]? Samples { get; private set; }
    public static bool Modified { get; private set; }
    public static UndoStack? Undo { get; private set; }
    /// <summary>The roads being edited across a scene reload (the undo stack's road commands hold this instance).</summary>
    public static RoadStore? Roads { get; private set; }
    /// <summary>The editor camera's last pose: pivot (game x, y, z), yaw, pitch, distance.</summary>
    public static float[]? Pose { get; set; }
    public static bool EnvTaken { get; set; }
    /// <summary>The tick count (ms) when a playtest was asked for or left, to log how long the reload took.</summary>
    public static ulong SwitchedAtMs { get; set; }

    /// <summary>The map's look (the map style) as the editor has it, carried across a reload: the world is rebuilt from the file with this over it. Null: the file's own.</summary>
    public static I76.Formats.Mission.MapLook? Look { get; private set; }

    /// <summary>The map style window's tab while it is open (it stays open across the rebuild its changes make), -1 when closed.</summary>
    public static int StyleTab { get; set; } = -1;

    /// <summary>The map's placed objects as the editor holds them: kept across a playtest's reloads (Objects/ObjectEditor.cs).</summary>
    public static ObjectLayer? Layer { get; private set; }
    /// <summary>The catalogue of placeable things (built once per install, Objects/ObjectEditor.cs).</summary>
    public static List<CatalogueItem>? Catalogue { get; set; }

    /// <summary>This scene's document, made by <see cref="Prepare"/> when the mission's world is read.</summary>
    public static TerrainDocument? Doc { get; private set; }

    /// <summary>Opens the editor on a map (a stock mission, or one in the addon folder) at the next scene load.</summary>
    public static void Begin(string mission)
    {
        End();
        I76.Formats.Vfs.SessionStore.Begin();   // what the editor's builds decode is kept for its playtests, until the session ends
        Active = true;
        Mission = mission.ToLowerInvariant();
    }

    /// <summary>Back to the game: everything forgotten.</summary>
    public static void End()
    {
        Active = Playtest = Modified = false;
        PlaytestAt = null; PlaytestRay = null; PlaytestNoRay = null; Samples = null; Undo = null; Pose = null; Doc = null; Roads = null; Layer = null; Look = null; StyleTab = -1;
        I76.Game.Editor.Intersections.IntersectionSession.End();
        I76.Game.Boot.Main.Stash.Drop();
        I76.Formats.Vfs.SessionStore.End();
    }

    /// <summary>
    /// The hook MissionBuilder runs on the world it has just read (before anything is built from it): wraps the terrain in the
    /// document and puts the carried heights in, so that the editor's view, a playtest's ground and its colliders all stand on them.
    /// </summary>
    public static void Prepare(MissionWorld world, Func<string, string?> resolveDefinition)
    {
        // the map style: the file's look, and the editor's own over it (the palette, sky, ground, horizon and hour the world is built from)
        var fileLook = I76.Formats.Mission.WorldSettings.Read(world.Mission) is { } ws ? I76.Formats.Mission.MapLook.Of(ws) : null;
        if (Look is { } shown && !shown.Equals(fileLook) && I76.Formats.Mission.WorldSettings.Patch(world.Mission, shown)) world.RefreshLook();
        var listed = I76.Formats.Mission.WorldSettings.ArenaName(world.Mission)?.Trim();
        Roads ??= RoadStore.FromMission(world.Mission.Data);
        I76.Game.Editor.Intersections.IntersectionSession.Prepare(world, Roads);   // the pieces beside the roads
        if (Roads.Dirty) world.Mission.RoadOverride = Roads.ToSegments();   // the world (the sim's road routes, the painted roads) has the edited roads
        var doc = new TerrainDocument(world.Terrain, world.Mission.Name, string.IsNullOrEmpty(listed) ? world.Mission.Name : listed, world.Mission.Path, Install?.AddonDir, Install, Roads);
        doc.InitLook(fileLook, Look);
        doc.HorizonEditable = world.Mission.WorldRevision() != 8;
        if (Samples is { } s)
        {
            if (!doc.Restore(s)) GD.PrintErr("editor: the carried heights do not fit the map");
            doc.SetModified(Modified);
        }
        // the placed objects: read from the file once, then kept (a playtest's world is made from them)
        Layer ??= new ObjectLayer(I76.Formats.Mission.Edit.MapDocument.Load(world.Mission.Path), resolveDefinition, Roads);
        // a playtest with the editor's scene kept aside (MainStash.cs) builds its world from the same layer, but the editor scene stays the layer's, the undo stack's and the session's document's owner
        bool playsBesideEditor = Playtest && I76.Game.Boot.Main.Stash.Holds;
        if (!playsBesideEditor)
        {
            Layer.Resolve = resolveDefinition;
            Layer.Ground = (x, z) => (float)world.BilinearHeight(x, z);
            doc.Layer = Layer;
        }
        world.Placements.Clear();
        world.Placements.AddRange(Layer.Items.Select(i => i.Placement));
        I76.Game.Editor.Intersections.IntersectionSession.Sync(world);   // the intersection pieces are the road tool's (they follow the roads)
        if (playsBesideEditor) return;
        Doc = doc;
        if (Undo is null) Undo = new UndoStack(doc);
        Undo.Doc = doc;
        GD.Print($"editor: {doc.Name} ({doc.BaseMission}), {doc.Tiles.TileCount} tiles{(doc.Modified ? ", edited" : "")}{(doc.NeedsSaveAs ? ", saving it asks for a new name" : "")}");
    }

    /// <summary>Keeps the document's heights for the next scene (a playtest, or the editor again after one). A map saved under a new name is that map from now on.</summary>
    public static void Capture()
    {
        if (Doc is null) return;
        Samples = Doc.Snapshot();
        Look = Doc.Look;
        Modified = Doc.Modified;
        Mission = Doc.BaseMission.ToLowerInvariant();
    }
}
