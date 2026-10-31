using System.Net;
using System.Net.Sockets;

namespace I76.Net.Anet;

/// <summary>
/// An anet Internet address: the IPv4 address and the UDP port, six bytes,
/// both in network byte order (the uudp driver's <c>addr_t</c>,
/// src/linux/uudp/uudps.c). Packets carry it in a ten-byte slot
/// (<c>dp_MAX_ADR_LEN</c>, the IPX size); the last four bytes are padding.
/// </summary>
public readonly record struct AnetAddress(uint Ip, ushort Port)
{
    public const int Size = 6, SlotSize = 10;

    public static AnetAddress Read(ReadOnlySpan<byte> b) =>
        new((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]), (ushort)(b[4] << 8 | b[5]));

    public void Write(Span<byte> b)
    {
        b[0] = (byte)(Ip >> 24); b[1] = (byte)(Ip >> 16); b[2] = (byte)(Ip >> 8); b[3] = (byte)Ip;
        b[4] = (byte)(Port >> 8); b[5] = (byte)Port;
    }

    public byte[] ToBytes() { var b = new byte[Size]; Write(b); return b; }

    public IPEndPoint ToEndPoint() =>
        new(new IPAddress([(byte)(Ip >> 24), (byte)(Ip >> 16), (byte)(Ip >> 8), (byte)Ip]), Port);

    public static AnetAddress From(IPEndPoint ep)
    {
        var a = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        if (a.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException($"anet addresses are IPv4: {ep}");
        var b = a.GetAddressBytes();
        return new((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]), (ushort)ep.Port);
    }

    public bool IsZero => Ip == 0 && Port == 0;

    public override string ToString() => $"{Ip >> 24}.{(Ip >> 16) & 255}.{(Ip >> 8) & 255}.{Ip & 255}:{Port}";

    /// <summary>
    /// The address this machine would send to <paramref name="toward"/> from,
    /// with the game socket's port: what the SYN reports as ours (the
    /// "private" address the server compares with the one it sees). A UDP
    /// connect picks the interface without sending anything.
    /// </summary>
    public static AnetAddress Local(IPAddress toward, int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(toward, 9);
            if (s.LocalEndPoint is IPEndPoint ep) return From(new IPEndPoint(ep.Address, port));
        }
        catch (SocketException) { }
        return From(new IPEndPoint(IPAddress.Loopback, port));
    }
}
