namespace I76.Formats.Subtitles;

/// <summary>
/// Discovers and loads subtitle tracks from organized 'subtitles/i76/' and 'subtitles/nitro/' directories
/// across the install, application directory, or user data.
/// </summary>
public static class SubtitleLoader
{
    private static readonly Dictionary<string, SubtitleTrack> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether Nitro Pack subtitles should be prioritized.</summary>
    public static bool IsNitro { get; set; }

    /// <summary>Additional search directories configured at startup or runtime.</summary>
    public static List<string> SearchDirectories { get; } = [];

    /// <summary>Registers an additional directory to search for subtitle files.</summary>
    public static void RegisterSearchDirectory(string dir)
    {
        if (!string.IsNullOrWhiteSpace(dir) && !SearchDirectories.Contains(dir))
            SearchDirectories.Add(dir);
    }

    /// <summary>Clears cached subtitle tracks.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// Checks whether any subtitle directory exists and contains subtitle files (.srt or .json).
    /// </summary>
    public static bool HasSubtitles(string? installDir = null, bool includeGlobal = true)
    {
        var dirs = GetSearchDirs(installDir, null, includeGlobal);
        foreach (var dir in dirs)
        {
            if (HasSubtitlesInDirectory(dir)) return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether the given directory contains subtitle files (.srt or .json).
    /// </summary>
    public static bool HasSubtitlesInDirectory(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return false;
        try
        {
            return System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Loads a subtitle track for the given movie or mission name (e.g. "introf01", "t01", "p01").
    /// Checks for .srt and .json files matching the name, as well as shared dialogue.json.
    /// </summary>
    public static SubtitleTrack? Load(string name, string? installDir = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string stem = System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant();

        bool isNitro = ResolveIsNitro(name);
        string primaryGame = isNitro ? "nitro" : "i76";
        string cacheKey = $"{primaryGame}:{stem}";

        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        var dirs = GetSearchDirs(installDir, name);
        foreach (var dir in dirs)
        {
            if (!System.IO.Directory.Exists(dir)) continue;

            // 1. Direct file match: <stem>.srt or <stem>.json
            string srtPath = System.IO.Path.Combine(dir, $"{stem}.srt");
            if (System.IO.File.Exists(srtPath))
            {
                var track = SubtitleTrack.ParseSrt(System.IO.File.ReadAllText(srtPath));
                return Cache[cacheKey] = track;
            }

            string jsonPath = System.IO.Path.Combine(dir, $"{stem}.json");
            if (System.IO.File.Exists(jsonPath))
            {
                var track = SubtitleTrack.ParseJson(System.IO.File.ReadAllText(jsonPath));
                return Cache[cacheKey] = track;
            }
        }

        // 2. Check for shared dialogue.json or subtitles.json
        foreach (var dir in dirs)
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            string dialoguePath = System.IO.Path.Combine(dir, "dialogue.json");
            if (System.IO.File.Exists(dialoguePath))
            {
                var track = SubtitleTrack.ParseJson(System.IO.File.ReadAllText(dialoguePath));
                return Cache[cacheKey] = track;
            }
            string subPath = System.IO.Path.Combine(dir, "subtitles.json");
            if (System.IO.File.Exists(subPath))
            {
                var track = SubtitleTrack.ParseJson(System.IO.File.ReadAllText(subPath));
                return Cache[cacheKey] = track;
            }
        }

        return null;
    }

    private static bool ResolveIsNitro(string? name)
    {
        if (string.IsNullOrEmpty(name)) return IsNitro;

        string norm = name.Replace('\\', '/').ToLowerInvariant();
        if (norm.Contains("/nitro/") || norm.Contains("\\nitro\\"))
            return true;
        if (norm.Contains("/i76/") || norm.Contains("\\i76\\"))
            return false;

        string stem = System.IO.Path.GetFileNameWithoutExtension(norm);
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(?:p\d{2}|b01|\d{2}[a-z]{3}\d{2}|(?:gd|rl|tm)[a-z]{3}\d{2})"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(?:t\d{2}|s\d{2}|m\d{2}|a01)"))
            return false;

        return IsNitro;
    }

    private static List<string> GetSearchDirs(string? installDir, string? name = null, bool includeGlobal = true)
    {
        bool isNitro = ResolveIsNitro(name);
        string primary = isNitro ? "nitro" : "i76";
        string secondary = isNitro ? "i76" : "nitro";

        var baseDirs = new List<string>();

        // 1. Explicitly registered directories
        foreach (var d in SearchDirectories)
        {
            if (!string.IsNullOrEmpty(d) && !baseDirs.Contains(d)) baseDirs.Add(d);
        }

        // 2. Directory from movie/asset path
        if (!string.IsNullOrEmpty(name))
        {
            string? dir = System.IO.Path.GetDirectoryName(name);
            if (!string.IsNullOrEmpty(dir))
            {
                if (!baseDirs.Contains(dir)) baseDirs.Add(dir);
                string sibSub = System.IO.Path.Combine(dir, "..", "subtitles");
                try
                {
                    string fullSib = System.IO.Path.GetFullPath(sibSub);
                    if (!baseDirs.Contains(fullSib)) baseDirs.Add(fullSib);
                }
                catch { }
                if (!baseDirs.Contains(sibSub)) baseDirs.Add(sibSub);
            }
        }

        // 3. installDir
        if (!string.IsNullOrEmpty(installDir))
        {
            baseDirs.Add(System.IO.Path.Combine(installDir, "subtitles"));
            baseDirs.Add(installDir);
        }

        if (includeGlobal)
        {
            // 4. App directory
            string appDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(appDir))
            {
                baseDirs.Add(System.IO.Path.Combine(appDir, "subtitles"));
                baseDirs.Add(System.IO.Path.Combine(appDir, "..", "subtitles"));
            }

            // 5. User data
            // Windows: %APPDATA%\I26\subtitles
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string winUserDir = System.IO.Path.Combine(appData, "I26", "subtitles");
                if (!baseDirs.Contains(winUserDir)) baseDirs.Add(winUserDir);
            }

            // Linux / macOS user data paths
            string? home = System.Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                // Linux: $XDG_DATA_HOME/I26/subtitles or ~/.local/share/I26/subtitles
                string xdg = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? System.IO.Path.Combine(home, ".local", "share");
                string linuxUserDir = System.IO.Path.Combine(xdg, "I26", "subtitles");
                if (!baseDirs.Contains(linuxUserDir)) baseDirs.Add(linuxUserDir);

                // macOS: ~/Library/Application Support/I26/subtitles
                string macUserDir = System.IO.Path.Combine(home, "Library", "Application Support", "I26", "subtitles");
                if (!baseDirs.Contains(macUserDir)) baseDirs.Add(macUserDir);
            }

            // 6. Project relative defaults
            baseDirs.Add("subtitles");
        }

        // Expand each base directory into game-specific subfolders: primary first, secondary fallback, then base itself
        var dirs = new List<string>();
        void AddDir(string d)
        {
            if (!string.IsNullOrEmpty(d) && !dirs.Contains(d))
                dirs.Add(d);
        }

        foreach (var b in baseDirs)
        {
            string normB = b.Replace('\\', '/').TrimEnd('/');
            if (normB.EndsWith("/" + primary, StringComparison.OrdinalIgnoreCase) ||
                normB.EndsWith("/" + secondary, StringComparison.OrdinalIgnoreCase))
            {
                AddDir(b);
                continue;
            }

            AddDir(System.IO.Path.Combine(b, primary));
            AddDir(System.IO.Path.Combine(b, "subtitles", primary));
            AddDir(System.IO.Path.Combine(b, secondary));
            AddDir(System.IO.Path.Combine(b, "subtitles", secondary));
            AddDir(b);
        }

        return dirs;
    }
}
