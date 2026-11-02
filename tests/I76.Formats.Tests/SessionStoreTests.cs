using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The store is process-wide and, while a session is open, other tests' reads of files would be kept in it: these run when nothing else does.</summary>
[CollectionDefinition("SessionStore", DisableParallelization = true)]
public class SessionStoreCollection;

[Collection("SessionStore")]
public class SessionStoreTests
{
    private sealed class Blob(int size) { public int Size { get; } = size; }

    [Fact]
    public void OutsideASessionNothingIsKept()
    {
        SessionStore.End();
        int made = 0;
        for (int i = 0; i < 3; i++) SessionStore.GetOrAdd<Blob>("k", () => { made++; return new Blob(10); }, b => b!.Size);
        Assert.Equal(3, made);
        Assert.Equal(0, SessionStore.Count);
        Assert.Null(SessionStore.TryGet<Blob>("k"));
    }

    [Fact]
    public void ASessionMakesEachThingOnceAndEndingItDropsAll()
    {
        SessionStore.Begin();
        int made = 0;
        var a = SessionStore.GetOrAdd<Blob>("k", () => { made++; return new Blob(10); }, b => b!.Size);
        var b2 = SessionStore.GetOrAdd<Blob>("k", () => { made++; return new Blob(10); }, b => b!.Size);
        Assert.Same(a, b2);
        Assert.Equal(1, made);
        SessionStore.GetOrAdd<Blob>("none", () => { made++; return null; }, b => 0);   // a missing file is remembered too
        SessionStore.GetOrAdd<Blob>("none", () => { made++; return null; }, b => 0);
        Assert.Equal(2, made);
        Assert.Equal(2, SessionStore.Count);
        SessionStore.End();
        Assert.False(SessionStore.Active);
        Assert.Equal(0, SessionStore.Count);
        Assert.Equal(0, SessionStore.Bytes);
        SessionStore.Begin();                       // a new session starts empty
        Assert.Null(SessionStore.TryGet<Blob>("k"));
        SessionStore.End();
    }

    [Fact]
    public void PastItsBudgetItStartsOver()
    {
        SessionStore.Begin();
        SessionStore.Put("a", new Blob(1), 100);
        SessionStore.Put("b", new Blob(1), SessionStore.Budget);   // over: everything before goes (and this one is kept)
        Assert.Null(SessionStore.TryGet<Blob>("a"));
        Assert.NotNull(SessionStore.TryGet<Blob>("b"));
        Assert.True(SessionStore.Count <= 1);
        SessionStore.End();
    }
}
