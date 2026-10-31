using System.Security.Cryptography;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Net;

/// <summary>One file of a map offer: its name, size and SHA-256.</summary>
public sealed record MapOfferFile(string Name, uint Size, byte[] Sha256);

/// <summary>
/// Server → client, before the seat (protocol 11): the arena is a custom map (<see cref="MapFileSet"/>), named by its
/// mission file and identified by the hash of its files. <see cref="Shared"/>: the server sends the files on request;
/// otherwise <see cref="Reason"/> says why not ("the host does not share maps", "the map is too big"). The client answers
/// with <see cref="MsgType.MapReady"/> once it has the map, or <see cref="MsgType.MapRequest"/> for the files.
/// </summary>
public sealed record MapOffer(string Mission, byte[] Hash, bool Shared, string Reason, IReadOnlyList<MapOfferFile> Files)
{
    public long TotalSize => Files.Sum(f => (long)f.Size);
    public string HashHex => Convert.ToHexString(Hash).ToLowerInvariant();

    public static MapOffer Of(MapFileSet set, bool shared, string reason) =>
        new(set.Mission, set.Hash, shared, reason, [.. set.Files.Select(f => new MapOfferFile(f.Name, (uint)f.Size, f.Sha256))]);

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.MapOffer);
        w.Str(Mission);
        w.Bytes(Hash);
        w.Bool(Shared);
        w.Str(Reason.Length > 200 ? Reason[..200] : Reason);
        w.U8((byte)Files.Count);
        foreach (var f in Files) { w.Str(f.Name); w.VarU(f.Size); w.Bytes(f.Sha256); }
    }

    /// <summary>
    /// Reads the body (the type byte read), refusing what a client must never act on: a name that is not a plain map
    /// file name (<see cref="MapFileSet.IsSafeName"/>), two files of one name, more than <see cref="MapFileSet.MaxFiles"/>.
    /// The size cap is the client's (<see cref="MapTransferLimits"/>).
    /// </summary>
    public static MapOffer Read(ref NetReader r)
    {
        string mission = r.Str();
        var hash = r.Bytes(32).ToArray();
        bool shared = r.Bool();
        string reason = r.Str();
        int n = r.U8();
        if (n > MapFileSet.MaxFiles) throw new WireException($"a map of {n} files");
        if (!MapFileSet.IsSafeName(mission)) throw new WireException($"\"{mission}\" is not a map file name this game takes");
        var files = new MapOfferFile[n];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            string name = r.Str();
            uint size = r.VarU();
            var sha = r.Bytes(32).ToArray();
            if (!MapFileSet.IsSafeName(name) || !names.Add(name)) throw new WireException($"\"{name}\" is not a map file name this game takes, or comes twice");
            files[i] = new MapOfferFile(name, size, sha);
        }
        if (shared && !names.Contains(mission)) throw new WireException("the map's files leave out its mission file");
        var offer = new MapOffer(mission, hash, shared, reason, files);
        // the hash covers the list: one that does not is no offer of this map
        if (n > 0 && !offer.Hash.AsSpan().SequenceEqual(MapFileSet.HashOf(mission, files.Select(f => (f.Name, (long)f.Size, f.Sha256))))) throw new WireException("the map's hash does not cover its files");
        return offer;
    }
}

/// <summary>Client → server: the files (by their index in the offer) and the offset to send each from (what the client already has).</summary>
public sealed record MapRequest(byte[] Hash, IReadOnlyList<(int File, uint Offset)> From)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.MapRequest);
        w.Bytes(Hash);
        w.U8((byte)From.Count);
        foreach (var (f, o) in From) { w.U8((byte)f); w.VarU(o); }
    }

    public static MapRequest Read(ref NetReader r)
    {
        var hash = r.Bytes(32).ToArray();
        int n = r.U8();
        if (n > MapFileSet.MaxFiles) throw new WireException($"{n} map files asked for");
        var from = new (int, uint)[n];
        for (int i = 0; i < n; i++) from[i] = (r.U8(), r.VarU());
        return new MapRequest(hash, from);
    }
}

/// <summary>Server → client, reliable ordered on <see cref="MapTransferLimits.Channel"/>: a piece of one file of the map.</summary>
public readonly record struct MapChunk(int File, uint Offset, ReadOnlyMemory<byte> Data)
{
    public static void Write(NetWriter w, int file, uint offset, ReadOnlySpan<byte> data)
    {
        w.U8((byte)MsgType.MapChunk);
        w.U8((byte)file);
        w.VarU(offset);
        w.BlobU16(data);
    }

    public static MapChunk Read(ref NetReader r) => new(r.U8(), r.VarU(), r.BlobU16());
}

