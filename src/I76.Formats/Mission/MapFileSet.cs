using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using I76.Formats.Assembly;
using I76.Formats.Vfs;

namespace I76.Formats.Mission;

/// <summary>One file of a custom map: its name (no folder), where it is on this machine, its size and SHA-256.</summary>
public sealed record MapFile(string Name, string Path, long Size, byte[] Sha256);

/// <summary>
/// The files a custom map is made of, as a network host sends them to a client that lacks them (docs/multiplayer.md
/// "Custom maps"): the mission file, its terrain (<see cref="MissionWorld.FindTerrain"/>) and the files its WRLD names
/// that the install has loose (the .npt text, a palette, a horizon, ...), each only if it is not stock data. Stock is
/// what either game ships: a name in the archive (the ZFS and its paks) or one of the stock missions' and addon\'s
/// (<see cref="IsStockName"/>). A map whose mission file is stock is not a custom map and has no set to send.
/// </summary>
public sealed record MapFileSet(string Mission, IReadOnlyList<MapFile> Files, byte[] Hash)
{
    /// <summary>A map bigger than this is not offered (the server's --map-max-mb changes it).</summary>
    public const long DefaultMaxBytes = 16L << 20;
    /// <summary>The most files one map may have (the mission, its terrain and what its WRLD names: about ten at most).</summary>
    public const int MaxFiles = 16;

    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>The hash in hex (lower case): the downloaded-map folder's name.</summary>
    public string HashHex => Convert.ToHexString(Hash).ToLowerInvariant();

    /// <summary>
    /// The extensions a map's file may have on the wire: the seven mission kinds, the terrain, and what a WRLD names (text,
    /// palette, luminance, translucency, scrounge, horizon, textures and tables).
    /// </summary>
    public static readonly string[] Extensions =
        [.. GameInstall.MissionExtensions, ".ter", ".npt", ".act", ".lum", ".tbl", ".sdf", ".hzd", ".map", ".tab"];

