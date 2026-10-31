using System.Numerics;
using System.Text.RegularExpressions;
using I76.Formats.Assembly;
using I76.Formats.Vfs;

namespace I76.Formats.Mission.Edit;

/// <summary>
/// A map as an editor holds it: the mission file (<see cref="MapDocument"/>) and its .ter (<see cref="TerrainTiles"/>), saved
/// back in the original formats as a set. Open a stock or community map, or make a new one (<see cref="CreateBlank"/>,
/// <see cref="CloneAs"/>), edit, and <see cref="SaveTo"/> a folder (the install's addon\ for the games to find it:
/// docs/map-format-writing.md says what is written and what is kept.
/// </summary>
public sealed class MapProject
{
    /// <summary>The longest map stem: the games build names such as "stem_16.pcf" in 8.3-minded buffers.</summary>
    public const int MaxStem = 8;
    private static readonly Regex StemRx = new($"^[A-Za-z0-9_]{{1,{MaxStem}}}$", RegexOptions.CultureInvariant);

    /// <summary>The mission file's stem (no extension): what the games call the map.</summary>
    public string Name { get; private set; }
    /// <summary>The mission file's extension with the dot: .cbt (melee), .rac (race), .cf2 .cf3 .cf4 (CTF with 2-4 teams), .msn, .lvl.</summary>
    public string Extension { get; private set; }
    public MapDocument Mission { get; }
    public TerrainTiles Terrain { get; }

    public string MissionFileName => Name + Extension;
    /// <summary>The terrain file's name as the mission gives it (TDEF/ZONE), or the stem + .ter if it names none.</summary>
    public string TerrainFileName => Mission.TerrainName ?? Name + ".ter";

    public MapProject(string name, string extension, MapDocument mission, TerrainTiles terrain)
    {
        Name = name; Extension = extension; Mission = mission; Terrain = terrain;
    }

    public static bool IsValidStem(string name) => StemRx.IsMatch(name);

    // ---- opening ----

