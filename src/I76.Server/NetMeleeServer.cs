using System.Diagnostics;
using I76.Net;
using I76.Net.Anet;
using LiteNetLib;
using LiteNetLib.Layers;
using LiteNetLib.Utils;

namespace I76.Server;

/// <summary>
/// The network melee's server: a <see cref="MeleeHost"/> on LiteNetLib. It
/// runs the session at the fixed 36 ms tick on its own thread, applies each
/// client's next input (holding the last when none has come), and sends
/// every joined client the tick's events (reliable, ordered) and a snapshot
/// (unreliable): the cars and rounds encoded once, and the client's own car
/// and input ack in front. A scoreboard goes out once a second. Used
/// in-process by the game's listen server and by tools/I76.Server.
/// </summary>
public sealed partial class NetMeleeServer : IDisposable
{
    /// <summary>The round being played; a new one replaces it between rounds (<see cref="Round"/>).</summary>
    public MeleeHost Host { get; private set; }
    private readonly Action<string> _hostLog;
    public ServerOptions Options => Host.Options;
    public event Action<string>? Log;

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly AnetLayer _layer;
    /// <summary>The game listed on anet servers and the hole punch's host side (<see cref="ListOn"/>); null when not listed.</summary>
    public AnetHost? Anet { get; private set; }
    private readonly Dictionary<int, Seat> _byPeer = [];
    private readonly NetWriter _msg = new(4096);
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _lock = new();

    /// <summary>Bytes sent in snapshots, events and scoreboards (per kind), and how many snapshots.</summary>
    public long SnapshotBytes, EventBytes, ScoreBytes, Snapshots, SplitSnapshots, SnapshotPieces;
    /// <summary>Event bytes sent, by <see cref="EventKind"/> (each event's size times its recipients; the load test's breakdown).</summary>
    public readonly long[] EventBytesByKind = new long[32];
    private readonly Action<I76.Net.NetEvent, int> _sized;
    public int LastSnapshotSize, MaxSnapshotSize;
    /// <summary>Snapshots sent in full (no baseline: a joiner, a client whose acks stopped).</summary>
    public long FullSnapshots;
    /// <summary>Time spent writing and sending snapshots (s), and their bytes by part: the head (own car, motion), the cars, the rounds.</summary>
    public double SnapshotSeconds;
    public long HeadBytes, CarBytes, RoundBytes;
    /// <summary>Every byte the socket has sent (LiteNetLib's count: headers, acks, resends and all).</summary>
    public long BytesSent => _net.Statistics.BytesSent;
    public long BytesReceived => _net.Statistics.BytesReceived;
    /// <summary>Each tick's time (simulation and sending, ms), raised on the tick thread (the load test's measure).</summary>
    public event Action<double>? Ticked;
    /// <summary>How long the last tick's simulation and sending took, ms.</summary>
    public double TickMs { get; private set; }
    /// <summary>Set once the game is over and the clients have been told; the console server exits some time after.</summary>
    public double? OverAt { get; private set; }
    /// <summary>Rounds played before the current one (0 in the first). Each is a new <see cref="Host"/>.</summary>
    public int Round { get; private set; }
    /// <summary>The melee carries on after its end (<see cref="ServerOptions.Rounds"/>) and has not failed to: the console server stays.</summary>
    public bool CarriesOn => Options.Rounds && Options.Coop is null && !_roundsFailed;
    private bool _roundsFailed, _skipPause;
    /// <summary>Seconds left of the standings pause, or null when a round is being played.</summary>
    public double? NextRoundIn { get { lock (_lock) return OverAt is { } o && CarriesOn ? Math.Max(0, Options.RoundPause - (Now - o)) : null; } }
    /// <summary>The standings pause ends now (a listen host's "Next Round").</summary>
    public void SkipPause() { lock (_lock) _skipPause = true; }
    /// <summary>A round is starting: the old one's players are being moved to the new one (tests).</summary>
    public event Action<MeleeHost>? RoundStarted;
    public int Port => _net.LocalPort;
    public int Players { get { lock (_lock) return Host.Seats.Count + _lobby.Count; } }

    /// <summary>
    /// Builds the arena (seconds, from the install) and opens the port; <see cref="Start"/> runs it. The socket carries
    /// anet's lobby traffic too (<see cref="AnetLayer"/>); <paramref name="inner"/> is a packet layer under it (the tests' NAT).
    /// </summary>
    public NetMeleeServer(ServerOptions o, PacketLayerBase? inner = null) : this(new MeleeHost(o), inner) { }

