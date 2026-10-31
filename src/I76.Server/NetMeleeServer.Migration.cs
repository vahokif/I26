using System.Net;
using System.Text.Json;
using I76.Net;
using I76.Sim.Session;
using LiteNetLib;
using LiteNetLib.Utils;

namespace I76.Server;

/// <summary>
/// Host migration on the server (docs/multiplayer.md "Host migration"; <see cref="ServerOptions.HostMigration"/>, a
/// client-hosted game's). The host side: it names the successor (the lowest seat, bar the host's own, whose client can
/// host), tells every client who that is and its seat's token, and streams the successor a savestate every
/// <see cref="HostMigration.StateSeconds"/> (at once after a join, a leave or a new round) and the inputs of every tick,
/// below the game's own traffic. The successor's side: <see cref="Resume"/> restores that game on the successor's port,
/// takes the players back to their seats (<see cref="JoinRequest.Rejoin"/>), holds the game until they are back or
/// <see cref="HostMigration.HoldSeconds"/> pass, and gives up the seats nobody came back to.
/// </summary>
public sealed partial class NetMeleeServer
{
    private sealed class MigrationHostSide
    {
        public ulong GameToken = (ulong)Random.Shared.NextInt64();
        public readonly Dictionary<int, HostCandidate> Candidates = [];
        public readonly Dictionary<int, ulong> SeatTokens = [];
        /// <summary>The invite-only joins waiting for the host's answer, by peer: each its token, to wait for the new host instead.</summary>
        public readonly Dictionary<int, ulong> InviteTokens = [];
        public int? Owner;
        public MeleeHost? Host;
        public string Sig = "";
        public NetPeer? Successor;
        public readonly HashSet<int> Told = [];
        public uint LastTick;
        public bool Barrier, Full = true, Dirty = true;
        public readonly Dictionary<byte, CarInput> Logged = [];
        public byte[]? State;
        public uint StateId, StateTick;
        public int Sent;
        public double StateAt = double.MinValue, Credit;
        public long Bytes;
        public int States;
    }

    private MigrationHostSide? _mig;
    private (string Host, int Port)[] _anetServers = [];

    /// <summary>The successor's restored game while its players come back: the seats awaiting them and their tokens.</summary>
    private sealed class ResumeSide
    {
        public required Dictionary<int, ulong> Tokens;
        public required Dictionary<int, JoinRequest> Requests;
        public required List<IPEndPoint> Clients;
        public required double Since;
        public required int? Owner;
        public required ulong GameToken;
        /// <summary>The joins that were waiting for the old host's answer, by token: the request and how long it had waited.</summary>
        public required Dictionary<ulong, (JoinRequest Request, double Age)> Invites;
        public readonly Dictionary<int, (JoinRequest Request, double Age)> PendingInvites = [];
        public readonly Dictionary<int, Seat> Pending = [];   // peer id → seat it rejoins
        public double ProbeAt;
        public bool Released;
    }

    private ResumeSide? _resume;

    /// <summary>The host's own seat (a listen host's player): never its own successor, and the one that leaves with it. Set by the game once its client is seated.</summary>
    public int? MigrationOwner
    {
        get { lock (_lock) return _mig?.Owner; }
        set { lock (_lock) { if (_mig is not null) _mig.Owner = value; Host.HostSlot = value; } }
    }

    /// <summary>The seat named to take over (null: none, or no migration), the savestates sent to it and the bytes its stream took.</summary>
    public int? SuccessorSlot { get { lock (_lock) return _mig?.Successor is { } p && _byPeer.TryGetValue(p.Id, out var s) ? s.Slot : null; } }
    public int MigrationStates { get { lock (_lock) return _mig?.States ?? 0; } }
    public long MigrationBytes { get { lock (_lock) return _mig?.Bytes ?? 0; } }
    /// <summary>Seats of a game taken over that nobody has come back to yet.</summary>
    public int AwaitingRejoin { get { lock (_lock) return _resume is null ? 0 : Host.Seats.Count(s => s.Connection is null); } }

