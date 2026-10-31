using System.Security.Cryptography;
using I76.Formats.Assembly;
using I76.Formats.Installers;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>A fact that needs a real GOG offline installer, named by an environment variable; skipped without one.</summary>
public sealed class InstallerFactAttribute : FactAttribute
{
    public InstallerFactAttribute(string env)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } p || !File.Exists(p))
            Skip = $"set {env} to a GOG offline installer (setup_....exe) to run";
    }
}

/// <summary>
/// The real installers (docs/dev-hooks.md): I76_GOG_INSTALLER (Interstate '76) and I76_GOG_NITRO_INSTALLER (the Nitro
/// Pack) are parsed, listed and extracted under ~/.cache/i26-tests; every extracted file is compared with the same file
/// in the install (I76_INSTALL, I76_NITRO_INSTALL; read only), and a mission runs headless on the extracted folder.
/// I76_GOG_GALAXY_STUB names a GOG Galaxy downloader, which must be refused as one.
/// </summary>
public class InstallerIntegrationTests(ITestOutputHelper output)
{
    // files the game (or the player) rewrites in an install: they may differ from the installer's copy
    private static readonly HashSet<string> PlayerFiles = new(StringComparer.OrdinalIgnoreCase)
        { "input.map", "keyboard.map", "joystick.map", "gamekey.map", "i76plyr.def", "nitplyr.def", "i76uveh.def", "nituser.mel", "internet.lst" };

    private static string CacheDir(string name)
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "i26-tests", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        return d;
    }

    [InstallerFact("I76_GOG_INSTALLER")]
    public void BaseGameInstallerExtractsAnInstallThatPlays() =>
        Check(Environment.GetEnvironmentVariable("I76_GOG_INSTALLER")!, GogGame.I76, TestInstall.Dir, "t01");

    [InstallerFact("I76_GOG_NITRO_INSTALLER")]
    public void NitroInstallerExtractsAnInstallThatPlays() =>
        Check(Environment.GetEnvironmentVariable("I76_GOG_NITRO_INSTALLER")!, GogGame.Nitro, NitroFactAttribute.Dir, "p01");

    [InstallerFact("I76_GOG_GALAXY_STUB")]
    public void GalaxyStubIsRefused()
    {
        var ex = Assert.Throws<InstallerException>(() => GogInstaller.Open(Environment.GetEnvironmentVariable("I76_GOG_GALAXY_STUB")!));
        Assert.Equal(InstallerError.GalaxyStub, ex.Kind);
        output.WriteLine(ex.Message);
    }

    private void Check(string exe, GogGame game, string? install, string mission)
    {
        var g = GogInstaller.Open(exe);
        Assert.Equal(game, g.Game);
        output.WriteLine($"{Path.GetFileName(exe)}: {g.Setup.IdString}, {g.Setup.Header.Compression}, {g.Setup.Files.Count} file entries, {g.Setup.Data.Count} data entries");
        output.WriteLine($"extracting {g.Wanted.Count()} of {g.Entries.Count} files under {{app}} ({GogInstaller.Mb(g.WantedBytes)}); left out: " +
            string.Join(", ", g.Entries.Where(e => !e.Wanted).Select(e => e.RelativePath)));
        Assert.Contains(g.Wanted, e => e.RelativePath.StartsWith("miss16/", StringComparison.OrdinalIgnoreCase));
        // of the executables only the game's own (the AI's tactics are read from it)
        Assert.Equal([game == GogGame.Nitro ? "nitro.exe" : "i76.exe"],
            g.Wanted.Where(e => e.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Select(e => e.RelativePath.ToLowerInvariant()));

        var dir = CacheDir("gog-" + GogInstaller.DefaultFolderName(game));
        var r = g.ExtractTo(dir);
        output.WriteLine($"extracted {r.Files} files, {GogInstaller.Mb(r.Bytes)} in {r.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(g.Wanted.Count(), r.Files);
        Assert.True(g.AlreadyIn(dir));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.i26part", SearchOption.AllDirectories));
        var gi = new GameInstall(dir);
        Assert.Equal(game == GogGame.Nitro, gi.IsNitro);

        if (install is not null)
        {
            var original = new GameInstall(install);
            var same = new List<string>();
            var differ = new List<string>();
            var absent = new List<string>();
            foreach (var e in g.Wanted)
            {
                var mine = Path.Combine(dir, e.RelativePath);
                if (original.Resolve(e.RelativePath.Split('/')) is not { } theirs || !File.Exists(theirs)) { absent.Add(e.RelativePath); continue; }
                (Hash(mine) == Hash(theirs) ? same : differ).Add(e.RelativePath);
            }
            output.WriteLine($"against {install}: {same.Count} identical, {differ.Count} differ ({string.Join(", ", differ)}), " +
                $"{absent.Count} not in the install ({string.Join(", ", absent)})");
            Assert.Contains(game == GogGame.Nitro ? "nitro.zfs" : "I76.ZFS", same, StringComparer.OrdinalIgnoreCase);
            Assert.All(differ, f => Assert.Contains(Path.GetFileName(f), PlayerFiles));
        }

        // the mission headless on the extracted folder, as `i76 run MISSION 10` does
        var vfs = new AssetVfs(gi);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, gi.FindMission(mission)!);
        var setup = MissionSetup.Build(vfs, w, asm);
        var session = new SimSession(setup);
        if (setup.World.Player is { } p) session.AddDriver(p);
        int ticks = (int)(10 / session.TickSeconds);
        for (int i = 0; i < ticks; i++) session.Tick([new CarInput()]);
        Assert.NotNull(setup.Script);
        Assert.True(setup.Script!.Host.Now > 9, $"the script ran {setup.Script.Host.Now} s");
        output.WriteLine($"{mission}: {ticks} ticks, script at {setup.Script.Host.Now:0.0} s");
    }

    private static string Hash(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(f));
    }
}

