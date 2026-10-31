using I76.Net;
using I76.Server;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The Nitro Pack's race and CTF through the server's host, on the Nitro
/// install's own arenas (skipped unless I76_NITRO_INSTALL names it): cars carried from point to point
/// as a scripted client would drive them.
/// </summary>
public class NitroModeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? Nitro
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e ? e : Environment.GetEnvironmentVariable("I76_NITRO");
            return d is { Length: > 0 } && Directory.Exists(d) ? d : null;
        }
    }

    private static void Put(Seat s, Double3 p) { s.Player.Car.Pos = p; s.Player.Car.Vel = default; }

    private static void Steps(MeleeHost h, int n) { for (int i = 0; i < n; i++) h.Step(); }

    /// <summary>
    /// The join accept carries the arena's fingerprint (mission file and terrain, size and CRC); a client whose copy
    /// matches plays, one whose copy differs or who has no such map is told why before it builds anything.
    /// </summary>
    [Fact]
    public void TheJoinAcceptNamesTheArenasFilesAndAClientWithoutThemRefuses()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N51.CBT" });
        var seat = host.Join("Taurus", "vppirna1.vcf", 0);
        var acc = host.AcceptFor(seat);
        var install = new I76.Formats.Vfs.GameInstall(Nitro);
        Assert.Equal((uint)new FileInfo(install.FindMission("N51.CBT")!).Length, acc.MapSize);
        Assert.Equal(I76.Formats.Mission.MapFingerprint.Crc(File.ReadAllBytes(install.FindMission("N51.CBT")!)), acc.MapCrc);
        Assert.Equal((uint)new FileInfo(install.FindTerrain("n51.ter")!).Length, acc.TerrainSize);
        Assert.Null(acc.MapMismatch(install));
        Assert.Contains("does not match", (acc with { MapCrc = acc.MapCrc ^ 1 }).MapMismatch(install));
        Assert.Contains("terrain", (acc with { TerrainCrc = acc.TerrainCrc ^ 1 }).MapMismatch(install));
        Assert.Contains("not in your game", (acc with { Arena = "nosuchmap.cbt" }).MapMismatch(install));
    }

    [Fact]
    public void ARaceOnANitroArenaCountsLapsAndTheFirstToTheLimitWins()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N35.RAC", Kind = MeleeKind.Race, GoalValue = 2 });
        var game = host.Game;
        Assert.Equal(MeleeKind.Race, game.Rules.Kind);
        Assert.NotEmpty(game.Spawns);
        Assert.True(game.Checkpoints.Count >= 2);
        output.WriteLine($"checkpoints {string.Join(",", game.Checkpoints.Keys.Order())}, {game.Spawns.Count} spawns");
        var a = host.Join("alice", "vppirna1.vcf", 0);
        var b = host.Join("bob", "vcgcour1.vcf", 0);
        var accept = host.AcceptFor(a);
        Assert.Equal(MeleeKind.Race, accept.Kind);
        Assert.Equal(MeleeGoal.Laps, accept.Goal);
        Assert.Equal(2, accept.GoalValue);
        var order = game.Checkpoints.Keys.Where(k => k < 99).Order().ToList();
        int total = order.Count;
        void Pass(Seat s, int n) { Put(s, game.Checkpoints[n]); Steps(host, 2); }
        // the first pass of check1 arms; laps count on the wrap
        foreach (var n in order) Pass(a, n);
        Assert.Equal(0, a.Player.Laps);
        Pass(a, order[0]);
        Assert.Equal(1, a.Player.Laps);
        // a missed checkpoint blocks the count
        Pass(b, order[0]);
        Pass(b, order[^1]);
        Pass(b, order[0]);
        Assert.Equal(0, b.Player.Laps);
        // kills score nothing in a race: a shot at b's car from a does not score
        Assert.Equal(0, a.Player.Score);
        foreach (var n in order.Skip(1)) Pass(a, n);
        Assert.False(game.Ended);
        Pass(a, order[0]);                                     // lap 2: the goal
        Assert.Equal(2, a.Player.Laps);
        Assert.True(game.Ended);
        Assert.Same(a.Player, game.Winner);
        Steps(host, 5);
        Assert.True(DamageModel.IsDead(b.Player.Car), "the loser's car blows up");
        Assert.False(DamageModel.IsDead(a.Player.Car));
        Assert.False(host.Over);
        Steps(host, (int)(10 / SimRate.Default.TickSeconds) + 20);
        Assert.True(host.Over);
        var events = host.TakeEvents();
        Assert.Contains(events, e => e is GameOverEvent g && g.Standings[0] == ((byte)a.Slot, 2));
        Assert.Contains(events, e => e is EndEvent { TimeUp: false } x && x.Winner == a.Slot && x.Count == 2);
        Assert.Contains(events, e => e is ScoreEvent { Kills: 2 } s && s.Slot == a.Slot);
    }

    [Theory]
    [InlineData("N40.CF2", 2)]
    public void CaptureTheFlagOnANitroArenaCarriesTheFlagHome(string arena, int teams)
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = arena, Kind = MeleeKind.Ctf, CtfTeams = teams, GoalValue = 1 });
        var game = host.Game;
        Assert.True(game.Rules.TeamPlay);
        Assert.Equal(teams, game.Ctf!.Flags.Count);
        Assert.NotEmpty(game.TeamSpawns[0]);
        Assert.NotEmpty(game.TeamSpawns[1]);
        output.WriteLine($"{arena}: flags {string.Join(", ", game.Ctf.Flags.Select(f => $"{f.Number}@{f.HomePos.X:0},{f.HomePos.Z:0}"))}; spawns per team {string.Join("/", game.TeamSpawns.Select(t => t.Count))}");
        var red = host.Join("red", "vppirna1.vcf", 1);
        var green = host.Join("green", "vppirna1.vcf", 2);
        var green2 = host.Join("green2", "vppirna1.vcf", 2);
        var nobody = host.Join("nobody", "vppirna1.vcf", 0);   // none asked: the smaller team
        Assert.Equal(1, red.Player.Team);
        Assert.Equal(2, green.Player.Team);
        Assert.Equal(2, green2.Player.Team);
        Assert.Equal(1, nobody.Player.Team);
        // each team spawned at its own table
        Assert.All(new[] { red, green }, s =>
        {
            var table = game.SpawnTable(s.Player.Team);
            Assert.Contains(table, sp => System.Math.Abs(sp.Pos.X - s.Player.Car.Pos.X) < 1 && System.Math.Abs(sp.Pos.Z - s.Player.Car.Pos.Z) < 1);
        });
        Steps(host, 3);
        var f1 = game.Ctf.Flags.First(f => f.Team == 1);
        var f2 = game.Ctf.Flags.First(f => f.Team == 2);
        Assert.Equal(CtfFlag.Home, f1.State);
        Assert.Equal(CtfFlag.Home, f2.State);
        // red takes green's flag, and dies with it: the flag stays on the wreck
        Put(red, f2.HomePos); Steps(host, 2);
        Assert.Equal(CtfFlag.Carried, f2.State);
        Assert.Same(red.Player, f2.Carrier);
        var snap = ReadSnapshot(host, red);
        Assert.Contains(snap.Flags, f => f.Team == 2 && f.State == 4 && f.Carrier == red.CarSerial);
        host.Setup.Damage.Destroy(red.Player.Car);
        Steps(host, 5);
        Assert.Equal(CtfFlag.Carried, f2.State);
        // and it goes home when red respawns (red presses RAISE_DEAD once the 5 s are up)
        Steps(host, (int)(host.Options.RespawnDelay / SimRate.Default.TickSeconds) + 30);
        Assert.Null(game.RequestRespawn(red.Player));
        Steps(host, 2);
        Assert.Equal(CtfFlag.Home, f2.State);
        Assert.False(game.Ended);
        // green touching its own flag's stand does nothing; red takes green's flag again and captures it
        Put(green, f2.HomePos); Steps(host, 2);
        Assert.Equal(CtfFlag.Home, f2.State);
        Put(red, new Double3(f2.HomePos.X + 1, f2.HomePos.Y, f2.HomePos.Z)); Put(green, f1.HomePos.X > 0 ? new Double3(0, 0, 0) : new Double3(99999, 0, 99999));
        Steps(host, 2);
        Assert.Same(red.Player, f2.Carrier);
        Put(red, f1.HomePos); Steps(host, 2);
        Assert.Equal(CtfFlag.Home, f2.State);
        Assert.Equal(1, red.Player.Captures);
        Assert.Equal(1, game.Ctf.TeamCaptures[1]);
        Assert.Equal(1, nobody.Player.Captures);               // every member of the capturing team scores
        Assert.Equal(0, green.Player.Captures);
        Assert.True(green.Player.Deaths >= 1);
        Assert.True(game.Ended);
        Assert.Equal(1, game.WinningTeam);
        Steps(host, 5);
        Assert.True(DamageModel.IsDead(green.Player.Car));
        Assert.True(DamageModel.IsDead(green2.Player.Car));     // team 2
        Assert.False(DamageModel.IsDead(nobody.Player.Car));
        Steps(host, (int)(10 / SimRate.Default.TickSeconds) + 20);
        Assert.True(host.Over);
        var events = host.TakeEvents();
        Assert.Contains(events, e => e is ChatEvent { From: -1 } c && c.Text.Contains("has captured"));
        Assert.Contains(events, e => e is ChatEvent { From: -1 } c && c.Text.Contains("has been taken"));
        Assert.Contains(events, e => e is GameOverEvent);
    }

    private static Snapshot ReadSnapshot(MeleeHost host, Seat seat)
    {
        var w = new NetWriter();
        var (cars, rounds) = host.Capture();
        Snapshot.WriteHeader(w, host.Tick, 0, host.CaptureOwn(seat));
        Snapshot.WriteBody(w, cars, rounds, host.CaptureFlags());
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.Snapshot, (MsgType)r.U8());
        return Snapshot.Read(ref r);
    }

    [Fact]
    public void TheNewRulesTravelInTheJoinAcceptAndTheProtocolIsBumped()
    {
        Assert.True(Protocol.Version >= 3);
        var acc = new JoinAccept(1, 5, "N40.CF2", 10, "vppirna1.vcf", true, 0, 0, 5, [], [], MeleeKind.Ctf, MeleeGoal.Captures, 3, 4);
        var w = new NetWriter();
        acc.Write(w);
        var r = new NetReader(w.Span);
        r.U8();
        var back = JoinAccept.Read(ref r);
        Assert.Equal((MeleeKind.Ctf, MeleeGoal.Captures, 3, (byte)4), (back.Kind, back.Goal, back.GoalValue, back.Teams));
    }
}
