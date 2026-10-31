using System.Security.Cryptography;
using System.Text;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Net;
using I76.Net.Anet;
using I76.Server;
using I76.Sim.Missions;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// A scratch install in the temp directory: links to a real install's top-level entries, except miss16 and addon, which
/// are real folders of links to its files, so that files of our own (a synthetic custom map) can go beside them. Nothing
/// is written through a link; disposing removes links and our files one by one, never recursively.
/// </summary>
public sealed class ScratchMapInstall : IDisposable
{
    public string Root { get; }
    public string Addon => Path.Combine(Root, "addon");
    private readonly List<string> _own = [];

    public ScratchMapInstall(string install, string tag)
    {
        Root = Path.Combine(Path.GetTempPath(), $"i26-mapxfer-{tag}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        foreach (var e in Directory.EnumerateFileSystemEntries(install))
        {
            var name = Path.GetFileName(e);
            if (name.Equals("miss16", StringComparison.OrdinalIgnoreCase) || name.Equals("addon", StringComparison.OrdinalIgnoreCase)) continue;
            var dst = Path.Combine(Root, name);
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(dst, e); else File.CreateSymbolicLink(dst, e);
        }
        foreach (var m in new[] { "miss16", "addon" })
        {
            var dir = Path.Combine(Root, m);
            Directory.CreateDirectory(dir);
            var own = Directory.EnumerateDirectories(install).FirstOrDefault(d => Path.GetFileName(d).Equals(m, StringComparison.OrdinalIgnoreCase));
            if (own is not null) foreach (var f in Directory.EnumerateFiles(own)) File.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(f)), f);
        }
    }

    /// <summary>A file of our own in addon\ (or in miss16\).</summary>
    public string Put(string name, byte[] data, string folder = "addon")
    {
        var p = Path.Combine(Root, folder, name);
        File.WriteAllBytes(p, data);
        _own.Add(p);
        return p;
    }

    /// <summary>
    /// A custom map made from a stock arena: its mission file under a new name with TDEF/ZONE naming <c>stem.ter</c> and
    /// WRLD's text naming <c>stem.npt</c>, a copy of its terrain under that name, and a small .npt. Returns the mission's name.
    /// </summary>
    public string SyntheticMap(string stockMission, string stem, string ext = ".lvl", byte salt = 0)
    {
        var gi = new GameInstall(Root) { MapDir = null };
        var src = gi.FindMission(stockMission)!;
        var data = File.ReadAllBytes(src);
        var msn = new MissionFile(src, data);
        var ter = File.ReadAllBytes(MissionWorld.FindTerrain(gi, msn)!);
        Patch(data, "TDEF", "ZONE", 1, stem + ".ter");
        Patch(data, "WDEF", "WRLD", 0x45, stem + ".npt");
        // a different map of the same name: one height changed
        if (salt != 0) ter[2] ^= salt;
        Put(stem + ext, data);
        Put(stem + ".ter", ter);
        Put(stem + ".npt", Encoding.Latin1.GetBytes($"{stem}\r\nA synthetic custom map for the transfer tests.\r\n"));
        return stem + ext;
    }

    private static void Patch(byte[] data, string chunk, string sub, int at, string name)
    {
        var f = new Bwd2File(data);
        foreach (var c in f.Chunks.Where(c => c.Tag == chunk))
            foreach (var sc in MissionFile.SubChunks(data.AsSpan(c.PayloadOffset, c.PayloadSize)))
                if (sc.Tag == sub)
                {
                    int p = c.PayloadOffset + sc.PayloadOffset + at;
                    Array.Clear(data, p, 13);
                    Encoding.Latin1.GetBytes(name).CopyTo(data, p);
                    return;
                }
        throw new InvalidDataException($"no {chunk}/{sub}");
    }

    public void Dispose()
    {
        foreach (var p in _own) File.Delete(p);
        foreach (var e in Directory.EnumerateFileSystemEntries(Root)) Remove(e, 1);
        Directory.Delete(Root);
    }

    private static void Remove(string e, int depth)
    {
        if (new FileInfo(e).LinkTarget is not null) { if (Directory.Exists(e)) Directory.Delete(e); else File.Delete(e); return; }
        if (!Directory.Exists(e) || depth == 0) return;
        foreach (var c in Directory.EnumerateFileSystemEntries(e)) Remove(c, depth - 1);
        Directory.Delete(e);
    }
}

