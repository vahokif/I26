using System.Numerics;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Formats.Assembly;

public sealed record Placement(PlacedObject Object, string? Definition);

/// <summary>
/// Everything a mission puts in the world, resolved without any engine:
/// terrain, palette, placements mapped to definitions, the WDEF scenery.
/// </summary>
public sealed class MissionWorld
{
    public const double SampleScale = 0.2;    // world -> sample (one sample is 5 units)
    public const double SampleBias = 0.5;     // the engine rounds, floor(x + 0.5)
    /// <summary>
    /// Raw height to world units: raw * (0.1 / hdiv) / scale, measured at
    /// runtime as raw * 0.1. Whether hdiv
    /// varies per mission is RE gate R2.
    /// </summary>
    public const double HeightScale = 0.1;

    public MissionFile Mission { get; }
    public TerrainTiles Terrain { get; }
    /// <summary>The .ter file the terrain came from.</summary>
    public string TerrainPath { get; }
    public string? PaletteName { get; private set; }
    public List<Placement> Placements { get; }
    public List<string> WdefRefs { get; private set; }
    /// <summary>LDEF polylines, one placement per link (Definition resolved like ODEF names).</summary>
    public List<Placement> LinePlacements { get; } = [];

    private readonly AssetVfs _vfs;

    /// <summary>
    /// The mission's palette file. Creeper Canyon (M08 and Nitro's N58) names "t013_16.ACT", which no archive holds (a typo for
    /// t03_16.ACT: its .lum and .tbl are t03's): the cockpit's 8-bit art then had no palette and drew white. A name the game does not
    /// have becomes the 16-bit (then the 8-bit) palette of the stem its .lum names, else stays as written.
    /// </summary>
    public static string? ResolvePalette(AssetVfs vfs, MissionFile mission)
    {
        var name = mission.Palette();
        if (name is null || vfs.Exists(name)) return name;
        var lum = WorldSettings.Read(mission)?.Luminance;
        if (string.IsNullOrEmpty(lum)) return name;
        var stem = Path.GetFileNameWithoutExtension(lum);
        foreach (var alt in new[] { stem + "_16.act", stem + ".act" })
            if (vfs.Exists(alt)) return alt;
        return name;
    }

    public MissionWorld(AssetVfs vfs, ModelAssembler asm, string msnPath)
    {
        Mission = new MissionFile(msnPath);
        TerrainPath = FindTerrain(vfs.Install, Mission)
            ?? throw new FileNotFoundException($"{Path.GetFileName(msnPath)} needs its terrain {Mission.TerrainName() ?? Mission.Name + ".ter"} in the game's miss16 or addon folder.");
        Terrain = TerrainTiles.Load(Mission, TerrainPath);
        _vfs = vfs;
        PaletteName = ResolvePalette(vfs, Mission);
        WdefRefs = Mission.WdefRefs();
        Placements = Mission.PlacedObjects()
            .Select(o => new Placement(o, o.IsKeyword || o.Name.Length == 0 ? null : asm.ResolveDefinition(o.Name)))
            .ToList();
        foreach (var line in LineObjects.Read(Mission))
        {
            var def = asm.ResolveDefinition(line.Name);
            foreach (var (rot, pos) in LineObjects.Placements(line, BilinearHeight))
                LinePlacements.Add(new Placement(new PlacedObject(line.Name, 0, rot, pos, []), def));
        }
    }

    private Lazy<OutsideGround?>? _outside;

    /// <summary>
    /// The ground past the map's tiles (<see cref="OutsideGround"/>): made on first use, then the same object for the simulation and the
    /// drawing; null for a map too big for it. <see cref="ForgetOutside"/> after the terrain changed (the map editor).
    /// </summary>
    public OutsideGround? Outside
    {
        get
        {
            if (_outside is null) Interlocked.CompareExchange(ref _outside, new Lazy<OutsideGround?>(() => OutsideGround.For(_vfs, this)), null);
            return _outside!.Value;
        }
    }

    /// <summary>The terrain was edited: <see cref="Outside"/> is made again from it when next asked for.</summary>
    public void ForgetOutside() => _outside = null;

    /// <summary>The names WDEF holds, read again: after <see cref="WorldSettings.Patch"/> changed the mission's look in memory (the map editor).</summary>
    public void RefreshLook()
    {
        PaletteName = ResolvePalette(_vfs, Mission);
        WdefRefs = Mission.WdefRefs();
    }

    /// <summary>
    /// The .ter a mission plays on: the name TDEF/ZONE gives, looked for in addon\ and then miss16\ (the ZONE handler,
    /// the Nitro Pack). When that file is in neither, the mission's own stem (.ter, beside it, then addon\ and
    /// miss16\): a deviation for the community pack's levels whose ZONE names a file their authors never shipped
    /// (h2h.rac names h2h16.ter, fmspeedcbt.cbt a 13-character "fmspeedcbt.te"), which the original fails to load
    /// (docs/deviations.md).
    /// </summary>
    public static string? FindTerrain(GameInstall install, MissionFile msn)
    {
        string? Beside(string name) => Directory.EnumerateFiles(Path.GetDirectoryName(msn.Path)!)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        // a map in a folder inside addon\ (the port's, GameInstall.FindMission) has its terrain beside it
        if (msn.TerrainName() is { } named && (install.FindTerrain(named) ?? Beside(named)) is { } hit) return hit;
        var stem = msn.Name + ".ter";
        return Beside(stem) ?? install.FindTerrain(stem);
    }

