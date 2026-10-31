using System.Buffers.Binary;

namespace I76.Net.Anet;

/// <summary>
/// One dpio reliable connection (src/dp/dpio.c), the subset a lobby client
/// needs; symmetric, so the tests' fake server uses it too. Each side opens
/// its half with a SYN carrying a random packet number; DATA packets follow
/// from that number plus one, at most <see cref="AnetWire.Window"/>
/// unacknowledged, retransmitted with a doubling timeout until acked. An ACK
/// names the packet it answers and, as an offset, the oldest one still
/// missing (so lost ACKs are made up). A peer's SYN tells us the address it
/// sends to: ours as it sees it, behind a NAT the public one. Three-byte
/// pings ('dB' n, answered 'dC' n) keep the link and the NAT mapping alive
/// and measure the round trip. Gathered packets ('dG') are taken apart.
/// Not thread-safe: the owner feeds it datagrams and calls
/// <see cref="Update"/> from one thread.
/// </summary>
public sealed class DpioLink
{
    private sealed class Out { public required byte[] Packet; public int Tries; public double FirstSent; public bool Acked; }

    private readonly Action<byte[]> _send;
    private readonly Out?[] _tx = new Out?[AnetWire.Window];
    private readonly Queue<byte[]> _waiting = new();
    private readonly byte[]?[] _rx = new byte[]?[AnetWire.Window];
    private ushort _txBase, _txNext, _synNum, _rxBase, _peerSyn;
    private bool _synAcked, _synReceived;
    private double _rto = InitialRto, _nextRetx, _lastRx, _lastPing, _opened;
    private byte _pingSeq;
    private readonly Dictionary<byte, double> _pingSent = [];

    /// <summary>First retransmission timeout (dpio starts from a 2 s guess; a LAN or loopback link wants less).</summary>
    public const double InitialRto = 0.5, MaxRto = 4.1;
    /// <summary>A ping every 5 s (dpio's keepalive interval is the 41 s timeout / 8).</summary>
    public double PingInterval { get; init; } = 5;
    /// <summary>Silence this long and the link is dead (dpio: 41 s; we give up sooner, the server keeps us 41).</summary>
    public double Timeout { get; init; } = 30;
    /// <summary>How long the handshake may take.</summary>
    public double ConnectTimeout { get; init; } = 6;
    /// <summary>dpio_MAX_RETRIES.</summary>
    public const int MaxTries = 17;

    /// <summary>The address the peer says it has (its SYN), and the one it says we have (null until its SYN comes, or if it gave none).</summary>
    public AnetAddress? PeerSays { get; private set; }
    public AnetAddress? PeerSays2 { get; private set; }
    public AnetAddress? SeenAs { get; private set; }
    public byte PeerCaps { get; private set; }
    /// <summary>Both halves open: data can flow.</summary>
    public bool Established => _synAcked && _synReceived;
    public bool Closed { get; private set; }
    /// <summary>Why it closed, as a phrase after the peer's name ("is not answering", "stopped answering", "closed the connection").</summary>
    public string? CloseReason { get; private set; }
    /// <summary>The smoothed round trip, seconds (0 before the first measurement).</summary>
    public double Rtt { get; private set; }
    public double LastHeard => _lastRx;
    /// <summary>Reliable payloads received, in order, not yet taken.</summary>
    public Queue<byte[]> Delivered { get; } = new();
    public long Retransmits { get; private set; }

    public DpioLink(Action<byte[]> send) => _send = send;

    /// <summary>Opens our half: the SYN, with a random first packet number.</summary>
    public void Open(double now, byte[] synBody, ushort? first = null)
    {
        _opened = _lastRx = _lastPing = now;
        _synNum = first ?? (ushort)Random.Shared.Next(65536);
        _txBase = _txNext = _synNum;
        Queue(AnetWire.Windowed(AnetWire.Syn, _txNext, synBody), now);
    }

    /// <summary>Queues a payload to go reliably (held until the link is open).</summary>
    public void Send(byte[] payload, double now)
    {
        if (Closed) return;
        _waiting.Enqueue(payload);
        Pump(now);
    }

    /// <summary>Closes our half with a FIN (dpio_closeHdl: "FIN" as its data); the link is closed at once on our side.</summary>
    public void Close(double now, string reason = "closed")
    {
        if (Closed) return;
        if (_synAcked && Free > 0)
        {
            var fin = AnetWire.Windowed(AnetWire.Fin, _txNext++, "FIN"u8);
            _send(fin);
        }
        Closed = true;
        CloseReason = reason;
    }

    private int Free => AnetWire.Window - (ushort)(_txNext - _txBase);

    private void Pump(double now)
    {
        while (_synAcked && _waiting.Count > 0 && Free > 0)
            Queue(AnetWire.Windowed(AnetWire.Data, _txNext, _waiting.Dequeue()), now);
    }

    private void Queue(byte[] packet, double now)
    {
        if ((ushort)(_txNext - _txBase) == 0) _nextRetx = now + _rto;
        _tx[_txNext % AnetWire.Window] = new Out { Packet = packet, Tries = 1, FirstSent = now };
        _txNext++;
        _send(packet);
    }

