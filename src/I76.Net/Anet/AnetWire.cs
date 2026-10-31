using System.Buffers.Binary;

namespace I76.Net.Anet;

/// <summary>
/// The anet packets I26 speaks, from the LGPL anet 0.10 source (h/dppkt.h,
/// h/dptab.h, src/dp/dpio.c, src/dp/dptab.c, src/dp/dp2.c; Activision 1995-2001).
/// Every packet starts with a two-byte type, <c>dppt_MAKE('d', X)</c>: the
/// letter 'd' (0x64) first. Multi-byte numbers are little-endian (Intel
/// order) except inside addresses. Only what a lobby client needs is here:
/// the dpio reliable link (SYN, ACK, DATA, FIN, ping, gather), the login,
/// dptab table items, INDIRECT_JOIN and REQUEST_OPEN.
/// </summary>
public static class AnetWire
{
    public const byte Initial = (byte)'d';
    /// <summary>The game server's UDP port: 0x52A1 + anet2's port offset 3 + 1 (README2.txt: "local UDP port 21157").</summary>
    public const int ServerPort = 21157;

    // dpio (h/dppkt.h)
    public const byte Syn = (byte)'Y', Ack = (byte)'U', Data = (byte)'T', Fin = (byte)'F', Gather = (byte)'G', Ping = (byte)'B', Pong = (byte)'C';
    // dp
    public const byte IndirectJoin = (byte)'I', RequestOpen = (byte)'X', Keepalive = (byte)'K';
    // dptab (h/dptab.h)
    public const byte TabDelete = (byte)'*', TabInitial = (byte)'!', TabBody = (byte)'@', TabSmall = (byte)'%', TabSubscribe = (byte)'^';

    /// <summary>The SYN's version byte (dpio_openHdl2: data[0] = 5).</summary>
    public const byte SynVersion = 5;
    /// <summary>dpio_REMCAP_NEWTAB: deletes may come as zero-length small items.</summary>
    public const byte CapNewTab = 1;
    /// <summary>dpio_WINDOWSIZE.</summary>
    public const int Window = 32;
    /// <summary>dpio_MAXLEN_UNRELIABLE 255 less the five-byte reliable header.</summary>
    public const int MaxReliable = 250;
    /// <summary>The ACK's gotAllUpTo_offset meaning "no correction" (a SYN's ack, or one too far to say).</summary>
    public const byte NoOffset = 128;
    /// <summary>dptab_SIZE_DELETE: a small item of this "length" deletes the item.</summary>
    public const byte SmallDelete = 0xff;

    /// <summary>Table keys (h/dp2.h): a species' sessions are [1, species lo, species hi]; a host's own are [2].</summary>
    public const byte KeySessions = 1, KeyMySessions = 2;

    /// <summary>Whether a datagram is anet's: 'd' and a printable type letter.</summary>
    public static bool Looks(ReadOnlySpan<byte> d) => d.Length >= 2 && d[0] == Initial && d[1] is >= 0x20 and < 0x7f;

    public static byte[] Packet(byte type, ReadOnlySpan<byte> body)
    {
        var b = new byte[2 + body.Length];
        b[0] = Initial; b[1] = type;
        body.CopyTo(b.AsSpan(2));
        return b;
    }