    /// <summary>Something changed the game outside the players' inputs (a test's hook, an admin's action): the successor gets a fresh savestate and does not replay across it.</summary>
    public void MigrationBarrier() { lock (_lock) if (_mig is not null) { _mig.Barrier = true; _mig.Dirty = true; } }

    private static ulong Token() => (ulong)Random.Shared.NextInt64() ^ (ulong)Random.Shared.NextInt64() << 1;

    private void ReceiveMigration(NetPeer peer, Seat s, MsgType type, ref NetReader r)
    {
        if (type != MsgType.HostCandidate || _mig is null) return;
        _mig.Candidates[peer.Id] = HostCandidate.Read(ref r);
        _mig.Told.Remove(peer.Id);
    }

    /// <summary>After each tick: the successor named and told, its stream sent; on a game taken over, the players' return.</summary>
    private void MigrationTick()
    {
        ResumeTick();
        if (_mig is not { } m) return;
        double now = Now;
        if (!ReferenceEquals(m.Host, Host)) { m.Host = Host; m.Barrier = m.Dirty = true; m.LastTick = Host.Tick; m.Logged.Clear(); m.Full = true; m.State = null; m.Told.Clear(); }
        var sig = string.Join(",", Host.Seats.Select(x => $"{x.Slot}:{x.CarSerial}:{(x.Connection as NetPeer)?.Id}"));
        if (sig != m.Sig) { m.Sig = sig; m.Barrier = m.Dirty = true; }
        foreach (var x in Host.Seats) if (!m.SeatTokens.ContainsKey(x.Slot)) m.SeatTokens[x.Slot] = Token();
        foreach (var gone in m.SeatTokens.Keys.Where(k => Host.Seats.All(x => x.Slot != k)).ToList()) m.SeatTokens.Remove(gone);
        foreach (var gone in m.Candidates.Keys.Where(k => !_byPeer.ContainsKey(k) && !_waiting.ContainsKey(k)).ToList()) m.Candidates.Remove(gone);

        // the lowest seat that can host, bar the host's own (the original's rule: the lowest remaining player id)
        var next = _byPeer.Where(kv => kv.Value.Slot != m.Owner && m.Candidates.TryGetValue(kv.Key, out var c) && c.CanHost)
            .OrderBy(kv => kv.Value.Slot).Select(kv => kv.Value).FirstOrDefault();
        var peer = next?.Connection as NetPeer;
        if (!ReferenceEquals(peer, m.Successor))
        {
            m.Successor = peer;
            m.Told.Clear();
            m.State = null;
            m.Logged.Clear();
            m.Full = m.Dirty = true;
            Log?.Invoke(next is null ? "host migration: nobody can take over" : $"host migration: {next.Name} (slot {next.Slot}) takes over if the host leaves");
        }
        foreach (var (id, x) in _byPeer) if (m.Told.Add(id)) TellSuccessor(m, (NetPeer)x.Connection!, m.SeatTokens.GetValueOrDefault(x.Slot));
        // the joins waiting for the host's answer hear it too: they wait for the new host's instead
        foreach (var k in m.InviteTokens.Keys.Where(k => _invites.All(i => i.Peer.Id != k)).ToList()) m.InviteTokens.Remove(k);
        foreach (var i in _invites)
        {
            if (!m.InviteTokens.ContainsKey(i.Peer.Id)) { m.InviteTokens[i.Peer.Id] = Token(); m.Dirty = true; }
            if (m.Told.Add(i.Peer.Id)) TellSuccessor(m, i.Peer, m.InviteTokens[i.Peer.Id]);
        }
        if (peer is null || next is null) { m.LastTick = Host.Tick; m.Barrier = false; return; }

        float ts = Host.Session.TickSeconds;
        long before = m.Bytes;
        if (Host.Tick != m.LastTick)
        {
            // the tick's inputs as the session applied them (each driver's): only the slots that changed, unless in full
            var changed = new List<(byte, CarInput)>();
            foreach (var d in Host.Session.Drivers)
                if (m.Full || !m.Logged.TryGetValue((byte)d.Slot, out var was) || was != d.Input) { changed.Add(((byte)d.Slot, d.Input)); m.Logged[(byte)d.Slot] = d.Input; }
            if (m.Full) foreach (var k in m.Logged.Keys.Where(k => Host.Session.Drivers.All(d => d.Slot != k)).ToList()) m.Logged.Remove(k);
            _msg.Clear();
            MigrationBuffer.WriteLog(_msg, Host.Tick, m.Barrier, m.Full, changed);
            peer.Send(_msg.Span, HostMigration.Channel, DeliveryMethod.ReliableOrdered);
            m.Bytes += _msg.Length;
            m.LastTick = Host.Tick;
            m.Barrier = m.Full = false;
        }
        if (m.State is null && (m.Dirty || now - m.StateAt >= HostMigration.StateSeconds))
        {
            try { m.State = Host.Session.Snapshot(MigrationExtras(m)); }
            catch (Exception ex) when (ex is InvalidOperationException or I76.Sim.Save.SnapshotFormatException)
            {
                // a savestate that cannot be written costs the successor one state, not the game its host: tried again soon
                Log?.Invoke($"host migration: no savestate this time ({ex.Message})");
                m.StateAt = now - HostMigration.StateSeconds + 0.25;
                m.Dirty = false;
                return;
            }
            m.StateTick = Host.Tick;
            m.StateId++;
            m.Sent = 0;
            m.StateAt = now;
            m.Dirty = false;
            m.Credit = Math.Max(m.Credit, HostMigration.StateChunk);
        }
        if (m.State is { } st)
        {
            // below the game's own traffic: its own rate, and less with an upload limit (a quarter of the successor's share)
            double rate = HostMigration.StateBytesPerSecond;
            if (Options.UploadBytesPerSecond > 0) rate = Math.Clamp(Options.UploadBytesPerSecond / (double)Math.Max(1, _byPeer.Count) / 4, 4096, rate);
            m.Credit = Math.Min(m.Credit + rate * ts, 4 * HostMigration.StateChunk);
            while (m.Sent < st.Length && m.Credit >= Math.Min(HostMigration.StateChunk, st.Length - m.Sent)
                   && peer.GetPacketsCountInReliableQueue(HostMigration.Channel, true) < 24)
            {
                int n = Math.Min(HostMigration.StateChunk, st.Length - m.Sent);
                _msg.Clear();
                MigrationBuffer.WriteStatePiece(_msg, m.StateId, m.StateTick, st, m.Sent, n);
                peer.Send(_msg.Span, HostMigration.Channel, DeliveryMethod.ReliableOrdered);
                m.Sent += n;
                m.Credit -= n;
                m.Bytes += _msg.Length;
            }
            if (m.Sent >= st.Length) { m.State = null; m.States++; }
        }
        // the stream counts against the successor's share of the upload, as its events do (its snapshots get the rest)
        next.EventRate += (m.Bytes - before) / ts * Math.Min(1, ts / 2.0);
    }

