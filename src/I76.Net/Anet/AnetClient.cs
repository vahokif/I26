using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace I76.Net.Anet;

public enum AnetState { Resolving, Connecting, Online, Offline }

/// <summary>What a host publishes: the name, the seats, the players, whether it takes joins, and the 14 user bytes (<see cref="I26SessionTag"/>).</summary>
public sealed record AnetGame(string Name, int MaxPlayers, int Players, bool Open, byte[] User);

/// <summary>
/// A client of one anet game server (src/linux/server/server.c, run by the
/// community): the I26 subset of dp's game-server path (src/dp/dp2.c).
/// <list type="bullet">
/// <item>Connects with a dpio link and logs in with our species ("e1" and
/// the species): the server subscribes us to that species' session table,
/// creating it if need be, and takes our MYSESSIONS table as a publisher
/// (dp_addClient).</item>
/// <item>Keeps the species' table: <see cref="Sessions"/>.</item>
/// <item>Publishes our game as host (<see cref="Publish"/>): the raw record in
/// table [2], again whenever it changes; withdrawn with a delete, and dropped
/// by the server anyway when our link closes or times out.</item>
/// <item>Asks the server to introduce us to a session's host
/// (<see cref="IndirectJoin"/>); as a host, hears the server's REQUEST_OPEN
/// with the joiner's two addresses (<see cref="RequestOpen"/>).</item>
/// </list>
/// Everything else the server may send (the servers list, the apps table
/// with its patch downloads, account traffic) is ignored: an untrusted server
/// must never get I26 to download or run anything. A silent server is
/// retried with a growing pause; <see cref="State"/> and <see cref="Problem"/>
/// say where it stands. Call <see cref="Update"/> often, from one thread.
/// </summary>
public sealed class AnetClient : IDisposable
{
    private readonly IAnetTransport _t;
    private readonly Func<double> _clock;
    private readonly Dictionary<ulong, AnetSession> _sessions = [];
    private readonly byte[] _sessionsKey;
    private Task<IPAddress?>? _lookup;
    private IPEndPoint? _server;
    private ConcurrentQueue<byte[]>? _inbox;
    private DpioLink? _link;
    private DptabReader _tab = new();
    private double _retryAt, _backoff = 2;
    private AnetGame? _game;
    private byte[]? _publishedSubkey, _publishedRaw;
    private readonly ushort _karma;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }
    public ushort Species { get; }
    /// <summary>"host" or "host:port" as the player wrote it.</summary>
    public string Name => Port == AnetWire.ServerPort ? Host : $"{Host}:{Port}";
    public AnetState State { get; private set; } = AnetState.Resolving;
    /// <summary>Why the server is offline, a phrase after its name ("is not answering", "stopped answering", "was not found").</summary>
    public string? Problem { get; private set; }
    /// <summary>Our address as the server sees it (behind a NAT, the public one), once its SYN has come.</summary>
    public AnetAddress? PublicAddress { get; private set; }
    /// <summary>Our own address: the interface toward the server and the socket's port (tests may set it, standing behind an emulated NAT).</summary>
    public AnetAddress? PrivateOverride { get; init; }
    public AnetAddress PrivateAddress { get; private set; }
    /// <summary>Round trip to the server, ms (0 until measured).</summary>
    public int Rtt => (int)((_link?.Rtt ?? 0) * 1000);
    /// <summary>The species' sessions, by id; bumped <see cref="Version"/> on every change.</summary>
    public IReadOnlyDictionary<ulong, AnetSession> Sessions => _sessions;
    /// <summary>The most sessions kept from one server.</summary>
    public const int MaxSessions = 512;
    public int Version { get; private set; }
    /// <summary>True while our session record has been sent and the link is up.</summary>
    public bool Listed => _publishedRaw is not null && State == AnetState.Online;
    /// <summary>Try again after the server went silent (false: stay offline, a one-shot join).</summary>
    public bool Reconnect { get; init; } = true;
    public event Action<AnetAddress, AnetAddress>? RequestOpen;
    public event Action<string>? Log;

    public AnetClient(IAnetTransport transport, string host, int port, ushort species, Func<double>? clock = null, ushort karma = 0)
    {
        _karma = karma != 0 ? karma : (ushort)Random.Shared.Next(1, 65536);
        _t = transport;
        Host = host;
        Port = port <= 0 ? AnetWire.ServerPort : port;
        Species = species;
        _sessionsKey = AnetWire.SessionsKey(species);
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed.TotalSeconds);
        Resolve();
    }

    /// <summary>"host[:port]" into its parts (the anet port by default); false if it is no address.</summary>
    public static bool TryParse(string text, out string host, out int port)
    {
        bool ok = Discovery.TryParseAddress(text, out host, out port);
        if (ok && !HasPort(text)) port = AnetWire.ServerPort;
        return ok;
    }

    /// <summary>Whether "host:port" names a port ("host" alone does not).</summary>
    public static bool HasPort(string text)
    {
        text = text.Trim();
        if (text.StartsWith('[')) return text.Contains("]:");
        return text.Count(c => c == ':') == 1;
    }

    private void Resolve()
    {
        State = AnetState.Resolving;
        if (IPAddress.TryParse(Host, out var ip)) { _lookup = Task.FromResult<IPAddress?>(ip); return; }
        string h = Host;
        _lookup = Task.Run(async () =>
        {
            try { return (await Dns.GetHostAddressesAsync(h)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch (SocketException) { return null; }
            catch (ArgumentException) { return null; }
        });
    }

    /// <summary>Sets the game to list (null withdraws it). Sent when online, and again whenever it changes.</summary>
    public void Publish(AnetGame? game)
    {
        _game = game;
        SyncPublication();
    }

    /// <summary>Asks the server to have the session's host open toward us (INDIRECT_JOIN). False if we are not online.</summary>
    public bool IndirectJoin(AnetSession s)
    {
        if (State != AnetState.Online || _link is null) return false;
        _link.Send(AnetWire.IndirectJoinPacket(s.Subkey()), _clock());
        Log?.Invoke($"{Name}: asked to be introduced to \"{s.Name}\" at {s.Master}");
        return true;
    }

    public void Update()
    {
        if (_disposed) return;
        double now = _clock();
        if (State == AnetState.Resolving)
        {
            if (_lookup is not { IsCompleted: true } l) return;
            if (l.Result is not { } ip) { GoOffline("was not found", now); return; }
            _server = new IPEndPoint(ip, Port);
            Connect(now);
        }
        if (State == AnetState.Offline)
        {
            if (Reconnect && now >= _retryAt) { if (_server is null) Resolve(); else Connect(now); }
            return;
        }
        if (_link is null || _inbox is null) return;
        while (_inbox.TryDequeue(out var d)) _link.Receive(d, now);
        _link.Update(now);
        while (_link.Delivered.TryDequeue(out var p)) Handle(p);
        if (State == AnetState.Connecting && _link.Established)
        {
            State = AnetState.Online;
            Problem = null;
            _backoff = 2;
            PublicAddress = _link.SeenAs;
            Log?.Invoke($"{Name}: online; we are {PublicAddress?.ToString() ?? "?"} to it, {PrivateAddress} here");
            _link.Send(AnetWire.Login(Species), now);
            SyncPublication();
        }
        if (_link.Closed) GoOffline(_link.CloseReason ?? "closed the connection", now);
    }

    private void Connect(double now)
    {
        PrivateAddress = PrivateOverride ?? AnetAddress.Local(_server!.Address, _t.LocalPort);
        _inbox = _t.Listen(_server!);
        while (_inbox.TryDequeue(out _)) { }
        var server = _server!;
        _link = new DpioLink(d => _t.Send(server, d));
        _tab = new DptabReader();
        _publishedRaw = _publishedSubkey = null;
        State = AnetState.Connecting;
        _link.Open(now, AnetWire.SynBody(PrivateAddress));
    }

    private void GoOffline(string why, double now)
    {
        if (State != AnetState.Offline) Log?.Invoke($"{Name}: offline ({why})");
        State = AnetState.Offline;
        Problem = why;
        _link = null;
        _publishedRaw = _publishedSubkey = null;
        if (_sessions.Count > 0) { _sessions.Clear(); Version++; }
        _retryAt = now + _backoff;
        _backoff = Math.Min(_backoff * 2, 30);
    }

    private void Handle(byte[] p)
    {
        if (p.Length >= 2 && p[0] == AnetWire.Initial && p[1] == AnetWire.RequestOpen)
        {
            if (AnetWire.ReadRequestOpen(p) is { } ro)
            {
                Log?.Invoke($"{Name}: REQUEST_OPEN for {ro.Seen} / {ro.Reported}");
                RequestOpen?.Invoke(ro.Seen, ro.Reported);
            }
            return;
        }
        foreach (var c in _tab.Read(p))
        {
            // only our species' sessions: the servers table, the apps table (patch downloads) and the rest are never acted on
            if (!c.Key.AsSpan().SequenceEqual(_sessionsKey)) continue;
            ulong id = AnetSession.IdOf(c.Subkey);
            if (c.Value is null) { if (_sessions.Remove(id)) Version++; continue; }
            if (AnetSession.Unpack(c.Subkey, c.Value) is not { } s) continue;
            // a server is not trusted with unbounded memory
            if (!_sessions.ContainsKey(id) && _sessions.Count >= MaxSessions) continue;
            _sessions[id] = s;
            Version++;
        }
    }

    private void SyncPublication()
    {
        if (State != AnetState.Online || _link is null || PublicAddress is not { } pub) return;
        double now = _clock();
        if (_game is null)
        {
            if (_publishedSubkey is { } old)
                _link.Send(AnetWire.Small(4, [AnetWire.KeyMySessions], [(old, null)]), now);
            _publishedSubkey = _publishedRaw = null;
            return;
        }
        var s = new AnetSession(PrivateAddress, _karma, pub, _game.MaxPlayers, _game.Players, _game.Open ? AnetSession.FlagsOpen : 0x20,
            _game.Name, "", _game.User);
        var raw = s.PackRaw(Species, _server is null ? null : AnetAddress.From(_server));
        var sk = s.Subkey();
        if (_publishedRaw is not null && raw.AsSpan().SequenceEqual(_publishedRaw) && sk.AsSpan().SequenceEqual(_publishedSubkey)) return;
        if (_publishedSubkey is { } prev && !prev.AsSpan().SequenceEqual(sk))
            _link.Send(AnetWire.Small(4, [AnetWire.KeyMySessions], [(prev, null)]), now);
        // hops 4: client, server, master server, server, client (dp_setSessionDesc)
        _link.Send(AnetWire.Small(4, [AnetWire.KeyMySessions], [(sk, raw)]), now);
        _publishedRaw = raw;
        _publishedSubkey = sk;
        Log?.Invoke($"{Name}: published \"{_game.Name}\" {_game.Players}/{_game.MaxPlayers} at {pub} (here {PrivateAddress})");
    }

    /// <summary>Withdraws our session and closes the link.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        double now = _clock();
        if (_link is { Closed: false } l)
        {
            Publish(null);
            // give the delete a moment to leave before the FIN (the server drops our records when the link goes anyway)
            for (int i = 0; i < 3; i++) l.Update(now);
            l.Close(now);
        }
        if (_server is not null) _t.Unlisten(_server);
        _disposed = true;
    }
}
