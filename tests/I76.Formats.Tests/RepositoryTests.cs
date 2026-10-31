using System.Diagnostics;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The repository holds no symlinks. A worktree links the main checkout's fetched assets (game/ground, game/vfx);
/// 493f17a committed two such links, and merging it replaced the main checkout's folders with links to themselves,
/// so the game lost its ground materials (the terrain fell back to its blurred procedural ground) and its flipbooks.
/// </summary>
public class RepositoryTests
{
    [Fact]
    public void NoSymlinksAreTracked()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "I76.sln"))) dir = dir.Parent;
        if (dir is null || !Path.Exists(Path.Combine(dir.FullName, ".git"))) return;   // not a checkout (a source archive)
        string listing;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", ["-C", dir.FullName, "ls-files", "-s"]) { RedirectStandardOutput = true })!;
            listing = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0) return;
        }
        catch (System.ComponentModel.Win32Exception) { return; }   // no git on PATH
        var links = listing.Split('\n').Where(l => l.StartsWith("120000 ")).Select(l => l[(l.IndexOf('\t') + 1)..]).ToList();
        Assert.True(links.Count == 0, $"symlinks tracked: {string.Join(", ", links)}");
    }
}
