namespace I76.Formats.Save;

/// <summary>
/// The folders that may hold the original's bookmarks for an install: the install folder itself (where the game
/// writes savegame.dir and the saveNNN.cmp files), and, for an install under Program Files on Windows, its copy in
/// the user's VirtualStore, where Windows puts the files an old game writes into a folder it may not write to.
/// </summary>
public static class OriginalSaveFolders
{
    public const string DirFile = "savegame.dir";

    /// <summary>The folders with a savegame.dir, install folder first, each once.</summary>
    /// <param name="localAppData">Windows' local application data folder (null elsewhere): the VirtualStore is under it.</param>
    public static List<string> Find(string installRoot, string? localAppData)
    {
        var l = new List<string>();
        void Try(string dir)
        {
            try
            {
                var full = Path.GetFullPath(dir);
                if (Directory.Exists(full) && FindFile(full, DirFile) is not null && !l.Contains(full, StringComparer.OrdinalIgnoreCase)) l.Add(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        Try(installRoot);
        if (localAppData is { Length: > 0 } && VirtualStore(installRoot, localAppData) is { } vs) Try(vs);
        return l;
    }

    /// <summary>The VirtualStore copy of a folder: the path without its drive, under localAppData\VirtualStore. Null for a path with no drive letter.</summary>
    public static string? VirtualStore(string dir, string localAppData)
    {
        if (dir.Length < 3 || !char.IsAsciiLetter(dir[0]) || dir[1] != ':' || dir[2] is not ('\\' or '/')) return null;
        return Path.Combine(localAppData, "VirtualStore", dir[3..].Replace('/', '\\').TrimEnd('\\'));
    }

    /// <summary>
    /// The bookmarks of one folder, as the original's LOAD board lists them: the savegame.dir rows whose .cmp is there,
    /// in the file's order, each read with its body. A body that does not read is returned with its error.
    /// </summary>
    public static List<(OriginalSaveEntry Entry, OriginalSave? Save, string? Error)> Read(string folder)
    {
        var l = new List<(OriginalSaveEntry, OriginalSave?, string?)>();
        if (FindFile(folder, DirFile) is not { } dir) return l;
        foreach (var e in OriginalSaveEntry.ReadDir(File.ReadAllBytes(dir)))
        {
            if (e.File.Length == 0 || e.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            if (FindFile(folder, e.File + ".cmp") is not { } cmp) continue;
            try { l.Add((e, OriginalSave.Read(File.ReadAllBytes(cmp)), null)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { l.Add((e, null, ex.Message)); }
        }
        return l;
    }

    /// <summary>A file of the folder by name, in any case (an install copied from a CD or unpacked elsewhere may be upper case).</summary>
    public static string? FindFile(string folder, string name)
    {
        var exact = Path.Combine(folder, name);
        if (File.Exists(exact)) return exact;
        try { return Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
