using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using I76.Net.Anet;
using LiteNetLib.Layers;

namespace I76.Net.Tests.Anet;

/// <summary>
/// A NAT for tests, without root: the machines "behind" it reach the world
/// only through it. Each one's LiteNetLib manager carries a
/// <see cref="NatTunnelLayer"/> that sends every datagram, with its real
/// destination in front, to the box's inside socket, and takes datagrams only
/// from there. The box sends them on from a public socket on its own
/// loopback address (127.0.0.N): one per inside machine
/// (<see cref="Mapping.EndpointIndependent"/>, the "party line" NATs anet's
/// README2 asks for) or one per inside machine and destination
/// (<see cref="Mapping.Symmetric"/>, at random ports; <see cref="Mapping.SymmetricSequential"/>, at
/// consecutive ports, as many home and carrier NATs hand them out). Filtering is address- and
/// port-dependent: a public socket takes a datagram only from an address it
/// has sent to (the strictest cone). Inside machines have private addresses
/// 10.0.id.n with their real ports; datagrams between two of them stay
/// inside; other 10.x addresses go nowhere.
/// </summary>
public sealed class NatBox : IDisposable
{
    public enum Mapping { EndpointIndependent, Symmetric, SymmetricSequential }

    private sealed class Pub
    {
        public required UdpClient Sock;
        public required int InsidePort;
        public readonly ConcurrentDictionary<IPEndPoint, byte> Allowed = new();
    }

    private readonly UdpClient _inside;
    private readonly IPAddress _public;
    private readonly int _id;
    private readonly Mapping _mapping;
    private readonly ConcurrentDictionary<(int Port, IPEndPoint? Dest), Pub> _pubs = new();
    private readonly ConcurrentDictionary<int, IPAddress> _privates = new();
    private readonly CancellationTokenSource _stop = new();
    private int _nextPort = 20000 + Random.Shared.Next(30000);
    private readonly List<Thread> _threads = [];
    public int Dropped, Forwarded;

    public IPEndPoint Inside => (IPEndPoint)_inside.Client.LocalEndPoint!;
    public IPAddress Public => _public;

    public NatBox(int id, Mapping mapping)
    {
        _id = id;
        _mapping = mapping;
        _public = new IPAddress([127, 0, 0, (byte)(id + 1)]);
        _inside = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Run(_inside, InsideLoop);
    }

    /// <summary>The private address of the machine whose socket is on 127.0.0.1:<paramref name="localPort"/>.</summary>
    public AnetAddress PrivateOf(int localPort)
    {
        var ip = _privates.GetOrAdd(localPort, _ => new IPAddress([10, 0, (byte)_id, (byte)(_privates.Count + 1)]));
        return AnetAddress.From(new IPEndPoint(ip, localPort));
    }

    public NatTunnelLayer Tunnel() => new(Inside);