    private void TellSuccessor(MigrationHostSide m, NetPeer to, ulong token)
    {
        var succ = m.Successor is { } p && _byPeer.TryGetValue(p.Id, out var ss) ? ss : null;
        var eps = new List<IPEndPoint>();
        if (succ is not null)
        {
            eps.Add(new IPEndPoint(m.Successor!.Address, m.Successor.Port));
            if (m.Candidates.TryGetValue(m.Successor.Id, out var c))
                foreach (var a in c.Local) { var ep = new IPEndPoint(a, c.Port); if (!eps.Contains(ep)) eps.Add(ep); }
        }
        string hostName = m.Owner is { } o ? Host.Seats.FirstOrDefault(x => x.Slot == o)?.Name ?? "" : "";
        _msg.Clear();
        new SuccessorInfo(succ is null ? SuccessorInfo.Nobody : (byte)succ.Slot, succ?.Name ?? "", eps, m.GameToken, token, hostName).Write(_msg);
        to.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
    }

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    /// <summary>The host's bytes in the successor's savestate: the game's options and round, the seats' tokens, requests and addresses, the host's own seat, the anet servers it is listed on, the game's own extras.</summary>
    private byte[] MigrationExtras(MigrationHostSide m)
    {
        var w = new NetWriter(1024);
        w.U16(2);       // 2: the join records carry the car's paint
        w.BlobU16(JsonSerializer.SerializeToUtf8Bytes(Options with { Install = "", Port = 0, MapDir = null }, Json));
        w.VarU((uint)Round);
        w.VarI(m.Owner ?? -1);
        HostMigration.WriteU64(w, m.GameToken);
        var seats = Host.Seats.Where(x => x.Connection is NetPeer).ToList();
        w.VarU((uint)seats.Count);
        foreach (var s in seats)
        {
            var peer = (NetPeer)s.Connection!;
            w.U8((byte)s.Slot);
            HostMigration.WriteU64(w, m.SeatTokens.GetValueOrDefault(s.Slot));
            var j = _joined.GetValueOrDefault(peer.Id) ?? new JoinRequest(Protocol.Version, s.Name, s.Actor.CarFile, (byte)s.Actor.Team, []);
            w.Str(j.Name); w.Str(j.Car); w.U8((byte)s.Actor.Team); w.VarU((uint)j.Config.Length); w.Bytes(j.Config);
            w.BlobU16(s.Actor.Car.Paint?.ToBytes() ?? j.Paint ?? []);
            var eps = new List<IPEndPoint> { new(peer.Address, peer.Port) };
            if (m.Candidates.TryGetValue(peer.Id, out var c)) eps.AddRange(c.Local.Select(a => new IPEndPoint(a, c.Port)));
            HostMigration.WriteEndpoints(w, eps);
        }
        var invites = _invites.Where(i => m.InviteTokens.ContainsKey(i.Peer.Id)).ToList();
        w.VarU((uint)invites.Count);
        foreach (var i in invites)
        {
            HostMigration.WriteU64(w, m.InviteTokens[i.Peer.Id]);
            w.Str(i.Request.Name); w.Str(i.Request.Car); w.U8(i.Request.Team); w.VarU((uint)i.Request.Config.Length); w.Bytes(i.Request.Config);
            w.BlobU16(i.Request.Paint ?? []);
            w.F32((float)(Now - i.Since));
        }
        w.U8((byte)_anetServers.Length);
        foreach (var (h, p) in _anetServers) { w.Str(h); w.U16((ushort)p); }
        Host.WriteMigrationExtras(w);
        return w.ToArray();
    }