/// <summary>An unpack from an earlier I26 (no executable) is repaired from the installer: only the executable is added.</summary>
public class ExeRepairTests
{
    [InstallerFact("I76_GOG_INSTALLER")]
    public void BaseGameExecutableIsRestored() => Run(Environment.GetEnvironmentVariable("I76_GOG_INSTALLER")!, "i76.exe");

    [InstallerFact("I76_GOG_NITRO_INSTALLER")]
    public void NitroExecutableIsRestored() => Run(Environment.GetEnvironmentVariable("I76_GOG_NITRO_INSTALLER")!, "nitro.exe");

    private static Dictionary<string, (long Len, DateTime Time)> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));

    private static void Run(string installer, string exeName)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "i26-tests", "exerepair-" + exeName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var dir = Path.Combine(root, "install");
        var g = GogInstaller.Open(installer);
        g.ExtractTo(dir, all: false);
        var exe = Path.Combine(dir, exeName);
        Assert.True(File.Exists(exe), "the extraction keeps the executable");
        var fresh = File.ReadAllBytes(exe);
        File.Delete(exe);
        var before = Snapshot(dir);

        // no installer in the folders: nothing happens
        Assert.False(ExeRepair.Repair(dir, [Path.Combine(root, "empty")]));
        Assert.False(File.Exists(exe));

        var log = new List<string>();
        Assert.True(ExeRepair.Repair(dir, [Path.GetDirectoryName(installer)], log.Add));
        Assert.Single(log);
        Assert.Equal(fresh, File.ReadAllBytes(exe));
        var after = Snapshot(dir);
        Assert.Equal(before.Keys.Append(exeName).Order().ToList(), after.Keys.Order().ToList());
        foreach (var (k, v) in before) Assert.Equal(v, after[k]);

        // a second run does nothing; a folder without I26's marker is never touched
        Assert.False(ExeRepair.Repair(dir, [Path.GetDirectoryName(installer)]));
        File.Delete(exe); File.Delete(Path.Combine(dir, GogInstaller.MarkerName));
        Assert.False(ExeRepair.Repair(dir, [Path.GetDirectoryName(installer)]));
        Assert.False(File.Exists(exe));
        Directory.Delete(root, true);
    }
}