/// <summary>The transfer's sizes and rates (docs/multiplayer.md "Custom maps").</summary>
public static class MapTransferLimits
{
    /// <summary>The LiteNetLib channel the map's pieces go on: its own reliable queue, so they never hold up the game's events (channel 0).</summary>
    public const byte Channel = 1;
    /// <summary>The bytes in one piece.</summary>
    public const int ChunkSize = 16 * 1024;
    /// <summary>A server sends one downloading client this many bytes a second by default (--map-rate in KiB/s).</summary>
    public const int DefaultBytesPerSecond = 256 * 1024;
    /// <summary>And all of them together at most this many.</summary>
    public const int DefaultTotalBytesPerSecond = 1024 * 1024;
    /// <summary>LiteNetLib packets waiting in a client's map channel above which the server sends no more pieces (about 90 KB in flight).</summary>
    public const int MaxQueuedPackets = 64;
    /// <summary>How long a server waits for a client to answer an offer (its player deciding) before it gives the seat up, seconds.</summary>
    public const double AnswerSeconds = 120;
}

/// <summary>What a client does about an offer, given its install and its downloaded maps.</summary>
public enum MapChoice
{
    /// <summary>Its install has the very files (the same hash): play from the install.</summary>
    Installed,
    /// <summary>A download of this hash is in the cache, checked: play from it.</summary>
    Cached,
    /// <summary>Not here, or here differently: the server shares it, so it can be downloaded.</summary>
    Download,
    /// <summary>Not here (or different) and the server does not share it, or it is beyond the client's cap: the join cannot go on.</summary>
    Unavailable,
}

