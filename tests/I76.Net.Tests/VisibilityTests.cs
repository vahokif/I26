using System.Diagnostics;
using I76.Net;
using I76.Server;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// Every client's mirror has every other player's car (docs/multiplayer.md "The client"): through joins, leaves and
/// rejoins over a lossy link, a new round, a player's own variant, a lost spawn, a car this install cannot build, and a
/// view that throws on one car.
/// </summary>
public class VisibilityTests(ITestOutputHelper output)
{
    /// <summary>A player as the game runs one: its mirror is built a while after the accept (the arena loads meanwhile).</summary>
    private sealed class Player : IDisposable
    {
        public required string Name { get; init; }
        public required string Car { get; init; }
        public byte[]? Config { get; init; }
        /// <summary>Drops every spawn event before its mirror sees it (a spawn lost on the way): it must ask for the cars.</summary>
        public bool LoseSpawns { get; init; }
        public Bot? Bot;
        public LagProxy? Proxy;
        public double AcceptedAt = -1;
        /// <summary>The mirror of the round before, still advanced for the old scene's last frames, and until when.</summary>
        public ClientWorld? Old;
        public double OldUntil;
        public int Round;
        public void Dispose() { Bot?.Dispose(); Proxy?.Dispose(); Bot = null; Proxy = null; Old = null; }
    }

    /// <summary>The game's loading screen between the accept and the mirror; and the old scene's frames after a new round is announced.</summary>
    private const double Load = 1.5, OldSceneFrames = 0.3;

    private NetMeleeServer _server = null!;
    private readonly List<Player> _players = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last, _nextTick;

