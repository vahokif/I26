namespace I76.Formats.Shell;

/// <summary>
/// DATABASE.MW2, the front-end shell's resource pack (docs/formats/mw2-database.md):
/// 83 records addressed by number only. 1–32 are LZSS-packed PCX
/// backgrounds, 33–64 sprite banks, 65–71 bitmap fonts, 72–82 sounds and
/// 83 the credits text.
/// </summary>
public sealed class ShellDatabase
{
    private readonly byte[] _d;
    private readonly int[] _offset;

    /// <summary>
    /// True for the Nitro Pack's database.tvf (76 records, renumbered; NitroShellMap.cs): every lookup below still takes the
    /// base DATABASE.MW2 number and the map finds the Nitro record, and a bank's sprite in it, with the same picture.
    /// </summary>
    public bool IsNitro { get; }

    public ShellDatabase(byte[] data, bool translate = true)
    {
        _d = data;
        int n = BitConverter.ToInt32(data, 0);
        if (n <= 0 || 4 + 4 * n > data.Length) throw new InvalidDataException($"implausible record count {n}");
        _offset = new int[n + 1];
        for (int i = 0; i < n; i++) _offset[i] = BitConverter.ToInt32(data, 4 + 4 * i);
        _offset[n] = data.Length;
        IsNitro = translate && n == NitroShellMap.NitroCount;
    }

    /// <summary>Opens the shell database; <paramref name="translate"/> false addresses a Nitro one by its own numbers (the map generator's).</summary>
    public static ShellDatabase Open(string path, bool translate = true) => new(File.ReadAllBytes(path), translate);

    /// <summary>The record a base number names here: itself, or the Nitro one the map gives (0: Nitro has no such record).</summary>
    private int Rec(int id) => IsNitro ? NitroShellMap.Record(id) : id;

    public int Count => _offset.Length - 1;

    /// <summary>Record <paramref name="id"/> (1-based) as stored (GetDBItem).</summary>
    public ReadOnlySpan<byte> Raw(int id) => RawNative(Rec(id));

    /// <summary>Record <paramref name="n"/> by this file's own numbering (a Nitro database's new records have no base number).</summary>
    public ReadOnlySpan<byte> RawNative(int n) =>
        n >= 1 && n <= Count ? _d.AsSpan(_offset[n - 1], _offset[n] - _offset[n - 1]) : throw new KeyNotFoundException($"shell record {n}: this database has no such record");

    /// <summary>The Nitro record base number <paramref name="id"/> is shown from, 0 on the base game (or when Nitro has none).</summary>
    public int NativeRecord(int id) => IsNitro ? Rec(id) : 0;

    /// <summary>Whether base record <paramref name="id"/> has a record here (always, but on Nitro, which dropped some screens).</summary>
    public bool Has(int id) => Rec(id) != 0;

    /// <summary>A background (records 1–32): LZSS to an 8 bpp PCX. A Nitro install without that screen gets a black one.</summary>
    public Pcx Background(int id) => Rec(id) == 0 ? Pcx.Black : BackgroundNative(Rec(id));

    /// <summary>A background by this file's own record number.</summary>
    public Pcx BackgroundNative(int n) => Pcx.Parse(Lzss(RawNative(n)));

    /// <summary>A sprite bank (records 33–64, "1.10"): its items, decoded. On Nitro, indexed by the base bank's sprite numbers; one Nitro dropped is empty.</summary>
    public SpriteBank Bank(int id) =>
        Rec(id) == 0 ? SpriteBank.Empty : new(RawNative(Rec(id)), IsNitro ? NitroShellMap.SpriteMap(id) : null, Rec(id));

    /// <summary>A bank by this file's own record number and sprite numbers.</summary>
    public SpriteBank BankNative(int n) => new(RawNative(n), null, n);

    /// <summary>A bitmap font (records 65–71).</summary>
    public ShellFont Font(int id) => new(Raw(id)) { Record = id };

    /// <summary>A sound (records 72–82) as a RIFF WAVE; the PIFF ones (81, 82) have their magic repaired.</summary>
    public byte[] Wave(int id)
    {
        var w = Raw(id).ToArray();
        if (w[0] == (byte)'P' && w[1] == (byte)'I' && w[2] == (byte)'F' && w[3] == (byte)'F') w[0] = (byte)'R';
        return w;
    }

    /// <summary>Record 83, the credits: lines as the shell splits them on '\n'.</summary>
    public string[] Credits() =>
        System.Text.Encoding.Latin1.GetString(Raw(83)).Replace("\r", "").Split('\n');