    /// <summary>On a game already built (a successor's restored one, <see cref="Resume"/>, on <paramref name="port"/>).</summary>
    private NetMeleeServer(MeleeHost host, PacketLayerBase? inner, int? port = null)
    {
        var o = host.Options;
        Host = host;
        _sized = (e, n) => EventBytesByKind[(int)e.Kind & 31] += n;
        _hostLog = m => Log?.Invoke(m);
        Host.Log += _hostLog;
        _layer = new AnetLayer(inner);
        _net = new NetManager(_listener, _layer)
        {
            AutoRecycle = true, DisconnectTimeout = 15000, EnableStatistics = true, UpdateTime = 5, MtuDiscovery = true,
            // channel 1: a custom map's pieces (MapTransferLimits.Channel), queued apart from the game's reliable messages
            ChannelsCount = 2,
            // the session queries: LAN broadcasts, and an address book's direct ones
            UnconnectedMessagesEnabled = true, BroadcastReceiveEnabled = true,
        };
        _listener.NetworkReceiveUnconnectedEvent += (ep, reader, type) =>
        {
            // (GetRemainingBytesSpan consumes the reader: taken once)
            var data = reader.GetRemainingBytesSpan();
            if (Anet?.HandleUnconnected(ep, data) == true || MigrationProbe(ep, data)) return;
            if (!Discovery.IsQuery(data)) return;
            _msg.Clear();
            Info().Write(_msg);
            _net.SendUnconnectedMessage(_msg.Span, ep);
        };
        _listener.ConnectionRequestEvent += OnRequest;
        _listener.PeerConnectedEvent += OnConnected;
        _listener.PeerDisconnectedEvent += (p, info) =>
        {
            _joined.Remove(p.Id);
            if (_invites.FindIndex(x => x.Peer.Id == p.Id) is var ii and >= 0) { Log?.Invoke($"{_invites[ii].Request.Name} left before the host answered ({info.Reason})"); _invites.RemoveAt(ii); }
            if (_byPeer.Remove(p.Id, out var s)) Host.Leave(s, info.Reason.ToString());
            else if (_lobby.Find(x => x.Peer.Id == p.Id) is { } lm) LobbyLeft(lm, info.Reason.ToString());
            else if (_waiting.Remove(p.Id, out var w))
                Log?.Invoke($"{w.Request.Name} left before joining{(w.Queue.Count > 0 ? $", the map {w.Sent / 1024} KiB in" : "")} ({info.Reason})");
        };
        _listener.NetworkReceiveEvent += (p, reader, ch, how) =>
        {
            if (_waiting.TryGetValue(p.Id, out var w))
            {
                try { ReceiveWaiting(w, reader.GetRemainingBytesSpan()); }
                catch (WireException ex) { Log?.Invoke($"{w.Request.Name}: malformed message before joining ({ex.Message})"); Refuse(w, "malformed map message"); }
                return;
            }
            if (_lobby.Find(x => x.Peer.Id == p.Id) is { } lobbyer)
            {
                try { ReceiveLobby(lobbyer, reader.GetRemainingBytesSpan()); }
                catch (WireException ex) { Log?.Invoke($"{lobbyer.Request.Name}: malformed message ({ex.Message})"); }
                return;
            }
            if (!_byPeer.TryGetValue(p.Id, out var s)) return;
            try { Receive(p, s, reader.GetRemainingBytesSpan()); }
            catch (WireException ex) { Log?.Invoke($"{s.Name}: malformed message ({ex.Message})"); }
        };
        _listener.NetworkLatencyUpdateEvent += (p, latency) => { if (_byPeer.TryGetValue(p.Id, out var s)) { s.Ping.Add(p.RoundTripTime); s.Rtt = s.Ping.Low; } };
        if (!_net.Start(port ?? o.Port)) throw new IOException($"could not open UDP port {port ?? o.Port}");
        Log?.Invoke($"listening on UDP {_net.LocalPort}");
        _lobbyOpen = o.Lobby && o.Coop is not null && port is null;
        if (o.HostMigration) _mig = new MigrationHostSide();
    }

    /// <summary>What the server says about its arena's files at the start (null for a stock arena).</summary>
    public string? MapNote => Host.MapOffer is { } m
        ? $"custom map {m.Mission}: {m.Files.Count} files, {m.TotalSize / 1024} KiB, hash {m.HashHex[..16]}; {(m.Shared ? "shared with players who lack it" : $"not shared: {m.Reason}")}"
        : null;

    private readonly Dictionary<int, JoinRequest> _requests = [];

    private void OnRequest(ConnectionRequest req)
    {
        JoinRequest j;
        try
        {
            var r = new NetReader(req.Data.GetRemainingBytesSpan());
            j = JoinRequest.Read(ref r);
        }
        catch (WireException) { req.RejectForce(); return; }
        // a player of the game this one took over, coming back to its seat (host migration)
        if (j.Rejoin is not null) { RequestRejoin(req, j); return; }
        string? why = j.Version != Protocol.Version ? $"protocol {j.Version}; this server speaks {Protocol.Version}"
            : CheckBan(req.RemoteEndPoint.Address, j.Name) ?? CheckPassword(req.RemoteEndPoint.Address, j.Password)
            ?? (_lobbyClosed ? LobbyClosedText : Host.Seats.Count + _lobby.Count + _requests.Count + _waiting.Count + _invites.Count >= Options.MaxPlayers ? "the server is full"
            : Host.Over ? (CarriesOn ? "between rounds: try again in a few seconds" : Options.Coop is not null ? "the mission is over" : "the melee is over")
            : Host.Mode.CanJoin() ?? Host.CheckJoin(j.Car, j.Config));
        if (why is not null)
        {
            var w = new NetDataWriter();
            w.Put(why);
            Log?.Invoke($"refused {req.RemoteEndPoint}: {why}");
            req.Reject(w);
            return;
        }
        var peer = req.Accept();
        _requests[peer.Id] = j;
    }

