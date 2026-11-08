using I76.Net;
using LiteNetLib;

namespace I76.Server;

/// <summary>
/// A co-op game's lobby (protocol 20, docs/multiplayer.md "The co-op lobby"): before the mission starts the players who connect are
/// not seated. They gather here, each with a car and a ready mark, and chat; the mission is built, every seat dealt and every accept sent
/// in one go when the host starts it (a listen host's button) or, on a dedicated server, when enough are all ready. The mission's first
/// tick then waits for every game to be loaded (<see cref="CoopMode.Holding"/>). Players who come after the start are seated as a
/// late joiner always was.
/// </summary>
public sealed partial class NetMeleeServer
{
    private sealed class LobbyPeer(byte id, NetPeer peer, JoinRequest request)
    {
        public byte Id { get; } = id;
        public NetPeer Peer { get; } = peer;
        public JoinRequest Request { get; set; } = request;
        public bool Ready { get; set; }
        public double SetAt { get; set; } = double.NegativeInfinity;
        public ChatLimiter Chat { get; } = new();
    }

    private readonly List<LobbyPeer> _lobby = [];
    /// <summary>Players are gathering and the mission has not started.</summary>
    private bool _lobbyOpen;
    /// <summary>The listen host left the lobby: it was closed, and nobody joins it now.</summary>
    private bool _lobbyClosed;
    private int _hostPeer = -1;

    /// <summary>The lobby is open (a co-op game with <see cref="ServerOptions.Lobby"/>, before its start).</summary>
    public bool InLobby { get { lock (_lock) return _lobbyOpen; } }
    /// <summary>The players in the lobby by name, with their ready marks.</summary>
    public IReadOnlyList<(string Name, string Car, bool Ready)> LobbyPlayers { get { lock (_lock) return [.. _lobby.Select(m => (m.Request.Name, m.Request.Car, m.Ready))]; } }

    /// <summary>The game as a session list shows it: in its lobby the players waiting in it, flagged as in the lobby.</summary>
    private SessionInfo Info()
    {
        var i = Host.Info();
        if (!_lobbyOpen) return i;
        return i with
        {
            Players = Host.Seats.Count + _lobby.Count, Lobby = true,
            Drivers = [.. _lobby.Select(m => Host.Listed(m.Request.Name, m.Request.Car, 0, m.Request.Config))],
        };
    }

    /// <summary>The host starts the mission (a listen host's START, in process; a client's message does the same): null when it did, else why not.</summary>
    public string? StartLobby(bool force = false)
    {
        lock (_lock) return TryStartLobby(force);
    }

    private string? TryStartLobby(bool force)
    {
        if (!_lobbyOpen) return "the mission has started";
        if (_lobby.Count == 0) return "nobody is here";
        var waiting = _lobby.Where(m => m.Peer.Id != _hostPeer && !m.Ready).Select(m => m.Request.Name).ToList();
        if (waiting.Count > 0 && !force) return $"not ready: {string.Join(", ", waiting)}";
        StartNow(waiting);
        return null;
    }

    private void StartNow(IReadOnlyList<string> notReady)
    {
        _lobbyOpen = false;
        // the host takes seat 0, the script's player, then the others in the order they came
        var members = _lobby.OrderBy(m => m.Peer.Id == _hostPeer ? 0 : 1).ToList();
        _lobby.Clear();
        Log?.Invoke($"lobby: the mission starts with {members.Count} players{(notReady.Count > 0 ? $" ({string.Join(", ", notReady)} not ready)" : "")}");
        // every seat is dealt before any accept is written: each accept names every seat's car
        var seated = members.Select(m => (m.Peer, Seat: SeatIn(m.Peer, m.Request))).ToList();
        foreach (var (peer, seat) in seated) SendAccept(peer, seat);
    }

    private void LobbyJoin(NetPeer peer, JoinRequest j)
    {
        string name = new string(j.Name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > Protocol.MaxName) name = name[..Protocol.MaxName];
        if (name.Length == 0) name = $"Player {_lobby.Count + 1}";
        int id = Enumerable.Range(0, 255).First(k => _lobby.All(m => m.Id != k));
        _lobby.Add(new LobbyPeer((byte)id, peer, j with { Name = name }));
        Log?.Invoke($"lobby: {name} comes in ({_lobby.Count} of {Options.MaxPlayers}), {j.Car}");
        SendLobby();
    }

