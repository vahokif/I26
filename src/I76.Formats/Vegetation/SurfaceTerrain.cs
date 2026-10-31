using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Formats.Vegetation;

/// <summary>
/// The modern look's drawn terrain (<see cref="TerrainSurface"/>) as ground for plants. The height is the lower of
/// the surface interpolated over the 1.25 m and the 2.5 m grids of its two finer levels of detail, so a plant stands
/// on whichever the camera draws near it and never floats over the other.
/// </summary>
public sealed class SurfaceTerrain(ITerrainSurface surface, TerrainTiles tiles) : IVegTerrain
{
    private const double K = MissionWorld.SampleScale;

    /// <summary>
    /// A copy that remembers the drawn points it has read (not thread-safe): for the rolling tumbleweeds, which read
    /// the same few cells frame after frame on the main thread, and a drawn point off the drivable cells is costly.
    /// </summary>
    public SurfaceTerrain Cached() => new(surface, tiles) { _cache = [] };
    private Dictionary<(int, int), Vector3>? _cache;

    /// <summary>The drawn point at sample coordinates on the quarter-sample grid (remembered in a cached copy).</summary>
    private Vector3 Point(double a, double b)
    {
        if (_cache is null) return surface.Point(a, b, out _);
        var key = ((int)Math.Round(a * 4), (int)Math.Round(b * 4));
        if (_cache.TryGetValue(key, out var p)) return p;
        if (_cache.Count > 1 << 16) _cache.Clear();
        return _cache[key] = surface.Point(a, b, out _);
    }

    /// <summary>
    /// Terrain here, and drawn where it is computed: off the drivable cells the surface's relief moves its points
    /// sideways too (cliffs, mesa rims), and a plant there could stand inside a bulge; such ground grows nothing.
    /// </summary>
    public bool Has(double x, double z)
    {
        int sx = (int)Math.Floor(x * K), sz = (int)Math.Floor(z * K);
        if (tiles.Sample(sx, sz) is null || tiles.Sample(sx + 1, sz + 1) is null) return false;
        if (surface.Open(sx, sz)) return true;
        double fx = x * K, fz = z * K, gx = Math.Floor(fx / 0.25) * 0.25, gz = Math.Floor(fz / 0.25) * 0.25;
        for (int k = 0; k < 4; k++)
        {
            double a = gx + (k & 1) * 0.25, b = gz + (k >> 1) * 0.25;
            var p = Point(a, b);
            if (Math.Abs(p.X - a / K) + Math.Abs(p.Z - b / K) > MaxShift) return false;
        }
        return true;
    }

    /// <summary>The most the drawn ground's points may have moved sideways (metres) for a plant to stand there.</summary>
    public const double MaxShift = 0.35;

    public float Height(double x, double z)
    {
        double fx = x * K, fz = z * K;
        return (float)Math.Min(Grid(fx, fz, 0.25), Grid(fx, fz, 0.5));
    }

    private double Grid(double fx, double fz, double step)
    {
        double gx = Math.Floor(fx / step) * step, gz = Math.Floor(fz / step) * step;
        double tx = (fx - gx) / step, tz = (fz - gz) / step;
        double Y(double a, double b) => Point(a, b).Y;
        double y00 = Y(gx, gz), y10 = Y(gx + step, gz), y01 = Y(gx, gz + step), y11 = Y(gx + step, gz + step);
        // the drawn quad's two triangles, split as the level-of-detail meshes split them (corner 10 to 01, TerrainLodMesh)
        return tx + tz <= 1 ? y00 + (y10 - y00) * tx + (y01 - y00) * tz : y11 + (y01 - y11) * (1 - tx) + (y10 - y11) * (1 - tz);
    }

    public float Slope(double x, double z)
    {
        double fx = x * K, fz = z * K, e = 0.3;
        double gx = (surface.Bilinear(fx + e, fz) - surface.Bilinear(fx - e, fz)) / (2 * e / K);
        double gz = (surface.Bilinear(fx, fz + e) - surface.Bilinear(fx, fz - e)) / (2 * e / K);
        return (float)Math.Sqrt(gx * gx + gz * gz);
    }

    /// <summary>The bilinear surface, a hundredth of <see cref="Height"/>'s cost (the sward map reads it at every texel).</summary>
    public float Ground(double x, double z) => (float)surface.Bilinear(x * K, z * K);

    /// <summary>Rough ground: terrain surface id 5, read as the ground does (<c>TerrainGround.SurfaceId</c>: the sample nearest, bits 13-15).</summary>
    public bool Rough(double x, double z) =>
        ((tiles.Sample((int)Math.Floor(x * K + 0.5), (int)Math.Floor(z * K + 0.5)) ?? 0) >> 13 & 7) == 5;

    /// <summary>A road surface (ids 2, 3, 4, 6, 7), read like <see cref="Rough"/>.</summary>
    public bool RoadSurface(double x, double z) =>
        VegSurface.IsRoad(tiles.Sample((int)Math.Floor(x * K + 0.5), (int)Math.Floor(z * K + 0.5)) ?? 0);

    public bool Open(double x, double z) => surface.Open((int)Math.Floor(x * K), (int)Math.Floor(z * K));

    /// <summary>The mission's low ground: the height under which a small share (<paramref name="quantile"/>) of the open (drivable) samples lie.</summary>
    public static float LowLevel(ITerrainSurface surface, TerrainTiles tiles, double quantile = 0.03)
    {
        var (r0, r1, c0, c1) = tiles.Bounds();
        var hs = new List<float>();
        if (tiles.Huge)
        {
            // a map of thousands of cells: its samples' own heights, every eighth (reading the surface would make every block of it)
            for (int sz = r0 * TerrainTiles.Tile; sz < (r1 + 1) * TerrainTiles.Tile; sz += 8)
                for (int sx = c0 * TerrainTiles.Tile; sx < (c1 + 1) * TerrainTiles.Tile; sx += 8)
                    if (tiles.Height(sx, sz) is { } h) hs.Add((float)(h * MissionWorld.HeightScale));
            if (hs.Count == 0) return 0;
            hs.Sort();
            return hs[(int)(quantile * (hs.Count - 1))];
        }
        for (int sz = r0 * TerrainTiles.Tile; sz < (r1 + 1) * TerrainTiles.Tile; sz += 2)
            for (int sx = c0 * TerrainTiles.Tile; sx < (c1 + 1) * TerrainTiles.Tile; sx += 2)
                if (surface.Open(sx, sz)) hs.Add((float)surface.Bilinear(sx + 0.5, sz + 0.5));
        if (hs.Count == 0) return 0;
        hs.Sort();
        return hs[(int)(quantile * (hs.Count - 1))];
    }
}
