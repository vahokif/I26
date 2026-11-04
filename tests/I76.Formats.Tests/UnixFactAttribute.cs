using Xunit;

namespace I76.Formats.Tests;

/// <summary>A test of Unix paths (a macOS bundle, Linux mount roots): skipped on Windows, where those paths are not rooted.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix paths"; }
}