    /// <summary>A reliable-window packet (SYN, DATA, FIN): type, u16 pktnum, u8 len, the bytes.</summary>
    public static byte[] Windowed(byte type, ushort pktnum, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxReliable) throw new ArgumentException("too long for a dpio packet");
        var b = new byte[5 + data.Length];
        b[0] = Initial; b[1] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), pktnum);
        b[4] = (byte)data.Length;
        data.CopyTo(b.AsSpan(5));
        return b;
    }

    /// <summary>
    /// A SYN's body (dpio_openHdl2): version 5, the address length 6, the
    /// sender's own address; then, optionally, the address it believes the
    /// peer has, its capabilities and its second address. I26 sends only the
    /// first three, as the pre-1998 clients did: a server compares a given
    /// destination with its own idea of its address and drops the SYN if they
    /// differ (a server behind a 1:1 NAT, as on a cloud host).
    /// </summary>
    public static byte[] SynBody(AnetAddress self, AnetAddress? peer = null, byte caps = 0, AnetAddress? self2 = null)
    {
        int n = peer is null ? 2 + 6 : self2 is null ? 2 + 12 + 1 : 2 + 18 + 1;
        var b = new byte[n];
        b[0] = SynVersion; b[1] = AnetAddress.Size;
        self.Write(b.AsSpan(2));
        if (peer is { } p)
        {
            p.Write(b.AsSpan(8));
            b[14] = caps;
            self2?.Write(b.AsSpan(15));
        }
        return b;
    }

    /// <summary>A SYN's body read: the sender's address, the address it sent to (ours, as it sees it), its capabilities and second address.</summary>
    public readonly record struct SynInfo(AnetAddress Source, AnetAddress? Dest, byte Caps, AnetAddress? Source2);

    public static SynInfo? ReadSyn(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[0] != SynVersion || data[1] != AnetAddress.Size) return null;
        var src = AnetAddress.Read(data[2..]);
        AnetAddress? dest = data.Length >= 14 ? AnetAddress.Read(data[8..]) : null;
        byte caps = data.Length >= 15 ? data[14] : (byte)0;
        AnetAddress? src2 = data.Length >= 21 ? AnetAddress.Read(data[15..]) : null;
        return new SynInfo(src, dest, caps, src2);
    }

    public static byte[] AckPacket(ushort pktnum, byte offset)
    {
        var b = new byte[5];
        b[0] = Initial; b[1] = Ack;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), pktnum);
        b[4] = offset;
        return b;
    }

    /// <summary>The login (dpSetGameServerEx): "e1" and the species, little-endian. Sent reliably; it does not start with 'd'.</summary>
    public static byte[] Login(ushort species) => [(byte)'e', (byte)'1', (byte)species, (byte)(species >> 8)];

    /// <summary>
    /// INDIRECT_JOIN (dpSendIndirectJoin): chunk 1 (joining) and the session's
    /// id, its subkey (the host's address and the karma) in a 12-byte slot.
    /// </summary>
    public static byte[] IndirectJoinPacket(ReadOnlySpan<byte> sessionId, bool joining = true)
    {
        var b = new byte[2 + 1 + 12];
        b[0] = Initial; b[1] = IndirectJoin;
        b[2] = joining ? (byte)1 : (byte)2;
        sessionId[..Math.Min(sessionId.Length, 12)].CopyTo(b.AsSpan(3));
        return b;
    }

    /// <summary>REQUEST_OPEN read: the joiner's two addresses (the one the server sees first, the one it reported second; 10-byte slots).</summary>
    public static (AnetAddress Seen, AnetAddress Reported)? ReadRequestOpen(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 + 2 * AnetAddress.SlotSize - 4 || p[0] != Initial || p[1] != RequestOpen) return null;
        return (AnetAddress.Read(p[2..]), AnetAddress.Read(p[12..]));
    }

    public static byte[] RequestOpenPacket(AnetAddress seen, AnetAddress reported)
    {
        var b = new byte[2 + 2 * AnetAddress.SlotSize];
        b[0] = Initial; b[1] = RequestOpen;
        seen.Write(b.AsSpan(2));
        reported.Write(b.AsSpan(12));
        return b;
    }

    /// <summary>The session table key of a species: [dp_KEY_SESSIONS, lo, hi].</summary>
    public static byte[] SessionsKey(ushort species) => [KeySessions, (byte)species, (byte)(species >> 8)];

    /// <summary>
    /// A dptab small packet (dptab_update_send_small): hops, the subkey
    /// length, the key, then per item its length (255 deletes it), its subkey
    /// and its bytes.
    /// </summary>
    public static byte[] Small(byte hops, ReadOnlySpan<byte> key, IEnumerable<(byte[] Subkey, byte[]? Value)> items)
    {
        var list = items.ToList();
        int subkeyLen = list.Count > 0 ? list[0].Subkey.Length : 0;
        var b = new List<byte> { Initial, TabSmall, hops, (byte)subkeyLen, (byte)key.Length };
        b.AddRange(key.ToArray());
        foreach (var (sk, v) in list)
        {
            if (sk.Length != subkeyLen) throw new ArgumentException("one small packet holds one subkey length");
            b.Add(v is null ? SmallDelete : (byte)v.Length);
            b.AddRange(sk);
            if (v is not null) b.AddRange(v);
        }
        if (b.Count > MaxReliable) throw new ArgumentException("too long for one small packet");
        return [.. b];
    }

    public static byte[] Subscribe(ReadOnlySpan<byte> key)
    {
        var b = new byte[3 + key.Length];
        b[0] = Initial; b[1] = TabSubscribe; b[2] = (byte)key.Length;
        key.CopyTo(b.AsSpan(3));
        return b;
    }

    /// <summary>An initial (first piece of a large item) packet, as dptab_update_send_large writes it (32-bit len and crc).</summary>
    public static byte[] InitialPacket(byte xferId, byte hops, ReadOnlySpan<byte> key, ReadOnlySpan<byte> subkey, ReadOnlySpan<byte> value, int first)
    {
        var b = new byte[2 + 12 + key.Length + subkey.Length + first];
        b[0] = Initial; b[1] = TabInitial;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), value.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), Crc32(value));
        b[10] = xferId; b[11] = (byte)key.Length; b[12] = (byte)subkey.Length; b[13] = hops;
        key.CopyTo(b.AsSpan(14));
        subkey.CopyTo(b.AsSpan(14 + key.Length));
        value[..first].CopyTo(b.AsSpan(14 + key.Length + subkey.Length));
        return b;
    }

    /// <summary>
    /// dp_crc32 (demo/utils/dpcrc32.c): CRC-32 with the polynomial 0x04C11DB7
    /// shifted most significant bit first, preset and complemented (the BZIP2
    /// variant, not the reflected zip one).
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (byte x in data) crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ x];
        return ~crc;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int j = 0; j < 8; j++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04c11db7 : c << 1;
            t[i] = c;
        }
        return t;
    }
}

