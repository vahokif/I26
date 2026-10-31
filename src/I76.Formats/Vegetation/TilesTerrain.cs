using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Formats.Vegetation;

/// <summary>
/// The samples' own heights as ground for plants, bilinear (what the cars ride on): no drawn surface behind it, so it costs a few
/// array reads a call. For the sward map of a map too big for a surface over all of it (<see cref="TerrainTiles.Huge"/>), which
/// reads the ground at every texel of the map.
/// </summary>
public sealed class TilesTerrain(TerrainTiles tiles) : IVegTerrain
{
    private const double K = MissionWorld.SampleScale;

    private double H(int sx, int sz) => (tiles.Height(sx, sz) ?? tiles.Height(sx - 1, sz) ?? tiles.Height(sx, sz - 1) ?? 0) * MissionWorld.HeightScale;

    private double Bilinear(double fx, double fz)
    {
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        double a = H(ix, iz) + (H(ix + 1, iz) - H(ix, iz)) * tx;
        double b = H(ix, iz + 1) + (H(ix + 1, iz + 1) - H(ix, iz + 1)) * tx;
        return a + (b - a) * tz;
    }

    public bool Has(double x, double z) => tiles.Sample((int)Math.Floor(x * K), (int)Math.Floor(z * K)) is not null;
    public float Height(double x, double z) => (float)Bilinear(x * K, z * K);
    public float Ground(double x, double z) => Height(x, z);

    public float Slope(double x, double z)
    {
        double fx = x * K, fz = z * K, e = 0.3;
        double gx = (Bilinear(fx + e, fz) - Bilinear(fx - e, fz)) / (2 * e / K);
        double gz = (Bilinear(fx, fz + e) - Bilinear(fx, fz - e)) / (2 * e / K);
        return (float)Math.Sqrt(gx * gx + gz * gz);
    }

    /// <summary>Ground gentler than a car can climb (<see cref="TerrainSurface.WallGradient"/>): with no road to start from, the gentle cells are the open ones.</summary>
    /// <summary>Rough ground: terrain surface id 5, read as the ground does (<c>TerrainGround.SurfaceId</c>: the sample nearest, bits 13-15).</summary>
    public bool Rough(double x, double z) =>
        ((tiles.Sample((int)Math.Floor(x * K + 0.5), (int)Math.Floor(z * K + 0.5)) ?? 0) >> 13 & 7) == 5;

    /// <summary>A road surface (ids 2, 3, 4, 6, 7), read like <see cref="Rough"/>.</summary>
    public bool RoadSurface(double x, double z) =>
        VegSurface.IsRoad(tiles.Sample((int)Math.Floor(x * K + 0.5), (int)Math.Floor(z * K + 0.5)) ?? 0);

    public bool Open(double x, double z) => Slope(x, z) <= TerrainSurface.WallGradient;
}
