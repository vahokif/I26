using System.Text.Json;
using I76.Formats.Vfs;

namespace I76.Formats.Installers;

/// <summary>Where GOG's own tools may have put the games: the machine, as plain data (tests build one over a temp dir).</summary>
public sealed class GogEnvironment
{
    /// <summary>A place a Windows drive can be found: a mount, a Wine prefix drive or a CrossOver bottle's drive_c. Letter is null when unknown.</summary>
    public sealed record Root(string Path, char? Letter);

    public bool IsWindows { get; init; }
    /// <summary>Windows: Environment.SpecialFolder.CommonApplicationData (C:\ProgramData).</summary>
    public string? CommonAppData { get; init; }
    /// <summary>Windows: the "path" values GOG's installers wrote under HKLM\SOFTWARE\[WOW6432Node\]GOG.com\Games\id.</summary>
    public Func<string, IEnumerable<string>>? RegistryPaths { get; init; }
    /// <summary>Elsewhere: the drives to look for a Windows installation on.</summary>
    public List<Root> Roots { get; init; } = [];
    /// <summary>Heroic's gog_store/installed.json files.</summary>
    public List<string> HeroicFiles { get; init; } = [];

    static readonly string[] WinFs = ["ntfs", "ntfs3", "fuseblk", "exfat", "vfat", "msdos"];

    /// <summary>Mount points (from /proc/mounts lines) of Windows-style file systems, /boot and /efi left out.</summary>
    public static List<string> WindowsMounts(IEnumerable<string> mountLines)
    {
        var l = new List<string>();
        foreach (var line in mountLines)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3 || !WinFs.Contains(f[2])) continue;
            var mp = f[1].Replace("\\040", " ");
            if (mp == "/boot" || mp.StartsWith("/boot/") || mp.StartsWith("/efi")) continue;
            l.Add(mp);
        }
        return l;
    }

    static IEnumerable<string> SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static GogEnvironment FromSystem()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return new GogEnvironment
            {
                IsWindows = true,
                CommonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                RegistryPaths = WindowsRegistryPaths,
                Roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed)
                    .Select(d => new Root(d.Name, char.ToUpperInvariant(d.Name[0]))).ToList(),
            };
        var env = new GogEnvironment();
        var user = Environment.UserName;
        var seen = new HashSet<string>();
        void Add(string p, char? letter = null) { if (seen.Add(p + "|" + letter)) env.Roots.Add(new Root(p, letter)); }
        // Wine prefixes: dosdevices/<letter>: symlinks give the letter
        var prefixes = new List<string> { Path.Combine(home, ".wine") };
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } wp) prefixes.Insert(0, wp);
        foreach (var p in prefixes)
            foreach (var l in "cdefghijklmnopqrstuvwxyz")
            {
                var link = Path.Combine(p, "dosdevices", l + ":");
                if (Directory.Exists(link)) Add(link, char.ToUpperInvariant(l));
            }
        if (OperatingSystem.IsMacOS())
        {
            foreach (var b in SubDirs(Path.Combine(home, "Library/Application Support/CrossOver/Bottles")))
                Add(Path.Combine(b, "drive_c"), 'C');
            foreach (var v in SubDirs("/Volumes")) Add(v);
            env.HeroicFiles.Add(Path.Combine(home, "Library/Application Support/heroic/gog_store/installed.json"));
        }
        else
        {
            try { foreach (var m in WindowsMounts(File.ReadLines("/proc/mounts"))) Add(m); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var d in new[] { "/mnt", $"/media/{user}", $"/run/media/{user}" })
                foreach (var s in SubDirs(d)) Add(s);
            env.HeroicFiles.Add(Path.Combine(home, ".config/heroic/gog_store/installed.json"));
            env.HeroicFiles.Add(Path.Combine(home, ".var/app/com.heroicgameslauncher.hgl/config/heroic/gog_store/installed.json"));
        }
        return env;
    }

    static IEnumerable<string> WindowsRegistryPaths(string id)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var sub in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\", @"SOFTWARE\GOG.com\Games\" })
        {
            string? p = null;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub + id);
                p = k?.GetValue("path") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            if (!string.IsNullOrEmpty(p)) yield return p;
        }
    }
}

