using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Editor;

/// <summary>
/// An in-memory map: the mission's own <see cref="TerrainTiles"/> edited in place. A tile the ZMAP grid shows in more than one
/// cell is one set of samples, so an edit shows in every cell (the original's data has it that way); <see cref="Changed"/>
/// reports every cell's rectangle.
/// </summary>
public sealed class TerrainDocument : IMapDocument
{
    public const int MaxHeight = MissionFile.HeightMask;   // 4095 = 409.5 m
    private readonly List<(int Row, int Col)>[] _cells;
    private int _dirty;
    private long _revision;

    /// <summary>
    /// Counts every change to the map as the editor holds it (heights, roads, placed objects, the look): never goes down, not even by a save or an undo.
    /// A copy of the world built at one revision shows the map exactly while the revision is still that one.
    /// </summary>
    public long Revision => _revision + Roads.Version + (Layer?.Version ?? 0) + (Paths?.Version ?? 0);

    /// <param name="sourcePath">The mission file the map was read from (saving reads it again to write over).</param>
    /// <param name="saveDir">Where saves go: the install's addon\ folder.</param>
    public TerrainDocument(TerrainTiles tiles, string baseMission, string? name = null, string? sourcePath = null, string? saveDir = null, GameInstall? install = null, RoadStore? roads = null)
    {
        Roads = roads ?? new RoadStore([]);
        Tiles = tiles;
        BaseMission = baseMission;
        Name = name ?? baseMission;
        SourcePath = sourcePath;
        SaveDir = saveDir;
        Install = install;
        _cells = new List<(int, int)>[tiles.TileCount];
        for (int t = 0; t < _cells.Length; t++) _cells[t] = [];
        for (int r = 0; r < MissionFile.ZmapGrid; r++)
            for (int c = 0; c < MissionFile.ZmapGrid; c++)
                if (tiles.TileAt(r, c) is var t && t != MissionFile.ZmapEmpty) _cells[t].Add((r, c));
    }

    public string Name { get; set; }
    public string BaseMission { get; private set; }
    public string? SourcePath { get; private set; }
    public string? SaveDir { get; }
    public GameInstall? Install { get; }
    public TerrainTiles Tiles { get; }
    public event Action<SampleRect>? Changed;
    /// <summary>The map's roads (the road tool edits them; Save writes them when they changed).</summary>
    public RoadStore Roads { get; }
    /// <summary>The map's placed objects as the editor holds them (set when the editor opens the map); saved with the heights.</summary>
    public Objects.ObjectLayer? Layer { get; set; }
    /// <summary>The script's nav paths as the editor holds them (set when the editor opens the map); saved with the heights.</summary>
    public Paths.PathStore? Paths { get; set; }
    public bool Modified => _dirty != 0 || Roads.Dirty || Layer is { } l && l.Version != l.SavedVersion || Paths is { Dirty: true } || Look != FileLook;

    /// <summary>The map's look as it is now (the map style: palette, sky, ground, horizon, hour). <see cref="LookChanged"/> fires when it is set.</summary>
    public MapLook? Look { get; private set; }
    /// <summary>The look the map's file holds (what a save would leave as it is): <see cref="Modified"/> when <see cref="Look"/> differs.</summary>
    public MapLook? FileLook { get; private set; }
    /// <summary>Whether the map's WRLD has a horizon field (a WREV 8 level has none).</summary>
    public bool HorizonEditable { get; set; } = true;
    /// <summary>Raised after <see cref="SetLook"/>: the world has to be rebuilt to show it (the editor does, keeping every edit).</summary>
    public event Action? LookChanged;

    /// <summary>Starts the look: what the file holds, and what the editor was showing (a look not yet saved, carried across a reload).</summary>
    public void InitLook(MapLook? file, MapLook? current) { FileLook = file; Look = current ?? file; }

    /// <summary>Changes the look (an edit, or its undo): the map is rebuilt to show it.</summary>
    public void SetLook(MapLook look)
    {
        if (look.Equals(Look)) return;
        Look = look; _revision++;
        LookChanged?.Invoke();
    }

    public bool Has(int sx, int sz) => Tiles.Sample(sx, sz) is not null;

    public int GetHeight(int sx, int sz) => (Tiles.Sample(sx, sz) ?? 0) & MaxHeight;

    public bool Locate(int sx, int sz, out int tile, out int offset)
    {
        tile = offset = -1;
        if (sx < 0 || sz < 0) return false;
        int col = sx / TerrainTiles.Tile, row = sz / TerrainTiles.Tile;
        if (col >= MissionFile.ZmapGrid || row >= MissionFile.ZmapGrid) return false;
        int t = Tiles.TileAt(row, col);
        if (t == MissionFile.ZmapEmpty) return false;
        tile = t;
        offset = (sz % TerrainTiles.Tile) * TerrainTiles.Tile + sx % TerrainTiles.Tile;
        return true;
    }

    public ushort GetSample(int tile, int offset) => Tiles.TileSamples(tile)[offset];

    public void SetHeight(int sx, int sz, int raw)
    {
        if (!Locate(sx, sz, out int tile, out int offset)) return;
        ushort old = GetSample(tile, offset);
        ushort now = (ushort)((old & ~MaxHeight) | Math.Clamp(raw, 0, MaxHeight));
        if (now == old) return;
        Tiles.SetSample(sx, sz, now);
        _dirty++; _revision++;
        Notify(tile, new SampleRect(sx, sz, sx, sz));
    }

