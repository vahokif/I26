using System.Numerics;
using I76.Net;
using I76.Server;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The original's network rules over the wire (docs/multiplayer.md, "The original's rules"): RAISE_DEAD and the predicted
/// car through a death, Reconfigure and its refusals, the boot, the invite-only join and its timeout, the end's lines.
/// </summary>
public class NetRulesTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A headless client of one seat: its mirror, its connection's buffers and its prediction.</summary>
    private sealed class Client
    {
        public required Seat Seat;
        public required ClientWorld Mirror;
        public readonly MeleeClient Net = new();
        public required OwnCarPredictor Predictor;
        public readonly List<NetEvent> News = [];
        public Vehicle Car => Mirror.Own!.Vehicle;
    }

    /// <summary>Each tick: the clients' inputs to the server and their predictions, the server's tick, its snapshots and events two ticks late.</summary>
    private sealed class Rig(MeleeHost host)
    {
        public uint T;
        public readonly List<Client> Clients = [];
        private readonly Queue<(List<(Client C, Snapshot S)> Snaps, EventBatch? Events)> _wire = new();

        public Client Add(Seat seat)
        {
            var mirror = new ClientWorld(HostTests.Mirror(host.Arena), host.AcceptFor(seat));
            var c = new Client { Seat = seat, Mirror = mirror, Predictor = null! };
            c.Predictor = new OwnCarPredictor(mirror, c.Net, predictFire: false);
            mirror.News += c.News.Add;
            Clients.Add(c);
            return c;
        }

        public void Tick(Func<Client, CarInput>? input = null)
        {
            T++;
            foreach (var c in Clients)
            {
                var i = input?.Invoke(c) ?? default;
                c.Seat.Inputs.Add(new InputMessage(T, [i]));
                c.Predictor.Step(T, i);
            }
            host.Step();
            var events = host.TakeEvents();
            _wire.Enqueue((Clients.Select(c => (c, PredictionTests.Through(host, c.Seat))).ToList(), events.Count > 0 ? new EventBatch(host.Tick, events) : null));
            while (_wire.Count > 2)
            {
                var (snaps, batch) = _wire.Dequeue();
                foreach (var (c, s) in snaps)
                {
                    if (batch is not null) c.Net.Events.Enqueue(batch);
                    c.Net.Inject(s);
                }
            }
            foreach (var c in Clients)
            {
                c.Mirror.Advance(host.Session.TickSeconds, c.Net);
                c.Predictor.Reconcile();
                c.Mirror.Setup.Combat.Events.Clear();
                c.Mirror.Setup.Damage.Events.Clear();
            }
        }

        public void Run(float seconds, Func<Client, CarInput>? input = null)
        {
            for (int k = 0; k < seconds / host.Session.TickSeconds; k++) Tick(input);
        }
    }

    private static void Kill(MeleeHost host, Vehicle victim, Vehicle? attacker)
    {
        for (int k = 0; k < 200 && !I76.Sim.Damage.DamageModel.IsDead(victim); k++)
        {
            var p = new DamagePacket { Attacker = attacker, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
            p.Ch[1] = 5000;
            host.Setup.Damage.Apply(victim, p, new Vector3(0, 0.5f, 2));
        }
        Assert.True(I76.Sim.Damage.DamageModel.IsDead(victim));
    }

    private static bool Dead(Vehicle v) => (v.Flags & VehicleFlags.Dead) != 0;

    /// <summary>
    /// RAISE_DEAD over the wire (confirmed in the real game): pressed 3 s after the death it is refused; the
    /// car never comes back on its own (20 s); pressed then, it does. Meanwhile the client's predicted car is dead, does
    /// not drive whatever is held, and is predicted exactly, until the server's respawn reaches it.
    /// </summary>
    [Fact]
    public void ADeadPlayerComesBackOnlyByRaiseDead_ThePredictedCarStaysDeadTillThen()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vcgcour1.vcf", 0);
        var rig = new Rig(host);
        var cb = rig.Add(b);
        rig.Run(1);
        int inexact = cb.Predictor.Inexact, compared = cb.Predictor.Compared;
        Kill(host, b.Player.Car, a.Player.Car);
        bool press = false;
        CarInput Drive(Client c) => new() { Buttons = CarButtons.ThrottleUp, Commands = press ? CarCommands.RaiseDead : 0 };
        rig.Run(1, Drive);
        Assert.True(b.Player.Destroyed);
        Assert.True(Dead(cb.Car), "the predicted car is dead");
        var wreck = cb.Predictor.Pose(1).Pos;
        rig.Run(2, Drive);
        press = true;
        rig.Tick(Drive);
        press = false;
        rig.Run(17, Drive);
        // the dead car is predicted exactly (its death timer runs as the server's): only the tick of the death itself misses
        output.WriteLine($"dead: {cb.Predictor.Inexact - inexact} inexact of {cb.Predictor.Compared - compared}");
        Assert.True(cb.Predictor.Inexact - inexact <= 3, $"{cb.Predictor.Inexact - inexact} inexact of {cb.Predictor.Compared - compared} while dead");
        Assert.True(b.Player.Destroyed, "no respawn without RAISE_DEAD, nor before 5 s");
        Assert.True(Dead(cb.Car));
        var still = cb.Predictor.Pose(1).Pos;
        Assert.True(System.Math.Abs(still.X - wreck.X) + System.Math.Abs(still.Z - wreck.Z) < 3, "the dead predicted car drove");
        press = true;
        rig.Tick(Drive);
        press = false;
        Assert.False(b.Player.Destroyed);
        rig.Run(1, Drive);
        Assert.False(Dead(cb.Car), "the client's car is back");
        Assert.Contains(cb.News, e => e is RenewedEvent { Respawn: true } r && r.Car == b.CarSerial);
        Assert.True(cb.Car.Speed > 1, $"the new car drives: {cb.Car.Speed}");
        var srv = b.Player.Car.Pos;
        Assert.True(Vector3.Distance(srv.ToVector3(), cb.Car.Pos.ToVector3()) < 2, "the prediction follows the server's new car");
    }

    /// <summary>
    /// Reconfigure while dead: refused while alive, for a car the server lacks, for a restricted weapon, for an illegal
    /// configuration (the old car stays); taken, the next respawn brings the new car on the server and on both clients (the
    /// player's own, rebuilt in place, predicted as before; the other's copy).
    /// </summary>
    [Fact]
    public void AReconfiguredCarIsCheckedAndTheRespawnBringsIt()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Restrictions = WeaponRestrictions.Parse("missiles", null, out _) });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var rig = new Rig(host);
        var ca = rig.Add(a);
        var cb = rig.Add(b);
        rig.Run(1);
        Assert.Equal("only while your car is destroyed", host.Reconfigure(b, "vcgcour1.vcf", null, out _));
        Kill(host, b.Player.Car, a.Player.Car);
        rig.Run(1);
        Assert.StartsWith("this server does not have", host.Reconfigure(b, "vnosuch1.vcf", null, out _));
        // a car with a missile launcher, restricted in this game; and a configuration with the same special twice
        var vfs = host.Vfs;
        var withMissiles = vfs.Names.Select(n => n.ToLowerInvariant()).Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^v[a-z]+1\.vcf$") && host.OfferedModels.Contains(n[..^5]))
            .First(n => host.CheckCar(n) is { } why && why.StartsWith("Restricted"));
        Assert.StartsWith("Restricted weapons", host.Reconfigure(b, withMissiles, null, out _));
        var twice = TripCar.FromVcf(vfs.Read("vcgcour1.vcf"));
        twice.Variant = "Twice";
        twice.Specials = [1, 1, 0];
        Assert.Contains("the same special twice", host.Reconfigure(b, "vcgcour9.vcf", twice.ToVcf(), out _) ?? "");
        Assert.Null(b.Player.NextCar);
        // taken: the player's own variant, served by its content
        var own = TripCar.FromVcf(vfs.Read("vcgcour1.vcf"));
        own.Variant = "Mine";
        Assert.Null(host.Reconfigure(b, "vcgcour9.vcf", own.ToVcf(), out var file));
        Assert.Matches(@"^n[0-9a-f]{8}vcgcour\.vcf$", file);
        rig.Run(5);
        rig.Tick(c => c == cb ? new CarInput { Commands = CarCommands.RaiseDead } : default);
        rig.Run(1, c => c == cb ? new CarInput { Buttons = CarButtons.ThrottleUp } : default);
        Assert.Equal((file, 1), (b.Player.CarFile, b.Player.CarEpoch));
        Assert.Equal(file, b.Player.Car.Definition);
        // the own client: its mirror car rebuilt in place, the build counted, the prediction on the new car
        Assert.Equal(1, cb.Mirror.OwnEpoch);
        Assert.Equal(file, cb.Mirror.Own!.Info.Def);
        Assert.Equal(file, cb.Car.Definition);
        Assert.Same(cb.Car, cb.Mirror.Car(b.CarSerial));
        Assert.False(Dead(cb.Car));
        Assert.True(Vector3.Distance(b.Player.Car.Pos.ToVector3(), cb.Car.Pos.ToVector3()) < 2);
        Assert.True(cb.Car.Speed > 1, $"speed {cb.Car.Speed}");
        // the other client's copy
        Assert.Equal(file, ca.Mirror.Cars[b.CarSerial].Info.Def);
        Assert.Equal(file, ca.Mirror.Cars[b.CarSerial].Vehicle.Definition);
        Assert.Contains(ca.News, e => e is RenewedEvent r && r.Car == b.CarSerial);
        // a later join is told the new car
        Assert.Contains(host.AcceptFor(a).Cars, c => c.Serial == b.CarSerial && c.Def == file && c.Vcf is not null);
    }

    private static void Throw(Vehicle v)
    {
        v.Pos = new I76.Sim.Math.Double3(v.Pos.X, v.Pos.Y + 20, v.Pos.Z);
        v.Rot = I76.Sim.Math.Rows.Concat(I76.Sim.Math.Rows.AboutZ(2.2f), I76.Sim.Math.Rows.AboutY(1.0f));
        v.Vel = new Vector3(5, -8, 2);
        v.AngVel = new Vector3(1, 2, 1);
        v.Flags |= VehicleFlags.Airborne;
    }

    /// <summary>
    /// The reset-upright key over the wire: a client's press reaches the server, which stands that car on the terrain under it,
    /// upright and still; the client's own car and the other client's copy follow (the generation jump snaps them), and a car
    /// that did not press is left alone. The host's switch (ServerOptions.AllowCarReset) turns it off.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AClientsResetIsAppliedByTheServerWhereTheHostAllowsIt(bool allowed)
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", AllowCarReset = allowed });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vcgcour1.vcf", 0);
        var rig = new Rig(host);
        var ca = rig.Add(a);
        var cb = rig.Add(b);
        rig.Run(1);
        var (ac, bc) = (a.Player.Car, b.Player.Car);
        Throw(bc);
        Throw(ac);
        float ground = host.Setup.World.Ground.BareHeight(bc.Pos.X, bc.Pos.Z);
        rig.Tick(c => c == cb ? new CarInput { Commands = CarCommands.ResetUpright } : default);
        rig.Run(0.5f);
        if (allowed)
        {
            Assert.True(bc.Rot.Up.Y > 0.95f, $"up {bc.Rot.Up}");
            Assert.True(System.Math.Abs(bc.Pos.Y - ground) < 1, $"y {bc.Pos.Y}, the ground {ground}");
            Assert.True(bc.Speed < 2, $"speed {bc.Speed}");
            // the client's own car (a prediction the generation jump restarted) and the other's copy of it
            Assert.True(cb.Car.Rot.Up.Y > 0.9f && Vector3.Distance(bc.Pos.ToVector3(), cb.Car.Pos.ToVector3()) < 2, $"the client's car at {cb.Car.Pos} up {cb.Car.Rot.Up}");
            Assert.True(Vector3.Distance(bc.Pos.ToVector3(), ca.Mirror.Cars[b.CarSerial].Vehicle.Pos.ToVector3()) < 6);
        }
        else Assert.True(bc.Pos.Y > ground + 5, "the host did not allow the reset: the car is still where it fell");
        // the car that did not press was not touched
        Assert.True(ac.Rot.Up.Y < 0.5f || ac.Pos.Y > host.Setup.World.Ground.BareHeight(ac.Pos.X, ac.Pos.Z) + 3);
    }

    /// <summary>
    /// The host's Chase Crosshair reaches the players in the join accept (and the game's listing): allowed, a player's own
    /// setting shows the crosshair; not allowed (the default), it is hidden whatever the player chose. Single player is the setting alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHostsCrosshairRuleReachesTheClients(bool allowed)
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, AllowCrosshair = allowed });
        server.Start();
        using var a = Connect(server, "Alice");
        Pump(() => a.State == ClientState.Joined, a);
        Assert.Equal(ClientState.Joined, a.State);
        Assert.Equal(allowed, a.Accept!.Crosshair);
        Assert.Equal(allowed, a.CrosshairAllowed);
        Assert.Equal(allowed, server.Host.Info().Crosshair);
        Assert.Equal(allowed, I76.Sim.Views.CrosshairPolicy.Shown(true, network: true, a.CrosshairAllowed));
        Assert.False(I76.Sim.Views.CrosshairPolicy.Shown(false, network: true, a.CrosshairAllowed));
        Assert.True(I76.Sim.Views.CrosshairPolicy.Shown(true, network: false, hostAllows: false));
        Assert.False(new ServerOptions { Install = install }.AllowCrosshair);
    }

    private static MeleeClient Connect(NetMeleeServer server, string name)
    {
        var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, name, "vppirna1.vcf");
        return c;
    }

    private static void Pump(Func<bool> until, params MeleeClient[] clients)
    {
        for (int t = 0; t < 500 && !until(); t++) { foreach (var c in clients) c.Poll(); Thread.Sleep(10); }
    }

    /// <summary>The boot (i26-server's kick): by name or slot, the player told, the others seeing them leave.</summary>
    [Fact]
    public void TheHostBootsAPlayer()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0 });
        server.Start();
        using var a = Connect(server, "Alice");
        using var b = Connect(server, "Bob");
        using var c = Connect(server, "Carol");
        Pump(() => new[] { a, b, c }.All(x => x.State == ClientState.Joined), a, b, c);
        Assert.Equal("no player Zed", server.Kick("Zed"));
        Assert.Null(server.Kick("bob"));
        Pump(() => b.State == ClientState.Disconnected, a, b, c);
        Assert.Equal((ClientState.Disconnected, Protocol.Booted), (b.State, b.Reason));
        Assert.Null(server.Kick(c.Accept!.Slot));
        Pump(() => c.State == ClientState.Disconnected && a.Events.SelectMany(x => x.Events).OfType<LeftEvent>().Count() >= 2, a, b, c);
        Assert.Equal(Protocol.Booted, c.Reason);
        var left = a.Events.SelectMany(x => x.Events).OfType<LeftEvent>().ToList();
        Assert.Equal(["booted", "booted"], left.Select(l => l.Reason));
        Assert.Equal(1, server.Players);
    }

    /// <summary>
    /// INVITE ONLY: a join waits for the host's answer; Ctrl+H asks about the next first, Ctrl+N
    /// turns one away ("The host has rejected you..."), Ctrl+Y lets one in; one unanswered is turned away after its time
    /// ("Your request timed out"). A listen host's own first join is let in without asking.
    /// </summary>
    [Fact]
    public void InviteOnlyJoinsWaitForTheHost()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, InviteOnly = true, ListenHost = true, InviteSeconds = 3 });
        var asked = new List<string>();
        server.InviteAsked += asked.Add;
        server.Start();
        using var host = Connect(server, "Host");
        Pump(() => host.State == ClientState.Joined, host);
        Assert.Equal(ClientState.Joined, host.State);
        Assert.Null(server.PendingInvite);
        using var x = Connect(server, "Xavier");
        Pump(() => x.WaitingForHost, host, x);
        using var y = Connect(server, "Yolanda");
        Pump(() => y.WaitingForHost && server.InvitesWaiting == 2, host, x, y);
        Assert.Equal("Xavier", server.PendingInvite?.Name);
        Assert.Equal("Waiting for the host to let you in...", x.Stage);
        Assert.Equal("Xavier", server.AnswerInvite(InviteAnswer.Skip));
        Assert.Equal("Yolanda", server.PendingInvite?.Name);
        Assert.Equal("Yolanda", server.AnswerInvite(InviteAnswer.Decline));
        Pump(() => y.State != ClientState.Connecting, host, x, y);
        Assert.Equal(Protocol.InviteDeclined, y.Reason);
        Assert.Equal("Xavier", server.AnswerInvite(InviteAnswer.Accept));
        Pump(() => x.State == ClientState.Joined, host, x);
        Assert.Equal(ClientState.Joined, x.State);
        Assert.False(x.WaitingForHost);
        using var z = Connect(server, "Zed");
        Pump(() => z.State != ClientState.Connecting, host, x, z);
        Assert.Equal(Protocol.InviteTimedOut, z.Reason);
        Assert.Null(server.PendingInvite);
        Assert.Equal(2, server.Players);
        Assert.Contains("Zed", asked);
    }

    /// <summary>
    /// The end: the goal reached sends each machine its own line (the winner "You win!", the others "You lose, chump!",
    /// the Nitro forms with the count), the losers blown up; the time limit's "*** Time limit has elapsed!", everyone a winner.
    /// </summary>
    [Fact]
    public void TheEndIsSaidOnEachMachine()
    {
        var e = new EndEvent(false, 1, 0, 20000);
        Assert.Equal("*** You have reached the maximum score! You win!", NetLines.End(e, false, MeleeGoal.Points, false, 1, 0, "Bob", "", out bool won));
        Assert.True(won);
        Assert.Equal("*** Bob has reached the maximum score! You lose, chump!", NetLines.End(e, false, MeleeGoal.Points, false, 2, 0, "Bob", "", out won));
        Assert.False(won);
        var t = new EndEvent(false, 1, 3, 20000);
        Assert.Equal("*** Team Crimson Kings has reached the maximum score! You win!", NetLines.End(t, false, MeleeGoal.Points, true, 4, 3, "Bob", MeleeGame.TeamName(MeleeKind.Combat, 3), out won));
        Assert.Equal("*** Team Crimson Kings has reached the maximum score! You lose, chump!", NetLines.End(t, false, MeleeGoal.Points, true, 4, 2, "Bob", MeleeGame.TeamName(MeleeKind.Combat, 3), out won));
        Assert.Equal("*** Bob has completed 3 laps! You lose, chump!", NetLines.End(new EndEvent(false, 1, 0, 3), true, MeleeGoal.Laps, false, 2, 0, "Bob", "", out _));
        Assert.Equal("*** Bob has reached 20 kills! You win!", NetLines.End(new EndEvent(false, 1, 0, 20), true, MeleeGoal.Kills, false, 1, 0, "Bob", "", out _));
        Assert.Equal("*** Red Dawn has reached 3 captures! You win!", NetLines.End(new EndEvent(false, 1, 1, 3), true, MeleeGoal.Captures, true, 0, 1, "Bob", "Red Dawn", out _));
        Assert.Null(NetLines.End(new EndEvent(true, -1, 0, 0), true, MeleeGoal.Points, false, 0, 0, "?", "", out won));
        Assert.True(won);
        Assert.Equal("*** Bob was killed by Al", NetLines.Kill("Bob", "Al"));
        Assert.Equal("*** Bob has been killed", NetLines.Kill("Bob", null));
        Assert.Equal("*** Bob has joined the game", NetLines.Join(false, false, "Bob", ""));
        Assert.Equal("*** Player Bob has joined the game", NetLines.Join(true, false, "Bob", ""));
        Assert.Equal("*** A Gang Green team member has joined the game!", NetLines.Join(true, true, "Bob", "Gang Green"));
        Assert.Equal("*** Player Bob has left the game", NetLines.Leave(true, "Bob"));
        Assert.Equal("From Bob: hi", NetLines.Chat("Bob", "hi"));

        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", MaxScore = 500 });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var c = host.Join("c", "vppirna1.vcf", 0);
        host.Step();
        Kill(host, c.Player.Car, a.Player.Car);      // 1000 points: the goal
        host.Step();
        host.Step();
        var events = host.TakeEvents();
        var end = Assert.Single(events.OfType<EndEvent>());
        Assert.Equal((false, (sbyte)a.Slot, a.Player.Score), (end.TimeUp, end.Winner, end.Count));
        Assert.True(I76.Sim.Damage.DamageModel.IsDead(b.Player.Car));
        // the blow-up has no killer: "*** b has been killed" on every machine, a death and −500 (to 0) for b
        Assert.Contains(events, x => x is KillEvent k && k.Victim == b.CarSerial && k.Attacker == 0);
        Assert.Equal((1, 0), (b.Player.Deaths, b.Player.Score));
        // and the time limit
        var timed = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", TimeLimit = 2 });
        timed.Join("a", "vppirna1.vcf", 0);
        var all = new List<NetEvent>();
        for (int k = 0; k < 2.2f / timed.Session.TickSeconds; k++) { timed.Step(); all.AddRange(timed.TakeEvents()); }
        Assert.Contains(all, x => x is ChatEvent { From: -1, Text: "*** Time limit has elapsed!" });
        Assert.Contains(all, x => x is EndEvent { TimeUp: true });
        Assert.False(timed.Over);
        for (int k = 0; k < 10.2f / timed.Session.TickSeconds && !timed.Over; k++) timed.Step();
        Assert.True(timed.Over);
    }

    /// <summary>The strict checks a joining car meets (the original's car check): the same special twice, a second turret, a gun on a dropper mount.</summary>
    [Fact]
    public void StrictChecksRefuseWhatTheOriginalRefused()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var vfs = host.Vfs;
        var car = TripCar.FromVcf(vfs.Read("vcgcour1.vcf"));
        car.Specials = [2, 2, 0];
        Assert.Contains("the same special twice", CarConfigCheck.Strict(vfs, car, "vcgcour") ?? "");
        // a stock car with a dropper mount: a gun on it, or a dropper on a gun mount
        var hpOf = new Dictionary<string, List<I76.Formats.Bwd2.Hardpoint>>();
        foreach (var stem in host.OfferedModels.Order())
        {
            var c = TripCar.FromVcf(vfs.Read(stem + "1.vcf"));
            Assert.Null(CarConfigCheck.Strict(vfs, c, stem));
            var hps = I76.Formats.Bwd2.Hardpoint.Read(new I76.Formats.Bwd2.Bwd2File(vfs.Read(c.Vdf)));
            var dropper = c.Weapons.FirstOrDefault(w => WeaponRestrictions.KeyOf(vfs, w.Gdf) is { Class: 6 });
            var gun = c.Weapons.FirstOrDefault(w => WeaponRestrictions.KeyOf(vfs, w.Gdf) is { Class: 2, Tier: < 100 });
            if (dropper.Gdf is null || gun.Gdf is null) continue;
            var swapped = TripCar.FromVcf(vfs.Read(stem + "1.vcf"));
            swapped.Weapons = [.. c.Weapons.Select(w => w.Hardpoint == dropper.Hardpoint ? (w.Hardpoint, gun.Gdf) : w)];
            Assert.Contains("on a dropper mount", CarConfigCheck.Strict(vfs, swapped, stem) ?? "");
            output.WriteLine($"{stem}: {CarConfigCheck.Strict(vfs, swapped, stem)}");
            return;
        }
        Assert.Fail("no stock car with both a dropper and a gun");
    }

    private static readonly I76.Formats.Imaging.CarPaint Red = new(new I76.Formats.Imaging.PaintColour(200, 10, 20), new I76.Formats.Imaging.PaintColour(1, 2, 3), I76.Formats.Imaging.PaintFinish.Metallic);
    private static readonly I76.Formats.Imaging.CarPaint Blue = new(new I76.Formats.Imaging.PaintColour(0, 0, 255));

    /// <summary>A coloured seat: the join accepts (its own and a second client's) list the paint, the mirror vehicles wear it, a respawn keeps it, a savestate restores it.</summary>
    [Fact]
    public void ACarPaintReachesEveryPlayerAndSurvivesARespawnAndASavestate()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0, paintBytes: Red.ToBytes());
        Assert.Equal(Red, b.Player.Car.Paint);
        Assert.Equal(Red, b.Player.Paint);
        Assert.Null(a.Player.Car.Paint);
        Assert.Equal(Red.ToBytes(), host.AcceptFor(a).Cars.Single(c => c.Serial == b.CarSerial).Paint);
        Assert.Null(host.AcceptFor(a).Cars.Single(c => c.Serial == a.CarSerial).Paint);
        var rig = new Rig(host);
        var ca = rig.Add(a);
        var cb = rig.Add(b);
        rig.Run(1);
        Assert.Equal(Red, ca.Mirror.Cars[b.CarSerial].Vehicle.Paint);
        Assert.Equal(Red, cb.Car.Paint);
        Assert.Null(ca.Car.Paint);
        // a respawn keeps it
        Kill(host, b.Player.Car, a.Player.Car);
        rig.Run(5);
        rig.Tick(c => c == cb ? new CarInput { Commands = CarCommands.RaiseDead } : default);
        rig.Run(1);
        Assert.Equal(Red, b.Player.Car.Paint);
        // Reconfigure to the same car with another paint rebuilds it with that one, and the clients are told
        Kill(host, b.Player.Car, a.Player.Car);
        rig.Run(5);
        Assert.Null(host.Reconfigure(b, "vppirna1.vcf", null, out _, Blue.ToBytes()));
        Assert.Equal(Blue, b.Player.Paint);
        rig.Tick(c => c == cb ? new CarInput { Commands = CarCommands.RaiseDead } : default);
        rig.Run(3);
        Assert.Equal(1, b.Player.CarEpoch);
        Assert.Equal(Blue, b.Player.Car.Paint);
        Assert.Equal(Blue, ca.Mirror.Cars[b.CarSerial].Vehicle.Paint);
        Assert.Equal(Blue, cb.Car.Paint);
        // the savestate: a successor host's cars and players wear it
        var fresh = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        fresh.RestoreMigrated(host.Session.Snapshot());
        Assert.Equal(Blue, fresh.Seats.Single(x => x.Slot == b.Slot).Player.Paint);
        Assert.Equal(Blue, fresh.Seats.Single(x => x.Slot == b.Slot).Player.Car.Paint);
        Assert.Null(fresh.Seats.Single(x => x.Slot == a.Slot).Player.Car.Paint);
        // bytes that do not parse are the factory paint
        var c3 = host.Join("c", "vppirna1.vcf", 0, paintBytes: [9, 9, 9]);
        Assert.Null(c3.Player.Car.Paint);
    }

    /// <summary>A linked group stays linked through a death and respawn, on the server and in the player's own client.</summary>
    [Fact]
    public void LinkedWeaponsStayLinkedThroughARespawn()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vcmanta1.vcf", 0);
        var rig = new Rig(host);
        var ca = rig.Add(a);
        var cb = rig.Add(b);
        rig.Run(2);
        rig.Tick(c => c == cb ? new CarInput { Commands = CarCommands.WeaponLink } : default);
        rig.Run(3);
        var arm = b.Player.Car.Armory!;
        Assert.True(arm.Link);
        Assert.NotEmpty(arm.Group);
        var (sel, group) = (arm.Selected, arm.Group.ToArray());
        Assert.True(cb.Car.Armory!.Link);
        Kill(host, b.Player.Car, a.Player.Car);
        rig.Run(8);
        rig.Tick(c => c == cb ? new CarInput { Commands = CarCommands.RaiseDead } : default);
        rig.Run(3);
        Assert.False(b.Player.Destroyed);
        var back = b.Player.Car.Armory!;
        Assert.True(back.Link);
        Assert.Equal(sel, back.Selected);
        Assert.Equal(group, back.Group.ToArray());
        Assert.True(cb.Car.Armory!.Link);
        Assert.Equal(group, cb.Car.Armory!.Group.ToArray());
    }

    /// <summary>A bot's car takes the paint the host form's callback gives.</summary>
    [Fact]
    public void ABotWearsThePaintOfItsCar()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, BotCars = ["vppirna1.vcf"], PaintOfCar = c => c == "vppirna1.vcf" ? Red : null });
        host.Join("a", "vcgcour1.vcf", 0);
        Assert.Equal(Red, host.Mode.Bots.Single().Player.Car.Paint);
    }

    /// <summary>The new messages and fields round trip.</summary>
    [Fact]
    public void TheNewMessagesRoundTrip()
    {
        var w = new NetWriter();
        new Reconfigure("vcgcour9.vcf", [1, 2, 3]).Write(w);
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.Reconfigure, (MsgType)r.U8());
        var q = Reconfigure.Read(ref r);
        Assert.Equal("vcgcour9.vcf", q.Car);
        Assert.Equal([1, 2, 3], q.Config);
        Assert.Equal([], q.Paint);       // written empty, read as a blob
        w = new NetWriter();
        new Reconfigure("vcgcour9.vcf", [], Red.ToBytes()).Write(w);
        r = new NetReader(w.Span);
        r.U8();
        Assert.Equal(Red.ToBytes(), Reconfigure.Read(ref r).Paint);
        // an older-shaped one (no paint) ends cleanly
        w = new NetWriter();
        w.U8((byte)MsgType.Reconfigure); w.Str("a.vcf"); w.BlobU16([]);
        r = new NetReader(w.Span);
        r.U8();
        Assert.Null(Reconfigure.Read(ref r).Paint);
        w = new NetWriter();
        new ReconfigureAnswer(false, "nope").Write(w);
        r = new NetReader(w.Span);
        Assert.Equal(MsgType.ReconfigureAnswer, (MsgType)r.U8());
        Assert.Equal(new ReconfigureAnswer(false, "nope"), ReconfigureAnswer.Read(ref r));
        w = new NetWriter();
        new EventBatch(7, [new CarChangedEvent(new CarInfo(5, "n1.vcf", 2, 3, "b", [9]), 4), new EndEvent(true, -1, 0, 12345)]).Write(w);
        r = new NetReader(w.Span);
        Assert.Equal(MsgType.Events, (MsgType)r.U8());
        var batch = EventBatch.Read(ref r);
        var cc = Assert.IsType<CarChangedEvent>(batch.Events[0]);
        Assert.Equal((5, "n1.vcf", (byte)4), (cc.Car.Serial, cc.Car.Def, cc.Epoch));
        Assert.Equal(new EndEvent(true, -1, 0, 12345), batch.Events[1]);
        var own = new OwnCar { Epoch = 9 };
        w = new NetWriter();
        own.Write(w);
        r = new NetReader(w.Span);
        Assert.Equal(9, OwnCar.Read(ref r).Epoch);
    }
}
