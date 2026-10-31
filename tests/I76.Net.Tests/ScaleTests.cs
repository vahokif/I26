using System.Numerics;
using Xunit;

namespace I76.Net.Tests;

/// <summary>What keeps 64 players' traffic in bounds (docs/multiplayer.md, "Many players"): events by interest and by name number, the scoreboard as deltas.</summary>
public class ScaleTests
{
    [Fact]
    public void NamesAreSentOnceThenByNumber()
    {
        var sent = new NameTable();
        var got = new NameTable();
        NetEvent[] batch =
        [
            new ImpactEvent("xbult1.ldp", "vimpb1.wav", new Vector3(1, 2, 3), 1, 1, new Vector3(1, 2, 4), Vector3.UnitY, Vector3.UnitZ),
            new ExplosionEvent("xsmlx1.ldp", new Vector3(5, 6, 7)),
            new SoundEvent(3, "vtcoll.wav"),
            new ClickEvent(4, "vimpb1.wav"),
        ];
        var sizes = new List<int>();
        for (int round = 0; round < 2; round++)
        {
            var w = new NetWriter { Names = sent };
            new EventBatch(10, batch).Write(w);
            sizes.Add(w.Length);
            var r = new NetReader(w.Span) { Names = got };
            r.U8();
            var back = EventBatch.Read(ref r);
            Assert.Equal(batch, back.Events);
        }
        // four names (one used twice) went once; the second batch names them by number
        Assert.Equal(4, got.Count);
        Assert.True(sizes[1] < sizes[0] - 30, $"{sizes[0]} then {sizes[1]} bytes");
        // a reader that never saw a name cannot make one up
        var w2 = new NetWriter { Names = sent };
        new EventBatch(11, batch).Write(w2);
        var r2 = new NetReader(w2.Span) { Names = new NameTable() };
        r2.U8();
        try { EventBatch.Read(ref r2); Assert.Fail("an unknown name was read"); } catch (WireException) { }
    }

    [Fact]
    public void FarEventsThatAreOnlySeenAndHeardAreNotSent()
    {
        var me = Vector3.Zero;
        Vector3? Car(int serial) => serial switch { 1 => me, 2 => new Vector3(300, 0, 0), 3 => new Vector3(1000, 0, 0), _ => null };
        bool For(NetEvent e) => EventInterest.Relevant(e, me, 1, Car);
        var far = new Vector3(1000, 0, 0);
        Assert.True(For(new ShotEvent(2, 0, new Vector3(300, 0, 0), Vector3.UnitZ)));
        Assert.False(For(new ShotEvent(3, 0, far, Vector3.UnitZ)));
        Assert.True(For(new ShotEvent(1, 0, far, Vector3.UnitZ)), "the own car's are always sent");
        Assert.False(For(new ImpactEvent("x", "y", far, 0, 1, far, Vector3.UnitY, Vector3.UnitZ)));
        Assert.True(For(new ExplosionEvent("x", far)), "explosions carry further");
        Assert.False(For(new ExplosionEvent("x", new Vector3(2500, 0, 0))));
        Assert.True(For(new SoundEvent(2, "vtcoll.wav")));
        Assert.False(For(new SoundEvent(3, "vtcoll.wav")));
        Assert.False(For(new MisfireEvent(2, 1)), "a misfire is its driver's alone");
        Assert.True(For(new MisfireEvent(1, 1)));
        // what changes the game goes to everyone, wherever
        Assert.True(For(new KilledEvent(3, false)));
        Assert.True(For(new ZoneEvent(3, 1, 2)));
        Assert.True(For(new SpawnEvent(new CarInfo(3, "vppirna1.vcf", -1, 0, "x"))));
        Assert.True(For(new ChatEvent(0, "hi")));
    }

    [Fact]
    public void TheScoreboardGoesAsChanges()
    {
        var rows = Enumerable.Range(0, 64).Select(i => new PlayerRow((byte)i, $"player{i}", 0, i * 100, i, 0, 50, i + 1, true)).ToList();
        var sent = new Dictionary<byte, PlayerRow>();
        var board = new Dictionary<byte, PlayerRow>();
        Scoreboard Through(Scoreboard s, out int bytes)
        {
            var w = new NetWriter();
            s.Write(w, sent);
            bytes = w.Length;
            var r = new NetReader(w.Span);
            r.U8();
            return Scoreboard.Read(ref r, board);
        }
        var first = Through(new Scoreboard(20, 1, rows), out int full);
        Assert.Equal(rows, first.Players);
        // a score, a death, a ping that moved a little (not sent) and one that moved a lot, one player gone
        rows[3] = rows[3] with { Score = 999 };
        rows[4] = rows[4] with { Deaths = 1, Alive = false };
        rows[5] = rows[5] with { Ping = 55 };
        rows[6] = rows[6] with { Ping = 120 };
        rows.RemoveAt(10);
        var second = Through(new Scoreboard(40, 2, rows), out int delta);
        Assert.True(full > 1000 && delta < 30, $"in full {full} B, as changes {delta} B");
        Assert.Equal(63, second.Players.Count);
        Assert.Equal(999, second.Players.First(p => p.Slot == 3).Score);
        Assert.False(second.Players.First(p => p.Slot == 4).Alive);
        Assert.Equal(50, second.Players.First(p => p.Slot == 5).Ping);
        Assert.Equal(120, second.Players.First(p => p.Slot == 6).Ping);
        Assert.DoesNotContain(second.Players, p => p.Slot == 10);
        // nothing changed: a few bytes
        Through(new Scoreboard(60, 3, rows), out int none);
        Assert.True(none <= 12, $"{none} B");
    }
}

