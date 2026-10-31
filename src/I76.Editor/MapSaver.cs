using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Editor;

/// <summary>
/// Saving through the map writers: the mission file is read again (so everything the editor does not edit is written as it was),
/// the document's heights are put into its terrain, and <see cref="MapProject"/> writes both files in the original formats.
/// </summary>
public static class MapSaver
{
    public const int MaxDisplayName = 15;

    /// <summary>Whether a name can be a map's: 1-8 letters, digits and underscores.</summary>
    public static bool ValidName(string name) => MapProject.IsValidStem(name);

    public static bool ValidDisplayName(string name) => name.Length is >= 1 and <= MaxDisplayName && name.All(c => c is >= ' ' and <= '~');

    /// <summary>
    /// Writes <paramref name="doc"/>'s heights over the map at <paramref name="sourcePath"/> and saves it into
    /// <paramref name="saveDir"/>: in place when that is where the file already is, else (or with <paramref name="newName"/>) as a copy under the new name.
    /// Returns the project saved (its name and extension say what the document is now) and the files written.
    /// </summary>
    public static (MapProject Project, IReadOnlyList<string> Files) Save(IMapDocument doc, string sourcePath, string saveDir, GameInstall? install, string? newName, string? displayName, RoadStore? roads = null)
    {
        var project = MapProject.Load(sourcePath, install);
        CopyHeights(doc, project.Terrain);
        // the object layer writes every object it holds, then the roads write theirs and (RoadStore.Pieces) the intersection pieces over the layer's copies
        if (doc is TerrainDocument { Layer: { } layer }) layer.ApplyTo(project.Mission);
        if (roads is { Dirty: true }) roads.WriteTo(project.Mission);
        // the map style: written only where the editor changed them (a map saved as it was stays byte for byte)
        if (doc is TerrainDocument { Look: { } look } && !look.Equals(project.Mission.Look)) project.Mission.Look = look;
        bool inPlace = SameFolder(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, saveDir);
        if (newName is null && !inPlace) throw new InvalidOperationException("a map outside the save folder is saved under a new name");
        if (newName is not null)
        {
            if (!ValidName(newName)) throw new ArgumentException($"map name '{newName}': 1-{MapProject.MaxStem} letters, digits and underscores");
            if (displayName is not null && !ValidDisplayName(displayName)) throw new ArgumentException($"listed name '{displayName}': 1-{MaxDisplayName} characters");
            // the melee lists read only .lvl (the base game) and .cbt (the Nitro Pack): a copy of a story mission's map
            // (Crater is m01.msn) keeps .msn and is never listed, so it is saved as the kind the game lists
            string? ext = project.Extension.Equals(".msn", StringComparison.OrdinalIgnoreCase) ? (install?.IsNitro == true ? ".cbt" : ".lvl") : null;
            project = project.CloneAs(newName, displayName, ext);
        }
        return (project, project.SaveTo(saveDir));
    }

    public static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Puts every sample the document holds that differs into a project's terrain (the same tiles).</summary>
    public static void CopyHeights(IMapDocument doc, TerrainTiles into)
    {
        if (doc.Tiles.TileCount != into.TileCount) throw new InvalidOperationException("the document and the map's terrain have different tiles");
        for (int r = 0; r < MissionFile.ZmapGrid; r++)
            for (int c = 0; c < MissionFile.ZmapGrid; c++)
            {
                if (doc.Tiles.TileAt(r, c) == MissionFile.ZmapEmpty) continue;
                // a tile shown in several cells is written from the first (the samples are the same ones)
                int t = doc.Tiles.TileAt(r, c);
                var samples = doc.Tiles.TileSamples(t);
                for (int z = 0; z < TerrainTiles.Tile; z++)
                    for (int x = 0; x < TerrainTiles.Tile; x++)
                    {
                        int sx = c * TerrainTiles.Tile + x, sz = r * TerrainTiles.Tile + z;
                        ushort v = samples[z * TerrainTiles.Tile + x];
                        if (into.Sample(sx, sz) != v) into.SetSample(sx, sz, v);
                    }
            }
    }
}
