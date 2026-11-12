using System.IO.Compression;
using I76.Formats.Imaging;

namespace I76.Formats.Shell;

/// <summary>How a pack was found: where its folder sits, or that it is read from inside its zip.</summary>
public enum HdPackPlace
{
    /// <summary>The user folder's own tex-menu, tex-world-4x ... (the layout the packs' README asks for).</summary>
    UserFolder,
    /// <summary>One level too deep: tex-world-4x\tex-world-4x (the folder made first, then the archive extracted into it).</summary>
    Nested,
    /// <summary>Inside a folder named after the archive (Windows' Extract All: I26-HD-Textures-4x-2026.11.11\tex-world-4x).</summary>
    Extracted,
    /// <summary>Beside the program instead of in the user folder (as a folder, nested or extracted there too).</summary>
    BesideProgram,
    /// <summary>Read from the archive itself, not extracted (I26-HD-*.zip or i76-hd-*.zip in the user folder or beside the program).</summary>
    Zip,
}

/// <summary>A pack found: its layout's name (tex-menu, tex-world, tex-world-4x ...), the folder or zip, the zip's folder that holds the layout.</summary>
public sealed record HdPackFound(string Name, string Path, string? ZipRoot, HdPackPlace Place, HdPackInfo? Info)
{
    /// <summary>The pack as a source (a new one each call: the caller owns it); a world pack's folder is read flat, as HdTextures does.</summary>
    public HdSource Open() => ZipRoot is not null ? new HdZipSource(Path, ZipRoot)
        : Name == HdPackDiscovery.Menu ? new HdDirSource(Path) : new HdFlatDirSource(Path);

    /// <summary>Where, in the log's and the row's plain words.</summary>
    public string Where => Place switch
    {
        HdPackPlace.UserFolder => Path,
        HdPackPlace.Nested => $"{Path} (one folder too deep; used)",
        HdPackPlace.Extracted => $"{Path} (inside the folder Extract All made; used)",
        HdPackPlace.BesideProgram => $"{Path} (beside the program, not in the user folder; used)",
        _ => $"{Path} (read from the zip, not extracted; used)",
    };
}

/// <summary>
/// Where the HD packs are, found once at start: the user folder's tex-menu, tex-world and tex-world-Nx folders as the packs' README lays
/// them out, and the likely wrong installs a player makes of the published archives, each used where it is, with a note of where:
/// a pack folder one level too deep (tex-world-4x\tex-world-4x), inside the folder Windows' Extract All names after the archive
/// (I26-HD-Textures-4x-2026.11.11\tex-world-4x), beside the program instead of in the user folder, or the archive left as a zip
/// (I26-HD-*.zip, i76-hd-*.zip; its layout read in place from under its top folder). A folder in the right place wins over every other
/// form of the same pack; zips come last, the newest pack.json version of a name first. Nothing is read but folder listings, pack.json
/// files and the zips' central directories (no picture). A folder that holds a pack.json but none of these names is reported as not
/// used, with the reason (a renamed folder could be a player's backup or a pack turned off, so it is never taken).
/// </summary>
public sealed class HdPackDiscovery
{
    public const string Menu = "tex-menu";

    /// <summary>Every layout name looked for: the menu pack, the plain tex-world, and the sized world folders largest first.</summary>
    public static readonly string[] Names = [Menu, HdTexture.Folder, .. HdTexture.Sizes.Select(HdTexture.FolderOf)];

    /// <summary>The release archives' names (the zips and the folders Extract All names after them), any case.</summary>
    public static bool IsArchiveName(string name) =>
        name.StartsWith("I26-HD-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("i76-hd-", StringComparison.OrdinalIgnoreCase);

    private readonly Dictionary<string, HdPackFound> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, string Why)> _notUsed = [];
    private readonly List<(string Dir, bool User)> _places = [];
    /// <summary>The folders looked in: the user folder, then the program's (each only when it exists).</summary>
    public IEnumerable<string> LookedIn => _places.Select(p => p.Dir);

    public IReadOnlyDictionary<string, HdPackFound> Found => _found;
    /// <summary>Plain sentences: a folder or zip that looks like a pack and was not used, and why.</summary>
    public IReadOnlyList<(string Path, string Why)> NotUsed => _notUsed;