    public void SetSample(int tile, int offset, ushort value)
    {
        if (GetSample(tile, offset) == value) return;
        int lx = offset % TerrainTiles.Tile, lz = offset / TerrainTiles.Tile;
        var (r0, c0) = _cells[tile][0];
        Tiles.SetSample(c0 * TerrainTiles.Tile + lx, r0 * TerrainTiles.Tile + lz, value);
        _dirty++; _revision++;
        foreach (var (r, c) in _cells[tile])
        {
            int sx = c * TerrainTiles.Tile + lx, sz = r * TerrainTiles.Tile + lz;
            Changed?.Invoke(new SampleRect(sx, sz, sx, sz));
        }
    }

    /// <summary>Raises <see cref="Changed"/> for a rectangle of the tile: where it was written, and the other cells showing the tile.</summary>
    private void Notify(int tile, SampleRect r)
    {
        Changed?.Invoke(r);
        if (_cells[tile].Count < 2) return;
        int cx = r.X0 / TerrainTiles.Tile, cz = r.Z0 / TerrainTiles.Tile;
        foreach (var (row, col) in _cells[tile])
        {
            if (row == cz && col == cx) continue;
            int dx = (col - cx) * TerrainTiles.Tile, dz = (row - cz) * TerrainTiles.Tile;
            Changed?.Invoke(new SampleRect(r.X0 + dx, r.Z0 + dz, r.X1 + dx, r.Z1 + dz));
        }
    }

    /// <summary>Every sample of every tile, in tile order: what survives a reload of the scene (a playtest) and what a save writes.</summary>
    public ushort[] Snapshot()
    {
        var all = new ushort[Tiles.TileCount * TerrainTiles.Tile * TerrainTiles.Tile];
        for (int t = 0; t < Tiles.TileCount; t++) Tiles.TileSamples(t).CopyTo(all.AsSpan(t * TerrainTiles.Tile * TerrainTiles.Tile));
        return all;
    }

    /// <summary>Puts a <see cref="Snapshot"/> back without reporting each sample (the caller redraws everything). False if it does not fit.</summary>
    public bool Restore(ushort[] samples)
    {
        int n = TerrainTiles.Tile * TerrainTiles.Tile;
        if (samples.Length != Tiles.TileCount * n) return false;
        for (int t = 0; t < Tiles.TileCount; t++)
            if (_cells[t].Count > 0)
                for (int i = 0; i < n; i++) PutSample(t, i, samples[t * n + i]);
        _dirty++; _revision++;
        return true;
    }

    /// <summary>Writes a sample without reporting it (the caller redraws everything).</summary>
    private void PutSample(int tile, int offset, ushort value)
    {
        if (GetSample(tile, offset) == value) return;
        var (r, c) = _cells[tile][0];
        Tiles.SetSample(c * TerrainTiles.Tile + offset % TerrainTiles.Tile, r * TerrainTiles.Tile + offset / TerrainTiles.Tile, value);
    }

    /// <summary>Sets the modified flag: a map reopened after a playtest carries it over.</summary>
    public void SetModified(bool modified) { _dirty = modified ? Math.Max(1, _dirty) : 0; if (!modified) { Roads.MarkSaved(); if (Layer is { } l) l.SavedVersion = l.Version; Paths?.MarkSaved(); } }

    public void MarkSaved() { _dirty = 0; Roads.MarkSaved(); if (Layer is { } l) l.SavedVersion = l.Version; Paths?.MarkSaved(); }

    /// <summary>A flat map: every sample at <paramref name="raw"/> with a clear nibble (drivable, no walls), reported as one change.</summary>
    public void Flatten(int raw)
    {
        raw = Math.Clamp(raw, 0, MaxHeight);
        for (int t = 0; t < Tiles.TileCount; t++)
            if (_cells[t].Count > 0)
                for (int i = 0; i < TerrainTiles.Tile * TerrainTiles.Tile; i++) PutSample(t, i, (ushort)raw);
        _dirty++; _revision++;
        Changed?.Invoke(Bounds());
    }

    /// <summary>The occupied cells' rectangle, in samples.</summary>
    public SampleRect Bounds()
    {
        var (r0, r1, c0, c1) = Tiles.Bounds();
        return new SampleRect(c0 * TerrainTiles.Tile, r0 * TerrainTiles.Tile, (c1 + 1) * TerrainTiles.Tile - 1, (r1 + 1) * TerrainTiles.Tile - 1);
    }

    public bool NeedsSaveAs => SourcePath is null || SaveDir is null || !MapSaver.SameFolder(Path.GetDirectoryName(Path.GetFullPath(SourcePath))!, SaveDir);

    public IReadOnlyList<string> Save(string? newName = null, string? displayName = null)
    {
        if (SourcePath is null || SaveDir is null) throw new InvalidOperationException("this map has no file to save over");
        if (NeedsSaveAs && newName is null) throw new InvalidOperationException("a map that is not in the save folder is saved under a new name");
        var (project, files) = MapSaver.Save(this, SourcePath, SaveDir, Install, newName, displayName, Roads);
        // the document is the map it has saved from now on
        BaseMission = project.Name;
        SourcePath = files.First(f => Path.GetFileName(f).Equals(project.MissionFileName, StringComparison.OrdinalIgnoreCase));
        Name = project.Mission.ArenaName is { Length: > 0 } listed ? listed.Trim() : project.Name;
        FileLook = Look;
        MarkSaved();
        return files;
    }
}
