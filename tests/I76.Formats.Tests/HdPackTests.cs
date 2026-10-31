using System.IO.Compression;
using System.Text;
using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

public class HdPackTests
{
    private static string Make(string dir, string name, string version, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(dir, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string n, string t) { using var w = z.CreateEntry(n).Open(); w.Write(Encoding.UTF8.GetBytes(t)); }
        Put("pack.json", $"{{\"name\":\"Test Pack\",\"version\":\"{version}\",\"date\":\"2026-01-01\",\"min_engine\":\"1.1\"}}");
        foreach (var (p, t) in files) Put(p, t);
        return path;
    }

    private static string Temp() => Directory.CreateTempSubdirectory("hdpack").FullName;

    [Fact]
    public void ReadsFilesAndManifestFromZip()
    {
        var dir = Temp();
        var zip = Make(dir, "i76-hd-menus.zip", "1.0.0", ("tex-menu/shellbg.json", "{\"6\":{\"file\":\"x.webp\",\"affine\":[1,2,3,4]}}"), ("tex-menu/nitro/load/Loadgame.webp", "px"), ("other/ignored.txt", "no"));
        using var s = new HdZipSource(zip);
        Assert.Equal("Test Pack 1.0.0", s.Info!.ToString());
        Assert.Equal(2, s.Count);
        Assert.True(s.Exists("nitro/load/loadgame.webp"));
        Assert.False(s.Exists("ignored.txt"));
        Assert.Equal("px", Encoding.UTF8.GetString(s.Read("nitro\\load\\loadgame.webp")!));
        Assert.Null(s.Read("missing.webp"));
        Assert.Contains("\"affine\"", Encoding.UTF8.GetString(s.Read("shellbg.json")!));
    }

    [Fact]
    public void NewestPackWinsAndBrokenOnesAreSkipped()
    {
        var dir = Temp();
        Make(dir, "i76-hd-menus-1.2.0.zip", "1.2.0", ("tex-menu/a.webp", "a"));
        Make(dir, "i76-hd-menus-1.10.0.zip", "1.10.0", ("tex-menu/a.webp", "b"));
        File.WriteAllText(Path.Combine(dir, "i76-hd-menus-bad.zip"), "not a zip");
        var warnings = new List<string>();
        using var s = HdPackFinder.Newest([dir, Path.Combine(dir, "nope")], warnings.Add);
        Assert.Equal("1.10.0", s!.Info!.Version);
        Assert.Single(warnings);
    }

    [Fact]
    public void FolderSourceReadsTheSameLayout()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "nitro"));
        File.WriteAllText(Path.Combine(dir, "nitro", "shellbg.json"), "{}");
        using var s = new HdDirSource(dir);
        Assert.True(s.Exists("nitro/shellbg.json"));
        Assert.Null(s.Info);
        Assert.Null(HdPackInfo.Parse("nope"u8.ToArray()));
    }
}