    // letters, digits, spaces and the marks a file name may hold on Windows and Linux alike (none of <>:"/\\|?*), up to 64 before
    // the extension; no leading space or dot, no ".." (the community's maps: "destruct .rac", a space before the dot, which
    // both systems keep)
    private static readonly Regex SafeName = new(@"^(?![ .])(?!.*\.\.)[A-Za-z0-9_\-!#$&@^~ ,'()+=\[\]{}%.]{1,64}\.[A-Za-z0-9]{1,4}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>
    /// A file name a map may carry over the network: a plain name (no folder, no "..", no leading dot or space, letters,
    /// digits, spaces and the marks the community's maps use, BZ!.LVL), one of <see cref="Extensions"/>, not a Windows device name.
    /// </summary>
    public static bool IsSafeName(string? name)
    {
        if (name is null || !SafeName.IsMatch(name)) return false;
        string ext = System.IO.Path.GetExtension(name);
        if (!Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        // Windows reserves a device name before any dot: CON.ter and CON.x.ter alike
        return !Devices.Contains(System.IO.Path.GetFileNameWithoutExtension(name)) && !Devices.Contains(name[..name.IndexOf('.')]);
    }

    // the stock installs' miss16 (both games, the GOG releases): a01, m01-m15, s01-s07, t01-t17 in I76; b01, p01-p19 and the
    // network arenas n01-n65 (not every number) in the Nitro Pack; each with its .ter, .msn or arena file and _16.pcf
    private static readonly HashSet<string> StockStems = MakeStockStems();
    // addon\ in the stock installs (the files a map could share a name with: the rest are pictures and cars)
    private static readonly HashSet<string> StockAddon = new(StringComparer.OrdinalIgnoreCase) { "i2ayj_13.map", "fullres.lst", "scenario.dat" };

    private static HashSet<string> MakeStockStems()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a01", "b01" };
        void Range(char c, int from, int to) { for (int i = from; i <= to; i++) s.Add($"{c}{i:00}"); }
        Range('m', 1, 15); Range('s', 1, 7); Range('t', 1, 17); Range('p', 1, 19);
        foreach (int n in (int[])[1, 2, 3, 5, 6, 9, 10]) s.Add($"n{n:00}");
        Range('n', 11, 35); Range('n', 40, 46); Range('n', 51, 65);
        return s;
    }

    /// <summary>
    /// Whether this mission file is one a stock game ships: in miss16 or miss8, I76's a01, m, s and t missions and the Nitro Pack's
    /// b01 and p ones as .msn, the Nitro Pack's n arenas as any mission file (n35 is both a .rac and a .msn). Not what a player added
    /// beside them, even under a stock name: community packs put melee versions of the campaign's maps there (t01.cbt, a01.cbt).
    /// </summary>
    public static bool IsStockMission(string path)
    {
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path));
        if (!"miss16".Equals(folder, StringComparison.OrdinalIgnoreCase) && !"miss8".Equals(folder, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        return StockStems.Contains(stem) && GameInstall.IsMissionFile(path)
            && (stem[0] is 'n' or 'N' || System.IO.Path.GetExtension(path).Equals(".msn", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a file of this name is one the stock games ship loose (a stock mission's, terrain, or addon\'s).</summary>
    public static bool IsStockName(string name)
    {
        if (StockAddon.Contains(name)) return true;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith("_16", StringComparison.Ordinal) || stem.EndsWith("_8", StringComparison.Ordinal)) stem = stem[..stem.LastIndexOf('_')];
        return StockStems.Contains(stem);
    }

    /// <summary>
    /// The custom map <paramref name="arena"/> of this install, or null when it is no custom map (not found, a stock
    /// mission, or its terrain missing: the server says so another way). <paramref name="vfs"/> resolves the names its
    /// WRLD gives (opened on the same install when null).
    /// </summary>
    public static MapFileSet? Resolve(GameInstall install, string arena, AssetVfs? vfs = null)
    {
        if (install.FindMission(arena) is not { } path) return null;
        string mission = System.IO.Path.GetFileName(path);
        if (IsStockName(mission)) return null;
        var data = File.ReadAllBytes(path);
        var msn = new MissionFile(path, data);
        if (MissionWorld.FindTerrain(install, msn) is not { } ter) return null;
        vfs ??= new AssetVfs(install);
        var files = new List<MapFile> { Of(mission, path, data) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mission };
        // only from where maps are put: addon\ and the folders in it, miss16\, the mission's own folder, a downloaded map's
        var dirs = new HashSet<string>(new[] { install.AddonDir, install.MissionDir, System.IO.Path.GetDirectoryName(path), install.MapDir }
            .Concat(install.AddonFolders()).OfType<string>().Select(Full), StringComparer.Ordinal);
        void Add(string name, string at)
        {
            if (!dirs.Contains(Full(System.IO.Path.GetDirectoryName(at)!))) return;
            if (!seen.Add(name) || !IsSafeName(name) || IsStockName(name) || vfs.InArchive(name)) return;
            files.Add(Of(name, at, File.ReadAllBytes(at)));
        }
        Add(System.IO.Path.GetFileName(ter), ter);
        foreach (var name in Referenced(msn))
            if (vfs.Locate(name) is AssetSource.Loose l && !seen.Contains(System.IO.Path.GetFileName(l.Path))) Add(System.IO.Path.GetFileName(l.Path), l.Path);
        return new MapFileSet(mission, files, HashOf(mission, files));
    }

    /// <summary>The names a mission's WRLD gives (with their extension, the .npt's added when it has none), and its own .npt.</summary>
    public static IEnumerable<string> Referenced(MissionFile msn)
    {
        var o = new List<string>();
        if (WorldSettings.Read(msn) is { } w)
        {
            o.AddRange(new[] { w.Palette, w.Luminance, w.Translucency, w.Scrounge, w.Horizon, w.Sky, w.Overview }.Where(n => n.Contains('.')));
            if (w.Text.Length > 0) o.Add(w.Text.Contains('.') ? w.Text : w.Text + ".npt");
        }
        o.AddRange(msn.WdefRefs());
        o.Add(msn.Name + ".npt");
        return o.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Full(string dir) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir));

    private static MapFile Of(string name, string path, byte[] data) => new(name, path, data.LongLength, SHA256.HashData(data));

    /// <summary>
    /// The map's identity: SHA-256 of "I26MAP1", the mission file's name and, in name order (ordinal, case-folded), each
    /// file's name, size and SHA-256, a line each. Two installs with the same files give the same hash whatever folders
    /// hold them.
    /// </summary>
    public static byte[] HashOf(string mission, IEnumerable<(string Name, long Size, byte[] Sha256)> files)
    {
        var sb = new StringBuilder("I26MAP1\n").Append(mission.ToLowerInvariant()).Append('\n');
        foreach (var f in files.OrderBy(f => f.Name.ToLowerInvariant(), StringComparer.Ordinal))
            sb.Append(f.Name.ToLowerInvariant()).Append('\n').Append(f.Size).Append('\n').Append(Convert.ToHexString(f.Sha256).ToLowerInvariant()).Append('\n');
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static byte[] HashOf(string mission, IEnumerable<MapFile> files) => HashOf(mission, files.Select(f => (f.Name, f.Size, f.Sha256)));

    /// <summary>Why this map cannot be sent (too big, too many files), or null.</summary>
    public string? TooBig(long maxBytes) =>
        Files.Count > MaxFiles ? $"{Files.Count} files, more than {MaxFiles}"
        : TotalSize > maxBytes ? $"{TotalSize / 1024} KiB, more than the {maxBytes / 1024} KiB allowed"
        : null;
}
