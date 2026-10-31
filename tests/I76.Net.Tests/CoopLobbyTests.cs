using System.Diagnostics;
using I76.Net;
using I76.Server;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// A co-op game's lobby (protocol 20, docs/multiplayer.md "The co-op lobby"): players gather before the mission, pick cars, mark
/// ready and chat; the host starts it; every seat is dealt at once and the mission's first tick waits for every game to be loaded.
/// </summary>
public class CoopLobbyTests(ITestOutputHelper output)
{
    private sealed class Lobby : IDisposable
    {
        public NetMeleeServer Server { get; }
        public readonly List<(string Name, MeleeClient Client)> Players = [];
        private readonly ITestOutputHelper _out;

        public Lobby(string install, ITestOutputHelper output, bool listenHost = true, int minPlayers = 1, double loadSeconds = 30, int max = 4)
        {
            _out = output;
            Server = new NetMeleeServer(new ServerOptions
            {
                Install = install, Arena = "t01", Coop = "t01", MaxPlayers = max, Port = 0, Seed = 7, Name = "co-op", ListenHost = listenHost,
                Lobby = true, LobbyMinPlayers = minPlayers, LoadSeconds = loadSeconds,
            });
            Server.Log += m => output.WriteLine($"[server] {m}");
            Server.Start();
        }

        public MeleeClient Join(string name, string car = Protocol.MissionCar, bool toLobby = true)
        {
            var c = new MeleeClient();
            c.Log += m => _out.WriteLine($"[{name}] {m}");
            c.Connect("127.0.0.1", Server.Port, name, car);
            Players.Add((name, c));
            if (toLobby) Assert.True(Wait(() => c.State == ClientState.InLobby && c.Lobby!.Members.Any(m => m.Name == name)), $"{name} did not reach the lobby: {c.State} {c.Reason}");
            return c;
        }

        public bool Wait(Func<bool> done, int ms = 10000)
        {
            var t = Stopwatch.StartNew();
            while (!done() && t.ElapsedMilliseconds < ms)
            {
                foreach (var (_, c) in Players) c.Poll();
                Thread.Sleep(5);
            }
            return done();
        }

        public void Pump(int ms) => Wait(() => false, ms);
        public uint Tick => Server.Invoke(h => (uint)h.Tick);

        public void Dispose()
        {
            foreach (var (_, c) in Players) c.Dispose();
            Server.Dispose();
        }
    }

    private static string? Install => HostTests.InstallDir;

    [Fact]
    public void TheLobbyTravelsOnTheWire()
    {
        var s = new LobbyState("t01", 4, 1, 0, 0, [new LobbyMember(0, "Hal", Protocol.MissionCar, false), new LobbyMember(1, "Ann", "vcgcour1.vcf", true)], "vppirna1.vcf");
        var w = new NetWriter();
        s.Write(w);
        var r = new NetReader(w.ToArray());
        Assert.Equal(MsgType.LobbyState, (MsgType)r.U8());
        var back = LobbyState.Read(ref r);
        Assert.Equal((s.Mission, s.Max, s.You, s.Host, s.AutoMin), (back.Mission, back.Max, back.You, back.Host, back.AutoMin));
        Assert.Equal(s.Members, back.Members);
        Assert.Equal("vppirna1.vcf", back.OwnCar);
        Assert.False(back.IsHost);
        Assert.Equal("Ann", back.Me!.Name);

        w = new NetWriter();
        new LobbySet(true, "vcgcour1.vcf", [1, 2, 3], [9]).Write(w);
        r = new NetReader(w.ToArray());
        r.U8();
        var set = LobbySet.Read(ref r);
        Assert.Equal((true, "vcgcour1.vcf"), (set.Ready, set.Car));
        Assert.Equal(new byte[] { 1, 2, 3 }, set.Config);
        w = new NetWriter();
        new LobbyChat("Hal", "ready when you are").Write(w);
        r = new NetReader(w.ToArray());
        r.U8();
        Assert.Equal(new LobbyChat("Hal", "ready when you are"), LobbyChat.Read(ref r));

        // the listing says the game is in its lobby: the session answer and the anet tag
        var info = new SessionInfo(1, Protocol.Version, "co-op", "t01", 2, 4, false, false, 0, 0, false, [], Coop: true, Lobby: true);
        w = new NetWriter();
        info.Write(w);
        Assert.True(SessionInfo.TryRead(w.ToArray())!.Lobby);
        w = new NetWriter();
        (info with { Lobby = false }).Write(w);
        Assert.False(SessionInfo.TryRead(w.ToArray())!.Lobby);
        var tag = new I76.Net.Anet.I26SessionTag(Protocol.Version, false, false, false, false, I76.Sim.Missions.MeleeKind.Combat, 2, I76.Sim.Missions.MeleeGoal.None, "t01", Coop: true, Lobby: true);
        var tagBack = I76.Net.Anet.I26SessionTag.Read(tag.Write())!;
        Assert.True(tagBack.Coop && tagBack.Lobby);
        Assert.Equal(I76.Sim.Missions.MeleeGoal.None, tagBack.Goal);
        Assert.False(I76.Net.Anet.I26SessionTag.Read((tag with { Lobby = false }).Write())!.Lobby);
    }