    /// <summary>What a savestate's extras say of the game (its options, without the install, port or map folder, which are the successor's), and its round.</summary>
    public static (ServerOptions Options, int Round, IReadOnlyList<(string Host, int Port)> Anet) ReadMigrationOptions(byte[] state, string install)
    {
        var x = I76.Sim.Save.SnapshotHeader.Read(state).Host;
        var r = new NetReader(x);
        if (r.U16() != 2) throw new InvalidDataException("a savestate of another host migration format");
        var o = JsonSerializer.Deserialize<ServerOptions>(r.BlobU16(), Json) ?? throw new InvalidDataException("no options");
        int round = (int)r.VarU();
        r.VarI(); HostMigration.ReadU64(ref r);
        for (int n = (int)r.VarU(); n > 0; n--)
        {
            r.U8(); HostMigration.ReadU64(ref r); r.Str(); r.Str(); r.U8(); r.Bytes((int)r.VarU()); r.BlobU16(); HostMigration.ReadEndpoints(ref r);
        }
        for (int n = (int)r.VarU(); n > 0; n--) { HostMigration.ReadU64(ref r); r.Str(); r.Str(); r.U8(); r.Bytes((int)r.VarU()); r.BlobU16(); r.F32(); }
        var anet = new List<(string, int)>();
        for (int n = r.U8(); n > 0; n--) anet.Add((r.Str(), r.U16()));
        return (o with { Install = install }, round, anet);
    }