    /// <summary>
    /// u32 output length, then LZSS: a 4 KB ring zeroed, written from 0, a
    /// rolling flag byte read least significant bit first: 1 a literal, 0 a
    /// u16 back-reference (low 12 bits the ring position, high 4 the length − 3).
    /// </summary>
    public static byte[] Lzss(ReadOnlySpan<byte> rec)
    {
        int len = BitConverter.ToInt32(rec);
        var src = rec[4..];
        var ring = new byte[4096];
        var o = new byte[len];
        int wp = 0, i = 0, n = 0, flags = 2;
        while (n < len && i < src.Length)
        {
            flags >>= 1;
            if (flags == 1) flags = src[i++] | 0x100;
            if ((flags & 1) != 0)
            {
                byte b = src[i++];
                o[n++] = b; ring[wp] = b; wp = (wp + 1) & 0xfff;
            }
            else
            {
                if (i + 1 >= src.Length) break;
                int v = src[i] | (src[i + 1] << 8);
                i += 2;
                int pos = v & 0xfff;
                for (int k = (v >> 12) + 3; k > 0 && n < len; k--)
                {
                    byte b = ring[pos];
                    o[n++] = b; ring[wp] = b; wp = (wp + 1) & 0xfff; pos = (pos + 1) & 0xfff;
                }
            }
        }
        if (n != len) throw new InvalidDataException($"LZSS gave {n} of {len} bytes");
        return o;
    }
}

/// <summary>An 8 bpp single-plane RLE PCX with its 256-colour palette at the end (8-bit RGB).</summary>
public sealed class Pcx
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Colour indices, row by row.</summary>
    public byte[] Pixels { get; }
    /// <summary>768 bytes, RGB.</summary>
    public byte[] Palette { get; }

    private Pcx(int w, int h, byte[] px, byte[] pal) { Width = w; Height = h; Pixels = px; Palette = pal; }

    /// <summary>A picture made here (the port's own screens): colour indices row by row, and the palette they are in.</summary>
    public static Pcx Create(int w, int h, byte[] pixels, byte[] palette) => new(w, h, pixels, palette);

    /// <summary>640x480 of colour 0, in a palette of black and white ramps: a screen the install does not have.</summary>
    public static Pcx Black => new(640, 480, new byte[640 * 480], new byte[768]);

    public static Pcx Parse(byte[] d)
    {
        if (d[0] != 0x0a || d[3] != 8 || d[65] != 1) throw new InvalidDataException("not an 8-bit single-plane PCX");
        int x0 = BitConverter.ToUInt16(d, 4), y0 = BitConverter.ToUInt16(d, 6), x1 = BitConverter.ToUInt16(d, 8), y1 = BitConverter.ToUInt16(d, 10);
        int w = x1 - x0 + 1, h = y1 - y0 + 1, stride = BitConverter.ToUInt16(d, 66);
        if (d[^769] != 0x0c) throw new InvalidDataException("PCX without a 256-colour palette");
        var pal = d.AsSpan(d.Length - 768, 768).ToArray();
        var raw = new byte[stride * h];
        int i = 128, end = d.Length - 769, n = 0;
        while (n < raw.Length && i < end)
        {
            byte b = d[i++];
            if (b >= 0xc0) { int c = b & 0x3f; byte v = d[i++]; for (; c > 0 && n < raw.Length; c--) raw[n++] = v; }
            else raw[n++] = b;
        }
        var px = new byte[w * h];
        for (int y = 0; y < h; y++) Array.Copy(raw, y * stride, px, y * w, w);
        return new Pcx(w, h, px, pal);
    }
}

/// <summary>A sprite bank ("1.10"): u32 count, then (offset, size) pairs from the record's start.</summary>
public sealed class SpriteBank
{
    private readonly Sprite?[] _items;
    private readonly int[]? _remap;

    public static SpriteBank Empty => new(Array.Empty<Sprite?>(), null);
    private SpriteBank(Sprite?[] items, int[]? remap) { _items = items; _remap = remap; }

    /// <summary><paramref name="remap"/>: for a Nitro bank, the sprite each base number stands for (−1: none).</summary>
    public SpriteBank(ReadOnlySpan<byte> rec, int[]? remap = null, int record = 0)
    {
        _remap = remap;
        int count = BitConverter.ToInt32(rec[4..]);
        if (count <= 0 || 8 + 8 * count > rec.Length) throw new InvalidDataException($"implausible bank count {count}");
        var offs = new int[count + 1];
        for (int i = 0; i < count; i++) offs[i] = BitConverter.ToInt32(rec[(8 + 8 * i)..]);
        offs[count] = rec.Length;
        _items = new Sprite?[count];
        for (int i = 0; i < count; i++)
        {
            try { _items[i] = Sprite.Decode(rec[offs[i]..offs[i + 1]]); _items[i]!.Bank = record; _items[i]!.Index = i; }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        }
    }

