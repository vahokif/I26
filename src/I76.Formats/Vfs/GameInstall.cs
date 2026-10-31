namespace I76.Formats.Vfs;

public enum GameProfile { I76, Nitro }

/// <summary>
/// A game install on disk, opened read-only. Lookups are case-insensitive,
/// since installs copied from Windows keep whatever case the installer used.
/// </summary>
public sealed class GameInstall
{
    public string Root { get; }
    public GameProfile Profile { get; }
    private readonly Dictionary<string, string> _top;

    public GameInstall(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        _top = Listing(Root);
        if (FindFile("I76.ZFS") is null && FindFile("nitro.zfs") is null)
            throw new FileNotFoundException($"{Root}: no I76.ZFS or nitro.zfs; is this an Interstate '76 install?");
        // Nitro is a standalone install: nitro.zfs (not I76.ZFS), nitshell.dll for i76shell.dll, database.tvf for DATABASE.MW2.
        Profile = FindFile("I76.ZFS") is null && (FindFile("nitro.zfs") is not null || FindFile("nitshell.dll") is not null) ? GameProfile.Nitro
            : FindFile("database.tvf") is not null && FindFile("DATABASE.MW2") is null ? GameProfile.Nitro : GameProfile.I76;
    }

    /// <summary>
    /// The driver variant: 1 Groove, 2 Taurus, 3 Jade, 4 Skeeter, 5 Natty Dread. The GOG base game
    /// passes NULL and so is always 1; Nitro picks it from the mission name (t* 1, n* 2, P01-04/17/19 2, P05-08/18 3, B01 3, P09-12 4, P13-16 5).
    /// </summary>
    public static int DriverVariant(string? mission, bool nitro)
    {
        if (!nitro || string.IsNullOrEmpty(mission)) return 1;
        char c = char.ToLowerInvariant(mission[0]);
        if (c == 't') return 1;
        if (c == 'n') return 2;
        int n = 0, i = 1;
        while (i < mission.Length && char.IsAsciiDigit(mission[i]) && i < 3) n = n * 10 + (mission[i++] - '0');
        if (i == 1) return 1;
        if (c == 'b') return n == 1 ? 3 : 1;
        if (c != 'p') return 1;
        return n switch { >= 1 and <= 4 or 17 or 19 => 2, >= 5 and <= 8 or 18 => 3, >= 9 and <= 12 => 4, >= 13 and <= 16 => 5, _ => 1 };
    }

    /// <summary>
    /// The port's: the folder of a map downloaded from a network game's host (user://downloaded_maps/&lt;hash&gt;,
    /// docs/multiplayer.md "Custom maps"), searched before everything else for missions and terrains, and above addon\ in
    /// <see cref="AssetVfs"/>. Process-wide for the game, set for one network session and cleared after it; every install
    /// opened meanwhile takes it (<see cref="MapDir"/>). Never a folder of the install itself.
    /// </summary>
    public static string? SessionMapDir { get; set; }

    /// <summary>This install's downloaded-map folder (<see cref="SessionMapDir"/> when it was opened, unless set); null: none.</summary>
    public string? MapDir { get; init; } = SessionMapDir is { } d && Directory.Exists(d) ? d : null;

    /// <summary>A file in <see cref="MapDir"/> by case-insensitive name, or null.</summary>
    public string? InMapDir(string name) =>
        MapDir is { } d && Directory.Exists(d) ? Directory.EnumerateFiles(d).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>The Nitro Pack (its own shell, data and campaign) rather than Interstate '76.</summary>
    public bool IsNitro => Profile == GameProfile.Nitro;

    /// <summary>
    /// The mission to start when none is asked for: base's a01 (the training ground); on Nitro the campaign's first, the first
    /// mission of the first character in addon\SCENARIO.DAT (p01 here), else p01, else the first .msn in miss16.
    /// </summary>
    public string DefaultMission()
    {
        if (!IsNitro) return "a01";
        if (Resolve("addon", "SCENARIO.DAT") is { } sc)
            foreach (var line in File.ReadLines(sc))
            {
                int k = line.IndexOf(".msn", StringComparison.OrdinalIgnoreCase);
                if (k < 0) continue;
                var name = line[..k].Split(',', '\t').Last().Trim();
                if (name.Length > 0 && FindMission(name) is not null) return name;
            }
        if (FindMission("p01") is not null) return "p01";
        var dir = Resolve("miss16");
        var first = dir is null ? null : Directory.EnumerateFiles(dir, "*.msn").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return first is null ? "p01" : Path.GetFileNameWithoutExtension(first);
    }

