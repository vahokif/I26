using System.IO.Compression;
using System.Text;
using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Where the HD packs are found (HdPackDiscovery): the README's layout, and the wrong installs a player makes of the published archives
/// (a folder one level too deep, the folder Windows' Extract All names after the archive, beside the program, the zip left as it is),
/// each used where it is; a renamed folder is never taken but reported. Tiny synthetic packs only.
/// </summary>
public sealed class HdPackDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("i26-hdfind").FullName;
    private string User => Path.Combine(_root, "I26");
    private string Program => Path.Combine(_root, "program");

    public HdPackDiscoveryTests() { Directory.CreateDirectory(User); Directory.CreateDirectory(Program); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string Json(string name, int? scale) =>
        $"{{\"name\":\"{name}\",\"version\":\"2026.11.11\"{(scale is { } s ? $",\"scale\":{s}" : "")}}}";

    /// <summary>A pack folder: its pack.json and one texture.</summary>
    private static string Pack(string dir, string layout, string file = "tpgrass1.dds", string text = "px")
    {
        Directory.CreateDirectory(dir);
        int? scale = layout.StartsWith("tex-world-") ? int.Parse(layout["tex-world-".Length..^1]) : null;
        File.WriteAllText(Path.Combine(dir, "pack.json"), Json(layout == "tex-menu" ? "Menu" : "World", scale));
        File.WriteAllText(Path.Combine(dir, file), text);
        return dir;
    }

    /// <summary>A zip as the release builds them: one top folder (or <paramref name="top"/> over it), pack.json inside it.</summary>
    private static string Zip(string path, string layout, string? top = null, string file = "tpgrass1.dds", string text = "zip", string version = "2026.11.11")
    {
        var root = (top is null ? "" : top + "/") + layout + "/";
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string n, string t) { using var w = z.CreateEntry(n).Open(); w.Write(Encoding.UTF8.GetBytes(t)); }
        Put(root + "pack.json", $"{{\"name\":\"Zipped\",\"version\":\"{version}\"}}");
        Put(root + "README.txt", "readme");
        Put(root + file, text);
        return path;
    }

    [Fact]
    public void TheReadmesLayoutIsFoundInTheUserFolder()
    {
        Pack(Path.Combine(User, "tex-world-4x"), "tex-world-4x");
        Pack(Path.Combine(User, "tex-world-8x"), "tex-world-8x");
        Pack(Path.Combine(User, "tex-menu"), "tex-menu", "shellbg1.webp");
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Equal(HdPackPlace.UserFolder, d.Get("tex-world-4x")!.Place);
        Assert.Equal(Path.Combine(User, "tex-world-8x"), d.Get("tex-world-8x")!.Path);
        Assert.Equal("Menu 2026.11.11", d.Get("tex-menu")!.Info!.ToString());
        Assert.False(d.Has("tex-world"));
        Assert.Empty(d.NotUsed);
    }

    /// <summary>tex-world-4x\tex-world-4x and tex-menu\tex-menu (the folder made first, the archive extracted into it): the inner folder, served.</summary>
    [Fact]
    public void AFolderOneLevelTooDeepIsUsedWhereItIs()
    {
        Pack(Path.Combine(User, "tex-world-4x", "tex-world-4x"), "tex-world-4x");
        Pack(Path.Combine(User, "tex-menu", "tex-menu"), "tex-menu", "shellbg1.webp");
        var d = HdPackDiscovery.Scan(User, Program);
        var w = d.Get("tex-world-4x")!;
        Assert.Equal(HdPackPlace.Nested, w.Place);
        Assert.Equal(Path.Combine(User, "tex-world-4x", "tex-world-4x"), w.Path);
        using var s = w.Open();
        Assert.True(s.Exists("tpgrass1.dds"));
        Assert.Equal(2, s.Count);
        using var m = d.Get("tex-menu")!.Open();
        Assert.True(m.Exists("shellbg1.webp"));
        Assert.Contains("one folder too deep", d.Summary());
    }

    /// <summary>Windows' Extract All: a folder named after the archive with the pack's folder in it.</summary>
    [Fact]
    public void ThePackInsideTheFolderExtractAllMadeIsUsed()
    {
        Pack(Path.Combine(User, "I26-HD-Textures-4x-2026.11.11", "tex-world-4x"), "tex-world-4x");
        Pack(Path.Combine(User, "I26-HD-Menu-Pack-2026.11.11", "tex-menu"), "tex-menu", "shellbg1.webp");
        // and the folder made first as well: I26-HD-...\tex-world-8x\tex-world-8x
        Pack(Path.Combine(User, "I26-HD-Textures-8x-Cockpit-Cars-2026.11.11", "tex-world-8x", "tex-world-8x"), "tex-world-8x");
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Equal(HdPackPlace.Extracted, d.Get("tex-world-4x")!.Place);
        Assert.Equal(HdPackPlace.Extracted, d.Get("tex-menu")!.Place);
        Assert.Equal(Path.Combine(User, "I26-HD-Textures-8x-Cockpit-Cars-2026.11.11", "tex-world-8x", "tex-world-8x"), d.Get("tex-world-8x")!.Path);
    }

    /// <summary>The release zip left unextracted in the user folder: its tex-world-4x/ read in place, pack.json from inside that folder.</summary>
    [Fact]
    public void AReleaseZipIsReadUnextracted()
    {
        Zip(Path.Combine(User, "I26-HD-Textures-4x-2026.11.11.zip"), "tex-world-4x");
        Zip(Path.Combine(User, "I26-HD-Menu-Pack-2026.11.11.zip"), "tex-menu", file: "nitro/load/Loadgame.webp");
        var d = HdPackDiscovery.Scan(User, Program);
        var w = d.Get("tex-world-4x")!;
        Assert.Equal(HdPackPlace.Zip, w.Place);
        Assert.Equal("tex-world-4x/", w.ZipRoot);
        using var s = w.Open();
        Assert.Equal("Zipped 2026.11.11", s.Info!.ToString());
        Assert.Equal("zip", Encoding.UTF8.GetString(s.Read("TPGRASS1.dds")!));
        Assert.Equal(3, s.Count);   // pack.json, README.txt and the texture, as a folder lists them
        using var m = d.Get("tex-menu")!.Open();
        Assert.True(m.Exists("nitro/load/loadgame.webp"));
    }

    /// <summary>A zip whose pack sits one folder down (I26-HD-.../tex-world-4x/...) and the old names (i76-hd-*.zip with tex-world/) still work.</summary>
    [Fact]
    public void AZipsLayoutOneFolderDownAndTheOldNamesAreRead()
    {
        Zip(Path.Combine(User, "i26-hd-textures-4x.zip"), "tex-world-4x", top: "I26-HD-Textures-4x-2026.11.11");
        Zip(Path.Combine(User, "i76-hd-world.zip"), "tex-world");
        File.WriteAllText(Path.Combine(User, "textures.zip"), "not ours, never opened");
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Equal("i26-hd-textures-4x-2026.11.11/tex-world-4x/", d.Get("tex-world-4x")!.ZipRoot);
        using (var s = d.Get("tex-world-4x")!.Open()) Assert.True(s.Exists("tpgrass1.dds"));
        Assert.Equal("tex-world/", d.Get("tex-world")!.ZipRoot);
        Assert.Empty(d.NotUsed);
    }

    /// <summary>Beside the program instead of the user folder: a folder, a nested one and a zip.</summary>
    [Fact]
    public void PacksBesideTheProgramAreUsed()
    {
        Pack(Path.Combine(Program, "tex-world-4x"), "tex-world-4x");
        Pack(Path.Combine(Program, "tex-menu", "tex-menu"), "tex-menu", "shellbg1.webp");
        Zip(Path.Combine(Program, "I26-HD-Textures-8x-Cockpit-Cars-2026.11.11.zip"), "tex-world-8x");
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Equal(HdPackPlace.BesideProgram, d.Get("tex-world-4x")!.Place);
        Assert.Equal(Path.Combine(Program, "tex-menu", "tex-menu"), d.Get("tex-menu")!.Path);
        Assert.Equal(HdPackPlace.Zip, d.Get("tex-world-8x")!.Place);
        Assert.Contains("beside the program", d.Summary());
    }

    /// <summary>The README's layout wins over every other copy of the same pack; the others are named as not used.</summary>
    [Fact]
    public void TheUserFoldersOwnPackWinsAndTheOthersAreReported()
    {
        Pack(Path.Combine(User, "tex-world-4x"), "tex-world-4x");
        Zip(Path.Combine(User, "I26-HD-Textures-4x-2026.11.11.zip"), "tex-world-4x");
        Pack(Path.Combine(Program, "tex-world-4x"), "tex-world-4x");
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Equal(Path.Combine(User, "tex-world-4x"), d.Get("tex-world-4x")!.Path);
        Assert.Equal(2, d.NotUsed.Count);
        Assert.Contains(d.NotUsed, n => n.Path.EndsWith(".zip"));
    }

    /// <summary>Between two zips of one pack the newer pack.json version.</summary>
    [Fact]
    public void TheNewerOfTwoZipsIsUsed()
    {
        Zip(Path.Combine(User, "I26-HD-Textures-4x-2026.11.11.zip"), "tex-world-4x", version: "2026.11.11");
        Zip(Path.Combine(User, "I26-HD-Textures-4x-2026.12.1.zip"), "tex-world-4x", version: "2026.12.1", text: "new");
        var d = HdPackDiscovery.Scan(User, Program);
        using var s = d.Get("tex-world-4x")!.Open();
        Assert.Equal("new", Encoding.UTF8.GetString(s.Read("tpgrass1.dds")!));
    }

    /// <summary>A renamed pack folder may be a backup or a pack turned off: never taken, named in the log with the folder name it needs.</summary>
    [Fact]
    public void ARenamedFolderIsNotTakenButSaysWhy()
    {
        Pack(Path.Combine(User, "tex-world-4x-2026.11.11"), "tex-world-4x");
        Pack(Path.Combine(User, "HD packs", "tex-world-8x"), "tex-world-8x");
        Directory.CreateDirectory(Path.Combine(User, ".backup", "tex-world-4x"));
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Empty(d.Found);
        Assert.Contains(d.NotUsed, n => n.Why.Contains("name the folder tex-world-4x"));
        Assert.Contains(d.NotUsed, n => n.Why.Contains("move the folder tex-world-8x up"));
        Assert.Equal(2, d.NotUsed.Count);
        Assert.StartsWith("none found", d.Summary());
    }

    /// <summary>A broken zip with a release name is reported, not fatal; a folder without a pack.json in the README's place is still used (a developer's pack).</summary>
    [Fact]
    public void ABrokenZipIsReportedAndABareFolderStillCounts()
    {
        File.WriteAllText(Path.Combine(User, "I26-HD-Textures-4x-2026.11.11.zip"), "half a download");
        Directory.CreateDirectory(Path.Combine(User, "tex-world-8x"));
        File.WriteAllText(Path.Combine(User, "tex-world-8x", "zdash101.dds"), "8x");
        var d = HdPackDiscovery.Scan(User, null);
        Assert.False(d.Has("tex-world-4x"));
        Assert.Contains(d.NotUsed, n => n.Why.StartsWith("not a readable zip"));
        Assert.Equal(HdPackPlace.UserFolder, d.Get("tex-world-8x")!.Place);
    }

    [Fact]
    public void NothingThereIsNone()
    {
        Directory.CreateDirectory(Path.Combine(User, "shader_cache", "a"));
        var d = HdPackDiscovery.Scan(User, Program);
        Assert.Empty(d.Found);
        Assert.Empty(d.NotUsed);
        Assert.Empty(HdPackDiscovery.Scan(Path.Combine(_root, "missing"), null).Found);
    }

    [Theory]
    [InlineData("tex-world-4x/pack.json", "tex-world-4x", "tex-world-4x/")]
    [InlineData("I26-HD-Textures-4x/tex-world-4x/a.dds", "tex-world-4x", "i26-hd-textures-4x/tex-world-4x/")]
    [InlineData("tex-menu/sprites/sprites.json", "tex-menu", "tex-menu/")]
    [InlineData("a/b/tex-world-4x/a.dds", null, null)]       // two folders down: not a layout
    [InlineData("tex-world-4x/", null, null)]                // a folder entry alone holds no file
    public void AZipsRootIsItsLayoutFolder(string entry, string? name, string? root)
    {
        var roots = HdPackDiscovery.RootsOf([entry]);
        if (name is null) Assert.Empty(roots);
        else Assert.Equal(root, roots[name]);
    }
}