/// <summary>One change to a replicated table: an item set (Value) or deleted (Value null).</summary>
public sealed record TableChange(byte[] Key, byte[] Subkey, byte[]? Value);

/// <summary>
/// Reassembles the dptab packets a peer sends (dptab_handlePacket): small
/// bundles, large items in an initial piece and body pieces checked by their
/// CRC, and deletes. One per link, since transfer ids are per sender.
/// </summary>
public sealed class DptabReader
{
    private sealed class Xfer { public byte[] Key = [], Subkey = []; public byte[] Buf = []; public int At; public uint Crc; }
    private readonly Dictionary<byte, Xfer> _xfers = [];
    public int Bad { get; private set; }

    /// <summary>The changes in one reliable payload, or none if it is not a dptab packet (or a broken one).</summary>
    public List<TableChange> Read(ReadOnlySpan<byte> p)
    {
        var o = new List<TableChange>();
        if (p.Length < 3 || p[0] != AnetWire.Initial) return o;
        try
        {
            switch (p[1])
            {
                case AnetWire.TabSmall:
                {
                    int hopsAt = 2;
                    int subLen = p[hopsAt + 1], keyLen = p[hopsAt + 2];
                    int at = hopsAt + 3;
                    var key = p.Slice(at, keyLen).ToArray();
                    at += keyLen;
                    while (p.Length - at > subLen)
                    {
                        int len = p[at++];
                        bool del = len == AnetWire.SmallDelete;
                        if (del) len = 0;
                        if (at + subLen + len > p.Length) { Bad++; break; }
                        var sk = p.Slice(at, subLen).ToArray();
                        at += subLen;
                        o.Add(new TableChange(key, sk, del ? null : p.Slice(at, len).ToArray()));
                        at += len;
                    }
                    break;
                }
                case AnetWire.TabInitial:
                {
                    int len = BinaryPrimitives.ReadInt32LittleEndian(p[2..]);
                    uint crc = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
                    byte id = p[10];
                    int keyLen = p[11], subLen = p[12];
                    int at = 14;
                    if (len <= 0 || len > 65536 || at + keyLen + subLen > p.Length) { Bad++; break; }
                    var x = new Xfer { Key = p.Slice(at, keyLen).ToArray(), Subkey = p.Slice(at + keyLen, subLen).ToArray(), Buf = new byte[len], Crc = crc };
                    at += keyLen + subLen;
                    int n = Math.Min(len, p.Length - at);
                    p.Slice(at, n).CopyTo(x.Buf);
                    x.At = n;
                    _xfers[id] = x;
                    Finish(id, x, o);
                    break;
                }
                case AnetWire.TabBody:
                {
                    if (!_xfers.TryGetValue(p[2], out var x)) break;
                    int n = Math.Min(p.Length - 3, x.Buf.Length - x.At);
                    p.Slice(3, n).CopyTo(x.Buf.AsSpan(x.At));
                    x.At += n;
                    Finish(p[2], x, o);
                    break;
                }
                case AnetWire.TabDelete:
                {
                    int keyLen = p[2], subLen = p[3];
                    if (4 + keyLen + subLen > p.Length) { Bad++; break; }
                    o.Add(new TableChange(p.Slice(4, keyLen).ToArray(), p.Slice(4 + keyLen, subLen).ToArray(), null));
                    break;
                }
            }
        }
        catch (ArgumentOutOfRangeException) { Bad++; }
        catch (IndexOutOfRangeException) { Bad++; }
        return o;
    }

    private void Finish(byte id, Xfer x, List<TableChange> o)
    {
        if (x.At < x.Buf.Length) return;
        _xfers.Remove(id);
        if (AnetWire.Crc32(x.Buf) != x.Crc) { Bad++; return; }
        o.Add(new TableChange(x.Key, x.Subkey, x.Buf));
    }
}