    public int Count => _remap?.Length ?? _items.Length;
    public Sprite? this[int i]
    {
        get
        {
            if (_remap is not null) i = i >= 0 && i < _remap.Length ? _remap[i] : -1;
            return i >= 0 && i < _items.Length ? _items[i] : null;
        }
    }
    /// <summary>The bank's own sprite <paramref name="i"/>, whatever the remap.</summary>
    public Sprite? Native(int i) => i >= 0 && i < _items.Length ? _items[i] : null;
    public int NativeCount => _items.Length;
}

/// <summary>One bank item: indices with a transparency mask.</summary>
public sealed class Sprite
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>The header rectangle's origin (+8): the shell adds it to the position it is given.</summary>
    public int X0 { get; }
    public int Y0 { get; }
    public byte[] Pixels { get; }
    /// <summary>The database record of the bank it came from (the install's own numbering) and its own index there; 0 when not known.</summary>
    public int Bank { get; set; }
    public int Index { get; set; }
    /// <summary>True where the pixel is drawn.</summary>
    public bool[] Opaque { get; }

    private Sprite(int w, int h, int x0, int y0, byte[] px, bool[] op) { Width = w; Height = h; X0 = x0; Y0 = y0; Pixels = px; Opaque = op; }

    /// <summary>
    /// The header's rectangle at +8 (x0, y0, x1, y1, inclusive), then the
    /// RLE at +0x18 (the original's RLE sprite draw): per control byte,
    /// count = c >> 1, flag = c &amp; 1; (0,0) ends the row, (0,1) skips the next
    /// byte's count of transparent pixels, (n,0) repeats the next byte n
    /// times, (n,1) copies n literal bytes.
    /// </summary>
    public static Sprite Decode(ReadOnlySpan<byte> b)
    {
        int x0 = BitConverter.ToInt32(b[8..]), y0 = BitConverter.ToInt32(b[12..]), x1 = BitConverter.ToInt32(b[16..]), y1 = BitConverter.ToInt32(b[20..]);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w <= 0 || h <= 0 || w > 2048 || h > 2048) throw new InvalidDataException("implausible sprite");
        var px = new byte[w * h];
        var op = new bool[w * h];
        int i = 0x18, x = 0, y = 0;
        void Put(byte v) { if (x < w && y < h) { px[y * w + x] = v; op[y * w + x] = true; } x++; }
        while (i < b.Length && y < h)
        {
            int c = b[i++], count = c >> 1, flag = c & 1;
            if (count == 0 && flag == 0) { y++; x = 0; }
            else if (count == 0) x += b[i++];
            else if (flag == 0) { byte v = b[i++]; for (int k = 0; k < count; k++) Put(v); }
            else for (int k = 0; k < count; k++) Put(b[i++]);
        }
        return new Sprite(w, h, x0, y0, px, op);
    }
}

/// <summary>A bitmap font: u32 count, height, key, glyph offsets; each glyph u32 width then width·height indices, key transparent. The glyph index is the character byte.</summary>
public sealed class ShellFont
{
    public int Height { get; }
    public int Key { get; }
    /// <summary>The DATABASE.MW2 record it came from (0 when built from bytes alone).</summary>
    public int Record { get; init; }
    private readonly (int Width, byte[] Px)?[] _g;

    public ShellFont(ReadOnlySpan<byte> rec)
    {
        int count = BitConverter.ToInt32(rec[4..]);
        Height = BitConverter.ToInt32(rec[8..]);
        Key = BitConverter.ToInt32(rec[12..]);
        if (count <= 0 || count > 4096 || Height <= 0 || Height > 256) throw new InvalidDataException("implausible font");
        _g = new (int, byte[])?[count];
        for (int i = 0; i < count; i++)
        {
            int o = BitConverter.ToInt32(rec[(16 + 4 * i)..]);
            if (o <= 0 || o + 4 > rec.Length) continue;
            int w = BitConverter.ToInt32(rec[o..]);
            if (w <= 0 || o + 4 + w * Height > rec.Length) continue;
            _g[i] = (w, rec.Slice(o + 4, w * Height).ToArray());
        }
    }

    public (int Width, byte[] Px)? Glyph(char c) => c < _g.Length ? _g[c] : null;

    public int Measure(string s) => s.Sum(c => Glyph(c)?.Width ?? 0);

    /// <summary>The number of glyph slots (the highest character code + 1).</summary>
    public int Count => _g.Length;

    private int _ink = int.MinValue;

    /// <summary>
    /// The glyph value that is solid ink: the highest non-key value over the
    /// printable glyphs, or −1 for an empty font. An antialiased face (record
    /// 70) carries a ramp whose top is the stroke's interior and whose lower
    /// values are its edges fading out, so the top is the face's colour.
    /// </summary>
    public int InkValue
    {
        get
        {
            if (_ink != int.MinValue) return _ink;
            int best = -1;
            for (int c = 32; c <= 126 && c < _g.Length; c++)
                if (_g[c] is { } g)
                    foreach (byte v in g.Px)
                        if (v != Key && v > best) best = v;
            return _ink = best;
        }
    }
}