    /// <summary>
    /// The successor takes over: the game <paramref name="host"/> (built from the savestate's options on this install,
    /// not yet run) is restored from the host's savestate, the host's inputs since replayed, and the game run on
    /// <see cref="MigrationHandoff.Port"/> from a little past the newest tick the successor saw. The players come back to
    /// their seats by their tokens; the host's own seat leaves with it once they are back (or the hold is over).
    /// </summary>
    public static NetMeleeServer Resume(MeleeHost host, MigrationHandoff handoff, Action<string>? log = null, LiteNetLib.Layers.PacketLayerBase? inner = null)
    {
        var x = I76.Sim.Save.SnapshotHeader.Read(handoff.State).Host;
        var r = new NetReader(x);
        if (r.U16() != 2) throw new InvalidDataException("a savestate of another host migration format");
        r.BlobU16();
        int round = (int)r.VarU();
        int owner = r.VarI();
        ulong game = HostMigration.ReadU64(ref r);
        var tokens = new Dictionary<int, ulong>();
        var requests = new Dictionary<int, JoinRequest>();
        var clients = new List<IPEndPoint>();
        for (int n = (int)r.VarU(); n > 0; n--)
        {
            int slot = r.U8();
            tokens[slot] = HostMigration.ReadU64(ref r);
            string name = r.Str(), car = r.Str();
            byte team = r.U8();
            var config = r.Bytes((int)r.VarU()).ToArray();
            var paint = r.BlobU16();
            requests[slot] = new JoinRequest(Protocol.Version, name, car, team, config, "", paint.Length > 0 ? paint : null);
            clients.AddRange(HostMigration.ReadEndpoints(ref r));
        }
        var invites = new Dictionary<ulong, (JoinRequest Request, double Age)>();
        for (int n = (int)r.VarU(); n > 0; n--)
        {
            ulong token = HostMigration.ReadU64(ref r);
            string name = r.Str(), car = r.Str();
            byte team = r.U8();
            var config = r.Bytes((int)r.VarU()).ToArray();
            var paint = r.BlobU16();
            invites[token] = (new JoinRequest(Protocol.Version, name, car, team, config, "", paint.Length > 0 ? paint : null), r.F32());
        }
        for (int n = r.U8(); n > 0; n--) { r.Str(); r.U16(); }
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var res = host.RestoreMigrated(handoff.State, requests.ToDictionary(kv => kv.Key, kv => kv.Value.Name));
        host.ReadMigrationExtras(ref r);
        // the movie the clients are playing (it began before the savestate, or the replay never reached it): it goes on under the new host
        if (handoff.Movie is { } movie && host.Mode is CoopMode co) co.ResumeMovie(movie);
        uint saved = host.Tick;
        uint upTo = Math.Max(handoff.NewestSeen, saved) + (uint)host.Session.Rate.TicksFor(HostMigration.ResumeMarginSeconds);
        var (replayed, held) = host.ReplayMigrated(handoff.Log, upTo);
        log?.Invoke($"host migration: restored tick {saved} ({res.Created.Count} cars made, {res.Dropped.Count} dropped), replayed {replayed} ticks of the host's inputs, held {held} to tick {host.Tick}, in {t0.Elapsed.TotalMilliseconds:0} ms");
        // the invites have waited on through the ticks since the savestate
        double since = (host.Tick - saved) * host.Session.TickSeconds;
        foreach (var k in invites.Keys.ToList()) invites[k] = (invites[k].Request, invites[k].Age + since);
        var server = new NetMeleeServer(host, inner, handoff.Port) { Round = round };
        if (log is not null) server.Log += log;
        server._resume = new ResumeSide
        {
            Tokens = tokens, Requests = requests, Clients = clients, Since = Now, Owner = owner >= 0 ? owner : null, GameToken = game, Invites = invites,
        };
        if (server._mig is { } m) { m.GameToken = Token(); m.Owner = handoff.Slot; }
        host.HostSlot = handoff.Slot;
        return server;
    }

