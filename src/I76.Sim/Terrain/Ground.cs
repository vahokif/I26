using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Sim.Terrain;

/// <summary>One entry of the surface table: eight of them, stride 20 bytes.</summary>
/// <summary>A surface record (stride 0x14): grip, rolling, bump, ?, and the rough-terrain damage per second (an int).</summary>
public readonly record struct Surface(float Grip, float Rolling, float Bump, float Unk_0x0c, int Damage);

/// <summary>What the vehicle code asks of the world.</summary>
public interface IGround
{
    /// <summary>Height and normal under (x, z). y is the query height (structure surfaces within ±3 of it).</summary>
    (float Height, Vector3 Normal) Query(double x, double y, double z);

    /// <summary>The surface id at (x, z), sample >> 13.</summary>
    int SurfaceId(double x, double z);

    Surface Surface(int id);

    /// <summary>The height cell's flag 0x1000 at (x, z), which the AI's planner and probes treat as impassable.</summary>
    bool Impassable(double x, double z) => false;

    /// <summary>The bare heightfield: no structure surfaces. The wall sweep uses it.</summary>
    float BareHeight(double x, double z) => Query(x, 0, z).Height;
    Vector3 BareNormal(double x, double z) => Query(x, 0, z).Normal;
}

/// <summary>
/// The heightfield ground (no structure surfaces yet: the engine first checks
/// the polygons of objects in the world, ramps and bridges).
///
/// Off the map's tiles the original reads every sample as 0 (and 0 anywhere outside the 80x80 grid): the map's last cell
/// falls to 0 and a plain at 0 runs on. With <paramref name="outside"/> those samples are the outside ground's instead
/// (<see cref="OutsideGround.Raw"/>, the modern look's distant terrain carried on from the map's edge), read the same way
/// (bilinear, the same normal rule) everywhere, and its surface is <see cref="OutsideGround.SurfaceId"/>.
/// </summary>
public sealed class TerrainGround(TerrainTiles tiles, Surface[] surfaces, OutsideGround? outside = null) : IGround
{
    /// <summary>The ground past the map's tiles, or null for the original's (0 everywhere off the map).</summary>
    public OutsideGround? Outside => outside;

    public const float Scale = 0.2f;        // world -> sample
    public const double Bias = 0.5;         // the add-magic rounding in the range check

    /// <summary>The heightfield's tiles (read only: the Developer panel's landmarks scan them).</summary>
    public TerrainTiles Tiles => tiles;

    public static Surface[] ReadSurfaces(WorldSettings w)
    {
        var s = new Surface[8];
        for (int i = 0; i < 8; i++)
        {
            float F(int k) => BitConverter.ToSingle(w.Unk_0x97, i * 20 + k * 4);
            s[i] = new Surface(F(0), F(1), F(2), F(3), BitConverter.ToInt32(w.Unk_0x97, i * 20 + 16));
        }
        return s;
    }

    private int Raw(int sx, int sz) => tiles.Sample(sx, sz) is { } v ? v & MissionFile.HeightMask : outside?.Raw(sx, sz) ?? 0;

    private static bool InRange(double x, double z)
    {
        double fx = Scale * (float)x + Bias, fz = Scale * (float)z + Bias;
        return fx >= 0 && fz >= 0 && fx <= 0x2800 && fz <= 0x2800;
    }

    /// <summary>Bilinear, floor of the sample coordinate, times 0.1.</summary>
    public float Height(double x, double z)
    {
        if (outside is null && !InRange(x, z)) return 0;
        float fx = Scale * (float)x, fz = Scale * (float)z;
        int sx = (int)MathF.Floor(fx), sz = (int)MathF.Floor(fz);
        float tx = fx - sx, tz = fz - sz;
        float a = Raw(sx, sz) + (Raw(sx + 1, sz) - Raw(sx, sz)) * tx;
        float b = Raw(sx, sz + 1) + (Raw(sx + 1, sz + 1) - Raw(sx, sz + 1)) * tx;
        return (a + (b - a) * tz) * 0.1f;
    }

    /// <summary>
    /// As the original has it. The
    /// sample cell is (ftol(0.2x), ftol(0.2z)), and one of its two triangles
    /// gives the normal. Which one is decided by comparing z − iz with
    /// x − ix — the *world* coordinates (FSUBR from the unscaled arguments)
    /// against the *sample* indices, an engine bug: the half is
    /// fixed by roughly z ≤ x, not by where in the cell the point lies.
    /// <list type="bullet">
    /// <item>z − iz ≤ x − ix: the half through (ix, iz), (ix+1, iz), (ix+1, iz+1);
    /// the normal is (+dh/dx, 1, +dh/dz) normalised — its horizontal part
    /// points into the slope.</item>
    /// <item>otherwise: the half through (ix, iz), (ix, iz+1), (ix+1, iz+1),
    /// with the true normal (−dh/dx, 1, −dh/dz).</item>
    /// </list>
    /// Checked against a trace of the original game on a01 (z ≫ x, so always the second
    /// half): driving into a cliff the engine reflects off the face on the
    /// frame the corner sweep reaches it, which the in-cell choice missed
    /// (the flat half's normal gave a weak reflection and the car climbed
    /// the face).
    /// </summary>
    public Vector3 Normal(double x, double z)
    {
        if (outside is null && !InRange(x, z)) return Vector3.UnitY;
        double fx = Scale * x, fz = Scale * z;
        int ix = (int)fx, iz = (int)fz;       // ftol: truncation
        if (fx < 0) ix = (int)System.Math.Floor(fx);   // (only the outside ground reaches below 0: the cell is the one the height reads)
        if (fz < 0) iz = (int)System.Math.Floor(fz);
        float a, b, c;
        if (z - iz <= x - ix)
        {
            a = Raw(ix + 1, iz); b = Raw(ix, iz); c = Raw(ix + 1, iz + 1);
        }
        else
        {
            a = Raw(ix, iz + 1); b = Raw(ix + 1, iz + 1); c = Raw(ix, iz);
        }
        float gx = Scale * 0.1f * (a - b), gz = Scale * 0.1f * (c - a);
        float inv = 1f / MathF.Sqrt(gx * gx + gz * gz + 1);
        return new Vector3(gx * inv, inv, gz * inv);
    }

    public (float Height, Vector3 Normal) Query(double x, double y, double z) => (Height(x, z), Normal(x, z));
    public float BareHeight(double x, double z) => Height(x, z);
    public Vector3 BareNormal(double x, double z) => Normal(x, z);

    public int SurfaceId(double x, double z)
    {
        int sx = (int)MathF.Floor(Scale * (float)x + 0.5f), sz = (int)MathF.Floor(Scale * (float)z + 0.5f);
        return tiles.Sample(sx, sz) is { } v ? (v >> 13) & 7 : outside?.SurfaceId ?? 0;
    }

    public Surface Surface(int id) => surfaces[id & 7];

    public bool Impassable(double x, double z)
    {
        int sx = (int)MathF.Floor(Scale * (float)x + 0.5f), sz = (int)MathF.Floor(Scale * (float)z + 0.5f);
        return ((tiles.Sample(sx, sz) ?? 0) & 0x1000) != 0;
    }
}