/// <summary>
/// Finds Interstate '76 and the Nitro Pack as GOG put them (GOG Galaxy, or an offline installer), read only. A folder counts
/// when it holds goggame-&lt;id&gt;.info for one of the two product ids AND is a valid install. Sources, in order:
/// the Windows registry (GOG.com\Games\id\path); Galaxy's ProgramData\GOG.com\Galaxy\config.json (installationPaths,
/// libraryPath: library roots with the games one level below; the true list is storage\galaxy-2.0.db's InstalledBaseProducts,
/// which we do not read to avoid an SQLite dependency); off Windows, the same config.json on a mounted Windows drive, a Wine
/// prefix or a CrossOver bottle, its drive-letter paths mapped onto the mounts; Heroic's installed.json.
/// </summary>
public static class GogLibrary
{
    public const string I76Id = "1207661003", NitroId = "1207661023";
    static readonly string[] Ids = [I76Id, NitroId];

    /// <summary>Whether the folder proves itself a GOG game folder for a known product (goggame-id.info), and is an install.</summary>
    static string? Proven(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.EnumerateFiles(dir, "goggame-*.info"))
            {
                var n = Path.GetFileName(f).ToLowerInvariant();
                foreach (var id in Ids)
                    if (n == $"goggame-{id}.info" && InstallSearch.IsInstall(dir)) return id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The proven game folders at <paramref name="root"/> itself and one level below it.</summary>
    static List<(string Dir, string Id)> ScanRoot(string root)
    {
        var l = new List<(string, string)>();
        if (Proven(root) is { } a) l.Add((root, a));
        try
        {
            if (Directory.Exists(root))
                foreach (var d in Directory.EnumerateDirectories(root))
                    if (Proven(d) is { } id) l.Add((d, id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return l;
    }

    /// <summary>Walks <paramref name="parts"/> below <paramref name="root"/> ignoring case (ntfs-3g and Wine may differ from Windows' spelling); null if a part is missing.</summary>
    public static string? ResolveIgnoreCase(string root, IEnumerable<string> parts)
    {
        var cur = root;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == ".") continue;
            var exact = Path.Combine(cur, part);
            if (Directory.Exists(exact) || File.Exists(exact)) { cur = exact; continue; }
            string? hit = null;
            try
            {
                hit = Directory.EnumerateFileSystemEntries(cur)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (hit is null) return null;
            cur = hit;
        }
        return cur;
    }

    static string[] Parts(string s) => s.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>"X:\rest" -> (X, [rest...]); null for anything not drive-lettered.</summary>
    static (char Letter, string[] Tail)? DrivePath(string p)
    {
        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])) return (char.ToUpperInvariant(p[0]), Parts(p[2..]));
        return null;
    }