    public bool Has(string name) => _found.ContainsKey(name);
    public HdPackFound? Get(string name) => _found.TryGetValue(name, out var f) ? f : null;

    /// <summary>Looks in <paramref name="userDir"/> and then <paramref name="programDir"/> (either may be null or missing).</summary>
    public static HdPackDiscovery Scan(string? userDir, string? programDir)
    {
        var d = new HdPackDiscovery();
        var places = d._places;
        if (userDir is { Length: > 0 } && Directory.Exists(userDir)) places.Add((Full(userDir), true));
        if (programDir is { Length: > 0 } && Directory.Exists(programDir) && !places.Any(p => Same(p.Dir, programDir))) places.Add((Full(programDir), false));

        // folders: the user folder's own names, nested, Extract All's; then the same beside the program
        foreach (var (dir, user) in places)
        {
            var subdirs = SafeDirs(dir);
            foreach (var name in Names)
            {
                // the folder as the file system spells it (a case-sensitive one may have TEX-MENU)
                if (subdirs.FirstOrDefault(s => Path.GetFileName(s).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } top && HasContent(top))
                {
                    var (path, nested) = Resolve(top, name);
                    d.Add(name, path, null, !user ? HdPackPlace.BesideProgram : nested ? HdPackPlace.Nested : HdPackPlace.UserFolder);
                }
                foreach (var x in subdirs.Where(s => IsArchiveName(Path.GetFileName(s))))
                {
                    if (SafeDirs(x).FirstOrDefault(s => Path.GetFileName(s).Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } inner || !HasContent(inner)) continue;
                    var (path, _) = Resolve(inner, name);
                    d.Add(name, path, null, user ? HdPackPlace.Extracted : HdPackPlace.BesideProgram);
                }
            }
            // a pack.json in a folder of another name: reported, never taken
            foreach (var x in subdirs)
            {
                var n = Path.GetFileName(x);
                if (n.StartsWith('.') || IsArchiveName(n) || Names.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                var pj = Path.Combine(x, "pack.json");
                if (File.Exists(pj))
                {
                    var info = HdPackInfo.Parse(ReadSmall(pj));
                    d._notUsed.Add((x, $"a pack ({info?.ToString() ?? "pack.json"}) in a folder named {n}: name the folder {LayoutOf(x)}"));
                    continue;
                }
                // a pack folder inside a folder of another name (a few entries only: never a cache's thousands)
                var inside = SafeDirs(x);
                if (inside.Count > 8) continue;
                foreach (var i in inside)
                    if (Names.Contains(Path.GetFileName(i), StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(i, "pack.json")))
                        d._notUsed.Add((i, $"move the folder {Path.GetFileName(i)} up into {dir}"));
            }
        }

        // zips, not extracted: every layout name under the zip's top (or one folder down), the newest version of each
        foreach (var (dir, _) in places)
        {
            IEnumerable<string> zips;
            try { zips = Directory.EnumerateFiles(dir, "*.zip", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Where(f => IsArchiveName(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var zip in zips) d.AddZip(zip);
        }
        return d;
    }

    private void Add(string name, string path, string? zipRoot, HdPackPlace place)
    {
        var info = zipRoot is null ? (File.Exists(Path.Combine(path, "pack.json")) ? HdPackInfo.Parse(ReadSmall(Path.Combine(path, "pack.json"))) : null) : null;
        if (_found.TryGetValue(name, out var had)) { _notUsed.Add((path, $"another {name} ({had.Path} is used)")); return; }
        _found[name] = new HdPackFound(name, path, zipRoot, place, info);
    }

    private void AddZip(string zip)
    {
        Dictionary<string, string> roots;
        Dictionary<string, HdPackInfo?> infos = [];
        try
        {
            using var z = new ZipArchive(new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
            roots = RootsOf(z.Entries.Select(e => e.FullName));
            foreach (var (name, root) in roots)
                infos[name] = HdPackInfo.Parse(Slurp(z.GetEntry(root + "pack.json"))) ?? HdPackInfo.Parse(Slurp(z.GetEntry("pack.json")));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _notUsed.Add((zip, $"not a readable zip ({e.Message})"));
            return;
        }
        if (roots.Count == 0) { _notUsed.Add((zip, "holds no tex-menu, tex-world or tex-world-Nx folder")); return; }
        foreach (var (name, root) in roots)
        {
            var info = infos[name];
            if (_found.TryGetValue(name, out var had))
            {
                // a folder beats any zip; between zips the newer pack.json version
                if (had.Place == HdPackPlace.Zip && info is not null && (had.Info is null || HdPackInfo.Compare(info.Version, had.Info.Version) > 0))
                {
                    _notUsed.Add((had.Path, $"{name} {had.Info?.Version} ({zip} is newer)"));
                    _found[name] = new HdPackFound(name, zip, root, HdPackPlace.Zip, info);
                }
                else _notUsed.Add((zip, $"its {name} ({had.Path} is used)"));
                continue;
            }
            _found[name] = new HdPackFound(name, zip, root, HdPackPlace.Zip, info);
        }
    }

    /// <summary>
    /// A zip's layouts: each layout name at the top of the zip (tex-world-4x/...) or one folder down (I26-HD-Textures-4x/tex-world-4x/...)
    /// with at least one file under it, mapped to that folder's path in the zip ("tex-world-4x/"), lower case as HdZipSource matches.
    /// </summary>
    public static Dictionary<string, string> RootsOf(IEnumerable<string> entries)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var seg = e.Replace('\\', '/').TrimStart('/').Split('/');
            for (int i = 0; i < Math.Min(2, seg.Length - 1); i++)
            {
                var name = Names.FirstOrDefault(n => n.Equals(seg[i], StringComparison.OrdinalIgnoreCase));
                if (name is null) continue;
                if (seg[^1].Length > 0 && !roots.ContainsKey(name)) roots[name] = (string.Join('/', seg.Take(i + 1)) + "/").ToLowerInvariant();
                break;
            }
        }
        return roots;
    }

    /// <summary>A pack folder, or the same name inside it when the folder holds no pack.json of its own and that inner folder exists (one level too deep).</summary>
    private static (string Path, bool Nested) Resolve(string dir, string name)
    {
        var inner = Path.Combine(dir, name);
        if (!File.Exists(Path.Combine(dir, "pack.json")) && Directory.Exists(inner) && HasContent(inner)) return (inner, true);
        return (dir, false);
    }

    /// <summary>The layout name a renamed pack folder's pack.json and files point to (the scale, or the menu pack's manifests).</summary>
    private static string LayoutOf(string dir)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ReadSmall(Path.Combine(dir, "pack.json")) ?? []);
            if (doc.RootElement.TryGetProperty("scale", out var s) && s.TryGetInt32(out int n)) return HdTexture.FolderOf(n);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { }
        return File.Exists(Path.Combine(dir, "shellbg.json")) || File.Exists(Path.Combine(dir, "pads.json")) ? Menu : "tex-menu or tex-world-4x (as its archive has it)";
    }

    private static bool HasContent(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static List<string> SafeDirs(string dir)
    {
        try { return [.. Directory.EnumerateDirectories(dir)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static byte[]? ReadSmall(string path)
    {
        try { return new FileInfo(path).Length < (4 << 20) ? File.ReadAllBytes(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static byte[]? Slurp(ZipArchiveEntry? e)
    {
        if (e is null || e.Length > (4 << 20)) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Full(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The start-up log's one line: each pack found and where (the place's words when it is not where the README puts it), then what
    /// looked like a pack and was not used.
    /// </summary>
    public string Summary()
    {
        var parts = Names.Where(Has).Select(n => $"{n} {Get(n)!.Info?.ToString() ?? ""}".TrimEnd() + $" at {Get(n)!.Where}").ToList();
        var line = parts.Count == 0 ? "none found (tex-menu, tex-world-4x, tex-world-8x in the user folder, or the I26-HD-*.zip archives)" : string.Join("; ", parts);
        line += "; looked in " + (_places.Count == 0 ? "no folder" : string.Join(" and ", _places.Select(p => p.Dir)));
        if (_notUsed.Count > 0) line += "; not used: " + string.Join("; ", _notUsed.Select(n => $"{n.Path}: {n.Why}"));
        return line;
    }
}