    /// <summary>Joining a hosted game that has not started puts you in the lobby: nobody is seated, the mission does not run, and the listing says so.</summary>
    [Fact]
    public void JoiningAnUnstartedGameIsTheLobbyNotTheMission()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann", "vcgcour1.vcf");
        Assert.All(g.Players, p => Assert.Null(p.Client.Accept));
        Assert.True(g.Wait(() => hal.Lobby!.Members.Count == 2));
        Assert.Equal(["Hal", "Ann"], hal.Lobby!.Members.Select(m => m.Name));
        Assert.True(hal.Lobby.IsHost);
        Assert.False(ann.Lobby!.IsHost);
        Assert.Equal("vcgcour1.vcf", hal.Lobby.Members[1].Car);
        Assert.Equal(Protocol.MissionCar, hal.Lobby.Members[0].Car);
        Assert.Equal("t01", ann.Lobby.Mission);
        // no seats, and the mission has not ticked
        Assert.Equal(0, g.Server.Invoke(h => h.Seats.Count));
        uint tick = g.Tick;
        g.Pump(300);
        Assert.Equal(tick, g.Tick);
        // the listing: in its lobby, two players
        var q = new SessionInfoProbe(g.Server.Port).Ask();
        Assert.NotNull(q);
        Assert.True(q!.Lobby);
        Assert.Equal(2, q.Players);
        Assert.Equal(["Hal", "Ann"], q.Drivers.Select(d => d.Name));
    }

    [Fact]
    public void ReadyMarksChatAndCarsAreEveryonesToSee()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        ann.SendLobbySet(true);
        Assert.True(g.Wait(() => hal.Lobby!.Members.Single(m => m.Name == "Ann").Ready));
        // a new car is checked, answered, and takes the ready mark off
        ann.SendLobbySet(true, "vcgcour1.vcf");
        Assert.True(g.Wait(() => ann.ReconfigureAnswers.Count > 0));
        Assert.True(ann.ReconfigureAnswers.Dequeue().Taken);
        Assert.True(g.Wait(() => hal.Lobby!.Members.Single(m => m.Name == "Ann") is { Car: "vcgcour1.vcf", Ready: false }));
        // chat reaches everyone in the lobby
        ann.SendChat("let's go");
        Assert.True(g.Wait(() => hal.LobbyLines.Count > 0 && ann.LobbyLines.Count > 0));
        Assert.Equal(new LobbyChat("Ann", "let's go"), hal.LobbyLines.Dequeue());
        // only the host starts it
        ann.SendLobbyStart(true);
        g.Pump(300);
        Assert.Null(ann.Accept);
        Assert.Null(hal.Accept);
    }

    /// <summary>The host starts when everyone is ready, or by force: the seats are dealt at once, in the order they came, each in its chosen car.</summary>
    [Fact]
    public void TheHostStartsAndEverySeatIsDealtTogether()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann", "vcgcour1.vcf");
        var bob = g.Join("Bob");
        // not everyone is ready: the plain start is refused
        hal.SendLobbyStart(false);
        g.Pump(400);
        Assert.All(g.Players, p => Assert.Null(p.Client.Accept));
        ann.SendLobbySet(true);
        bob.SendLobbySet(true);
        Assert.True(g.Wait(() => hal.Lobby!.Members.Where(m => m.Name != "Hal").All(m => m.Ready)));
        hal.SendLobbyStart(false);
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined)), string.Join(", ", g.Players.Select(p => $"{p.Name} {p.Client.State}")));
        Assert.Equal([0, 1, 2], g.Players.Select(p => (int)p.Client.Accept!.Coop!.Seat));
        // every accept names every seat's car: Ann's own, the mission's for the rest
        foreach (var (_, c) in g.Players)
        {
            Assert.True(c.Accept!.Coop!.SeatCars.Count >= 3);
            Assert.Equal("vcgcour1.vcf", c.Accept.Coop.SeatCars[1]);
            Assert.Null(c.Lobby);
        }
        Assert.Equal("vcgcour1.vcf", ann.Accept!.Car);
        Assert.Equal(3, g.Server.Invoke(h => h.Seats.Count));
    }

    [Fact]
    public void AForcedStartLeavesTheUnreadyBehindInTheirSeats()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        hal.SendLobbyStart(true);
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined)));
        Assert.Equal(2, g.Server.Invoke(h => h.Seats.Count));
    }

    /// <summary>The mission's first tick waits until every game has built it and sent an input; one that never does is left behind after the timeout.</summary>
    [Fact]
    public void TheFirstTickWaitsForEveryoneToLoad()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output, loadSeconds: 3);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        var bob = g.Join("Bob");
        hal.SendLobbyStart(true);
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined)));
        uint tick = g.Tick;
        // Hal and Ann are loaded and send inputs; Bob is still loading: nothing moves
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1200)
        {
            hal.SendInput(new CarInput()); ann.SendInput(new CarInput());
            g.Pump(40);
        }
        Assert.Equal(tick, g.Tick);
        // Bob's game is up: the mission starts for all of them together
        bob.SendInput(new CarInput());
        Assert.True(g.Wait(() => { hal.SendInput(new CarInput()); return g.Tick > tick + 5; }), "the mission did not start once everyone had loaded");
        Assert.True(g.Wait(() => hal.Snapshots.Count > 0 && bob.Snapshots.Count > 0));
    }

    [Fact]
    public void ALoaderStillAwayAfterTheTimeoutIsLeftBehind()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output, loadSeconds: 1.5);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        hal.SendLobbyStart(true);
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined)));
        uint tick = g.Tick;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 800) { hal.SendInput(new CarInput()); g.Pump(40); }
        Assert.Equal(tick, g.Tick);
        // Ann never sent: the mission starts without her once the time is up, and she is still seated
        Assert.True(g.Wait(() => { hal.SendInput(new CarInput()); return g.Tick > tick + 5; }, 8000), "the mission never started without the straggler");
        Assert.Equal(2, g.Server.Invoke(h => h.Seats.Count));
        // she plays in when she is loaded, as a late joiner does: her inputs are taken from then
        ann.SendInput(new CarInput());
        Assert.True(g.Wait(() => ann.Snapshots.Count > 0));
    }

    /// <summary>After the start the lobby is gone: a new player is seated at once, as a late joiner always was.</summary>
    [Fact]
    public void ALateJoinerAfterTheStartTakesTheOldPath()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        hal.SendLobbyStart(true);
        Assert.True(g.Wait(() => hal.State == ClientState.Joined));
        hal.SendInput(new CarInput());
        Assert.True(g.Wait(() => { hal.SendInput(new CarInput()); return g.Tick > 10; }));
        var dee = g.Join("Dee", toLobby: false);
        Assert.True(g.Wait(() => dee.State == ClientState.Joined), $"{dee.State} {dee.Reason}");
        Assert.Equal(1, dee.Accept!.Coop!.Seat);
        Assert.Null(dee.Lobby);
        var info = new SessionInfoProbe(g.Server.Port).Ask();
        Assert.False(info!.Lobby);
    }

    /// <summary>
    /// The lobby has no host to hand over to: the listen host is the server, and no mission has begun, so there is no state to migrate.
    /// The others are told, cleanly, and the game takes no more joins.
    /// </summary>
    [Fact]
    public void TheHostLeavingClosesTheLobbyWithAMessage()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        hal.Leave();
        Assert.True(g.Wait(() => ann.State == ClientState.Disconnected), $"{ann.State}");
        Assert.Equal(NetMeleeServer.LobbyClosedText, ann.Reason);
        var eve = g.Join("Eve", toLobby: false);
        Assert.True(g.Wait(() => eve.State == ClientState.Rejected));
        Assert.Equal(NetMeleeServer.LobbyClosedText, eve.Reason);
    }

    [Fact]
    public void AGuestLeavingTheLobbyJustLeaves()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        var bob = g.Join("Bob");
        ann.Leave();
        Assert.True(g.Wait(() => hal.Lobby!.Members.Select(m => m.Name).SequenceEqual(["Hal", "Bob"])));
        Assert.Equal(InLobbyHost(hal), hal.Lobby!.Host);
        // a seat is free for another
        var cy = g.Join("Cy");
        Assert.Equal(3, g.Server.Players);
        Assert.True(g.Wait(() => hal.Lobby!.Members.Count == 3));
    }

    private static byte InLobbyHost(MeleeClient c) => c.Lobby!.Members[0].Id;

    [Fact]
    public void TheHostBootsAPlayerFromTheLobby()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output);
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        Assert.Null(g.Server.Kick("Ann"));
        Assert.True(g.Wait(() => ann.State == ClientState.Disconnected));
        Assert.Equal(Protocol.Booted, ann.Reason);
        Assert.True(g.Wait(() => hal.Lobby!.Members.Count == 1));
    }

    [Fact]
    public void TheLobbyIsFullAtTheSeatsOfTheMission()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output, max: 2);
        g.Join("Hal");
        g.Join("Ann");
        var bob = g.Join("Bob", toLobby: false);
        Assert.True(g.Wait(() => bob.State == ClientState.Rejected));
        Assert.Contains("full", bob.Reason);
    }

    /// <summary>A dedicated server has no host: it starts the mission when the players there are all ready and enough have come.</summary>
    [Fact]
    public void ADedicatedServerStartsWhenEnoughAreAllReady()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output, listenHost: false, minPlayers: 2);
        var hal = g.Join("Hal");
        Assert.Equal(LobbyState.NoHost, hal.Lobby!.Host);
        Assert.Equal(2, hal.Lobby.AutoMin);
        hal.SendLobbySet(true);
        g.Pump(500);
        Assert.Null(hal.Accept);       // one ready player is not enough (--start-when 2)
        var ann = g.Join("Ann");
        g.Pump(300);
        Assert.Null(hal.Accept);       // Ann is not ready
        ann.SendLobbySet(true);
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined)));
        Assert.Equal([0, 1], g.Players.Select(p => (int)p.Client.Accept!.Coop!.Seat));
        // the console's start forces it with nobody ready
    }

    [Fact]
    public void TheConsoleCanForceTheStart()
    {
        if (Install is not { } install) return;
        using var g = new Lobby(install, output, listenHost: false);
        var hal = g.Join("Hal");
        Assert.Null(g.Server.StartLobby(force: true));
        Assert.True(g.Wait(() => hal.State == ClientState.Joined));
        Assert.NotNull(g.Server.StartLobby());      // already started
    }

    /// <summary>Without a lobby (--autostart, and every game before protocol 20) a join is seated at once and the first input starts the mission.</summary>
    [Fact]
    public void WithoutALobbyAJoinIsSeatedAtOnce()
    {
        if (Install is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "t01", Coop = "t01", MaxPlayers = 4, Port = 0, Seed = 7, Name = "co-op", ListenHost = true });
        server.Start();
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "Hal", Protocol.MissionCar);
        var t = Stopwatch.StartNew();
        while (c.State != ClientState.Joined && t.ElapsedMilliseconds < 10000) { c.Poll(); Thread.Sleep(5); }
        Assert.Equal(ClientState.Joined, c.State);
        Assert.Null(c.Lobby);
        Assert.False(server.InLobby);
    }

    /// <summary>The roster's direct query, as the shell makes it.</summary>
    private sealed class SessionInfoProbe(int port)
    {
        public SessionInfo? Ask()
        {
            using var b = new SessionBrowser();
            var t = Stopwatch.StartNew();
            ulong asked = 0;
            while (t.ElapsedMilliseconds < 5000)
            {
                if ((ulong)t.ElapsedMilliseconds - asked > 500) { asked = (ulong)t.ElapsedMilliseconds; b.Ask("127.0.0.1", port); }
                b.Poll();
                if (b.Sessions.FirstOrDefault(f => f.Info.Name == "co-op") is { } f) return f.Info;
                Thread.Sleep(10);
            }
            return null;
        }
    }
}
