using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace I76.Net;

/// <summary>
/// A UDP relay that delays and drops datagrams, for testing over a bad
/// link on one machine: clients connect to the proxy's port, and it
/// forwards to the server, each way delayed by <see cref="LatencyMs"/>
/// (± <see cref="JitterMs"/>) and dropped with <see cref="LossPercent"/>.
/// LiteNetLib's own SimulateLatency / SimulatePacketLoss are compiled out
/// of its NuGet build (they need DEBUG or SIMULATE_NETWORK), so the tests
/// and the game's I76_NET_LAG use this instead.
/// </summary>
public sealed class LagProxy : IDisposable
{
    public int LatencyMs { get; }
    public int JitterMs { get; }
    public float LossPercent { get; set; }
    public int Port => ((IPEndPoint)_listen.Client.LocalEndPoint!).Port;
    public long Forwarded, Dropped;

    private readonly UdpClient _listen;
    private readonly IPEndPoint _server;
    private readonly Dictionary<IPEndPoint, UdpClient> _upstream = [];
    private readonly PriorityQueue<(UdpClient Via, byte[] Data, IPEndPoint To), double> _due = new();
    private readonly object _lock = new();
    private readonly Random _rng;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _sender;

    public LagProxy(IPEndPoint server, int latencyMs, float lossPercent, int jitterMs = 0, int listenPort = 0, int seed = 1)
    {
        _server = server;
        LatencyMs = latencyMs;
        JitterMs = jitterMs;
        LossPercent = lossPercent;
        _rng = new Random(seed);
        _listen = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
        _ = Pump(_listen, null);
        _sender = new Thread(Send) { IsBackground = true, Name = "lag proxy" };
        _sender.Start();
    }

    /// <summary>Reads one socket: from a client (forwarded to the server) or from the server (back to that client).</summary>
    private async Task Pump(UdpClient sock, IPEndPoint? client)
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await sock.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (client is null)
            {
                UdpClient up;
                lock (_lock)
                    if (!_upstream.TryGetValue(r.RemoteEndPoint, out up!))
                    {
                        up = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                        _upstream[r.RemoteEndPoint] = up;
                        _ = Pump(up, r.RemoteEndPoint);
                    }
                Queue(up, r.Buffer, _server);
            }
            else Queue(_listen, r.Buffer, client);
        }
    }

    private void Queue(UdpClient via, byte[] data, IPEndPoint to)
    {
        lock (_lock)
        {
            if (_rng.NextDouble() * 100 < LossPercent) { Dropped++; return; }
            double at = _clock.Elapsed.TotalMilliseconds + LatencyMs + (JitterMs > 0 ? _rng.Next(-JitterMs, JitterMs + 1) : 0);
            _due.Enqueue((via, data, to), at);
        }
    }

    private void Send()
    {
        while (!_stop.IsCancellationRequested)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            while (true)
            {
                (UdpClient Via, byte[] Data, IPEndPoint To) p;
                lock (_lock)
                {
                    if (!_due.TryPeek(out p, out double at) || at > now) break;
                    _due.Dequeue();
                    Forwarded++;
                }
                try { p.Via.Send(p.Data, p.Data.Length, p.To); } catch (SocketException) { } catch (ObjectDisposedException) { }
            }
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _sender.Join(500);
        _listen.Dispose();
        lock (_lock) foreach (var u in _upstream.Values) u.Dispose();
    }
}
