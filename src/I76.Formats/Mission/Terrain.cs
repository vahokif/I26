namespace I76.Formats.Mission;

/// <summary>
/// A mission's .ter: raw u16 samples in 128×128 tiles, placed by the ZMAP
/// grid. Bits 0–11 are height, 12–15 a surface/edge nibble.
/// </summary>
public sealed class TerrainTiles
{
    public const int Tile = MissionFile.TerrainTile;
    public int TileCount { get; private set; }
    public byte[] Grid { get; }
    private ushort[] _samples;
    private readonly byte[] _orig;
    private bool _extended;

    /// <summary>
    /// Tiles ZMAP counts that the .ter is too short to hold, read as flat ground at height 0. The engines map the file and
    /// point tile i at base + i·0x8000 with no size check (ZONE handler), so such a level reads past its file and
    /// faults (measured: the community pack's obsid.cbt, one tile for ten, faults in the terrain sampler).
    /// </summary>
    public int MissingTiles { get; }

    /// <summary>
    /// The tiles of a .ter: <paramref name="tileCount"/> tiles of 128×128 samples. A file shorter than that has its missing
    /// tiles zeroed (<see cref="MissingTiles"/>); bytes past the last tile are ignored, as the engine ignores them.
    /// </summary>
    public TerrainTiles(byte[] ter, int tileCount, byte[] grid)
    {
        TileCount = tileCount;
        Grid = grid;
        _orig = ter;
        _samples = new ushort[tileCount * Tile * Tile];
        int n = Math.Min(_samples.Length, ter.Length / 2);
        for (int i = 0; i < n; i++) _samples[i] = Bin.U16(ter, i * 2);
        MissingTiles = (_samples.Length - n + Tile * Tile - 1) / (Tile * Tile);
    }

    public static TerrainTiles Load(MissionFile msn, string terPath) => Load(msn, File.ReadAllBytes(terPath));

    public static TerrainTiles Load(MissionFile msn, byte[] ter)
    {
        var (n, grid) = msn.TerrainGrid();
        return new TerrainTiles(ter, n, grid);
    }