    /// <summary>
    /// Where a placed object stands: its file position with the height set to the terrain's bilinear height at its x, z.
    /// The loader (the original's scene loader, called with its ground flag for every ODEF record) overwrites
    /// the node's y with the bilinear terrain height at x and z, so a pole authored at y=100 stands on the ground. The sim's colliders
    /// and the drawing both use it.
    /// </summary>
    public Vector3 GroundedPosition(Placement p) => p.Object.Position with { Y = (float)BilinearHeight(p.Object.Position.X, p.Object.Position.Z) };

    public static int ToSample(double world) => (int)Math.Floor(world * SampleScale + SampleBias);

    public int RawHeightAtSample(int sx, int sz) => (Terrain.Sample(sx, sz) ?? 0) & MissionFile.HeightMask;

    /// <summary>The engine's ground height: the nearest sample, no interpolation (terramesh_height).</summary>
    public double GroundHeight(double x, double z) => RawHeightAtSample(ToSample(x), ToSample(z)) * HeightScale;

    /// <summary>The mean ground height over every sample of the map's occupied tiles (each tile counted once per grid cell using it).</summary>
    public double AverageGroundHeight()
    {
        var (r0, r1, c0, c1) = Terrain.Bounds();
        double sum = 0; long n = 0;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                byte t = Terrain.TileAt(r, c);
                if (t == MissionFile.ZmapEmpty || t >= Terrain.TileCount) continue;
                foreach (var s in Terrain.TileSamples(t)) sum += s & MissionFile.HeightMask;
                n += MissionFile.TerrainTile * MissionFile.TerrainTile;
            }
        return n == 0 ? 0 : sum / n * HeightScale;
    }

    /// <summary>The lowest ground sample of the map's occupied tiles, in metres (0 with no ground).</summary>
    public double LowestGroundHeight()
    {
        var (r0, r1, c0, c1) = Terrain.Bounds();
        int low = int.MaxValue;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                byte t = Terrain.TileAt(r, c);
                if (t == MissionFile.ZmapEmpty || t >= Terrain.TileCount) continue;
                foreach (var s in Terrain.TileSamples(t)) low = Math.Min(low, s & MissionFile.HeightMask);
            }
        return low == int.MaxValue ? 0 : low * HeightScale;
    }

    /// <summary>
    /// The bilinear terrain height. floor() of the sample
    /// coordinate, four raw samples (0 outside the terrain), lerp in x then z,
    /// times 0.1. 0 anywhere outside 0..10240 samples. Roads stand on this.
    /// </summary>
    public double BilinearHeight(double x, double z)
    {
        double fx = SampleScale * x, fz = SampleScale * z;
        if (fx + SampleBias < 0 || fz + SampleBias < 0 || fx + SampleBias > 0x2800 || fz + SampleBias > 0x2800) return 0;
        int sx = (int)Math.Floor(fx), sz = (int)Math.Floor(fz);
        double S(int a, int b) => (Terrain.Sample(a, b) ?? 0) & MissionFile.HeightMask;
        double tx = fx - sx, tz = fz - sz;
        double a0 = S(sx, sz) + (S(sx + 1, sz) - S(sx, sz)) * tx;
        double a1 = S(sx, sz + 1) + (S(sx + 1, sz + 1) - S(sx, sz + 1)) * tx;
        return (a0 + (a1 - a0) * tz) * HeightScale;
    }

    /// <summary>Centre of the occupied terrain, in world units (game frame).</summary>
    public Vector3 Centre()
    {
        var (r0, r1, c0, c1) = Terrain.Bounds();
        float cx = (c0 + c1 + 1) * 0.5f * TerrainTiles.Tile / (float)SampleScale;
        float cz = (r0 + r1 + 1) * 0.5f * TerrainTiles.Tile / (float)SampleScale;
        return new Vector3(cx, (float)GroundHeight(cx, cz), cz);
    }

    /// <summary>
    /// The original's per-vertex light (docs/engine/terrain-lighting.md), on
    /// sample coordinates. The normal is the unnormalised (-dhdx, 1, -dhdz);
    /// back-facing ground returns ambient; the final value is clamped to 1.
    /// </summary>
    public static double FaithfulLight(Func<int, int, double> sample, int x, int z, double viewZ,
        Vector3 light, double ambient, double fadeA, double fadeB, double hdiv = 5.0)
    {
        double k = 0.1 / hdiv;
        double dhdx = (sample(x + 1, z) - sample(x - 1, z)) * k / 2;
        double dhdz = (sample(x, z + 1) - sample(x, z - 1)) * k / 2;
        double dot = light.Y - dhdx * light.X - dhdz * light.Z;
        if (dot < 0) return ambient;
        double fade = Math.Clamp(viewZ * fadeA + fadeB, 0.75, 1.0);
        return Math.Min(dot * fade + ambient, 1.0);
    }
}
