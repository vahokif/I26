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
    /// Drives a seat's car through a point at 8 m/s along its heading: set down 15 m short of it (near enough that the
    /// arena's slopes do not carry it far off its line, slow enough that it does not leap the crest before N40's flags),
    /// left to settle, sent off and stepped until it is 10 m past (a stand is touched by a car that drives onto it, not
    /// one put there).
    /// </summary>
    private static void DriveThrough(MeleeHost h, Seat s, Double3 at)
    {
        var car = s.Player.Car;
        var fwd = System.Numerics.Vector3.Normalize(car.Rot.Forward with { Y = 0 });
        double sx = at.X - fwd.X * 15, sz = at.Z - fwd.Z * 15;
        car.Pos = new Double3(sx, h.Setup.Ground.Query(sx, at.Y + 2, sz).Height + 0.5, sz);
        car.Vel = default;
        Steps(h, 30);
        car.Vel = fwd * 8; car.Speed = 8;
        for (int i = 0; i < 160 && (car.Pos.X - at.X) * fwd.X + (car.Pos.Z - at.Z) * fwd.Z < 10; i++) h.Step();
    }

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
        // red takes green's flag, and dies with it: the flag goes home as the car blows up, before red respawns
        DriveThrough(host, red, f2.HomePos);
        Assert.Equal(CtfFlag.Carried, f2.State);
        Assert.Same(red.Player, f2.Carrier);
        var snap = ReadSnapshot(host, red);
        Assert.Contains(snap.Flags, f => f.Team == 2 && f.State == 4 && f.Carrier == red.CarSerial);
        host.TakeEvents();
        host.Setup.Damage.Destroy(red.Player.Car);
        Steps(host, 2);
        Assert.Equal(CtfFlag.Home, f2.State);
        Assert.Null(f2.Carrier);
        Assert.Contains(host.TakeEvents(), e => e is ChatEvent { From: -1 } c && c.Text.Contains("returned to its base"));
        snap = ReadSnapshot(host, red);
        Assert.Contains(snap.Flags, f => f.Team == 2 && f.State == 1 && f.Carrier == 0);
        Steps(host, 3);
        // red respawns (RAISE_DEAD once the 5 s are up): nothing more to return
        Steps(host, (int)(host.Options.RespawnDelay / SimRate.Default.TickSeconds) + 30);
        Assert.Null(game.RequestRespawn(red.Player));
        Steps(host, 2);
        Assert.Equal(CtfFlag.Home, f2.State);
        Assert.False(game.Ended);
        // green touching its own flag's stand does nothing; red takes green's flag again and captures it
        DriveThrough(host, green, f2.HomePos);
        Assert.Equal(CtfFlag.Home, f2.State);
        Put(green, f1.HomePos.X > 0 ? new Double3(0, 0, 0) : new Double3(99999, 0, 99999));
        DriveThrough(host, red, f2.HomePos);
        Assert.Same(red.Player, f2.Carrier);
        DriveThrough(host, red, f1.HomePos);
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

    /// <summary>
    /// A carrier whose driver is killed (the .45, the Chemical Mortar) does not blow up: the flag stays on the car past its
    /// 10 s death timer, and goes home only when its player respawns.
    /// </summary>
    [Fact]
    public void ACarrierWhoseDriverIsKilledKeepsTheFlagUntilTheRespawn()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N40.CF2", Kind = MeleeKind.Ctf, CtfTeams = 2, GoalValue = 3 });
        var game = host.Game;
        var red = host.Join("red", "vppirna1.vcf", 1);
        host.Join("green", "vppirna1.vcf", 2);
        Steps(host, 3);
        var f2 = game.Ctf!.Flags.First(f => f.Team == 2);
        DriveThrough(host, red, f2.HomePos);
        Assert.Same(red.Player, f2.Carrier);
        var car = red.Player.Car;
        car.Flags |= (I76.Sim.Vehicles.VehicleFlags)0x4000;
        host.Setup.Damage.Destroy(car);
        Assert.Equal(0x4020u, (uint)car.Flags & 0x4020u);
        Steps(host, (int)(12 / SimRate.Default.TickSeconds));
        Assert.Equal(CtfFlag.Carried, f2.State);
        Assert.Same(red.Player, f2.Carrier);
        Assert.Null(game.RequestRespawn(red.Player));
        Steps(host, 2);
        Assert.Equal(CtfFlag.Home, f2.State);
        Assert.Null(f2.Carrier);
    }

    /// <summary>
    /// The arena's own flag (a1flagN: a base disc and a pole with its pennant) is no obstacle: the car's collide slot ignores
    /// both parts, so a car driven through it at 15 m/s meets nothing (no bounce, no ram damage) and still takes the flag
    /// through the CTF game's base contact.
    /// </summary>
    [Fact]
    public void ACarDrivesThroughAnEnemyFlagAndTakesIt()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N40.CF2", Kind = MeleeKind.Ctf, CtfTeams = 2, GoalValue = 3 });
        var game = host.Game;
        var red = host.Join("red", "vppirna1.vcf", 1);
        host.Join("green", "vppirna1.vcf", 2);
        Steps(host, 3);
        var f2 = game.Ctf!.Flags.First(f => f.Team == 2);
        var flag = host.Setup.Statics.Objects.Single(o => o.Name.TrimStart('#').StartsWith("a1flag2", StringComparison.OrdinalIgnoreCase));
        // the stand's base as the arena's model gives it: a 4 m sphere (class 0x4000) about the disc's middle, 0.15 m above
        // the flag's grounded placement, and the model's bounds (the 8 m disc and the pole)
        Assert.Equal(4f, f2.Base.Radius, 3);
        Assert.Equal(f2.Base.Radius, f2.Base.Grow);
        Assert.Equal(0.15f + (float)(flag.Origin.Y - f2.HomePos.Y), f2.Base.Origin.Y, 3);
        Assert.Equal(f2.Base.Origin, f2.Base.Centre);
        Assert.InRange(f2.Base.BoundsRadius, 4.3f, 4.45f);
        var car = red.Player.Car;
        var fwd = car.Rot.Forward;
        fwd = System.Numerics.Vector3.Normalize(fwd with { Y = 0 });
        double sx = f2.HomePos.X - fwd.X * 25, sz = f2.HomePos.Z - fwd.Z * 25;
        car.Pos = new Double3(sx, host.Setup.Ground.Query(sx, f2.HomePos.Y + 2, sz).Height + 0.5, sz);
        car.Vel = default;
        Steps(host, 30);
        car.Vel = fwd * 15; car.Speed = 15;
        var start = car.Pos;
        double Along() => (car.Pos.X - f2.HomePos.X) * fwd.X + (car.Pos.Z - f2.HomePos.Z) * fwd.Z;
        // the arena's ground slows the car (it climbs to the flag); the flag itself takes nothing off it
        float slowest = float.MaxValue, worstDrop = 0, last = 15;
        for (int i = 0; i < 160 && Along() < 12; i++)
        {
            host.Step();
            // no contact with the flag, so no ram damage either (the ground's bumps cost the car a little on its own)
            Assert.DoesNotContain(host.Session.World.Step.ObjectHits, h => ReferenceEquals(h.Self, car) && h.Name == flag.Name);
            float now = System.Numerics.Vector3.Dot(car.Vel, fwd);
            slowest = MathF.Min(slowest, now);
            if (System.Math.Abs(Along()) < 3) worstDrop = MathF.Max(worstDrop, last - now);
            last = now;
        }
        output.WriteLine($"flag {flag.Name} at {f2.HomePos.X:0.0},{f2.HomePos.Z:0.0}: colliders {flag.Boxes.Count}/{flag.Spheres.Count}/{flag.Polys.Count}; car from {start.X:0.0},{start.Z:0.0} to {car.Pos.X:0.0},{car.Pos.Z:0.0}, slowest {slowest:0.0} m/s, at most {worstDrop:0.00} m/s lost in a tick at the flag");
        Assert.True(Along() > 6, $"the car stopped at the flag: {Along():0.0} m past it");
        Assert.True(slowest > 3, $"the car bounced or was stopped: {slowest:0.0} m/s");
        Assert.True(worstDrop < 0.5f, $"the car was slowed at the flag: {worstDrop:0.00} m/s in a tick");
        Assert.Equal(CtfFlag.Carried, f2.State);
        Assert.Same(red.Player, f2.Carrier);
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
