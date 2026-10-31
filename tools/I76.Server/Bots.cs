using System.Diagnostics;
using I76.Net;

namespace I76.ServerTool;

/// <summary>
/// Headless players for testing a server under load (i26-server --bots N): each connects as a client would, sends an
/// input every tick and reads the snapshots, without drawing anything. Half hunt the nearest car and fire at it, half
/// roam within 400 m of where they started and hunt now and then (the load test's two drivers, <see cref="BotDriver"/>). Optionally through a
/// <see cref="LagProxy"/> (--bots-lag ms[,loss%[,jitter ms]]). Testing only: they are ordinary players to the server.
/// </summary>
internal sealed class Bots : IDisposable
{
    private sealed class One
    {
        public required MeleeClient Client;
        public required string Name;
        public required BotDriver Driver;
        public double Next, ReadyAt;
        public int Said;
        public int Ticks;
    }

    private readonly List<One> _bots = [];
    private readonly LagProxy? _proxy;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly float _tick;
    private static readonly string[] Cars = ["vppirna1.vcf", "vcgcour1.vcf", "valepre1.vcf", "vfypony1.vcf", "vdstag1.vcf"];

    public Bots(int port, int n, float tickSeconds, string? lag, Action<string> log)
    {
        _tick = tickSeconds;
        if (lag is { Length: > 0 })
        {
            var p = lag.Split(',');
            int ms = int.Parse(p[0]), jitter = p.Length > 2 ? int.Parse(p[2]) : 0;
            float loss = p.Length > 1 ? float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            _proxy = new LagProxy(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), ms, loss, jitterMs: jitter, seed: 11);
            port = _proxy.Port;
            log($"bots: through a lag proxy, {ms} ms each way ±{jitter}, {loss}% lost");
        }
        for (int i = 0; i < n; i++)
        {
            var b = new One { Client = new MeleeClient(), Name = I76.Formats.Shell.MeleeDrivers.Bot(i), Driver = new BotDriver(roams: i % 2 == 1, seed: i + 1) };
            b.Client.Connect("127.0.0.1", port, b.Name, Cars[i % Cars.Length]);
            _bots.Add(b);
        }
        log($"bots: {n} headless players connecting");
        _thread = new Thread(Run) { Name = "bots", IsBackground = true };
        _thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        while (!_stop)
        {
            double now = NetClock.Seconds(clock);
            foreach (var b in _bots)
            {
                b.Client.Poll();
                // a co-op game's lobby: a bot is ready as soon as it is in
                if (b.Client.State == ClientState.InLobby && b.Client.Lobby?.Me is { Ready: false } && now - b.ReadyAt > 0.5) { b.ReadyAt = now; b.Client.SendLobbySet(true); }
                // and says so to each newcomer
                if (b.Client.State == ClientState.InLobby && b.Client.Lobby is { } lob && lob.Members.Count > b.Said && now - b.ReadyAt > 0.3) { b.Said = lob.Members.Count; b.Client.SendChat("Ready when you are."); }
                while (b.Client.Events.Count > 0) b.Client.Events.Dequeue();
                // what the cars are (a co-op bot tells friend from foe by it): the server is asked about new ones each second
                while (b.Client.CarAnswers.Count > 0) b.Driver.Learn(b.Client.CarAnswers.Dequeue());
                // a client keeps a couple of seconds of snapshots for its view; nothing reads them here
                while (b.Client.Snapshots.Count > 4) b.Client.Snapshots.RemoveAt(0);
                if (b.Next == 0) b.Next = now;
                while (now >= b.Next)
                {
                    b.Next += _tick / b.Client.ClockScale;
                    if (b.Client.State != ClientState.Joined) continue;
                    b.Client.SendInput(b.Driver.Next(b.Client));
                    if (++b.Ticks % 20 == 0 && b.Driver.Unknown(b.Client) is { Count: > 0 } unknown) b.Client.AskCars(unknown);
                }
            }
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        foreach (var b in _bots) b.Client.Dispose();
        _proxy?.Dispose();
    }
}
