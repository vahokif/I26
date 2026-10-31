using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>A fact that needs a game install (I76_INSTALL); skipped without one.</summary>
public sealed class InstallFactAttribute : FactAttribute
{
    public InstallFactAttribute()
    {
        if (TestInstall.Dir is null)
            Skip = "set I76_INSTALL to an Interstate '76 install to run";
    }
}

public sealed class InstallTheoryAttribute : TheoryAttribute
{
    public InstallTheoryAttribute()
    {
        if (TestInstall.Dir is null)
            Skip = "set I76_INSTALL to an Interstate '76 install to run";
    }
}

public static class TestInstall
{
    public static string? Dir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static readonly Lazy<GameInstall> _install = new(() => new GameInstall(Dir!));
    private static readonly Lazy<AssetVfs> _vfs = new(() => new AssetVfs(_install.Value));
    public static GameInstall Install => _install.Value;
    public static AssetVfs Vfs => _vfs.Value;
}
