using System.Buffers.Binary;
using System.Text;

namespace I76.Formats.Vfs;

/// <summary>One live record in the ZFS directory.</summary>
public sealed record ZfsEntry(string Name, int Offset, int StoredSize, uint Flags)
{
    public uint Method => Flags & ZfsArchive.MethodMask;

    /// <summary>Size after decompression; stored records use their stored size.</summary>
    public int Size => Method != 0 ? (int)(Flags >> 8) : StoredSize;

    public string MethodName => Method switch { 0 => "stored", 2 => "lzo1x", 4 => "lzo1y", _ => "?" };
}

/// <summary>
/// I76.ZFS, a port of the reference reader (docs/formats/zfs-archive.md). Thread-safe:
/// every read opens its own view of the file.
/// </summary>
public sealed class ZfsArchive
{
    public const int HeaderSize = 0x1C;
    public const int DTableSize = 0xE14;
    public const int EntrySize = 0x24;
    public const int EntriesPerDTable = 100;
    public const uint FlagDeleted = 1;
    public const uint MethodMask = 6;

    public string Path { get; }
    public int Version { get; }
    public int Const16 { get; }
    public int PerTable { get; }
    public int Count { get; }
    public uint XorKey { get; }
    public int DTables { get; private set; }
    public IReadOnlyList<ZfsEntry> Entries { get; }

    private readonly Dictionary<string, ZfsEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ZfsArchive(string path)
    {
        Path = path;
        using var f = File.OpenRead(path);
        var head = new byte[HeaderSize];
        if (f.Read(head) != HeaderSize || Encoding.Latin1.GetString(head, 0, 4) != "ZFSF")
            throw new InvalidDataException($"{path}: not a ZFS archive");
        var h = head.AsSpan(4);
        Version = BinaryPrimitives.ReadInt32LittleEndian(h);
        Const16 = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
        PerTable = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
        Count = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
        XorKey = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        int first = BinaryPrimitives.ReadInt32LittleEndian(h[20..]);
        if ((Version, Const16, PerTable) != (1, 16, 100))
            throw new InvalidDataException($"{path}: unexpected header constants {Version},{Const16},{PerTable}");
        Entries = ReadDirectory(f, first);
        foreach (var e in Entries)
            _byName.TryAdd(e.Name, e);
    }

    private List<ZfsEntry> ReadDirectory(FileStream f, int offset)
    {
        var list = new List<ZfsEntry>();
        var blk = new byte[DTableSize];
        DTables = 0;
        while (offset != 0)
        {
            f.Seek(offset, SeekOrigin.Begin);
            if (f.Read(blk) != DTableSize)
                throw new InvalidDataException($"{Path}: truncated directory table");
            DTables++;
            int next = BinaryPrimitives.ReadInt32LittleEndian(blk);
            for (int i = 0; i < EntriesPerDTable; i++)
            {
                var raw = blk.AsSpan(4 + i * EntrySize, EntrySize);
                if (raw[0] == 0) break;
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x20..]);
                if ((flags & FlagDeleted) != 0) continue;
                list.Add(new ZfsEntry(
                    Bin.CStr(raw[..16]),
                    BinaryPrimitives.ReadInt32LittleEndian(raw[0x10..]),
                    BinaryPrimitives.ReadInt32LittleEndian(raw[0x18..]),
                    flags));
            }
            offset = next;
        }
        return list;
    }

    public ZfsEntry? Find(string name) => _byName.GetValueOrDefault(name);

    public byte[] Read(ZfsEntry e)
    {
        var stored = new byte[e.StoredSize];
        using (var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
        {
            f.Seek(e.Offset, SeekOrigin.Begin);
            f.ReadExactly(stored);
        }
        var data = Lzo.DecompressZfsRecord(stored, e.Flags, e.Size);
        if (XorKey != 0)
        {
            // Applied after decompression, dword-wise, as ReadZfsRecord does.
            Span<byte> key = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(key, XorKey);
            for (int i = 0; i + 3 < data.Length; i += 4)
                for (int j = 0; j < 4; j++)
                    data[i + j] ^= key[j];
        }
        return data;
    }
}
