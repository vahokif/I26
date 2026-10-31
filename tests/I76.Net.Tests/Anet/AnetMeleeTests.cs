using System.Diagnostics;
using System.Net;
using I76.Net.Anet;
using I76.Server;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests.Anet;

/// <summary>
/// The game's own pieces through an anet server: a <see cref="NetMeleeServer"/> listed on it from behind one NAT,
/// the roster's <see cref="SessionBrowser"/> and a <see cref="MeleeClient"/> joining through it from behind another,
/// on m01 (skipped without an install).
/// </summary>
public class AnetMeleeTests(ITestOutputHelper output)
{
    /// <summary>The test's output; a server or standby thread that outlives its test (a slow join under load) must not crash the host by logging after it ended.</summary>
    private readonly SafeLog log = new(output);

    internal sealed class SafeLog(ITestOutputHelper o)
    {
        public void WriteLine(string m) { try { o.WriteLine(m); } catch (InvalidOperationException) { } }
    }

    private static bool Wait(Func<bool> done, double seconds, Action step)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            step();
            if (done()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    public static IEnumerable<object[]> Servers() => AnetPunchTests.Servers();

    [Theory, MemberData(nameof(Servers))]
    public void AListedServerIsJoinedThroughTheLobbyFromBehindNats(string kind)
    {
        if (HostTests.InstallDir is not { } install) return;
        IDisposable srv;
        int port;
        if (kind == "real") { var r = new RealAnetServer(); srv = r; port = r.Port; }
        else { var f = new FakeAnetServer(); srv = f; port = f.Port; }
        using var _ = srv;
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.EndpointIndependent);

        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Lobby melee", MaxPlayers = 4 }, natA.Tunnel());
        server.Log += m => log.WriteLine($"server: {m}");
        server.ListOn([("127.0.0.1", port)], natA.PrivateOf(server.Port));
        server.Start();
        Assert.True(Wait(() => server.Anet!.AnyListed, 5, () => { }), server.AnetStatus);
        log.WriteLine(server.AnetStatus);

        using var browser = new SessionBrowser(natB.Tunnel());
        SessionBrowser.Found? game = null;
        Assert.True(Wait(() => (game = browser.Sessions.FirstOrDefault(f => f.Anet is not null)) is not null, 5, () => { browser.List("127.0.0.1", port); browser.Poll(); }),
            browser.ServerStatus);
        Assert.Equal("Lobby melee", game!.Info.Name);
        Assert.Equal("m01", game.Info.Arena);
        Assert.Equal(4, game.Info.MaxPlayers);
        // the host's NAT filters strangers: no direct answer, so no ping
        Assert.Equal(-1, game.Ping);
        Assert.Equal(natA.Public, game.EndPoint.Address);

        using var client = new MeleeClient(natB.Tunnel());
        client.Log += m => log.WriteLine($"client: {m}");
        client.ConnectVia("127.0.0.1", port, game.Anet!.Session, "joiner", "vcgcour1.vcf", privateOverride: null);
        var stages = new HashSet<string>();
        Assert.True(Wait(() => client.State != ClientState.Connecting, 25, () => { client.Poll(); if (client.Stage is { } s) stages.Add(s); }), "still connecting");
        log.WriteLine($"stages: {string.Join(" | ", stages)}");
        Assert.True(client.State == ClientState.Joined, client.Reason);
        Assert.Equal("m01", client.Accept!.Arena);
        Assert.Contains("Joining via server...", stages);
        // the listing follows the join
        Assert.True(Wait(() => browser.Sessions.Any(f => f.Anet is not null && f.Info.Players == 1), 6, () => { browser.List("127.0.0.1", port); browser.Poll(); client.Poll(); }));
        client.Leave();
    }

