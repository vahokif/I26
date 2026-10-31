namespace I76.Formats.Installers;

/// <summary>
/// Where a GOG installer is unpacked, tried in order: (a) inside the I26 folder, next to the executable, as i76/ and
/// nitro/; (b) the local user folder (Windows %LOCALAPPDATA%\I26, Linux ~/.local/share/I26), as i76/ and nitro/.
/// A development run (the Godot editor binary) skips (a). Nothing is stored about the choice: discovery just looks at
/// both places again, and they hold any valid install, a game folder copied in by hand included.
/// </summary>
public static class ExtractionPlaces
{

    /// <summary>
    /// The folder "next to the executable": its own folder, except inside a macOS app bundle
    /// (.../I26.app/Contents/MacOS/I26), where it is the folder holding I26.app. The bundle is signed and nothing
    /// may be written into it, and the player puts the game beside the .app. Pure path logic (no file access).
    /// </summary>
    public static string? AppFolder(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (dir is null) return null;
        var macos = new DirectoryInfo(dir);
        if (macos.Name == "MacOS" && macos.Parent is { Name: "Contents" } contents
            && contents.Parent is { } app && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return app.Parent?.FullName;
        return dir;
    }

    /// <summary>The Godot editor or a Godot runner (nix store, godot-mono, Godot_v4...) rather than a shipped i26 executable.</summary>
    public static bool IsDevBinary(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return true;
        var full = Path.GetFullPath(exePath);
        return Path.GetFileName(full).Contains("godot", StringComparison.OrdinalIgnoreCase)
            || full.StartsWith("/nix/store/", StringComparison.Ordinal);
    }

    /// <summary>The folder (a) would use for the game (i76/ or nitro/ in the executable's folder), or null in a development run.</summary>
    public static string? Inside(string? exePath, GogGame g)
    {
        if (IsDevBinary(exePath)) return null;
        var exeDir = AppFolder(exePath);
        return exeDir is null ? null : Path.Combine(exeDir, GogInstaller.DefaultFolderName(g));
    }

    /// <summary>(b): the local user folder's i76/ or nitro/ (<paramref name="localRoot"/> is the I26 user data folder).</summary>
    public static string Local(string localRoot, GogGame g) => Path.Combine(localRoot, GogInstaller.DefaultFolderName(g));

    /// <summary>Every place an extraction of either game could be, for discovery: inside the executable's folder, then local.</summary>
    public static List<string> All(string? exePath, string localRoot)
    {
        var l = new List<string>();
        foreach (var g in new[] { GogGame.I76, GogGame.Nitro })
            if (Inside(exePath, g) is { } b) l.Add(b);
        foreach (var g in new[] { GogGame.I76, GogGame.Nitro }) l.Add(Local(localRoot, g));
        return l;
    }

    /// <summary>Ours to write into: absent, empty, or holding an earlier extraction (the installer's marker).</summary>
    public static bool IsFreeOrOurs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return !File.Exists(dir);
            return File.Exists(Path.Combine(dir, GogInstaller.MarkerName)) || !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Can a folder be created (or written to) under <paramref name="parent"/>? Tried for real, with a scratch file.</summary>
    public static bool CanWriteIn(string parent)
    {
        try
        {
            if (!Directory.Exists(parent)) return false;
            var probe = Path.Combine(parent, ".i26-write-test-" + Environment.ProcessId);
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Where to unpack <paramref name="g"/> needing <paramref name="bytes"/>: (a) if usable, else (b).</summary>
    public static string Choose(GogGame g, string? exePath, string localRoot, long bytes = 0)
    {
        if (Inside(exePath, g) is { } b && Path.GetDirectoryName(b) is { } parent
            && CanWriteIn(parent) && IsFreeOrOurs(b)
            && (bytes <= 0 || (GogInstaller.FreeSpace(parent) is not { } free || free >= bytes)))
            return b;
        return Local(localRoot, g);
    }
}
