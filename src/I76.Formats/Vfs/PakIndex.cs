using System.Text;

namespace I76.Formats.Vfs;

public sealed record PakMember(string Name, int Offset, int Size);

/// <summary>
/// The .pix/.pak pairs nested in I76.ZFS, a port of the reference reader
/// (docs/formats/pak-container.md). A .pak is a flat concatenation of member
/// files and its .pix is a text index into it.
/// </summary>
public static class PakIndex
{
    /// <summary>Parse a .pix: a member count, then "name offset length" lines.</summary>
    public static List<PakMember> ParsePix(byte[] text)
    {
        var lines = Encoding.Latin1.GetString(text).Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0).ToList();
        int declared = int.Parse(lines[0].Trim());
        var members = new List<PakMember>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) throw new InvalidDataException($"malformed .pix line: {line}");
            members.Add(new PakMember(parts[0], int.Parse(parts[1]), int.Parse(parts[2])));
        }
        if (declared != members.Count)
            throw new InvalidDataException($".pix declares {declared} members, found {members.Count}");
        return members;
    }
}

/// <summary>All .pix/.pak pairs held inside a ZFS archive.</summary>
public sealed class PakSet
{
    public ZfsArchive Archive { get; }
    public IReadOnlyList<string> Stems { get; }

    public PakSet(ZfsArchive ar)
    {
        Archive = ar;
        Stems = ar.Entries.Where(e => e.Name.EndsWith(".pix", StringComparison.Ordinal))
            .Select(e => e.Name[..^4]).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public List<PakMember> Members(string stem) => PakIndex.ParsePix(Archive.Read(Archive.Find(stem + ".pix")!));
    public byte[] Payload(string stem) => Archive.Read(Archive.Find(stem + ".pak")!);
    public ZfsEntry? PakEntry(string stem) => Archive.Find(stem + ".pak");
}
