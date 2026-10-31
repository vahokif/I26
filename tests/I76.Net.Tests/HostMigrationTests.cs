using System.Diagnostics;
using System.Net;
using I76.Net;
using I76.Server;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// Host migration (docs/multiplayer.md "Host migration"): a client-hosted game whose host leaves or drops goes on under
/// the lowest seat that can host, the others rejoining it with their seats, cars and scores.
/// </summary>
public class HostMigrationTests(ITestOutputHelper output)
{
    [Fact]
    public void TheWireCarriesTheMigrationMessages()
    {
        var w = new NetWriter();
        new JoinRequest(Protocol.Version, "Ann", "vppirna1.vcf", 2, [1, 2], "pw") { Rejoin = (5, 0x0123456789abcdefUL) }.Write(w);
        var r = new NetReader(w.ToArray());
        var j = JoinRequest.Read(ref r);
        Assert.Equal(((byte)5, 0x0123456789abcdefUL), j.Rejoin);
        Assert.Equal("pw", j.Password);
        w.Clear();
        new JoinRequest(Protocol.Version, "Ann", "vppirna1.vcf", 2, []).Write(w);
        r = new NetReader(w.ToArray());
        Assert.Null(JoinRequest.Read(ref r).Rejoin);

        w.Clear();
        var info = new SuccessorInfo(3, "Bob", [new IPEndPoint(IPAddress.Parse("10.0.0.7"), 51234), new IPEndPoint(IPAddress.Loopback, 51234)], 77, 99, "Host");
        info.Write(w);
        r = new NetReader(w.ToArray());
        Assert.Equal(MsgType.Successor, (MsgType)r.U8());
        var back = SuccessorInfo.Read(ref r);
        Assert.Equal((info.Slot, info.Name, info.GameToken, info.SeatToken, info.HostName), (back.Slot, back.Name, back.GameToken, back.SeatToken, back.HostName));
        Assert.Equal(info.Endpoints, back.Endpoints);

        // the inputs' log: the full list, then only what changed
        var buf = new MigrationBuffer();
        var a = new CarInput { Buttons = CarButtons.ThrottleUp };
        var b = new CarInput { Buttons = CarButtons.SteerLeft };
        w.Clear();
        MigrationBuffer.WriteLog(w, 10, false, true, [(0, a), (2, b)]);
        r = new NetReader(w.ToArray()); r.U8();
        buf.AddLog(ref r, w.Length);
        w.Clear();
        MigrationBuffer.WriteLog(w, 11, true, false, [(0, b)]);
        r = new NetReader(w.ToArray()); r.U8();
        buf.AddLog(ref r, w.Length);
        Assert.Equal(new[] { a, default, b }, buf.Log[0].Inputs);
        Assert.Equal(new[] { b, default, b }, buf.Log[1].Inputs);
        Assert.True(buf.Log[1].Barrier);
        Assert.Equal(HostMigration.ProbeSize, HostMigration.Probe(HostMigration.ProbeKind.Answer, 5).Length);
        Assert.True(HostMigration.TryReadProbe(HostMigration.Probe(HostMigration.ProbeKind.Server, 5), out var kind, out ulong token));
        Assert.Equal((HostMigration.ProbeKind.Server, 5UL), (kind, token));
    }

    /// <summary>A listen host, its own player, and players who can take over (each with its standby).</summary>
    private sealed class Game : IDisposable
    {
        public required NetMeleeServer Server;
        public required MeleeClient HostPlayer;
        public readonly List<(MeleeClient Client, MigrationStandby Standby, LagProxy? Proxy)> Players = [];
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        private double _input;
        /// <summary>Called after every round of polls (a test keeping what the clients were sent).</summary>
        public Action? Polled;
        /// <summary>With mirrors on, every joined client draws a world as the game does (ClientWorld), and what they logged is kept.</summary>
        public bool Mirrors;
        public readonly Dictionary<MeleeClient, (JoinAccept Accept, ClientWorld World)> Worlds = [];
        public readonly List<string> MirrorLog = [];
        private double _frame;

        public IEnumerable<MeleeClient> Clients => Players.Select(p => p.Client).Prepend(HostPlayer);

