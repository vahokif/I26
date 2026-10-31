using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace I76.Formats.Assembly;

/// <summary>
/// A folder of baked results (a mission's road layers, the ground materials) that are slow to make and always come
/// out the same: each file is named by a hash of everything that went into it (the inputs themselves, not their
/// file names, plus a format version and the engine's), so a changed input misses and bakes afresh. Each blob is
/// zlib-packed on its own (its Adler-32 checks it on the way back) and the files are written whole or not at all
/// (a temporary file, then a rename). The folder is kept under <see cref="Cap"/> bytes, the least recently used
/// going first. Any failure reads as a miss.
/// </summary>
public sealed class BakeCache(string dir, long cap = BakeCache.DefaultCap)
{
    public const long DefaultCap = 1L << 30;
    private const uint Magic = 0x4b423649;   // "I6BK"
    private const int FormatVersion = 1;
    public string Dir { get; } = dir;
    public long Cap { get; } = cap;

    /// <summary>One stored piece: a caller's tag (which chunk), a word of its own (an image format) and the bytes.</summary>
    public readonly record struct Blob(long Tag, int Meta, byte[] Data);

    /// <summary>Gathers a key: everything that decides the result, in a fixed order.</summary>
    public sealed class Key : IDisposable
    {
        private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public Key(string kind) => Add(kind);
        public Key Add(ReadOnlySpan<byte> b) { Add(b.Length); _h.AppendData(b); return this; }
        public Key Add<T>(ReadOnlySpan<T> v) where T : unmanaged => Add(MemoryMarshal.AsBytes(v));
        public Key Add<T>(List<T> v) where T : unmanaged => Add<T>(CollectionsMarshal.AsSpan(v));
        public Key Add(string s) => Add(System.Text.Encoding.UTF8.GetBytes(s));
        public Key Add(int v) { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v); _h.AppendData(b); return this; }
        public Key Add(long v) { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, v); _h.AppendData(b); return this; }
        public Key Add(float v) => Add(BitConverter.SingleToInt32Bits(v));
        public Key Add(bool v) => Add(v ? 1 : 0);
        /// <summary>The key's name, hex.</summary>
        public string Finish() => Convert.ToHexString(_h.GetHashAndReset()).ToLowerInvariant();
        public void Dispose() => _h.Dispose();
    }

    private string PathOf(string kind, string key) => Path.Combine(Dir, kind, key + ".bake");

    /// <summary>The blobs stored under the key, or null (none, unreadable or damaged: then the file goes).</summary>
    public List<Blob>? Read(string kind, string key)
    {
        string path = PathOf(kind, key);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var blobs = Unpack(bytes, key);
            if (blobs is null) { TryDelete(path); return null; }
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // most recently used
            return blobs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>
    /// Stores the blobs under the key, then trims the folder. Safe on any thread; failures are ignored. One core
    /// packs them (it runs while the game does), unless <paramref name="parallel"/>.
    /// </summary>
    public bool Write(string kind, string key, IReadOnlyList<Blob> blobs, bool parallel = false)
    {
        string path = PathOf(kind, key);
        string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var packed = new byte[blobs.Count][];
            void Pack(int i)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(blobs[i].Data);
                packed[i] = ms.ToArray();
            }
            if (parallel) Parallel.For(0, blobs.Count, Pack);
            else for (int i = 0; i < blobs.Count; i++) Pack(i);
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new BinaryWriter(f))
            {
                w.Write(Magic); w.Write(FormatVersion); w.Write(key); w.Write(blobs.Count);
                for (int i = 0; i < blobs.Count; i++) { w.Write(blobs[i].Tag); w.Write(blobs[i].Meta); w.Write(blobs[i].Data.Length); w.Write(packed[i].Length); }
                foreach (var p in packed) w.Write(p);
                w.Write(Magic);
            }
            File.Move(tmp, path, overwrite: true);
            Trim();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return false;
        }
    }

    private static List<Blob>? Unpack(byte[] bytes, string key)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        if (bytes.Length < 16 || r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion || r.ReadString() != key) return null;
        int n = r.ReadInt32();
        if (n < 0 || n > 1 << 20) return null;
        var head = new (long Tag, int Meta, int Raw, int Packed)[n];
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            head[i] = (r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (head[i].Raw < 0 || head[i].Packed < 0) return null;
            total += head[i].Packed;
        }
        long start = r.BaseStream.Position;
        if (start + total + 4 != bytes.Length || BitConverter.ToUInt32(bytes, bytes.Length - 4) != Magic) return null;
        var offs = new long[n];
        for (int i = 0, o = 0; i < n; o += head[i].Packed, i++) offs[i] = start + o;
        var blobs = new Blob[n];
        bool bad = false;
        Parallel.For(0, n, i =>
        {
            try
            {
                var data = new byte[head[i].Raw];
                using var z = new ZLibStream(new MemoryStream(bytes, (int)offs[i], head[i].Packed), CompressionMode.Decompress);
                z.ReadExactly(data);
                // nothing left over: the blob is exactly as long as it says
                if (z.ReadByte() != -1) bad = true;
                blobs[i] = new Blob(head[i].Tag, head[i].Meta, data);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException) { bad = true; }
        });
        return bad ? null : [.. blobs];
    }

    /// <summary>Least recently used first, until the folder holds no more than <see cref="Cap"/>; stray temporary files older than an hour go too.</summary>
    public void Trim()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            foreach (var f in files.Where(f => f.Name.EndsWith(".tmp") && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1))) TryDelete(f.FullName);
            var bakes = files.Where(f => f.Name.EndsWith(".bake")).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long sum = bakes.Sum(f => f.Length);
            foreach (var f in bakes)
            {
                if (sum <= Cap) break;
                sum -= f.Length;
                TryDelete(f.FullName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
