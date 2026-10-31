using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using I76.Net;
using I76.Server;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The network melee under load (docs/multiplayer.md, "Bandwidth"): a server and N headless clients in this process,
/// for each player count and link, measuring what goes over the wire, how the snapshots arrive and how the server keeps
/// up. Long: it runs only with I76_NET_LOAD set (and an install).
///
/// <code>
/// I76_NET_LOAD=8,16,24,32      player counts
/// I76_NET_LOAD_LINKS=clean,50,75:30:5 links: one-way ms[:jitter ms[:loss %]] ("clean" connects directly)
/// I76_NET_LOAD_SECONDS=30      measured seconds per run (after the joins and 3 s to settle)
/// I76_NET_LOAD_AI=0         AI fill
/// I76_NET_LOAD_ARENA=m01
/// I76_NET_LOAD_PREDICTED=all     clients that also run a mirror and the own car's prediction (and so send the
///                  hashes the server confirms the motion by); a number for fewer
/// I76_NET_LOAD_UPLOAD=0       the host's upload limit, Mbit/s (0: one datagram a client a tick)
/// I76_NET_LOAD_OUT=file.md      the table is appended there too
/// I76_NET_LOAD_DELTA=0, I76_NET_LOAD_INTEREST=0  full snapshots, or every car every tick (A/B)
/// I76_NET_LOAD_RATE=20        the simulation rate (Hz)
/// </code>
/// </summary>
public class LoadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal sealed record Link(string Name, int LatencyMs, int JitterMs, float LossPercent)
    {
        public bool Clean => LatencyMs == 0 && JitterMs == 0 && LossPercent == 0;

        public static Link Parse(string s)
        {
            if (s == "clean") return new Link("clean", 0, 0, 0);
            var p = s.Split(':');
            int lat = int.Parse(p[0], CultureInfo.InvariantCulture), jit = p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 0;
            float loss = p.Length > 2 ? float.Parse(p[2], CultureInfo.InvariantCulture) : 0;
            return new Link($"{2 * lat} ms{(jit > 0 ? $" ±{jit}" : "")}{(loss > 0 ? $" {loss}%" : "")}", lat, jit, loss);
        }
    }

    /// <summary>One run's numbers.</summary>
    internal sealed class Result
    {
        public int Players, Ai;
        public string Link = "";
        public double Seconds;
        public double SnapshotAvg, SnapshotMax;
        public double DownKiBs, UpKiBs, HostUpMbit, HostUpKiBs;
        public double SplitShare, PiecesPerSnapshot, SnapshotLoss;
        public double TickAvg, TickP95, TickMax;
        public long Compared, Corrections, Inexact, Snaps;
        /// <summary>The same for the first four clients alone (what the runs with four predicting clients measured).</summary>
        public long Compared4, Corrections4;
        public double GapP50, GapP95, GapP99, GapMax, GapMean;
        /// <summary>The same for cars within 200 m of the client's (the gaps above are then the far ones).</summary>
        public double NearMean, NearP95, NearP99, NearMax;
        public double Starved;
        public double FullShare, MissShare, SendMs;
        public long HeadB, CarB, RoundB;
        public string Mode = "";
        /// <summary>Events and scoreboards per client (KiB/s), and the event kinds that cost most.</summary>
        public double EventKiBs, ScoreKiBs;
        public string TopEvents = "";
        /// <summary>Snapshots whose motion was confirmed by hash, entries put off a snapshot, snapshots over budget.</summary>
        public double ConfirmShare, DeferredPer, OverShare;

        public static string Header =>
            "| players | AI | link | mode | snapshot B (avg / max) | down KiB/s per client | up KiB/s per client | host up | in pieces | pieces per snapshot | snapshots lost | tick ms (avg / p95 / max) | prediction: corrections / compared (inexact, snaps) | near car gaps, ticks (mean / p95 / p99 / max) | far car gaps | view starved | full / baseline lost | B: head / cars / rounds | events / scores KiB/s per client | top events | motion confirmed | put off a snapshot | over budget |\n" +
            "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

        public string Row => string.Create(CultureInfo.InvariantCulture,
            $"| {Players} | {Ai} | {Link} | {Mode} | {SnapshotAvg:0} / {SnapshotMax:0} | {DownKiBs:0.0} | {UpKiBs:0.0} | {HostUpMbit:0.00} Mbit/s ({HostUpKiBs:0} KiB/s) | {100 * SplitShare:0.0}% | {PiecesPerSnapshot:0.00} | {100 * SnapshotLoss:0.0}% | " +
            $"{TickAvg:0.00} / {TickP95:0.00} / {TickMax:0.0} (snapshots {SendMs:0.00}) | {Corrections} / {Compared} ({Inexact}, {Snaps}); first 4: {Corrections4} / {Compared4} | {NearMean:0.00} / {NearP95:0} / {NearP99:0} / {NearMax:0} | {GapMean:0.00} / {GapP95:0} / {GapP99:0} / {GapMax:0} | {100 * Starved:0.0}% | {100 * FullShare:0.0}% / {100 * MissShare:0.00}% | {HeadB} / {CarB} / {RoundB} | {EventKiBs:0.0} / {ScoreKiBs:0.00} | {TopEvents} | {100 * ConfirmShare:0}% | {DeferredPer:0.0} | {100 * OverShare:0.0}% |");
    }

    [Fact]
    public void BandwidthAndCapacityUnderLoad()
    {
        if (HostTests.InstallDir is not { } install || Environment.GetEnvironmentVariable("I76_NET_LOAD") is not { Length: > 0 } spec) return;
        var counts = spec.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
        var links = (Environment.GetEnvironmentVariable("I76_NET_LOAD_LINKS") is { Length: > 0 } l ? l : "clean,50,75:30:5").Split(',').Select(Link.Parse).ToList();
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("I76_NET_LOAD_SECONDS"), CultureInfo.InvariantCulture, out var s) ? s : 30;
        int ai = int.TryParse(Environment.GetEnvironmentVariable("I76_NET_LOAD_AI"), out var a) ? a : 0;
        int predicted = int.TryParse(Environment.GetEnvironmentVariable("I76_NET_LOAD_PREDICTED"), out var pr) ? pr : int.MaxValue;
        string arena = Environment.GetEnvironmentVariable("I76_NET_LOAD_ARENA") is { Length: > 0 } ar ? ar : "m01";
        var outFile = Environment.GetEnvironmentVariable("I76_NET_LOAD_OUT");
        var rows = new List<string>();
        void Say(string line)
        {
            output.WriteLine(line);
            if (outFile is { Length: > 0 }) File.AppendAllText(outFile, line + "\n");
        }
        Say($"\n{arena}, {seconds:0} s measured, {Rate.Label} Hz, protocol {Protocol.Version}, {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        Say(Result.Header);
        foreach (var n in counts)
            foreach (var link in links)
            {
                var r = Run(install, arena, n, link, ai, seconds, Math.Min(predicted, n));
                Say(r.Row);
                Assert.True(r.SnapshotLoss < 0.5, $"{n} players over {link.Name}: {100 * r.SnapshotLoss:0}% of the snapshots lost");
            }
    }

    /// <summary>Drives towards a waypoint near where it started, a new one every 15 s or when there; hunts now and then.</summary>
    private static Func<Bot, int, CarInput> Roamer(int seed)
    {
        var rng = new Random(seed);
        Vector3? home = null, wp = null;
        int until = 0;
        return (b, t) =>
        {
            if (b.Me is not { } me) return default;
            var pos = me.Pos.ToVector3();
            home ??= pos;
            if (wp is null || t >= until || Vector3.DistanceSquared(pos, wp.Value) < 30 * 30)
            {
                wp = home.Value + new Vector3(rng.NextSingle() * 800 - 400, 0, rng.NextSingle() * 800 - 400);
                until = t + 300;
            }
            if (t % 600 > 450) return Bot.Hunter(b, t);
            var local = me.Rot.ToLocal(wp.Value - pos);
            float angle = MathF.Atan2(local.X, local.Z);
            var i = new CarInput { Buttons = CarButtons.Analog, Throttle = 24000, Steer = (short)(Math.Clamp(-angle / 0.6f, -1f, 1f) * 32000) };
            if (t % 300 > 270) { i.Throttle = -20000; i.Steer = (short)-i.Steer; }
            return i;
        };
    }

    private static double Upload => double.TryParse(Environment.GetEnvironmentVariable("I76_NET_LOAD_UPLOAD"), CultureInfo.InvariantCulture, out var u) ? u : 0;

    private static SimRate Rate => SimRate.FromLabel(int.TryParse(Environment.GetEnvironmentVariable("I76_NET_LOAD_RATE"), out var hz) ? hz : 20);

    private static readonly string[] Cars = ["vppirna1.vcf", "vcgcour1.vcf", "valepre1.vcf", "vpstang1.vcf", "vdbaron1.vcf"];

    private sealed class Measure
    {
        public required Bot Bot;
        public OwnCarPredictor? Predictor;
        public double NextTick;
        public uint LastSeen;
        public readonly Dictionary<int, uint> LastFresh = [];
        public long Snaps0, Bytes0, Down0, Up0, Miss0;
        public uint Tick0;
        public int Starved0, Frames, Frames0;
        public int Index;
        public double FrameDt;
        public readonly List<int> Gaps = [], NearGaps = [];
    }

    internal Result Run(string install, string arena, int players, Link link, int ai, double seconds, int predicted)
    {
        var tickTimes = new List<double>();
        bool measuring = false;
        bool delta = Environment.GetEnvironmentVariable("I76_NET_LOAD_DELTA") != "0", interest = Environment.GetEnvironmentVariable("I76_NET_LOAD_INTEREST") != "0";
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = arena, Port = 0, Ai = ai, Seed = 5, MaxPlayers = players, DeltaSnapshots = delta, InterestManagement = interest, Rate = Rate,
            UploadBytesPerSecond = (long)(Upload * 1e6 / 8) });
        server.Ticked += ms => { if (measuring) lock (tickTimes) tickTimes.Add(ms); };
        server.Start();
        using var proxy = link.Clean ? null
            : new LagProxy(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port), link.LatencyMs, link.LossPercent, jitterMs: link.JitterMs, seed: 7);
        int port = proxy?.Port ?? server.Port;
        var ms = new List<Measure>();
        for (int i = 0; i < players; i++)
        {
            var b = new Bot($"bot{i}") { Policy = i % 2 == 0 ? Bot.Hunter : Roamer(i) };
            b.Client.Connect("127.0.0.1", port, b.Name, Cars[i % Cars.Length]);
            ms.Add(new Measure { Bot = b, Index = i });
        }
        var gaps = new List<int>();
        var nearGaps = new List<int>();
        try
        {
            var clock = Stopwatch.StartNew();
            double last = 0, settleUntil = double.MaxValue, end = double.MaxValue;
            long hostUp0 = 0, snaps0 = 0, split0 = 0, pieces0 = 0, snapBytes0 = 0, full0 = 0, head0 = 0, car0 = 0, round0 = 0, ev0 = 0, sc0 = 0;
            long[] kinds0 = [];
            long conf0 = 0, def0 = 0, over0 = 0;
            double send0 = 0;
            bool spread = false;
            while (clock.Elapsed.TotalSeconds < end)
            {
                double now = clock.Elapsed.TotalSeconds, dt = now - last;
                last = now;
                // the clients each on their own (a predicting client runs a mirror of the arena and replays its car)
                bool measuringNow = measuring;
                Parallel.ForEach(ms, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, m =>
                {
                    var b = m.Bot;
                    b.Client.Poll();
                    Gaps(m, measuringNow ? m.Gaps : null, measuringNow ? m.NearGaps : null);
                    if (m.Predictor is null && m.Index < predicted && b.Client.Accept is { } acc)
                    {
                        MissionSetup world;
                        lock (ms) world = HostTests.Mirror(arena);
                        b.UseMirror(new ClientWorld(world, acc));
                        m.Predictor = new OwnCarPredictor(b.Mirror!, b.Client);
                    }
                    // a game draws at most 60 frames a second, not at this loop's rate
                    m.FrameDt += dt;
                    if (b.Mirror is { } mirror && m.FrameDt >= 1 / 60.0)
                    {
                        mirror.Advance(m.FrameDt, b.Client);
                        m.Predictor!.Reconcile();
                        m.Predictor.Smooth(m.FrameDt);
                        m.Frames++;
                        m.FrameDt = 0;
                    }
                    else while (b.Client.Events.Count > 0) b.Client.Events.Dequeue();
                    while (now >= m.NextTick)
                    {
                        m.NextTick += Rate.TickSeconds / b.Client.ClockScale;
                        if (b.Client.State != ClientState.Joined) continue;
                        var input = b.NextInput();
                        b.Client.SendInput(input);
                        m.Predictor?.Step(b.Client.InputTick, input);
                    }
                });
                if (!spread && ms.All(m => m.Bot.Me is not null))
                {
                    spread = true;
                    server.Invoke(Spread);
                    settleUntil = now + 3;
                }
                if (!spread && now > 60) throw new TimeoutException($"{ms.Count(m => m.Bot.Client.State == ClientState.Joined)} of {players} joined in 60 s");
                if (!measuring && now >= settleUntil)
                {
                    measuring = true;
                    end = now + seconds;
                    (hostUp0, snaps0, split0, pieces0, snapBytes0, full0) = server.Invoke(_ => (server.BytesSent, server.Snapshots, server.SplitSnapshots, server.SnapshotPieces, server.SnapshotBytes, server.FullSnapshots));
                    server.Invoke(_ => server.MaxSnapshotSize = 0);
                    (head0, car0, round0, send0) = server.Invoke(_ => (server.HeadBytes, server.CarBytes, server.RoundBytes, server.SnapshotSeconds));
                    (ev0, sc0, kinds0) = server.Invoke(_ => (server.EventBytes, server.ScoreBytes, (long[])server.EventBytesByKind.Clone()));
                    (conf0, def0, over0) = server.Invoke(h => (h.Seats.Sum(x => x.Confirmed), h.Deferred, h.OverBudget));
                    foreach (var m in ms)
                    {
                        var c = m.Bot.Client;
                        (m.Snaps0, m.Bytes0, m.Down0, m.Up0, m.Tick0, m.Miss0) = (c.SnapshotsReceived, c.SnapshotBytes, c.BytesReceived, c.BytesSent, c.NewestTick, c.BaselineMisses);
                        (m.Starved0, m.Frames0) = (m.Bot.Mirror?.Starved ?? 0, m.Frames);
                        if (m.Predictor is { } p) (p.Compared, p.Corrections, p.Inexact, p.Snaps) = (0, 0, 0, 0);
                    }
                }
                Thread.Sleep(2);
            }
            measuring = false;
            Assert.Null(server.Fault);
            var (hostUp, snaps, split, pieces, snapBytes, maxSnap, full) = server.Invoke(_ => (server.BytesSent, server.Snapshots, server.SplitSnapshots, server.SnapshotPieces, server.SnapshotBytes, server.MaxSnapshotSize, server.FullSnapshots));
            var r = new Result { Players = players, Ai = ai, Link = link.Name, Seconds = seconds, Mode = (!delta ? "full" : interest ? "budget" : "delta") + (Upload > 0 ? $", {Upload} Mbit/s up" : "") };
            r.SnapshotMax = maxSnap;
            var (head, carB, roundB, send) = server.Invoke(_ => (server.HeadBytes, server.CarBytes, server.RoundBytes, server.SnapshotSeconds));
            long sentN = Math.Max(1, snaps - snaps0);
            (r.HeadB, r.CarB, r.RoundB) = ((head - head0) / sentN, (carB - car0) / sentN, (roundB - round0) / sentN);
            r.SendMs = (send - send0) * 1000 / Math.Max(1, seconds / Rate.TickSeconds);
            var (ev, sc, kinds) = server.Invoke(_ => (server.EventBytes, server.ScoreBytes, (long[])server.EventBytesByKind.Clone()));
            var (conf, def, over) = server.Invoke(h => (h.Seats.Sum(x => x.Confirmed), h.Deferred, h.OverBudget));
            (r.ConfirmShare, r.DeferredPer, r.OverShare) = ((conf - conf0) / (double)sentN, (def - def0) / (double)sentN, (over - over0) / (double)sentN);
            r.EventKiBs = (ev - ev0) / seconds / 1024 / players;
            r.ScoreKiBs = (sc - sc0) / seconds / 1024 / players;
            r.TopEvents = string.Join(", ", Enumerable.Range(0, kinds.Length).Select(k => (k, b: kinds[k] - kinds0[k])).Where(x => x.b > 0).OrderByDescending(x => x.b).Take(4)
                .Select(x => string.Create(CultureInfo.InvariantCulture, $"{(EventKind)x.k} {x.b / seconds / 1024 / players:0.0}")));
            long sent = snaps - snaps0;
            r.SnapshotAvg = (snapBytes - snapBytes0) / (double)Math.Max(1, sent);
            r.SplitShare = (split - split0) / (double)Math.Max(1, sent);
            r.FullShare = (full - full0) / (double)Math.Max(1, sent);
            r.PiecesPerSnapshot = (sent - (split - split0) + (pieces - pieces0)) / (double)Math.Max(1, sent);
            r.HostUpKiBs = (hostUp - hostUp0) / seconds / 1024;
            r.HostUpMbit = (hostUp - hostUp0) * 8 / seconds / 1e6;
            double down = 0, up = 0, lost = 0, expected = 0, starved = 0, frames = 0;
            foreach (var m in ms)
            {
                var c = m.Bot.Client;
                down += (c.BytesReceived - m.Down0) / seconds / 1024;
                up += (c.BytesSent - m.Up0) / seconds / 1024;
                double exp = c.NewestTick - m.Tick0;
                expected += exp;
                lost += Math.Max(0, exp - (c.SnapshotsReceived - m.Snaps0));
                r.MissShare += c.BaselineMisses - m.Miss0;
                if (m.Bot.Mirror is { } mirror) { starved += mirror.Starved - m.Starved0; frames += m.Frames - m.Frames0; }
                if (m.Predictor is { } p)
                {
                    r.Compared += p.Compared; r.Corrections += p.Corrections; r.Inexact += p.Inexact; r.Snaps += p.Snaps;
                    if (m.Index < 4) { r.Compared4 += p.Compared; r.Corrections4 += p.Corrections; }
                }
            }
            r.DownKiBs = down / ms.Count;
            r.UpKiBs = up / ms.Count;
            r.SnapshotLoss = lost / Math.Max(1, expected);
            r.MissShare /= Math.Max(1, expected);
            r.Starved = starved / Math.Max(1, frames);
            lock (tickTimes)
            {
                tickTimes.Sort();
                if (tickTimes.Count > 0) (r.TickAvg, r.TickP95, r.TickMax) = (tickTimes.Average(), tickTimes[(int)(tickTimes.Count * 0.95)], tickTimes[^1]);
            }
            foreach (var m in ms) { gaps.AddRange(m.Gaps); nearGaps.AddRange(m.NearGaps); }
            gaps.Sort();
            if (gaps.Count > 0)
                (r.GapMean, r.GapP50, r.GapP95, r.GapP99, r.GapMax) = (gaps.Average(), gaps[gaps.Count / 2], gaps[(int)(gaps.Count * 0.95)], gaps[(int)(gaps.Count * 0.99)], gaps[^1]);
            nearGaps.Sort();
            if (nearGaps.Count > 0)
                (r.NearMean, r.NearP95, r.NearP99, r.NearMax) = (nearGaps.Average(), nearGaps[(int)(nearGaps.Count * 0.95)], nearGaps[(int)(nearGaps.Count * 0.99)], nearGaps[^1]);
            return r;
        }
        finally
        {
            foreach (var m in ms) m.Bot.Dispose();
        }
    }

    /// <summary>The ticks between the fresh states of each other car that reached this client (1: every tick), near (within 200 m of its car) and far.</summary>
    private static void Gaps(Measure m, List<int>? into, List<int>? near = null)
    {
        var snaps = m.Bot.Client.Snapshots;
        int i = snaps.Count;
        while (i > 0 && snaps.Keys[i - 1] > m.LastSeen) i--;
        for (; i < snaps.Count; i++)
        {
            var s = snaps.Values[i];
            var me = s.Cars.FirstOrDefault(c => c.Serial == m.Bot.CarSerial);
            foreach (var c in s.Cars)
            {
                if (c.SampleTick != s.Tick || c.Serial == m.Bot.CarSerial) continue;
                if (m.LastFresh.TryGetValue(c.Serial, out var was) && into is not null)
                {
                    bool close = me is not null && Vector3.DistanceSquared(me.Pos.ToVector3(), c.Pos.ToVector3()) < 200 * 200;
                    (close && near is not null ? near : into).Add((int)(s.Tick - was));
                }
                m.LastFresh[c.Serial] = s.Tick;
            }
            m.LastSeen = s.Tick;
        }
    }

    /// <summary>The players spread over the arena's spawn points, 30 m apart along a point's side when there are more players than points.</summary>
    private static void Spread(MeleeHost h)
    {
        var spawns = h.Game.Spawns;
        if (spawns.Count == 0) return;
        for (int k = 0; k < h.Seats.Count; k++)
        {
            var spot = spawns[k % spawns.Count];
            var car = h.Seats[k].Player.Car;
            int row = k / spawns.Count;
            var p = spot.Pos + spot.Rot.Right * (30f * ((row + 1) / 2) * (row % 2 == 0 ? 1 : -1));
            var (height, _) = h.Setup.Ground.Query(p.X, p.Y + 50, p.Z);
            car.Pos = new Double3(p.X, height + 1.5, p.Z);
            car.Rot = spot.Rot;
            car.Vel = Vector3.Zero;
            car.AngVel = Vector3.Zero;
        }
    }
}
