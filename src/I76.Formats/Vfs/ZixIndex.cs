using System.Text;

namespace I76.Formats.Vfs;

/// <summary>A data source declared in I76.ZIX: a directory or archive ("dir:"), or a CD volume.</summary>
public sealed record ZixSource(bool IsDir, string Name, int CdIndex);

public sealed record ZixEntry(int Source, string Name);

/// <summary>
/// I76.ZIX, the plain-text archive index (docs/formats/zix-index.md): a count,
/// a block of CD volumes and a block of sources, each ended by a line
/// starting with '-', then "sourceIndex name" per file.
/// </summary>
public sealed class ZixIndex
{
    public int DeclaredCount { get; }
    public List<string> CdVolumes { get; } = new();
    public List<ZixSource> Sources { get; } = new();
    public List<ZixEntry> Entries { get; } = new();

    public ZixIndex(byte[] text)
    {
        var lines = Encoding.Latin1.GetString(text).Replace("\r\n", "\n").Split('\n');
        int i = 0;
        DeclaredCount = int.Parse(lines[i++].Trim());
        for (; i < lines.Length && !lines[i].StartsWith('-'); i++)
            if (lines[i].Trim().Length > 0) CdVolumes.Add(lines[i].Trim());
        i++;
        for (; i < lines.Length && !lines[i].StartsWith('-'); i++)
        {
            var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0].Equals("dir:", StringComparison.OrdinalIgnoreCase))
                Sources.Add(new ZixSource(true, parts[1], 0));
            else if (parts[0].StartsWith("cd", StringComparison.OrdinalIgnoreCase))
                Sources.Add(new ZixSource(false, parts[1], int.TryParse(parts[0][2..], out var n) ? n : 0));
        }
        i++;
        for (; i < lines.Length; i++)
        {
            var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            Entries.Add(new ZixEntry(int.Parse(parts[0]), parts[1].ToLowerInvariant()));
        }
    }
}