    /// <summary>A rejoin's request: a seat of the game taken over, not yet back, with its token. Anything else is refused.</summary>
    private void RequestRejoin(ConnectionRequest req, JoinRequest j)
    {
        var (slot, token) = j.Rejoin!.Value;
        if (slot == SuccessorInfo.Nobody && _resume is { } ri && ri.Invites.Remove(token, out var inv))
        {
            // a join that was waiting for the old host's answer: it waits for this host's, keeping its place in time
            ri.PendingInvites[req.Accept().Id] = (j with { Rejoin = null }, inv.Age);
            return;
        }
        var seat = _resume is { } rs && rs.Tokens.TryGetValue(slot, out var want) && want == token
            ? Host.Seats.FirstOrDefault(s => s.Slot == slot && s.Connection is null) : null;
        if (seat is null || _resume!.Pending.ContainsValue(seat))
        {
            var w = new NetDataWriter();
            w.Put("no such seat to come back to");
            Log?.Invoke($"refused a rejoin from {req.RemoteEndPoint} for slot {slot}");
            req.Reject(w);
            return;
        }
        var peer = req.Accept();
        _resume.Pending[peer.Id] = seat;
    }

    private bool BindRejoin(NetPeer peer)
    {
        if (_resume is { } ri && ri.PendingInvites.Remove(peer.Id, out var inv))
        {
            _invites.Add(new Invite(peer, inv.Request, Now - inv.Age));
            _msg.Clear();
            _msg.U8((byte)MsgType.JoinWait);
            peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
            Log?.Invoke($"{inv.Request.Name} asks to join again (invite only, waiting {inv.Age:0} s already): waiting for the host's answer");
            if (_invites.Count == 1) InviteAsked?.Invoke(inv.Request.Name);
            return true;
        }
        if (_resume is not { } rs || !rs.Pending.Remove(peer.Id, out var seat)) return false;
        var s = Host.Bind(seat, peer);
        _byPeer[peer.Id] = s;
        _joined[peer.Id] = rs.Requests.GetValueOrDefault(s.Slot) ?? new JoinRequest(Protocol.Version, s.Name, s.Actor.CarFile, (byte)s.Actor.Team, []);
        if (_mig is not null) _mig.Told.Remove(peer.Id);
        _msg.Clear();
        Host.AcceptFor(s).Write(_msg);
        peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
        Log?.Invoke($"{s.Name} is back in slot {s.Slot} from {peer}");
        return true;
    }

    /// <summary>The restored game waits for its players (all back, or <see cref="HostMigration.HoldSeconds"/>).</summary>
    private bool HoldForRejoins()
    {
        if (_resume is not { Released: false } rs) return false;
        bool all = Host.Seats.All(s => s.Connection is not null || s.Slot == rs.Owner);
        if (!all && Now - rs.Since < HostMigration.HoldSeconds) return true;
        rs.Released = true;
        // the old host's own player leaves with it, as any leaving player (one call, the leave rule's)
        if (rs.Owner is { } o && Host.Seats.FirstOrDefault(s => s.Slot == o && s.Connection is null) is { } gone) Host.Leave(gone, "left");
        Log?.Invoke($"host migration: the game goes on, {Host.Seats.Count(s => s.Connection is not null)} players back after {Now - rs.Since:0.00} s");
        return false;
    }

    /// <summary>The successor probes the players' addresses for a while (opening its side of their NATs), and gives up the seats nobody came back to.</summary>
    private void ResumeTick()
    {
        if (_resume is not { } rs) return;
        double now = Now;
        if (now - rs.Since < HostMigration.RejoinSeconds)
        {
            if (now - rs.ProbeAt >= HostMigration.ProbeSeconds)
            {
                rs.ProbeAt = now;
                var probe = HostMigration.Probe(HostMigration.ProbeKind.Server, rs.GameToken);
                foreach (var ep in rs.Clients) _net.SendUnconnectedMessage(probe, ep);
            }
            return;
        }
        foreach (var s in Host.Seats.Where(s => s.Connection is null).ToList()) Host.Leave(s, "did not come back after the host left");
        _resume = null;
    }

    /// <summary>A player's probe for the game it is looking for: answered (the token is the game's).</summary>
    private bool MigrationProbe(IPEndPoint from, ReadOnlySpan<byte> d)
    {
        if (!HostMigration.TryReadProbe(d, out var kind, out ulong token)) return false;
        if (_resume is { } rs && kind == HostMigration.ProbeKind.Client && token == rs.GameToken)
            _net.SendUnconnectedMessage(HostMigration.Probe(HostMigration.ProbeKind.Answer, token), from);
        return true;
    }
}
