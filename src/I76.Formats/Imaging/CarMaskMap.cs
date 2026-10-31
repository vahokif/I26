using System.IO.Compression;
using System.Text;

namespace I76.Formats.Imaging;

/// <summary>
/// The per-texel material maps of the car textures that the segmentation pipeline made (`i76 car-project`):
/// for each texture (named by its stem with the paint scheme's trailing digit as ?, since a model's schemes share one layout) a
/// preset id (CarMaterialTable.Presets) per texel, 0 where no view saw the texel. Stored as one gzip file of run-length-encoded maps
/// (CarMasks.bin, embedded; reviewed car by car in the 3D garage).
/// </summary>
public sealed class CarMaskMap
{
    public sealed record Entry(string Key, int W, int H, byte[] Ids);

    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<Entry> Entries => _byKey.Values;

    public void Add(Entry e) => _byKey[e.Key] = e;

    /// <summary>The key a texture's maps are filed under: its stem with a trailing digit (the paint scheme) as ?.</summary>
    public static string KeyOf(string member)
    {
        string s = CarMaterialTable.StemOf(member);
        return s.Length > 0 && char.IsAsciiDigit(s[^1]) ? s[..^1] + "?" : s;
    }

    /// <summary>
    /// The ids of a texture of the given size, or null if there is no map for it (or it is another size): the texture's own map (its
    /// exact stem: a paint scheme segmented on its own, whose windows may lie elsewhere, the bus's Godolphin Lines) before the map its
    /// schemes share.
    /// </summary>
    public byte[]? Ids(string member, int w, int h) =>
        (_byKey.TryGetValue(CarMaterialTable.StemOf(member), out var own) && own.W == w && own.H == h ? own.Ids : null)
        ?? (_byKey.TryGetValue(KeyOf(member), out var e) && e.W == w && e.H == h ? e.Ids : null);

    public bool Has(string member) => _byKey.ContainsKey(CarMaterialTable.StemOf(member)) || _byKey.ContainsKey(KeyOf(member));

    public void Write(Stream to)
    {
        using var gz = new GZipStream(to, CompressionLevel.SmallestSize, true);
        using var w = new BinaryWriter(gz, Encoding.UTF8, true);
        w.Write(Encoding.ASCII.GetBytes("CMK1"));
        w.Write(_byKey.Count);
        foreach (var e in _byKey.Values.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            w.Write(e.Key.ToLowerInvariant()); w.Write((ushort)e.W); w.Write((ushort)e.H);
            var runs = new List<(byte Id, int Len)>();
            foreach (var b in e.Ids)
                if (runs.Count > 0 && runs[^1].Id == b) runs[^1] = (b, runs[^1].Len + 1); else runs.Add((b, 1));
            w.Write(runs.Count);
            foreach (var (id, len) in runs) { w.Write(id); w.Write7BitEncodedInt(len); }
        }
    }

    public static CarMaskMap Read(Stream from)
    {
        using var gz = new GZipStream(from, CompressionMode.Decompress, true);
        using var r = new BinaryReader(gz, Encoding.UTF8, true);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "CMK1") throw new FormatException("not a car mask file");
        var m = new CarMaskMap();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string key = r.ReadString(); int w = r.ReadUInt16(), h = r.ReadUInt16(); int runs = r.ReadInt32();
            var ids = new byte[w * h];
            int p = 0;
            for (int k = 0; k < runs; k++)
            {
                byte id = r.ReadByte(); int len = r.Read7BitEncodedInt();
                if (p + len > ids.Length) throw new FormatException($"car mask {key}: runs overflow {w}x{h}");
                Array.Fill(ids, id, p, len); p += len;
            }
            if (p != ids.Length) throw new FormatException($"car mask {key}: runs fill {p} of {w * h} texels");
            m.Add(new Entry(key, w, h, ids));
        }
        return m;
    }

    /// <summary>The maps of the game: I76_CAR_MASKS_FILE if set (to try a fresh `i76 car-project` run), else CarMasks.bin, embedded; empty if there is none.</summary>
    public static CarMaskMap Default => _default ??= LoadDefault();
    private static CarMaskMap? _default;

    private static CarMaskMap LoadDefault()
    {
        try
        {
            string? f = Environment.GetEnvironmentVariable("I76_CAR_MASKS_FILE");
            if (!string.IsNullOrEmpty(f) && File.Exists(f)) { using var fs = File.OpenRead(f); return Read(fs); }
            using var s = typeof(CarMaskMap).Assembly.GetManifestResourceStream("CarMasks.bin");
            return s is null ? new CarMaskMap() : Read(s);
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException) { return new CarMaskMap(); }
    }

    /// <summary>Whether the game applies the maps: always, but I76_CAR_MASKS=0 (to compare the cars without them).</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("I76_CAR_MASKS") != "0";
}
