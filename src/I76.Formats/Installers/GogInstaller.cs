using System.Globalization;

namespace I76.Formats.Installers;

public enum GogGame { I76, Nitro }

/// <summary>A file the installer puts under {app}: its path there, its data, and why it is left out (null: extracted).</summary>
public sealed record GogEntry(string RelativePath, InnoData Data, string? Skip)
{
    public bool Wanted => Skip is null;
}

/// <summary>What an extraction wrote.</summary>
public sealed record GogExtractResult(string Folder, int Files, long Bytes, TimeSpan Elapsed);

/// <summary>
/// A GOG offline installer of Interstate '76 or its Nitro Pack (Inno Setup, the game under {app}), and the game data
/// I26 reads out of it: the archives, missions, movies, music, force-feedback effects and the loose .map/.def/.pcx/...
/// files; not the executables, DLLs (but the shells', whose tables are read), GOG Galaxy files or the old launchers.
/// Extracted, the folder is an ordinary install for <see cref="Vfs.GameInstall"/>.
/// </summary>
public sealed class GogInstaller
{
    public InnoSetup Setup { get; }
    public GogGame Game { get; }
    public IReadOnlyList<GogEntry> Entries { get; }
    public IEnumerable<GogEntry> Wanted => Entries.Where(e => e.Wanted);
    public long WantedBytes => Wanted.Sum(e => (long)e.Data.FileSize);
    public string Title => Game == GogGame.Nitro ? "Interstate '76 Nitro Pack" : "Interstate '76";

    /// <summary>The marker an extraction leaves in its folder, naming the installer it came from.</summary>
    public const string MarkerName = "i26-installer.txt";

    /// <summary>The default folder under the user data folder: game/ for Interstate '76, game-nitro/ for the Nitro Pack.</summary>
    public static string DefaultFolderName(GogGame g) => g == GogGame.Nitro ? "nitro" : "i76";

    /// <summary>The folder made inside a chosen folder that already holds other things.</summary>
    public static string SubfolderName(GogGame g) => g == GogGame.Nitro ? "Interstate76-Nitro" : "Interstate76";

    /// <summary>Where to extract when the player picks <paramref name="chosen"/>: there if empty or a previous extraction, else a subfolder.</summary>
    public static string TargetIn(string chosen, GogGame g)
    {
        if (!Directory.Exists(chosen) || File.Exists(Path.Combine(chosen, MarkerName)) || !Directory.EnumerateFileSystemEntries(chosen).Any()) return chosen;
        return Path.Combine(chosen, SubfolderName(g));
    }

    private GogInstaller(InnoSetup setup, GogGame game, List<GogEntry> entries) { Setup = setup; Game = game; Entries = entries; }

