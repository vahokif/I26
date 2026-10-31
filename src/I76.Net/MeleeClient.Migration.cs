using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace I76.Net;

/// <summary>
/// The client's side of host migration (<see cref="HostMigration"/>, docs/multiplayer.md "Host migration"). In a game whose
/// host has named a successor: the host gone (its "host left" goodbye, or <see cref="HostMigration.HostTimeoutSeconds"/>
/// without a word), the client goes <see cref="ClientState.Migrating"/>; the successor's own client hands its socket to the
/// takeover (<see cref="Standby"/>) and rejoins it over the loopback; the others probe the successor's addresses and rejoin
/// with their seat's token. The rejoin's accept leaves <see cref="Accept"/> as it was (the same arena, slot and car):
/// <see cref="RejoinAccept"/> and <see cref="Migrations"/> tell the mirror to follow. Nothing here acts in a game without a
/// successor (a dedicated server, a listen server without migration).
/// </summary>
public sealed partial class MeleeClient
{
    private JoinRequest? _request;
    private double _lastHeard;
    private MigrationBuffer? _link;
    private double _migrateAt, _probeAt;
    private bool _rejoining;
    private Task? _takeover;
    private int _takeoverPort;

    /// <summary>This client may take over as host (it has the install to build the arena): said to the host at the accept.</summary>
    public bool CanHost { get; set; }
    /// <summary>The takeover when this client is the successor (I76.Server's MigrationStandby); without one it never hosts.</summary>
    public IHostSuccessor? Standby { get; set; }
    /// <summary>The host's latest word on who takes over (null: none named, no migration).</summary>
    public SuccessorInfo? Successor { get; private set; }
    /// <summary>What this client holds of the host's stream while it is the successor.</summary>
    public MigrationBuffer? MigrationLink => _link;
    /// <summary>Migrations this client came through (the mirror follows each), the accept of the last, and when it began and ended (<see cref="Now"/>).</summary>
    public int Migrations { get; private set; }
    public JoinAccept? RejoinAccept { get; private set; }
    public double MigrationBegan { get; private set; }
    public double MigrationEnded { get; private set; }
    /// <summary>This client took over as host in the last migration (its server is the standby's).</summary>
    public bool BecameHost { get; private set; }
    public event Action? HostTakenOver;
    /// <summary>Lines for the feed, in the original's manner ("*** X is now the host!"): the mirror passes them on as the server's chat.</summary>
    public Queue<string> Notices { get; } = new();
    /// <summary>How long a migration may take before the game ends for this client (tests shorten it).</summary>
    public double MigrationGiveUp { get; set; } = HostMigration.GiveUpSeconds;
    public double HostTimeout { get; set; } = HostMigration.HostTimeoutSeconds;
    /// <summary>The addresses at home the host is told of (null: this machine's own, <see cref="HostMigration.LocalAddresses"/>; the tests' NAT gives none).</summary>
    public IReadOnlyList<IPAddress>? HomeAddresses { get; set; }

    private bool HandsOver => Successor is { Slot: not SuccessorInfo.Nobody } && Accept is not null;
    /// <summary>An invite-only join waiting for the host's answer, in a game that hands over: it waits for the new host's instead.</summary>
    private bool Invited => Successor is { Slot: not SuccessorInfo.Nobody } && Accept is null && State == ClientState.Connecting && WaitingForHost;

    private void ResetMigration()
    {
        Successor = null;
        _link = null;
        _lastHeard = Now;
        // the host hears that we could take over, and where we are at home (a server without migration ignores it)
        if (_peer is null || !CanHost || Standby is null) return;
        _w.Clear();
        new HostCandidate(true, (ushort)_net.LocalPort, HomeAddresses ?? HostMigration.LocalAddresses()).Write(_w);
        _peer.Send(_w.Span, DeliveryMethod.ReliableOrdered);
    }

    private void ReceiveMigration(MsgType type, ref NetReader r, int size)
    {
        switch (type)
        {
            case MsgType.Successor when State == ClientState.Joined || State == ClientState.Connecting && WaitingForHost:
                Successor = SuccessorInfo.Read(ref r);
                if (Accept is not null && Successor.Slot == Accept.Slot) _link ??= new MigrationBuffer();
                else _link = null;
                break;
            case MsgType.MigrationState when _link is not null:
                if (_link.AddState(ref r, size))
                {
                    // a savestate this build cannot use is the game's end when the host leaves, not a crash now
                    try { Standby?.Prepare(_link.State!); }
                    catch (Exception e) { Log?.Invoke($"host migration: the savestate cannot be used ({e.Message})"); _link = null; }
                }
                break;
            case MsgType.MigrationLog when _link is not null:
                _link.AddLog(ref r, size);
                break;
        }
    }

