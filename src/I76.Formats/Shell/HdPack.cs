using System.IO.Compression;
using System.Text.Json;

namespace I76.Formats.Shell;

/// <summary>What a pack's top-level pack.json says about it.</summary>
public sealed record HdPackInfo(string Name, string Version, string Date, string MinEngine)
{
    public override string ToString() => $"{Name} {Version}";

    public static HdPackInfo? Parse(byte[]? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new HdPackInfo(S("name") is { Length: > 0 } n ? n : "HD Menu Pack", S("version"), S("date"), S("min_engine"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Positive when <paramref name="a"/> is the newer version (dotted numbers, else ordinal).</summary>
    public static int Compare(string a, string b) =>
        System.Version.TryParse(a, out var x) && System.Version.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);
}

/// <summary>Where the HD menu art is read from: paths relative to the tex-menu layout ("shellbg1.webp", "nitro/shellbg.json").</summary>
public abstract class HdSource : IDisposable
{
    /// <summary>A folder or a zip: what the log says.</summary>
    public abstract string Origin { get; }
    public abstract int Count { get; }
    public HdPackInfo? Info { get; protected set; }
    public abstract bool Exists(string rel);
    public abstract byte[]? Read(string rel);
    public virtual void Dispose() { }

    protected static string Norm(string rel) => rel.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}

/// <summary>A folder in the tex-menu layout (case-sensitive file systems: the names as written).</summary>
public sealed class HdDirSource : HdSource
{
    private readonly string _dir;
    private readonly Lazy<int> _count;
    public HdDirSource(string dir)
    {
        _dir = dir;
        _count = new(() => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : 0);
        var p = Path.Combine(dir, "pack.json");
        if (File.Exists(p)) Info = HdPackInfo.Parse(File.ReadAllBytes(p));
    }
    public override string Origin => _dir;
    public override int Count => _count.Value;
    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
    public override bool Exists(string rel) => File.Exists(P(rel));
    public override byte[]? Read(string rel) { var p = P(rel); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
}

/// <summary>A pack file: a zip whose tex-menu/ folder is the layout, read in place.</summary>
public sealed class HdZipSource : HdSource
{
    private readonly string _path;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = [];
    private readonly object _lock = new();
    public const string Root = "tex-menu/";

    public HdZipSource(string path)
    {
        _path = path;
        _zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        foreach (var e in _zip.Entries)
        {
            var n = Norm(e.FullName);
            if (n.StartsWith(Root) && n.Length > Root.Length) _entries[n[Root.Length..]] = e;
        }
        Info = HdPackInfo.Parse(Slurp(_zip.GetEntry("pack.json")));
    }

    public override string Origin => _path;
    public override int Count => _entries.Count;
    public override bool Exists(string rel) => _entries.ContainsKey(Norm(rel));
    public override byte[]? Read(string rel) => _entries.TryGetValue(Norm(rel), out var e) ? Slurp(e) : null;

    private byte[]? Slurp(ZipArchiveEntry? e)
    {
        if (e is null) return null;
        lock (_lock)
        {
            using var s = e.Open();
            using var ms = new MemoryStream((int)Math.Min(e.Length, int.MaxValue));
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    public override void Dispose() => _zip.Dispose();
}

public static class HdPackFinder
{
    /// <summary>Opens the newest readable pack among i76-hd-menus*.zip in the folders, or null; unreadable ones are reported through <paramref name="warn"/>.</summary>
    public static HdZipSource? Newest(IEnumerable<string> folders, Action<string>? warn = null)
    {
        HdZipSource? best = null;
        foreach (var dir in folders.Where(Directory.Exists).Distinct())
            foreach (var f in Directory.EnumerateFiles(dir, "i76-hd-menus*.zip").OrderBy(f => f, StringComparer.Ordinal))
            {
                HdZipSource z;
                try { z = new HdZipSource(f); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { warn?.Invoke($"{f}: {e.Message}"); continue; }
                if (z.Info is null || z.Count == 0) { warn?.Invoke($"{f}: no pack.json or no tex-menu/ files"); z.Dispose(); continue; }
                if (best is null || HdPackInfo.Compare(z.Info.Version, best.Info!.Version) > 0) { best?.Dispose(); best = z; }
                else z.Dispose();
            }
        return best;
    }
}