    // folders of the GOG layout holding only the old launchers, network DLLs, the level editor or GOG's support files
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "builder", "dll", "splash", "sp256", "__support", "__redist", "redist", "directx", "dosbox", "commonappdata", "__installer" };
    private static readonly HashSet<string> SkipExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".ovl", ".htm", ".html", ".ico", ".lnk", ".url", ".pdf", ".hashdb", ".info", ".inf", ".zip", ".doc", ".msi", ".cab" };
    // the executables and DLLs I26 reads: the game's (the AI's tactics table) and the shells' (nitshell.dll's car table; i76shell.dll kept alongside it)
    private static readonly HashSet<string> KeepBinaries = new(StringComparer.OrdinalIgnoreCase) { "i76.exe", "nitro.exe", "i76shell.dll", "nitshell.dll" };

    /// <summary>Why a file under {app} is left out, or null to extract it.</summary>
    public static string? SkipReason(string rel)
    {
        var parts = rel.Split('/');
        var name = parts[^1];
        if (parts.Length > 1 && SkipDirs.Contains(parts[0])) return $"{parts[0]}/ (launcher, network or editor files)";
        if (name.StartsWith("goggame", StringComparison.OrdinalIgnoreCase)) return "GOG Galaxy";
        if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return "uninstaller";
        if (KeepBinaries.Contains(name) && parts.Length == 1) return null;
        var ext = Path.GetExtension(name);
        if (SkipExts.Contains(ext)) return $"{ext} (not game data)";
        return null;
    }

    /// <summary>A destination under {app}, as a safe relative path with '/', or null (other folders, or anything escaping it).</summary>
    public static string? AppRelative(string destination)
    {
        const string App = "{app}\\";
        if (!destination.StartsWith(App, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = destination[App.Length..].Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Any(c => c < 32 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*')))
            return null;
        return string.Join('/', parts);
    }

    /// <summary>Opens an installer and checks it holds Interstate '76 or the Nitro Pack.</summary>
    public static GogInstaller Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{path} doesn't exist", path);
        var setup = InnoSetup.Open(path);
        var byDest = new Dictionary<string, (InnoFile File, string Rel)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in setup.Files)
        {
            if (f.External || f.Location >= setup.Data.Count) continue;
            if (AppRelative(f.Destination) is not { } rel) continue;
            // the same destination twice (per-language or conditional copies): the language-neutral or English one
            if (byDest.TryGetValue(rel, out var prev) && Preference(prev.File) >= Preference(f)) continue;
            byDest[rel] = (f, rel);
        }
        var entries = byDest.Values.OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .Select(x => new GogEntry(x.Rel, setup.Data[x.File.Location], SkipReason(x.Rel))).ToList();
        bool Has(string n) => entries.Any(e => string.Equals(e.RelativePath, n, StringComparison.OrdinalIgnoreCase));
        GogGame game;
        if (Has("I76.ZFS")) game = GogGame.I76;
        else if (Has("nitro.zfs")) game = GogGame.Nitro;
        else
            throw new InstallerException(InstallerError.NotTheGame,
                $"{Path.GetFileName(path)} (\"{setup.Header.AppName}\") doesn't contain Interstate '76 or the Nitro Pack (no I76.ZFS or nitro.zfs).");
        return new GogInstaller(setup, game, entries);
    }

    /// <summary>One file of the installer's game data (by its path under {app}, any case), decoded alone, or null if it has none. Reads only up to that file; nothing is written.</summary>
    public byte[]? ReadFile(string relativePath)
    {
        var e = Entries.FirstOrDefault(x => string.Equals(x.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        using var ms = new MemoryStream();
        InnoExtractor.Run(Setup, [e.Data], (_, src) => src.CopyTo(ms));
        return ms.ToArray();
    }

    private static int Preference(InnoFile f) =>
        f.Languages.Length == 0 ? 2 : f.Languages.Split(' ', ',').Any(l => l.Equals("english", StringComparison.OrdinalIgnoreCase) || l.Equals("en", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

    /// <summary>Free bytes on the disk holding <paramref name="dir"/> (the longest mount point that contains it), or null.</summary>
    public static long? FreeSpace(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var best = DriveInfo.GetDrives().Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return best?.AvailableFreeSpace;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The folder already holds this installer's extraction, complete (its marker names the same installer).</summary>
    public bool AlreadyIn(string dir)
    {
        var m = Path.Combine(dir, MarkerName);
        if (!File.Exists(m) || !Vfs.InstallSearch.IsInstall(dir)) return false;
        try { return File.ReadLines(m).Contains(MarkerLine()); }
        catch (IOException) { return false; }
    }

    private string MarkerLine() => $"installer: {Path.GetFileName(Setup.Path)} {Setup.ExeLength}";

    /// <summary>
    /// Extracts the game data into <paramref name="dir"/> (created if needed). Files are written as *.i26part and renamed
    /// once their checksum matched; the archive (I76.ZFS / nitro.zfs) is renamed last, so an interrupted extraction is
    /// never taken for an install.
    /// </summary>
    public GogExtractResult ExtractTo(string dir, Action<InnoProgress>? progress = null, CancellationToken cancel = default, bool all = false)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        dir = Path.GetFullPath(dir);
        var marker = Path.Combine(dir, MarkerName);
        if (Vfs.InstallSearch.IsInstall(dir) && !File.Exists(marker))
            throw new InstallerException(InstallerError.TargetInUse,
                $"{dir} already holds an Interstate '76 install; choose an empty folder (I26 never writes into a game install).");
        Directory.CreateDirectory(dir);
        // all: every file under {app}, executables and launchers included (a full install for development)
        var wanted = (all ? Entries : Wanted).ToList();
        long need = wanted.Sum(e => (long)e.Data.FileSize);
        if (FreeSpace(dir) is { } free && free < need + (16L << 20))
            throw new InstallerException(InstallerError.NoSpace,
                $"not enough disk space in {dir}: {Mb(need)} needed, {Mb(free)} free.");

        var byData = wanted.GroupBy(e => e.Data.Index).ToDictionary(g => g.Key, g => g.ToList());
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // one spelling per folder
        string Target(string rel)
        {
            var parts = rel.Split('/');
            string cur = dir, key = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                key += parts[i] + "/";
                if (!dirs.TryGetValue(key, out var actual))
                {
                    actual = Directory.Exists(cur) ? Directory.EnumerateDirectories(cur)
                        .FirstOrDefault(d => string.Equals(Path.GetFileName(d), parts[i], StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(cur, parts[i]) : Path.Combine(cur, parts[i]);
                    Directory.CreateDirectory(actual);
                    dirs[key] = actual;
                }
                cur = actual;
            }
            return Path.Combine(cur, parts[^1]);
        }

        // the marker goes until this extraction is complete
        if (File.Exists(marker)) File.Delete(marker);
        var deferred = new List<(string Part, string Final)>();
        string? current = null;
        long bytes = 0;
        int files = 0;
        try
        {
            InnoExtractor.Run(Setup, byData.Values.Select(g => g[0].Data), (d, src) =>
            {
                var first = byData[d.Index][0];
                current = Target(first.RelativePath) + ".i26part";
                using var o = new FileStream(current, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                src.CopyTo(o, 1 << 16);
            }, d =>
            {
                var group = byData[d.Index];
                var part = current!;
                current = null;
                for (int i = 0; i < group.Count; i++)
                {
                    var final = Target(group[i].RelativePath);
                    var p = i == group.Count - 1 ? part : final + ".i26part";
                    if (i < group.Count - 1) File.Copy(part, p, true);
                    SetTime(p, d.FileTime);
                    if (IsArchive(group[i].RelativePath)) deferred.Add((p, final));
                    else File.Move(p, final, true);
                    files++;
                    bytes += (long)d.FileSize;
                }
            }, progress, cancel, d => byData.TryGetValue(d.Index, out var g) ? g[0].RelativePath : $"#{d.Index}");
            foreach (var (part, final) in deferred) File.Move(part, final, true);
            deferred.Clear();
            File.WriteAllLines(marker,
            [
                "Extracted by I26 from a GOG offline installer.",
                MarkerLine(),
                $"title: {Setup.Header.AppVersionedName}",
                $"inno: {Setup.Version}",
                $"date: {DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)}",
            ]);
        }
        catch
        {
            if (current is not null) TryDelete(current);
            foreach (var (part, _) in deferred) TryDelete(part);
            throw;
        }
        return new GogExtractResult(dir, files, bytes, started.Elapsed);
    }

    private static bool IsArchive(string rel) => rel.Equals("I76.ZFS", StringComparison.OrdinalIgnoreCase) || rel.Equals("nitro.zfs", StringComparison.OrdinalIgnoreCase);

    private static void SetTime(string path, long filetime)
    {
        try { if (filetime > 0) File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc(filetime)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
