using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using I76.Sim.Session;

namespace I76.Net;

/// <summary>
/// Host migration (docs/multiplayer.md "Host migration"; protocol 13): when the host of a client-hosted game leaves or
/// drops, the lowest seat that can host takes over and the others reconnect to it, keeping their seats. The original did
/// the same in its own way (docs/deviations.md): the lowest remaining player id became the host ("*** %s is now the
/// host!"). The host keeps one successor in step with a savestate every <see cref="StateSeconds"/> and the inputs of every
/// tick since; the successor restores it, replays the inputs and listens on its game port; the others probe it there and
/// rejoin with their seat's token.
/// </summary>
public static class HostMigration
{
    /// <summary>The disconnect's reason when a host that hands over quits: the clients go to the successor at once.</summary>
    public const string HostLeavingReason = "the host left";
    /// <summary>Without a word from the host for this long, the clients take it as gone.</summary>
    public const double HostTimeoutSeconds = 3;
    /// <summary>A migration that has not seated the client again after this long ends the game for it, as a lost connection did.</summary>
    public const double GiveUpSeconds = 10;
    /// <summary>How often the successor gets a fresh savestate (and at once after a join, a leave or a new round).</summary>
    public const double StateSeconds = 1.5;
    /// <summary>The savestate's pieces, and the most of them a second (it rides below the game's own traffic).</summary>
    public const int StateChunk = 1100, StateBytesPerSecond = 24 * 1024;
    /// <summary>The restored game waits this long at most for its players to come back before it runs again.</summary>
    public const double HoldSeconds = 3;
    /// <summary>A seat nobody has come back to after this long is given up, as a player leaving.</summary>
    public const double RejoinSeconds = 10;
    /// <summary>The restored game starts this far past the newest tick the successor had seen (no client sees a tick twice).</summary>
    public const float ResumeMarginSeconds = 0.3f;
    /// <summary>How often a client probes the successor's addresses (and the successor the clients').</summary>
    public const double ProbeSeconds = 0.2;
    /// <summary>The channel the savestate and the inputs go on: the custom map's, which only clients not yet seated use.</summary>
    public const byte Channel = MapTransferLimits.Channel;

    private static readonly byte[] Magic = "I26M"u8.ToArray();
    public enum ProbeKind : byte { Client = 0, Answer = 1, Server = 2 }
    public const int ProbeSize = 4 + 1 + 8;

    /// <summary>The probe datagram (an unconnected message): "I26M", the kind, the game's migration token.</summary>
    public static byte[] Probe(ProbeKind kind, ulong token)
    {
        var b = new byte[ProbeSize];
        Magic.CopyTo(b, 0);
        b[4] = (byte)kind;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(5), token);
        return b;
    }

    public static bool TryReadProbe(ReadOnlySpan<byte> d, out ProbeKind kind, out ulong token)
    {
        kind = 0; token = 0;
        if (d.Length != ProbeSize || !d[..4].SequenceEqual(Magic) || d[4] > (byte)ProbeKind.Server) return false;
        kind = (ProbeKind)d[4];
        token = BinaryPrimitives.ReadUInt64LittleEndian(d[5..]);
        return true;
    }

    /// <summary>This machine's own IPv4 addresses (up to four, then the loopback): where players behind the same router, or on this machine, reach it.</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var l = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !l.Contains(a.Address) && l.Count < 4) l.Add(a.Address);
            }
        }
        catch (NetworkInformationException) { }
        l.Add(IPAddress.Loopback);
        return l;
    }

    public static void WriteU64(NetWriter w, ulong v) { w.U32((uint)v); w.U32((uint)(v >> 32)); }
    public static ulong ReadU64(ref NetReader r) => r.U32() | (ulong)r.U32() << 32;

    public static void WriteEndpoints(NetWriter w, IReadOnlyList<IPEndPoint> eps)
    {
        var v4 = eps.Where(e => e.AddressFamily == AddressFamily.InterNetwork || e.Address.IsIPv4MappedToIPv6).Take(8).ToList();
        w.U8((byte)v4.Count);
        foreach (var e in v4) { w.Bytes(e.Address.MapToIPv4().GetAddressBytes()); w.U16((ushort)e.Port); }
    }

    public static List<IPEndPoint> ReadEndpoints(ref NetReader r)
    {
        int n = r.U8();
        if (n > 8) throw new WireException($"{n} addresses");
        var l = new List<IPEndPoint>(n);
        for (int i = 0; i < n; i++) l.Add(new IPEndPoint(new IPAddress(r.Bytes(4)), r.U16()));
        return l;
    }
}