    /// <summary>One datagram from the peer (already known to be anet's).</summary>
    public void Receive(ReadOnlySpan<byte> d, double now)
    {
        if (Closed || !AnetWire.Looks(d)) return;
        _lastRx = now;
        switch (d[1])
        {
            case AnetWire.Gather:
                for (int at = 2; at < d.Length;)
                {
                    int n = d[at++];
                    if (at + n > d.Length) break;
                    Receive(d.Slice(at, n), now);
                    at += n;
                }
                break;
            case AnetWire.Ack when d.Length >= 4:
                GotAck(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), d.Length >= 5 ? d[4] : AnetWire.NoOffset, now);
                break;
            case AnetWire.Syn when d.Length >= 5:
                GotSyn(d, now);
                break;
            case AnetWire.Data when d.Length >= 5:
                GotData(d, now);
                break;
            case AnetWire.Fin when d.Length >= 5:
                _send(AnetWire.AckPacket(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), AnetWire.NoOffset));
                if (_synReceived) { Closed = true; CloseReason = "closed the connection"; }
                break;
            case AnetWire.Ping when d.Length == 3:
                _send([AnetWire.Initial, AnetWire.Pong, d[2]]);
                break;
            case AnetWire.Pong when d.Length == 3:
                if (_pingSent.Remove(d[2], out var at0)) Sample(now - at0);
                break;
        }
    }

    private void GotSyn(ReadOnlySpan<byte> d, double now)
    {
        ushort num = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        int len = d[4];
        var body = d.Slice(5, Math.Min(len, d.Length - 5));
        if (AnetWire.ReadSyn(body) is not { } syn) return;
        if (_synReceived && num != _peerSyn)
        {
            // a new SYN after data: the peer started over (dpio marks the handle reset)
            Closed = true;
            CloseReason = "reset the connection";
            return;
        }
        if (!_synReceived)
        {
            _synReceived = true;
            _peerSyn = num;
            _rxBase = (ushort)(num + 1);
            PeerSays = syn.Source;
            PeerSays2 = syn.Source2;
            SeenAs = syn.Dest;
            PeerCaps = syn.Caps;
        }
        _send(AnetWire.AckPacket(num, AnetWire.NoOffset));
        Pump(now);
    }

    private void GotData(ReadOnlySpan<byte> d, double now)
    {
        if (!_synReceived) return;      // dpio drops data before the SYN, unacked
        ushort num = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        int len = Math.Min(d[4], d.Length - 5);
        short ahead = (short)(num - _rxBase);
        if (ahead >= AnetWire.Window) return;
        if (ahead >= 0) _rx[num % AnetWire.Window] ??= d.Slice(5, len).ToArray();
        while (_rx[_rxBase % AnetWire.Window] is { } p)
        {
            _rx[_rxBase % AnetWire.Window] = null;
            _rxBase++;
            if (!(p.Length >= 2 && p[0] == AnetWire.Initial && p[1] == AnetWire.Keepalive)) Delivered.Enqueue(p);
        }
        // the ack names this packet and how far before it everything has come (dpio_sendAck)
        short off = (short)(_rxBase - num);
        _send(AnetWire.AckPacket(num, off is < -126 or > 126 ? AnetWire.NoOffset : (byte)(sbyte)off));
    }

    private void GotAck(ushort num, byte offset, double now)
    {
        int used = (ushort)(_txNext - _txBase);
        int at = (ushort)(num - _txBase);
        bool any = false;
        if (at < used && _tx[num % AnetWire.Window] is { Acked: false } o)
        {
            o.Acked = true;
            any = true;
            if (o.Tries == 1) Sample(now - o.FirstSent);
            if (num == _synNum) _synAcked = true;
        }
        if (offset != AnetWire.NoOffset)
        {
            ushort upTo = (ushort)(num + (sbyte)offset);
            int k = (ushort)(upTo - _txBase);
            if (k <= used)
                for (ushort n = _txBase; n != upTo; n++)
                    if (_tx[n % AnetWire.Window] is { Acked: false } m) { m.Acked = true; any = true; if (n == _synNum) _synAcked = true; }
        }
        while (_txBase != _txNext && _tx[_txBase % AnetWire.Window] is { Acked: true })
        {
            _tx[_txBase % AnetWire.Window] = null;
            _txBase++;
        }
        if (any)
        {
            _rto = Math.Clamp(Rtt > 0 ? Rtt * 1.5 + 0.05 : InitialRto, 0.1, MaxRto);
            _nextRetx = now + _rto;
        }
        Pump(now);
    }

    private void Sample(double rtt) => Rtt = Rtt == 0 ? rtt : Rtt * 0.8 + rtt * 0.2;

    /// <summary>Retransmissions, pings and the timeouts.</summary>
    public void Update(double now)
    {
        if (Closed) return;
        if (!Established && now - _opened > ConnectTimeout) { Closed = true; CloseReason = "is not answering"; return; }
        if (now - _lastRx > Timeout) { Closed = true; CloseReason = "stopped answering"; return; }
        if (_txBase != _txNext && now >= _nextRetx)
        {
            for (ushort n = _txBase; n != _txNext; n++)
            {
                if (_tx[n % AnetWire.Window] is not { Acked: false } o) continue;
                if (o.Tries > MaxTries) { Closed = true; CloseReason = "stopped answering"; return; }
                o.Tries++;
                Retransmits++;
                _send(o.Packet);
                if (!_synAcked) break;      // only the SYN until it is acked
            }
            _rto = Math.Min(_rto * 2, MaxRto);
            _nextRetx = now + _rto;
        }
        if (Established && now - _lastPing >= PingInterval)
        {
            _lastPing = now;
            byte s = _pingSeq++;
            _pingSent[s] = now;
            if (_pingSent.Count > 16) _pingSent.Remove(_pingSent.MinBy(kv => kv.Value).Key);
            _send([AnetWire.Initial, AnetWire.Ping, s]);
        }
    }
}
