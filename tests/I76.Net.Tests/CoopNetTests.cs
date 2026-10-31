using System.Diagnostics;
using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// Co-op story missions over the network (protocol 15, docs/multiplayer.md "Co-op over the network"): the server runs the
/// mission's script; each client's mirror builds the mission as the server did and shows what a single player sees of the
/// script from the server's events and snapshots: the radio, the objectives, the cut-scene camera, the end.
/// </summary>
public class CoopNetTests(ITestOutputHelper output)
{
    private static string? Nitro =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e && Directory.Exists(e) ? e : null;

    private static string? InstallFor(string which) => which == "nitro" ? Nitro : HostTests.InstallDir;

    [Fact]
    public void TheWireCarriesTheScriptsPresentation()
    {
        NetEvent[] events =
        [
            new RadioEvent("01sta09.wav", 17, 3), new RadioEvent("gdsgc26.wav", RadioEvent.Player, 2), new RadioCutEvent(),
            new ObjectiveEvent(2, 2), new MovieEvent("t05mov.smk"), new MovieEndEvent(), new MissionEndEvent(false, 1, "You abandoned Taurus."), new SirenEvent(301, true),
        ];
        var w = new NetWriter { Names = new NameTable() };
        new EventBatch(9, events).Write(w);
        var r = new NetReader(w.ToArray()) { Names = new NameTable() };
        Assert.Equal(MsgType.Events, (MsgType)r.U8());
        Assert.Equal(events, EventBatch.Read(ref r).Events);

        var coop = new CoopInfo(["vppirna1.vcf", null, null], 5, 2, [0, 1, 2, 4], [12, 40], new RadioEvent("01sta10.wav", 3, 3), 1.5f,
            new MissionEndEvent(true, -1, ""), "t05mov.smk");
        var acc = new JoinAccept(1, 7, "t01", 99, "vppirna1.vcf", false, 0, 0, 5, [], []) { Coop = coop };
        w = new NetWriter();
        acc.Write(w);
        r = new NetReader(w.ToArray());
        r.U8();
        var back = JoinAccept.Read(ref r).Coop!;
        Assert.Equal(coop.SeatCars, back.SeatCars);
        Assert.Equal((coop.Seat, coop.RespawnDelay, coop.RadioSeconds, coop.Radio, coop.End), (back.Seat, back.RespawnDelay, back.RadioSeconds, back.Radio, back.End));
        Assert.Equal("t05mov.smk", back.Movie);
        Assert.Equal(coop.Objectives, back.Objectives);
        Assert.Equal(coop.Sirens, back.Sirens);
        // a melee's accept has none
        w = new NetWriter();
        (acc with { Coop = null }).Write(w);
        r = new NetReader(w.ToArray());
        r.U8();
        Assert.Null(JoinAccept.Read(ref r).Coop);

        // the snapshot's tail: the gates and the camera (a melee's: neither, one byte)
        var cam = new CameraState(new Vector3(10, 20, 30), new Vector3(1, 2, 3), null);
        w = new NetWriter();
        Snapshot.WriteHeader(w, 5, 0, null);
        var own = new CameraState(new Vector3(-4, 5, 6), null, Rows.Euler(0, 1f, 0));
        Snapshot.WriteBody(w, [], [], gates: [new GateState(44, 2, 0.25f)], camera: cam, personal: [(1, cam), (3, own)]);
        r = new NetReader(w.ToArray());
        r.U8();
        var snap = Snapshot.Read(ref r);
        Assert.Equal([new GateState(44, 2, 0.25f)], snap.Gates);
        Assert.Equal(cam, snap.Camera);
        // protocol 19: the seats' own camera scenes
        Assert.Equal([(1, cam), (3, snap.Personal[1].Camera)], snap.Personal.Select(p => ((int)p.Seat, p.Camera)).ToList());
        Assert.Equal(cam, snap.Personal[0].Camera);
        Assert.True(Vector3.Distance(own.Basis!.Value.Forward, snap.Personal[1].Camera.Basis!.Value.Forward) < 1e-3f);
        var basis = Rows.Euler(0, 0.5f, 0);
        w = new NetWriter();
        new CameraState(Vector3.One, null, basis).Write(w);
        r = new NetReader(w.ToArray());
        var bc = CameraState.Read(ref r);
        Assert.Null(bc.LookAt);
        Assert.True(Vector3.Distance(bc.Basis!.Value.Forward, basis.Forward) < 1e-3f);
        // the listing says it is a co-op game, by its mission
        var tag = new I76.Net.Anet.I26SessionTag(Protocol.Version, false, false, false, false, MeleeKind.Combat, 2, MeleeGoal.None, "t01", Coop: true);
        Assert.Equal((true, "t01"), (I76.Net.Anet.I26SessionTag.Read(tag.Write())!.Coop, I76.Net.Anet.I26SessionTag.Read(tag.Write())!.Arena));
        var info = new SessionInfo(1, Protocol.Version, "co-op", "t01", 1, 4, false, false, 0, 0, false, [], Coop: true);
        w = new NetWriter();
        info.Write(w);
        Assert.True(SessionInfo.TryRead(w.ToArray())!.Coop);
    }