        /// <summary>Polls everyone, each client driving (throttle, a little steering), until <paramref name="done"/>.</summary>
        public bool Wait(Func<bool> done, int ms = 20000)
        {
            var t = Stopwatch.StartNew();
            while (!done() && t.ElapsedMilliseconds < ms)
            {
                foreach (var c in Clients) c.Poll();
                Polled?.Invoke();
                if (Mirrors) Frame();
                if (Clock.Elapsed.TotalSeconds - _input >= 0.05)
                {
                    _input = Clock.Elapsed.TotalSeconds;
                    foreach (var c in Clients)
                        if (c.State is ClientState.Joined or ClientState.Migrating)
                            c.SendInput(new CarInput { Buttons = CarButtons.ThrottleUp | ((c.InputTick / 40) % 2 == 0 ? CarButtons.SteerLeft : 0) });
                }
                Thread.Sleep(5);
            }
            return done();
        }

        private void Frame()
        {
            double now = Clock.Elapsed.TotalSeconds, dt = now - _frame;
            if (dt < 1 / 60.0) return;
            _frame = now;
            foreach (var c in Clients)
            {
                if (!Worlds.TryGetValue(c, out var w) || !ReferenceEquals(w.Accept, c.Accept))
                {
                    if (c.Accept is not { } acc || c.State != ClientState.Joined) continue;
                    string who = c.Accept.Slot.ToString();
                    var world = new ClientWorld(HostTests.Mirror(acc.Arena), acc);
                    world.Log += m => { lock (MirrorLog) MirrorLog.Add($"[mirror {who}] {m}"); };
                    Worlds[c] = w = (acc, world);
                }
                w.World.Advance(dt, c);
            }
        }

        public void Dispose()
        {
            foreach (var (c, s, p) in Players) { s.Server?.Dispose(); c.Dispose(); s.Dispose(); p?.Dispose(); }
            HostPlayer.Dispose();
            Server.Dispose();
        }
    }

    private Game Start(string install, int players, bool proxies = false, string? badStandbyInstall = null, bool inviteOnly = false, Func<ServerOptions, ServerOptions>? tweak = null)
    {
        var server = new NetMeleeServer((tweak ?? (o => o))(new ServerOptions
        {
            Install = install, Arena = "m01", Port = 0, Rounds = true, TimeLimit = 600, HostMigration = true, Name = "migrate", Seed = 5,
            InviteOnly = inviteOnly, ListenHost = true,
        }));
        server.Log += m => output.WriteLine($"[host] {m}");
        server.Start();
        var hostPlayer = new MeleeClient();
        var vfs = server.Invoke(h => h.Vfs);
        bool restricted = !server.Invoke(h => h.Options.Restrictions.IsEmpty);
        byte[]? Unarmed(string stem, string who) { if (!restricted) return null; var t = I76.Sim.Trip.TripCar.FromVcf(vfs.Read(stem + "1.vcf")); t.Variant = who; t.Weapons = []; return t.ToVcf(); }
        if (restricted) hostPlayer.Connect("127.0.0.1", server.Port, "Hal", "vppirna4.vcf", config: Unarmed("vppirna", "Hal's"));
        else hostPlayer.Connect("127.0.0.1", server.Port, "Hal", "vppirna1.vcf");
        var g = new Game { Server = server, HostPlayer = hostPlayer };
        Assert.True(g.Wait(() => hostPlayer.State == ClientState.Joined));
        server.MigrationOwner = hostPlayer.Accept!.Slot;
        string[] names = ["Ann", "Bob", "Cy", "Dee"];
        string[] cars = ["vcgcour1.vcf", "valepre1.vcf", "vppirna1.vcf", "vcgcour1.vcf"];
        for (int i = 0; i < players; i++)
        {
            string name = names[i];
            var c = new MeleeClient { CanHost = true, MigrationGiveUp = 6 };
            var standby = new MigrationStandby(i == 0 && badStandbyInstall is not null ? badStandbyInstall : install, m => output.WriteLine($"[{name}] {m}"));
            c.Standby = standby;
            c.Log += m => output.WriteLine($"[{name}] {m}");
            LagProxy? proxy = proxies ? new LagProxy(new IPEndPoint(IPAddress.Loopback, server.Port), 10, 0) : null;
            if (restricted) c.Connect("127.0.0.1", proxy?.Port ?? server.Port, name, cars[i].Replace("1.vcf", "4.vcf"), config: Unarmed(cars[i][..^5], name + "'s"));
            else c.Connect("127.0.0.1", proxy?.Port ?? server.Port, name, cars[i]);
            g.Players.Add((c, standby, proxy));
            // one at a time, so the seats are in this order (an invite-only game's host lets each in)
            Assert.True(g.Wait(() => { if (server.PendingInvite is not null) server.AnswerInvite(InviteAnswer.Accept); return c.State == ClientState.Joined; }));
        }
        return g;
    }

