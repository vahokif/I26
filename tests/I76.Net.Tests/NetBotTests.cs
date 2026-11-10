using System.Numerics;
using I76.Net;
using I76.Server;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// Bots (docs/multiplayer.md "Bots"): players the server drives by the game's mode, on the stock arenas of the base game and of the
/// Nitro Pack (the Nitro tests skip without I76_NITRO_INSTALL, the base ones without I76_INSTALL). They play through
/// <see cref="MeleeHost"/> as a listen server's game does, stepped as fast as the machine goes; the game is deterministic, so a
/// run repeats.
/// </summary>
public class NetBotTests(ITestOutputHelper output)
{
    private static string? Nitro
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e ? e : Environment.GetEnvironmentVariable("I76_NITRO");
            return d is { Length: > 0 } && Directory.Exists(d) ? d : null;
        }
    }

    /// <summary>Steps the host until <paramref name="done"/> or <paramref name="seconds"/> of game time; returns the seconds played.</summary>
    private static double Play(MeleeHost host, double seconds, Func<bool>? done = null)
    {
        int n = (int)(seconds / host.Session.TickSeconds), t = 0;
        for (; t < n && !host.Over; t++)
        {
            host.Step();
            if (done?.Invoke() == true) { t++; break; }
        }
        return t * host.Session.TickSeconds;
    }

    // ---- race ----

    /// <summary>
    /// Four race bots on a Nitro arena drive the checkpoints in order, lap after lap (the tracker counts a lap only on the wrap of a
    /// full round in order), and the first to the limit wins by laps: the end names a bot, and the game's own end follows.
    /// </summary>
    [Fact]
    public void RaceBotsLapTheCheckpointsInOrderAndOneWinsByLaps()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N26.RAC", Kind = MeleeKind.Race, GoalValue = 3, Bots = 4, MaxPlayers = 8, BotSkill = BotSkill.Hard });
        var game = host.Game;
        Assert.Equal(4, host.Bots.Count);
        var laps = host.Bots.ToDictionary(b => b.Player, _ => new List<double>());
        double now = 0;
        game.LapCounted += p => laps[p].Add(now);
        int ticks = 0;
        while (!game.Ended && ticks < 20 * 600) { host.Step(); ticks++; now = ticks * host.Session.TickSeconds; }
        output.WriteLine($"N26: ended at {now:0} s; laps " + string.Join(" | ", laps.Values.Select(l => string.Join(", ", l.Select(x => x.ToString("0"))))));
        Assert.True(game.Ended, "a bot reached the lap limit");
        var winner = Assert.IsType<MeleePlayer>(game.Winner);
        Assert.True(winner.IsBot);
        Assert.Equal(3, winner.Laps);
        Assert.Equal(3, laps[winner].Count);
        // the lap counter is the tracker's: each lap is a round of the checkpoints in order, so the times are increasing and no lap is shorter than the course allows
        Assert.True(laps[winner].Zip(laps[winner].Skip(1), (a, b) => b - a > 15).All(x => x), "lap times");
        Assert.True(laps.Values.Count(l => l.Count >= 1) >= 3, "most of the field finished a lap");
        // the race is the bots' scoreboard: names marked, laps in the kills column
        var rows = host.Scoreboard().Players;
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.True(BotNames.IsBot(r.Name)));
        int winnerSlot = host.Bots.Single(b => b.Player == winner).Slot;
        Assert.Equal(3, rows.Single(r => r.Slot == winnerSlot).Kills);
        // and the end line the clients are sent names the bot's slot
        Play(host, 20);
        var events = host.TakeEvents();
        var end = Assert.Single(events.OfType<EndEvent>());
        Assert.Equal(winnerSlot, end.Winner);
        Assert.Equal(3, end.Count);
        Assert.True(host.Over);
        Assert.Equal((byte)winnerSlot, events.OfType<GameOverEvent>().Single().Standings[0].Slot);
    }

    /// <summary>
    /// A race bot killed mid-lap comes back at a spawn point (behind check1) wanting the checkpoint it wanted, as a person's car does
    /// (the race's count goes on through a respawn), and drives the course to it from check1 in order, not straight across the arena
    /// (where the planner took it off the course into the walls and drops, to die and come back to try again): it takes that one and
    /// finishes the lap without dying again.
    /// </summary>
    [Theory]
    [InlineData("N31.RAC", 1)]
    [InlineData("N34.RAC", 7)]
    public void AKilledRaceBotDrivesTheCourseFromTheStartToTheCheckpointItWants(string arena, int killAfter)
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = arena, Kind = MeleeKind.Race, GoalValue = 0, Bots = 2, MaxPlayers = 4, BotSkill = BotSkill.Hard, Seed = 3 });
        var game = host.Game;
        var bot = host.Bots[0].Player;
        var checks = game.Checkpoints.Where(c => c.Key < 99).OrderBy(c => c.Key).ToList();
        double dt = host.Session.TickSeconds, t = 0;
        RaceTracker Tracker() => game.RaceOf(bot)!;
        // lap one, then to the checkpoint after check(killAfter + 1)
        while (t < 600 && !(game.RaceOf(bot) is { Laps: >= 1 } r && r.Current == killAfter)) { host.Step(); t += dt; }
        Assert.True(t < 600, "the bot raced a lap");
        int deaths = bot.Deaths, laps = Tracker().Laps;
        output.WriteLine($"{arena}: killed at {t:0} s by check{killAfter + 1}, {bot.Car.Pos.X:0},{bot.Car.Pos.Z:0}");
        host.Setup.Damage.Destroy(bot.Car);
        while (t < 900 && (bot.Destroyed || DamageModel.IsDead(bot.Car))) { host.Step(); t += dt; }
        double back = t;
        var at = bot.Car.Pos;
        output.WriteLine($"back at {back:0} s at {at.X:0},{at.Z:0}");
        Assert.Equal(deaths + 1, bot.Deaths);
        Assert.True(game.Spawns.Min(s => System.Math.Sqrt((s.Pos.X - at.X) * (s.Pos.X - at.X) + (s.Pos.Z - at.Z) * (s.Pos.Z - at.Z))) < 25, "at a spawn point");
        Assert.Equal(killAfter, Tracker().Current);                     // the same checkpoint wanted
        // the checkpoints the car comes by (within 30 m across the ground) on its way: the course from check1 in order
        var seen = new List<int>();
        double took = 0;
        while (t - back < 240 && Tracker().Laps == laps)
        {
            host.Step(); t += dt;
            Assert.Equal(deaths + 1, bot.Deaths);
            var p = bot.Car.Pos;
            foreach (var (n, c) in checks)
                if (took == 0 && (p.X - c.X) * (p.X - c.X) + (p.Z - c.Z) * (p.Z - c.Z) < 30 * 30 && (seen.Count == 0 || seen[^1] != n)) seen.Add(n);
            if (took == 0 && Tracker().Current != killAfter) took = t;
        }
        output.WriteLine($"by checkpoints {string.Join(", ", seen)}; took check{killAfter + 2} {took - back:0} s after, the lap {t - back:0} s after");
        Assert.True(took > 0, $"took check{killAfter + 2}");
        Assert.Equal(Enumerable.Range(1, killAfter + 2), seen);
        Assert.Equal(laps + 1, Tracker().Laps);
    }

    // ---- capture the flag ----

    /// <summary>Three teams of two on a Nitro CTF arena: a raider takes an enemy flag from its stand and carries it home, a capture the game counts to the team and its players.</summary>
    [Fact]
    public void CtfBotsTakeAnEnemyFlagHomeAndCaptureIt()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N12.CF3", Kind = MeleeKind.Ctf, CtfTeams = 3, GoalValue = 1, Bots = 6, MaxPlayers = 8, BotSkill = BotSkill.Hard });
        var game = host.Game;
        Assert.Equal(new[] { 2, 2, 2 }, host.Bots.GroupBy(b => b.Player.Team).OrderBy(g => g.Key).Select(g => g.Count()).ToArray());
        var taken = new List<string>();
        game.Ctf!.Captured += (team, flag) => taken.Add($"team {team} took team {flag}'s flag");
        double t = Play(host, 600, () => game.Ended);
        output.WriteLine($"N12: {string.Join("; ", taken)} after {t:0} s");
        Assert.True(game.Ended, "a team captured within ten minutes");
        Assert.Single(taken);
        int team = game.WinningTeam!.Value;
        Assert.Equal(1, game.Ctf.TeamCaptures[team]);
        Assert.True(game.Winner!.IsBot);
        // the capture is on each of the team's players (the goal counter), and the scoreboard shows it
        Assert.All(host.Bots.Where(b => b.Player.Team == team), b => Assert.Equal(1, b.Player.Captures));
        Assert.All(host.Bots.Where(b => b.Player.Team != team), b => Assert.Equal(0, b.Player.Captures));
    }

    // ---- combat ----

    /// <summary>Records what is done to the cars of a game, by whom (a decorator over the game's damage sink).</summary>
    private sealed class Tap(IDamageSink inner, Action<object, DamagePacket> seen) : IDamageSink
    {
        public void Apply(object victim, DamagePacket p, Vector3 hitLocal) { seen(victim, p); inner.Apply(victim, p, hitLocal); }
        public void Apply(object victim, DamagePacket p, Vector3 hitLocal, int node) { seen(victim, p); inner.Apply(victim, p, hitLocal, node); }
        public void CaltropTyreHit(Vehicle v, Vector3 at) => inner.CaltropTyreHit(v, at);
    }

    /// <summary>
    /// Eight bots on two teams in a base arena fight for ten minutes of game time: they hit the other side, kill it, and never
    /// kill a teammate; of the thousands of hits only a stray round or two ever lands on a teammate (a wreck's blast is not a shot).
    /// </summary>
    [Fact]
    public void TeamCombatBotsKillEnemiesAndNeverShootTheirTeam()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m05", TeamPlay = true, Bots = 8, MaxPlayers = 8, BotSkill = BotSkill.Hard });
        var game = host.Game;
        Assert.Equal(new[] { 4, 4 }, host.Bots.GroupBy(b => b.Player.Team).OrderBy(g => g.Key).Select(g => g.Count()).ToArray());
        int enemyHits = 0, friendlyShots = 0, enemyKills = 0, friendlyKills = 0;
        host.Setup.Combat.Damage = new Tap(host.Setup.Combat.Damage!, (v, p) =>
        {
            if (v is not Vehicle victim || p.Attacker is not Vehicle attacker) return;
            var vp = game.Players.FirstOrDefault(x => x.Car == victim);
            var ap = game.Players.FirstOrDefault(x => x.Car == attacker);
            if (vp is null || ap is null || vp == ap || DamageModel.IsDead(attacker)) return;
            if (vp.Team == ap.Team) friendlyShots++; else enemyHits++;
        });
        host.Setup.Damage.KilledBy += (victim, attacker) =>
        {
            var vp = game.Players.FirstOrDefault(x => x.Car == victim);
            var ap = attacker is Vehicle a ? game.Players.FirstOrDefault(x => x.Car == a) : null;
            if (vp is null || ap is null || vp == ap) return;
            if (vp.Team == ap.Team) friendlyKills++; else enemyKills++;
        };
        Play(host, 600);
        output.WriteLine($"m05: {enemyHits} hits on the other team, {friendlyShots} on teammates, {enemyKills} kills, {friendlyKills} teamkills; " +
            string.Join(" ", host.Bots.Select(b => $"[T{b.Player.Team} {b.Player.Score} pts {b.Player.Kills}k {b.Player.Deaths}d]")));
        Assert.True(enemyHits > 500, $"{enemyHits} hits");
        Assert.True(enemyKills >= 3, "the bots kill the other side");
        Assert.Equal(0, friendlyKills);
        Assert.True(friendlyShots <= enemyHits / 100, $"{friendlyShots} teammates hit against {enemyHits} enemies");
        // a kill scores as a player's (1000 x victim mass / own mass) and only for the killer
        Assert.Equal(host.Bots.Sum(b => b.Player.Kills), enemyKills);
        Assert.True(host.Bots.Sum(b => b.Player.Score) >= enemyKills * 100);
    }

    /// <summary>In a free for all the bots hunt each other (any other car is an enemy), humans and bots alike.</summary>
    [Fact]
    public void FreeForAllBotsAttackAnyoneAndAHumanIsOneToThem()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Bots = 3, MaxPlayers = 8, BotSkill = BotSkill.Hard });
        var human = host.Join("Taurus", "vppirna1.vcf", 0);
        var hits = new HashSet<(Vehicle, Vehicle)>();
        host.Setup.Combat.Damage = new Tap(host.Setup.Combat.Damage!, (v, p) => { if (v is Vehicle a && p.Attacker is Vehicle b && a != b) hits.Add((b, a)); });
        Play(host, 300);
        var bots = host.Bots.Select(b => b.Player.Car).ToList();
        Assert.Equal(3, bots.Count);
        Assert.Contains(hits, h => bots.Contains(h.Item1) && h.Item2 == human.Player.Car);       // a bot shot the human
        Assert.Contains(hits, h => bots.Contains(h.Item1) && bots.Contains(h.Item2));            // and the bots one another
        Assert.All(host.Bots, b => Assert.Equal(0, b.Player.Team));
    }

    // ---- seats ----

    /// <summary>
    /// The bots take the seats the humans leave free (up to the limit asked) and give them up as humans join, one at a time, and come
    /// back when a human leaves; a human's slot never meets a bot's, and every join and leave is told to the clients.
    /// </summary>
    [Fact]
    public void BotsLeaveToMakeRoomForHumansAndComeBackWhenTheyGo()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Bots = 3, MaxPlayers = 4 });
        Assert.Equal(3, host.Bots.Count);
        Assert.Equal([63, 62, 61], host.Bots.Select(b => b.Slot).OrderByDescending(s => s));
        Assert.All(host.Bots, b => Assert.True(BotNames.IsBot(b.Name)));
        Assert.Equal(3, host.Bots.Select(b => b.Name).Distinct().Count());
        host.TakeEvents();
        var a = host.Join("a", "vppirna1.vcf", 0);
        Assert.Equal(3, host.Bots.Count);                       // four seats: one human, three bots
        var b2 = host.Join("b", "vcgcour1.vcf", 0);
        Assert.Equal(2, host.Bots.Count);                       // the last bot made room
        var c = host.Join("c", "valepre1.vcf", 0);
        Assert.Single(host.Bots);
        var d = host.Join("d", "vppirna1.vcf", 0);
        Assert.Empty(host.Bots);
        var slots = host.Seats.Select(s => s.Slot).ToList();
        Assert.Equal(slots.Count, slots.Distinct().Count());
        var events = host.TakeEvents();
        Assert.Equal(3, events.OfType<LeftEvent>().Count());                    // the bots' leaving, each to its clients
        Assert.Equal(4, events.OfType<JoinedEvent>().Count());
        host.Leave(d, "left");
        Assert.Single(host.Bots);                                               // a seat is free: a bot comes back
        host.Leave(c, "left");
        Assert.Equal(2, host.Bots.Count);
        Assert.Equal(4, host.Scoreboard().Players.Count);
        // the host boots one: it goes and nothing takes its seat
        var booted = host.Bots[0];
        Assert.Null(host.BootBot(booted.Slot.ToString()));
        Assert.Equal(1, host.Bots.Count);
        host.Leave(b2, "left");
        Assert.Equal(2, host.Bots.Count);                                       // up to the limit the host set, one fewer
        Assert.NotNull(host.BootBot("nobody"));
        events = host.TakeEvents();
        Assert.Equal(3, events.OfType<JoinedEvent>().Count(j => BotNames.IsBot(j.Name)));
        // the clients' roster: a bot's car names its slot, so it is listed as a player (not "AI")
        var info = host.AcceptFor(a).Cars.Where(x => x.Owner >= 0 && BotNames.IsBot(x.Name)).ToList();
        Assert.Equal(2, info.Count);
        Assert.All(info, x => Assert.Contains(host.Bots, bs => bs.Slot == x.Owner && bs.CarSerial == x.Serial));
    }

    /// <summary>A bot that is destroyed asks to come back when the 5 s are up, like a player's key press, and is a fresh car at a spawn point.</summary>
    [Fact]
    public void ADeadBotComesBackAfterTheDelay()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Bots = 1, MaxPlayers = 4 });
        var bot = host.Bots[0];
        Play(host, 3);
        double diedAt = 0, back = 0, t = 0;
        host.Setup.Damage.Destroy(bot.Player.Car);
        for (int k = 0; k < 20 * 20 && back == 0; k++)
        {
            host.Step(); t += host.Session.TickSeconds;
            if (diedAt == 0 && bot.Player.Destroyed) diedAt = t;
            if (diedAt > 0 && !bot.Player.Destroyed) back = t;
        }
        output.WriteLine($"died at {diedAt:0.0}, back at {back:0.0}");
        Assert.True(back > 0, "the bot came back");
        Assert.InRange(back - diedAt, 5.0, 8.0);
        Assert.Equal(1, bot.Player.Deaths);
        Assert.False(DamageModel.IsDead(bot.Player.Car));
        Assert.Equal(100, bot.Player.Entity.Health, 1);
    }

    /// <summary>A hurt bot with a regen pad to be had goes to it: a fresh car and 400 points, without dying.</summary>
    [Fact]
    public void AHurtBotUsesARegenPad()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m07", Bots = 1, MaxPlayers = 4 });
        var bot = host.Bots[0].Player;
        var renewed = new List<(Vehicle Car, double At)>();
        double t = 0;
        host.Game.PlayerRenewed += v => renewed.Add((v, t));
        // thirty seconds on (a regen is once in 30 s from the spawn), then hurt, not killed
        for (int k = 0; k < 35 * 20; k++) { host.Step(); t += host.Session.TickSeconds; }
        for (int k = 0; k < 200 && bot.Entity.Health > 30; k++)
        {
            var p = new DamagePacket { Mask = 2, Kind = DamageKind.Round };
            p.Ch[1] = 40;
            host.Setup.Damage.Apply(bot.Car, p, new Vector3(0, 0.5f, 2));
        }
        Assert.InRange(bot.Entity.Health, 1, 40);
        Assert.False(bot.Destroyed);
        double hurtAt = t;
        int score = bot.Score;
        for (int k = 0; k < 120 * 20 && renewed.Count == 0 && !bot.Destroyed; k++) { host.Step(); t += host.Session.TickSeconds; }
        output.WriteLine($"hurt at {hurtAt:0}, regen at {(renewed.Count > 0 ? renewed[0].At : -1):0}, score {score} -> {bot.Score}");
        Assert.False(bot.Destroyed, "it got there alive");
        Assert.Single(renewed);
        Assert.Equal(score + 400, bot.Score);
        Assert.Equal(100, bot.Entity.Health, 1);
    }

    /// <summary>The cars the bots drive are the install's own stock cars, the same through the restrictions as a player's, and each takes a different driver's name.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BotsDriveStockCarsUnderStockDriverNames(bool nitro)
    {
        string? install = nitro ? Nitro : HostTests.InstallDir;
        if (install is null) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = nitro ? "N26.RAC" : "m05", Kind = nitro ? MeleeKind.Race : MeleeKind.Combat, Bots = 12, MaxPlayers = 16 });
        Assert.Equal(12, host.Bots.Count);
        Assert.Equal(12, host.Bots.Select(b => b.Name).Distinct().Count());
        Assert.All(host.Bots, b =>
        {
            Assert.True(host.CarExists(b.Player.CarFile), b.Player.CarFile);
            Assert.Null(host.CheckCar(b.Player.CarFile));
            Assert.Contains(b.Name[..^BotNames.Suffix.Length], I76.Formats.Shell.MeleeDrivers.Names);
        });
        Assert.True(host.Bots.Select(b => b.Player.CarFile).Distinct().Count() >= 3, "a mix of cars");
        Assert.Equal([4, 3, 1], new[] { BotSkill.Easy, BotSkill.Normal, BotSkill.Hard }.Select(s => MeleeGame.SkillTier(s)).ToArray());
    }

    /// <summary>The AI fill is as it was: it attacks the humans (not the bots), is not on the scoreboard, and bots are a seat apart from it.</summary>
    [Fact]
    public void TheAiFillStillAttacksTheHumansAndIsNotABot()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Ai = 2, Bots = 2, MaxPlayers = 8 });
        var human = host.Join("Taurus", "vppirna1.vcf", 0);
        Play(host, 5);
        var fill = host.Setup.Script!.Host.Entities.Where(e => e.Name.StartsWith("nvcl") && !e.Removed).ToList();
        Assert.Equal(2, fill.Count);
        Assert.All(fill, e => Assert.Same(human.Player.Entity, e.OrderTarget));
        Assert.Equal(3, host.Scoreboard().Players.Count);
    }

    // ---- over the network ----

    /// <summary>
    /// A listen-server-style game: the host's server has two bots, one client joins. The client's roster and scoreboard list the bots
    /// as players (marked), their cars are in its snapshots, and the game ends as any other: the time limit, the end event, the
    /// standings with every player in them.
    /// </summary>
    [Fact]
    public void AClientSeesTheBotsOnTheScoreboardAndTheGameEndsNormally()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m05", Port = 0, Bots = 2, MaxPlayers = 4, TimeLimit = 6, BotSkill = BotSkill.Hard });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Start();
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "Taurus", "vppirna1.vcf");
        bool Wait(Func<bool> done, int ms = 30000)
        {
            for (int t = 0; t < ms / 10 && !done(); t++) { c.Poll(); Thread.Sleep(10); }
            return done();
        }
        Assert.True(Wait(() => c.State == ClientState.Joined && c.Snapshots.Count > 20));
        var accept = c.Accept!;
        var botCars = accept.Cars.Where(x => BotNames.IsBot(x.Name)).ToList();
        Assert.Equal(2, botCars.Count);
        Assert.All(botCars, x => Assert.True(x.Owner > accept.Slot, "a player's slot"));
        Assert.Equal(1, server.Players);                        // the seats are the human's; bots are not connections
        Assert.True(Wait(() => c.Scores is { } s && s.Players.Count == 3));
        var rows = c.Scores!.Players;
        Assert.Equal(2, rows.Count(r => BotNames.IsBot(r.Name)));
        Assert.Contains(rows, r => r.Name == "Taurus" && r.Slot == accept.Slot);
        // the bots' cars are in the snapshots and they drive
        var serials = botCars.Select(x => x.Serial).ToHashSet();
        Assert.True(Wait(() => c.Snapshots.Values[^1].Cars.Where(x => serials.Contains(x.Serial)).Any(x => Math.Abs(x.Speed) > 3)), "a bot drives");
        // the end
        Assert.True(Wait(() => c.Events.Any(e => e.Events.OfType<GameOverEvent>().Any()), 40000));
        var events = c.Events.SelectMany(e => e.Events).ToList();
        Assert.Contains(events, e => e is EndEvent { TimeUp: true });
        var over = events.OfType<GameOverEvent>().Single();
        Assert.Equal(3, over.Standings.Count);
        Assert.Equal(2, over.Standings.Count(s => rows.Any(r => r.Slot == s.Slot && BotNames.IsBot(r.Name))));
        Assert.True(Wait(() => server.Host.Over));
    }

    // ---- host migration ----

    /// <summary>
    /// A successor host takes the bots over: restoring the host's savestate it has the same bots under the same slots and names with the
    /// same scores, and replaying the host's inputs it plays the same game, bots included, hash for hash.
    /// </summary>
    [Fact]
    public void ASuccessorHostTakesTheBotsOverAndPlaysTheSameGame()
    {
        if (HostTests.InstallDir is not { } install) return;
        var o = new ServerOptions { Install = install, Arena = "m05", Bots = 3, MaxPlayers = 6, BotSkill = BotSkill.Hard, TeamPlay = true };
        var host = new MeleeHost(o);
        var a = host.Join("a", "vppirna1.vcf", 1);
        uint t = 0;
        var log = new List<LoggedTick>();
        void Tick()
        {
            t++;
            a.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp }]));
            host.Step();
            var inputs = new CarInput[host.Session.Drivers.Max(d => d.Slot) + 1];
            foreach (var d in host.Session.Drivers) inputs[d.Slot] = d.Input;
            log.Add(new LoggedTick(host.Tick, inputs, false));
        }
        for (int k = 0; k < 20 * 40; k++) Tick();
        var state = host.Session.Snapshot();
        log.Clear();
        for (int k = 0; k < 20 * 20; k++) Tick();
        var fresh = new MeleeHost(o);
        fresh.RestoreMigrated(state);
        Assert.Equal(host.Bots.Select(b => (b.Slot, b.Name, b.Player.Team, b.Player.CarFile)), fresh.Bots.Select(b => (b.Slot, b.Name, b.Player.Team, b.Player.CarFile)));
        var (replayed, held) = fresh.ReplayMigrated(log, 0);
        Assert.Equal((log.Count, 0, (int)host.Tick), (replayed, held, (int)fresh.Tick));
        Assert.Equal(host.Bots.Select(b => (b.Player.Score, b.Player.Kills, b.Player.Deaths)), fresh.Bots.Select(b => (b.Player.Score, b.Player.Kills, b.Player.Deaths)));
        Assert.True(host.Session.StateHash() == fresh.Session.StateHash(), "the successor's game is the host's");
    }

    /// <summary>
    /// A savestate taken at any moment of a bot game restores into a fresh game that plays on as the host's does: every few seconds of
    /// a combat, race and CTF game with bots (dying, respawning, carrying flags, lapping) a successor is built, restored and
    /// replayed over the next stretch and must hash the same.
    /// </summary>
    [Theory]
    [InlineData("m05", "", 0, false)]
    [InlineData("m05", "", 0, true)]
    [InlineData("N26.RAC", "race", 0, false)]
    [InlineData("N12.CF3", "ctf", 3, false)]
    public void ASuccessorRestoredAtAnyMomentPlaysOnAsTheHostDoes(string arena, string kind, int ctfTeams, bool restricted)
    {
        string? install = kind.Length > 0 ? Nitro : HostTests.InstallDir;
        if (install is null) return;
        var o = new ServerOptions
        {
            Install = install, Arena = arena, Bots = kind == "ctf" ? 6 : 4, Ai = kind.Length > 0 ? 0 : 2, MaxPlayers = 8, BotSkill = BotSkill.Hard, TeamPlay = kind == "" && !restricted,
            Kind = kind switch { "race" => MeleeKind.Race, "ctf" => MeleeKind.Ctf, _ => MeleeKind.Combat }, CtfTeams = Math.Max(2, ctfTeams), GoalValue = kind == "race" ? 3 : kind == "ctf" ? 2 : 0,
            Restrictions = restricted ? I76.Sim.Trip.WeaponRestrictions.Everything : I76.Sim.Trip.WeaponRestrictions.None,
        };
        var host = new MeleeHost(o);
        var a = host.Join("a", restricted ? host.Options.DefaultCar : "vppirna1.vcf", 1);
        uint t = 0;
        var log = new List<LoggedTick>();
        void Tick()
        {
            t++;
            a.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp | ((t / 60) % 2 == 0 ? CarButtons.SteerLeft : 0) | ((t / 25) % 5 == 0 ? CarButtons.Fire : 0) }]));
            host.Step();
            var inputs = new CarInput[host.Session.Drivers.Max(d => d.Slot) + 1];
            foreach (var d in host.Session.Drivers) inputs[d.Slot] = d.Input;
            log.Add(new LoggedTick(host.Tick, inputs, false));
        }
        int checks = 0;
        for (int seg = 0; seg < 24 && !host.Over; seg++)
        {
            for (int k = 0; k < 8 * 20 && !host.Over; k++) Tick();
            if (seg % 3 == 2) foreach (var b in host.Bots.Take(2)) host.Setup.Damage.Destroy(b.Player.Car);
            var state = host.Session.Snapshot();
            var extras = new I76.Net.NetWriter();
            host.WriteMigrationExtras(extras);
            log.Clear();
            for (int k = 0; k < 3 * 20 && !host.Over; k++) Tick();
            var fresh = new MeleeHost(o);
            fresh.RestoreMigrated(state);
            var r = new I76.Net.NetReader(extras.ToArray());
            fresh.ReadMigrationExtras(ref r);
            Assert.Equal(host.Bots.Count, fresh.Bots.Count);
            var (replayed, _) = fresh.ReplayMigrated(log, 0);
            Assert.Equal((int)host.Tick, (int)fresh.Tick);
            Assert.True(host.Session.StateHash() == fresh.Session.StateHash(), $"{arena}: segment {seg}, tick {host.Tick}: the successor's game is not the host's");
            checks++;
        }
        output.WriteLine($"{arena}: {checks} restores matched, to tick {host.Tick}; scores {string.Join(" ", host.Bots.Select(b => $"{b.Player.Score}/{b.Player.Deaths}/{b.Player.Laps}"))}");
        Assert.True(checks >= 6);
    }

    /// <summary>
    /// A savestate can be taken on any tick of a bot game (the host writes one every 1.5 s for its successor): a bot given a new route
    /// leaves the AI's nav path on the old one until its next plan, a list no entity owns, which the writer used to refuse
    /// ("a nav path that is neither the program's nor an entity's own") and so stopped the host's game. Each such moment restores to the
    /// same state hash.
    /// </summary>
    [Theory]
    [InlineData("N12.CF3", true)]
    [InlineData("m05", false)]
    public void ASavestateIsTakenOnEveryTickOfABotGame(string arena, bool nitro)
    {
        string? install = nitro ? Nitro : HostTests.InstallDir;
        if (install is null) return;
        var o = new ServerOptions
        {
            Install = install, Arena = arena, Bots = 6, MaxPlayers = 8, BotSkill = BotSkill.Hard,
            Kind = nitro ? MeleeKind.Ctf : MeleeKind.Combat, CtfTeams = 3, GoalValue = nitro ? 2 : 0,
        };
        var host = new MeleeHost(o);
        int stale = 0, restored = 0;
        for (int t = 0; t < 20 * 150 && !host.Over; t++)
        {
            host.Step();
            var state = host.Session.Snapshot();      // throws for a path it cannot name
            if (host.Setup.Script!.Host.Entities.Any(e => e.NavPath is { } p && !ReferenceEquals(p, e.Path) && !host.Setup.Script.Host.Entities.Any(x => ReferenceEquals(x.OwnPath, p) || ReferenceEquals(x.SnapshotPath, p))))
            {
                stale++;
                if (restored < 6)
                {
                    var fresh = new MeleeHost(o);
                    fresh.RestoreMigrated(state);
                    Assert.True(host.Session.StateHash() == fresh.Session.StateHash(), $"restored at tick {host.Tick}");
                    restored++;
                }
            }
        }
        output.WriteLine($"{arena}: {stale} ticks with an old nav path, {restored} restored");
        if (nitro) Assert.True(stale > 0, "the case was reached");
    }
}