/// <summary>A scratch downloaded-maps folder (a user dir's downloaded_maps), removed file by file.</summary>
public sealed class ScratchCache : IDisposable
{
    public MapCache Cache { get; }
    public ScratchCache() => Cache = new MapCache(Path.Combine(Path.GetTempPath(), $"i26-mapcache-{Environment.ProcessId}-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (!Directory.Exists(Cache.Root)) return;
        foreach (var d in Directory.EnumerateDirectories(Cache.Root))
        {
            foreach (var f in Directory.EnumerateFiles(d)) File.Delete(f);
            Directory.Delete(d);
        }
        foreach (var f in Directory.EnumerateFiles(Cache.Root)) File.Delete(f);
        Directory.Delete(Cache.Root);
    }
}

/// <summary>Custom maps sent from the host (docs/multiplayer.md "Custom maps").</summary>
public class MapTransferTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("canyonlx.rac", true)]
    [InlineData("BZ!.LVL", true)]
    [InlineData("ofjuicy_8.cbt", true)]
    [InlineData("Dp4n.ter", true)]
    [InlineData("m05.npt", true)]
    [InlineData("../m05.ter", false)]
    [InlineData("..", false)]
    [InlineData("a/b.ter", false)]
    [InlineData("a\\b.ter", false)]
    [InlineData("C:x.ter", false)]
    [InlineData(".hidden.ter", false)]
    [InlineData("map.exe", false)]
    [InlineData("map.dll", false)]
    [InlineData("map", false)]
    [InlineData("map.ter ", false)]
    [InlineData("con.ter", false)]
    [InlineData("LPT1.npt", false)]
    [InlineData("con.x.ter", false)]
    [InlineData("Destruxion TX.rac", true)]
    [InlineData("Day Light (Two), v1.1.rac", true)]
    [InlineData("Bob's [Big] Race+.cbt", true)]
    [InlineData("averyveryveryveryverylongmapname1.ter", true)]
    [InlineData("averyveryveryveryverylongmapname1averyveryveryveryverylongmapname1.ter", false)]
    [InlineData(" lead.ter", false)]
    [InlineData("destruct .rac", true)]
    [InlineData("a..b.ter", false)]
    [InlineData("what?.ter", false)]
    [InlineData("a:b.ter", false)]
    [InlineData("a*b.ter", false)]
    [InlineData("a\"b.ter", false)]
    [InlineData("a|b.ter", false)]
    [InlineData("café.ter", false)]
    [InlineData("", false)]
    public void MapFileNamesAreChecked(string name, bool ok) => Assert.Equal(ok, MapFileSet.IsSafeName(name));

    [Theory]
    [InlineData("m05.msn", true)]
    [InlineData("M05.TER", true)]
    [InlineData("t01_16.pcf", true)]
    [InlineData("n40.cf2", true)]
    [InlineData("N65.TER", true)]
    [InlineData("p19.msn", true)]
    [InlineData("i2ayj_13.map", true)]
    [InlineData("n04.cbt", false)]
    [InlineData("n70.cbt", false)]
    [InlineData("m16.lvl", false)]
    [InlineData("canyonlx.rac", false)]
    [InlineData("zzmap.npt", false)]
    public void StockNamesAreKnown(string name, bool stock) => Assert.Equal(stock, MapFileSet.IsStockName(name));

    [Theory]
    [InlineData("miss16/T01.MSN", true)]
    [InlineData("miss8/m15.msn", true)]
    [InlineData("miss16/p19.msn", true)]
    [InlineData("miss16/N05.CBT", true)]
    [InlineData("miss8/n35.msn", true)]           // the Nitro Pack ships n35 as a .rac and a .msn
    [InlineData("miss16/t01.cbt", false)]         // a community melee version of a campaign map
    [InlineData("miss16/A01.cbt", false)]
    [InlineData("miss16/narrowed.rac", false)]
    [InlineData("miss16/t01.ter", false)]
    [InlineData("addon/n01.cbt", false)]
    public void StockMissionsAreTheShippedFiles(string path, bool stock) =>
        Assert.Equal(stock, MapFileSet.IsStockMission(Path.Combine("/games/i76", path)));

    private static (string, long, byte[]) F(string name, string content) => (name, content.Length, SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void TheHashCoversNamesSizesAndContentInAnyOrder()
    {
        var a = MapFileSet.HashOf("x.cbt", [F("x.cbt", "mission"), F("x.ter", "terrain")]);
        Assert.Equal(32, a.Length);
        // the order files are listed in, and the case of their names, do not matter
        Assert.Equal(a, MapFileSet.HashOf("X.CBT", [F("X.TER", "terrain"), F("x.cbt", "mission")]));
        // a byte, a name or a file more do
        Assert.NotEqual(a, MapFileSet.HashOf("x.cbt", [F("x.cbt", "mission"), F("x.ter", "terrair")]));
        Assert.NotEqual(a, MapFileSet.HashOf("x.cbt", [F("x.cbt", "mission"), F("y.ter", "terrain")]));
        Assert.NotEqual(a, MapFileSet.HashOf("x.cbt", [F("x.cbt", "mission"), F("x.ter", "terrain"), F("x.npt", "")]));
        Assert.NotEqual(a, MapFileSet.HashOf("y.cbt", [F("x.cbt", "mission"), F("x.ter", "terrain")]));
    }

    private static MapOffer OfferOf(string mission, params (string Name, byte[] Data)[] files)
    {
        var list = files.Select(f => new MapOfferFile(f.Name, (uint)f.Data.Length, SHA256.HashData(f.Data))).ToList();
        return new MapOffer(mission, MapFileSet.HashOf(mission, list.Select(f => (f.Name, (long)f.Size, f.Sha256))), true, "", list);
    }

    private static T RoundTrip<T>(Action<NetWriter> write, NetReaderFunc<T> read, MsgType type)
    {
        var w = new NetWriter();
        write(w);
        var r = new NetReader(w.Span);
        Assert.Equal(type, (MsgType)r.U8());
        var v = read(ref r);
        Assert.Equal(0, r.Remaining);
        return v;
    }

    private delegate T NetReaderFunc<T>(ref NetReader r);

    [Fact]
    public void TheMessagesRoundTrip()
    {
        var rnd = new Random(7);
        byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        var offer = OfferOf("zz!map.lvl", ("zz!map.lvl", Bytes(1000)), ("zz!map.ter", Bytes(70000)), ("zz!map.npt", []));
        var back = RoundTrip(offer.Write, MapOffer.Read, MsgType.MapOffer);
        Assert.Equal(offer.Mission, back.Mission);
        Assert.Equal(offer.Hash, back.Hash);
        Assert.Equal(offer.Files.Select(f => (f.Name, f.Size, Convert.ToHexString(f.Sha256))), back.Files.Select(f => (f.Name, f.Size, Convert.ToHexString(f.Sha256))));
        Assert.Equal(71000, back.TotalSize);
        var notShared = offer with { Shared = false, Reason = "the host does not share maps" };
        var ns = RoundTrip(notShared.Write, MapOffer.Read, MsgType.MapOffer);
        Assert.False(ns.Shared);
        Assert.Equal("the host does not share maps", ns.Reason);

        var q = new MapRequest(offer.Hash, [(1, 32768u), (2, 0u)]);
        var qb = RoundTrip(q.Write, MapRequest.Read, MsgType.MapRequest);
        Assert.Equal(q.From, qb.From);
        Assert.Equal(q.Hash, qb.Hash);

        var piece = Bytes(MapTransferLimits.ChunkSize);
        var c = RoundTrip(w => MapChunk.Write(w, 1, 16384, piece), MapChunk.Read, MsgType.MapChunk);
        Assert.Equal((1, 16384u), (c.File, c.Offset));
        Assert.Equal(piece, c.Data.ToArray());

        // the accept carries the hash; a stock arena's carries none
        var acc = new JoinAccept(1, 5, "zz!map.lvl", 100, "vppirna1.vcf", false, 0, 0, 5, [], [], MapHash: offer.Hash);
        Assert.Equal(offer.Hash, RoundTrip(acc.Write, JoinAccept.Read, MsgType.JoinAccept).MapHash);
        Assert.Null(RoundTrip((acc with { MapHash = null }).Write, JoinAccept.Read, MsgType.JoinAccept).MapHash);

        // the session's answer and the anet listing say there is a custom map, and whether it is shared
        var info = new SessionInfo(1, Protocol.Version, "g", "zz!map.lvl", 0, 4, false, false, 0, 0, false, [], MapHash: offer.Hash, MapShared: true, MapSize: 71000);
        var w2 = new NetWriter();
        info.Write(w2);
        var ib = SessionInfo.TryRead(w2.Span)!;
        Assert.Equal((true, true, 71000u), (ib.CustomMap, ib.MapShared, ib.MapSize));
        Assert.Equal(offer.Hash, ib.MapHash);
        var tag = new I26SessionTag(Protocol.Version, true, false, false, false, MeleeKind.Race, 2, MeleeGoal.Laps, "canyonlx.RAC", true, true);
        Assert.Equal(tag, I26SessionTag.Read(tag.Write()));
        var plain = tag with { Arena = "m05", CustomMap = false, MapShared = false, Goal = MeleeGoal.Kills };
        Assert.Equal(plain, I26SessionTag.Read(plain.Write()));
        Assert.Equal("zzzzzzzz", I26SessionTag.Read((tag with { Arena = "zzzzzzzz.xyz" }).Write())!.Arena);
    }

    [Fact]
    public void OffersAClientMustNotActOnAreRefused()
    {
        var good = OfferOf("a.cbt", ("a.cbt", [1, 2, 3]), ("a.ter", [4]));
        MapOffer Read(MapOffer o)
        {
            var w = new NetWriter();
            o.Write(w);
            var r = new NetReader(w.Span);
            r.U8();
            return MapOffer.Read(ref r);
        }
        Read(good);
        // a path, a name twice, more files than allowed, a list the hash does not cover, a mission not among its files
        Assert.Throws<WireException>(() => Read(good with { Files = [good.Files[0], good.Files[1] with { Name = "../a.ter" }] }));
        Assert.Throws<WireException>(() => Read(OfferOf("a.cbt", ("a.cbt", [1]), ("A.CBT", [2]))));
        Assert.Throws<WireException>(() => Read(OfferOf("a.cbt", [.. Enumerable.Range(0, 17).Select(i => ($"a{i}.ter", new byte[] { 1 })), ("a.cbt", [1])])));
        Assert.Throws<WireException>(() => Read(good with { Files = [good.Files[0], good.Files[1] with { Size = 100 }] }));
        Assert.Throws<WireException>(() => Read(OfferOf("b.cbt", ("a.cbt", [1]), ("a.ter", [4]))));
        Assert.Throws<WireException>(() => Read(good with { Mission = "a.exe" }));
    }

    [Fact]
    public void ADownloadIsPutTogetherCheckedAndTakenUpAgain()
    {
        using var scratch = new ScratchCache();
        var cache = scratch.Cache;
        var rnd = new Random(11);
        var big = new byte[MapTransferLimits.ChunkSize * 3 + 123];
        rnd.NextBytes(big);
        var small = Encoding.Latin1.GetBytes("text");
        var offer = OfferOf("x.cbt", ("x.cbt", small), ("x.ter", big), ("x.npt", []));
        Assert.Null(cache.Find(offer));
        var d = new MapDownload(cache, offer);
        Assert.Equal([(0, 0u), (1, 0u)], d.Request().From);
        d.Add(new MapChunk(0, 0, small));
        // out of place, past the end, another file: refused
        Assert.Throws<WireException>(() => d.Add(new MapChunk(1, 100, big.AsMemory(100, 10))));
        Assert.Throws<WireException>(() => d.Add(new MapChunk(0, 4, new byte[] { 1 })));
        Assert.Throws<WireException>(() => d.Add(new MapChunk(3, 0, new byte[] { 1 })));
        d.Add(new MapChunk(1, 0, big.AsMemory(0, MapTransferLimits.ChunkSize)));
        Assert.Equal(small.Length + MapTransferLimits.ChunkSize, d.Received);
        d.Dispose();
        // cut off here: a new download (a reconnect) asks only for the rest
        var again = new MapDownload(cache, offer);
        Assert.Equal([(1, (uint)MapTransferLimits.ChunkSize)], again.Request().From);
        for (int o = MapTransferLimits.ChunkSize; o < big.Length; o += MapTransferLimits.ChunkSize)
            again.Add(new MapChunk(1, (uint)o, big.AsMemory(o, Math.Min(MapTransferLimits.ChunkSize, big.Length - o))));
        Assert.True(again.Complete);
        Assert.Equal(cache.Dir(offer.Hash), again.Done);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(again.Done!, "x.ter")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(again.Done!, "x.npt")));
        Assert.False(Directory.Exists(cache.PartDir(offer.Hash)));
        Assert.Equal(again.Done, cache.Find(offer));
        again.Dispose();

        // bytes that are not the host's fail the check, and nothing is used
        var bad = OfferOf("y.cbt", ("y.cbt", small));
        var d2 = new MapDownload(cache, bad with { Files = [bad.Files[0]] });
        Assert.Throws<InvalidDataException>(() => d2.Add(new MapChunk(0, 0, Encoding.Latin1.GetBytes("txet"))));
        Assert.Null(cache.Find(bad));
        d2.Dispose();
        Directory.Delete(cache.PartDir(bad.Hash));
        // a cached copy whose file was changed afterwards is not taken
        File.WriteAllBytes(Path.Combine(cache.Dir(offer.Hash), "x.cbt"), Encoding.Latin1.GetBytes("tex!"));
        Assert.Null(cache.Find(offer));
    }

    [Fact]
    public void BigMapsAreNotSent()
    {
        var set = new MapFileSet("x.cbt", [new MapFile("x.cbt", "", 1000, new byte[32]), new MapFile("x.ter", "", 20L << 20, new byte[32])], new byte[32]);
        Assert.NotNull(set.TooBig(MapFileSet.DefaultMaxBytes));
        Assert.Null(set.TooBig(32L << 20));
        var many = set with { Files = [.. Enumerable.Range(0, 17).Select(i => new MapFile($"f{i}.npt", "", 1, new byte[32]))] };
        Assert.Contains("files", many.TooBig(MapFileSet.DefaultMaxBytes));
    }

    // ---- on an install (skipped without I76_INSTALL)

    [Fact]
    public void TheFileSetIsTheMapsOwnFiles()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var s = new ScratchMapInstall(install, "set");
        var name = s.SyntheticMap("m01", "zzmap");
        var gi = new GameInstall(s.Root) { MapDir = null };
        var set = MapFileSet.Resolve(gi, name)!;
        Assert.NotNull(set);
        Assert.Equal(["zzmap.lvl", "zzmap.npt", "zzmap.ter"], set.Files.Select(f => f.Name).Order());
        Assert.All(set.Files, f => Assert.StartsWith(s.Addon, f.Path));
        // stock maps are no custom maps: nothing to send
        Assert.Null(MapFileSet.Resolve(gi, "m01"));
        Assert.Null(MapFileSet.Resolve(gi, "nosuchmap"));
        // the same files elsewhere give the same hash
        using var s2 = new ScratchMapInstall(install, "set2");
        s2.SyntheticMap("m01", "zzmap");
        Assert.Equal(set.Hash, MapFileSet.Resolve(new GameInstall(s2.Root) { MapDir = null }, "zzmap")!.Hash);
        // a map naming a stock terrain sends its mission file alone
        s2.Put("zzown.lvl", File.ReadAllBytes(gi.FindMission("m01")!));
        var own = MapFileSet.Resolve(new GameInstall(s2.Root) { MapDir = null }, "zzown.lvl")!;
        Assert.Equal(["zzown.lvl"], own.Files.Select(f => f.Name));
    }

    /// <summary>
    /// The end to end: a server whose install has a custom map, and clients whose installs do not (or have another map of
    /// that name). The map comes down on channel 1, is checked, lands in the client's cache folder for its hash, and the
    /// client's arena builds from there; two clients at once; a client cut off halfway takes the download up where it
    /// stopped; a server that does not share says so.
    /// </summary>
    [Fact]
    public void AClientWithoutTheMapDownloadsItAndPlaysFromTheCache()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var host = new ScratchMapInstall(install, "host");
        var arena = host.SyntheticMap("m01", "zzmap");
        using var other = new ScratchMapInstall(install, "other");
        other.SyntheticMap("m01", "zzmap", salt: 0x5a);       // the same name, another terrain
        using var bare = new ScratchMapInstall(install, "bare");
        using var cacheA = new ScratchCache();
        using var cacheB = new ScratchCache();

        using var server = new NetMeleeServer(new ServerOptions { Install = host.Root, Arena = arena, Port = 0, MaxPlayers = 4, MapBytesPerSecond = 64 * 1024 });
        server.Log += m => output.WriteLine($"[server] {m}");
        output.WriteLine(server.MapNote);
        Assert.NotNull(server.Host.MapOffer);
        server.Start();
        var hostHash = server.Host.MapOffer!.Hash;

        // A: the map not installed at all; B: installed differently. Both download, at once.
        using var a = new MeleeClient();
        using var b = new MeleeClient();
        a.Connect("127.0.0.1", server.Port, "A", "vppirna1.vcf");
        b.Connect("127.0.0.1", server.Port, "B", "vppirna1.vcf");
        var choices = new Dictionary<MeleeClient, MapChoice>();
        void Answer(MeleeClient c, GameInstall gi, MapCache cache)
        {
            if (c.State != ClientState.MapOffered || choices.ContainsKey(c)) return;
            choices[c] = cache.Choose(gi, c.Offer!, MapFileSet.DefaultMaxBytes, out var dir);
            if (choices[c] == MapChoice.Download) c.DownloadMap(cache); else c.ReadyMap(dir);
        }
        var giA = new GameInstall(bare.Root) { MapDir = null };
        var giB = new GameInstall(other.Root) { MapDir = null };
        bool sawBoth = false;
        for (int t = 0; t < 1500 && !(a.State == ClientState.Joined && b.State == ClientState.Joined); t++)
        {
            a.Poll(); b.Poll();
            Answer(a, giA, cacheA.Cache);
            Answer(b, giB, cacheB.Cache);
            sawBoth |= a.State == ClientState.Downloading && b.State == ClientState.Downloading;
            Thread.Sleep(10);
        }
        Assert.Equal(ClientState.Joined, a.State);
        Assert.Equal(ClientState.Joined, b.State);
        Assert.Equal(MapChoice.Download, choices[a]);
        Assert.Equal(MapChoice.Download, choices[b]);
        Assert.True(sawBoth, "the two downloads ran side by side");
        Assert.Equal(cacheA.Cache.Dir(hostHash), a.MapDir);
        Assert.Equal(hostHash, a.Accept!.MapHash);
        Assert.Equal(2, server.MapDownloads);

        // the client builds the arena from its cache: the host's files, not its own (B's addon has another zzmap)
        foreach (var (c, root) in new[] { (a, bare.Root), (b, other.Root) })
        {
            var gi = new GameInstall(root) { MapDir = c.MapDir };
            var path = gi.FindMission(c.Accept!.Arena)!;
            Assert.StartsWith(c.MapDir!, path);
            Assert.Null(c.Accept.MapMismatch(gi));
            var vfs = new AssetVfs(gi);
            var asm = new ModelAssembler(vfs);
            var world = new MissionWorld(vfs, asm, path);
            Assert.StartsWith(c.MapDir!, world.TerrainPath);
            var setup = MissionSetup.Build(vfs, world, asm, melee: [], meleeRules: new MeleeRules { Network = true });
            var mirror = new ClientWorld(setup, c.Accept);
            Assert.Equal(c.Accept.Cars.Count, mirror.Cars.Count);
            // without the downloaded folder, B's own (different) copy fails the fingerprint
            if (c == b) Assert.NotNull(c.Accept.MapMismatch(new GameInstall(root) { MapDir = null }));
        }

        // a second visit finds it in the cache, without a download
        using var a2 = new MeleeClient();
        a2.Connect("127.0.0.1", server.Port, "A2", "vppirna1.vcf");
        MapChoice? second = null;
        for (int t = 0; t < 500 && a2.State != ClientState.Joined; t++)
        {
            a2.Poll();
            if (a2.State == ClientState.MapOffered && second is null)
            {
                second = cacheA.Cache.Choose(giA, a2.Offer!, MapFileSet.DefaultMaxBytes, out var dir);
                a2.ReadyMap(dir);
            }
            Thread.Sleep(10);
        }
        Assert.Equal(MapChoice.Cached, second);
        Assert.Equal(ClientState.Joined, a2.State);
        // and the host's own install, with the very files, needs none either
        Assert.Equal(MapChoice.Installed, cacheB.Cache.Choose(new GameInstall(host.Root), server.Host.MapOffer!, MapFileSet.DefaultMaxBytes, out _));
    }

    [Fact]
    public void ADownloadCutOffIsTakenUpWhereItStopped()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var host = new ScratchMapInstall(install, "host");
        var arena = host.SyntheticMap("m01", "zzmap");
        using var bare = new ScratchMapInstall(install, "bare");
        using var cache = new ScratchCache();
        // slow, so that it can be cut off halfway
        using var server = new NetMeleeServer(new ServerOptions { Install = host.Root, Arena = arena, Port = 0, MapBytesPerSecond = 32 * 1024 });
        server.Start();
        var gi = new GameInstall(bare.Root) { MapDir = null };
        long total = server.Host.MapOffer!.TotalSize;
        Assert.True(total > 4 * MapTransferLimits.ChunkSize, $"the map is {total} bytes");

        using (var c = new MeleeClient())
        {
            c.Connect("127.0.0.1", server.Port, "cut", "vppirna1.vcf");
            for (int t = 0; t < 1000 && (c.Download?.Received ?? 0) < 2 * MapTransferLimits.ChunkSize; t++)
            {
                c.Poll();
                if (c.State == ClientState.MapOffered) c.DownloadMap(cache.Cache);
                Thread.Sleep(10);
            }
            Assert.Equal(ClientState.Downloading, c.State);
            c.Fail("cancelled");
            Assert.Equal("cancelled", c.Reason);
        }
        for (int t = 0; t < 100 && server.WaitingCount > 0; t++) Thread.Sleep(10);
        Assert.Equal(0, server.WaitingCount);
        Assert.Equal(0, server.Players);

        using var again = new MeleeClient();
        again.Connect("127.0.0.1", server.Port, "again", "vppirna1.vcf");
        long resumedAt = -1;
        for (int t = 0; t < 2000 && again.State != ClientState.Joined; t++)
        {
            again.Poll();
            if (again.State == ClientState.MapOffered)
            {
                Assert.Equal(MapChoice.Download, cache.Cache.Choose(gi, again.Offer!, MapFileSet.DefaultMaxBytes, out _));
                again.DownloadMap(cache.Cache);
                resumedAt = again.Download!.Received;
            }
            Thread.Sleep(10);
        }
        Assert.Equal(ClientState.Joined, again.State);
        Assert.True(resumedAt >= 2 * MapTransferLimits.ChunkSize, $"taken up at {resumedAt}");
        Assert.NotNull(cache.Cache.Find(again.Offer!));
    }

    [Fact]
    public void AServerThatDoesNotShareSaysSo()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var host = new ScratchMapInstall(install, "host");
        var arena = host.SyntheticMap("m01", "zzmap");
        using var bare = new ScratchMapInstall(install, "bare");
        using var cache = new ScratchCache();
        using var server = new NetMeleeServer(new ServerOptions { Install = host.Root, Arena = arena, Port = 0, ShareMaps = false });
        server.Start();
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "c", "vppirna1.vcf");
        for (int t = 0; t < 300 && c.State == ClientState.Connecting; t++) { c.Poll(); Thread.Sleep(10); }
        Assert.Equal(ClientState.MapOffered, c.State);
        Assert.False(c.Offer!.Shared);
        var gi = new GameInstall(bare.Root) { MapDir = null };
        Assert.Equal(MapChoice.Unavailable, cache.Cache.Choose(gi, c.Offer, MapFileSet.DefaultMaxBytes, out _));
        var why = MapCache.WhyNot(gi, c.Offer, MapFileSet.DefaultMaxBytes);
        Assert.Contains("zzmap.lvl", why);
        Assert.Contains("does not share", why);
        // a client too small for a shared one says that instead
        Assert.Contains("bigger than", MapCache.WhyNot(gi, c.Offer with { Shared = true }, 1024));
        Assert.Equal(MapChoice.Unavailable, cache.Cache.Choose(gi, c.Offer with { Shared = true }, 1024, out _));
        // a request anyway is refused, with the reason
        var w = new NetWriter();
        new MapRequest(c.Offer.Hash, [(0, 0u)]).Write(w);
        c.SendRaw(w.Span, LiteNetLib.DeliveryMethod.ReliableOrdered);
        for (int t = 0; t < 300 && c.State == ClientState.MapOffered; t++) { c.Poll(); Thread.Sleep(10); }
        Assert.Equal(ClientState.Disconnected, c.State);
        Assert.Contains("does not share", c.Reason);
        // a stock arena offers nothing: the accept comes at once, as before
        using var stock = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0 });
        stock.Start();
        Assert.Null(stock.Host.MapOffer);
        using var d = new MeleeClient();
        d.Connect("127.0.0.1", stock.Port, "d", "vppirna1.vcf");
        for (int t = 0; t < 300 && d.State == ClientState.Connecting; t++) { d.Poll(); Thread.Sleep(10); }
        Assert.Equal(ClientState.Joined, d.State);
        Assert.Null(d.Accept!.MapHash);
    }

    /// <summary>A real community map (I76_CUSTOM_MAPS, the Nitro pack, with I76_NITRO_INSTALL): its file set, sent and rebuilt from the cache.</summary>
    [Fact]
    public void ACommunityMapIsSentWithItsTerrain()
    {
        var pack = Environment.GetEnvironmentVariable("I76_CUSTOM_MAPS");
        var nitro = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL");
        if (pack is null || nitro is null || !Directory.Exists(Path.Combine(pack, "miss16")) || !Directory.Exists(nitro)) return;
        using var host = new ScratchMapInstall(nitro, "nhost");
        // canyonlx.rac has its terrain in addon\; linked, as the player would have unzipped them
        string Find(string dir, string name) => Directory.EnumerateFiles(Path.Combine(pack, dir)).First(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        var msn = Find("miss16", "canyonlx.rac");
        var ter = Find("addon", "canyonlx.ter");
        File.CreateSymbolicLink(Path.Combine(host.Root, "miss16", Path.GetFileName(msn)), msn);
        File.CreateSymbolicLink(Path.Combine(host.Root, "addon", Path.GetFileName(ter)), ter);
        var set = MapFileSet.Resolve(new GameInstall(host.Root) { MapDir = null }, "canyonlx.rac")!;
        output.WriteLine(string.Join(", ", set.Files.Select(f => $"{f.Name} {f.Size}")));
        Assert.Contains(set.Files, f => f.Name.Equals("canyonlx.ter", StringComparison.OrdinalIgnoreCase));
        using var bare = new ScratchMapInstall(nitro, "nbare");
        using var cache = new ScratchCache();
        using var server = new NetMeleeServer(new ServerOptions { Install = host.Root, Arena = "canyonlx.rac", Kind = MeleeKind.Race, Port = 0 });
        server.Start();
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "c", "vppirna1.vcf");
        for (int t = 0; t < 1500 && c.State != ClientState.Joined && c.State != ClientState.Disconnected; t++)
        {
            c.Poll();
            if (c.State == ClientState.MapOffered) c.DownloadMap(cache.Cache);
            Thread.Sleep(10);
        }
        Assert.Equal(ClientState.Joined, c.State);
        var gi = new GameInstall(bare.Root) { MapDir = c.MapDir };
        Assert.Null(c.Accept!.MapMismatch(gi));
        var vfs = new AssetVfs(gi);
        var asm = new ModelAssembler(vfs);
        var world = new MissionWorld(vfs, asm, gi.FindMission("canyonlx.rac")!);
        var setup = MissionSetup.Build(vfs, world, asm, melee: [], meleeRules: new MeleeRules { Network = true, Kind = MeleeKind.Race });
        Assert.NotNull(new ClientWorld(setup, c.Accept));
        // the server's own files were links to the pack: nothing was written there
        Assert.Equal(new FileInfo(msn).Length, set.Files.First(f => f.Name.EndsWith(".rac", StringComparison.OrdinalIgnoreCase)).Size);
    }
}