    private void Join(Player p)
    {
        p.Proxy = new LagProxy(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _server.Port), 75, 5, jitterMs: 20);
        p.Bot = new Bot(p.Name) { Policy = (_, t) => new CarInput { Buttons = CarButtons.Analog, Throttle = 12000, Steer = (short)(t % 200 < 100 ? 8000 : -8000) } };
        p.Bot.Client.Connect("127.0.0.1", p.Proxy.Port, p.Name, p.Car, 0, p.Config);
        p.AcceptedAt = -1;
        p.Round = 0;
        if (!_players.Contains(p)) _players.Add(p);
    }

    private void Leave(Player p)
    {
        p.Bot?.Client.Leave();
        Run(0.3);
        p.Dispose();
    }

    private void Run(double seconds)
    {
        double until = _clock.Elapsed.TotalSeconds + seconds;
        while (_clock.Elapsed.TotalSeconds < until)
        {
            double now = _clock.Elapsed.TotalSeconds, dt = now - _last;
            foreach (var p in _players)
            {
                if (p.Bot is not { } b) continue;
                var c = b.Client;
                c.Poll();
                if (p.LoseSpawns && c.Events.Count > 0)
                {
                    var kept = c.Events.Select(x => new EventBatch(x.Tick, [.. x.Events.Where(e => e is not SpawnEvent)])).ToList();
                    c.Events.Clear();
                    foreach (var x in kept) c.Events.Enqueue(x);
                }
                // a new round: the scene reloads (its mirror still runs a few frames), then loads the arena again
                if (c.Rounds != p.Round)
                {
                    p.Round = c.Rounds;
                    if (b.Mirror is { } was) { p.Old = was; p.OldUntil = now + OldSceneFrames; }
                    b.DropMirror();
                    p.AcceptedAt = -1;
                }
                if (p.Old is { } old) { if (now < p.OldUntil) old.Advance(dt, c); else p.Old = null; }
                // the game takes nothing from the connection while its arena loads: only the mirror does
                if (b.Mirror is not null) b.Frame(dt, poll: false);
                if (c.Accept is not null && p.AcceptedAt < 0) p.AcceptedAt = now;
                if (b.Mirror is null && p.Old is null && c.Accept is { } a && now - p.AcceptedAt >= Load)
                {
                    var m = new ClientWorld(HostTests.Mirror(a.Arena), a);
                    m.Log += s => output.WriteLine($"[{p.Name}] {s}");
                    b.UseMirror(m);
                }
            }
            _last = now;
            while (now >= _nextTick) { foreach (var p in _players) p.Bot?.Tick(); _nextTick += SimRate.Default.TickSeconds; }
            Thread.Sleep(3);
        }
    }

    /// <summary>Who each client cannot see, against the server's seats.</summary>
    private List<string> Missing()
    {
        var seats = _server.Invoke(h => h.Seats.Select(s => (s.Name, s.CarSerial)).ToList());
        var missing = new List<string>();
        foreach (var p in _players)
        {
            if (p.Bot?.Mirror is not { } m) continue;
            foreach (var (name, serial) in seats)
                if (!m.Cars.TryGetValue(serial, out var c) || !c.Present)
                    missing.Add($"{p.Name} cannot see {name} (car {serial}, {(m.Cars.ContainsKey(serial) ? "not in the snapshot" : "not in the mirror")})");
        }
        return missing;
    }

    private void Expect(string when)
    {
        // everyone in, with an arena, and some play
        for (int i = 0; i < 150 && _players.Any(p => p.Bot is { } b && (b.Mirror is null || b.Mirror.IntervalTo == 0)); i++) Run(0.1);
        Run(2);
        var miss = Missing();
        output.WriteLine($"{when}: {(miss.Count == 0 ? "everyone sees everyone" : string.Join("; ", miss))}");
        Assert.True(miss.Count == 0, $"{when}: {string.Join("; ", miss)}");
    }

    /// <summary>
    /// The testers' "fine at first, then nobody can see anyone": three players, then the melee carries on into a new round.
    /// The round's accept lists only the players seated before each one; the rest come as spawn events at the round's first
    /// tick, which the old round's mirror (the scene still running its last frames) used to take and lose with it. The
    /// first player (a listen host) then saw nobody, the second only the first. Also: a player's own variant, a rejoin.
    /// </summary>
    [Fact]
    public void EveryoneSeesEveryoneAfterANewRound()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = _server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "m01", Port = 0, Seed = 3, Rounds = true, RoundPause = 1, TimeLimit = 14,
        });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Start();
        var vcf = VariantTests.Custom(server.Host.Vfs);
        var a = new Player { Name = "Ann", Car = "vppirna1.vcf" };
        var b = new Player { Name = "Bob", Car = "vppirna4.vcf", Config = vcf };
        var c = new Player { Name = "Cat", Car = "valepre1.vcf" };
        try
        {
            Join(a); Run(0.5); Join(b); Run(0.5); Join(c);
            Expect("round 1, all three joined");
            Assert.Contains(a.Bot!.Mirror!.Cars.Values, m => m.Info.Vcf is not null && m.Info.Name == "Bob");
            for (int i = 0; i < 300 && _players.Any(p => p.Bot?.Client.Rounds != 1); i++) Run(0.1);
            Assert.All(_players, p => Assert.Equal(1, p.Bot!.Client.Rounds));
            Expect("round 2");
            Leave(b); Join(b);
            Expect("round 2, Bob left and came back");
        }
        finally { foreach (var p in _players) p.Dispose(); }
    }

    /// <summary>
    /// Joins, leaves and rejoins in several orders through a lossy link (75 ms ± 20 each way, 5% lost); one player loses
    /// every spawn event and must learn each car that comes after its accept by asking the server about it.
    /// </summary>
    [Fact]
    public void EveryoneSeesEveryoneThroughJoinsLeavesAndRejoins()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = _server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Seed = 3, Ai = 1 });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Start();
        var a = new Player { Name = "Ann", Car = "vppirna1.vcf", LoseSpawns = true };
        var b = new Player { Name = "Bob", Car = "vcgcour1.vcf" };
        var c = new Player { Name = "Cat", Car = "valepre1.vcf" };
        try
        {
            Join(a); Run(0.5); Join(b); Run(0.5); Join(c);
            Expect("all three joined");
            Assert.True(a.Bot!.Mirror!.Asked >= 2, $"Ann asked about {a.Bot.Mirror.Asked} cars");
            Leave(b); Join(b);
            Expect("Bob left and came back");
            Leave(a); Join(a);
            Expect("Ann (the first) left and came back");
            Leave(c); Leave(b); Join(c); Join(b);
            Expect("Cat and Bob left and came back in the other order");
            Assert.Equal(0, b.Bot!.Mirror!.Asked);        // nothing lost, nothing asked
        }
        finally { foreach (var p in _players) p.Dispose(); }
    }

    /// <summary>A car this install cannot build (another game's, a variant it lacks) is drawn as a stand-in, not left out.</summary>
    [Fact]
    public void ACarThisInstallCannotBuildIsDrawnAsAStandIn()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var seat = host.Join("me", "vcgcour1.vcf", 0);
        var accept = host.AcceptFor(seat);
        accept = accept with { Cars = [.. accept.Cars, new CarInfo(90, "vcgcour9.vcf", 1, 0, "Nitro"), new CarInfo(91, "zzghost3.vcf", 2, 0, "Ghost")] };
        var mirror = new ClientWorld(HostTests.Mirror("m01"), accept);
        var nitro = mirror.Cars[90];
        Assert.Equal(("vcgcour1.vcf", "vcgcour9.vcf"), (nitro.Info.Def, nitro.StandInFor));
        var ghost = mirror.Cars[91];
        Assert.Equal((ClientWorld.StandInCar, "zzghost3.vcf"), (ghost.Info.Def, ghost.StandInFor));
        Assert.Null(mirror.Cars[seat.CarSerial].StandInFor);
        Assert.Contains(ghost.Vehicle, mirror.Setup.World.Humans);
    }

    /// <summary>A view that throws on one car's spawn (the game's car node) loses neither the other spawns nor the events after it.</summary>
    [Fact]
    public void AViewThatThrowsOnOneCarLosesNoOtherEvents()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var me = host.Join("me", "vppirna1.vcf", 0);
        var accept = host.AcceptFor(me);
        host.Step();
        host.TakeEvents();
        var bob = host.Join("bob", "vcgcour1.vcf", 0);
        var cat = host.Join("cat", "valepre1.vcf", 0);
        host.Step();
        var events = host.TakeEvents();
        Assert.Equal(2, events.OfType<SpawnEvent>().Count());
        var mirror = new ClientWorld(HostTests.Mirror("m01"), accept);
        var logged = new List<string>();
        mirror.Log += logged.Add;
        mirror.Added += m => { if (m.Info.Name == "bob") throw new InvalidOperationException("no model"); };
        var client = new MeleeClient();
        client.Events.Enqueue(new EventBatch(host.Tick, events));
        var w = new NetWriter();
        var (cars, rounds) = host.Capture();
        Snapshot.WriteHeader(w, host.Tick, me.Inputs.LastApplied, host.CaptureOwn(me));
        Snapshot.WriteBody(w, cars, rounds);
        var r = new NetReader(w.Span);
        r.U8();
        client.Inject(Snapshot.Read(ref r));
        mirror.Advance(0, client);
        Assert.True(mirror.Cars.ContainsKey(cat.CarSerial), "the spawn after the one that threw was lost");
        Assert.True(mirror.Cars[cat.CarSerial].Present);
        Assert.Contains(logged, l => l.Contains("no model"));
        output.WriteLine(string.Join("\n", logged));
    }

    /// <summary>The car query and its answer round trip; the server answers about cars in the arena only, once a quarter second a seat.</summary>
    [Fact]
    public void TheServerAnswersWhatACarIs()
    {
        var w = new NetWriter();
        new CarQuery([3, 70000]).Write(w);
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.CarQuery, (MsgType)r.U8());
        Assert.Equal([3, 70000], CarQuery.Read(ref r).Serials);
        w = new NetWriter();
        new CarList([new CarInfo(3, "n1.vcf", 1, 2, "a", [1, 2]), new CarInfo(4, "b.vcf", -1, 0, "")]).Write(w);
        r = new NetReader(w.Span);
        Assert.Equal(MsgType.Cars, (MsgType)r.U8());
        var list = CarList.Read(ref r).Cars;
        Assert.Equal(("n1.vcf", (sbyte)1, (byte)2, "a"), (list[0].Def, list[0].Owner, list[0].Team, list[0].Name));
        Assert.Equal([1, 2], list[0].Vcf);
        Assert.Null(list[1].Vcf);

        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Ai = 1 });
        var me = host.Join("me", "vppirna1.vcf", 0);
        var you = host.Join("you", "vcgcour1.vcf", 0);
        var cars = host.AnswerCarQuery(me, [you.CarSerial, 999])!;
        Assert.Equal(("vcgcour1.vcf", (sbyte)you.Slot, "you"), (Assert.Single(cars).Def, cars[0].Owner, cars[0].Name));
        Assert.Null(host.AnswerCarQuery(me, [you.CarSerial]));            // too soon
        for (int i = 0; i < 6; i++) host.Step();
        host.Leave(you, "left");
        // the leaver's car is a wreck in the arena for 10 s (the original blows it up), then gone: nothing to bring back
        for (int t = 0; t < 11 / host.Session.TickSeconds; t++) host.Step();
        Assert.Empty(host.AnswerCarQuery(me, [you.CarSerial])!);
    }
}