    /// <summary>The connection ended: in a game that hands over, the host's goodbye or its silence starts the migration; a refusal or a kick does not.</summary>
    private bool MigrationDisconnect(NetPeer p, DisconnectInfo info)
    {
        if (State == ClientState.Migrating)
        {
            // the old host's connection going (we let it go), or a try at the successor that failed: carry on seeking
            if (ReferenceEquals(p, _peer)) { _peer = null; _rejoining = false; }
            if (ReferenceEquals(p, _oldPeer)) _oldPeer = null;
            return true;
        }
        // a connection that is not ours any more (the quiet old host, let go once the new one seated us): its end ends nothing.
        // (A host that drops rather than quits is still connected when the others rejoin the successor: they all left the game.)
        if (_peer is not null && !ReferenceEquals(p, _peer)) return true;
        if (!(State == ClientState.Joined && HandsOver) && !Invited) return false;
        string? said = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : null;
        if (said is not null && said != HostMigration.HostLeavingReason) return false;
        _peer = null;
        BeginMigration(said ?? info.Reason.ToString());
        return true;
    }

    private void BeginMigration(string why)
    {
        var s = Successor!;
        bool mine = Accept is not null && s.Slot == Accept.Slot;
        Log?.Invoke($"the host is gone ({why}): {(mine ? "this client takes over" : $"{s.Name} takes over")}");
        State = ClientState.Migrating;
        Reason = null;
        MigrationBegan = Now;
        _probeAt = 0;
        _rejoining = false;
        BecameHost = false;
        // a host that went quiet keeps its connection a while: should it speak again before the successor is found (our
        // own link had dropped, not its), the game goes on with it
        _oldPeer = _peer;
        _peer = null;
        if (Accept is null) { WaitingForHost = false; return; }     // an invited join: it only seeks the new host, to wait for it
        // (the host's own "has left the game" comes from the new host, as its car goes)
        Notices.Enqueue(NetLines.Migrating);
        if (!mine) return;
        if (Standby is null || _link?.State is null)
        {
            Log?.Invoke("this client was to take over but has no savestate yet");
            GiveUp("the host left before the game could be handed over");
            return;
        }
        // started from Poll, not from within the socket's events
        _takeoverDue = true;
    }

    private bool _takeoverDue;

    private void StartTakeover()
    {
        _takeoverDue = false;
        _oldPeer = null;
        // the takeover listens on this socket's port (the one the host, and so the others, know): it is let go first
        _takeoverPort = _net.LocalPort;
        _net.Stop();
        try { _takeover = Standby!.TakeOver(new MigrationHandoff(_link!.State!, [.. _link.Log], NewestTick, _takeoverPort, Accept!.Slot, MovieName)); }
        catch (Exception e) { Log?.Invoke($"the takeover failed: {e.Message}"); GiveUp("connection to the host lost (the takeover failed)"); }
    }

    private void PollMigration()
    {
        double now = Now;
        if (State == ClientState.Joined && HandsOver && now - _lastHeard > HostTimeout)
        {
            BeginMigration($"nothing from it for {now - _lastHeard:0.0} s");
            return;
        }
        if (State != ClientState.Migrating) return;
        if (now - MigrationBegan > MigrationGiveUp) { GiveUp("connection to the host lost (no one took over)"); return; }
        if (_takeoverDue) { StartTakeover(); return; }
        if (_takeover is { } t)
        {
            if (!t.IsCompleted) return;
            _takeover = null;
            if (t.IsFaulted || t.IsCanceled)
            {
                Log?.Invoke($"the takeover failed: {t.Exception?.GetBaseException().Message}");
                GiveUp("connection to the host lost (the takeover failed)");
                return;
            }
            BecameHost = true;
            _net.Start();
            Rejoin(new IPEndPoint(IPAddress.Loopback, _takeoverPort));
            HostTakenOver?.Invoke();
            return;
        }
        if (BecameHost || _rejoining || Successor is not { } s || now - _probeAt < HostMigration.ProbeSeconds) return;
        _probeAt = now;
        var probe = HostMigration.Probe(HostMigration.ProbeKind.Client, s.GameToken);
        foreach (var ep in s.Endpoints) _net.SendUnconnectedMessage(probe, ep);
    }

