using System.IO.Compression;
using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// A texture pack read from many threads at once, as a mission's build and the main thread may: a folder, a chain of folders and a
/// zip (whose archive is not thread safe by itself) give every file whole, and a folder is listed once. Synthetic files only.
/// </summary>
public sealed class HdPackThreadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "i26-hdpack-" + Guid.NewGuid().ToString("N"));

    public HdPackThreadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>File i's bytes: its own length and pattern, so a mixed-up or torn read shows.</summary>
    private static byte[] Content(int i)
    {
        var b = new byte[1000 + i * 37 % 5000];
        for (int k = 0; k < b.Length; k++) b[k] = (byte)(i * 31 + k * 7);
        return b;
    }

    private string Folder(string name, IEnumerable<int> files)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        foreach (int i in files) File.WriteAllBytes(Path.Combine(d, $"t{i:000}.dds"), Content(i));
        return d;
    }

    private void Zip(IEnumerable<int> files)
    {
        var p = Path.Combine(_dir, "i76-hd-textures-test.zip");
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("pack.json").Open()) s.Write("""{"name": "test", "version": "1"}"""u8);
            foreach (int i in files)
                using (var s = z.CreateEntry($"tex-world/t{i:000}.dds").Open()) s.Write(Content(i));
        }

    }

    private static void Hammer(HdSource src, int files)
    {
        int bad = 0, reads = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
        {
            var rng = new Random(t);
            for (int n = 0; n < 1500; n++)
            {
                int i = rng.Next(files + 20);   // some names the pack has not got
                bool want = i < files;
                if (src.Exists($"T{i:000}.DDS") != want) Interlocked.Increment(ref bad);
                var b = src.Read($"t{i:000}.dds");
                Interlocked.Increment(ref reads);
                if (want ? b is null || !b.AsSpan().SequenceEqual(Content(i)) : b is not null) Interlocked.Increment(ref bad);
            }
        });
        Assert.Equal(16 * 1500, reads);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void AFolderReadFromManyThreadsGivesEveryFileWhole() => Hammer(new HdFlatDirSource(Folder("one", Enumerable.Range(0, 60))), 60);

    [Fact]
    public void AChainReadFromManyThreadsTakesEachFileFromTheFirstThatHasIt()
    {
        // the 8x over the 4x: the first holds half the files, the second all of them
        using var chain = new HdChainSource([new HdFlatDirSource(Folder("eight", Enumerable.Range(0, 30))), new HdFlatDirSource(Folder("four", Enumerable.Range(0, 60)))]);
        Hammer(chain, 60);
    }

    [Fact]
    public void AZipReadFromManyThreadsGivesEveryFileWhole()
    {
        Zip(Enumerable.Range(0, 60));
        using var zip = HdPackFinder.Newest([_dir], null, "i76-hd-textures*.zip", "tex-world/");
        Assert.NotNull(zip);
        Hammer(zip!, 60);
    }

    [Fact]
    public void AFolderIsListedOnceAndAFileAddedLaterIsNotSeen()
    {
        var d = Folder("listed", Enumerable.Range(0, 3));
        var src = new HdFlatDirSource(d);
        Assert.True(src.Exists("t000.dds"));
        File.WriteAllBytes(Path.Combine(d, "t003.dds"), Content(3));
        // the index made at the first look answers every question after it (a mission asks for a thousand names)
        Assert.False(src.Exists("t003.dds"));
        Assert.Null(src.Read("t003.dds"));
        Assert.Equal(3, src.Count);
    }
}
