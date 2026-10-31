using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// What the modern look draws terrain from: a pure function of a point in sample coordinates (<see cref="TerrainSurface"/>
/// over the whole map, <see cref="BlockedSurface"/> in blocks for a map too big to hold at once).
/// </summary>
public interface ITerrainSurface
{
    /// <summary>The drawn point for sample coordinates: game-frame metres, and the height the shading normal is taken from.</summary>
    Vector3 Point(double fx, double fz, out double shadeY);
    /// <summary>The bilinear surface on the (needle-filtered) samples, metres.</summary>
    double Bilinear(double fx, double fz);
    /// <summary>The smooth surface's normal over a broad span (game frame).</summary>
    Vector3 BroadNormal(double fx, double fz);
    /// <summary>Whether the cell with corner (sx, sz) is drivable ground.</summary>
    bool Open(int sx, int sz);
    /// <summary>Done once what a query near sample (sx, sz) reads is ready (a <see cref="BlockedSurface"/> makes its block meanwhile, off the caller's thread); a whole surface is always ready.</summary>
    Task Ready(int sx, int sz) => Task.CompletedTask;
    /// <summary>The lowest and highest the drawn surface can be over a square of samples.</summary>
    (float Min, float Max) HeightRange(int sx, int sz, int size);
}
