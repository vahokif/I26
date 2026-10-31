namespace I76.Editor.Geo;

/// <summary>
/// Where a geographic box lands on the game's grid: whole 640 m tiles centred on the box (so the box's middle is the map's middle),
/// at most 254 of them (tile ids are bytes, 255 is empty). The map's own coordinates run from (0, 0) at its south-west corner,
/// x east and z north (the game is left-handed, +Z is north), in metres; <see cref="WorldX"/> and <see cref="WorldZ"/> add the grid cell it sits at.
/// </summary>
public sealed record GeoGrid(GeoProjection Projection, int Cols, int Rows, int Col0, int Row0, double BoxWidth, double BoxHeight, string? CropNote)
{
    public const double TileMetres = 640;
    public const int MaxTiles = 254;
    /// <summary>Row 78 is the last row the stock arenas use (M01's spawn is at z 49997); the map's last row lies there, its first column at the 4 a blank map gets.</summary>
    public const int LastRow = 78, FirstCol = 4;
    /// <summary>A box this much (fraction of a tile) over a whole number of tiles is cropped to it, not given another tile (a 10.26 km box is 16.03 tiles: 16 are 10.24 km).</summary>
    public const double Tolerance = 0.05;

    public double Width => Cols * TileMetres;
    public double Height => Rows * TileMetres;
    public int Tiles => Cols * Rows;
    public double WorldX(double x) => Col0 * TileMetres + x;
    public double WorldZ(double z) => Row0 * TileMetres + z;

    public static GeoGrid Plan(double latA, double lonA, double latB, double lonB)
    {
        double lat0 = (latA + latB) / 2, lon0 = (lonA + lonB) / 2;
        var proj = new GeoProjection(lat0, lon0);
        double w = Math.Abs(lonA - lonB) * proj.MetresPerDegLon, h = Math.Abs(latA - latB) * proj.MetresPerDegLat;
        int cols = Math.Max(1, (int)Math.Ceiling(w / TileMetres - Tolerance)), rows = Math.Max(1, (int)Math.Ceiling(h / TileMetres - Tolerance));
        string? note = null;
        if (cols * rows > MaxTiles || cols > 76 || rows > 79)
        {
            int c0 = cols, r0 = rows;
            while (cols * rows > MaxTiles || cols > 76 || rows > 79) { if (cols >= rows) cols--; else rows--; }
            note = $"the box needs {c0} x {r0} = {c0 * r0} tiles but a map holds at most {MaxTiles} (and fits the 80 x 80 grid): cropped to the centred {cols} x {rows} = {cols * rows}";
        }
        return new GeoGrid(proj, cols, rows, FirstCol, LastRow + 1 - rows, w, h, note);
    }

    /// <summary>The map coordinates (x east, z north, metres from the south-west corner) of a latitude/longitude.</summary>
    public (double X, double Z) ToMap(double lat, double lon)
    {
        var (e, n) = Projection.ToLocal(lat, lon);
        return (e + Width / 2, n + Height / 2);
    }

    public (double Lat, double Lon) ToLatLon(double x, double z) => Projection.ToLatLon(x - Width / 2, z - Height / 2);

    /// <summary>The box the map covers (south, west, north, east), a little over the tiles for the terrain to reach.</summary>
    public (double South, double West, double North, double East) Bounds(double marginMetres = 0)
    {
        var (s, w) = ToLatLon(-marginMetres, -marginMetres);
        var (n, e) = ToLatLon(Width + marginMetres, Height + marginMetres);
        return (Math.Min(s, n), Math.Min(w, e), Math.Max(s, n), Math.Max(w, e));
    }
}

/// <summary>A heightfield in metres packed into the .ter's 12-bit samples (tenths of a metre, 0 to 409.5 m).</summary>
public sealed record PackedHeights(ushort[] Raw, double MinMetres, double MaxMetres, double OffsetMetres, double Scale)
{
    public bool Compressed => Scale < 1;
    /// <summary>Metres below the lowest ground the sample 0 lies, so cuts under it do not clip.</summary>
    public const double LowMargin = 10, HighMargin = 15;

    /// <summary>
    /// The lowest ground minus <see cref="LowMargin"/> is sample 0. When the relief (with the high margin) is more than 409.5 m the
    /// heights are scaled down by one factor (<see cref="Scale"/> below 1, vertically compressed) so the highest point fits.
    /// </summary>
    public static PackedHeights Pack(IReadOnlyList<double> metres)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (double v in metres) { min = Math.Min(min, v); max = Math.Max(max, v); }
        double offset = min - LowMargin, range = max + HighMargin - offset;
        double scale = range > Units.MaxMetres ? Units.MaxMetres / range : 1;
        var raw = new ushort[metres.Count];
        for (int i = 0; i < raw.Length; i++) raw[i] = (ushort)Math.Clamp((int)Math.Round((metres[i] - offset) * scale / Units.HeightScale), 0, 4095);
        return new PackedHeights(raw, min, max, offset, scale);
    }
}
