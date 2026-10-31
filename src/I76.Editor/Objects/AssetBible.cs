using System.Reflection;

namespace I76.Editor.Objects;

/// <summary>One object's entry in the Complete Asset Bible: what it is, where the bible lists it, and its remark on a broken one.</summary>
public sealed record BibleEntry(string Name, string Description, string Section, bool GoldNitroOnly, string Note);

/// <summary>
/// The object names of the Complete Asset Bible for Interstate '76/Gold/Nitro (DIVER, building on Skull N. Bones and Brian Dean
/// Jennings), read from the embedded asset-bible.tsv: the picker shows a description as an object's label and searches it.
/// </summary>
public static class AssetBible
{
    /// <summary>The credit line the editor shows.</summary>
    public const string Credit = "Object names from the Complete Asset Bible by DIVER, Skull N. Bones and Brian Dean Jennings";

    private static Dictionary<string, BibleEntry>? _entries;
    public static IReadOnlyDictionary<string, BibleEntry> Entries => _entries ??= Load();

    /// <summary>The bible's description of an object name, or null when it has none (the name is shown instead).</summary>
    public static string? Describe(string name) => Entries.TryGetValue(name.TrimStart('#'), out var e) && e.Description.Length > 0 ? e.Description : null;

    public static Dictionary<string, BibleEntry> Parse(TextReader r)
    {
        var d = new Dictionary<string, BibleEntry>(StringComparer.OrdinalIgnoreCase);
        bool header = true;
        for (string? line; (line = r.ReadLine()) is not null;)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (header) { header = false; continue; }                 // the column names
            var f = line.Split('\t');
            if (f.Length < 4) continue;
            d[f[0]] = new BibleEntry(f[0], f[1], f[2], f[3] == "1", f.Length > 4 ? f[4] : "");
        }
        return d;
    }

    private static Dictionary<string, BibleEntry> Load()
    {
        using var s = typeof(AssetBible).Assembly.GetManifestResourceStream("asset-bible.tsv");
        return s is null ? new(StringComparer.OrdinalIgnoreCase) : Parse(new StreamReader(s));
    }
}
