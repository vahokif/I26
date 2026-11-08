using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using I76.Net.Anet;
using I76.Sim.Session;
using LiteNetLib;
using LiteNetLib.Layers;
using LiteNetLib.Utils;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("I76.Net.Tests")]

namespace I76.Net;

/// <summary>
/// Where a client is. <see cref="MapOffered"/>: connected, and the server's arena is a custom map (<see cref="MeleeClient.Offer"/>)
/// whose answer is the game's to give (<see cref="MeleeClient.ReadyMap"/>, <see cref="MeleeClient.DownloadMap"/>, <see cref="MeleeClient.Leave"/>);
/// <see cref="Downloading"/>: its files are coming. Then <see cref="Connecting"/> again until the accept.
/// </summary>
public enum ClientState { Idle, Connecting, Joined, Rejected, Disconnected, MapOffered, Downloading,
    /// <summary>The host left a game that hands over: the client seeks the successor and rejoins it (HostMigration.cs).</summary>
    Migrating,
    /// <summary>Connected to a co-op game that has not started: its lobby (<see cref="MeleeClient.Lobby"/>); the accept comes when the mission starts (protocol 20).</summary>
    InLobby }

/// <summary>
/// A network melee's client end, without Godot: connects with a join
/// request, sends the driver's input every tick (with the last few repeated,
/// <see cref="InputHistory"/>), and keeps what the server sends: the accept,
/// a short buffer of snapshots, the events in order, the scoreboard. The
/// game drives it from a node; the tests' bots drive it from a loop. Call
/// <see cref="Poll"/> often (each frame): LiteNetLib's thread queues what
/// arrives and Poll hands it over.
/// </summary>
public sealed partial class MeleeClient : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly AnetLayer _layer;
    private AnetJoiner? _joiner;
    private NetDataWriter? _joinData;
    private NetPeer? _peer;
    private InputHistory _inputs = new();
    private int _bufferTicks = Protocol.BufferTicks(SimRate.Default);
    private readonly NetWriter _w = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SnapshotAssembler _parts = new();
    private SnapshotDecoder _decoder = new(SimRate.Default);

    /// <summary>The snapshots decoded against a baseline, and those lost for naming a baseline this client had let go.</summary>
    public long DeltaSnapshots => _decoder.Deltas;
    public long BaselineMisses => _decoder.Misses;
    /// <summary>The snapshot tick decoded last: the ack each input message carries (protocol 13's delta snapshots).</summary>
    public uint SnapshotAck => _decoder.Last;

    public ClientState State { get; private set; } = ClientState.Idle;
    /// <summary>Why the join was refused or the connection ended.</summary>
    public string? Reason { get; private set; }
    public JoinAccept? Accept { get; private set; }
    public Scoreboard? Scores { get; private set; }

    /// <summary>The snapshots received, by server tick (the last 2.3 s of them).</summary>
    public SortedList<uint, Snapshot> Snapshots { get; } = [];
    /// <summary>The newest snapshot's tick and when it came (seconds on this client's clock).</summary>
    public uint NewestTick { get; private set; }
    public double NewestAt { get; private set; }
    public double Now => TestClock?.Invoke() ?? NetClock.Seconds(_clock);
    /// <summary>The tests' clock, in place of the stopwatch.</summary>
    internal Func<double>? TestClock;

    /// <summary>Event batches received and not yet taken, in the server's order.</summary>
    public Queue<EventBatch> Events { get; } = new();
    /// <summary>The server's answers about cars the mirror asked for (<see cref="AskCars"/>), not yet taken.</summary>
    public Queue<CarInfo> CarAnswers { get; } = new();

    /// <summary>Counters: snapshots, their bytes (and the last one's size), events, input messages sent.</summary>
    public long SnapshotsReceived, SnapshotBytes, EventBytes, InputsSent, InputBytes;
    public int LastSnapshotSize;
    public int Malformed;

    /// <summary>Round trip time to the server, ms: the transport's latest sample.</summary>
    public int Rtt => _peer?.RoundTripTime ?? 0;
    /// <summary>The ping the HUD shows, ms: the low of the recent samples (<see cref="PingWindow"/>).</summary>
    public int Ping => _ping.Low;
    private readonly PingWindow _ping = new();
    /// <summary>The client's own input tick, and the last one the server says it applied.</summary>
    public uint InputTick => _inputs.Tick;
    public uint AckedInput { get; private set; }

    /// <summary>
    /// How fast the input clock runs, against the tick (protocol 13): the host's tick loop sends an input every
    /// tick ÷ this. Each snapshot says how many of our inputs were left waiting on the server (its slack); the fewest
    /// over the last second is the margin a late packet has. The clock runs 2% fast a missing input while that is
    /// under one (a starved tick, when the server had to hold the last input, counts as −1), and 1% slow an input
    /// while it is over one, so the server neither runs dry nor merges inputs away (Protocol.CatchUpInputs), and the
    /// prediction's inputs land on the ticks it stepped them on. <see cref="TimeSync"/> off keeps it at 1.
    /// </summary>
    public double ClockScale { get; private set; } = 1;
    public bool TimeSync { get; set; } = true;
    private readonly Queue<sbyte> _slack = new();
    private int _slackWindow = SimRate.Default.TicksFor(1f);

    private void TakeSlack(sbyte slack)
    {
        _slack.Enqueue(slack);
        while (_slack.Count > _slackWindow) _slack.Dequeue();
        int min = int.MaxValue;
        foreach (var x in _slack) min = Math.Min(min, x);
        ClockScale = !TimeSync || _slack.Count < _slackWindow / 2 ? 1 : min < 1 ? 1 + Math.Min((1 - min) * 0.02, 0.04) : min > 1 ? 1 - Math.Min((min - 1) * 0.01, 0.03) : 1;
    }

    /// <summary>The server's custom arena, offered before the seat (protocol 11); null for a stock arena.</summary>
    public MapOffer? Offer { get; private set; }
    /// <summary>The map being downloaded, or done (its folder in <see cref="MapDownload.Done"/>).</summary>
    public MapDownload? Download { get; private set; }
    /// <summary>The downloaded map's folder this game plays from (null: the install's own map).</summary>
    public string? MapDir { get; private set; }
    public event Action<MapOffer>? MapOffered;

    /// <summary>How many times the server has started a new round on this connection (protocol 12), and the arena it named.</summary>
    public int Rounds { get; private set; }
    public string? NextArena { get; private set; }
    /// <summary>The server's next round begins (<see cref="Accept"/> is cleared: the offer or accept of the new round follows).</summary>
    public event Action<string>? NewRound;

    /// <summary>
    /// A co-op mission's movie being played now (protocol 19): from the event that began it (taken when it arrives, not with its
    /// tick: the game stands still for it), the join accept, or the new host's accept; null once the server ended it.
    /// <see cref="MovieSeq"/> counts the starts.
    /// </summary>
    public string? MovieName { get; private set; }
    public int MovieSeq { get; private set; }
    private string? _movieFinished;

    private void SetMovie(string? name)
    {
        if (name is not null && name != MovieName) MovieSeq++;
        MovieName = name;
        // a copy that was over already tells a server it comes back to (the new host's) that it is
        if (name is not null && name == _movieFinished) SendMovieDone(name);
    }

    /// <summary>This client's copy of the movie is over (played out, skipped, or not there): the server ends it for everyone when the host's is.</summary>
    public void SendMovieDone(string name)
    {
        _movieFinished = name;
        if (_peer is null || Accept is null) return;
        _w.Clear();
        _w.U8((byte)MsgType.MovieDone);
        _w.Str(name);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    public event Action<JoinAccept>? Joined;
    /// <summary>The game is invite only and the join is waiting for the host's answer (protocol 13, <see cref="MsgType.JoinWait"/>).</summary>
    public bool WaitingForHost { get; private set; }
    /// <summary>The server's answers to <see cref="SendReconfigure"/>, not yet taken.</summary>
    public Queue<ReconfigureAnswer> ReconfigureAnswers { get; } = new();
    public event Action<string>? Disconnected;

    /// <summary>
    /// A client; for a bad link to test against, connect through a <see cref="LagProxy"/>. The socket also carries a
    /// join through an anet server (<see cref="ConnectVia"/>); <paramref name="inner"/> is a packet layer under it (the tests' NAT).
    /// </summary>
    public MeleeClient(PacketLayerBase? inner = null)
    {
        _layer = new AnetLayer(inner);
        _net = new NetManager(_listener, _layer)
        {
            AutoRecycle = true,
            DisconnectTimeout = 15000,
            // the hole punch's datagrams (a join through an anet server)
            UnconnectedMessagesEnabled = true,
            EnableStatistics = true,
            // channel 1: a custom map's pieces (MapTransferLimits.Channel)
            ChannelsCount = 2,
            MtuDiscovery = true,
        };
        _listener.PeerConnectedEvent += p => _peer = p;
        _listener.NetworkLatencyUpdateEvent += (p, _) => { if (p == _peer) _ping.Add(p.RoundTripTime); };
        _listener.PeerDisconnectedEvent += (p, info) =>
        {
            // we left (Leave, Fail): the reason is ours
            if (State == ClientState.Disconnected && Reason is not null) { _peer = null; return; }
            // the host left a game that hands over (or a peer of a migration gone by): the successor is sought instead
            if (MigrationDisconnect(p, info)) return;
            if (State == ClientState.Connecting && info.Reason == DisconnectReason.ConnectionRejected)
            {
                State = ClientState.Rejected;
                Reason = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : "refused";
            }
            else
            {
                State = ClientState.Disconnected;
                Reason = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : info.Reason.ToString();
            }
            _peer = null;
            Disconnected?.Invoke(Reason);
        };
        _listener.NetworkReceiveUnconnectedEvent += (ep, reader, type) =>
        {
            var d = reader.GetRemainingBytesSpan();
            if (!MigrationProbe(ep, d)) _joiner?.HandleUnconnected(ep, d);
        };
        _listener.NetworkReceiveEvent += (p, reader, channel, method) =>
        {
            // a host left behind in a migration says nothing more that counts
            if (IgnoreFrom(p)) return;
            _lastHeard = Now;
            var data = reader.GetRemainingBytesSpan();
            try { Receive(data); }
            catch (WireException) { Malformed++; }
        };
    }

    /// <summary>Starts connecting to host:port with the join request (name, car, team).</summary>
    public void Connect(string host, int port, string name, string car, byte team = 0, byte[]? config = null, string password = "", byte[]? paint = null)
    {
        _net.Start();
        State = ClientState.Connecting;
        _request = new JoinRequest(Protocol.Version, name, car, team, config ?? [], password, paint);
        _net.Connect(host, port, JoinData(name, car, team, config, password, paint));
    }

    private static NetDataWriter JoinData(string name, string car, byte team, byte[]? config, string password = "", byte[]? paint = null)
    {
        var w = new NetDataWriter();
        var nw = new NetWriter();
        new JoinRequest(Protocol.Version, name, car, team, config ?? [], password, paint).Write(nw);
        w.Put(nw.ToArray());
        return w;
    }

    /// <summary>
    /// Joins a game listed on an anet server: asks that server, from this socket, to introduce us to the host, punches
    /// toward the host's public and own addresses, and connects to whichever answers (<see cref="AnetJoiner"/>). A
    /// failure ends in <see cref="ClientState.Disconnected"/> with the reason: whose router stopped it, by asking one of
    /// <paramref name="checkServers"/> (the address book's other servers) where it sees us, and the port the host can forward
    /// (<see cref="AnetJoiner.FailureText"/>). <see cref="Stage"/> says where it is.
    /// </summary>
    public void ConnectVia(string serverHost, int serverPort, AnetSession game, string name, string car, byte team = 0, byte[]? config = null,
        AnetAddress? privateOverride = null, string password = "", IReadOnlyList<(string Host, int Port)>? checkServers = null, byte[]? paint = null)
    {
        _net.Start();
        State = ClientState.Connecting;
        _joinData = JoinData(name, car, team, config, password, paint);
        _request = new JoinRequest(Protocol.Version, name, car, team, config ?? [], password, paint);
        _joiner = new AnetJoiner(new LiteNetAnetTransport(_net, _layer), serverHost, serverPort, game, (ep, d) => _net.SendUnconnectedMessage(d, ep),
            privateOverride: privateOverride) { CheckServers = checkServers ?? [] };
        _joiner.Log += m => Log?.Invoke(m);
    }

    /// <summary>What a join through a server is doing ("Joining via server...", "Connecting to the host..."), null otherwise.</summary>
    public string? Stage => _joiner?.Stage ?? (State == ClientState.Connecting && WaitingForHost ? "Waiting for the host to let you in..."
        : State == ClientState.Connecting && _joinData is not null ? "Connecting to the host..." : null);
    public event Action<string>? Log;

    public void Poll()
    {
        _net.PollEvents();
        PollMigration();
        if (_joiner is not { } j) return;
        j.Update();
        if (j.Reached is { } at)
        {
            Log?.Invoke($"connecting to the host at {at}");
            _net.Connect(at, _joinData!);
            j.Dispose();
            _joiner = null;
        }
        else if (j.Failed is { } why)
        {
            j.Dispose();
            _joiner = null;
            State = ClientState.Disconnected;
            Reason = why;
            Disconnected?.Invoke(why);
        }
    }

    /// <summary>A snapshot as if it had just come (the tests).</summary>
    internal void Inject(Snapshot s)
    {
        Snapshots[s.Tick] = s;
        NewestTick = s.Tick;
        NewestAt = Now;
    }

    /// <summary>The names the server's events have sent once (<see cref="NameTable"/>), from the join accept.</summary>
    private readonly NameTable _names = new();
    /// <summary>The scoreboard as the deltas have built it, by slot.</summary>
    private readonly Dictionary<byte, PlayerRow> _board = [];

    private void Receive(ReadOnlySpan<byte> data)
    {
        var r = new NetReader(data) { Names = _names };
        var type = (MsgType)r.U8();
        switch (type)
        {
            case MsgType.JoinAccept when State == ClientState.Migrating && Accept is not null:
                Rejoined(JoinAccept.Read(ref r));
                break;
            case MsgType.JoinWait when State == ClientState.Migrating && Accept is null:
                // an invited join carried over to the new host: waiting for its answer now
                State = ClientState.Connecting;
                WaitingForHost = true;
                MigrationJoinWait();
                break;
            case MsgType.JoinWait when State == ClientState.Connecting:
                WaitingForHost = true;
                break;
            case MsgType.LobbyState when State is ClientState.Connecting or ClientState.InLobby && Accept is null:
                WaitingForHost = false;
                Lobby = LobbyState.Read(ref r);
                State = ClientState.InLobby;
                break;
            case MsgType.LobbyChat when State == ClientState.InLobby:
                LobbyLines.Enqueue(LobbyChat.Read(ref r));
                break;
            case MsgType.ReconfigureAnswer when Accept is not null || State == ClientState.InLobby:
                ReconfigureAnswers.Enqueue(ReconfigureAnswer.Read(ref r));
                break;
            case MsgType.JoinAccept:
                WaitingForHost = false;
                Accept = JoinAccept.Read(ref r);
                CbMuted = false;
                Lobby = null;
                _names.Clear();
                _board.Clear();
                Array.Clear(_motionHashes);
                _newestHash = 0;
                NeedExact = false;
                _inputs = new InputHistory(Accept.Rate);
                _bufferTicks = Protocol.BufferTicks(Accept.Rate);
                _slackWindow = Accept.Rate.TicksFor(1f);
                _decoder = new SnapshotDecoder(Accept.Rate);
                _slack.Clear();
                ClockScale = 1;
                State = ClientState.Joined;
                ResetMigration();
                MovieName = null;
                SetMovie(Accept.Coop?.Movie);
                Joined?.Invoke(Accept);
                break;
            case MsgType.Snapshot when Accept is not null:
            {
                var s = _decoder.Read(ref r);
                if (s is null) break;
                SnapshotsReceived++;
                SnapshotBytes += data.Length;
                LastSnapshotSize = data.Length;
                if (s.AckedInput > AckedInput) AckedInput = s.AckedInput;
                if (Snapshots.ContainsKey(s.Tick)) break;
                if (s.Tick > NewestTick) TakeSlack(s.Slack);
                Snapshots.Add(s.Tick, s);
                while (Snapshots.Count > _bufferTicks) Snapshots.RemoveAt(0);
                if (s.Tick > NewestTick || NewestAt == 0) { NewestTick = s.Tick; NewestAt = Now; }
                break;
            }
            case MsgType.SnapshotPart when Accept is not null:
                if (_parts.Add(ref r) is { } whole) Receive(whole);
                break;
            case MsgType.MapOffer when State == ClientState.Connecting && Accept is null:
                WaitingForHost = false;
                try { Offer = MapOffer.Read(ref r); }
                catch (WireException e) { Fail($"the host's map: {e.Message}"); break; }
                State = ClientState.MapOffered;
                MapOffered?.Invoke(Offer);
                break;
            case MsgType.MapChunk when State == ClientState.Downloading && Download is { } d:
                try
                {
                    d.Add(MapChunk.Read(ref r));
                    if (d.Done is { } dir) { MapDir = dir; SendReady(); }
                }
                catch (Exception e) when (e is WireException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Fail($"the map download failed: {e.Message}");
                }
                break;
            case MsgType.NextRound when Accept is not null || State == ClientState.Connecting:
            {
                // the old round is over for good: what is left of it (snapshots, events) must not meet the new one's
                string arena = r.Str();
                Accept = null;
                Offer = null;
                Download?.Dispose();
                Download = null;
                MapDir = null;
                Scores = null;
                Snapshots.Clear();
                Events.Clear();
                CarAnswers.Clear();
                ReconfigureAnswers.Clear();
                WaitingForHost = false;
                NewestTick = 0;
                NewestAt = 0;
                AckedInput = 0;
                _slack.Clear();
                ClockScale = 1;
                _parts.Clear();
                _decoder.Clear();
                _names.Clear();
                _board.Clear();
                State = ClientState.Connecting;
                Rounds++;
                MovieName = null;
                _movieFinished = null;
                NextArena = arena;
                NewRound?.Invoke(arena);
                break;
            }
            case MsgType.Events:
            {
                EventBytes += data.Length;
                var batch = EventBatch.Read(ref r);
                Events.Enqueue(batch);
                // a co-op movie stops the game for everyone: taken at once, not with the tick it began at
                foreach (var e in batch.Events)
                    if (e is MovieEvent mv) SetMovie(mv.Name);
                    else if (e is MovieEndEvent) MovieName = null;
                break;
            }
            case MsgType.Scoreboard:
                Scores = Scoreboard.Read(ref r, _board);
                break;
            case MsgType.VoiceFrom:
                // unreliable and unordered: a bounded queue, the oldest dropped (the player's voice buffer is shorter than this); outside the game it is just dropped
                var vp = VoicePacket.ReadDown(ref r);
                if (State != ClientState.Joined) break;
                if (VoiceIn.Count >= 256) VoiceIn.Dequeue();
                VoiceIn.Enqueue(vp);
                break;
            case MsgType.VoiceMuted when Accept is not null:
                CbMuted = r.Bool();
                break;
            case MsgType.Cars when Accept is not null:
                foreach (var c in CarList.Read(ref r).Cars) CarAnswers.Enqueue(c);
                break;
            default:
                ReceiveMigration(type, ref r, data.Length);
                break;
        }
    }

    /// <summary>The offered map is here (the install's own, or <paramref name="dir"/>, a checked download): the server seats us.</summary>
    public void ReadyMap(string? dir = null)
    {
        if (State != ClientState.MapOffered) return;
        MapDir = dir;
        SendReady();
    }

    private void SendReady()
    {
        if (_peer is null || Offer is null) return;
        _w.Clear();
        _w.U8((byte)MsgType.MapReady);
        _w.Bytes(Offer.Hash);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
        State = ClientState.Connecting;
    }

    /// <summary>
    /// Downloads the offered map into <paramref name="cache"/> (taking up a download cut off before where it stopped);
    /// once it is whole and checked the server is told and seats us. Its progress is <see cref="Download"/>'s.
    /// </summary>
    public void DownloadMap(MapCache cache)
    {
        if (State != ClientState.MapOffered || Offer is not { Shared: true } offer || _peer is null) return;
        try
        {
            Download?.Dispose();
            Download = new MapDownload(cache, offer);
            if (Download.TryFinish()) { MapDir = Download.Done; SendReady(); return; }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Fail($"the map download failed: {e.Message}");
            return;
        }
        _w.Clear();
        Download.Request().Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
        State = ClientState.Downloading;
    }

    /// <summary>Gives up the join (a download cancelled, an offer declined or failed), saying why.</summary>
    public void Fail(string why)
    {
        Reason = why;
        Download?.Dispose();
        Leave();
        Disconnected?.Invoke(why);
    }

    private readonly (uint Tick, uint Hash)[] _motionHashes = new (uint, uint)[64];
    private uint _newestHash;

    /// <summary>
    /// The prediction's car after client tick <paramref name="tick"/> hashes to this (<see cref="OwnMotion.Hash"/>; the
    /// latest say for a tick counts): the next inputs carry it, and the server confirms the motion by it (protocol 13).
    /// </summary>
    public void ReportMotion(uint tick, uint hash)
    {
        _motionHashes[tick % 64] = (tick, hash);
        if (tick > _newestHash) _newestHash = tick;
    }

    /// <summary>The prediction could not find a confirmed state: the inputs ask for the motion itself until one comes.</summary>
    public bool NeedExact { get; set; }

    /// <summary>The driver's input for the next tick: sent with the ones before it, unreliably.</summary>
    public void SendInput(CarInput input)
    {
        var m = _inputs.Push(input) with { SnapshotAck = _decoder.Last, NeedExact = NeedExact };
        if (_newestHash > 0 && _newestHash <= m.Tick)
        {
            var hashes = new List<uint>(InputMessage.MaxHashes);
            for (uint t = _newestHash; t > 0 && hashes.Count < Protocol.InputHistory(Accept?.Rate ?? I76.Sim.Session.SimRate.Default) && _motionHashes[t % 64].Tick == t; t--)
                hashes.Add(_motionHashes[t % 64].Hash);
            if (hashes.Count > 0) m = m with { Hashes = hashes, HashTick = _newestHash };
        }
        if (_peer is null || State != ClientState.Joined) return;
        _w.Clear();
        m.Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.Unreliable);
        InputsSent++;
        InputBytes += _w.Length;
    }

    /// <summary>Raw bytes as input (the tests' misbehaving client).</summary>
    public void SendRaw(ReadOnlySpan<byte> bytes, DeliveryMethod how = DeliveryMethod.Unreliable) => _peer?.Send(bytes, how);

    /// <summary>Asks the server what these cars are (the snapshots name them and the mirror lacks them); the answers come in <see cref="CarAnswers"/>.</summary>
    public void AskCars(IReadOnlyCollection<int> serials)
    {
        if (_peer is null || State != ClientState.Joined || serials.Count == 0) return;
        _w.Clear();
        new CarQuery([.. serials]).Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
        CarQueries++;
    }

    /// <summary>How many times the mirror asked about cars it lacked (<see cref="AskCars"/>).</summary>
    public int CarQueries { get; private set; }

    /// <summary>
    /// While dead: the car the next respawn should build (I26's Reconfigure), with its .vcf when the server's install may
    /// lack it, as a join's configuration. The answer comes in <see cref="ReconfigureAnswers"/>.
    /// </summary>
    public void SendReconfigure(string car, byte[]? config, byte[]? paint = null)
    {
        if (_peer is null || State != ClientState.Joined) return;
        _w.Clear();
        new Reconfigure(car, config ?? [], paint).Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>The co-op game's lobby as the server last sent it (protocol 20); null outside one. The accept comes when the host starts the mission.</summary>
    public LobbyState? Lobby { get; private set; }
    /// <summary>The lobby's chat lines, not yet taken.</summary>
    public Queue<LobbyChat> LobbyLines { get; } = new();

    /// <summary>In the lobby: this player's ready mark, and a new car when <paramref name="car"/> is given (a car file or <see cref="Protocol.MissionCar"/>, with its .vcf for a variant the server may lack). A car is answered in <see cref="ReconfigureAnswers"/>.</summary>
    public void SendLobbySet(bool ready, string? car = null, byte[]? config = null, byte[]? paint = null)
    {
        if (_peer is null || State != ClientState.InLobby) return;
        _w.Clear();
        new LobbySet(ready, car ?? "", config ?? [], paint).Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>The lobby's host starts the mission (<paramref name="force"/>: though some are not ready).</summary>
    public void SendLobbyStart(bool force)
    {
        if (_peer is null || State != ClientState.InLobby) return;
        _w.Clear();
        new LobbyStart(force).Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Other players' CB voice frames, as the server relayed them, not yet taken (the game plays them).</summary>
    public Queue<VoicePacket> VoiceIn { get; } = new();

    /// <summary>One frame of this player's CB voice (unreliable); the server relays it to the players who may hear it. Dropped when not in the game.</summary>
    public void SendVoice(in VoicePacket p)
    {
        if (_peer is null || State != ClientState.Joined || CbMuted || !CbAllowed) return;
        _w.Clear();
        p.WriteUp(_w);
        _peer.Send(_w.Span, DeliveryMethod.Unreliable);
    }

    /// <summary>The host has muted this player's CB for everyone: the server relays none of this player's voice (<see cref="MsgType.VoiceMuted"/>).</summary>
    public bool CbMuted { get; private set; }
    /// <summary>The game has the CB radio on (the host's CB Radio option; a game that does not say has it).</summary>
    public bool CbAllowed => Accept?.Cb != false;
    /// <summary>The game allows the chase crosshair (the host's Chase Crosshair option; a game that does not say does not).</summary>
    public bool CrosshairAllowed => Accept?.Crosshair == true;

    /// <summary>Tells the server whom this client does not want to hear on the CB, so it stops relaying them (everyone with <paramref name="all"/>).</summary>
    public void SendVoiceIgnore(bool all, IEnumerable<int> slots)
    {
        if (_peer is null || State != ClientState.Joined) return;
        _w.Clear();
        new VoiceIgnore(all, [.. slots.Where(s => s is >= 0 and < 256).Select(s => (byte)s)]).Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    public void SendChat(string text)
    {
        if (_peer is null) return;
        _w.Clear();
        _w.U8((byte)MsgType.Chat);
        _w.Str(text.Length > Protocol.MaxChat ? text[..Protocol.MaxChat] : text);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Says goodbye and disconnects.</summary>
    public void Leave()
    {
        if (_peer is { } p)
        {
            _w.Clear();
            _w.U8((byte)MsgType.Leave);
            p.Send(_w.Span, DeliveryMethod.ReliableOrdered);
            _net.TriggerUpdate();
            _net.DisconnectPeer(p);
        }
        State = ClientState.Disconnected;
        Reason ??= "left";
    }

    public long BytesReceived => _net.Statistics.BytesReceived;
    public long BytesSent => _net.Statistics.BytesSent;

    public void Dispose()
    {
        Download?.Dispose();
        _joiner?.Dispose();
        _joiner = null;
        if (State is ClientState.Joined or ClientState.MapOffered or ClientState.Downloading or ClientState.InLobby) Leave();
        _net.Stop();
    }
}
