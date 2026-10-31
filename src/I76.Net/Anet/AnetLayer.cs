using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using LiteNetLib.Layers;

namespace I76.Net.Anet;

/// <summary>Sends and receives anet datagrams on some UDP socket.</summary>
public interface IAnetTransport
{
    /// <summary>The socket's local port (the private address's port).</summary>
    int LocalPort { get; }
    void Send(IPEndPoint to, ReadOnlySpan<byte> datagram);
    /// <summary>From now on datagrams from <paramref name="server"/> that look like anet's go into the inbox instead of the game.</summary>
    ConcurrentQueue<byte[]> Listen(IPEndPoint server);
    void Unlisten(IPEndPoint server);
}

/// <summary>
/// Puts anet's lobby traffic on the game's own LiteNetLib socket, so the NAT
/// mapping the anet server sees, and tells a host or joiner about, is the one
/// the game uses (the whole point of the server's introduction).
/// <para>
/// Inbound, a datagram is anet's if it comes from a server being talked to
/// and starts with 'd' and a letter; it is taken out before LiteNetLib reads
/// it. The source decides, not the first byte alone: LiteNetLib's first byte
/// is the packet property in its low five bits, the connection number in
/// bits 5-6 and the fragment flag in bit 7, and 0x64 ('d') is a Ping
/// (property 4) of connection number 3, which LiteNetLib does send.
/// </para>
/// <para>
/// Outbound, LiteNetLib 2.1.4 has no public raw send; anet datagrams go out
/// as unconnected messages and this layer strips LiteNetLib's one-byte header
/// again on the way out (a thread-static flag set around the send marks them:
/// SendUnconnectedMessage calls the layer synchronously).
/// </para>
/// An optional inner layer runs below this one (the tests' NAT emulator).
/// </summary>
public sealed class AnetLayer : PacketLayerBase
{
    private readonly PacketLayerBase? _inner;
    private readonly ConcurrentDictionary<IPEndPoint, ConcurrentQueue<byte[]>> _servers = new();
    [ThreadStatic] private static bool _raw;

    public AnetLayer(PacketLayerBase? inner = null) : base(inner?.ExtraPacketSizeForLayer ?? 0) => _inner = inner;

    /// <summary>Anet datagrams taken out so far (diagnostics).</summary>
    public long Taken;

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        _inner?.ProcessInboundPacket(ref endPoint, ref data, ref length);
        if (length < 2 || data[0] != AnetWire.Initial || data[1] is < 0x20 or >= 0x7f) return;
        if (!_servers.TryGetValue(Normal(endPoint), out var inbox)) return;
        inbox.Enqueue(data.AsSpan(0, length).ToArray());
        Interlocked.Increment(ref Taken);
        length = 0;
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (_raw && length > 1)
        {
            // drop LiteNetLib's unconnected-message header: the datagram is anet's own bytes
            offset++;
            length--;
        }
        _inner?.ProcessOutBoundPacket(ref endPoint, ref data, ref offset, ref length);
    }

    internal static IPEndPoint Normal(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : new IPEndPoint(ep.Address, ep.Port);

    internal ConcurrentQueue<byte[]> Listen(IPEndPoint server) => _servers.GetOrAdd(Normal(server), _ => new ConcurrentQueue<byte[]>());
    internal void Unlisten(IPEndPoint server) => _servers.TryRemove(Normal(server), out _);

    internal static void SendRaw(NetManager net, IPEndPoint to, ReadOnlySpan<byte> datagram)
    {
        _raw = true;
        try { net.SendUnconnectedMessage(datagram, to); }
        finally { _raw = false; }
    }
}

/// <summary>An anet transport on a LiteNetLib manager built with an <see cref="AnetLayer"/>.</summary>
public sealed class LiteNetAnetTransport(NetManager net, AnetLayer layer) : IAnetTransport
{
    public int LocalPort => net.LocalPort;
    public void Send(IPEndPoint to, ReadOnlySpan<byte> datagram) => AnetLayer.SendRaw(net, to, datagram);
    public ConcurrentQueue<byte[]> Listen(IPEndPoint server) => layer.Listen(server);
    public void Unlisten(IPEndPoint server) => layer.Unlisten(server);
}
