using System.Diagnostics;
using System.Numerics;
using I76.Net;
using I76.Server;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Net.Tests;

/// <summary>A headless client: connects, drives by a policy every tick, and keeps what it was told.</summary>
internal sealed class Bot : IDisposable
{
    public MeleeClient Client { get; }
    public string Name { get; }
    public Func<Bot, int, CarInput> Policy { get; init; } = (_, _) => default;
    /// <summary>A client's mirror of the arena, written from what comes (as the game's), or null to only count.</summary>
    public ClientWorld? Mirror { get; private set; }
    public List<NetEvent> News { get; } = [];
    public int Ticks;
    public float TopSpeedSeen;

    public Bot(string name)
    {
        Name = name;
        Client = new MeleeClient();
    }

    public int CarSerial => Client.Accept?.CarSerial ?? 0;
    public Snapshot? Latest => Client.Snapshots.Count > 0 ? Client.Snapshots.Values[^1] : null;
    public CarState? Me => Latest?.Cars.FirstOrDefault(c => c.Serial == CarSerial);

    public void UseMirror(ClientWorld m)
    {
        Mirror = m;
        m.News += News.Add;
    }

    /// <summary>The mirror is let go (a new round: the next one is built from its accept).</summary>
    public void DropMirror() => Mirror = null;

    /// <summary>A frame: the network's news, and the mirror (if any) moved on.</summary>
    public void Frame(double dt, bool poll = true)
    {
        if (poll) Client.Poll();
        if (Mirror is not null) Mirror.Advance(dt, Client);
        else
            while (Client.Events.Count > 0) News.AddRange(Client.Events.Dequeue().Events);
        foreach (var c in Latest?.Cars ?? []) TopSpeedSeen = MathF.Max(TopSpeedSeen, c.Speed);
    }

    /// <summary>Dead, the bot presses RAISE_DEAD once the 5 s are up (and each second after) as a player would: there is no automatic respawn.</summary>
    public bool RaiseWhenDead { get; init; } = true;
    private int _deadTicks;

    public void Tick()
    {
        if (Client.State != ClientState.Joined) return;
        Client.SendInput(NextInput());
    }

    /// <summary>The policy's input for the next tick, with RAISE_DEAD pressed when dead (<see cref="RaiseWhenDead"/>).</summary>
    public CarInput NextInput()
    {
        var input = Policy(this, Ticks++);
        if (RaiseWhenDead && Me is { } me && Client.Accept is { } acc && ((me.Flags & VehicleFlags.Dead) != 0 || me.Wreck))
        {
            int dead = _deadTicks++, wait = acc.Rate.TicksFor(5.5f), every = acc.Rate.TicksFor(1f);
            if (dead >= wait && (dead - wait) % every == 0) input.Commands |= CarCommands.RaiseDead;
        }
        else _deadTicks = 0;
        return input;
    }

    public void Dispose() => Client.Dispose();

    /// <summary>
    /// An AI-derived driver: from the latest snapshot, steer at the nearest
    /// live car, throttle up, fire when it is ahead and near, and pick it as
    /// the radar target now and then.
    /// </summary>
    public static CarInput Hunter(Bot b, int t)
    {
        if (b.Me is not { } me || b.Latest is not { } snap) return default;
        var pos = me.Pos.ToVector3();
        CarState? best = null; float bd = float.MaxValue;
        foreach (var c in snap.Cars)
        {
            if (c.Serial == me.Serial || (c.Flags & VehicleFlags.Dead) != 0 || c.Wreck) continue;
            float d = Vector3.DistanceSquared(c.Pos.ToVector3(), pos);
            if (d < bd) { bd = d; best = c; }
        }
        var i = new CarInput { Buttons = CarButtons.Analog, Throttle = 26000 };
        if (best is null) return i;
        var local = me.Rot.ToLocal(best.Pos.ToVector3() - pos);
        float angle = MathF.Atan2(local.X, local.Z);
        // +1 steers left (the engine's sign); x is to the right
        i.Steer = (short)(Math.Clamp(-angle / 0.6f, -1f, 1f) * 32000);
        if (MathF.Abs(angle) < 0.35f && bd < 200 * 200) i.Buttons |= CarButtons.Fire;
        // stuck against something: back off a moment
        if (t % 300 > 270) { i.Throttle = -20000; i.Steer = (short)-i.Steer; }
        if (t % 90 == 0) i.Commands |= CarCommands.TargetNearest;
        return i;
    }

