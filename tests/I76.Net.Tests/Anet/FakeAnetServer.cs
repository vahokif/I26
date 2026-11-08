using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using I76.Net.Anet;

namespace I76.Net.Tests.Anet;

/// <summary>
/// An anet game server for tests, in process: the parts of
/// src/linux/server/server.c and dp2.c a lobby client meets. A dpio link
/// per client (its SYN tells it the address the server sees, and the
/// server's dual-address bookkeeping keeps the client's reported one too);
/// "e1" and a species subscribes the client to that species' table; the
/// client's MYSESSIONS items (raw records) go into the table and out to its
/// subscribers in the compact form; a client's sessions go when its link
/// does; INDIRECT_JOIN sends the host REQUEST_OPEN with the joiner's seen and
/// reported addresses. Used when the real server is not built.
/// </summary>
public sealed class FakeAnetServer : IDisposable
{
    private sealed class Client
    {
        public required DpioLink Link;
        public required IPEndPoint Seen;
        public readonly DptabReader Tab = new();
        public ushort? Species;
    }

    private sealed record Rec(byte[] Subkey, byte[] Raw, ushort Species, IPEndPoint Owner);

    private readonly UdpClient _sock;
    private readonly Dictionary<IPEndPoint, Client> _clients = [];
    private readonly Dictionary<ulong, Rec> _sessions = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    public int Logins, Publishes, Introductions;

    public int Port => ((IPEndPoint)_sock.Client.LocalEndPoint!).Port;

    /// <summary>A server on <paramref name="bind"/> (default: a free port on 127.0.0.1; a capture's servers sit on anet's port at 127.0.0.x).</summary>
    public FakeAnetServer(IPEndPoint? bind = null)
    {
        _sock = new UdpClient(bind ?? new IPEndPoint(IPAddress.Loopback, 0));
        _sock.Client.ReceiveTimeout = 20;
        _thread = new Thread(Run) { IsBackground = true, Name = "fake anet server" };
        _thread.Start();
    }

    public int SessionCount { get { lock (_lock) return _sessions.Count; } }

    private void Run()
    {
        while (!_stop)
        {
            double now = _clock.Elapsed.TotalSeconds;
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var d = _sock.Receive(ref from);
                lock (_lock) Datagram(from, d, now);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            lock (_lock)
            {
                foreach (var (ep, c) in _clients.ToList())
                {
                    c.Link.Update(now);
                    Drain(c, now);
                    if (c.Link.Closed) Drop(ep, now);
                }
            }
        }
    }

    private void Datagram(IPEndPoint from, byte[] d, double now)
    {
        if (!AnetWire.Looks(d)) return;
        if (!_clients.TryGetValue(from, out var c))
        {
            if (d[1] != AnetWire.Syn) return;
            var link = new DpioLink(x => _sock.Send(x, x.Length, from)) { Timeout = 41 };
            var self = AnetAddress.From(new IPEndPoint(IPAddress.Loopback, Port));
            // the server's SYN: its own address, the client's as seen, capabilities, its address again (dpio_openHdl2)
            link.Open(now, AnetWire.SynBody(self, AnetAddress.From(from), 0, self));
            _clients[from] = c = new Client { Link = link, Seen = from };
        }
        c.Link.Receive(d, now);
        Drain(c, now);
    }

    private void Drain(Client c, double now)
    {
        while (c.Link.Delivered.TryDequeue(out var p))
        {
            if (p.Length >= 4 && p[0] == 'e' && p[1] == '1')
            {
                ushort species = (ushort)(p[2] | p[3] << 8);
                c.Species = species;
                Logins++;
                var items = _sessions.Values.Where(r => r.Species == species).ToList();
                foreach (var r in items) c.Link.Send(Compact(r, null), now);
                continue;
            }
            if (p.Length >= 15 && p[0] == AnetWire.Initial && p[1] == AnetWire.IndirectJoin)
            {
                ulong id = AnetSession.IdOf(p.AsSpan(3, 8));
                if (!_sessions.TryGetValue(id, out var r) || !_clients.TryGetValue(r.Owner, out var host)) continue;
                var reported = c.Link.PeerSays ?? AnetAddress.From(c.Seen);
                host.Link.Send(AnetWire.RequestOpenPacket(AnetAddress.From(c.Seen), reported), now);
                Introductions++;
                continue;
            }
            foreach (var ch in c.Tab.Read(p))
            {
                if (ch.Key is not [AnetWire.KeyMySessions]) continue;
                ulong id = AnetSession.IdOf(ch.Subkey);
                if (ch.Value is null)
                {
                    if (_sessions.Remove(id, out var gone)) Broadcast(gone, delete: true, now);
                    continue;
                }
                if (ch.Value.Length != AnetSession.RawSize) continue;
                ushort species = (ushort)(ch.Value[13] | ch.Value[14] << 8);
                var rec = new Rec(ch.Subkey, ch.Value, species, c.Seen);
                _sessions[id] = rec;
                Publishes++;
                Broadcast(rec, delete: false, now);
            }
        }
    }

    private static byte[] Compact(Rec r, bool? delete)
    {
        var s = AnetSession.Unpack(r.Subkey, r.Raw)!;
        return AnetWire.Small(1, AnetWire.SessionsKey(r.Species), [(r.Subkey, delete == true ? null : s.PackCompact())]);
    }

    private void Broadcast(Rec r, bool delete, double now)
    {
        foreach (var c in _clients.Values)
            if (c.Species == r.Species) c.Link.Send(Compact(r, delete), now);
    }

    private void Drop(IPEndPoint ep, double now)
    {
        _clients.Remove(ep);
        foreach (var (id, r) in _sessions.Where(kv => kv.Value.Owner.Equals(ep)).ToList())
        {
            _sessions.Remove(id);
            Broadcast(r, delete: true, now);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        _sock.Dispose();
    }
}