    /// <summary>A client of the co-op game: its connection, and its mirror of the mission once accepted, built as the game builds it.</summary>
    private sealed class Player(string name, string install) : IDisposable
    {
        public string Name { get; } = name;
        public MeleeClient Client { get; } = new() { CanHost = true, MigrationGiveUp = 6 };
        public ClientWorld? Mirror;
        public readonly List<NetEvent> News = [];
        public bool SawCamera;
        /// <summary>The skip key held on the next inputs.</summary>
        public bool Skip;
        public MigrationStandby? Standby;
        private JoinAccept? _built;

        public ScriptHost? Script => Mirror?.Setup.Script?.Host;

        public void Frame(double dt)
        {
            Client.Poll();
            if (Client.Accept is { Coop: not null } a && !ReferenceEquals(a, _built))
            {
                _built = a;
                var gi = new GameInstall(install);
                var vfs = new AssetVfs(gi);
                var asm = new ModelAssembler(vfs);
                var setup = CoopMission.Build(vfs, new MissionWorld(vfs, asm, gi.FindMission(a.Arena)!), asm, a.Coop!.Setup, new I76.Sim.Rng.CrtRand(1), 1);
                Mirror = new ClientWorld(setup, a, new CoopMirrorCars(vfs));
                Mirror.News += News.Add;
            }
            if (Mirror is null) return;
            // a co-op movie stands the game still here as in the game (NetView.Frozen)
            Mirror.Hold = Client.MovieName is not null;
            Mirror.Advance(dt, Client);
            SawCamera |= Script!.View.Active;
        }

        public void Dispose()
        {
            Standby?.Server?.Dispose();
            Client.Dispose();
            Standby?.Dispose();
        }
    }

    private sealed class Game(NetMeleeServer server, string install, ITestOutputHelper output) : IDisposable
    {
        public NetMeleeServer Server = server;
        public readonly List<Player> Players = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _input, _frame;

        public Player Join(string name, bool standby = false, string car = Protocol.MissionCar)
        {
            var p = new Player(name, install);
            p.Client.Log += m => output.WriteLine($"[{name}] {m}");
            if (standby) { p.Standby = new MigrationStandby(install, m => output.WriteLine($"[{name}] {m}")); p.Client.Standby = p.Standby; }
            p.Client.Connect("127.0.0.1", Server.Port, name, car);
            Players.Add(p);
            Assert.True(Wait(() => p.Client.State == ClientState.Joined && p.Mirror is not null), $"{name} did not join: {p.Client.State} {p.Client.Reason}");
            return p;
        }

        /// <summary>Polls and draws everyone (inputs every tick: throttle up, the skip key where held) until <paramref name="done"/>.</summary>
        public bool Wait(Func<bool> done, int ms = 20000)
        {
            var t = Stopwatch.StartNew();
            while (!done() && t.ElapsedMilliseconds < ms)
            {
                double now = _clock.Elapsed.TotalSeconds;
                foreach (var p in Players) p.Frame(now - _frame);
                _frame = now;
                if (now - _input >= 0.05)
                {
                    _input = now;
                    foreach (var p in Players)
                        if (p.Client.State is ClientState.Joined or ClientState.Migrating && p.Client.MovieName is null)
                            p.Client.SendInput(new CarInput { Buttons = CarButtons.ThrottleUp, Commands = p.Skip ? CarCommands.Skip : 0 });
                }
                Thread.Sleep(5);
            }
            return done();
        }

        public void Dispose()
        {
            foreach (var p in Players) p.Dispose();
            Server.Dispose();
        }
    }