    private record SeatView(int Slot, string Name, string Car, int Score, int Kills, int Team);

    private static List<SeatView> Seats(NetMeleeServer s) =>
        s.Invoke(h => h.Seats.Select(x => new SeatView(x.Slot, x.Name, x.Player.CarFile, x.Player.Score, x.Player.Kills, x.Player.Team)).ToList());

    /// <summary>Scores to carry over, set on the host's game (outside the inputs, so a fresh savestate follows).</summary>
    private static void GiveScores(Game g)
    {
        g.Server.Invoke(h =>
        {
            int k = 1;
            foreach (var s in h.Seats)
            {
                typeof(MeleePlayer).GetProperty("Score")!.SetValue(s.Player, 1000 * k);
                typeof(MeleePlayer).GetProperty("Kills")!.SetValue(s.Player, k++);
            }
        });
        g.Server.MigrationBarrier();
    }

    /// <summary>Waits for the successor to be named and to hold a savestate taken after now, with its game built.</summary>
    private static void WaitForSuccessor(Game g, int index)
    {
        var (c, standby, _) = g.Players[index];
        uint now = g.Server.Invoke(h => h.Tick);
        Assert.True(g.Wait(() => g.Server.SuccessorSlot == c.Accept!.Slot && c.Successor?.Slot == c.Accept.Slot));
        Assert.True(g.Wait(() => c.MigrationLink is { State: not null } l && l.StateTick > now && standby.Ready, 30000), "no savestate, or the standby game was not built");
        // everyone else has heard who it is
        Assert.True(g.Wait(() => g.Clients.All(x => x.State != ClientState.Joined || x.Successor?.Slot == c.Accept!.Slot)));
    }

