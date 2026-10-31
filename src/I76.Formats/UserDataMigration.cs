namespace I76.Formats;

/// <summary>
/// The one-time copy of the player's settings, saves and car variants from the old user folder
/// ("Interstate '76 (Godot)") to I26's. Copy only: the old folder is never changed, and a file already
/// in the new folder is never overwritten.
/// </summary>
public static class UserDataMigration
{
    public const string Marker = ".migrated-from-i76-godot";
    private static readonly string[] Derived = ["boot_splash.png", "boot_splash.cfg"];

    /// <summary>True if it copied. Does nothing if the marker is there, the new folder already has settings, or the old one is missing.</summary>
    public static bool Run(string oldDir, string newDir)
    {
        if (!Directory.Exists(oldDir)) return false;
        Directory.CreateDirectory(newDir);
        if (File.Exists(Path.Combine(newDir, Marker))) return false;
        if (File.Exists(Path.Combine(newDir, "shell.cfg")) || File.Exists(Path.Combine(newDir, "settings.cfg"))) return false;
        Copy(oldDir, newDir, topLevel: true);
        File.WriteAllText(Path.Combine(newDir, Marker), oldDir + "\n");
        return true;
    }

    private static void Copy(string from, string to, bool topLevel)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(f);
            if (topLevel && Derived.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(to, name);
            if (!File.Exists(dest)) File.Copy(f, dest, overwrite: false);
        }
        foreach (var d in Directory.GetDirectories(from))
            Copy(d, Path.Combine(to, Path.GetFileName(d)), false);
    }
}