    public static List<string> Find(GogEnvironment env, Action<string>? log = null)
    {
        var found = new List<string>();
        void Keep(string how, string dir, string id)
        {
            log?.Invoke($"gog: {how} -> {dir} ({id})");
            if (!found.Contains(dir)) found.Add(dir);
        }
        var tried = new HashSet<string>();

        // 1. registry (Windows)
        if (env.IsWindows && env.RegistryPaths is { } reg)
        {
            int n = 0;
            foreach (var id in Ids)
                foreach (var p in reg(id))
                {
                    n++;
                    if (Proven(p) is { } pid) Keep("registry GOG.com\\Games\\" + id + " path " + p, p, pid);
                }
            if (n == 0) log?.Invoke("gog: registry has no GOG.com\\Games entry for either game");
        }

        // 2. Galaxy's config.json
        var configs = new List<(string File, GogEnvironment.Root? On)>();
        if (env.IsWindows)
        {
            if (env.CommonAppData is { } cad) configs.Add((Path.Combine(cad, "GOG.com", "Galaxy", "config.json"), null));
        }
        else
            foreach (var r in env.Roots)
            {
                var c = ResolveIgnoreCase(r.Path, ["ProgramData", "GOG.com", "Galaxy", "config.json"])
                    ?? ResolveIgnoreCase(r.Path, ["drive_c", "ProgramData", "GOG.com", "Galaxy", "config.json"]);
                if (c is not null && File.Exists(c)) configs.Add((c, r));
            }
        if (configs.Count == 0 && !env.IsWindows) log?.Invoke($"gog: no Galaxy config.json on {env.Roots.Count} candidate drives");
        foreach (var (file, on) in configs)
        {
            var paths = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("installationPaths", out var ip) && ip.ValueKind == JsonValueKind.Array)
                    paths.AddRange(ip.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                if (doc.RootElement.TryGetProperty("libraryPath", out var lp) && lp.ValueKind == JsonValueKind.String)
                    paths.Add(lp.GetString()!);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"gog: config.json {file}: {ex.Message}");
                continue;
            }
            var where = on is null ? "config.json" : $"config.json on {on.Path}";
            foreach (var gp in paths.Distinct())
            {
                if (!tried.Add(gp)) continue;
                var hits = MapAndScan(env, gp, on);
                if (hits.Count == 0) log?.Invoke($"gog: {where} lists {gp} -> nothing there");
                // GOG's own folder names first ("Interstate 76", "Interstate 76 Nitro Pack"): a copy of the game beside it in the same
                // library (a backup carrying the same goggame .info) is offered after the install Galaxy made
                foreach (var (d, id) in hits.OrderBy(h => GogFolderNames.Contains(Path.GetFileName(h.Dir.TrimEnd('/', '\\')), StringComparer.OrdinalIgnoreCase) ? 0 : 1))
                    Keep($"{where} lists {gp}", d, id);
            }
        }

        // 3. Heroic
        foreach (var hf in env.HeroicFiles)
        {
            if (!File.Exists(hf)) continue;
            int before = found.Count;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(hf));
                var entries = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("installed", out var arr) ? arr : doc.RootElement;
                foreach (var e in entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().ToList() : [])
                {
                    string? app = e.TryGetProperty("appName", out var a) ? a.GetString() : e.TryGetProperty("gameId", out var g) ? g.GetString() : null;
                    if (app is null || !Ids.Contains(app) || !e.TryGetProperty("install_path", out var ipath) || ipath.GetString() is not { } path) continue;
                    foreach (var (d, id) in ScanRoot(path)) Keep($"Heroic {hf} install_path {path}", d, id);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { log?.Invoke($"gog: {hf}: {ex.Message}"); }
            if (found.Count == before) log?.Invoke($"gog: Heroic {hf} has neither game");
        }
        return found;
    }

    static readonly string[] GogFolderNames = ["Interstate 76", "Interstate 76 Nitro Pack"];

    /// <summary>One Galaxy path: on Windows as is; elsewhere "X:\rest" under the roots (the one naming letter X first), kept only where the .info proves it.</summary>
    static List<(string Dir, string Id)> MapAndScan(GogEnvironment env, string galaxyPath, GogEnvironment.Root? configOn)
    {
        if (env.IsWindows) return ScanRoot(galaxyPath);
        if (DrivePath(galaxyPath) is not { } dp) return ScanRoot(galaxyPath);
        var roots = env.Roots.OrderBy(r => r.Letter == dp.Letter ? 0 : r.Letter is null ? 1 : 2).ToList();
        // a root known to be another letter is not tried: the config's own drive (C:) when it is the root the config was read from
        foreach (var r in roots)
        {
            if (r.Letter is { } l && l != dp.Letter) continue;
            var dir = ResolveIgnoreCase(r.Path, dp.Tail);
            if (dir is null) continue;
            var hits = ScanRoot(dir);
            if (hits.Count > 0) return hits;
        }
        return [];
    }

    private static readonly object Gate = new();
    private static List<string>? cached;

    /// <summary>The system's GOG games, searched once per run and given at most <paramref name="timeoutMs"/> (a sleeping drive must not hold the start up).</summary>
    public static List<string> FindOnSystem(Action<string> log, int timeoutMs = 4000)
    {
        lock (Gate)
        {
            if (cached is not null) return cached;
            var lines = new List<string>();
            var task = Task.Run(() =>
            {
                var r = Find(GogEnvironment.FromSystem(), s => { lock (lines) lines.Add(s); });
                return r;
            });
            if (task.Wait(timeoutMs)) cached = task.Result;
            else { lock (lines) lines.Add($"gog: gave up after {timeoutMs} ms"); cached = []; }
            lock (lines) foreach (var s in lines) log(s);
            return cached;
        }
    }
}
