using I76.Formats.Vfs;

namespace I76.Formats.Installers;

/// <summary>A game the player can start: an install on disk, or only its GOG offline installer (extracted when chosen). One per <see cref="GogGame"/>.</summary>
public sealed record AvailableGame(GogGame Game, string? InstallDir, string? InstallerPath, long InstallerBytes)
{
    public bool Installed => InstallDir is not null;
    public string Title => Game == GogGame.Nitro ? "Interstate '76 Nitro Pack" : "Interstate '76";
    /// <summary>The shell database inside the game's files (the main menu's logo card is in it).</summary>
    public string ShellDatabaseName => Game == GogGame.Nitro ? "database.tvf" : "DATABASE.MW2";
}

/// <summary>
/// Which games can be started: the installs found, and the GOG offline installers (setup_*.exe) next to the executable,
/// in its parent folder and in the user data folder. A game counts once, its install preferred over its installer.
/// </summary>
public static class GameDiscovery
{
    /// <summary>The game an installer file is for by its name (GOG's setup_interstate76_*.exe, setup_interstate76_nitro_pack_*.exe); null for anything else, the Galaxy stub included.</summary>
    public static GogGame? InstallerKindByName(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        if (!n.StartsWith("setup_") || !n.EndsWith(".exe") || n.Contains("galaxy")) return null;
        if (n.Contains("nitro")) return GogGame.Nitro;
        if (n.Contains("interstate76") || n.Contains("interstate_76") || n.Contains("i76")) return GogGame.I76;
        return null;
    }

    /// <summary>By name, else (for a setup_*.exe named otherwise) by the installer's contents.</summary>
    public static GogGame? InstallerKind(string path)
    {
        if (InstallerKindByName(path) is { } k) return k;
        var n = Path.GetFileName(path).ToLowerInvariant();
        if (!n.StartsWith("setup") || !n.EndsWith(".exe") || n.Contains("galaxy")) return null;
        try { return GogInstaller.Open(path).Game; }
        catch (Exception ex) when (ex is InstallerException or IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>The game an install folder holds, or null if it isn't an install.</summary>
    public static GogGame? InstallKind(string? dir)
    {
        if (!InstallSearch.IsInstall(dir)) return null;
        try { return new GameInstall(dir!).IsNitro ? GogGame.Nitro : GogGame.I76; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The installers in these folders (not below them): per game the newest by name.</summary>
    public static Dictionary<GogGame, string> FindInstallers(IEnumerable<string?> folders)
    {
        var found = new Dictionary<GogGame, string>();
        foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).Distinct())
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "setup*.exe").ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files.OrderByDescending(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (InstallerKind(f) is not { } g || found.ContainsKey(g)) continue;
                found[g] = f;
            }
        }
        return found;
    }

    /// <summary>The games available, Interstate '76 first; <paramref name="installs"/> in order of preference (the first folder of a game wins).</summary>
    public static List<AvailableGame> Discover(IEnumerable<string?> installs, IEnumerable<string?> installerFolders)
    {
        var inst = new Dictionary<GogGame, string>();
        foreach (var d in installs)
            if (!string.IsNullOrEmpty(d) && InstallKind(d) is { } g) inst.TryAdd(g, d);
        var setups = FindInstallers(installerFolders);
        var list = new List<AvailableGame>();
        foreach (var g in new[] { GogGame.I76, GogGame.Nitro })
        {
            inst.TryGetValue(g, out var dir);
            setups.TryGetValue(g, out var setup);
            if (dir is null && setup is null) continue;
            long size = 0;
            if (dir is null && setup is not null) { try { using var fs = File.OpenRead(setup); size = fs.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            list.Add(new AvailableGame(g, dir, dir is null ? setup : null, size));
        }
        return list;
    }

    /// <summary>The places the game looks, for a program that is not the game (the dedicated server): install folders (the executable's folder and its parent, then ExtractionPlaces) and installer folders (the same two and the user folder).</summary>
    public static (List<string?> Installs, List<string?> InstallerFolders) Places(string? exePath, string localRoot)
    {
        var exeDir = ExtractionPlaces.AppFolder(exePath);
        var parent = exeDir is null ? null : Path.GetDirectoryName(exeDir);
        var installs = new List<string?> { exeDir, parent };
        installs.AddRange(ExtractionPlaces.All(exePath, localRoot));
        return (installs, [exeDir, parent, localRoot]);
    }

    /// <summary>The game to serve: <paramref name="want"/> if given (error when it isn't available), else Interstate '76 if present, else the Nitro Pack. Null with an error when nothing is available.</summary>
    public static AvailableGame? Pick(IReadOnlyList<AvailableGame> games, GogGame? want, out string? error)
    {
        error = null;
        if (games.Count == 0) { error = "no game found"; return null; }
        if (want is { } w)
        {
            var g = games.FirstOrDefault(x => x.Game == w);
            if (g is null) error = $"{(w == GogGame.Nitro ? "Interstate '76 Nitro Pack" : "Interstate '76")} was not found";
            return g;
        }
        return games[0];
    }
}