    /// <summary>Open a mission file with the terrain its ZONE names (found beside it, then in the install's addon\ and miss16\).</summary>
    public static MapProject Load(string missionPath, GameInstall? install = null, string? terrainPath = null)
    {
        var data = File.ReadAllBytes(missionPath);
        var doc = MapDocument.Parse(data);
        if (terrainPath is null)
        {
            var msn = new MissionFile(missionPath, data);
            terrainPath = install is not null ? MissionWorld.FindTerrain(install, msn) : null;
            if (terrainPath is null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(missionPath))!;
                foreach (var n in new[] { doc.TerrainName, msn.Name + ".ter" }.OfType<string>())
                    if (Directory.EnumerateFiles(dir).FirstOrDefault(p => string.Equals(Path.GetFileName(p), n, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    { terrainPath = hit; break; }
            }
        }
        if (terrainPath is null) throw new FileNotFoundException($"the terrain of {Path.GetFileName(missionPath)} ({doc.TerrainName}) was not found");
        return Open(Path.GetFileNameWithoutExtension(missionPath), Path.GetExtension(missionPath), doc, File.ReadAllBytes(terrainPath));
    }

    /// <summary>Open the install's map <paramref name="arena"/> (a stem or file name, as <see cref="GameInstall.FindMission"/> takes).</summary>
    public static MapProject Load(GameInstall install, string arena) =>
        Load(install.FindMission(arena) ?? throw new FileNotFoundException($"no map '{arena}' in the install"), install);

    public static MapProject Open(string name, string extension, MapDocument mission, byte[] ter) =>
        new(name, extension, mission, new TerrainTiles(ter, mission.TileCount, mission.Zmap));

    // ---- saving ----

    /// <summary>The files of the set, by name: the mission and the terrain.</summary>
    public IReadOnlyDictionary<string, byte[]> ToFiles()
    {
        Mission.TileCount = (byte)Terrain.TileCount;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [TerrainFileName] = Terrain.ToBytes(),
            [MissionFileName] = Mission.ToBytes(),
        };
        return files;
    }

    /// <summary>Write the set into a folder (created if needed). Returns the paths written. Never touches other files.</summary>
    public IReadOnlyList<string> SaveTo(string dir)
    {
        if (!IsValidStem(Name)) throw new InvalidOperationException($"map name '{Name}': 1-{MaxStem} letters, digits and underscores");
        Directory.CreateDirectory(dir);
        var o = new List<string>();
        foreach (var (name, bytes) in ToFiles())
        {
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            o.Add(p);
        }
        return o;
    }

    /// <summary>Write the set into the install's addon\ folder, which both games read before miss16.</summary>
    public IReadOnlyList<string> SaveToAddon(GameInstall install) =>
        SaveTo(install.AddonDir ?? throw new DirectoryNotFoundException("the install has no addon folder"));

    // ---- new maps ----

    /// <summary>
    /// A copy of this map under another name: its mission, terrain and all, with the ZONE renamed to
    /// <c>newName.ter</c>, the listed (arena) name set to <paramref name="displayName"/> (the old one when null) and the
    /// extension kept unless <paramref name="extension"/> says otherwise. The source is not changed.
    /// </summary>
    public MapProject CloneAs(string newName, string? displayName = null, string? extension = null)
    {
        if (!IsValidStem(newName)) throw new ArgumentException($"map name '{newName}': 1-{MaxStem} letters, digits and underscores");
        var ext = (extension ?? Extension).ToLowerInvariant();
        if (!GameInstall.MissionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"'{ext}' is not a mission extension");
        var files = ToFiles();
        var doc = MapDocument.Parse(files[MissionFileName]);
        if (doc.TerrainName is not null) doc.TerrainName = newName + ".ter";
        if (displayName is not null) doc.ArenaName = displayName;
        return Open(newName, ext, doc, files[TerrainFileName]);
    }

    // The WRLD of a stock Nitro melee arena (n05) with its name blank: sky, palette, light table and the stock file names
    // (p18_16.act, m01.npt, p18_16.hzd...), all of which the archive holds.
    private const string DefaultWrld =
        "0a00000000000000000000000000000000000000000000000000000000007031385f31362e6163740000007031382e4c554d0000000000007031382e54424c0000000000006d30312e6e70740000000000006e6b5f36636c64622e6d6170006e677363726e67322e5344460074703138317377362e6d6170007a6d6170366d30302e6d6170007031385f31362e485a44000000120000003333f33f8fc2f53c0000000000000000000000000000c03fcdcccc3d000000000000000003000000666606400ad7233c0000000000000000000000003333f33f8fc2f53c0000000000000000000000000000c03f0ad7a33d0000000000000000000000000000c03fcdcc4c3e000000000000000000000000666606400ad7233c0000000000000000000000003333f33f8fc2f53c0000000000000000000000005802000000000000000000000000000000000000";

    /// <summary>
    /// A new map from scratch: flat terrain of <paramref name="tilesWide"/> x <paramref name="tilesHigh"/> tiles (128 x 128
    /// samples, 640 m a side) at grid cell (<paramref name="row"/>, <paramref name="col"/>) (the stock arenas sit near row 76,
    /// column 4), every sample at <paramref name="height"/> (0-4095, tenths of a metre), and an empty mission: no objects,
    /// roads, lines or script. Add spawns (<see cref="MapObject.Spawn"/>) before playing it.
    /// </summary>
    public static MapProject CreateBlank(string name, string displayName, string extension = ".cbt", int tilesWide = 2, int tilesHigh = 2,
        int height = 100, int row = 72, int col = 4)
    {
        if (!IsValidStem(name)) throw new ArgumentException($"map name '{name}': 1-{MaxStem} letters, digits and underscores");
        if (!GameInstall.MissionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"'{extension}' is not a mission extension");
        if (tilesWide < 1 || tilesHigh < 1 || tilesWide * tilesHigh > 254 || col < 0 || row < 0 || col + tilesWide > MissionFile.ZmapGrid || row + tilesHigh > MissionFile.ZmapGrid)
            throw new ArgumentException("tiles do not fit the 80x80 grid");
        var wrld = Convert.FromHexString(DefaultWrld);
        if (wrld.Length != 331) throw new InvalidOperationException("default WRLD");
        static BwdNode Rev(string tag, uint v) => new(tag, BitConverter.GetBytes(v));
        static BwdNode Exit() => new("EXIT", Array.Empty<byte>());
        static BwdNode Box(string tag, params BwdNode[] kids) => new(tag, kids.ToList());
        var zone = new byte[14]; zone[0] = 0xff;
        var zmap = new byte[1 + MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(zmap, MissionFile.ZmapEmpty, 1, MissionFile.ZmapGrid * MissionFile.ZmapGrid);
        var tree = new BwdTree(
        [
            new("BWD2", Array.Empty<byte>()), Rev("REV\0", 2),
            Box("WDEF", Rev("WREV", 9), new("WRLD", wrld), Exit()),
            Box("TDEF", Rev("TREV", 4), new("ZMAP", zmap), new("ZONE", zone), Exit(), Exit()),
            Box("RDEF", Rev("RREV", 1), Exit(), Exit(), Exit(), Exit()),
            Box("ODEF", Rev("OREV", 3), Exit()),
            Box("LDEF", Rev("LREV", 1), Exit()),
            Box("ADEF", Rev("AREV", 1), new("FSM\0", Array.Empty<byte>()), Exit()),
            Exit(),
        ]);
        var doc = new MapDocument(tree);
        doc.TerrainName = name + ".ter";
        doc.ArenaName = displayName;
        int id = 0;
        for (int r = 0; r < tilesHigh; r++)
            for (int c = 0; c < tilesWide; c++) doc.Zmap[(row + r) * MissionFile.ZmapGrid + col + c] = (byte)id++;
        doc.TileCount = (byte)id;
        var ter = new byte[id * MissionFile.TerrainTileBytes];
        ushort h = (ushort)Math.Clamp(height, 0, MissionFile.HeightMask);
        for (int i = 0; i < ter.Length / 2; i++) BitConverter.TryWriteBytes(ter.AsSpan(i * 2), h);
        // round-trip through bytes so the document is the file it will be saved as
        return Open(name, extension, MapDocument.Parse(doc.ToBytes()), ter);
    }

    // ---- conveniences for the editor ----

    /// <summary>World X/Z to the global terrain sample that covers it (x/5 on both axes).</summary>
    public static int SampleOf(double world) => MissionWorld.ToSample(world);

    /// <summary>Terrain height in metres at a world position (the nearest sample, as the engine reads it); null off the map.</summary>
    public double? HeightAt(double x, double z) => Terrain.Height(SampleOf(x), SampleOf(z)) is { } h ? h * MissionWorld.HeightScale : null;

    /// <summary>The world rectangle of the occupied tiles: (minX, minZ, maxX, maxZ).</summary>
    public Vector4 WorldBounds()
    {
        var (r0, r1, c0, c1) = Terrain.Bounds();
        float s = MissionFile.TerrainTile * MissionFile.WorldScale;
        return new Vector4(c0 * s, r0 * s, (c1 + 1) * s, (r1 + 1) * s);
    }
}