    private readonly Dictionary<System.Net.IPAddress, double> _wrongAt = [];

    /// <summary>
    /// Why the password turns a join away (null: it is right, or the game is open). A wrong one blocks that address's next
    /// try for <see cref="Protocol.PasswordRetrySeconds"/>, whatever it holds, so guessing is not quick. Asking without a
    /// password (to learn that one is needed) does not count as a try.
    /// </summary>
    private string? CheckPassword(System.Net.IPAddress from, string given)
    {
        var want = Options.Password;
        if (want.Length == 0) return null;
        double now = Now;
        if (_wrongAt.TryGetValue(from, out var at) && now - at < Protocol.PasswordRetrySeconds)
        {
            _wrongAt[from] = now;
            return Protocol.PasswordTooSoon;
        }
        if (given.Length == 0) return Protocol.NeedsPassword;
        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(want), System.Text.Encoding.UTF8.GetBytes(given)))
        {
            _wrongAt.Remove(from);
            return null;
        }
        if (_wrongAt.Count > 1024) _wrongAt.Clear();
        _wrongAt[from] = now;
        return Protocol.WrongPassword;
    }

    /// <summary>
    /// A client connected and not yet seated: it was offered the custom arena (<see cref="MeleeHost.MapOffer"/>) and is
    /// deciding, downloading (<see cref="Queue"/>: the files and offsets still to send) or about to say it is ready. Its
    /// seat is held for it (the full check counts it).
    /// </summary>
    private sealed class Waiting(NetPeer peer, JoinRequest request, double since)
    {
        public NetPeer Peer { get; } = peer;
        public JoinRequest Request { get; } = request;
        public double Since { get; } = since;
        /// <summary>The files still to send and where each is up to, sent one after the other.</summary>
        public readonly List<(int File, uint Offset)> Queue = [];
        public bool Requested;
        public double Credit;
        public long Sent;
    }

    private readonly Dictionary<int, Waiting> _waiting = [];
    /// <summary>What each seated peer asked for when it joined (its car and configuration, for the next round's seat).</summary>
    private readonly Dictionary<int, JoinRequest> _joined = [];
    private double _mapCredit, _lastPump;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * NetClock.Scale;
    /// <summary>Bytes of custom maps sent, and the clients that finished a download.</summary>
    public long MapBytes;
    public int MapDownloads;
    /// <summary>Clients connected and not yet seated (offered the map, downloading it).</summary>
    public int WaitingCount { get { lock (_lock) return _waiting.Count; } }

    private void OnConnected(NetPeer peer)
    {
        if (BindRejoin(peer)) return;
        if (!_requests.Remove(peer.Id, out var j)) { _net.DisconnectPeer(peer); return; }
        // INVITE ONLY: the join waits for the host's answer; a listen host's own join, the first, does not
        bool host = Options.ListenHost && !_hostIn;
        _hostIn = true;
        if (host) _hostPeer = peer.Id;
        if (Options.InviteOnly && !host)
        {
            _invites.Add(new Invite(peer, j, Now));
            _msg.Clear();
            _msg.U8((byte)MsgType.JoinWait);
            peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
            Log?.Invoke($"{j.Name} asks to join from {peer} (invite only): waiting for the host's answer");
            InviteAsked?.Invoke(j.Name);
            return;
        }
        Admit(peer, j);
    }

    private bool _hostIn;

    /// <summary>A join let in: the custom arena's offer first, or the seat at once.</summary>
    private void Admit(NetPeer peer, JoinRequest j)
    {
        if (Host.MapOffer is { } offer)
        {
            // a custom arena: the client hears of its files first, and is seated once it has them
            _waiting[peer.Id] = new Waiting(peer, j, Now);
            _msg.Clear();
            offer.Write(_msg);
            peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
            return;
        }
        Seat(peer, j);
    }

    /// <summary>An invite-only join waiting for the host: when it asked.</summary>
    private sealed record Invite(NetPeer Peer, JoinRequest Request, double Since);
    /// <summary>The joins waiting for the host's answer, the one asked about first (the head of the queue).</summary>
    private readonly List<Invite> _invites = [];

    /// <summary>The join the host is being asked about ("Let %s join the game?"): the player's name and how long ago they asked; null with none.</summary>
    public (string Name, double Seconds)? PendingInvite { get { lock (_lock) return _invites.Count > 0 ? (_invites[0].Request.Name, Now - _invites[0].Since) : null; } }
    /// <summary>How many joins are waiting for the host's answer.</summary>
    public int InvitesWaiting { get { lock (_lock) return _invites.Count; } }
    /// <summary>A join asks to be let in (an invite-only game): its name. Raised on the tick thread.</summary>
    public event Action<string>? InviteAsked;

    /// <summary>
    /// The host's answer to the join asked about (INVITE_ACCEPT / INVITE_DECLINE / INVITE_SKIP): let in, turned away ("The host has rejected you..."), or put at the back of the queue, keeping the
    /// time it asked (so a skip does not lengthen its 30 s). Returns the name answered, or null with nobody waiting.
    /// </summary>
    public string? AnswerInvite(InviteAnswer answer)
    {
        lock (_lock)
        {
            if (_invites.Count == 0) return null;
            var i = _invites[0];
            _invites.RemoveAt(0);
            switch (answer)
            {
                case InviteAnswer.Accept:
                    Log?.Invoke($"the host lets {i.Request.Name} join");
                    if (Host.Over) { Turn(i.Peer, "the melee is over"); break; }
                    Admit(i.Peer, i.Request);
                    break;
                case InviteAnswer.Decline:
                    Log?.Invoke($"the host turns {i.Request.Name} away");
                    Turn(i.Peer, Protocol.InviteDeclined);
                    break;
                default:
                    _invites.Add(i);
                    if (_invites.Count > 1) InviteAsked?.Invoke(_invites[0].Request.Name);
                    break;
            }
            return i.Request.Name;
        }
    }

    /// <summary>Joins unanswered for <see cref="Protocol.InviteSeconds"/> are turned away ("Your request timed out").</summary>
    private void ExpireInvites()
    {
        bool head = false;
        for (int k = _invites.Count - 1; k >= 0; k--)
        {
            if (Now - _invites[k].Since < Options.InviteSeconds) continue;
            var i = _invites[k];
            head |= k == 0;
            _invites.RemoveAt(k);
            Log?.Invoke($"{i.Request.Name}'s request to join timed out (no answer in {Options.InviteSeconds:0} s)");
            Turn(i.Peer, Protocol.InviteTimedOut);
        }
        if (head && _invites.Count > 0) InviteAsked?.Invoke(_invites[0].Request.Name);
    }

    private void Turn(NetPeer peer, string why)
    {
        var d = new NetDataWriter();
        d.Put(why);
        _net.DisconnectPeer(peer, d);
    }

    /// <summary>
    /// The host boots a player (BOOT_n; i26-server's <c>kick</c>): by
    /// slot number or name. The player is told (<see cref="Protocol.Booted"/>), the others see them leave, and the car is
    /// blown up as a leaver's. Returns null when done, else why not.
    /// </summary>
    public string? Kick(string who)
    {
        lock (_lock)
        {
            who = who.Trim();
            if (_lobbyOpen && KickFromLobby(who)) return null;
            if (FindSeat(who) is not { } seat) return Host.BootBot(who);
            var peer = (NetPeer)seat.Connection!;
            _byPeer.Remove(peer.Id);
            _joined.Remove(peer.Id);
            Host.Leave(seat, "booted");
            Log?.Invoke($"{seat.Name} was booted");
            Turn(peer, Protocol.Booted);
            return null;
        }
    }

    /// <summary>A connected player's seat by slot number, name, or a unique start of a name (case does not matter); null if none.</summary>
    private Seat? FindSeat(string who)
    {
        var seat = int.TryParse(who, out int slot) ? _byPeer.Values.FirstOrDefault(x => x.Slot == slot) : null;
        return seat ?? _byPeer.Values.FirstOrDefault(x => x.Name.Equals(who, StringComparison.OrdinalIgnoreCase))
            ?? (_byPeer.Values.Where(x => x.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);
    }

    /// <summary>Boots the player in this slot (the listen host's BOOT keys).</summary>
    public string? Kick(int slot) => Kick(slot.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void Seat(NetPeer peer, JoinRequest j)
    {
        // a co-op game that has not started gathers its players in the lobby first (NetMeleeServer.Lobby.cs)
        if (_lobbyOpen) { LobbyJoin(peer, j); return; }
        SendAccept(peer, SeatIn(peer, j));
    }

    private Seat SeatIn(NetPeer peer, JoinRequest j)
    {
        var s = Host.Join(j.Name, j.Car, j.Team, peer, j.Config, j.Paint);
        _byPeer[peer.Id] = s;
        _joined[peer.Id] = j;
        return s;
    }

    private void SendAccept(NetPeer peer, Seat s)
    {
        _msg.Clear();
        Host.AcceptFor(s).Write(_msg);
        peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
        Log?.Invoke($"{s.Name} connected from {peer}");
        if (Host.CbMuted.Contains(s.Name)) TellMuted(s, true);
    }

    private void Refuse(Waiting w, string why)
    {
        _waiting.Remove(w.Peer.Id);
        var d = new NetDataWriter();
        d.Put(why);
        _net.DisconnectPeer(w.Peer, d);
    }

    /// <summary>A client before its seat: asks for the map's files, says it has the map, or leaves.</summary>
    private void ReceiveWaiting(Waiting w, ReadOnlySpan<byte> data)
    {
        var offer = Host.MapOffer!;
        var r = new NetReader(data);
        switch ((MsgType)r.U8())
        {
            case MsgType.MapRequest:
            {
                var q = MapRequest.Read(ref r);
                if (!q.Hash.AsSpan().SequenceEqual(offer.Hash)) { Refuse(w, "that is not this server's map"); return; }
                if (!offer.Shared) { Refuse(w, offer.Reason.Length > 0 ? offer.Reason : "the host does not share maps"); return; }
                w.Queue.Clear();
                foreach (var (f, o) in q.From)
                {
                    if (f >= offer.Files.Count || o > offer.Files[f].Size) throw new WireException($"map file {f} from {o}");
                    if (o < offer.Files[f].Size && !w.Queue.Any(x => x.File == f)) w.Queue.Add((f, o));
                }
                if (!w.Requested) Log?.Invoke($"{w.Request.Name} is downloading the map ({q.From.Sum(x => offer.Files[x.File].Size - x.Offset) / 1024} KiB)");
                w.Requested = true;
                break;
            }
            case MsgType.MapReady:
            {
                var hash = r.Bytes(32);
                if (!hash.SequenceEqual(offer.Hash)) { Refuse(w, "the map does not match the host's"); return; }
                if (Host.Over) { Refuse(w, "the melee is over"); return; }
                _waiting.Remove(w.Peer.Id);
                if (w.Requested) { MapDownloads++; Log?.Invoke($"{w.Request.Name} has the map ({w.Sent / 1024} KiB sent)"); }
                Seat(w.Peer, w.Request);
                break;
            }
            case MsgType.Leave:
                Refuse(w, "left");
                break;
        }
    }

    /// <summary>
    /// The map's pieces out, each downloading client in turn: at most its rate's worth (and all together the total
    /// rate's), and none while its map channel already has <see cref="MapTransferLimits.MaxQueuedPackets"/> waiting (a
    /// slow or lossy link). Players already in the game share the line with this, so it is held well below it.
    /// </summary>
    private void PumpMaps()
    {
        double now = Now, dt = Math.Min(0.25, _lastPump == 0 ? 0 : now - _lastPump);
        _lastPump = now;
        if (_waiting.Count == 0) return;
        _mapCredit = Math.Min(_mapCredit + Options.MapTotalBytesPerSecond * dt, Options.MapTotalBytesPerSecond * 0.25);
        foreach (var w in _waiting.Values.ToList())
        {
            if (!w.Requested && now - w.Since > MapTransferLimits.AnswerSeconds) { Log?.Invoke($"{w.Request.Name} did not answer the map offer"); Refuse(w, "no answer to the map offer"); continue; }
            if (w.Queue.Count == 0) continue;
            w.Credit = Math.Min(w.Credit + Options.MapBytesPerSecond * dt, Options.MapBytesPerSecond * 0.25);
            while (w.Queue.Count > 0 && w.Credit > 0 && _mapCredit > 0 && w.Peer.GetPacketsCountInReliableQueue(MapTransferLimits.Channel, true) < MapTransferLimits.MaxQueuedPackets)
            {
                var (f, o) = w.Queue[0];
                var file = Host.MapData[f];
                int n = (int)Math.Min(MapTransferLimits.ChunkSize, file.Length - o);
                _msg.Clear();
                MapChunk.Write(_msg, f, o, file.AsSpan((int)o, n));
                w.Peer.Send(_msg.Span, MapTransferLimits.Channel, DeliveryMethod.ReliableOrdered);
                if (o + n < file.Length) w.Queue[0] = (f, (uint)(o + n));
                else w.Queue.RemoveAt(0);
                w.Credit -= n;
                _mapCredit -= n;
                w.Sent += n;
                MapBytes += n;
            }
        }
    }

    private void Receive(NetPeer peer, Seat s, ReadOnlySpan<byte> data)
    {
        var r = new NetReader(data);
        var type = (MsgType)r.U8();
        switch (type)
        {
            case MsgType.Input:
            {
                var m = InputMessage.Read(ref r);
                s.Inputs.Add(m);
                Host.AckSnapshot(s, m.SnapshotAck);
                break;
            }
            case MsgType.Chat:
                Host.Chat(s, r.Str());
                break;
            case MsgType.Voice:
                RelayVoice(s, VoicePacket.ReadUp(ref r, (byte)s.Slot));
                break;
            case MsgType.VoiceIgnore:
                SetIgnored(s, VoiceIgnore.Read(ref r));
                break;
            case MsgType.MovieDone:
                Host.MovieDone(s, r.Str());
                break;
            case MsgType.Reconfigure:
            {
                var q = Reconfigure.Read(ref r);
                double now = Now;
                string? file = null;
                string? why = now - s.ReconfiguredAt < 0.5 ? "too many changes - wait a moment" : Host.Reconfigure(s, q.Car, q.Config.Length > 0 ? q.Config : null, out file, q.Paint);
                s.ReconfiguredAt = now;
                if (why is null && _joined.TryGetValue(peer.Id, out var jr)) _joined[peer.Id] = jr with { Car = q.Car, Config = q.Config };
                else if (why is not null) Log?.Invoke($"{s.Name}'s new car {q.Car} refused: {why}");
                _msg.Clear();
                new ReconfigureAnswer(why is null, why ?? file ?? "").Write(_msg);
                peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
                break;
            }
            case MsgType.CarQuery:
                if (Host.AnswerCarQuery(s, CarQuery.Read(ref r).Serials) is { } cars)
                {
                    _msg.Clear();
                    new CarList([.. cars]).Write(_msg);
                    peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
                }
                break;
            case MsgType.Leave:
                _byPeer.Remove(peer.Id);
                Host.Leave(s, "left");
                break;
            default:
                ReceiveMigration(peer, s, type, ref r);
                break;
        }
    }

    /// <summary>One tick: the network's news, the simulation, then the events and snapshots out.</summary>
    public void Step()
    {
        lock (_lock)
        {
            _net.PollEvents();
            Anet?.Update();
            // a server that carries on holds its round while nobody is there (and nobody is on the way in): the clock and
            // the AI stand still, so an idle server does not play rounds out to nobody and the first joiner gets a whole one
            // a game taken over waits a moment for its players to come back (host migration)
            if (!(Options.Rounds && !Host.Over && Host.Seats.Count == 0 && _waiting.Count == 0) && !HoldForRejoins()) Host.Step();
            ExpireInvites();
            LobbyTick();
            Send();
            MigrationTick();
            if (Host.Over && CarriesOn && OverAt is { } over && (_skipPause || Now - over >= Options.RoundPause)) StartNextRound();
            PumpMaps();
            _net.TriggerUpdate();
        }
    }

    private int _sinceScores;

    /// <summary>
    /// The standings pause is over: the next round is built (the same arena, or the next of the rotation) and the players
    /// still connected are seated in it as they joined (name, car and configuration, team), each told first
    /// (<see cref="MsgType.NextRound"/>), then offered a custom map or sent the accept, on the connection they have.
    /// Scores start again with the new game. A round that cannot be built (a rotation entry the install lacks) falls back to
    /// the arena just played; if that fails too the server stops carrying on and ends as with --once.
    /// </summary>
    private void StartNextRound()
    {
        var old = Host;
        MeleeHost? next = null;
        var o = RoundPlan.Next(old.Options);
        foreach (var attempt in new[] { o, old.Options with { Seed = o.Seed } })
        {
            try { next = new MeleeHost(attempt); break; }
            catch (Exception ex)
            {
                Log?.Invoke($"round {Round + 2}: {attempt.Arena} could not be built ({ex.Message})");
            }
        }
        if (next is null) { _roundsFailed = true; Log?.Invoke("no further round: the server ends"); return; }
        var carried = _byPeer.Values.OrderBy(s => s.Slot).ToList();
        old.Log -= _hostLog;
        Host = next;
        next.Log += _hostLog;
        Round++;
        OverAt = null;
        _skipPause = false;
        _sinceScores = 0;
        _byPeer.Clear();
        Log?.Invoke($"round {Round + 1}: {next.Arena}, {carried.Count} players carry on");
        foreach (var s in carried)
        {
            var peer = (NetPeer)s.Connection!;
            var j = _joined.GetValueOrDefault(peer.Id) ?? new JoinRequest(Protocol.Version, s.Name, s.Actor.CarFile, (byte)s.Actor.Team, []);
            // the team it had (CTF may have dealt it one), not the one it first asked for
            j = j with { Team = (byte)s.Actor.Team, Paint = s.Actor.Car.Paint?.ToBytes() };
            _msg.Clear();
            _msg.U8((byte)MsgType.NextRound);
            _msg.Str(next.Arena);
            peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
            if (next.MapOffer is { } offer)
            {
                _waiting[peer.Id] = new Waiting(peer, j, Now);
                _msg.Clear();
                offer.Write(_msg);
                peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
            }
            else Seat(peer, j);
        }
        RoundStarted?.Invoke(next);
    }

    private void Send()
    {
        var events = Host.TakeEvents();
        float ts = Host.Session.TickSeconds;
        double ema = Math.Min(1, ts / 2.0);
        var carAt = events.Count > 0 ? Host.CarPositions() : null;
        foreach (var (_, s) in _byPeer)
        {
            // each client its own batch: the events within its range, its names by number once sent
            int sent = 0;
            var mine = events.Count > 0 ? Host.EventsFor(s, events, carAt!) : null;
            if (mine is { Count: > 0 })
            {
                _msg.Clear();
                _msg.Names = s.Names;
                new EventBatch(Host.Tick, mine).Write(_msg, _sized);
                _msg.Names = null;
                ((NetPeer)s.Connection!).Send(_msg.Span, DeliveryMethod.ReliableOrdered);
                EventBytes += sent = _msg.Length;
            }
            s.EventRate += (sent / ts - s.EventRate) * ema;
        }
        if (events.OfType<GameOverEvent>().Any())
        {
            OverAt = Now;
            foreach (var w in _waiting.Values.ToList()) Refuse(w, "the melee is over");
            foreach (var i in _invites) Turn(i.Peer, "the melee is over");
            _invites.Clear();
        }
        long t0 = Stopwatch.GetTimestamp();
        SendSnapshots();
        SnapshotSeconds += Stopwatch.GetElapsedTime(t0).TotalSeconds;
        if (++_sinceScores >= (int)MathF.Round(1f / Host.Session.TickSeconds))
        {
            _sinceScores = 0;
            var board = Host.Scoreboard();
            foreach (var (_, s) in _byPeer)
            {
                _msg.Clear();
                board.Write(_msg, s.Board);
                ((NetPeer)s.Connection!).Send(_msg.Span, DeliveryMethod.ReliableOrdered);
                ScoreBytes += _msg.Length;
            }
        }
    }

    /// <summary>
    /// Each client's snapshot: against the snapshot it last acknowledged (<see cref="SnapshotEncoder"/>), with the cars
    /// far from it only every few ticks (<see cref="Interest"/>). The cars' and rounds' channels are taken once a tick.
    /// </summary>
    private void SendSnapshots()
    {
        var t = Host.CaptureTick();
        float ts = Host.Session.TickSeconds;
        var seats = _byPeer.Values.ToArray();
        if (_writers.Length < seats.Length) _writers = [.. _writers, .. Enumerable.Range(0, seats.Length - _writers.Length).Select(_ => new NetWriter(2048))];
        var budgets = new (int Max, int Budget)[seats.Length];
        for (int i = 0; i < seats.Length; i++)
        {
            var s = seats[i];
            // the budget: one datagram (the path's MTU), and with an upload limit this client's share of it less what its
            // events take (docs/multiplayer.md, "The snapshot budget")
            int max = ((NetPeer)s.Connection!).GetMaxSinglePacketSize(DeliveryMethod.Unreliable), budget = max;
            if (Options.UploadBytesPerSecond > 0)
            {
                double share = Options.UploadBytesPerSecond / (double)Math.Max(1, seats.Length) - s.EventRate - Protocol.ClientOverheadBytesPerSecond;
                budget = Math.Min(max, (int)(Math.Max(share, Protocol.MinSnapshotBytesPerSecond) * ts));
            }
            budgets[i] = (max, budget);
        }
        // each client's snapshot is its own work (its baseline, priorities and plans; the tick's capture is shared and
        // read only): written on as many threads as there are, then sent in order from this one
        var parts = new (int Head, int Cars, bool Full)[seats.Length];
        if (seats.Length >= 8) Parallel.For(0, seats.Length, i => { _writers[i].Clear(); parts[i] = Host.WriteSnapshot(_writers[i], seats[i], t, budgets[i].Budget); });
        else for (int i = 0; i < seats.Length; i++) { _writers[i].Clear(); parts[i] = Host.WriteSnapshot(_writers[i], seats[i], t, budgets[i].Budget); }
        for (int i = 0; i < seats.Length; i++)
        {
            var (head, cars, full) = parts[i];
            var msg = _writers[i];
            var peer = (NetPeer)seats[i].Connection!;
            int max = budgets[i].Max;
            HeadBytes += head;
            CarBytes += cars;
            RoundBytes += msg.Length - head - cars;
            // an unreliable datagram is at most the path's MTU: a bigger snapshot (its essential entries alone too many) goes in pieces
            if (msg.Length <= max) peer.Send(msg.Span, DeliveryMethod.Unreliable);
            else
            {
                foreach (var piece in SnapshotParts.Split(t.Tick, msg.ToArray(), max)) { peer.Send(piece, DeliveryMethod.Unreliable); SnapshotPieces++; }
                SplitSnapshots++;
            }
            SnapshotBytes += msg.Length;
            LastSnapshotSize = msg.Length;
            MaxSnapshotSize = Math.Max(MaxSnapshotSize, msg.Length);
            Snapshots++;
            if (full) FullSnapshots++;
        }
    }

    private NetWriter[] _writers = [];

    /// <summary>Runs the tick loop on its own thread until <see cref="Stop"/>.</summary>
    public void Start()
    {
        // the bots seated while the game was built (nobody was listening to the log yet)
        lock (_lock)
            foreach (var b in Host.Bots)
                Log?.Invoke($"bot: {b.Name} in slot {b.Slot}, {b.Player.CarFile}, team {b.Player.Team}, car {b.CarSerial}");
        _thread = new Thread(Run) { Name = "I76 melee server", IsBackground = true };
        _thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;
        while (!_stop)
        {
            double now = NetClock.Seconds(clock);
            if (now < next)
            {
                // between ticks: keep the network moving (acks, pings) without simulating
                try { lock (_lock) { _net.PollEvents(); Anet?.Update(); } }
                catch (Exception ex)
                {
                    // as a failed tick: on this thread an escaping exception ends the whole process
                    Fault = ex;
                    if (!_stop) Log?.Invoke($"network poll failed: {ex}");
                    break;
                }
                Thread.Sleep(Math.Clamp((int)((next - now) / NetClock.Scale * 1000) - 1, 0, 5));
                continue;
            }
            // fell far behind (a debugger, a stall): start counting again rather than racing to catch up
            if (now - next > 0.5) next = now;
            var t0 = clock.Elapsed.TotalMilliseconds;
            try { Step(); }
            catch (Exception ex)
            {
                // a bug in the game: logged, and the server stops rather than run on in a broken state
                Fault = ex;
                if (!_stop) Log?.Invoke($"tick {Host.Tick} failed: {ex}");
                break;
            }
            TickMs = clock.Elapsed.TotalMilliseconds - t0;
            Ticked?.Invoke(TickMs);
            Summarize(NetClock.Seconds(clock));
            next += Host.Session.TickSeconds;
        }
    }

    /// <summary>Seconds between the load lines in the log (players, the tick's time, the upload, the snapshots).</summary>
    public double SummarySeconds { get; set; } = 30;
    private double _sumAt = -1, _tickSum, _tickMax;
    private long _sumTicks, _sumBytes, _sumSnaps, _sumSnapBytes, _sumSplit;

    /// <summary>A line in the log every <see cref="SummarySeconds"/> while players are seated: what the server is costing.</summary>
    private void Summarize(double now)
    {
        _tickSum += TickMs;
        _tickMax = Math.Max(_tickMax, TickMs);
        _sumTicks++;
        if (_sumAt < 0) { (_sumAt, _sumBytes, _sumSnaps, _sumSnapBytes, _sumSplit) = (now, BytesSent, Snapshots, SnapshotBytes, SplitSnapshots); return; }
        if (now - _sumAt < SummarySeconds) return;
        double dt = now - _sumAt;
        long snaps = Snapshots - _sumSnaps;
        int players = Players;
        if (players > 0)
            Log?.Invoke(System.FormattableString.Invariant(
                $"load: {players} players, tick {_tickSum / _sumTicks:0.00} ms (max {_tickMax:0.0}) of {Host.Session.TickSeconds * 1000:0}, upload {(BytesSent - _sumBytes) * 8 / dt / 1e6:0.00} Mbit/s, snapshot {(SnapshotBytes - _sumSnapBytes) / (double)Math.Max(1, snaps):0} B, {SplitSnapshots - _sumSplit} in pieces"));
        (_sumAt, _sumBytes, _sumSnaps, _sumSnapBytes, _sumSplit, _tickSum, _tickMax, _sumTicks) = (now, BytesSent, Snapshots, SnapshotBytes, SplitSnapshots, 0, 0, 0);
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(10000);   // a tick stalled by a loaded machine must not outlive the server it belongs to
        _thread = null;
    }

    /// <summary>Tells the clients and closes.</summary>
    public void Dispose()
    {
        Stop();
        var w = new NetDataWriter();
        // a game that is over for good (a co-op mission's end; not a melee's pause before its next round) is not handed on
        w.Put(_lobbyOpen ? LobbyClosedText : _mig is not null && !(Host.Over && !CarriesOn) ? I76.Net.HostMigration.HostLeavingReason : "the server closed");
        lock (_lock)
        {
            Anet?.Dispose();
            Anet = null;
            _net.DisconnectAll(w.Data, 0, w.Length);
        }
        _net.Stop();
    }

    public void Chat(string text)
    {
        lock (_lock)
        {
            // in the lobby the server's word goes to the players there
            if (_lobbyOpen) { LobbySay("Server", text); return; }
            Host.Chat(null, text);
        }
    }

    /// <summary>
    /// Lists the game on these anet servers ("host[:port]", port 21157 by default) under I26's species, kept up to
    /// date once a second, and answers the hole punch for joiners they introduce. <paramref name="privateOverride"/>
    /// is our own address when an emulated NAT stands in front (tests).
    /// </summary>
    public void ListOn(IEnumerable<(string Host, int Port)> servers, AnetAddress? privateOverride = null)
    {
        bool nitro = new I76.Formats.Vfs.GameInstall(Options.Install).IsNitro;
        lock (_lock)
        {
            _anetServers = [.. servers];
            Anet?.Dispose();
            Anet = new AnetHost(new LiteNetAnetTransport(_net, _layer), _anetServers, () => Describe(nitro),
                (ep, d) => _net.SendUnconnectedMessage(d, ep), privateOverride: privateOverride);
            Anet.Log += m => Log?.Invoke($"anet: {m}");
        }
    }

    /// <summary>The listing's line for the host's screens (null when not listed anywhere).</summary>
    public string? AnetStatus { get { lock (_lock) return Anet?.Status; } }

    private AnetGame Describe(bool nitro)
    {
        var i = Info();
        // the hidden rule too: a host behind a NAT does not answer the roster's direct query, so the listing is all a joiner knows
        var tag = new I26SessionTag(Protocol.Version, nitro, i.TeamPlay, i.InviteOnly, i.Over, i.Kind, i.Teams, i.Goal, i.Arena, i.CustomMap, i.MapShared, i.Locked, i.Hidden, i.Coop, i.Lobby);
        // an invite-only game takes joins: the host is asked about each
        return new AnetGame(i.Name, i.MaxPlayers, i.Players, !i.Over && i.Players < i.MaxPlayers, tag.Write());
    }

    /// <summary>Runs something on the game between ticks (the tests' hooks).</summary>
    public void Invoke(Action<MeleeHost> a) { lock (_lock) a(Host); }
    public T Invoke<T>(Func<MeleeHost, T> f) { lock (_lock) return f(Host); }

    /// <summary>What stopped the tick loop, if something went wrong.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>The thread is still ticking.</summary>
    public bool Running => _thread?.IsAlive == true;
}