/// <summary>The snapshot budget on a real arena (docs/multiplayer.md, "The snapshot budget"; skipped without an install).</summary>
public class BudgetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// 48 players driving and firing on m01, each client's snapshots written within a tight budget (700 bytes: these
    /// clients send no hashes, so the own motion goes whole, about 250 bytes of it) and decoded
    /// by its own decoder with acks four ticks late: every snapshot fits its budget, the client's frame is the server's,
    /// and no car goes unsent for longer than the bound (two seconds, and the tick it is written in; in practice a
    /// second and a few ticks); with the budget of a
    /// datagram the near ones still go every tick.
    /// </summary>
    [Theory]
    [InlineData(700, 20)]
    [InlineData(1400, 20)]
    [InlineData(700, 60)]
    public void EverySnapshotFitsItsBudgetAndEveryCarGoesInTime(int budget, int hz)
    {
        if (HostTests.InstallDir is not { } install) return;
        var rate = I76.Sim.Session.SimRate.FromLabel(hz);
        var host = new I76.Server.MeleeHost(new I76.Server.ServerOptions { Install = install, Arena = "m01", MaxPlayers = 48, Seed = 4, Rate = rate });
        var seats = Enumerable.Range(0, 48).Select(i => host.Join($"p{i}", "vppirna1.vcf", 0)).ToList();
        var spawns = host.Game.Spawns;
        for (int k = 0; k < seats.Count; k++)
        {
            var sp = spawns[k % spawns.Count];
            var p = sp.Pos + sp.Rot.Right * (12f * (k / spawns.Count));
            seats[k].Player.Car.Pos = new I76.Sim.Math.Double3(p.X, host.Setup.Ground.Query(p.X, p.Y + 50, p.Z).Height + 1.5, p.Z);
        }
        var decoders = seats.Select(_ => new SnapshotDecoder(rate)).ToList();
        var acks = seats.Select(_ => new Queue<(uint Due, uint Tick)>()).ToList();
        var lastSeen = seats.Select(_ => new Dictionary<int, uint>()).ToList();
        int maxAge = rate.TicksFor(SnapshotPlanner.MaxAgeSeconds), worst = 0, over = 0, near = 0, nearEvery = 0;
        var w = new NetWriter(4096);
        int ticks = rate.TicksFor(15f);
        for (uint t = 1; t <= ticks; t++)
        {
            foreach (var s in seats)
                s.Inputs.Add(new InputMessage(t, [new I76.Sim.Session.CarInput { Buttons = I76.Sim.Session.CarButtons.ThrottleUp | I76.Sim.Session.CarButtons.Fire | ((t / 30 + (uint)s.Slot) % 2 == 0 ? I76.Sim.Session.CarButtons.SteerLeft : 0) }]));
            host.Step();
            host.TakeEvents();
            var tk = host.CaptureTick();
            for (int i = 0; i < seats.Count; i++)
            {
                while (acks[i].Count > 0 && acks[i].Peek().Due <= t) host.AckSnapshot(seats[i], acks[i].Dequeue().Tick);
                w.Clear();
                host.WriteSnapshot(w, seats[i], tk, budget);
                if (w.Length > budget) over++;
                var r = new NetReader(w.Span);
                r.U8();
                var snap = decoders[i].Read(ref r);
                Assert.NotNull(snap);
                acks[i].Enqueue((t + 4, t));
                var me = seats[i].Player.Car.Pos.ToVector3();
                foreach (var c in snap!.Cars)
                {
                    if (c.SampleTick != t) continue;
                    if (lastSeen[i].TryGetValue(c.Serial, out var was))
                    {
                        worst = Math.Max(worst, (int)(t - was));
                        if (t > ticks / 2 && System.Numerics.Vector3.Distance(me, c.Pos.ToVector3()) < 50) { near++; if (t - was == 1) nearEvery++; }
                    }
                    lastSeen[i][c.Serial] = t;
                }
            }
            if (t == ticks / 2)
                // past the first second every car the server has has reached every client (the joiners' full snapshots included)
                for (int i = 0; i < seats.Count; i++)
                    foreach (var (serial, _) in tk.Cars) Assert.True(lastSeen[i].ContainsKey(serial), $"client {i} never got car {serial}");
        }
        output.WriteLine($"{budget} B at {hz} Hz: {over} snapshots over budget ({host.OverBudget} by their essential entries alone), the longest a car went unsent {worst} ticks (bound {2 * maxAge + 1}), near cars every tick {100.0 * nearEvery / Math.Max(1, near):0}%, {host.Deferred} entries put off");
        // only when its essential entries alone do not fit (a full snapshot's head, a burst of removals) may one go over: then in pieces
        Assert.Equal(host.OverBudget, over);
        Assert.True(over <= ticks * seats.Count / 1000, $"{over} over budget");
        Assert.True(worst <= 2 * maxAge + 1, $"a car went {worst} ticks unsent");
        if (budget >= 1400) Assert.True(nearEvery >= near * 0.9, $"{nearEvery} of {near} near updates a tick apart");
    }
}
