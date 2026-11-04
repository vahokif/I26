using I76.Formats;
using I76.Formats.Installers;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public sealed class InstallAndMigrationTests : IDisposable
{
    private readonly string _t = Directory.CreateTempSubdirectory("i26test").FullName;
    public void Dispose() => Directory.Delete(_t, true);

    private string Dir(params string[] p) => Directory.CreateDirectory(Path.Combine([_t, .. p])).FullName;
    private string Install(params string[] p) { var d = Dir(p); File.WriteAllText(Path.Combine(d, "I76.ZFS"), ""); return d; }

    [Fact]
    public void Order_option_env_exe_parent_then_extraction_places()
    {
        var opt = Install("opt"); var env = Install("env"); var game = Install("game"); var sub = Dir("game", "i26-1");
        var exe = Path.Combine(sub, "i26");
        Assert.Equal(opt, InstallSearch.Find(opt, env, exe));
        Assert.Equal(env, InstallSearch.Find(null, env, exe));
        Assert.Equal(game, InstallSearch.Find(null, null, exe));          // the parent of the executable's folder
        var other = Install("local", "game");
        Assert.Equal(other, InstallSearch.Find(null, null, Path.Combine(Dir("empty", "x"), "i26"), null, [other]));
    }

    [Fact]
    public void Discovery_needs_no_saved_state_and_sees_both_extraction_places()
    {
        var exeDir = Dir("rel", "i26-0.2"); var exe = Path.Combine(exeDir, "i26.x86_64");
        var local = Dir("user", "I26");
        var beside = Install("rel", "i26-0.2", "i76");   // a copied game folder: no marker
        var nitro = Dir("user", "I26", "nitro"); File.WriteAllText(Path.Combine(nitro, "nitro.zfs"), "");
        var places = ExtractionPlaces.All(exe, local);
        Assert.Equal([beside, Path.Combine(exeDir, "nitro"), Path.Combine(local, "i76"), nitro], places);
        var games = GameDiscovery.Discover(places, []);
        Assert.Equal(beside, games.Single(g => g.Game == GogGame.I76).InstallDir);
        Assert.Equal(nitro, games.Single(g => g.Game == GogGame.Nitro).InstallDir);
    }

    [UnixFact]
    public void App_folder_is_the_one_holding_the_mac_bundle()
    {
        Assert.Equal("/Users/p/Games/i26-1", ExtractionPlaces.AppFolder("/Users/p/Games/i26-1/I26.app/Contents/MacOS/I26"));
        Assert.Equal("/opt/i26", ExtractionPlaces.AppFolder("/opt/i26/i26.x86_64"));
        Assert.Equal("/opt/MacOS", ExtractionPlaces.AppFolder("/opt/MacOS/i26"));   // not in a bundle
        Assert.Equal("/a/Contents/MacOS", ExtractionPlaces.AppFolder("/a/Contents/MacOS/x"));   // no .app above
        Assert.Null(ExtractionPlaces.AppFolder(null));
        var seen = new List<string>();
        InstallSearch.Find(null, null, "/Users/p/Games/i26-1/I26.app/Contents/MacOS/I26", seen);
        Assert.Equal(["/Users/p/Games/i26-1", "/Users/p/Games"], seen);
        Assert.Equal("/Users/p/Games/i26-1/i76", ExtractionPlaces.Inside("/Users/p/Games/i26-1/I26.app/Contents/MacOS/I26", GogGame.I76));
    }

    [Fact]
    public void Target_is_inside_the_executables_folder_when_it_is_writable()
    {
        var exe = Path.Combine(Dir("rel", "i26-0.2"), "i26.x86_64"); var local = Path.Combine(_t, "user");
        Assert.Equal(Path.Combine(_t, "rel", "i26-0.2", "i76"), ExtractionPlaces.Choose(GogGame.I76, exe, local));
        Assert.Equal(Path.Combine(_t, "rel", "i26-0.2", "nitro"), ExtractionPlaces.Choose(GogGame.Nitro, exe, local));
    }

    [Fact]
    public void Target_is_inside_again_for_our_own_earlier_extraction()
    {
        var exe = Path.Combine(Dir("rel", "i26-0.2"), "i26.x86_64"); var local = Path.Combine(_t, "user");
        var mine = Dir("rel", "i26-0.2", "i76"); File.WriteAllText(Path.Combine(mine, GogInstaller.MarkerName), "installer: x 1");
        File.WriteAllText(Path.Combine(mine, "other.txt"), "");
        Assert.Equal(mine, ExtractionPlaces.Choose(GogGame.I76, exe, local));
    }

    [Fact]
    public void Target_is_local_for_a_foreign_folder_a_dev_binary_or_not_enough_space()
    {
        var exe = Path.Combine(Dir("rel", "i26-0.2"), "i26.x86_64"); var local = Path.Combine(_t, "user");
        var foreign = Dir("rel", "i26-0.2", "i76"); File.WriteAllText(Path.Combine(foreign, "mine.txt"), "");
        Assert.Equal(Path.Combine(local, "i76"), ExtractionPlaces.Choose(GogGame.I76, exe, local));
        Assert.Equal(Path.Combine(_t, "rel", "i26-0.2", "nitro"), ExtractionPlaces.Choose(GogGame.Nitro, exe, local));
        var dev = Path.Combine(Dir("rel2", "godot-4.7"), "godot-mono");
        Assert.True(ExtractionPlaces.IsDevBinary(dev));
        Assert.Equal(Path.Combine(local, "nitro"), ExtractionPlaces.Choose(GogGame.Nitro, dev, local));
        Assert.Equal(Path.Combine(local, "i76"), ExtractionPlaces.Choose(GogGame.I76, Path.Combine(Dir("rel3", "x"), "i26"), local, long.MaxValue));
    }

    [Fact]
    public void Target_is_local_when_the_exe_folder_is_not_writable()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var parent = Dir("ro", "i26-0.2"); var exe = Path.Combine(parent, "i26.x86_64"); var local = Path.Combine(_t, "user");
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { Assert.Equal(Path.Combine(local, "i76"), ExtractionPlaces.Choose(GogGame.I76, exe, local)); }
        finally { File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void Exe_folder_beats_parent_and_nitro_counts()
    {
        var outer = Install("a"); var inner = Dir("a", "b");
        File.WriteAllText(Path.Combine(inner, "nitro.zfs"), "");
        Assert.Equal(inner, InstallSearch.Find(null, null, Path.Combine(inner, "i26")));
        Assert.Equal(outer, InstallSearch.Find(null, null, Path.Combine(Dir("a", "c"), "i26")));
    }

    [Fact]
    public void Nothing_found_lists_what_was_checked()
    {
        var seen = new List<string>();
        Assert.Null(InstallSearch.Find(null, null, Path.Combine(Dir("n", "m"), "i26"), seen));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Migration_copies_once_never_overwrites_never_touches_old()
    {
        var o = Dir("old"); var n = Path.Combine(_t, "new");
        File.WriteAllText(Path.Combine(o, "shell.cfg"), "old");
        File.WriteAllText(Path.Combine(o, "boot_splash.png"), "x");
        Directory.CreateDirectory(Path.Combine(o, "cars")); File.WriteAllText(Path.Combine(o, "cars", "a.vcf"), "v");
        Assert.True(UserDataMigration.Run(o, n));
        Assert.Equal("old", File.ReadAllText(Path.Combine(n, "shell.cfg")));
        Assert.Equal("v", File.ReadAllText(Path.Combine(n, "cars", "a.vcf")));
        Assert.False(File.Exists(Path.Combine(n, "boot_splash.png")));
        Assert.True(File.Exists(Path.Combine(o, "boot_splash.png")));
        // changes after migration are not clobbered, and it does not repeat
        File.WriteAllText(Path.Combine(n, "shell.cfg"), "new"); File.WriteAllText(Path.Combine(o, "shell.cfg"), "old2");
        Assert.False(UserDataMigration.Run(o, n));
        Assert.Equal("new", File.ReadAllText(Path.Combine(n, "shell.cfg")));
        Assert.Equal("old2", File.ReadAllText(Path.Combine(o, "shell.cfg")));
    }

    [Fact]
    public void Migration_skips_when_new_has_settings_or_old_missing()
    {
        var o = Dir("o2"); File.WriteAllText(Path.Combine(o, "shell.cfg"), "old");
        var n = Dir("n2"); File.WriteAllText(Path.Combine(n, "settings.cfg"), "mine");
        Assert.False(UserDataMigration.Run(o, n));
        Assert.False(File.Exists(Path.Combine(n, "shell.cfg")));
        Assert.False(UserDataMigration.Run(Path.Combine(_t, "nope"), Path.Combine(_t, "n3")));
    }
}