    private static Dictionary<string, string> Listing(string dir)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Directory.EnumerateFileSystemEntries(dir))
            d.TryAdd(Path.GetFileName(p), p);
        return d;
    }

    /// <summary>A file or directory at the install root, by case-insensitive name.</summary>
    public string? FindFile(string name) => _top.GetValueOrDefault(name);

    public string? FindDir(string name) => FindFile(name) is { } p && Directory.Exists(p) ? p : null;

    /// <summary>Case-insensitive path below the root, one component at a time.</summary>
    public string? Resolve(params string[] parts)
    {
        string cur = Root;
        foreach (var part in parts)
        {
            var hit = Directory.EnumerateFileSystemEntries(cur)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), part, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return null;
            cur = hit;
        }
        return cur;
    }

    /// <summary>
    /// Mission file extensions: .msn (campaign and base melee), Nitro's melee kinds (.cbt, .cf2/.cf3/.cf4, .rac), and the
    /// community's .lvl levels (base I76's addon\*.lvl arenas; both engines' BWD revision table accepts all seven,
    /// the base game).
    /// </summary>
    public static readonly string[] MissionExtensions = [".msn", ".cbt", ".cf2", ".cf3", ".cf4", ".rac", ".lvl"];

    public static bool IsMissionFile(string path) =>
        MissionExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A mission by name with or without its extension (a name without one tries each kind), as the engine's file system
    /// finds it: addon\ first, then miss16\ (measured under Wine: the Nitro Pack opens addon\elim2.cbt over miss16\elim2.cbt);
    /// then, the port's, a folder one level inside addon\ (a map zip unpacked into a
    /// folder of its own there, which the original never looks in).
    /// </summary>
    public string? FindMission(string name)
    {
        string[] names = IsMissionFile(name) ? [name] : [.. MissionExtensions.Select(e => name + e)];
        foreach (var n in names)
            if (InMapDir(n) is { } dl) return dl;
        foreach (var n in names)
            if ((Resolve("addon", n) ?? Resolve("miss16", n)) is { } p) return p;
        foreach (var n in names)
            if (InAddonFolders(n) is { } p) return p;
        return null;
    }

    /// <summary>
    /// The .ter a mission names (<see cref="Mission.MissionFile.TerrainName"/>), where the ZONE handler opens it
    /// (the Nitro Pack): addon\NAME if it exists, else miss16\NAME. Null if neither has it. A downloaded map's
    /// folder (<see cref="MapDir"/>) comes first.
    /// </summary>
    public string? FindTerrain(string name) => InMapDir(name) ?? Resolve("addon", name) ?? Resolve("miss16", name);

    /// <summary>The folders directly inside addon\, in name order (where a map zip unpacked into its own folder lands).</summary>
    public IEnumerable<string> AddonFolders() =>
        AddonDir is { } a ? Directory.EnumerateDirectories(a).OrderBy(d => Path.GetFileName(d).ToUpperInvariant(), StringComparer.Ordinal) : [];

    private string? InAddonFolders(string name)
    {
        foreach (var d in AddonFolders())
            if (Directory.EnumerateFiles(d).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return null;
    }

    /// <summary>The install's addon\ folder (any case), or null.</summary>
    public string? AddonDir => FindDir("addon");

    /// <summary>
    /// The mission files of one extension the shell lists, in its order: the mission directory's (*.cbt), then addon\'s
    /// (addon\*.cbt), each in file-name order (FindFirstFile on NTFS;
    /// addon\*.lvl); then the port's addition, the folders one level inside addon\. A name already listed is not listed
    /// again: the engine loads the first one <see cref="FindMission"/> finds either way.
    /// </summary>
    public List<string> ListMissions(string ext, bool missionDir = true, bool addon = true)
    {
        var o = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string?> dirs = [missionDir ? MissionDir : null, addon ? AddonDir : null, .. addon ? AddonFolders() : []];
        foreach (var d in dirs)
        {
            if (d is null || !Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d).Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => Path.GetFileName(f).ToUpperInvariant(), StringComparer.Ordinal))
                if (seen.Add(Path.GetFileName(f))) o.Add(f);
        }
        return o;
    }

    /// <summary>The 16-bit-colour mission directory, which the game plays from.</summary>
    public string? MissionDir => FindDir("miss16");

    public string ZfsPath => (FindFile("I76.ZFS") ?? FindFile("nitro.zfs"))!;

    /// <summary>The I76_INSTALL environment variable, if it names an install.</summary>
    public static GameInstall? FromEnvironment()
    {
        var p = Environment.GetEnvironmentVariable("I76_INSTALL");
        return string.IsNullOrEmpty(p) || !Directory.Exists(p) ? null : new GameInstall(p);
    }
}