    private Game Start(string install, string mission, int max = 4, bool migration = false)
    {
        var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = mission, Coop = mission, MaxPlayers = max, Port = 0, Seed = 7, Name = "co-op", ListenHost = true, HostMigration = migration,
        });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Start();
        return new Game(server, install, output);
    }

    /// <summary>
    /// Three players in a story mission, then a fourth late: everyone sees the opening cut-scene's camera and hears its
    /// radio; only the host's skip ends it; the objectives change for everyone and a late joiner has them at once; one
    /// player's leave-map warning is that player's alone, and the player comes back beside a teammate; a destroyed player
    /// comes back after the delay; the mission's end (a failure, as failAllObj lands one) reaches everyone, and the game ends.
    /// </summary>
    [Theory]
    [InlineData("i76", "t01")]
    [InlineData("nitro", "p01")]
    public void AStoryMissionPlaysOverTheNetwork(string game, string mission)
    {
        if (InstallFor(game) is not { } install) return;
        using var g = Start(install, mission);
        var hal = g.Join("Hal");
        g.Server.MigrationOwner = hal.Client.Accept!.Slot;
        var ann = g.Join("Ann");
        var bob = g.Join("Bob");
        Assert.Equal([0, 1, 2], g.Players.Select(p => (int)p.Client.Accept!.Coop!.Seat));
        // the opening cut-scene: the script's camera on every screen, and its first radio line
        Assert.True(g.Wait(() => g.Players.All(p => p.SawCamera && p.News.OfType<RadioEvent>().Any())),
            string.Join(", ", g.Players.Select(p => $"{p.Name}: camera {p.SawCamera}, {p.News.OfType<RadioEvent>().Count()} lines")));
        foreach (var p in g.Players) output.WriteLine($"{p.Name}: {string.Join(", ", p.News.OfType<RadioEvent>().Select(r => r.Sound))}");
        Assert.All(g.Players, p => Assert.NotNull(p.Script!.Cb.Current ?? (object?)p.News.OfType<RadioEvent>().FirstOrDefault()));
        // a player's skip is not the host's: the scene goes on; the host's ends it
        ann.Skip = true;
        g.Wait(() => false, 600);
        ann.Skip = false;
        Assert.True(g.Server.Invoke(h => h.Setup.Script!.Host.View.Active), "a player who is not the host skipped the cut-scene");
        hal.Skip = true;
        bool skipped = g.Wait(() => !g.Server.Invoke(h => h.Setup.Script!.Host.View.Active), 3000);
        hal.Skip = false;
        output.WriteLine($"the host's skip {(skipped ? "ended" : "did not end")} the cut-scene at {g.Server.Invoke(h => h.Setup.Script!.Host.Now):0.0} s");
        // the clients' cameras follow the server's out of the scene (skipped, or at its own end)
        Assert.True(g.Wait(() => !g.Server.Invoke(h => h.Setup.Script!.Host.View.Active), 45000), "the cut-scene never ended");
        Assert.True(g.Wait(() => g.Players.All(p => !p.Script!.View.Active), 3000));

        // an objective's change reaches everyone (forced when the mission has none so soon)
        if (!g.Wait(() => g.Players.All(p => p.News.OfType<ObjectiveEvent>().Any()), 3000))
        {
            g.Server.Invoke(h => h.Setup.Script!.Host.Objectives.Reveal(2));
            Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<ObjectiveEvent>().Any())));
        }
        var flags = g.Server.Invoke(h => h.Setup.Script!.Host.Objectives.Flags.ToArray());
        Assert.True(g.Wait(() => g.Players.All(p => p.Script!.Objectives.Flags.SequenceEqual(flags))));

        // Bob drives off the map: the warnings are his alone, and he is put back beside a teammate
        var gi = new GameInstall(install);
        var gv = new AssetVfs(gi);
        var exits = new MissionWorld(gv, new ModelAssembler(gv), gi.FindMission(mission)!).Mission.ReadFsmProgram()!.Paths
            .Where(p => p.Name.StartsWith("exit", StringComparison.OrdinalIgnoreCase)).Select(p => p.Points[0]).ToList();
        var bobWarnings = bob.News.OfType<RadioEvent>().Count();
        if (exits.Count > 0)
        {
            g.Server.Invoke(h =>
            {
                var car = h.Seats.Single(s => s.Name == "Bob").Actor.Car;
                var at = exits[0];
                at.Y = h.Setup.World.Ground.Query(at.X, 500, at.Z).Height;
                car.Pos = new Double3(at.X, at.Y, at.Z);
                car.Vel = Vector3.Zero; car.Speed = 0;
            });
        }
        else g.Server.Invoke(h => h.Setup.Script!.Host.LeaveWarning(2, "gdsgc26.wav", 3));
        Assert.True(g.Wait(() => bob.News.OfType<RadioEvent>().Count() > bobWarnings, 5000), "Bob heard no warning");
        g.Wait(() => false, 500);
        var warned = bob.News.OfType<RadioEvent>().Skip(bobWarnings).Select(r => r.Sound).ToList();
        output.WriteLine($"Bob's warnings: {string.Join(", ", warned)}");
        Assert.DoesNotContain(ann.News.OfType<RadioEvent>(), r => warned.Contains(r.Sound));
        Assert.DoesNotContain(hal.News.OfType<RadioEvent>(), r => warned.Contains(r.Sound));
        if (exits.Count > 0)
            Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<ChatEvent>().Any(c => c.Text.Contains("Bob drove off the map")))), "the put-back was not told");

        // Ann is destroyed: everyone hears of it, and she comes back after the delay
        int annCar = ann.Client.Accept!.CarSerial;
        g.Server.Invoke(h => h.Setup.Damage.Destroy(h.Seats.Single(s => s.Name == "Ann").Actor.Car));
        Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<KillEvent>().Any(k => k.Victim == annCar))));
        var t = Stopwatch.StartNew();
        Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<RenewedEvent>().Any(r => r.Car == annCar && r.Respawn)), 10000), "Ann did not come back");
        output.WriteLine($"Ann back after {t.Elapsed.TotalSeconds:0.0} s");
        Assert.True(g.Wait(() => g.Server.Invoke(h => !h.Seats.Single(s => s.Name == "Ann").Actor.Destroyed)));

        // a fourth player joins late: the objectives as they stand, at once
        var dee = g.Join("Dee");
        Assert.Equal(3, dee.Client.Accept!.Coop!.Seat);
        flags = g.Server.Invoke(h => h.Setup.Script!.Host.Objectives.Flags.ToArray());
        Assert.Equal(flags.Select(f => (byte)f), dee.Client.Accept.Coop.Objectives);
        Assert.Equal(flags, dee.Script!.Objectives.Flags);
        Assert.Contains(dee.Client.Accept.Cars, c => c.Serial == dee.Client.Accept.CarSerial);
        // a fifth is turned away: the mission has four seats
        using (var eve = new MeleeClient())
        {
            eve.Connect("127.0.0.1", g.Server.Port, "Eve", Protocol.MissionCar);
            Assert.True(g.Wait(() => { eve.Poll(); return eve.State == ClientState.Rejected; }));
            output.WriteLine($"Eve: {eve.Reason}");
        }

        // the mission fails (as failAllObj lands it): the end and its line reach everyone, then the game over
        g.Server.Invoke(h => h.Setup.Script!.Host.EndMission(false, 0, 0));
        Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<MissionEndEvent>().Any())));
        foreach (var p in g.Players)
        {
            var end = p.News.OfType<MissionEndEvent>().Single();
            Assert.False(end.Won);
            Assert.Equal(false, p.Mirror!.Setup.Script!.Result);
            output.WriteLine($"{p.Name}: the end: {end.Text}");
        }
        Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<GameOverEvent>().Any()), 10000));
        Assert.True(g.Server.Invoke(h => h.Over));
    }

    /// <summary>
    /// A movie the script plays mid-mission (T05's and T06's playMovie) stands the game still for everyone: the server stops
    /// ticking (no timer, nobody hurt), the clients draw on nothing. Another player's copy being over does not end it; the host's
    /// does, for everyone, and the game goes on. A player who joins during it has it too.
    /// </summary>
    [Fact]
    public void AMovieStandsTheGameStillForEveryoneUntilTheHostsCopyIsOver()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, "t01");
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Setup.Script!.Host.Now) > 1));
        g.Server.Invoke(h => h.Setup.Script!.Host.PlayMovie("t05mov.smk"));
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.MovieName == "t05mov.smk")), "the movie did not reach everyone");
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Setup.Script!.Host.Now) > 0, 1000));
        g.Wait(() => false, 300);
        var frozen = g.Server.Invoke(h => (h.Tick, h.Setup.Script!.Host.Now, h.Setup.Script.Host.Cb.NowMs));
        uint drawn = hal.Mirror!.IntervalTo;
        g.Wait(() => false, 1200);
        Assert.Equal(frozen, g.Server.Invoke(h => (h.Tick, h.Setup.Script!.Host.Now, h.Setup.Script.Host.Cb.NowMs)));
        Assert.Equal(drawn, hal.Mirror.IntervalTo);
        // Ann's copy being over (or her skip key) is only hers: the movie goes on for the host
        ann.Client.SendMovieDone("t05mov.smk");
        ann.Client.SendInput(new CarInput { Commands = CarCommands.Skip });
        g.Wait(() => false, 600);
        Assert.Equal(frozen.Tick, g.Server.Invoke(h => h.Tick));
        Assert.Equal("t05mov.smk", hal.Client.MovieName);
        // a player joining during it plays it too
        var dee = g.Join("Dee");
        Assert.Equal("t05mov.smk", dee.Client.MovieName);
        // the host's copy is over: for everybody
        hal.Client.SendMovieDone("t05mov.smk");
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.MovieName is null)), "the host's end did not end it for everyone");
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Tick) > frozen.Tick + 20 && hal.Mirror.IntervalTo > drawn));
        Assert.True(g.Server.Invoke(h => h.Setup.Script!.Host.Now) > frozen.Now);
        // a second movie, and no one ever answers it: the host leaving does not matter either (nobody left ends it); here the host answers at once
        g.Server.Invoke(h => h.Setup.Script!.Host.PlayMovie("t06mov.smk"));
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.MovieName == "t06mov.smk")));
        hal.Client.SendMovieDone("t06mov.smk");
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.MovieName is null)));
    }

    /// <summary>
    /// A player's own camera scene (S03's and T06's jump cameras): the jumper's screen has the script camera, the other player's
    /// does not and they go on driving; the scene ends by its own clock.
    /// </summary>
    [Fact]
    public void AJumpCameraShowsOnTheJumpersScreenAlone()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, "t06");
        var hal = g.Join("Hal");
        var ann = g.Join("Ann");
        // the opening is the story's: both see it; the host's skip ends it
        Assert.True(g.Wait(() => g.Players.All(p => p.SawCamera)));
        hal.Skip = true;
        Assert.True(g.Wait(() => !g.Server.Invoke(h => h.Setup.Script!.Host.View.Active), 45000));
        hal.Skip = false;
        Assert.True(g.Wait(() => g.Players.All(p => !p.Script!.View.Active), 3000));
        g.Wait(() => false, 500);
        hal.SawCamera = ann.SawCamera = false;
        var gi = new GameInstall(install);
        var gv = new AssetVfs(gi);
        var at = new MissionWorld(gv, new ModelAssembler(gv), gi.FindMission("t06")!).Mission.ReadFsmProgram()!.Paths
            .First(p => p.Name.Equals("jump1", StringComparison.OrdinalIgnoreCase)).Points[0];
        // Ann is over the first jump zone, in the air
        g.Server.Invoke(h =>
        {
            var car = h.Seats.Single(s => s.Name == "Ann").Actor.Car;
            car.Pos = new Double3(at.X, h.Setup.World.Ground.Query(at.X, 500, at.Z).Height + 12, at.Z);
            car.Vel = Vector3.Zero; car.Speed = 0;
        });
        Assert.True(g.Wait(() => ann.Script!.View.Active, 5000), "the jump camera did not reach Ann");
        Assert.True(ann.Script!.View.Personal);
        Assert.False(g.Server.Invoke(h => h.Setup.Script!.Host.View.Active), "the story's camera took the view");
        // Hal never sees it, and drives on
        Assert.False(hal.SawCamera, "Hal saw Ann's jump camera");
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Seats.Single(s => s.Name == "Hal").Actor.Car.Speed) > 3, 5000));
        // the scene ends by its clock on Ann's screen too
        Assert.True(g.Wait(() => !ann.Script.View.Active, 8000), "the jump camera never ended");
        Assert.False(hal.SawCamera);
    }

    /// <summary>Eight players in one story mission (the most it takes): each in its own seat and car, none on another, the ninth turned away, and the mission's end reaches all eight.</summary>
    [Theory]
    [InlineData("i76", "t01")]
    [InlineData("nitro", "p01")]
    public void EightPlayersShareAStoryMission(string game, string mission)
    {
        if (InstallFor(game) is not { } install) return;
        using var g = Start(install, mission, max: 8);
        for (int i = 0; i < 8; i++) g.Join("P" + i);
        Assert.Equal(Enumerable.Range(0, 8), g.Players.Select(p => (int)p.Client.Accept!.Coop!.Seat));
        Assert.Equal(8, g.Players.Select(p => p.Client.Accept!.CarSerial).Distinct().Count());
        Assert.All(g.Players, p => Assert.Equal(8, p.Client.Accept!.Coop!.SeatCars.Count));
        g.Wait(() => false, 1500);
        var cars = g.Server.Invoke(h => h.Seats.Select(s => s.Actor.Car.Pos.ToVector3()).ToList());
        for (int i = 0; i < cars.Count; i++)
            for (int j = i + 1; j < cars.Count; j++) Assert.True(Vector3.Distance(cars[i], cars[j]) > 3f, $"seats {i} and {j} stand {Vector3.Distance(cars[i], cars[j]):0.0} m apart");
        using (var nine = new MeleeClient())
        {
            nine.Connect("127.0.0.1", g.Server.Port, "Nine", Protocol.MissionCar);
            Assert.True(g.Wait(() => { nine.Poll(); return nine.State == ClientState.Rejected; }));
            output.WriteLine($"Nine: {nine.Reason}");
        }
        g.Server.Invoke(h => h.Setup.Script!.Host.EndMission(false, 0, 0));
        Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<MissionEndEvent>().Any())));
        Assert.True(g.Wait(() => g.Players.All(p => p.News.OfType<GameOverEvent>().Any()), 10000));
        Assert.True(g.Server.Invoke(h => h.Over));
    }

    /// <summary>
    /// The successor's restore and replay of a co-op mission is the host's game itself: the script, its threads and the human
    /// group come from the savestate, and the host's inputs after it, replayed in a mission built afresh, hash the same as
    /// the host's game.
    /// </summary>
    [Theory]
    [InlineData("i76", "t01", false)]
    [InlineData("nitro", "p01", false)]
    [InlineData("i76", "t01", true)]
    [InlineData("nitro", "p01", true)]
    public void TheSuccessorsReplayOfAMissionIsTheHostsGame(string game, string mission, bool chosenCars)
    {
        if (InstallFor(game) is not { } install) return;
        using var g = Start(install, mission, migration: true);
        // with the players' own cars: the first seat's remade in place, the third's built when its player comes
        var cars = chosenCars ? TwoOtherCars(install) : [Protocol.MissionCar, Protocol.MissionCar];
        var hal = g.Join("Hal", car: cars[0]);
        g.Server.MigrationOwner = hal.Client.Accept!.Slot;
        var ann = g.Join("Ann", standby: true);
        g.Join("Bob", standby: true, car: cars[1]);
        // a little play, and a death and its respawn on the way: the group's state goes across too
        g.Wait(() => false, 1500);
        g.Server.Invoke(h => h.Setup.Damage.Destroy(h.Seats.Single(s => s.Name == "Bob").Actor.Car));
        g.Server.MigrationBarrier();
        uint now = g.Server.Invoke(h => h.Tick);
        Assert.True(g.Wait(() => g.Server.SuccessorSlot == ann.Client.Accept!.Slot && ann.Client.MigrationLink is { State: not null } l && l.StateTick > now && ann.Standby!.Ready, 30000),
            "no savestate, or the standby game was not built");
        var link = ann.Client.MigrationLink!;
        var state = link.State!;
        uint stateTick = link.StateTick;
        var log = new SortedDictionary<uint, LoggedTick>();
        void Keep() { foreach (var t in link.Log) if (t.Tick > stateTick) log[t.Tick] = t; }
        Assert.True(g.Wait(() => { Keep(); return g.Server.Invoke(h => h.Tick) >= stateTick + 6 * 20; }, 30000), "the host did not get on");
        g.Server.Stop();
        var (tick, hash, body) = g.Server.Invoke(h => (h.Tick, h.Session.StateHash(), Body(h.Session)));
        Assert.True(g.Wait(() => { Keep(); return log.ContainsKey(tick); }, 10000), $"the inputs reached tick {(log.Count == 0 ? stateTick : log.Keys.Max())}, the host stopped at {tick}");
        var (o, _, _) = NetMeleeServer.ReadMigrationOptions(state, install);
        Assert.Equal(mission, o.Coop);
        var fresh = new MeleeHost(o);
        Assert.IsType<CoopMode>(fresh.Mode);
        fresh.RestoreMigrated(state, new Dictionary<int, string> { [hal.Client.Accept.Slot] = "Hal", [ann.Client.Accept!.Slot] = "Ann" });
        var (replayed, held) = fresh.ReplayMigrated([.. log.Values], 0);
        output.WriteLine($"state at tick {stateTick}, replayed {replayed} ticks to {fresh.Tick} (host at {tick}); seats {string.Join(", ", fresh.Seats.Select(s => $"{s.Slot} {s.Name}"))}");
        Assert.Equal((tick, 0), (fresh.Tick, held));
        Assert.True(hash == fresh.Session.StateHash(), HostMigrationFirstDifference(body, Body(fresh.Session)));
        Assert.Equal(3, fresh.Seats.Count);
        Assert.Contains(fresh.Seats, s => s.Name == "Ann");
    }

    /// <summary>Two stock cars of the game's melee list, other than any a mission starts in.</summary>
    private static string[] TwoOtherCars(string install)
    {
        var stems = I76.Formats.Shell.CarTable.OfferedStems(new GameInstall(install)).Order(StringComparer.Ordinal).ToList();
        return [stems[3] + "1.vcf", stems[7] + "2.vcf"];
    }

    /// <summary>
    /// Each player picks a car on joining (the shell's co-op forms): the first seat's is made again in place before the mission
    /// starts, a later one's is built as it takes its seat, "the mission's own" is the story's car (not the first seat's choice);
    /// the join accept names each seat's car, so a client builds the mission with the same cars; the others' mirrors show them.
    /// </summary>
    [Theory]
    [InlineData("i76", "t01")]
    [InlineData("nitro", "p01")]
    public void EachPlayerDrivesTheCarTheyChose(string game, string mission)
    {
        if (InstallFor(game) is not { } install) return;
        using var g = Start(install, mission);
        var picks = TwoOtherCars(install);
        string own = g.Server.Invoke(h => ((CoopMode)h.Mode).OwnCar);
        var hal = g.Join("Hal", car: picks[0]);
        var ann = g.Join("Ann");
        var bob = g.Join("Bob", car: picks[1]);
        // the server's seats drive what they chose; Ann the story's car, not Hal's
        var files = g.Server.Invoke(h => h.Seats.Select(s => (s.Name, s.Actor.CarFile, s.Actor.Car.Definition)).ToList());
        output.WriteLine($"the story's car {own}; seats {string.Join(", ", files)}");
        Assert.Equal([("Hal", picks[0], picks[0]), ("Ann", own, own), ("Bob", picks[1], picks[1])], files.Select(f => (f.Name, f.CarFile, f.Definition)).ToList());
        Assert.NotEqual(own, picks[0]);
        // the accept: each client's own car, and the seats as the mission is built from them (null: the story's)
        Assert.Equal(picks[0], hal.Client.Accept!.Car);
        Assert.Equal(own, ann.Client.Accept!.Car);
        Assert.Equal(picks[1], bob.Client.Accept!.Car);
        var seatCars = bob.Client.Accept.Coop!.SeatCars;
        // (null would mean the first seat's car: the story's own is named once the first seat drives another)
        Assert.Equal([picks[0], own, picks[1]], seatCars.Take(3));
        Assert.Equal(4, seatCars.Count);
        // every mirror has each seat's car as the server's
        Assert.True(g.Wait(() => g.Players.All(p => p.Mirror!.Cars.Values.Count(c => c.Info.Def == picks[0]) == 1 && p.Mirror.Cars.Values.Count(c => c.Info.Def == picks[1]) == 1)),
            string.Join(" | ", g.Players.Select(p => $"{p.Name}: {string.Join(",", p.Mirror!.Cars.Values.Select(c => c.Info.Def))}")));
        // the first seat's own mirror built the car it chose as the mission's player (its script entity is the vehicle)
        Assert.Equal(picks[0], hal.Script!.Coop!.Seats[0].Car.Definition);
        // and the story goes on with them: the clock runs
        float t = g.Server.Invoke(h => h.Setup.Script!.Host.Now);
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Setup.Script!.Host.Now) > t + 1));
    }

    /// <summary>The host quits mid-mission: the lowest player takes over, everyone comes back to their seats, and the mission goes on from where it was.</summary>
    [Fact]
    public void TheHostQuitsMidMissionAndTheMissionGoesOn()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, "t01", migration: true);
        var hal = g.Join("Hal");
        g.Server.MigrationOwner = hal.Client.Accept!.Slot;
        var ann = g.Join("Ann", standby: true);
        var bob = g.Join("Bob", standby: true);
        uint now = g.Server.Invoke(h => h.Tick);
        Assert.True(g.Wait(() => ann.Client.MigrationLink is { State: not null } l && l.StateTick > now && ann.Standby!.Ready, 30000));
        g.Wait(() => false, 1000);
        float clock = g.Server.Invoke(h => h.Setup.Script!.Host.Now);
        var objectives = g.Server.Invoke(h => h.Setup.Script!.Host.Objectives.Flags.ToArray());
        int lines = bob.News.OfType<RadioEvent>().Count();
        g.Players.Remove(hal);
        hal.Dispose();
        g.Server.Dispose();
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined && p.Client.Migrations == 1), 15000),
            string.Join(", ", g.Players.Select(p => $"{p.Client.State} {p.Client.Reason}")));
        var server = Assert.IsType<NetMeleeServer>(ann.Standby!.Server);
        Assert.IsType<CoopMode>(server.Host.Mode);
        Assert.True(g.Wait(() => server.AwaitingRejoin == 0 && server.Invoke(h => h.Seats.Count == 2)));
        Assert.Equal(["Ann", "Bob"], server.Invoke(h => h.Seats.Select(s => s.Name).ToList()));
        // the mission goes on: its clock, its radio, the objectives as they were
        Assert.True(g.Wait(() => server.Invoke(h => h.Setup.Script!.Host.Now) > clock + 3, 10000));
        Assert.True(server.Invoke(h => h.Setup.Script!.Host.Objectives.Flags.Zip(objectives).All(x => (x.Second & 6 & ~x.First) == 0)), "an objective's success or failure was lost");
        output.WriteLine($"the mission at {server.Invoke(h => h.Setup.Script!.Host.Now):0.0} s under Ann; Bob has heard {bob.News.OfType<RadioEvent>().Count() - lines} lines since");
        Assert.True(g.Wait(() => bob.Mirror!.Cars.Count > 0 && bob.Client.SnapshotsReceived > 0));
    }

    /// <summary>The host leaves while a movie stands the game still: the successor takes the movie on (the game does not run on under it), and its own copy ending ends it for everyone.</summary>
    [Fact]
    public void AMovieGoesOnUnderTheNewHostWhenTheHostLeavesDuringIt()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var g = Start(install, "t01", migration: true);
        var hal = g.Join("Hal");
        g.Server.MigrationOwner = hal.Client.Accept!.Slot;
        var ann = g.Join("Ann", standby: true);
        var bob = g.Join("Bob", standby: true);
        uint now = g.Server.Invoke(h => h.Tick);
        Assert.True(g.Wait(() => ann.Client.MigrationLink is { State: not null } l && l.StateTick > now && ann.Standby!.Ready, 30000));
        g.Server.Invoke(h => h.Setup.Script!.Host.PlayMovie("t05mov.smk"));
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.MovieName == "t05mov.smk")));
        g.Wait(() => false, 500);
        g.Players.Remove(hal);
        hal.Dispose();
        g.Server.Dispose();
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.State == ClientState.Joined && p.Client.Migrations == 1), 15000),
            string.Join(", ", g.Players.Select(p => $"{p.Client.State} {p.Client.Reason}")));
        var server = Assert.IsType<NetMeleeServer>(ann.Standby!.Server);
        var mode = Assert.IsType<CoopMode>(server.Host.Mode);
        Assert.True(g.Wait(() => server.AwaitingRejoin == 0 && server.Invoke(h => h.Seats.Count == 2)));
        // the movie is still on, for the new host's game and Bob's; the game stands still
        Assert.Equal("t05mov.smk", mode.Movie);
        Assert.Equal("t05mov.smk", ann.Client.MovieName);
        Assert.Equal("t05mov.smk", bob.Client.MovieName);
        uint tick = server.Invoke(h => h.Tick);
        g.Wait(() => false, 1000);
        Assert.Equal(tick, server.Invoke(h => h.Tick));
        // Bob's copy ending is not the host's; Ann's (the new host's) is
        bob.Client.SendMovieDone("t05mov.smk");
        g.Wait(() => false, 400);
        Assert.Equal("t05mov.smk", mode.Movie);
        ann.Client.SendMovieDone("t05mov.smk");
        Assert.True(g.Wait(() => g.Players.All(p => p.Client.MovieName is null) && mode.Movie is null), "the new host's end did not end it");
        Assert.True(g.Wait(() => server.Invoke(h => h.Tick) > tick + 20, 10000));
    }

    /// <summary>Velocity's intro over the network: the bus and the other cars the cut-scene shows stay on the ground, on the server and as a client draws them.</summary>
    [Fact]
    public void CarsInViewOfTheIntroStayOnTheGroundOnTheClient()
    {
        if (Nitro is not { } install) return;
        using var g = Start(install, "p17");
        var hal = g.Join("Hal");
        int shown = 0;
        double worstClient = 0, worstServer = 0;
        string where = "";
        bool seen = false;
        g.Wait(() =>
        {
            if (hal.Script!.View.Active) seen = true;
            else if (seen) return true;
            if (!hal.Script.View.Active) return false;
            var eye = hal.Script.View.Eye;
            var ground = hal.Mirror!.Setup.World.Ground;
            foreach (var m in hal.Mirror.Cars.Values)
            {
                if (m.IsPlayer || m.Drawn is not { } d || m.Vehicle.IsHeli || m.Track.Samples.Count == 0 || m.Track.Samples[^1].SampleTick < 40 || (d.Pos.ToVector3() - eye).Length() > 1500f) continue;
                shown++;
                double below = ground.BareHeight(d.Pos.X, d.Pos.Z) - d.Pos.Y;
                if (below > worstClient) { worstClient = below; where = $"{m.Info.Serial} at {d.Pos.X:0},{d.Pos.Z:0} y={d.Pos.Y:0.0} d={(d.Pos.ToVector3() - eye).Length():0}"; }
            }
            double ws = g.Server.Invoke(h =>
            {
                double w = 0;
                foreach (var v in h.Setup.World.Vehicles)
                    if (!v.IsHeli && !v.Wreck && !h.Setup.World.IsHuman(v) && (v.Pos.ToVector3() - eye).Length() <= 1500f)
                        w = Math.Max(w, h.Setup.World.Ground.BareHeight(v.Pos.X, v.Pos.Z) - v.Pos.Y);
                return w;
            });
            worstServer = Math.Max(worstServer, ws);
            return false;
        }, 60000);
        output.WriteLine($"samples {shown}; worst below ground: server {worstServer:0.0} m, client {worstClient:0.0} m ({where})");
        Assert.True(shown > 0, "a car in view of the intro");
        Assert.True(worstServer < 2, $"server: {worstServer:0.0} m below the ground");
        Assert.True(worstClient < 2, $"client: {worstClient:0.0} m below the ground ({where})");
    }

    private static byte[] Body(SimSession s) =>
        (byte[])typeof(SimSession).GetMethod("SnapshotBody", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(s, [false])!;

    private static string HostMigrationFirstDifference(byte[] a, byte[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return $"the replay differs from the host at byte {i} of {a.Length} / {b.Length}";
    }
}
