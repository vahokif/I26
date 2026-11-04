using I76.Formats.Installers;
using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>Which games the start-up offers: installs and GOG installers found in folders, counted once per game.</summary>
public class GameDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-disc-" + Guid.NewGuid().ToString("N"));

    public GameDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(string name) { var d = Path.Combine(_root, name); Directory.CreateDirectory(d); return d; }
    private string Install(string name, bool nitro) { var d = Dir(name); File.WriteAllText(Path.Combine(d, nitro ? "nitro.zfs" : "I76.ZFS"), "x"); return d; }
    private string File_(string dir, string name, int bytes = 10) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, new byte[bytes]); return p; }

    [Theory]
    [InlineData("setup_interstate76_2.1.0.17.exe", GogGame.I76)]
    [InlineData("setup_interstate76_2.1.0.17_(64bit)_(12345).exe", GogGame.I76)]
    [InlineData("setup_interstate76_nitro_pack_2.1.0.17.exe", GogGame.Nitro)]
    [InlineData("SETUP_INTERSTATE76_NITRO_PACK_1.exe", GogGame.Nitro)]
    public void RecognisesInstallersByName(string name, GogGame game) => Assert.Equal(game, GameDiscovery.InstallerKindByName(name));

    [Theory]
    [InlineData("GOG_Galaxy_Interstate_76_Nitro_Pack.exe")]
    [InlineData("setup_galaxy_2.0.exe")]
    [InlineData("setup_other_game_1.0.exe")]
    [InlineData("i76.exe")]
    [InlineData("setup_interstate76_1.0.bin")]
    public void IgnoresOtherFilesAndTheGalaxyStub(string name) => Assert.Null(GameDiscovery.InstallerKindByName(name));

    [Fact]
    public void InstallKindSeparatesTheTwo()
    {
        Assert.Equal(GogGame.I76, GameDiscovery.InstallKind(Install("a", false)));
        Assert.Equal(GogGame.Nitro, GameDiscovery.InstallKind(Install("b", true)));
        Assert.Null(GameDiscovery.InstallKind(Dir("empty")));
        Assert.Null(GameDiscovery.InstallKind(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void NothingFound()
    {
        Assert.Empty(GameDiscovery.Discover([Dir("e1"), null, Path.Combine(_root, "nope")], [Dir("e2")]));
    }

    [Fact]
    public void ServerPlacesFindTheGameNextToTheExecutable()
    {
        var dir = Dir("rel");
        var exe = Path.Combine(dir, "i26-server");
        var i76 = Install(Path.Combine("rel", "i76"), false);
        var (installs, folders) = GameDiscovery.Places(exe, Path.Combine(_root, "local"));
        var g = Assert.Single(GameDiscovery.Discover(installs, folders));
        Assert.Equal(GogGame.I76, g.Game);
        Assert.Equal(i76, g.InstallDir);
    }

    [Fact]
    public void ServerPlacesSeeTheUserFolderAndItsInstallers()
    {
        var local = Dir("local");
        Install("local/nitro", true);
        File_(Dir("rel"), "setup_interstate76_2.1.exe", 50);
        var (installs, folders) = GameDiscovery.Places(Path.Combine(_root, "rel", "i26-server"), local);
        var games = GameDiscovery.Discover(installs, folders);
        Assert.Equal([GogGame.I76, GogGame.Nitro], games.Select(x => x.Game));
        Assert.False(games[0].Installed);
        Assert.True(games[1].Installed);
    }

    [Fact]
    public void PickPrefersInterstate76AndHonoursTheRequest()
    {
        List<AvailableGame> both = [new(GogGame.I76, "a", null, 0), new(GogGame.Nitro, "b", null, 0)];
        Assert.Equal(GogGame.I76, GameDiscovery.Pick(both, null, out _)!.Game);
        Assert.Equal(GogGame.Nitro, GameDiscovery.Pick(both, GogGame.Nitro, out _)!.Game);
        Assert.Equal(GogGame.Nitro, GameDiscovery.Pick([both[1]], null, out _)!.Game);
        Assert.Null(GameDiscovery.Pick([both[1]], GogGame.I76, out var err));
        Assert.Contains("Interstate '76", err);
        Assert.Null(GameDiscovery.Pick([], null, out err));
        Assert.NotNull(err);
    }

    [Fact]
    public void OneInstallerIsOneGame()
    {
        var f = Dir("dl");
        var p = File_(f, "setup_interstate76_nitro_pack_2.1.exe", 1234);
        File_(f, "GOG_Galaxy_Interstate_76_Nitro_Pack.exe");
        var g = Assert.Single(GameDiscovery.Discover([], [f]));
        Assert.Equal(GogGame.Nitro, g.Game);
        Assert.False(g.Installed);
        Assert.Equal(p, g.InstallerPath);
        Assert.Equal(1234, g.InstallerBytes);
    }

    [Fact]
    public void AnInstallBeatsItsInstaller()
    {
        var f = Dir("dl");
        File_(f, "setup_interstate76_2.1.exe");
        File_(f, "setup_interstate76_nitro_pack_2.1.exe");
        var i76 = Install("i76", false);
        var list = GameDiscovery.Discover([i76], [f]);
        Assert.Equal(2, list.Count);
        Assert.Equal(GogGame.I76, list[0].Game);
        Assert.True(list[0].Installed);
        Assert.Equal(i76, list[0].InstallDir);
        Assert.Null(list[0].InstallerPath);
        Assert.Equal(GogGame.Nitro, list[1].Game);
        Assert.False(list[1].Installed);
        Assert.NotNull(list[1].InstallerPath);
    }

    [Fact]
    public void TwoInstallsOfOneGameCountOnce_TheFirstWins()
    {
        var a = Install("a", true);
        var b = Install("b", true);
        var g = Assert.Single(GameDiscovery.Discover([a, b], []));
        Assert.Equal(a, g.InstallDir);
    }

    [Fact]
    public void SearchesEveryFolder_NewestInstallerWins()
    {
        var a = Dir("a"); var b = Dir("b");
        File_(a, "setup_interstate76_2.0.exe");
        File_(a, "setup_interstate76_2.1.exe");
        File_(b, "setup_interstate76_nitro_pack_2.1.exe");
        var list = GameDiscovery.Discover([], [a, b, a]);
        Assert.Equal(2, list.Count);
        Assert.EndsWith("setup_interstate76_2.1.exe", list[0].InstallerPath);
        Assert.Equal(GogGame.Nitro, list[1].Game);
    }

    [Fact]
    public void BothInstalledNeedsNoInstallers()
    {
        var list = GameDiscovery.Discover([Install("n", true), Install("b", false)], []);
        Assert.Equal([GogGame.I76, GogGame.Nitro], list.Select(g => g.Game));
        Assert.All(list, g => Assert.True(g.Installed));
    }

    // ---- the real installers: recognised by contents, the main-menu logo read straight out of them ----

    [InstallerFact("I76_GOG_INSTALLER")]
    public void RealBaseInstaller_LogoCardWithoutExtracting() => Logo("I76_GOG_INSTALLER", GogGame.I76);

    [InstallerFact("I76_GOG_NITRO_INSTALLER")]
    public void RealNitroInstaller_LogoCardWithoutExtracting() => Logo("I76_GOG_NITRO_INSTALLER", GogGame.Nitro);

    private static void Logo(string env, GogGame game)
    {
        var exe = Environment.GetEnvironmentVariable(env)!;
        Assert.Equal(game, GameDiscovery.InstallerKind(exe));
        var g = GogInstaller.Open(exe);
        var name = new AvailableGame(game, null, exe, 0).ShellDatabaseName;
        var bytes = g.ReadFile(name);
        Assert.NotNull(bytes);
        var card = new ShellDatabase(bytes!).Background(26);
        Assert.Equal((640, 480), (card.Width, card.Height));
        Assert.Contains(card.Pixels, b => b != 0);
    }
}
