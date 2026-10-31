namespace I76.Formats.Vfs;

/// <summary>
/// Where the install is, in order: --install, I76_INSTALL, the folder holding the executable and its parent
/// (i26 unpacked into the game's folder, or into a subfolder of it), then i76/ and nitro/ inside the executable's
/// folder and in the local user folder (Installers.ExtractionPlaces; our extractions or any copied-in game folder), then the games GOG Galaxy or a GOG installer put on the machine (Installers.GogLibrary). No state is stored between runs.
/// An install is a folder GameInstall accepts (I76.ZFS or nitro.zfs; GOG keeps the base game and the Nitro Pack in sibling folders).
/// </summary>
public static class InstallSearch
{
    public static bool IsInstall(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        try { _ = new GameInstall(dir); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The first candidate that is an install, else null; every folder looked at lands in <paramref name="checkedDirs"/>.</summary>
    public static string? Find(string? option, string? env, string? exePath, List<string>? checkedDirs = null,
        IEnumerable<string>? extracted = null)
    {
        var candidates = new List<string?> { option, env };
        if (I76.Formats.Installers.ExtractionPlaces.AppFolder(exePath) is { } exeDir)
        {
            candidates.Add(exeDir);
            candidates.Add(Path.GetDirectoryName(exeDir));
        }
        if (extracted is not null) candidates.AddRange(extracted);
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            checkedDirs?.Add(c);
            if (IsInstall(c)) return c;
        }
        return null;
    }
}