    /// <summary>
    /// Host migration across NATs: a host behind one NAT, listed on the anet server, and two players behind two others who
    /// joined through it (the host's own player is left out: the emulated NAT does not hairpin). The host quits; Ann, the lowest seat, takes over on her own game socket (the NAT
    /// mapping the host and the others knew), lists the game again on the same server, and Bob finds her by the
    /// addresses the host gave him, both sides probing, with no introduction.
    /// </summary>
    [Fact]
    public void AGameListedOnAnetMigratesAcrossNats()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var srv = new FakeAnetServer();
        int srvPort = srv.Port;   // read by the NAT callbacks of threads that can outlive the test
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.EndpointIndependent);
        using var natC = new NatBox(3, NatBox.Mapping.EndpointIndependent);
        var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Moving melee", HostMigration = true, Rounds = true, ListenHost = true, AllowHidden = true }, natA.Tunnel());
        using var browser = new SessionBrowser();
        var players = new List<(MeleeClient C, MigrationStandby S)>();
        NetMeleeServer? taken = null;
        try
        {
            server.Log += m => log.WriteLine($"host: {m}");
            server.ListOn([("127.0.0.1", srv.Port)], natA.PrivateOf(server.Port));
            server.Start();
            SessionBrowser.Found? game = null;
            Assert.True(Wait(() => (game = browser.Sessions.FirstOrDefault(f => f.Anet is not null)) is not null, 8, () => { browser.List("127.0.0.1", srv.Port); browser.Poll(); }));
            // Ann's game reaches its own server over the loopback without her NAT, as a real one does
            Func<IPEndPoint, bool> home = ep => ep.Address.Equals(IPAddress.Loopback) && ep.Port != srvPort;
            foreach (var (name, nat) in new[] { ("Ann", natB), ("Bob", natC) })
            {
                var direct = name == "Ann" ? home : null;
                var standby = new MigrationStandby(install, m => log.WriteLine($"{name}: {m}")) { Layer = () => new NatTunnelLayer(nat.Inside) { Direct = direct } };
                var c = new MeleeClient(new NatTunnelLayer(nat.Inside) { Direct = direct }) { CanHost = true, Standby = standby, HomeAddresses = [] };
                c.Log += m => log.WriteLine($"{name}: {m}");
                c.ConnectVia("127.0.0.1", srv.Port, game!.Anet!.Session, name, "vcgcour1.vcf");
                players.Add((c, standby));
                Assert.True(Wait(() => c.State == ClientState.Joined, 25, () => { foreach (var p in players) p.C.Poll(); }), c.Reason);
            }
            void PollAll() { foreach (var p in players) p.C.Poll(); }
            var (ann, annStandby) = players[0];
            var bob = players[1].C;
            // a savestate taken after Bob sat down (one from before has no seat for him, and he could not rejoin)
            uint seated = server.Invoke(h => h.Tick);
            Assert.True(Wait(() => ann.MigrationLink is { State: not null } l && l.StateTick > seated && annStandby.Ready && bob.Successor?.Slot == ann.Accept!.Slot, 30, PollAll));
            // the successor's address as the host saw it is her NAT's public one, not her own
            Assert.Equal(natB.Public, bob.Successor!.Endpoints[0].Address);
            server.Dispose();
            Assert.True(Wait(() => ann.State == ClientState.Joined && bob.State == ClientState.Joined && bob.Migrations == 1, 15, PollAll), $"{ann.State} {ann.Reason}; {bob.State} {bob.Reason}");
            taken = annStandby.Server!;
            Assert.True(Wait(() => taken.Invoke(h => h.Seats.Count) == 2 && taken.AwaitingRejoin == 0, 5, PollAll));
            // listed again, by the new host (its NAT's address), under the game's name
            Assert.True(Wait(() => browser.Sessions.Any(f => f.Anet is { } a && f.Info.Name == "Moving melee" && f.EndPoint.Address.Equals(natB.Public)), 10,
                () => { browser.List("127.0.0.1", srv.Port); browser.Poll(); PollAll(); }), string.Join("; ", browser.Sessions.Select(f => $"{f.Info.Name} at {f.EndPoint}")));
            // the game's Hidden Weapons & Cars went with it, and its new listing says so (a player who left comes back with a hidden car)
            Assert.True(taken.Invoke(h => h.Options.AllowHidden));
            Assert.True(browser.Sessions.First(f => f.Anet is not null && f.EndPoint.Address.Equals(natB.Public)).Info.MayBringHidden);
            Assert.Null(taken.Invoke(h => h.CheckJoin("vuchuey1.vcf", null)));
        }
        finally
        {
            server.Dispose();
            foreach (var (c, s) in players) { c.Dispose(); s.Dispose(); }
            taken?.Dispose();
        }
    }

    /// <summary>
    /// The successor cannot be reached: Ann is behind a symmetric NAT (her mapping to the host lets in the host alone, and
    /// her takeover's probe packets leave from new ports that Bob's NAT has never sent to). The host leaves; Ann takes over but no
    /// one finds her, and Bob's game ends as a lost connection within the give-up time, not hanging in "Migrating".
    /// </summary>
    [Fact]
    public void ASuccessorBehindASymmetricNatLeavesTheOthersToFallBack()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var natB = new NatBox(2, NatBox.Mapping.Symmetric);
        using var natC = new NatBox(3, NatBox.Mapping.EndpointIndependent);
        var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Stuck melee", HostMigration = true, Rounds = true, ListenHost = true });
        server.Log += m => log.WriteLine($"host: {m}");
        server.Start();
        var hal = new MeleeClient();
        hal.Connect("127.0.0.1", server.Port, "Hal", "vppirna1.vcf");
        var players = new List<(MeleeClient C, MigrationStandby S)>();
        void PollAll() { hal.Poll(); foreach (var p in players) p.C.Poll(); }
        NetMeleeServer? taken = null;
        try
        {
            Assert.True(Wait(() => hal.State == ClientState.Joined, 10, PollAll));
            server.MigrationOwner = hal.Accept!.Slot;
            foreach (var (name, nat) in new[] { ("Ann", natB), ("Bob", natC) })
            {
                // the takeover's own player reaches it over the loopback, not through the NAT
                Func<IPEndPoint, bool> home = ep => ep.Address.Equals(IPAddress.Loopback) && ep.Port != server.Port;
                var standby = new MigrationStandby(install, m => log.WriteLine($"{name}: {m}")) { Layer = () => new NatTunnelLayer(nat.Inside) { Direct = home } };
                var c = new MeleeClient(new NatTunnelLayer(nat.Inside) { Direct = home }) { CanHost = true, Standby = standby, HomeAddresses = [], MigrationGiveUp = 6 };
                c.Log += m => log.WriteLine($"{name}: {m}");
                c.Connect("127.0.0.1", server.Port, name, "vcgcour1.vcf");
                players.Add((c, standby));
                Assert.True(Wait(() => c.State == ClientState.Joined, 15, PollAll), c.Reason);
            }
            var (ann, annStandby) = players[0];
            var bob = players[1].C;
            // a savestate taken after Bob sat down (one from before has no seat for him, and he could not rejoin)
            uint seated = server.Invoke(h => h.Tick);
            Assert.True(Wait(() => ann.MigrationLink is { State: not null } l && l.StateTick > seated && annStandby.Ready && bob.Successor?.Slot == ann.Accept!.Slot, 30, PollAll));
            // the host leaves
            hal.Dispose();
            server.Dispose();
            var t = Stopwatch.StartNew();
            Assert.True(Wait(() => bob.State == ClientState.Disconnected, 15, PollAll), $"{bob.State} {bob.Reason}");
            log.WriteLine($"Bob fell back after {t.Elapsed.TotalSeconds:0.00} s: {bob.Reason}; Ann {ann.State}, became host {ann.BecameHost}");
            Assert.StartsWith("connection to the host lost", bob.Reason);
            Assert.Equal(0, bob.Migrations);
            // Ann's game goes on, hers alone
            Assert.True(Wait(() => ann.State == ClientState.Joined, 5, PollAll), $"{ann.State} {ann.Reason}");
            Assert.True(ann.BecameHost);
            taken = annStandby.Server;
        }
        finally
        {
            hal.Dispose();
            server.Dispose();
            foreach (var (c, s) in players) { c.Dispose(); s.Dispose(); }
            taken?.Dispose();
        }
    }

    /// <summary>
    /// The testers' report: everyone had Hidden Weapons &amp; Cars on, yet only the one host whose game answered directly
    /// could be joined with a hidden car. A host behind a NAT that filters strangers is known by its listing alone, which
    /// did not carry the setting, so every joiner's own check refused hidden cars as if it were off. The listing says it now,
    /// and a hidden car gets in through the lobby.
    /// </summary>
    [Fact]
    public void AListedGameBehindANatSaysItAllowsHiddenCarsAndOneGetsIn()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var srv = new FakeAnetServer();
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.EndpointIndependent);
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Hidden melee", MaxPlayers = 4, AllowHidden = true }, natA.Tunnel());
        server.Log += m => log.WriteLine($"server: {m}");
        server.ListOn([("127.0.0.1", srv.Port)], natA.PrivateOf(server.Port));
        server.Start();
        using var browser = new SessionBrowser(natB.Tunnel());
        SessionBrowser.Found? game = null;
        Assert.True(Wait(() => (game = browser.Sessions.FirstOrDefault(f => f.Anet is not null)) is not null, 6, () => { browser.List("127.0.0.1", srv.Port); browser.Poll(); }));
        Assert.Equal(-1, game!.Ping);           // no direct answer: the listing is all the roster has
        Assert.True(game.Info.Hidden);
        Assert.True(game.Info.HiddenKnown);
        Assert.True(game.Info.MayBringHidden);

        using var client = new MeleeClient(natB.Tunnel());
        client.Log += m => log.WriteLine($"client: {m}");
        client.ConnectVia("127.0.0.1", srv.Port, game.Anet!.Session, "huey", "vuchuey1.vcf");
        Assert.True(Wait(() => client.State != ClientState.Connecting, 25, client.Poll), "still connecting");
        Assert.True(client.State == ClientState.Joined, client.Reason);
        client.Leave();
    }

    /// <summary>
    /// A tester: the roster's game popup showed no players for a game in progress. A host behind a NAT that filters
    /// strangers never answered the roster's direct question, so the drivers (which only the direct answer carries) never
    /// came. The roster now asks the server to introduce it, as a join does; the host opens toward it and answers.
    /// </summary>
    [Fact]
    public void ARunningGameBehindANatShowsItsDriversInTheRoster()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var srv = new FakeAnetServer();
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.EndpointIndependent);
        using var natC = new NatBox(3, NatBox.Mapping.EndpointIndependent);
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Busy melee", MaxPlayers = 4 }, natA.Tunnel());
        server.Log += m => log.WriteLine($"server: {m}");
        server.ListOn([("127.0.0.1", srv.Port)], natA.PrivateOf(server.Port));
        server.Start();
        Assert.True(Wait(() => server.Anet!.AnyListed, 5, () => { }), server.AnetStatus);
        // a player in the game, joined through the server from behind its own NAT
        using var joiner = new SessionBrowser(natC.Tunnel());
        SessionBrowser.Found? listed = null;
        Assert.True(Wait(() => (listed = joiner.Sessions.FirstOrDefault(f => f.Anet is not null)) is not null, 6, () => { joiner.List("127.0.0.1", srv.Port); joiner.Poll(); }));
        using var client = new MeleeClient(natC.Tunnel());
        client.ConnectVia("127.0.0.1", srv.Port, listed!.Anet!.Session, "dewey", "vcgcour1.vcf");
        Assert.True(Wait(() => client.State != ClientState.Connecting, 25, client.Poll), "still connecting");
        Assert.True(client.State == ClientState.Joined, client.Reason);

        using var browser = new SessionBrowser(natB.Tunnel()) { IntroduceAfter = 1 };
        SessionBrowser.Found? game = null;
        Assert.True(Wait(() => (game = browser.Sessions.FirstOrDefault(f => f.Anet is not null && f.Info.Drivers.Count > 0)) is not null, 15,
            () => { browser.List("127.0.0.1", srv.Port); browser.Poll(); client.Poll(); }), "no drivers came");
        Assert.Contains(game!.Info.Drivers, d => d.Name == "dewey");
        Assert.True(game.Ping >= 0);
        Assert.Single(browser.Sessions);       // the answer fills in the listing, no second row
        client.Leave();
    }

    /// <summary>A listed game with the setting off says so too; the server's refusal names the setting and what to do.</summary>
    [Fact]
    public void AListedGameWithHiddenCarsOffSaysSoAndTheRefusalSaysWhatToChange()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var srv = new FakeAnetServer();
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Stock melee", MaxPlayers = 4 });
        server.ListOn([("127.0.0.1", srv.Port)]);
        server.Start();
        using var browser = new SessionBrowser();
        SessionBrowser.Found? game = null;
        Assert.True(Wait(() => (game = browser.Sessions.FirstOrDefault(f => f.Anet is not null)) is not null, 6, () => { browser.List("127.0.0.1", srv.Port); browser.Poll(); }));
        Assert.False(game!.Info.Hidden);
        Assert.True(game.Info.HiddenKnown);
        Assert.False(game.Info.MayBringHidden);
        using var client = new MeleeClient();
        client.ConnectVia("127.0.0.1", srv.Port, game.Anet!.Session, "huey", "vuchuey1.vcf");
        Assert.True(Wait(() => client.State != ClientState.Connecting, 25, client.Poll), "still connecting");
        Assert.Equal(ClientState.Rejected, client.State);
        Assert.Contains("Hidden Weapons & Cars off", client.Reason);
        Assert.Contains("turns Hidden Wpns & Cars on in the host form", client.Reason);
    }

    /// <summary>A game found both in the server's list and at the address book's own address is one row: the listing, with the direct answer's drivers and ping.</summary>
    [Fact]
    public void AGameFoundDirectlyAndListedIsOneRowWithAPing()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var srv = new FakeAnetServer();
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Both ways", MaxPlayers = 4 });
        server.ListOn([("127.0.0.1", srv.Port)]);
        server.Start();
        Assert.True(Wait(() => server.Anet!.AnyListed, 5, () => { }), server.AnetStatus);
        using var browser = new SessionBrowser();
        Assert.True(Wait(() => browser.Sessions.Any(f => f.Anet is not null && f.Ping >= 0), 6, () =>
        {
            browser.Ask("127.0.0.1", server.Port);
            browser.List("127.0.0.1", srv.Port);
            browser.Poll();
        }), string.Join("; ", browser.Sessions.Select(f => $"{f.Info.Name} anet={f.Anet is not null} ping={f.Ping} at {f.EndPoint}")));
        Thread.Sleep(1200);
        browser.Ask("127.0.0.1", server.Port); browser.List("127.0.0.1", srv.Port);
        for (int i = 0; i < 20; i++) { browser.Poll(); Thread.Sleep(10); }
        Assert.Single(browser.Sessions);
    }
}