    private void SendLobby()
    {
        byte host = _lobby.FirstOrDefault(m => m.Peer.Id == _hostPeer) is { } h ? h.Id : LobbyState.NoHost;
        byte auto = Options.ListenHost ? (byte)0 : (byte)Math.Clamp(Options.LobbyMinPlayers, 1, Protocol.MaxCoopSeats);
        foreach (var to in _lobby)
        {
            _msg.Clear();
            new LobbyState(Options.Coop ?? Options.Arena, (byte)Options.MaxPlayers, to.Id, host, auto,
                [.. _lobby.Select(m => new LobbyMember(m.Id, m.Request.Name, m.Request.Car, m.Ready))], (Host.Mode as CoopMode)?.OwnCar ?? "").Write(_msg);
            to.Peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>A lobby player leaves or drops: out of the list, the others told; the listen host's going closes the lobby.</summary>
    private void LobbyLeft(LobbyPeer m, string why)
    {
        if (!_lobby.Remove(m)) return;
        Log?.Invoke($"lobby: {m.Request.Name} leaves ({why})");
        if (m.Peer.Id == _hostPeer)
        {
            // there is no host to hand the lobby to: the listen host's game is the server (no mission has begun, so no savestate to
            // migrate), so the lobby closes with a word to the others
            _lobbyOpen = false;
            _lobbyClosed = true;
            foreach (var o in _lobby.ToList()) Turn(o.Peer, LobbyClosedText);
            _lobby.Clear();
            Log?.Invoke("lobby: the host left: the lobby is closed");
            return;
        }
        SendLobby();
    }

    public const string LobbyClosedText = "The host closed the lobby.";

    private void ReceiveLobby(LobbyPeer m, ReadOnlySpan<byte> data)
    {
        var r = new NetReader(data);
        switch ((MsgType)r.U8())
        {
            case MsgType.Chat:
            {
                string text = new string(r.Str().Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (text.Length > Protocol.MaxChat) text = text[..Protocol.MaxChat];
                if (text.Length == 0 || !m.Chat.Allow(Now, out _)) break;
                Log?.Invoke($"chat: {m.Request.Name}: {text}");
                LobbySay(m.Request.Name, text);
                break;
            }
            case MsgType.LobbySet:
            {
                var q = LobbySet.Read(ref r);
                if (q.Car.Length == 0) { m.Ready = q.Ready; SendLobby(); break; }
                double now = Now;
                string? why = now - m.SetAt < 0.25 ? "too many changes - wait a moment" : Host.CheckJoin(q.Car, q.Config.Length > 0 ? q.Config : null);
                m.SetAt = now;
                _msg.Clear();
                new ReconfigureAnswer(why is null, why ?? q.Car).Write(_msg);
                m.Peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
                if (why is not null) { Log?.Invoke($"lobby: {m.Request.Name}'s car {q.Car} refused: {why}"); break; }
                // a new car takes the ready mark off: the others should see what they are waiting for
                m.Request = m.Request with { Car = q.Car, Config = q.Config, Paint = q.Paint };
                m.Ready = false;
                SendLobby();
                break;
            }
            case MsgType.LobbyStart:
            {
                var q = LobbyStart.Read(ref r);
                if (m.Peer.Id != _hostPeer) break;
                if (TryStartLobby(q.Force) is { } no) Log?.Invoke($"lobby: {m.Request.Name} cannot start: {no}");
                break;
            }
            case MsgType.Leave:
                LobbyLeft(m, "left");
                _net.DisconnectPeer(m.Peer);
                break;
        }
    }

    private void LobbySay(string who, string text)
    {
        foreach (var to in _lobby)
        {
            _msg.Clear();
            new LobbyChat(who, text).Write(_msg);
            to.Peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>The host boots a lobby player (by their number in the lobby's list, or name); false when there is none by that name.</summary>
    private bool KickFromLobby(string who)
    {
        if (FindInLobby(who) is not { } m) return false;
        _lobby.Remove(m);
        Log?.Invoke($"lobby: {m.Request.Name} was booted");
        Turn(m.Peer, Protocol.Booted);
        SendLobby();
        return true;
    }

    /// <summary>A co-op lobby's player by place, name, or a unique start of a name; null if none.</summary>
    private LobbyPeer? FindInLobby(string who)
    {
        return int.TryParse(who, out int n) && n >= 0 && n < _lobby.Count ? _lobby[n]
            : _lobby.FirstOrDefault(x => x.Request.Name.Equals(who, StringComparison.OrdinalIgnoreCase))
            ?? (_lobby.Where(x => x.Request.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);
    }

    /// <summary>A lobby with no host (a dedicated server) starts itself when enough players are all ready.</summary>
    private void LobbyTick()
    {
        if (!_lobbyOpen || Options.ListenHost) return;
        if (_lobby.Count >= Math.Max(1, Options.LobbyMinPlayers) && _lobby.All(m => m.Ready)) StartNow([]);
    }
}
