using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Editor.Tests;

/// <summary><see cref="MissionWorld.Outside"/> made again after <see cref="MissionWorld.ForgetOutside"/> (the map editor's terrain edits).</summary>
public class OutsideForgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-editor-outside-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private MissionWorld World()
    {
        var (_, files) = NewMap.CreateFlat(Path.Combine(_root, "addon"), "outs1", "Outside One", 2, 10, nitro: true);
        // an empty archive (no entries): the install the map's world is opened in
        var zfs = new byte[ZfsArchive.HeaderSize];
        "ZFSF"u8.CopyTo(zfs);
        BitConverter.GetBytes(1).CopyTo(zfs, 4); BitConverter.GetBytes(16).CopyTo(zfs, 8); BitConverter.GetBytes(100).CopyTo(zfs, 12);
        File.WriteAllBytes(Path.Combine(_root, "I76.ZFS"), zfs);
        var vfs = new AssetVfs(new GameInstall(_root));
        return new MissionWorld(vfs, new ModelAssembler(vfs), files.First(f => f.EndsWith(".cbt")));
    }

    [Fact]
    public void ForgettingTheOutsideGroundMakesItAgainFromTheEditedTerrain()
    {
        var w = World();
        var a = w.Outside;
        Assert.NotNull(a);
        Assert.Same(a, w.Outside);
        w.ForgetOutside();
        var b = w.Outside;
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Same(b, w.Outside);
    }

    [Fact]
    public void TheOutsideGroundIsNeverNullWhileAnotherThreadForgetsIt()
    {
        var w = World();
        Assert.NotNull(w.Outside);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var forget = Task.Run(() => { while (!stop.IsCancellationRequested) w.ForgetOutside(); });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            int nulls = 0;
            while (!stop.IsCancellationRequested) if (w.Outside is null) nulls++;
            return nulls;
        })).ToArray();
        Task.WaitAll([forget, .. readers]);
        Assert.All(readers, r => Assert.Equal(0, r.Result));
    }
}