/// <summary>
/// The downloaded maps: user://downloaded_maps/&lt;hash&gt;/ in the game, one folder per map's hash holding its files
/// under their own names. Only this folder is ever written (never the game install). A download goes into
/// &lt;hash&gt;.part/ and is renamed once every file has its size and SHA-256.
/// </summary>
public sealed class MapCache(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>The folder for a map of this hash (64 hex digits).</summary>
    public string Dir(byte[] hash) => Path.Combine(Root, Convert.ToHexString(hash).ToLowerInvariant());

    public string PartDir(byte[] hash) => Dir(hash) + ".part";

    /// <summary>The folder of this offer's map if it is here and every file checks out (size and SHA-256), else null.</summary>
    public string? Find(MapOffer offer)
    {
        if (offer.Files.Count == 0) return null;
        var dir = Dir(offer.Hash);
        if (!Directory.Exists(dir)) return null;
        foreach (var f in offer.Files)
        {
            var p = Path.Combine(dir, f.Name);
            if (!File.Exists(p) || new FileInfo(p).Length != f.Size) return null;
            using var s = File.OpenRead(p);
            if (!SHA256.HashData(s).AsSpan().SequenceEqual(f.Sha256)) return null;
        }
        return dir;
    }

    /// <summary>
    /// What to do about an offer: the install's own copy if it is the same map (by hash), else a checked download, else
    /// download it if the server shares it and it is within <paramref name="maxBytes"/>.
    /// </summary>
    public MapChoice Choose(GameInstall install, MapOffer offer, long maxBytes, out string? dir)
    {
        dir = null;
        try
        {
            var mine = MapFileSet.Resolve(new GameInstall(install.Root) { MapDir = null }, offer.Mission);
            if (mine is not null && mine.Hash.AsSpan().SequenceEqual(offer.Hash)) return MapChoice.Installed;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        if (Find(offer) is { } d) { dir = d; return MapChoice.Cached; }
        return offer.Shared && offer.Files.Count > 0 && offer.TotalSize <= maxBytes ? MapChoice.Download : MapChoice.Unavailable;
    }

    /// <summary>Why the client cannot play the offered map (for <see cref="MapChoice.Unavailable"/>), as the player reads it.</summary>
    public static string WhyNot(GameInstall install, MapOffer offer, long maxBytes)
    {
        bool have = install.FindMission(offer.Mission) is not null;
        string what = have ? $"Your copy of the host's map {offer.Mission} differs from the host's" : $"The host's map {offer.Mission} is not installed";
        if (!offer.Shared) return $"{what}, and the host does not share maps{(offer.Reason.Length > 0 ? $" ({offer.Reason})" : "")}.";
        return $"{what}, and at {offer.TotalSize / 1024} KiB it is bigger than the {maxBytes / 1024} KiB you allow.";
    }
}

/// <summary>
/// A map being downloaded: the offer's files filled in from the server's pieces, in order, into the cache's part
/// folder (so a download cut off is taken up where it stopped, within the game and after). Every piece is checked
/// against the offer (its file, its place, no byte past the size); once each file is whole and its SHA-256 matches, the
/// folder is renamed to the map's hash.
/// </summary>
public sealed class MapDownload : IDisposable
{
    public MapOffer Offer { get; }
    public MapCache Cache { get; }
    private readonly string _part;
    private readonly long[] _have;
    private FileStream? _open;
    private int _openIndex = -1;

    /// <summary>Bytes received (including what was already in the part folder), and the whole map's.</summary>
    public long Received => _have.Sum();
    public long Total => Offer.TotalSize;
    public bool Complete => Done is not null;
    /// <summary>The map's folder once it is whole and checked.</summary>
    public string? Done { get; private set; }

    public MapDownload(MapCache cache, MapOffer offer)
    {
        Cache = cache;
        Offer = offer;
        _part = cache.PartDir(offer.Hash);
        Directory.CreateDirectory(_part);
        _have = new long[offer.Files.Count];
        for (int i = 0; i < offer.Files.Count; i++)
        {
            var p = Path.Combine(_part, offer.Files[i].Name);
            _have[i] = File.Exists(p) ? new FileInfo(p).Length : 0;
            // more than the file's size: not this map's; started again
            if (_have[i] > offer.Files[i].Size) { File.Delete(p); _have[i] = 0; }
        }
    }

    /// <summary>The request for what is still missing: each unfinished file from what is already here.</summary>
    public MapRequest Request() =>
        new(Offer.Hash, [.. Enumerable.Range(0, _have.Length).Where(i => _have[i] < Offer.Files[i].Size).Select(i => (i, (uint)_have[i]))]);

    /// <summary>Checks and finishes a download that needs nothing more (every file already in the part folder); false if pieces are still to come.</summary>
    public bool TryFinish()
    {
        if (Complete) return true;
        if (Enumerable.Range(0, _have.Length).Any(i => _have[i] != Offer.Files[i].Size)) return false;
        Finish();
        return true;
    }

    /// <summary>
    /// Takes one piece. Throws <see cref="WireException"/> for one that does not fit the offer (another file, a gap or
    /// an overlap, past the end) and <see cref="InvalidDataException"/> when a whole file fails its SHA-256.
    /// </summary>
    public void Add(MapChunk c)
    {
        if (Complete) return;
        if (c.File < 0 || c.File >= _have.Length) throw new WireException($"map piece of file {c.File}");
        var f = Offer.Files[c.File];
        if (c.Offset != _have[c.File]) throw new WireException($"map piece at {c.Offset} of {f.Name}, expected {_have[c.File]}");
        if (c.Offset + (long)c.Data.Length > f.Size) throw new WireException($"map piece past the end of {f.Name}");
        if (_openIndex != c.File)
        {
            _open?.Dispose();
            _open = new FileStream(Path.Combine(_part, f.Name), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            _openIndex = c.File;
        }
        _open!.Seek(c.Offset, SeekOrigin.Begin);
        _open.Write(c.Data.Span);
        _have[c.File] += c.Data.Length;
        if (_have[c.File] == f.Size) { _open.Dispose(); _open = null; _openIndex = -1; }
        TryFinish();
    }

    private void Finish()
    {
        _open?.Dispose();
        _open = null;
        foreach (var f in Offer.Files)
        {
            var p = Path.Combine(_part, f.Name);
            if (!File.Exists(p)) File.WriteAllBytes(p, []);     // an empty file gets no piece
            using var s = File.OpenRead(p);
            if (!SHA256.HashData(s).AsSpan().SequenceEqual(f.Sha256))
            {
                s.Dispose();
                File.Delete(p);
                throw new InvalidDataException($"the host's {f.Name} failed its check");
            }
        }
        var dir = Cache.Dir(Offer.Hash);
        if (Directory.Exists(dir))
        {
            // a copy finished meanwhile (another game on this machine): this one's files are let go one by one
            foreach (var f in Offer.Files) File.Delete(Path.Combine(_part, f.Name));
            try { Directory.Delete(_part); } catch (IOException) { }
        }
        else Directory.Move(_part, dir);
        Done = Cache.Find(Offer) ?? throw new InvalidDataException("the downloaded map does not check out");
    }

    public void Dispose()
    {
        _open?.Dispose();
        _open = null;
    }
}
