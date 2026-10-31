using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>Rounds: a melee that carries on after its end (docs/multiplayer.md "Rounds").</summary>
public class RoundTests(ITestOutputHelper output)
{
    private static ServerOptions Opts(string arena, params string[] rotation) =>
        new() { Install = "unused", Arena = arena, Rotation = rotation, Seed = 10 };

    [Fact]
    public void WithNoRotationEveryRoundIsTheSameArena()
    {
        var o = Opts("m05");
        for (int i = 0; i < 3; i++) o = RoundPlan.Next(o);
        Assert.Equal("m05", o.Arena);
        Assert.Equal(13, o.Seed);        // a seed of its own each round, still replayable from --seed
        Assert.Equal("m05", RoundPlan.Next(Opts("m05.msn")).Arena);
    }

    [Fact]
    public void AnUnlimitedRaceStaysUnlimitedAndAKindChangeGetsTheFormsDefault()
    {
        var race = Opts("n01.rac") with { Kind = MeleeKind.Race, Goal = MeleeGoal.Laps, GoalValue = 0, Rotation = ["n01.rac", "n11.cf2"] };
        Assert.Equal(0, RoundPlan.Next(race with { Arena = "n01.rac" }).GoalValue);           // unlimited, then the next round is too
        var next = RoundPlan.Next(race with { GoalValue = 5 });
        Assert.Equal(MeleeKind.Ctf, next.Kind);
        Assert.Equal(3, next.GoalValue);                                                  // a set limit becomes the new kind's default
        Assert.Equal(0, RoundPlan.Next(race).GoalValue);                                  // no limit stays none across kinds
    }

    [Fact]
    public void ARotationCyclesInOrderAndWraps()
    {
        var o = Opts("m01", "m01", "m02", "m03");
        var seen = new List<string>();
        for (int i = 0; i < 7; i++) { o = RoundPlan.Next(o); seen.Add(o.Arena); }
        Assert.Equal(["m02", "m03", "m01", "m02", "m03", "m01", "m02"], seen);
    }

    [Fact]
    public void ARotationWithoutTheStartingArenaGoesToItsFirstThenOn()
    {
        var o = Opts("m05", "m01", "m02");
        o = RoundPlan.Next(o);
        Assert.Equal("m01", o.Arena);
        Assert.Equal("m02", RoundPlan.Next(o).Arena);
        // names as --arena takes them: with or without .msn, any case, custom maps by their file name
        Assert.Equal(["m01", "zzmap.lvl", "n35.rac"], RoundPlan.ParseRotation("m01.msn, zzmap.lvl;n35.rac,,"));
        Assert.Equal("zzmap.lvl", RoundPlan.Next(Opts("M01", "m01", "zzmap.lvl")).Arena);
    }

    [Fact]
    public void ANitroArenaInTheRotationBringsItsOwnKind()
    {
        var o = Opts("m05", "m05", "n35.rac", "n40.cf3") with { Goal = MeleeGoal.Kills, GoalValue = 10 };
        o = RoundPlan.Next(o);
        Assert.Equal((MeleeKind.Race, "n35.rac"), (o.Kind, o.Arena));
        Assert.Equal((MeleeGoal.None, 3), (o.Goal, o.GoalValue));      // the combat goal does not follow into a race; its limit becomes the race's 3 laps
        o = RoundPlan.Next(o);
        Assert.Equal((MeleeKind.Ctf, 3), (o.Kind, o.CtfTeams));
        o = RoundPlan.Next(o);
        Assert.Equal((MeleeKind.Combat, "m05"), (o.Kind, o.Arena));
    }

