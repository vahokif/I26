using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

public sealed class BakeCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "i76-bakecache-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static List<BakeCache.Blob> Blobs(int seed, int n = 3, int size = 50_000)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(i =>
        {
            var d = new byte[size + i];
            for (int k = 0; k < d.Length; k += 1 + rng.Next(64)) d[k] = (byte)rng.Next(256);   // mostly zero, as road layers are
            return new BakeCache.Blob((long)i << 32 | (uint)(i * 32), 17 + i, d);
        }).ToList();
    }

    private static string KeyOf(params int[] parts)
    {
        using var k = new BakeCache.Key("test");
        foreach (var p in parts) k.Add(p);
        return k.Finish();
    }

    [Fact]
    public void ReadsBackWhatWasWritten()
    {
        var c = new BakeCache(_dir);
        var blobs = Blobs(1);
        Assert.Null(c.Read("roads", KeyOf(1)));
        Assert.True(c.Write("roads", KeyOf(1), blobs));
        var got = c.Read("roads", KeyOf(1));
        Assert.NotNull(got);
        Assert.Equal(blobs.Count, got.Count);
        for (int i = 0; i < blobs.Count; i++)
        {
            Assert.Equal(blobs[i].Tag, got[i].Tag);
            Assert.Equal(blobs[i].Meta, got[i].Meta);
            Assert.Equal(blobs[i].Data, got[i].Data);
        }
    }

    [Fact]
    public void AnyChangeInTheKeyMisses()
    {
        var c = new BakeCache(_dir);
        c.Write("roads", KeyOf(1, 2), Blobs(1));
        Assert.NotNull(c.Read("roads", KeyOf(1, 2)));
        Assert.Null(c.Read("roads", KeyOf(1, 3)));
        Assert.Null(c.Read("roads", KeyOf(2, 1)));
        Assert.Null(c.Read("ground", KeyOf(1, 2)));
        // the key covers lengths, so moving bytes between parts changes it
        using var a = new BakeCache.Key("x"); a.Add<byte>(new byte[] { 1, 2 }).Add<byte>(new byte[] { 3 });
        using var b = new BakeCache.Key("x"); b.Add<byte>(new byte[] { 1 }).Add<byte>(new byte[] { 2, 3 });
        Assert.NotEqual(a.Finish(), b.Finish());
    }

    [Fact]
    public void DamagedOrTruncatedFilesMissAndGo()
    {
        var c = new BakeCache(_dir);
        string key = KeyOf(5);
        c.Write("roads", key, Blobs(5));
        var path = Directory.GetFiles(_dir, "*.bake", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x5a;   // inside a blob: its checksum fails
        File.WriteAllBytes(path, bytes);
        Assert.Null(c.Read("roads", key));
        Assert.False(File.Exists(path));

        c.Write("roads", key, Blobs(5));
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^10]);
        Assert.Null(c.Read("roads", key));
        Assert.False(File.Exists(path));

        // a file renamed to another key is not that key's
        c.Write("roads", key, Blobs(5));
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, KeyOf(6) + ".bake"));
        Assert.Null(c.Read("roads", KeyOf(6)));
    }

    [Fact]
    public void KeepsUnderItsCapLeastRecentlyUsedFirst()
    {
        var probe = new BakeCache(_dir);
        probe.Write("roads", KeyOf(0), Blobs(0, 1, 200_000));
        long one = new FileInfo(Directory.GetFiles(_dir, "*.bake", SearchOption.AllDirectories).Single()).Length;
        Directory.Delete(_dir, true);

        var c = new BakeCache(_dir, cap: one * 3 + one / 2);
        for (int i = 0; i < 3; i++)
        {
            c.Write("roads", KeyOf(i), Blobs(0, 1, 200_000));
            File.SetLastWriteTimeUtc(Directory.GetFiles(_dir, KeyOf(i) + ".bake", SearchOption.AllDirectories).Single(), DateTime.UtcNow.AddMinutes(-10 + i));
        }
        Assert.NotNull(c.Read("roads", KeyOf(0)));   // used: now the most recent
        c.Write("roads", KeyOf(3), Blobs(0, 1, 200_000));
        Assert.NotNull(c.Read("roads", KeyOf(0)));
        Assert.Null(c.Read("roads", KeyOf(1)));       // the least recently used went
        Assert.NotNull(c.Read("roads", KeyOf(2)));
        Assert.NotNull(c.Read("roads", KeyOf(3)));
    }

    [Fact]
    public void AnUnwritableFolderIsOnlyAMiss()
    {
        File.WriteAllText(_dir, "a file where the folder would be");
        try
        {
            var c = new BakeCache(_dir);
            Assert.False(c.Write("roads", KeyOf(1), Blobs(1)));
            Assert.Null(c.Read("roads", KeyOf(1)));
        }
        finally { File.Delete(_dir); }
    }
}