    /// <summary>
    /// The host quits cleanly with three players: the lowest seat takes over at once; every player rejoins it with its
    /// slot, car, team and score; the round's clock carries on from where it was; the host's own player has left; and the
    /// new host names the next successor in turn.
    /// </summary>
    [Fact]
    public void TheHostQuitsAndTheLowestSeatTakesOver()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 3);
        GiveScores(g);
        WaitForSuccessor(g, 0);
        g.Wait(() => false, 1500);    // a little play since the savestate: replayed from the inputs
        var before = Seats(g.Server);
        float clock = g.Server.Invoke(h => h.Setup.Script!.Host.Now);
        uint tick = g.Server.Invoke(h => h.Tick);
        int hostSlot = g.HostPlayer.Accept!.Slot;
        output.WriteLine($"before: tick {tick}, clock {clock:0.00}, {string.Join("; ", before)}; stream {g.Server.MigrationBytes} B, {g.Server.MigrationStates} states, the last {g.Players[0].Client.MigrationLink!.State!.Length} B");

        var quit = Stopwatch.StartNew();
        g.HostPlayer.Dispose();
        g.Server.Dispose();
        var players = g.Players.Select(p => p.Client).ToList();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Joined && c.Migrations == 1), 15000),
            string.Join(", ", players.Select(c => $"{c.State} {c.Reason}")));
        double gap = quit.Elapsed.TotalSeconds;
        output.WriteLine($"takeover: all {players.Count} players back after {gap:0.00} s (Ann's own {players[0].MigrationEnded - players[0].MigrationBegan:0.00} s)");
        Assert.True(gap < 5, $"took {gap:0.00} s");

        var ann = g.Players[0];
        Assert.True(ann.Client.BecameHost);
        var server = Assert.IsType<NetMeleeServer>(ann.Standby.Server);
        Assert.True(g.Wait(() => server.AwaitingRejoin == 0 && server.Invoke(h => h.Seats.All(s => s.Slot != hostSlot))));
        var after = Seats(server);
        output.WriteLine($"after: {string.Join("; ", after)}");
        Assert.Equal(before.Where(s => s.Slot != hostSlot), after);
        float clockAfter = server.Invoke(h => h.Setup.Script!.Host.Now);
        Assert.InRange(clockAfter, clock, clock + 6);
        Assert.True(server.Invoke(h => h.Tick) > tick);
        // the mirrors follow: the host's car gone, the others still there; the feed says who took over
        foreach (var c in players)
        {
            Assert.Contains(c.Notices, n => n == NetLines.Migrating);
            Assert.True(g.Wait(() => c.Events.Any(b => b.Events.OfType<LeftEvent>().Any(l => l.Slot == hostSlot))), "the host's leaving was not told");
            Assert.Contains(c.Notices, n => c == ann.Client ? n == "*** You are now the host!" : n == "*** Ann is now the host!");
            Assert.True(c.SnapshotsReceived > 0);
        }
        // snapshots keep coming from the new host, and its own successor is the next seat
        long seen = players[1].SnapshotsReceived;
        Assert.True(g.Wait(() => players[1].SnapshotsReceived > seen + 20));
        Assert.True(g.Wait(() => server.SuccessorSlot == players[1].Accept!.Slot && players[1].MigrationLink is { States: > 0 }, 30000));
    }

    /// <summary>
    /// The successor's restore and replay is the host's game itself, not an approximation: a savestate the successor
    /// holds, and the inputs of the ticks after it, replayed in a game built afresh, hash the same as the host's game
    /// stopped a second (20 ticks) past that savestate.
    /// </summary>
    [Fact]
    public void TheSuccessorsReplayIsTheHostsGameExactly()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 2);
        WaitForSuccessor(g, 0);
        // the savestate to replay from, and every tick's inputs after it as they come: a newer savestate drops the older
        // ticks from the link, and when one lands depends on the clock, so the host is stopped by the ticks since this one
        var link = g.Players[0].Client.MigrationLink!;
        var state = link.State!;
        uint stateTick = link.StateTick;
        var log = new SortedDictionary<uint, LoggedTick>();
        g.Polled = () => { foreach (var t in link.Log) if (t.Tick > stateTick) log[t.Tick] = t; };
        g.Polled();
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Tick) >= stateTick + 20, 30000), "the host did not get on");
        g.Server.Stop();
        var (tick, hash, body) = g.Server.Invoke(h => (h.Tick, h.Session.StateHash(), Body(h.Session)));
        Assert.True(g.Wait(() => log.ContainsKey(tick), 10000), $"the inputs reached tick {(log.Count == 0 ? stateTick : log.Keys.Max())}, the host stopped at {tick}");
        Assert.Equal(tick, g.Server.Invoke(h => h.Tick));
        var (o, _, _) = NetMeleeServer.ReadMigrationOptions(state, install);
        var fresh = new MeleeHost(o);
        fresh.RestoreMigrated(state);
        var (replayed, held) = fresh.ReplayMigrated([.. log.Values], 0);
        output.WriteLine($"state at tick {stateTick}, replayed {replayed} ticks to {fresh.Tick} (host at {tick})");
        Assert.Equal((tick, 0), (fresh.Tick, held));
        Assert.True(replayed >= 20, $"{replayed} ticks replayed");
        Assert.True(hash == fresh.Session.StateHash(), FirstDifference(body, Body(fresh.Session)));
    }

    /// <summary>
    /// A dead player's RAISE_DEAD among the inputs replayed: the host's game asks the melee for the respawn before the
    /// tick, so the replay must too, or the successor's game has the car still dead where the host's brought it back.
    /// </summary>
    [Fact]
    public void TheReplayRaisesTheDeadAsTheHostDid()
    {
        if (HostTests.InstallDir is not { } install) return;
        var o = new ServerOptions { Install = install, Arena = "m01" };
        var host = new MeleeHost(o);
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vcgcour1.vcf", 0);
        uint t = 0;
        var log = new List<LoggedTick>();
        void Tick(bool raise)
        {
            t++;
            a.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp }]));
            b.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp, Commands = raise ? CarCommands.RaiseDead : 0 }]));
            host.Step();
            var inputs = new CarInput[host.Session.Drivers.Max(d => d.Slot) + 1];
            foreach (var d in host.Session.Drivers) inputs[d.Slot] = d.Input;
            log.Add(new LoggedTick(host.Tick, inputs, false));
        }
        for (int k = 0; k < 20; k++) Tick(false);
        for (int k = 0; k < 200 && !I76.Sim.Damage.DamageModel.IsDead(b.Player.Car); k++)
        {
            var p = new I76.Sim.Weapons.DamagePacket { Attacker = a.Player.Car, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
            p.Ch[1] = 5000;
            host.Setup.Damage.Apply(b.Player.Car, p, new System.Numerics.Vector3(0, 0.5f, 2));
        }
        Tick(false);
        Assert.True(b.Player.Destroyed);
        var state = host.Session.Snapshot();
        log.Clear();
        // the key held from the death on: refused until the respawn delay is out, then the car comes back
        for (int k = 0; k < 8 / host.Session.TickSeconds; k++) Tick(true);
        Assert.False(b.Player.Destroyed, "the host's game brought the car back");
        var fresh = new MeleeHost(o);
        fresh.RestoreMigrated(state);
        var (replayed, held) = fresh.ReplayMigrated(log, 0);
        Assert.Equal((log.Count, 0, host.Tick), (replayed, held, fresh.Tick));
        Assert.False(fresh.Seats.Single(s => s.Slot == b.Slot).Player.Destroyed, "the replay brought the car back");
        Assert.True(host.Session.StateHash() == fresh.Session.StateHash(), FirstDifference(Body(host.Session), Body(fresh.Session)));
    }

    private static byte[] Body(SimSession s) =>
        (byte[])typeof(SimSession).GetMethod("SnapshotBody", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(s, [false])!;

    /// <summary>Where two savestate bodies part: the byte, the section it is in (the four letters before it), the bytes around.</summary>
    private static string FirstDifference(byte[] a, byte[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        static bool Upper(byte c) => c is >= (byte)'A' and <= (byte)'Z';
        string section = "?";
        for (int k = Math.Min(i, a.Length) - 4; k >= 0; k--)
            if (Upper(a[k]) && Upper(a[k + 1]) && Upper(a[k + 2]) && Upper(a[k + 3])) { section = System.Text.Encoding.ASCII.GetString(a, k, 4); break; }
        int lo = Math.Max(0, i - 16);
        string Around(byte[] x) => Convert.ToHexString(x, Math.Min(lo, x.Length), Math.Clamp(x.Length - lo, 0, 48));
        return $"the replay differs from the host at byte {i} of {a.Length} / {b.Length}, in {section}: host {Around(a)}, replay {Around(b)}";
    }

    /// <summary>The host's packets stop (the link drops them all): the players take it as gone after the timeout and go to the successor.</summary>
    [Fact]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void TheHostTimesOut()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 2, proxies: true);
        GiveScores(g);
        WaitForSuccessor(g, 0);
        var before = Seats(g.Server);
        int hostSlot = g.HostPlayer.Accept!.Slot;
        var cut = Stopwatch.StartNew();
        foreach (var p in g.Players) p.Proxy!.LossPercent = 100;
        var players = g.Players.Select(p => p.Client).ToList();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Joined && c.Migrations == 1), 20000),
            string.Join(", ", players.Select(c => $"{c.State} {c.Reason}")));
        output.WriteLine($"takeover after the link died: {cut.Elapsed.TotalSeconds:0.00} s (timeout {HostMigration.HostTimeoutSeconds} s)");
        Assert.InRange(cut.Elapsed.TotalSeconds, HostMigration.HostTimeoutSeconds, HostMigration.HostTimeoutSeconds + 5);
        var server = g.Players[0].Standby.Server!;
        Assert.True(g.Wait(() => server.Invoke(h => h.Seats.All(s => s.Slot != hostSlot))));
        Assert.Equal(before.Where(s => s.Slot != hostSlot), Seats(server));
        // the others stay in the new host's game: letting the quiet host's connection go after the rejoin is not the end
        // of the new one (0.5.0 ended the game for everyone but the successor here: "DisconnectPeerCalled")
        var bob = players[1];
        long seen = bob.SnapshotsReceived;
        g.Wait(() => bob.State != ClientState.Joined || bob.SnapshotsReceived > seen + 40);
        Assert.True(bob.State == ClientState.Joined, $"{bob.State} {bob.Reason}");
        Assert.True(bob.SnapshotsReceived > seen + 40);
    }

    /// <summary>A player's own link drops for a while (not the host's): it seeks the successor, and goes on with the host when it speaks again.</summary>
    [Fact]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void APlayersOwnDropoutGoesBackToTheHost()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 2, proxies: true);
        WaitForSuccessor(g, 0);
        var (bob, _, proxy) = g.Players[1];
        proxy!.LossPercent = 100;
        Assert.True(g.Wait(() => bob.State == ClientState.Migrating, 8000));
        g.Wait(() => false, 1000);
        proxy.LossPercent = 0;
        Assert.True(g.Wait(() => bob.State == ClientState.Joined, 5000), $"{bob.State} {bob.Reason}");
        long seen = bob.SnapshotsReceived;
        Assert.True(g.Wait(() => bob.SnapshotsReceived > seen + 20));
        Assert.Equal(0, bob.Migrations);
        Assert.False(g.Players[0].Client.BecameHost);
        Assert.Null(g.Players[0].Standby.Server);
        Assert.Equal(3, g.Server.Players);
    }

    /// <summary>The successor leaves before the host does: the next seat is named and streamed to, and takes over.</summary>
    [Fact]
    public void TheSuccessorLeavesFirstAndTheNextTakesOver()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 3);
        GiveScores(g);
        WaitForSuccessor(g, 0);
        g.Players[0].Client.Leave();
        WaitForSuccessor(g, 1);
        var before = Seats(g.Server);
        int hostSlot = g.HostPlayer.Accept!.Slot;
        g.HostPlayer.Dispose();
        g.Server.Dispose();
        var players = g.Players.Skip(1).Select(p => p.Client).ToList();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Joined && c.Migrations == 1), 15000),
            string.Join(", ", players.Select(c => $"{c.State} {c.Reason}")));
        Assert.True(players[0].BecameHost);
        var server = g.Players[1].Standby.Server!;
        Assert.True(g.Wait(() => server.AwaitingRejoin == 0 && server.Invoke(h => h.Seats.All(s => s.Slot != hostSlot))));
        Assert.Equal(before.Where(s => s.Slot != hostSlot), Seats(server));
        Assert.Contains(players[1].Notices, n => n == "*** Bob is now the host!");
    }

    /// <summary>
    /// An invite-only game: a join waiting for the host's answer when the host leaves waits for the new host's instead,
    /// keeping the time it has waited, and the new host lets it in.
    /// </summary>
    [Fact]
    public void AJoinWaitingForTheHostWaitsForTheNewHost()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 2, inviteOnly: true);
        WaitForSuccessor(g, 0);
        var late = new MeleeClient { MigrationGiveUp = 6 };
        late.Log += m => output.WriteLine($"[Dee] {m}");
        late.Connect("127.0.0.1", g.Server.Port, "Dee", "vppirna1.vcf");
        try
        {
            bool Wait(Func<bool> done, int ms = 15000) => g.Wait(() => { late.Poll(); return done(); }, ms);
            Assert.True(Wait(() => late.WaitingForHost && g.Server.PendingInvite is { Name: "Dee" } && late.Successor is not null));
            Wait(() => false, 1000);
            g.HostPlayer.Dispose();
            g.Server.Dispose();
            var server = (NetMeleeServer?)null;
            Assert.True(Wait(() => (server = g.Players[0].Standby.Server) is not null && server.PendingInvite is { Name: "Dee" } && late.WaitingForHost));
            var (name, waited) = server!.PendingInvite!.Value;
            output.WriteLine($"{name} waits for the new host, {waited:0.0} s in");
            Assert.True(waited >= 0.8, $"{waited:0.0} s");
            Assert.Equal(ClientState.Connecting, late.State);
            Assert.Equal("Dee", server.AnswerInvite(InviteAnswer.Accept));
            Assert.True(Wait(() => late.State == ClientState.Joined));
            Assert.Contains(server.Invoke(h => h.Seats.Select(x => x.Name).ToList()), n => n == "Dee");
        }
        finally { late.Dispose(); }
    }

    /// <summary>The successor cannot take over (its game will not build): everyone falls back to the lost connection, as before.</summary>
    [Fact]
    public void AFailedMigrationEndsTheGameAsALostConnection()
    {
        if (HostTests.InstallDir is not { } install) return;
        var bad = Path.Combine(Path.GetTempPath(), $"i26-no-install-{Environment.ProcessId}");
        using var g = Start(install, 2, badStandbyInstall: bad);
        var (ann, _, _) = g.Players[0];
        Assert.True(g.Wait(() => g.Server.SuccessorSlot == ann.Accept!.Slot && ann.MigrationLink is { States: > 0 }));
        Assert.True(g.Wait(() => g.Players[1].Client.Successor?.Slot == ann.Accept!.Slot));
        g.HostPlayer.Dispose();
        g.Server.Dispose();
        var players = g.Players.Select(p => p.Client).ToList();
        var t = Stopwatch.StartNew();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Disconnected), 15000), string.Join(", ", players.Select(c => $"{c.State}")));
        output.WriteLine($"fell back after {t.Elapsed.TotalSeconds:0.00} s: {string.Join("; ", players.Select(c => c.Reason))}");
        Assert.All(players, c => Assert.StartsWith("connection to the host lost", c.Reason));
        Assert.Null(g.Players[0].Standby.Server);
    }

    /// <summary>
    /// Bots across host migrations: the host leaves (quits or drops), the first successor plays on a while with the bots acting
    /// (fighting, dying, coming back, leaving for a joiner), then it leaves too and the next takes over.
    /// </summary>
    [Theory]
    [InlineData(2, 0, false, true)]
    [InlineData(3, 2, false, false)]
    [InlineData(3, 2, true, true)]
    [InlineData(1, 0, true, false)]
    [InlineData(3, 1, false, true, 5, true)]
    [InlineData(3, 0, true, false, 5, false)]
    public void BotsSurviveTwoMigrationsAndPlayOn(int bots, int ai, bool restricted, bool graceful, int maxPlayers = 8, bool teams = false)
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 3, tweak: o => o with
        {
            Bots = bots, Ai = ai, MaxPlayers = maxPlayers, TeamPlay = teams, BotSkill = I76.Sim.Missions.BotSkill.Hard,
            Restrictions = restricted ? I76.Sim.Trip.WeaponRestrictions.Everything : I76.Sim.Trip.WeaponRestrictions.None,
        });
        g.Mirrors = true;
        WaitForSuccessor(g, 0);
        g.Wait(() => false, 3000);
        var players = g.Players.Select(p => p.Client).ToList();
        if (graceful) g.HostPlayer.Dispose();
        g.Server.Dispose();
        if (!graceful) g.HostPlayer.Dispose();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Joined && c.Migrations == 1), 20000), string.Join(", ", players.Select(c => $"{c.State} {c.Reason}")));
        var s1 = Assert.IsType<NetMeleeServer>(g.Players[0].Standby.Server);
        Assert.True(g.Wait(() => s1.AwaitingRejoin == 0));
        Assert.Null(s1.Fault);
        Assert.Equal(Math.Min(bots, maxPlayers - 3), s1.Invoke(h => h.Bots.Count));
        // play on: bots act; kill them so they die and respawn
        for (int round = 0; round < 3; round++)
        {
            g.Wait(() => false, 8000);
            s1.Invoke(h => { foreach (var b in h.Bots) h.Setup.Damage.Destroy(b.Player.Car); });
        }
        output.WriteLine($"after play: tick {s1.Invoke(h => h.Tick)}");
        // a second migration: Ann (the successor) leaves, Bob takes over
        Assert.True(g.Wait(() => s1.SuccessorSlot == g.Players[1].Client.Accept!.Slot && g.Players[1].Client.MigrationLink is { States: > 0 }, 30000));
        g.Wait(() => false, 2000);
        g.Players[0].Client.Dispose();
        s1.Dispose();
        var rest = players.Skip(1).ToList();
        Assert.True(g.Wait(() => rest.All(c => c.State == ClientState.Joined && c.Migrations == 2), 20000), string.Join(", ", rest.Select(c => $"{c.State} {c.Reason}")));
        var s2 = Assert.IsType<NetMeleeServer>(g.Players[1].Standby.Server);
        Assert.True(g.Wait(() => s2.Invoke(h => h.Bots.Count) == Math.Min(bots, maxPlayers - 2)), "the bots");
        g.Wait(() => false, 8000);
        s2.Invoke(h => { foreach (var b in h.Bots) h.Setup.Damage.Destroy(b.Player.Car); });
        g.Wait(() => false, 8000);
        Assert.Equal(Math.Min(bots, maxPlayers - 2), s2.Invoke(h => h.Bots.Count));
        Assert.Null(s2.Fault);
        Assert.Empty(g.MirrorLog.Where(m => m.Contains("Exception")));
        Assert.True(g.Worlds.Count >= 3);
        Assert.All(rest, c => Assert.Equal(ClientState.Joined, c.State));
    }

    /// <summary>
    /// The new host's game ends and its next round begins with the bots again (they are built afresh from the options the
    /// savestate carried), the players carried on, and the game goes on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void TheNewHostsNextRoundHasTheBotsToo(bool restricted)
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 2, tweak: o => o with
        {
            Bots = 2, Ai = 1, MaxPlayers = 8, TimeLimit = 30, RoundPause = 2, Rotation = ["m01", "m05"],
            Restrictions = restricted ? I76.Sim.Trip.WeaponRestrictions.Everything : I76.Sim.Trip.WeaponRestrictions.None,
        });
        g.Mirrors = true;
        WaitForSuccessor(g, 0);
        var players = g.Players.Select(p => p.Client).ToList();
        g.HostPlayer.Dispose();
        g.Server.Dispose();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Joined && c.Migrations == 1), 20000), string.Join(", ", players.Select(c => $"{c.State} {c.Reason}")));
        var s1 = Assert.IsType<NetMeleeServer>(g.Players[0].Standby.Server);
        Assert.True(g.Wait(() => s1.AwaitingRejoin == 0));
        Assert.True(g.Wait(() => players.All(c => c.Rounds == 1 && c.State == ClientState.Joined && c.Accept is { Arena: "m05" }), 60000), string.Join(", ", players.Select(c => $"{c.State} {c.Rounds} {c.Reason}")));
        Assert.Null(s1.Fault);
        Assert.Equal(2, s1.Invoke(h => h.Bots.Count));
        output.WriteLine("round 2 under the new host");
        g.Wait(() => false, 5000);
        Assert.Null(s1.Fault);
        Assert.Equal("m05", s1.Invoke(h => h.Arena));
        Assert.Empty(g.MirrorLog.Where(m => m.Contains("Exception")));
    }

    /// <summary>A standby whose savestates cannot be used (another build's format, a damaged one).</summary>
    private sealed class UnreadableStandby : IHostSuccessor
    {
        public int Prepared;
        public void Prepare(byte[] state) { Prepared++; throw new I76.Sim.Save.SnapshotMismatchException("the snapshot is format version 0; this build reads version 12"); }
        public Task TakeOver(MigrationHandoff handoff) => throw new I76.Sim.Save.SnapshotMismatchException("unreadable");
    }

    /// <summary>
    /// A successor that cannot read the host's savestate (a build of another format) does not crash on receiving it, nor when
    /// asked to take over: the game ends for it and for the others with the lost connection's message.
    /// </summary>
    [Fact]
    public void AnUnreadableSavestateEndsTheGameWithAMessageNotACrash()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, 2, tweak: o => o with { Bots = 2 });
        var bad = new UnreadableStandby();
        var (ann, _, _) = g.Players[0];
        ann.Standby = bad;
        Assert.True(g.Wait(() => bad.Prepared >= 1 && g.Server.SuccessorSlot == ann.Accept!.Slot && g.Players[1].Client.Successor?.Slot == ann.Accept.Slot));
        g.HostPlayer.Dispose();
        g.Server.Dispose();
        var players = g.Players.Select(p => p.Client).ToList();
        Assert.True(g.Wait(() => players.All(c => c.State == ClientState.Disconnected), 15000), string.Join(", ", players.Select(c => $"{c.State}")));
        Assert.Contains("handed over", players[0].Reason);
        Assert.StartsWith("connection to the host lost", players[1].Reason);
    }
}