    /// <summary>A client that sends what it likes: every bit, the extreme analog values, the commands every tick.</summary>
    public static CarInput Nonsense(Bot b, int t) => new()
    {
        Buttons = (CarButtons)0xffffffff, Commands = (CarCommands)0xffff, Steer = short.MinValue, Throttle = short.MaxValue,
    };
}

/// <summary>
/// Bots against a server on localhost through a <see cref="LagProxy"/>
/// (LiteNetLib's own simulation is compiled out of its NuGet build): 100 ms
/// each way ± 10 ms and 2% of datagrams lost each way, for a minute of real
/// time (I76_NET_TEST_SECONDS to change it).
/// </summary>
public class BotTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void ThreeBotsPlayAMinuteOfMeleeOverALossyLink()
    {
        if (HostTests.InstallDir is not { } install) return;
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("I76_NET_TEST_SECONDS"), out var s) ? s : 60;
        const int latency = 100;
        const float loss = 2;
        var log = new List<string>();
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Ai = 2, Seed = 3 });
        server.Log += m => { lock (log) log.Add(m); };
        server.Start();
        using var proxy = new LagProxy(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port), latency, loss, jitterMs: 10);
        var mirror = HostTests.Mirror("m01");
        using var hunter = new Bot("hunter") { Policy = Bot.Hunter };
        using var prey = new Bot("prey") { Policy = Bot.Hunter };
        using var cheat = new Bot("cheat") { Policy = Bot.Nonsense };
        Bot[] bots = [hunter, prey, cheat];
        hunter.Client.Connect("127.0.0.1", proxy.Port, "hunter", "vppirna1.vcf");
        prey.Client.Connect("127.0.0.1", proxy.Port, "prey", "vcgcour1.vcf");
        cheat.Client.Connect("127.0.0.1", proxy.Port, "cheat", "../../../evil.vcf");

        var clock = Stopwatch.StartNew();
        double nextTick = 0, last = 0;
        bool killed = false, left = false;
        int preySerial = 0, hunterSerial = 0;
        uint deadGen = 0;
        bool sawPreyDead = false, sawPreyBack = false;
        var rng = new Random(9);
        var rtts = new List<int>();
        double nextRtt = 1;
        var acks = new List<long>();
        // events are judged on the server's simulation clock (the newest tick the hunter has heard), not the wall's:
        // a stalled scheduler slows the game, it must not make the test's deadlines run out. The wall only caps a hang.
        double Sim() => hunter.Client.NewestTick * SimRate.Default.TickSeconds;
        while (Sim() < seconds + 3 && clock.Elapsed.TotalSeconds < (seconds + 3) * 6)
        {
            double now = clock.Elapsed.TotalSeconds;
            foreach (var b in bots) b.Frame(now - last);
            last = now;
            if (hunter.Mirror is null && hunter.Client.Accept is { } acc) hunter.UseMirror(new ClientWorld(mirror, acc));
            while (now >= nextTick)
            {
                foreach (var b in bots) b.Tick();
                nextTick += SimRate.Default.TickSeconds;
                // the cheat also sends garbage now and then: cut-short and made-up messages
                if (cheat.Client.State == ClientState.Joined && cheat.Ticks % 40 == 0)
                {
                    var junk = new byte[rng.Next(1, 40)];
                    rng.NextBytes(junk);
                    junk[0] = (byte)rng.Next(1, 7);   // not 7, Leave: that would only end its own game
                    cheat.Client.SendRaw(junk);
                }
            }
            // a third of the way in, the hunter's guns finish the prey (on the server, as a hit of its own would)
            if (!killed && Sim() > seconds / 3 && prey.CarSerial != 0 && hunter.CarSerial != 0)
            {
                (preySerial, hunterSerial) = (prey.CarSerial, hunter.CarSerial);
                server.Invoke(h =>
                {
                    var victim = h.Seats.First(x => x.Name == "prey").Player.Car;
                    var attacker = h.Seats.First(x => x.Name == "hunter").Player.Car;
                    // both alive (the AI fill may have got one first): else try again next frame
                    if (I76.Sim.Damage.DamageModel.IsDead(victim) || I76.Sim.Damage.DamageModel.IsDead(attacker)) return;
                    killed = true;
                    sawPreyDead = false;
                    deadGen = (uint)victim.NetGeneration;
                    for (int k = 0; k < 200 && !I76.Sim.Damage.DamageModel.IsDead(victim); k++)
                    {
                        var p = new DamagePacket { Attacker = attacker, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
                        p.Ch[1] = 5000;
                        h.Setup.Damage.Apply(victim, p, new Vector3(0, 0.5f, 2));
                    }
                });
            }
            if (killed && hunter.Latest?.Cars.FirstOrDefault(c => c.Serial == preySerial) is { } ps)
            {
                if ((ps.Flags & VehicleFlags.Dead) != 0 && ps.Generation == (byte)deadGen) sawPreyDead = true;
                else if (sawPreyDead && ps.Generation != (byte)deadGen && (ps.Flags & VehicleFlags.Dead) == 0) sawPreyBack = true;
            }
            // sampled, judged by the median: one stalled scheduler slice on a loaded machine spikes the smoothed value
            if (now >= nextRtt) { nextRtt += 0.5; if (hunter.Client.Rtt > 0) rtts.Add(hunter.Client.Rtt); acks.Add((long)hunter.Client.InputTick - hunter.Client.AckedInput); }
            if (!left && Sim() > seconds * 0.85) { left = true; cheat.Client.Leave(); }
            Thread.Sleep(3);
        }
        lock (log) foreach (var l in log) output.WriteLine(l);

        Assert.Null(server.Fault);
        Assert.True(server.Running);
        // by the server's ticks, not the wall's: what a loaded server did not tick, it did not send
        double expected = hunter.Client.NewestTick;
        foreach (var b in bots)
        {
            output.WriteLine($"{b.Name}: {b.Client.State}, {b.Client.SnapshotsReceived} snapshots (avg {b.Client.SnapshotBytes / Math.Max(1, b.Client.SnapshotsReceived)} B, last {b.Client.LastSnapshotSize} B), " +
                $"events {b.Client.EventBytes} B, inputs {b.Client.InputsSent} sent ({b.Client.InputBytes} B), rtt {b.Client.Rtt} ms, acked {b.Client.AckedInput}/{b.Client.InputTick}, " +
                $"received {b.Client.BytesReceived / seconds / 1024:0.0} KiB/s, sent {b.Client.BytesSent / seconds / 1024:0.0} KiB/s, malformed {b.Client.Malformed}");
            Assert.Equal(ClientState.Joined, b == cheat ? ClientState.Joined : b.Client.State);
            Assert.Equal(0, b.Client.Malformed);
        }
        output.WriteLine($"proxy: {proxy.Forwarded} forwarded, {proxy.Dropped} dropped ({100.0 * proxy.Dropped / Math.Max(1, proxy.Forwarded + proxy.Dropped):0.0}%)");
        output.WriteLine($"server: {server.Snapshots} snapshots, last {server.LastSnapshotSize} B, {server.SnapshotBytes / 1024} KiB of snapshots, {server.EventBytes / 1024} KiB of events, tick {server.TickMs:0.00} ms");
        output.WriteLine($"mirror: {hunter.Mirror?.Cars.Count} cars, starved {hunter.Mirror?.Starved} frames, view {hunter.Mirror?.RenderTick:0.0} vs newest {hunter.Client.NewestTick}");
        // snapshots keep coming (2% lost, the rest in)
        Assert.True(hunter.Client.SnapshotsReceived > expected * 0.85, $"{hunter.Client.SnapshotsReceived} of {expected}");
        Assert.True(prey.Client.SnapshotsReceived > expected * 0.85, $"{prey.Client.SnapshotsReceived} of {expected}");
        // the inputs are applied: the server's ack follows the client's tick, about a round trip behind
        acks.Sort();
        Assert.True(acks.Count > 0 && acks[acks.Count / 2] is >= 3 and <= 24, $"median ack gap {(acks.Count > 0 ? acks[acks.Count / 2] : 0)} of {acks.Count} samples");
        rtts.Sort();
        Assert.True(rtts.Count > 0 && rtts[rtts.Count / 2] is >= 150 and <= 400, $"median rtt {(rtts.Count > 0 ? rtts[rtts.Count / 2] : 0)} of {rtts.Count} samples");
        // the kill scored by mass for the hunter, everyone heard, and the prey came back
        var kill = hunter.News.OfType<KillEvent>().FirstOrDefault(k => k.Victim == preySerial && k.Attacker == hunterSerial);
        Assert.True(killed, "the hunter and the prey were never both alive to stage the kill");
        Assert.True(kill is not null, $"no kill of {preySerial} by {hunterSerial}: {string.Join(", ", hunter.News.OfType<KillEvent>())}");
        Assert.Equal(hunterSerial, kill!.Attacker);
        Assert.True(kill.Points > 0);
        Assert.Contains(prey.News.OfType<ScoreEvent>(), e => e.Slot == hunter.Client.Accept!.Slot && e.Score >= kill.Points && e.Kills >= 1);
        Assert.Contains(prey.News.OfType<RenewedEvent>(), e => e.Car == preySerial && e.Respawn);
        Assert.True(sawPreyDead && sawPreyBack, $"dead {sawPreyDead} back {sawPreyBack}");
        // (not "alive": the AI fill may have caught the prey again by the end; the respawn is checked above)
        Assert.Contains(prey.Client.Scores!.Players, p => p.Slot == prey.Client.Accept!.Slot && p.Deaths >= 1);
        // the nonsense was corrected, never obeyed: the cheat's car is no faster than a car can be
        Assert.Contains(log, l => l.Contains("leave: cheat") && !l.Contains(" 0 inputs corrected"));
        Assert.Contains(log, l => l.Contains("cheat asked for ../../../evil.vcf"));
        Assert.True(hunter.TopSpeedSeen < 80, $"{hunter.TopSpeedSeen}");
        Assert.Contains(hunter.News.OfType<LeftEvent>(), e => e.Slot == cheat.Client.Accept!.Slot);
        Assert.DoesNotContain(hunter.Latest!.Cars, c => c.Serial == cheat.CarSerial);
        Assert.True(hunter.Mirror!.Starved < seconds * 60 * 0.1, $"{hunter.Mirror.Starved} starved frames");
    }

    /// <summary>
    /// Damage reaches every client: one bot's guns (staged on the server, as
    /// its hits would land) take the other's front armour and some of its
    /// parts. The victim's damage as the attacker's client sees it (the target
    /// box's bar, the zone pools, engine, suspension and brake) and as the
    /// victim's own client sees it (every part exactly, for the damage panel)
    /// matches the server's, within the wire's quantisation. Through the lag
    /// proxy, 50 ms each way and 2% lost.
    /// </summary>
    [Fact]
    public void DamageShowsOnTheAttackersAndTheVictimsClients()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Ai = 0, Seed = 5 });
        server.Start();
        using var proxy = new LagProxy(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port), 50, 2, jitterMs: 5);
        using var attacker = new Bot("attacker") { Policy = (_, t) => new CarInput { Commands = t % 30 == 5 ? CarCommands.TargetNearest : 0 } };
        using var victim = new Bot("victim");
        Bot[] bots = [attacker, victim];
        attacker.Client.Connect("127.0.0.1", proxy.Port, "attacker", "vppirna1.vcf");
        victim.Client.Connect("127.0.0.1", proxy.Port, "victim", "vcgcour1.vcf");

        var clock = Stopwatch.StartNew();
        double nextTick = 0, last = 0, stagedAt = -1;
        int victimSerial = 0;
        string staged = "";
        // waits are on the server's ticks the clients have heard since the staging (a loaded machine stalls the wall, not the game);
        // the wall only caps a hang
        uint[] heardAtStage = [0, 0];
        bool Settled() => attacker.Client.NewestTick >= heardAtStage[0] + 150 && victim.Client.NewestTick >= heardAtStage[1] + 150
            && attacker.Mirror!.RenderTick >= heardAtStage[0] + 100 && victim.Mirror!.RenderTick >= heardAtStage[1] + 100;
        while (clock.Elapsed.TotalSeconds < 120 && (stagedAt < 0 || !Settled()))
        {
            double now = clock.Elapsed.TotalSeconds;
            foreach (var b in bots)
            {
                b.Frame(now - last);
                if (b.Mirror is null && b.Client.Accept is { } acc) b.UseMirror(new ClientWorld(HostTests.Mirror("m01"), acc));
            }
            last = now;
            while (now >= nextTick) { foreach (var b in bots) b.Tick(); nextTick += SimRate.Default.TickSeconds; }
            // both in and seeing each other a second: the attacker's rounds strip the victim's front, then reach its parts
            if (stagedAt < 0 && now > 1 && attacker.Mirror?.Cars.Count == 2 && victim.Mirror?.Cars.Count == 2 && attacker.Latest is not null && victim.Latest is not null)
            {
                server.Invoke(h =>
                {
                    var v = h.Seats.First(x => x.Name == "victim").Player.Car;
                    var a = h.Seats.First(x => x.Name == "attacker").Player.Car;
                    var d = v.Health!;
                    for (int k = 0; k < 400 && !I76.Sim.Damage.DamageModel.IsDead(v); k++)
                    {
                        if (d.Armour[0] <= 0 && (d.Engine!.Ratio < 0.9f || d.Suspension!.Ratio < 0.9f || d.Brake!.Ratio < 0.9f)) break;
                        var p = new DamagePacket { Attacker = a, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
                        p.Ch[1] = 60;
                        h.Setup.Damage.Apply(v, p, new Vector3(0, 0.5f, 2));
                    }
                    staged = $"armour [{string.Join(", ", d.Armour)}] chassis [{string.Join(", ", d.Chassis)}], engine {d.Engine!.Hp}/{d.Engine.Max}, " +
                        $"suspension {d.Suspension!.Hp}/{d.Suspension.Max}, brake {d.Brake!.Hp}/{d.Brake.Max}, bar {d.BarRatio():0.000}";
                });
                victimSerial = victim.CarSerial;
                heardAtStage = [attacker.Client.NewestTick, victim.Client.NewestTick];
                stagedAt = now;
            }
            Thread.Sleep(3);
        }
        output.WriteLine($"staged at {stagedAt:0.0} s: {staged}");
        Assert.True(stagedAt > 0, "the two clients never both had the arena");

        // the server's victim against each client's copy of it
        server.Invoke(h =>
        {
            var real = h.Seats.First(x => x.Name == "victim").Player.Car;
            var rd = real.Health!;
            Assert.False(I76.Sim.Damage.DamageModel.IsDead(real), "the staged hits killed the victim");
            Assert.True(rd.BarRatio() < 0.9f, $"the staging did too little: {rd.BarRatio()}");
            foreach (var (who, b) in new[] { ("attacker", attacker), ("victim", victim) })
            {
                var seen = b.Mirror!.Car(victimSerial)!;
                var sd = seen.Health!;
                output.WriteLine($"{who}'s client: armour [{string.Join(", ", sd.Armour)}] chassis [{string.Join(", ", sd.Chassis)}], engine {sd.Engine!.Hp}/{sd.Engine.Max}, " +
                    $"suspension {sd.Suspension!.Hp}/{sd.Suspension.Max}, brake {sd.Brake!.Hp}/{sd.Brake.Max}, bar {sd.BarRatio():0.000}");
                Assert.Equal(rd.Armour, sd.Armour);
                Assert.Equal(rd.Chassis, sd.Chassis);
                Assert.Equal(rd.ArmourMirror, sd.ArmourMirror);
                Assert.Equal(rd.ChassisMirror, sd.ChassisMirror);
                Assert.Equal(rd.ZoneRatio(0), sd.ZoneRatio(0));
                foreach (var (r, s) in new (I76.Sim.Damage.Part R, I76.Sim.Damage.Part S)[] { (rd.Engine!, sd.Engine), (rd.Suspension!, sd.Suspension!), (rd.Brake!, sd.Brake!) })
                {
                    Assert.Equal(r.Max, s.Max);
                    // the attacker's copy is a byte of Hp/Max; the victim's own car comes exactly
                    Assert.InRange(s.Hp, r.Hp - (b == victim ? 0 : r.Max / 255 + 1), r.Hp + (b == victim ? 0 : r.Max / 255 + 1));
                    Assert.Equal(r.Hp == 0, s.Hp == 0);
                    Assert.Equal(r.Hp >= r.Max, s.Hp >= s.Max);
                }
                Assert.InRange(sd.BarRatio(), rd.BarRatio() - 1 / 255f - 1e-4f, rd.BarRatio() + 1 / 255f + 1e-4f);
                Assert.True(sd.BarRatio() < 0.9f);
            }
            // the victim's own damage panel: every slot exactly
            var mine = victim.Mirror!.Own!.Vehicle.Health!;
            for (int i = 0; i < 20; i++) Assert.Equal(rd.Slots[i]?.Hp, mine.Slots[i]?.Hp);
            // and the attacker's own car untouched on its client
            var aSeen = attacker.Mirror!.Own!.Vehicle.Health!;
            Assert.Equal(1f, aSeen.BarRatio());
        });
    }
}