    /// <summary>Tile id at grid cell (row, col), or 255 if the cell is empty or names a tile past ZMAP's count (the ZMAP handler rejects such a grid: "zone number too big").</summary>
    /// <summary>A hash of everything that is drawn or driven on: the tile grid and every sample (heights and surface bits). Equal terrain hashes equal; used to know a build's work is still good.</summary>
    public ulong ContentHash()
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in Grid) h = (h ^ b) * 1099511628211UL;
        h = (h ^ (ulong)TileCount) * 1099511628211UL;
        var s = _samples.AsSpan(0, Math.Min(_samples.Length, TileCount * Tile * Tile));
        int i = 0;
        for (; i + 3 < s.Length; i += 4)
            h = (h ^ ((ulong)s[i] | (ulong)s[i + 1] << 16 | (ulong)s[i + 2] << 32 | (ulong)s[i + 3] << 48)) * 1099511628211UL;
        for (; i < s.Length; i++) h = (h ^ s[i]) * 1099511628211UL;
        return h;
    }

    public byte TileAt(int row, int col) => Grid[row * MissionFile.ZmapGrid + col] is var t && t < TileCount ? t : MissionFile.ZmapEmpty;

    /// <summary>
    /// The raw sample at global sample coordinates (sx along X, sz along Z),
    /// where row r of the grid covers sz in [r*128, r*128+128). Null outside any tile.
    /// </summary>
    public ushort? Sample(int sx, int sz)
    {
        if (sx < 0 || sz < 0) return null;
        int col = sx / Tile, row = sz / Tile;
        if (col >= MissionFile.ZmapGrid || row >= MissionFile.ZmapGrid) return null;
        byte t = TileAt(row, col);
        if (t == MissionFile.ZmapEmpty || t >= TileCount) return null;
        return _samples[t * Tile * Tile + (sz % Tile) * Tile + (sx % Tile)];
    }

    // ---- writing (docs/map-format-writing.md) ----

    /// <summary>The raw sample (height | surface nibble) set at global sample coordinates; false if no tile covers them.</summary>
    public bool SetSample(int sx, int sz, ushort value)
    {
        if (sx < 0 || sz < 0) return false;
        int col = sx / Tile, row = sz / Tile;
        if (col >= MissionFile.ZmapGrid || row >= MissionFile.ZmapGrid) return false;
        byte t = TileAt(row, col);
        if (t == MissionFile.ZmapEmpty || t >= TileCount) return false;
        int i = t * Tile * Tile + (sz % Tile) * Tile + (sx % Tile);
        if (i * 2 + 2 > _orig.Length) _extended = true;
        _samples[i] = value;
        return true;
    }

    /// <summary>The 12-bit height at a sample (0-4095; the world height is this times 0.1), or null outside any tile.</summary>
    public int? Height(int sx, int sz) => Sample(sx, sz) is { } v ? v & MissionFile.HeightMask : null;

    /// <summary>Set a sample's 12-bit height (clamped to 0-4095), keeping its surface nibble.</summary>
    public bool SetHeight(int sx, int sz, int height) =>
        Sample(sx, sz) is { } v && SetSample(sx, sz, (ushort)((v & ~MissionFile.HeightMask) | Math.Clamp(height, 0, MissionFile.HeightMask)));

    /// <summary>Set a sample's surface nibble (bits 12-15), keeping its height.</summary>
    public bool SetSurface(int sx, int sz, int surface) =>
        Sample(sx, sz) is { } v && SetSample(sx, sz, (ushort)((v & MissionFile.HeightMask) | ((surface & 0xf) << MissionFile.SurfaceShift)));

    /// <summary>
    /// Add a tile of flat ground at a grid cell that is empty (the new tile takes the next id: ZMAP's count goes up by
    /// one and the cell's id is set; the caller writes the document's ZMAP from <see cref="Grid"/> and
    /// <see cref="TileCount"/>). Returns the new id, or -1 if the cell is taken or the grid is full (at most 254 tiles).
    /// </summary>
    public int AddTile(int row, int col, ushort fill)
    {
        if (row is < 0 or >= MissionFile.ZmapGrid || col is < 0 or >= MissionFile.ZmapGrid || TileAt(row, col) != MissionFile.ZmapEmpty || TileCount >= 254) return -1;
        var n = new ushort[(TileCount + 1) * Tile * Tile];
        _samples.CopyTo(n, 0);
        Array.Fill(n, fill, TileCount * Tile * Tile, Tile * Tile);
        _samples = n;
        Grid[row * MissionFile.ZmapGrid + col] = (byte)TileCount;
        _extended = true;
        return TileCount++;
    }

    /// <summary>
    /// The .ter as bytes. Untouched, these are the bytes read: samples are little-endian words, and anything past the last
    /// tile (or a file too short for ZMAP's count) is kept, not padded; writing into a tile the file lacked, or adding a
    /// tile, pads the file to whole tiles of 0x8000 bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        int len = _extended ? Math.Max(_orig.Length, TileCount * Tile * Tile * 2) : _orig.Length;
        var o = new byte[len];
        Array.Copy(_orig, o, Math.Min(_orig.Length, len));
        int n = Math.Min(_samples.Length, len / 2);
        for (int i = 0; i < n; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(i * 2), _samples[i]);
        return o;
    }

    public ReadOnlySpan<ushort> TileSamples(int tile) => _samples.AsSpan(tile * Tile * Tile, Tile * Tile);

    /// <summary>
    /// A map whose cells span more than <see cref="HugeCells"/> grid cells of the 80x80 grid (a community map that tiles one
    /// terrain over all 6400 cells, 51 km across): the modern look cannot hold all of it, and streams (<see cref="BlockedSurface"/>).
    /// </summary>
    public bool Huge { get { var (r0, r1, c0, c1) = Bounds(); return r1 >= r0 && (r1 - r0 + 1) * (c1 - c0 + 1) > HugeCells; } }
    public const int HugeCells = 1024;

    /// <summary>Occupied cell bounds: (row0, row1, col0, col1), inclusive.</summary>
    public (int R0, int R1, int C0, int C1) Bounds()
    {
        int r0 = int.MaxValue, r1 = -1, c0 = int.MaxValue, c1 = -1;
        for (int r = 0; r < MissionFile.ZmapGrid; r++)
            for (int c = 0; c < MissionFile.ZmapGrid; c++)
                if (TileAt(r, c) != MissionFile.ZmapEmpty)
                {
                    r0 = Math.Min(r0, r); r1 = Math.Max(r1, r);
                    c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
                }
        return (r0, r1, c0, c1);
    }
}