    private void Run(UdpClient sock, Action<IPEndPoint, byte[]> handle)
    {
        var t = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var d = sock.Receive(ref from);
                    handle(from, d);
                }
                catch (SocketException) { if (_stop.IsCancellationRequested) return; }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "nat box" };
        _threads.Add(t);
        t.Start();
    }

    private void InsideLoop(IPEndPoint from, byte[] d)
    {
        if (d.Length < 6) return;
        var dest = AnetAddress.Read(d).ToEndPoint();
        var payload = d.AsSpan(6).ToArray();
        int port = from.Port;
        var src = PrivateOf(port);
        var db = dest.Address.GetAddressBytes();
        if (db[0] == 10)
        {
            // inside: delivered as from the sender's private address; elsewhere in 10/8: nowhere
            var to = _privates.FirstOrDefault(kv => kv.Value.Equals(dest.Address) && kv.Key == dest.Port);
            if (to.Value is null) { Interlocked.Increment(ref Dropped); return; }
            Deliver(to.Key, src.ToEndPoint(), payload);
            return;
        }
        var key = (port, _mapping != Mapping.EndpointIndependent ? dest : null);
        var pub = _pubs.GetOrAdd(key, _ =>
        {
            var p = new Pub { Sock = Bind(), InsidePort = port };
            Run(p.Sock, (f, x) => Outside(p, f, x));
            return p;
        });
        pub.Allowed[dest] = 0;
        pub.Sock.Send(payload, payload.Length, dest);
        Interlocked.Increment(ref Forwarded);
    }

    private UdpClient Bind()
    {
        if (_mapping == Mapping.EndpointIndependent) return new UdpClient(new IPEndPoint(_public, 0));
        while (true)
        {
            int p;
            if (_mapping == Mapping.SymmetricSequential)
                // the next free port up (one taken by something else is skipped, as a NAT's other traffic would)
                p = Interlocked.Increment(ref _nextPort);
            else
            {
                // a port at random, as a NAT that randomises its mappings picks it. Not the OS's pick: Windows hands those
                // out in sequence, which made this box a sequential one there, and the host's probes around the port the
                // server saw found the joiner's mapping toward the host. Kept well away from the box's other mappings, so
                // the prediction never hits one by chance either.
                p = 20000 + Random.Shared.Next(40000);
                if (_pubs.Values.Any(x => Math.Abs(((IPEndPoint)x.Sock.Client.LocalEndPoint!).Port - p) <= 4 * AnetHost.PredictSpan)) continue;
            }
            try { return new UdpClient(new IPEndPoint(_public, p)); }
            catch (SocketException) { }
        }
    }

    private void Outside(Pub p, IPEndPoint from, byte[] d)
    {
        if (!p.Allowed.ContainsKey(from)) { Interlocked.Increment(ref Dropped); return; }
        Deliver(p.InsidePort, from, d);
    }

    private void Deliver(int insidePort, IPEndPoint source, byte[] payload)
    {
        var b = new byte[6 + payload.Length];
        AnetAddress.From(source).Write(b);
        payload.CopyTo(b, 6);
        try { _inside.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, insidePort)); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The public port the box uses for the inside machine on that local port (endpoint-independent mapping only).</summary>
    public int? PublicPortOf(int localPort) =>
        _pubs.TryGetValue((localPort, null), out var p) ? ((IPEndPoint)p.Sock.Client.LocalEndPoint!).Port : null;

    public void Dispose()
    {
        _stop.Cancel();
        _inside.Dispose();
        foreach (var p in _pubs.Values) p.Sock.Dispose();
        foreach (var t in _threads) t.Join(500);
    }
}

/// <summary>The inside machine's end of a <see cref="NatBox"/>: everything goes to the box, prefixed with where it is meant to go.</summary>
public sealed class NatTunnelLayer(IPEndPoint box) : PacketLayerBase(6)
{
    /// <summary>Addresses on the inside machine itself, reached without the box (a game talking to its own server over the loopback).</summary>
    public Func<IPEndPoint, bool>? Direct { get; init; }

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        if (!AnetLayerNormal(endPoint).Equals(box) && Direct?.Invoke(AnetLayerNormal(endPoint)) == true) return;
        if (!AnetLayerNormal(endPoint).Equals(box) || length < 6) { length = 0; return; }
        var src = AnetAddress.Read(data).ToEndPoint();
        Buffer.BlockCopy(data, 6, data, 0, length - 6);
        length -= 6;
        endPoint = src;
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
    {
        if (endPoint is null || Direct?.Invoke(AnetLayerNormal(endPoint)) == true) return;
        if (data.Length < offset + length + 6) Array.Resize(ref data, offset + length + 6);
        Buffer.BlockCopy(data, offset, data, offset + 6, length);
        AnetAddress.From(endPoint).Write(data.AsSpan(offset));
        length += 6;
        endPoint = box;
    }

    private static IPEndPoint AnetLayerNormal(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;
}