    /// <summary>
    /// Two clients play a short melee on a server that rotates m01, m05. At the end one leaves from the standings: the other
    /// follows into m05 on the same connection with its slot, car and team, scores from zero, the leaver gone; a join
    /// during the pause is turned away; and the second round ends into the first arena again.
    /// </summary>
    [Fact]
    public void PlayersFollowIntoTheNextRoundWithTheirSeats()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "m01", Rotation = ["m01", "m05"], Port = 0, TimeLimit = 3, Rounds = true, RoundPause = 2, TeamPlay = true,
        });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Start();
        using var a = new MeleeClient();
        using var b = new MeleeClient();
        a.Connect("127.0.0.1", server.Port, "Ann", "vcgcour1.vcf", 2);
        b.Connect("127.0.0.1", server.Port, "Bob", "valepre1.vcf", 3);
        bool Wait(Func<bool> done, int ms = 20000)
        {
            for (int t = 0; t < ms / 10 && !done(); t++) { a.Poll(); b.Poll(); Thread.Sleep(10); }
            return done();
        }
        Assert.True(Wait(() => a.State == ClientState.Joined && b.State == ClientState.Joined));
        Assert.Equal("m01", a.Accept!.Arena);
        var first = server.Host;
        int annSlot = a.Accept.Slot;
        // something to reset
        server.Invoke(h =>
        {
            var p = h.Seats.First(s => s.Name == "Ann").Player;
            typeof(MeleePlayer).GetProperty("Score")!.SetValue(p, 1234);
            typeof(MeleePlayer).GetProperty("Kills")!.SetValue(p, 3);
        });

        // the game over, with its pause announced
        Assert.True(Wait(() => server.Host.Over));
        Assert.True(Wait(() => a.Events.Any(e => e.Events.OfType<GameOverEvent>().Any())));
        Assert.Equal(2, a.Events.SelectMany(e => e.Events).OfType<GameOverEvent>().Single().NextRoundSeconds);
        Assert.NotNull(server.NextRoundIn);
        // a join in the pause is turned away (the listing also shows the game as over)
        using (var late = new MeleeClient())
        {
            late.Connect("127.0.0.1", server.Port, "Late", "vppirna1.vcf");
            for (int t = 0; t < 300 && late.State == ClientState.Connecting; t++) { late.Poll(); a.Poll(); b.Poll(); Thread.Sleep(10); }
            Assert.Equal(ClientState.Rejected, late.State);
            Assert.Contains("between rounds", late.Reason);
        }
        // Bob leaves from the standings and just drops out
        b.Leave();

        Assert.True(Wait(() => a.Rounds == 1 && a.State == ClientState.Joined && a.Accept is { Arena: "m05" }));
        Assert.NotSame(first, server.Host);
        Assert.Equal(1, server.Round);
        Assert.Equal("m05", server.Host.Arena);
        Assert.False(server.Host.Over);
        var seat = Assert.Single(server.Host.Seats);
        Assert.Equal(("Ann", "vcgcour1.vcf", 2, annSlot), (seat.Name, seat.Player.CarFile, seat.Player.Team, seat.Slot));
        Assert.Equal((0, 0, 0), (seat.Player.Score, seat.Player.Kills, seat.Player.Deaths));
        Assert.Equal(("vcgcour1.vcf", annSlot), (a.Accept.Car, (int)a.Accept.Slot));
        Assert.Equal(1, server.Players);
        // the old round's snapshots and events did not come along
        Assert.True(a.NewestTick < 400, $"newest tick {a.NewestTick}");
        Assert.DoesNotContain(a.Events, e => e.Events.OfType<GameOverEvent>().Any());

        // and the next round after this one is the rotation's first again
        Assert.True(Wait(() => a.Rounds == 2 && a.State == ClientState.Joined && a.Accept is { Arena: "m01" }, 30000));
        Assert.Equal("m01", server.Host.Arena);
        Assert.Equal("Ann", Assert.Single(server.Host.Seats).Name);
    }

    /// <summary>
    /// #109: a player carried into the next round plays it as a fresh join: the car moves on its inputs, its guns fire and count
    /// down, a death can be answered with a respawn.
    /// </summary>
    [Fact]
    public void ThePlayerCarriedIntoTheNextRoundDrivesFiresAndRespawns()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "m01", Rotation = ["m01", "m05"], Port = 0, MaxScore = 1000, Rounds = true, RoundPause = 1,
        });
        server.Start();
        using var a = new MeleeClient();
        a.Connect("127.0.0.1", server.Port, "Ann", "vppirna1.vcf");
        bool Wait(Func<bool> done, int ms = 20000)
        {
            for (int t = 0; t < ms / 10 && !done(); t++) { a.Poll(); Thread.Sleep(10); }
            return done();
        }
        Assert.True(Wait(() => a.State == ClientState.Joined));
        server.Invoke(h => typeof(MeleePlayer).GetProperty("Score")!.SetValue(h.Seats.Single().Player, 1234));
        Assert.True(Wait(() => a.Rounds == 1 && a.State == ClientState.Joined && a.Accept is { Arena: "m05" }, 30000));
        Vehicle Car() => server.Invoke(h => h.Seats.Single().Player.Car);
        int Ammo() => server.Invoke(h => h.Seats.Single().Player.Car.Armory!.Weapons.Sum(w => w.Ammo));
        void Drive(CarInput input, int ms)
        {
            var until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until) { a.SendInput(input); a.Poll(); Thread.Sleep(25); }
        }
        var start = server.Invoke(h => h.Seats.Single().Player.Car.Pos.ToVector3());
        int ammo = Ammo();
        Drive(new CarInput { Buttons = CarButtons.ThrottleUp | CarButtons.Fire }, 1500);
        var moved = server.Invoke(h => h.Seats.Single().Player.Car.Pos.ToVector3()) - start;
        output.WriteLine($"moved {moved.Length():0.0} m, ammo {ammo} -> {Ammo()}");
        Assert.True(moved.Length() > 3, $"moved {moved.Length()}");
        Assert.True(Ammo() < ammo, "the guns did not fire");
        Drive(new CarInput { Commands = CarCommands.SelfDestruct }, 100);
        Assert.True(Wait(() => server.Invoke(h => I76.Sim.Damage.DamageModel.IsDead(h.Seats.Single().Player.Car))));
        int gen = server.Invoke(h => h.Seats.Single().Player.Car.NetGeneration);
        for (int t = 0; t < 40 && gen == server.Invoke(h => h.Seats.Single().Player.Car.NetGeneration); t++) Drive(new CarInput { Commands = CarCommands.RaiseDead }, 250);
        Assert.NotEqual(gen, server.Invoke(h => h.Seats.Single().Player.Car.NetGeneration));
        Assert.False(server.Invoke(h => I76.Sim.Damage.DamageModel.IsDead(h.Seats.Single().Player.Car)));
    }

    /// <summary>A custom map in the rotation: the client that lacks it is offered it on its connection, downloads it, and is seated.</summary>
    [Fact]
    public void ACustomMapInTheRotationIsOfferedAndDownloadedInTheGame()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var host = new ScratchMapInstall(install, "rounds");
        var custom = host.SyntheticMap("m01", "zzround");
        using var bare = new ScratchMapInstall(install, "roundsbare");
        using var cache = new ScratchCache();
        using var server = new NetMeleeServer(new ServerOptions
        {
            Install = host.Root, Arena = "m01", Rotation = ["m01", custom], Port = 0, TimeLimit = 2, Rounds = true, RoundPause = 1,
        });
        server.Start();
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "Cy", "vppirna1.vcf");
        var gi = new GameInstall(bare.Root) { MapDir = null };
        bool offered = false;
        for (int t = 0; t < 3000 && !(c.Rounds == 1 && c.State == ClientState.Joined); t++)
        {
            c.Poll();
            if (c.State == ClientState.MapOffered && !offered)
            {
                offered = true;
                Assert.Equal(1, c.Rounds);       // the offer came with the second round, on the connection from the first
                Assert.Equal(MapChoice.Download, cache.Cache.Choose(gi, c.Offer!, MapFileSet.DefaultMaxBytes, out _));
                c.DownloadMap(cache.Cache);
            }
            Thread.Sleep(10);
        }
        Assert.True(offered);
        Assert.Equal(ClientState.Joined, c.State);
        Assert.Equal(custom, c.Accept!.Arena, ignoreCase: true);
        Assert.NotNull(c.MapDir);
        Assert.Equal(1, server.Players);
        Assert.Single(server.Host.Seats);
    }

    /// <summary>An empty server that carries on holds its round: no clock, no round change; the first joiner gets a full one.</summary>
    [Fact]
    public void AnEmptyServerHoldsItsRound()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, TimeLimit = 2, Rounds = true, RoundPause = 1 });
        server.Start();
        Thread.Sleep(4000);
        Assert.Equal(0, server.Round);
        Assert.False(server.Host.Over);
        Assert.True(server.Host.Tick < 5, $"tick {server.Host.Tick}");
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "First", "vppirna1.vcf");
        for (int t = 0; t < 500 && c.State != ClientState.Joined; t++) { c.Poll(); Thread.Sleep(10); }
        Assert.Equal(ClientState.Joined, c.State);
        Thread.Sleep(1000);
        Assert.False(server.Host.Game.Ended);       // its two seconds have only begun
        // the time limit, then the 10 s to the end ("*** Time limit has elapsed!")
        for (int t = 0; t < 1600 && !server.Host.Over; t++) { c.Poll(); Thread.Sleep(10); }
        Assert.True(server.Host.Over);
    }
}