/// <summary>Client → host, reliable (protocol 13): this client can host the game should the host leave, and where its game socket is at home.</summary>
public sealed record HostCandidate(bool CanHost, ushort Port, IReadOnlyList<IPAddress> Local)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.HostCandidate);
        w.Bool(CanHost);
        w.U16(Port);
        w.U8((byte)Math.Min(Local.Count, 5));
        foreach (var a in Local.Take(5)) w.Bytes(a.MapToIPv4().GetAddressBytes());
    }

    public static HostCandidate Read(ref NetReader r)
    {
        bool can = r.Bool();
        ushort port = r.U16();
        int n = r.U8();
        if (n > 5) throw new WireException($"{n} addresses");
        var l = new List<IPAddress>(n);
        for (int i = 0; i < n; i++) l.Add(new IPAddress(r.Bytes(4)));
        return new HostCandidate(can, port, l);
    }
}

/// <summary>
/// Host → every client, reliable ordered (protocol 13): who takes over if the host goes (<see cref="Slot"/>, 255 for
/// nobody), its name and the addresses to find it at (as the host sees it, then its own), the game's migration token
/// (the probes carry it) and the recipient's own seat token (its rejoin carries it). <see cref="HostName"/> is the host's
/// player, for "has left the game".
/// </summary>
public sealed record SuccessorInfo(byte Slot, string Name, IReadOnlyList<IPEndPoint> Endpoints, ulong GameToken, ulong SeatToken, string HostName)
{
    public const byte Nobody = 255;

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.Successor);
        w.U8(Slot);
        w.Str(Name);
        HostMigration.WriteEndpoints(w, Endpoints);
        HostMigration.WriteU64(w, GameToken);
        HostMigration.WriteU64(w, SeatToken);
        w.Str(HostName);
    }

    public static SuccessorInfo Read(ref NetReader r) =>
        new(r.U8(), r.Str(), HostMigration.ReadEndpoints(ref r), HostMigration.ReadU64(ref r), HostMigration.ReadU64(ref r), r.Str());
}

/// <summary>One server tick's inputs as the host applied them (by slot), for the successor's replay; <see cref="Barrier"/>: the game changed outside the inputs before it (a join, a leave, a new round), so a replay stops short of it.</summary>
public sealed record LoggedTick(uint Tick, CarInput[] Inputs, bool Barrier);

/// <summary>
/// What a successor keeps of the host's stream (<see cref="MsgType.MigrationState"/>, <see cref="MsgType.MigrationLog"/>):
/// the last complete savestate and the inputs of every tick after it.
/// </summary>
public sealed class MigrationBuffer
{
    /// <summary>The newest complete savestate (a <see cref="SimSession.Snapshot"/> with the host's extras) and its tick.</summary>
    public byte[]? State { get; private set; }
    public uint StateTick { get; private set; }
    public int States { get; private set; }
    public long Bytes { get; private set; }
    private readonly List<LoggedTick> _log = [];
    private readonly Dictionary<byte, CarInput> _cur = [];
    private uint _id, _tick;
    private byte[]? _building;
    private int _have;

    public IReadOnlyList<LoggedTick> Log => _log;

