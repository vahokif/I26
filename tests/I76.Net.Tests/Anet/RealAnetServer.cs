using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace I76.Net.Tests.Anet;

/// <summary>
/// The real anet game server (src/linux/server from the LGPL anet 0.10
/// source, built 32-bit), run on 127.0.0.1 for a test when I76_ANET_SERVER
/// names its binary. Never a public server. The build notes are in
/// docs/multiplayer.md ("Testing against the real server"). It reads its
/// rooms, servers and apps files from a scratch folder and binds the
/// address in ANET_BIND (a two-line local change to uudps.c and sbdserv.c in
/// that build; without it, it binds every interface).
/// </summary>
public sealed class RealAnetServer : IDisposable
{
    public static string? Binary => Environment.GetEnvironmentVariable("I76_ANET_SERVER") is { Length: > 0 } b && File.Exists(b) ? b : null;
    public static bool Available => Binary is not null;

    private readonly Process _p;
    private readonly string _dir;
    public int Port { get; }

    public RealAnetServer()
    {
        Port = FreeUdpPort();
        int tcp = FreeTcpPort();
        _dir = Path.Combine(Path.GetTempPath(), $"i26-anet-{Environment.ProcessId}-{Port}");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "rooms.txt"), "1234 Test Room\n");
        File.WriteAllText(Path.Combine(_dir, "servers.txt"), "127.0.0.1 localhost\n");
        File.WriteAllText(Path.Combine(_dir, "apps.txt"), "");
        var psi = new ProcessStartInfo(Binary!, [$"-p={Port}", "-R=rooms.txt", "-H=servers.txt", "-A=apps.txt"])
        {
            WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
        };
        psi.Environment["ANET_BIND"] = "127.0.0.1";
        psi.Environment["UUDP_HOSTNAME"] = "127.0.0.1";
        psi.Environment["ANET_SBDPORT"] = tcp.ToString();
        _p = Process.Start(psi)!;
        _p.OutputDataReceived += (_, _) => { };
        _p.ErrorDataReceived += (_, _) => { };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
        // up when its port is taken
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !_p.HasExited)
        {
            try { using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port)); }
            catch (SocketException) { return; }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"the anet server did not start (exited: {_p.HasExited})");
    }

    private static int FreeUdpPort() { using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)u.Client.LocalEndPoint!).Port; }
    private static int FreeTcpPort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    public void Dispose()
    {
        try { if (!_p.HasExited) { _p.Kill(); _p.WaitForExit(2000); } } catch (InvalidOperationException) { }
        _p.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
