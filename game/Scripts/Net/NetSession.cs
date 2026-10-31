using Godot;
using I76.Net;
using I76.Server;

namespace I76.Game.Net;

/// <summary>
/// The game's end of a network melee (docs/multiplayer.md): the
/// <see cref="MeleeClient"/>, polled every frame even while the tree is
/// paused (the loading screen's fade, a picture), and for a listen game the
/// <see cref="NetMeleeServer"/> running in this process on its own thread.
/// Leaving the tree says goodbye and stops the server.
/// </summary>
public partial class NetSession : Node
{
    public MeleeClient Client { get; } = new();
    public NetMeleeServer? Server { get; private set; }
    /// <summary>This player's takeover should the host of a client-hosted game leave (docs/multiplayer.md "Host migration").</summary>
    public MigrationStandby? Standby { get; private set; }
    private bool _logging;
    private LagProxy? _lag;

    /// <summary>Taken out of the tree for the next round's scene: leaving it does not close the connection or the server.</summary>
    public bool Carrying { get; set; }

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    public override void _Process(double delta) => Client.Poll();

    /// <summary>Builds and starts the listen server (seconds: it loads the arena), off the main thread.</summary>
    public async System.Threading.Tasks.Task<NetMeleeServer> Host(ServerOptions o)
    {
        Server = await System.Threading.Tasks.Task.Run(() => new NetMeleeServer(o));
        Server.Log += m => GD.Print($"[server] {m}");
        Server.Start();
        GD.Print($"listen server: {o.Arena} on UDP {Server.Port}");
        if (o.Kind != I76.Sim.Missions.MeleeKind.Combat || o.Goal != I76.Sim.Missions.MeleeGoal.None) GD.Print($"listen server: {o.Kind}, goal {o.Goal} {o.GoalValue}, {o.CtfTeams} teams");
        return Server;
    }

    /// <summary>
    /// Joins host:port. I76_NET_LAG=ms[,loss%[,jitter ms]] goes through a local
    /// <see cref="LagProxy"/> first: that many ms each way (± the jitter, a tenth
    /// of it by default) and that share of datagrams lost, to see the game over a
    /// bad link on one machine.
    /// </summary>
    public void Join(string host, int port, string name, string car, int team, byte[]? config = null, string password = "", byte[]? paint = null)
    {
        if (System.Environment.GetEnvironmentVariable("I76_NET_LAG") is { Length: > 0 } lag)
        {
            var parts = lag.Split(',');
            int ms = int.Parse(parts[0]);
            float loss = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            int jitter = parts.Length > 2 ? int.Parse(parts[2]) : ms / 10;
            var addr = System.Net.Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            _lag = new LagProxy(new System.Net.IPEndPoint(addr, port), ms, loss, jitterMs: jitter);
            GD.Print($"network: through a lag proxy, {ms} ms ±{jitter} each way, {loss}% lost");
            (host, port) = ("127.0.0.1", _lag.Port);
        }
        Client.Connect(host, port, name, car, (byte)team, config, password, paint);
    }

    /// <summary>Joins a game listed on an anet server, through the server's introduction and the hole punch (MeleeClient.ConnectVia).</summary>
    public void JoinVia(string server, int serverPort, I76.Net.Anet.AnetSession game, string name, string car, int team, byte[]? config = null, string password = "",
        IReadOnlyList<(string Host, int Port)>? checkServers = null, byte[]? paint = null)
    {
        if (!_logging) { _logging = true; Client.Log += m => GD.Print($"network: {m}"); }
        Client.ConnectVia(server, serverPort, game, name, car, (byte)team, config, password: password, checkServers: checkServers, paint: paint);
    }

    /// <summary>
    /// This player can take over the game if its host leaves (a client-hosted game names a successor; a dedicated server
    /// never does, and nothing of this acts then). The takeover's server becomes <see cref="Server"/>, as a listen host's.
    /// </summary>
    public void EnableMigration(string install)
    {
        if (Standby is not null) return;
        if (!_logging) { _logging = true; Client.Log += m => GD.Print($"network: {m}"); }
        Standby = new MigrationStandby(install, m => GD.Print($"[server] {m}"));
        Client.CanHost = true;
        Client.Standby = Standby;
        Client.HostTakenOver += () =>
        {
            if (Standby.Server is not { } s) return;
            Server?.Dispose();
            Server = s;
            GD.Print($"network: this game took over as host, on UDP {s.Port}");
        };
    }

    public override void _ExitTree()
    {
        if (Carrying) return;
        Standby?.Dispose();
        I76.Formats.Vfs.GameInstall.SessionMapDir = null;
        Client.Dispose();
        _lag?.Dispose();
        Server?.Dispose();
        Server = null;
    }
}