    /// <summary>Takes a piece of a savestate (the type byte read); true when one has just become complete.</summary>
    public bool AddState(ref NetReader r, int size)
    {
        Bytes += size;
        uint id = r.U32(), tick = r.U32(), total = r.U32(), offset = r.U32();
        if (total > 64 << 20) throw new WireException($"a savestate of {total} bytes");
        var piece = r.Bytes(r.Remaining);
        if (offset == 0) { _id = id; _tick = tick; _building = new byte[total]; _have = 0; }
        if (_building is null || id != _id || offset != _have || offset + piece.Length > total) { _building = null; return false; }
        piece.CopyTo(_building.AsSpan(_have));
        _have += piece.Length;
        if (_have < total) return false;
        State = _building;
        StateTick = _tick;
        States++;
        _building = null;
        _log.RemoveAll(t => t.Tick <= StateTick);
        return true;
    }

    /// <summary>Takes a tick's inputs (the type byte read): bit 0 of the flags a barrier, bit 1 the full list (else the slots that changed).</summary>
    public void AddLog(ref NetReader r, int size)
    {
        Bytes += size;
        uint tick = r.U32();
        byte flags = r.U8();
        int n = r.U8();
        if ((flags & 2) != 0) _cur.Clear();
        for (int i = 0; i < n; i++)
        {
            byte slot = r.U8();
            _cur[slot] = CarInput.Read(r.Bytes(CarInput.Size));
        }
        int max = _cur.Count == 0 ? 0 : _cur.Keys.Max() + 1;
        var inputs = new CarInput[max];
        foreach (var (s, i) in _cur) inputs[s] = i;
        _log.Add(new LoggedTick(tick, inputs, (flags & 1) != 0));
        // nothing older than what the next state will need (a state comes every 1.5 s; 15 s of ticks at most)
        if (_log.Count > 1200) _log.RemoveRange(0, _log.Count - 1200);
    }

    /// <summary>Forgets everything (another successor was chosen, or the stream restarts).</summary>
    public void Clear()
    {
        State = null; _building = null; _log.Clear(); _cur.Clear();
    }

    /// <summary>The host side's encoding of a tick's inputs: the changed slots (or all, <paramref name="full"/>).</summary>
    public static void WriteLog(NetWriter w, uint tick, bool barrier, bool full, IReadOnlyList<(byte Slot, CarInput Input)> inputs)
    {
        w.U8((byte)MsgType.MigrationLog);
        w.U32(tick);
        w.U8((byte)((barrier ? 1 : 0) | (full ? 2 : 0)));
        w.U8((byte)inputs.Count);
        Span<byte> b = stackalloc byte[CarInput.Size];
        foreach (var (slot, i) in inputs) { w.U8(slot); i.Write(b); w.Bytes(b); }
    }

    public static void WriteStatePiece(NetWriter w, uint id, uint tick, byte[] state, int offset, int length)
    {
        w.U8((byte)MsgType.MigrationState);
        w.U32(id);
        w.U32(tick);
        w.U32((uint)state.Length);
        w.U32((uint)offset);
        w.Bytes(state.AsSpan(offset, length));
    }
}

/// <summary>What the successor's game is handed to take over: the savestate, the inputs since, the newest tick it saw from the host, the port to listen on (the one its client used, which the others know) and its own seat.</summary>
public sealed record MigrationHandoff(byte[] State, IReadOnlyList<LoggedTick> Log, uint NewestSeen, int Port, byte Slot, string? Movie = null);

/// <summary>
/// The successor's side of a takeover, which needs the server (I76.Server's <c>MigrationStandby</c>): it may build the
/// arena ahead when a savestate comes, and takes over when the host is gone.
/// </summary>
public interface IHostSuccessor
{
    /// <summary>A complete savestate came (called from <see cref="MeleeClient.Poll"/>).</summary>
    void Prepare(byte[] state);
    /// <summary>Restores the game and starts listening on <see cref="MigrationHandoff.Port"/>; completes when it listens.</summary>
    Task TakeOver(MigrationHandoff handoff);
}
