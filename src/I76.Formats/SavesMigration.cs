namespace I76.Formats;

/// <summary>
/// Moves the save files that used to sit loose in the user folder (the trip's bookmarks and car, Nitro's campaign
/// progress) into user://saves/ (and saves/nitro/). Each file is copied, verified, then the original removed, so an
/// interrupted run loses nothing and a second run does nothing. A file already in saves/ is never overwritten:
/// if it differs, the old one is kept beside it under a "-old" suffix. Nothing else is touched.
/// </summary>
public static class SavesMigration
{
    public const string SavesDir = "saves";

    /// <summary>Moves what is found; log gets one line per file. Returns the number of files handled.</summary>
    public static int Run(string userDir, Action<string>? log = null)
    {
        if (!Directory.Exists(userDir)) return 0;
        int n = 0;
        var saves = Path.Combine(userDir, SavesDir);
        foreach (var name in new[] { "bookmarks.cfg", "trip.cfg", "trip_car.json" })
            n += Move(Path.Combine(userDir, name), Path.Combine(saves, name), log);
        foreach (var f in Directory.GetFiles(userDir, "bookmark_*.json"))
            n += Move(f, Path.Combine(saves, Path.GetFileName(f)), log);
        n += Move(Path.Combine(userDir, "nitro", "scenario.dat"), Path.Combine(saves, "nitro", "scenario.dat"), log);
        return n;
    }

    private static int Move(string from, string to, Action<string>? log)
    {
        try
        {
            if (!File.Exists(from)) return 0;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                if (Same(from, to)) { File.Delete(from); log?.Invoke($"saves: {from} already in {to}; the duplicate removed"); return 1; }
                to = Free(to);      // different content: keep both
            }
            var tmp = to + ".moving";
            File.Copy(from, tmp, overwrite: true);
            if (!Same(from, tmp)) { File.Delete(tmp); log?.Invoke($"saves: copy of {from} did not verify; left in place"); return 0; }
            File.Move(tmp, to);
            File.Delete(from);
            log?.Invoke($"saves: moved {from} -> {to}");
            return 1;
        }
        catch (Exception ex) { log?.Invoke($"saves: {from}: {ex.Message}"); return 0; }
    }

    private static bool Same(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static string Free(string path)
    {
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 0; ; i++)
        {
            var p = Path.Combine(dir, $"{stem}-old{(i == 0 ? "" : i.ToString())}{ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
