using I76.Formats.Mission;

namespace I76.Editor;

/// <summary>
/// What the editor edits: a map's heightfield in memory. The editor, the brushes and the undo stack work only through this.
/// Saving goes through the map writers (I76.Formats.Mission.Edit, docs/map-format-writing.md): <see cref="Save"/> writes the
/// mission and its .ter to the install's addon\ folder in the original formats.
///
/// Heights are the .ter's raw samples: bits 0-11 the height in tenths of a metre (0 to 409.5 m), bits 12-15 the surface and
/// edge nibble, which a height edit never changes. Sample coordinates are global: row r of the ZMAP grid covers sz in
/// [128 r, 128 r + 128), column c sx likewise; a sample is 5 world units from the next.
/// </summary>
public interface IMapDocument
{
    /// <summary>The name shown in the editor (the map's listed name, else its file name).</summary>
    string Name { get; }
    /// <summary>The map's file stem (its mission file without the extension): what the games call it.</summary>
    string BaseMission { get; }

    /// <summary>The live heightfield: the same instance the game draws and the simulation walks. Read it; change it only through the document.</summary>
    TerrainTiles Tiles { get; }

    bool Has(int sx, int sz);
    /// <summary>The 12-bit height (tenths of a metre) at a sample; 0 outside the map.</summary>
    int GetHeight(int sx, int sz);
    /// <summary>Sets a sample's 12-bit height (clamped to 0..4095), keeping its nibble. Does nothing outside the map.</summary>
    void SetHeight(int sx, int sz, int raw);

    /// <summary>Where a global sample lives: its tile and its index in that tile. False outside the map.</summary>
    bool Locate(int sx, int sz, out int tile, out int offset);
    /// <summary>The whole raw sample (height and nibble) at a tile's index.</summary>
    ushort GetSample(int tile, int offset);
    /// <summary>Writes a whole raw sample: what undo and redo use.</summary>
    void SetSample(int tile, int offset, ushort value);

    /// <summary>Raised after every change with the samples it touched (in every grid cell showing the tile).</summary>
    event Action<SampleRect>? Changed;

    /// <summary>Whether the heights differ from the last save (or the load).</summary>
    bool Modified { get; }

    /// <summary>The mission file the map was read from.</summary>
    string? SourcePath { get; }
    /// <summary>
    /// True when <see cref="Save"/> must be given a new name: the map was read from somewhere other than the save folder (a stock
    /// mission in miss16, a map in a subfolder), and a stock file is never written in place.
    /// </summary>
    bool NeedsSaveAs { get; }

    /// <summary>
    /// Writes the map into the save folder (the install's addon\), mission and .ter, and returns the files. With
    /// <paramref name="newName"/> (1-8 letters, digits, underscores) it is saved as a copy under that name with
    /// <paramref name="displayName"/> (up to 15 characters) as its listed name, and the document is that map from then on.
    /// </summary>
    IReadOnlyList<string> Save(string? newName = null, string? displayName = null);
}
