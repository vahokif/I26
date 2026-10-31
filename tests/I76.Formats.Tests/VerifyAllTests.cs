using I76.Formats.Verification;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>tools/verify_all.py's 26 checks, one test per check group.</summary>
public class VerifyAllTests(ITestOutputHelper log)
{
    private void AssertAll(IEnumerable<CheckResult> results)
    {
        var list = results.ToList();
        foreach (var r in list) log.WriteLine($"[{(r.Ok ? "ok" : "FAIL")}] {r.Name} ({r.Detail})");
        Assert.All(list, r => Assert.True(r.Ok, $"{r.Section}: {r.Name} ({r.Detail})"));
    }

    [InstallFact]
    public void Archive() => AssertAll(VerifyAll.Archive(TestInstall.Vfs.Zfs));

    [InstallFact]
    public void PakCoverage() => AssertAll([VerifyAll.PakCoverage(TestInstall.Vfs.Paks)]);

    [InstallFact]
    public void Assets() => AssertAll(VerifyAll.Assets(TestInstall.Vfs.Paks));

    [InstallFact]
    public void Definitions() => AssertAll(VerifyAll.Definitions(TestInstall.Vfs.Zfs));

    [InstallTheory]
    [InlineData("miss16")]
    [InlineData("miss8")]
    public void Missions(string dir)
    {
        var d = TestInstall.Install.FindDir(dir);
        if (d is null) return; // Nitro ships miss16 only
        var r = VerifyAll.Missions(d);
        Assert.Equal(7, r.Count);
        AssertAll(r);
    }

    [InstallFact]
    public void AllTwentySixChecks()
    {
        var dirs = new[] { "miss16", "miss8" }.Select(TestInstall.Install.FindDir).OfType<string>().ToList();
        var r = VerifyAll.Run(TestInstall.Install.ZfsPath, dirs);
        Assert.Equal(12 + 7 * dirs.Count, r.Count);
        AssertAll(r);
    }
}