    /// <summary>A probe or its answer: the successor is listening at <paramref name="from"/>.</summary>
    private bool MigrationProbe(IPEndPoint from, ReadOnlySpan<byte> d)
    {
        if (!HostMigration.TryReadProbe(d, out var kind, out ulong token)) return false;
        if (State != ClientState.Migrating || _rejoining || BecameHost || Successor is not { } s || token != s.GameToken || kind == HostMigration.ProbeKind.Client) return true;
        // the successor probing us (opening its side): answered, so our side opens too
        if (kind == HostMigration.ProbeKind.Server) _net.SendUnconnectedMessage(HostMigration.Probe(HostMigration.ProbeKind.Answer, token), from);
        Rejoin(from);
        return true;
    }

    private void Rejoin(IPEndPoint at)
    {
        if (_request is null || Successor is not { } s) return;
        Log?.Invoke($"rejoining at {at}");
        _rejoining = true;
        var nw = new NetWriter();
        (_request with { Version = Protocol.Version, Rejoin = (Accept?.Slot ?? SuccessorInfo.Nobody, s.SeatToken) }).Write(nw);
        var w = new NetDataWriter();
        w.Put(nw.ToArray());
        _net.Connect(at, w);
    }

    /// <summary>The successor seated us again: the same game goes on, its snapshots and events from a new connection.</summary>
    private void Rejoined(JoinAccept a)
    {
        var old = Accept!;
        if (a.Slot != old.Slot || a.CarSerial != old.CarSerial || !string.Equals(a.Arena, old.Arena, StringComparison.OrdinalIgnoreCase))
        {
            GiveUp($"the new host's game is not this one (slot {a.Slot}, car {a.CarSerial}, {a.Arena})");
            return;
        }
        string host = BecameHost ? "" : Successor?.Name ?? "";
        RejoinAccept = a;
        Migrations++;
        MigrationEnded = Now;
        Snapshots.Clear();
        CarAnswers.Clear();
        NewestTick = 0;
        NewestAt = 0;
        AckedInput = 0;
        _slack.Clear();
        ClockScale = 1;
        _parts.Clear();
        _decoder = new SnapshotDecoder(a.Rate);
        // the movie as the new host has it (it may have ended, or never reached it)
        MovieName = null;
        SetMovie(a.Coop?.Movie);
        _names.Clear();
        _board.Clear();
        Array.Clear(_motionHashes);
        _newestHash = 0;
        NeedExact = false;
        _rejoining = false;
        State = ClientState.Joined;
        DropOldHost();
        ResetMigration();
        Notices.Enqueue(NetLines.NewHost(host.Length == 0 ? null : host));
        Log?.Invoke($"rejoined after {MigrationEnded - MigrationBegan:0.00} s: slot {a.Slot}, server tick {a.ServerTick}, {a.Cars.Count} cars");
    }

    private NetPeer? _oldPeer;

    private void DropOldHost()
    {
        if (_oldPeer is { } o) { _oldPeer = null; _net.DisconnectPeerForce(o); }
    }

    /// <summary>
    /// A message on a connection that is not the current one: dropped, unless it is the quiet host speaking again before
    /// anything was done about it (no takeover started, no rejoin under way), when the migration is called off.
    /// </summary>
    private bool IgnoreFrom(NetPeer p)
    {
        if (State == ClientState.Migrating && ReferenceEquals(p, _oldPeer))
        {
            if (_rejoining || BecameHost || _takeover is not null || _takeoverDue) return true;
            Log?.Invoke($"the host is back after {Now - MigrationBegan:0.00} s: the game goes on with it");
            _peer = _oldPeer;
            _oldPeer = null;
            State = ClientState.Joined;
            return false;
        }
        return _peer is not null && !ReferenceEquals(p, _peer);
    }

    private void MigrationJoinWait()
    {
        DropOldHost();
        _rejoining = false;
        Migrations++;
        MigrationEnded = Now;
        Successor = null;
        _link = null;
        _lastHeard = Now;
        Log?.Invoke($"waiting for the new host to let us in, after {MigrationEnded - MigrationBegan:0.00} s");
    }

    private void GiveUp(string why)
    {
        _takeover = null;
        _takeoverDue = false;
        _rejoining = false;
        if (_peer is { } p) { _peer = null; _net.DisconnectPeerForce(p); }
        DropOldHost();
        State = ClientState.Disconnected;
        Reason = why;
        Log?.Invoke($"host migration failed: {why}");
        Disconnected?.Invoke(why);
    }
}
