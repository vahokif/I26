using I76.Formats.Installers;
using Xunit;

namespace I76.Formats.Tests;

public sealed class GogLibraryTests : IDisposable
{
    private readonly string _t = Directory.CreateTempSubdirectory("i26gog").FullName;
    public void Dispose() => Directory.Delete(_t, true);

    private string Dir(params string[] p) => Directory.CreateDirectory(Path.Combine([_t, .. p])).FullName;

    /// <summary>A GOG game folder: the install file and the .info.</summary>
    private string Game(string id, bool info, params string[] p)
    {
        var d = Dir(p);
        File.WriteAllText(Path.Combine(d, id == GogLibrary.NitroId ? "nitro.zfs" : "I76.ZFS"), "");
        if (info) File.WriteAllText(Path.Combine(d, $"goggame-{id}.info"), $"{{\"gameId\":\"{id}\"}}");
        return d;
    }

    private void Config(string root, string json, params string[] pathBelow)
    {
        var d = Dir([root, .. pathBelow]);
        File.WriteAllText(Path.Combine(d, "config.json"), json);
    }

    [Fact]
    public void Mount_table_keeps_windows_file_systems_only()
    {
        var m = GogEnvironment.WindowsMounts([
            "/dev/sda1 /boot/efi vfat rw 0 0", "/dev/sdb1 /mnt/windows fuseblk rw 0 0", "/dev/sdc1 /mnt/my\\040disk ntfs3 rw 0 0",
            "/dev/sdd1 / ext4 rw 0 0", "tmpfs /run tmpfs rw 0 0", "/dev/sde1 /data exfat rw 0 0"]);
        Assert.Equal(["/mnt/windows", "/mnt/my disk", "/data"], m);
    }

    [Fact]
    public void Drive_letters_map_across_two_roots_with_case_insensitive_walk()
    {
        var c = Dir("mnt", "windows");   // C:, Galaxy's config in odd case
        var f = Dir("mnt", "bulk");      // F:
        Config(c, "{\"installationPaths\":[\"F:\\\\games\", \"F:\\\\games\\\\interstate2\"],\"libraryPath\":\"C:\\\\Program Files (x86)\\\\GOG Galaxy\\\\Games\"}", "PROGRAMDATA", "gog.com", "Galaxy");
        var g1 = Game(GogLibrary.I76Id, true, "mnt", "bulk", "Games", "interstate2", "Interstate 76");
        var g2 = Game(GogLibrary.NitroId, true, "mnt", "bulk", "Games", "interstate2", "Nitro Pack");
        Game(GogLibrary.I76Id, false, "mnt", "bulk", "Games", "interstate2", "Interstate 76 decoy");   // right sort of folder, no .info
        var log = new List<string>();
        var env = new GogEnvironment { Roots = [new(c, null), new(f, null)] };
        var found = GogLibrary.Find(env, log.Add);
        Assert.Equal([g1, g2], found.OrderBy(x => x));
        Assert.Contains(log, l => l.StartsWith("gog: config.json on ") && l.Contains("F:\\games\\interstate2") && l.Contains(g1));
    }

    [Fact]
    public void A_known_letter_wins_and_a_wrong_letter_is_skipped()
    {
        var c = Dir("bottle", "drive_c");
        var d = Dir("other");
        Config(c, "{\"installationPaths\":[\"D:\\\\lib\"]}", "ProgramData", "GOG.com", "Galaxy");
        var good = Game(GogLibrary.I76Id, true, "other", "lib", "I76");
        Game(GogLibrary.I76Id, true, "bottle", "drive_c", "lib", "I76");   // C:\lib\I76 would be a different game
        var found = GogLibrary.Find(new GogEnvironment { Roots = [new(c, 'C'), new(d, 'D')] });
        Assert.Equal([good], found);
    }

    [Fact]
    public void Gogs_own_folder_comes_before_a_backup_copy()
    {
        var c = Dir("w");
        Config(c, "{\"installationPaths\":[\"C:\\\\lib\"]}", "ProgramData", "GOG.com", "Galaxy");
        var copy = Game(GogLibrary.I76Id, true, "w", "lib", "a-backup");
        var gog = Game(GogLibrary.I76Id, true, "w", "lib", "Interstate 76");
        var found = GogLibrary.Find(new GogEnvironment { Roots = [new(c, null)] });
        Assert.Equal([gog, copy], found);
    }

    [Fact]
    public void Decoy_without_info_is_rejected_and_info_without_install_is_too()
    {
        var c = Dir("w");
        Config(c, "{\"installationPaths\":[\"C:\\\\lib\"]}", "ProgramData", "GOG.com", "Galaxy");
        Game(GogLibrary.I76Id, false, "w", "lib", "Interstate 76");
        Dir("w", "lib", "Empty");
        File.WriteAllText(Path.Combine(Dir("w", "lib", "Hollow"), $"goggame-{GogLibrary.I76Id}.info"), "{}");
        Assert.Empty(GogLibrary.Find(new GogEnvironment { Roots = [new(c, null)] }));
    }

    [Fact]
    public void Heroic_installed_json()
    {
        var g = Game(GogLibrary.NitroId, true, "heroic", "Nitro");
        var hf = Path.Combine(Dir("cfg"), "installed.json");
        File.WriteAllText(hf, "{\"installed\":[{\"appName\":\"1207661023\",\"install_path\":" + System.Text.Json.JsonSerializer.Serialize(g)
            + "},{\"appName\":\"99\",\"install_path\":\"/nowhere\"}]}");
        Assert.Equal([g], GogLibrary.Find(new GogEnvironment { HeroicFiles = [hf] }));
    }

    [Fact]
    public void Windows_config_and_registry_paths_are_used_as_given()
    {
        var lib = Dir("lib");
        var g1 = Game(GogLibrary.I76Id, true, "lib", "Interstate 76");
        var g2 = Game(GogLibrary.NitroId, true, "reg", "Nitro");
        Config(_t, "{\"installationPaths\":[" + System.Text.Json.JsonSerializer.Serialize(lib) + "]}", "pd", "GOG.com", "Galaxy");
        var env = new GogEnvironment
        {
            IsWindows = true, CommonAppData = Path.Combine(_t, "pd"),
            RegistryPaths = id => id == GogLibrary.NitroId ? [g2] : [],
        };
        Assert.Equal([g2, g1], GogLibrary.Find(env));
    }

    [Fact]
    public void Registry_reads_only_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var env = GogEnvironment.FromSystem();
        Assert.True(env.IsWindows);
        _ = GogLibrary.Find(env);   // must not throw, whatever is installed
    }
}
