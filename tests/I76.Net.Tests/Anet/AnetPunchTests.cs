using System.Diagnostics;
using System.Net;
using I76.Net.Anet;
using LiteNetLib;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests.Anet;

/// <summary>A bare LiteNetLib endpoint with anet on its socket, optionally behind a <see cref="NatBox"/>: a host or a joiner without the game.</summary>
internal sealed class TestPeer : IDisposable
{
    public readonly EventBasedNetListener Listener = new();
    public readonly NetManager Net;
    public readonly AnetLayer Layer;
    public readonly LiteNetAnetTransport Transport;
    public AnetHost? Host;
    public AnetJoiner? Joiner;
    public NetPeer? Connected;
    public readonly NatBox? Nat;

    public TestPeer(NatBox? nat = null)
    {
        Nat = nat;
        Layer = new AnetLayer(nat?.Tunnel());
        Net = new NetManager(Listener, Layer) { UnconnectedMessagesEnabled = true, IPv6Enabled = false, AutoRecycle = true, DisconnectTimeout = 5000 };
        Listener.NetworkReceiveUnconnectedEvent += (ep, r, t) =>
        {
            var d = r.GetRemainingBytesSpan();
            if (Host?.HandleUnconnected(ep, d) == true) return;
            Joiner?.HandleUnconnected(ep, d);
        };
        Listener.ConnectionRequestEvent += r => r.AcceptIfKey("i26 test");
        Listener.PeerConnectedEvent += p => Connected = p;
        Net.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0);
        Transport = new LiteNetAnetTransport(Net, Layer);
    }

    /// <summary>Our own address as the anet client should report it: behind the box, the private one it gives us.</summary>
    public AnetAddress? Private => Nat?.PrivateOf(Net.LocalPort);

    public void Send(IPEndPoint to, byte[] d) => Net.SendUnconnectedMessage(d, to);

    public void Step()
    {
        Net.PollEvents();
        Host?.Update();
        Joiner?.Update();
    }

    public void Dispose()
    {
        Host?.Dispose();
        Joiner?.Dispose();
        Net.Stop();
    }
}

/// <summary>
/// Listing, publishing and the server's introduction end to end over real
/// UDP on this machine, against the in-process <see cref="FakeAnetServer"/>
/// (and the real anet server when I76_ANET_SERVER names its binary, see
/// <see cref="RealAnetServer"/>), with and without emulated NATs.
/// </summary>
public class AnetPunchTests(ITestOutputHelper log)
{
    private static readonly byte[] User = new I26SessionTag(Protocol.Version, false, true, false, false, I76.Sim.Missions.MeleeKind.Combat, 2,
        I76.Sim.Missions.MeleeGoal.None, "m05").Write();

    private static bool Loop(Func<bool> done, double seconds, params TestPeer[] peers)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            foreach (var p in peers) p.Step();
            if (done()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    private (TestPeer Host, TestPeer Joiner, AnetSession Session) Setup(int serverPort, NatBox? hostNat, NatBox? joinerNat, NatBox? browserNat = null)
    {
        var host = new TestPeer(hostNat);
        host.Host = new AnetHost(host.Transport, [("127.0.0.1", serverPort)], () => new AnetGame("Punch test", 8, 1, true, User), host.Send,
            privateOverride: host.Private);
        host.Host.Log += m => log.WriteLine($"host: {m}");
        Assert.True(Loop(() => host.Host.AnyListed, 5, host), $"host not listed: {host.Host.Status}");
        log.WriteLine(host.Host.Status);

        // a browser on its own socket lists the game (the game's roster and its join are separate sockets too)
        using var browser = new TestPeer(browserNat);
        var lister = new AnetClient(browser.Transport, "127.0.0.1", serverPort, AnetSpecies.I26) { PrivateOverride = browser.Private };
        AnetSession? found = null;
        Assert.True(Loop(() => { lister.Update(); found = lister.Sessions.Values.FirstOrDefault(s => s.Name == "Punch test"); return found is not null; }, 5, host, browser),
            $"not listed: {lister.State} {lister.Problem}");
        lister.Dispose();
        Assert.Equal(Protocol.Version, found!.Tag!.Protocol);
        Assert.Equal("m05", found.Tag.Arena);
        Assert.True(found.Tag.TeamPlay);
        log.WriteLine($"listed: {found.Name} master {found.Master} key {found.KeyAddress} {found.Players}/{found.MaxPlayers}");

        return (host, Joiner(serverPort, found, joinerNat), found);
    }

    /// <summary>A joiner of the listed session behind <paramref name="nat"/> (none: on the open internet).</summary>
    private TestPeer Joiner(int serverPort, AnetSession found, NatBox? nat, string name = "joiner", IReadOnlyList<(string, int)>? check = null, double punchTime = 10)
    {
        var joiner = new TestPeer(nat);
        joiner.Joiner = new AnetJoiner(joiner.Transport, "127.0.0.1", serverPort, found, joiner.Send, privateOverride: joiner.Private)
        {
            CheckServers = check ?? [], PunchTime = punchTime,
        };
        joiner.Joiner.Log += m => log.WriteLine($"{name}: {m}");
        return joiner;
    }

    private bool PunchAndConnect(TestPeer host, TestPeer joiner)
    {
        bool reached = Loop(() => joiner.Joiner!.Reached is not null || joiner.Joiner.Failed is not null, 20, host, joiner);
        Assert.True(reached, "the joiner neither reached the host nor gave up");
        if (joiner.Joiner!.Reached is not { } at) return false;
        joiner.Net.Connect(at, "i26 test");
        Assert.True(Loop(() => joiner.Connected is not null && host.Connected is not null, 5, host, joiner), $"no connection to {at}");
        log.WriteLine($"connected to {at}; the host sees {host.Connected}");
        return true;
    }

    public static IEnumerable<object[]> Servers() => RealAnetServer.Available ? [["fake"], ["real"]] : [["fake"]];

    private static IDisposable StartServer(string kind, out int port)
    {
        if (kind == "real") { var r = new RealAnetServer(); port = r.Port; return r; }
        var f = new FakeAnetServer();
        port = f.Port;
        return f;
    }

    [Theory, MemberData(nameof(Servers))]
    public void NoNatJoinsThroughTheServer(string server)
    {
        using var srv = StartServer(server, out int port);
        var (host, joiner, s) = Setup(port, null, null);
        using (host) using (joiner)
        {
            Assert.True(PunchAndConnect(host, joiner));
            Assert.Equal(s.Master.ToEndPoint(), joiner.Joiner!.Reached);
        }
    }

    [Theory, MemberData(nameof(Servers))]
    public void PartyLineNatsPunchThrough(string server)
    {
        using var srv = StartServer(server, out int port);
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.EndpointIndependent);
        var (host, joiner, s) = Setup(port, natA, natB, natB);
        using (host) using (joiner)
        {
            // the server sees the host at its NAT's public address, the record keeps its private one as the subkey
            Assert.Equal(natA.Public, s.Master.ToEndPoint().Address);
            Assert.Equal(host.Private, s.KeyAddress);
            Assert.True(PunchAndConnect(host, joiner));
            Assert.Equal(natA.Public, joiner.Joiner!.Reached!.Address);
            Assert.True(host.Host!.Introductions >= 1);
            log.WriteLine($"NAT A dropped {natA.Dropped}, NAT B dropped {natB.Dropped}");
        }
    }

    [Theory, MemberData(nameof(Servers))]
    public void SymmetricNatFailsWithTheForwardingHint(string server)
    {
        using var srv = StartServer(server, out int port);
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.Symmetric);
        var (host, joiner, s) = Setup(port, natA, natB, natB);
        using (host) using (joiner)
        {
            Assert.False(PunchAndConnect(host, joiner));
            Assert.Contains("forward UDP port", joiner.Joiner!.Failed);
            Assert.Contains(s.KeyAddress.Port.ToString(), joiner.Joiner.Failed);
            Assert.Equal(NatVerdict.Unknown, joiner.Joiner.Verdict);
            Assert.True(host.Host!.Introductions >= 1, "the server did introduce them");
        }
    }

    /// <summary>
    /// A symmetric NAT that hands out ports in sequence (many home and carrier NATs): the joiner's probes to the host leave
    /// from a port or two after the one the server saw, and the host's probes to the ports around it find that one.
    /// </summary>
    [Theory, MemberData(nameof(Servers))]
    public void ASequentialSymmetricNatIsReachedAroundTheSeenPort(string server)
    {
        using var srv = StartServer(server, out int port);
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.SymmetricSequential);
        var (host, joiner, _) = Setup(port, natA, natB, natB);
        using (host) using (joiner)
        {
            Assert.True(PunchAndConnect(host, joiner), joiner.Joiner!.Failed);
            Assert.Equal(natA.Public, joiner.Joiner!.Reached!.Address);
        }
    }

    /// <summary>
    /// The tester's report: three players got in and a fourth got a punch error. The host behind a filtering NAT takes
    /// joiners one after another while the earlier ones stay connected: two behind NATs of their own, two sharing one
    /// (a household), one behind a sequential symmetric NAT, then three more at once.
    /// </summary>
    [Fact]
    public void EveryJoinerGetsInWhileTheEarlierOnesStayConnected()
    {
        using var srv = new FakeAnetServer();
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        var nats = new[] { new NatBox(2, NatBox.Mapping.EndpointIndependent), new NatBox(3, NatBox.Mapping.EndpointIndependent),
            new NatBox(4, NatBox.Mapping.SymmetricSequential), new NatBox(5, NatBox.Mapping.EndpointIndependent), new NatBox(6, NatBox.Mapping.EndpointIndependent) };
        var (host, first, s) = Setup(srv.Port, natA, nats[0], nats[0]);
        var all = new List<TestPeer> { first };
        try
        {
            Assert.True(PunchAndConnect(host, first), first.Joiner!.Failed);
            // one after another: a NAT of its own, the first one's household, the sequential symmetric one
            foreach (var (nat, i) in new[] { (nats[1], 2), (nats[0], 3), (nats[2], 4) })
            {
                var j = Joiner(srv.Port, s, nat, $"joiner {i}");
                all.Add(j);
                Assert.True(Loop(() => j.Joiner!.Reached is not null || j.Joiner.Failed is not null, 20, [host, .. all]), $"joiner {i} neither reached the host nor gave up");
                Assert.True(j.Joiner!.Reached is not null, $"joiner {i}: {j.Joiner.Failed}");
                j.Net.Connect(j.Joiner.Reached!, "i26 test");
                Assert.True(Loop(() => j.Connected is not null && host.Net.ConnectedPeersCount == i, 5, [host, .. all]), $"joiner {i} did not connect");
            }
            // three at once (two NATs of their own and the household again)
            var batch = new[] { Joiner(srv.Port, s, nats[3], "joiner 5"), Joiner(srv.Port, s, nats[4], "joiner 6"), Joiner(srv.Port, s, nats[1], "joiner 7") };
            all.AddRange(batch);
            Assert.True(Loop(() => batch.All(j => j.Joiner!.Reached is not null || j.Joiner.Failed is not null), 20, [host, .. all]));
            foreach (var j in batch) Assert.True(j.Joiner!.Reached is not null, j.Joiner.Failed);
            foreach (var j in batch) j.Net.Connect(j.Joiner!.Reached!, "i26 test");
            Assert.True(Loop(() => host.Net.ConnectedPeersCount == 7 && all.All(j => j.Connected is not null), 5, [host, .. all]), $"{host.Net.ConnectedPeersCount} connected");
            log.WriteLine($"introductions {host.Host!.Introductions}, answered {host.Host.Answered}");
        }
        finally
        {
            foreach (var j in all) j.Dispose();
            host.Dispose();
            foreach (var n in nats) n.Dispose();
        }
    }

    /// <summary>A failed punch asks a second server where it sees the joiner: another port than the first saw is the joiner's symmetric NAT.</summary>
    [Fact]
    public void AFailedPunchBlamesTheJoinersSymmetricNat()
    {
        using var srv = new FakeAnetServer();
        using var other = new FakeAnetServer();
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var natB = new NatBox(2, NatBox.Mapping.Symmetric);
        var (host, plain, s) = Setup(srv.Port, natA, null, natB);
        plain.Dispose();
        using var joiner = Joiner(srv.Port, s, natB, check: [("127.0.0.1", other.Port)], punchTime: 4);
        using (host)
        {
            Assert.False(PunchAndConnect(host, joiner));
            Assert.Equal(NatVerdict.JoinerSymmetric, joiner.Joiner!.Verdict);
            Assert.StartsWith("your router gives every connection a new port", joiner.Joiner.Failed);
            Assert.Contains($"forward UDP port {s.KeyAddress.Port}", joiner.Joiner.Failed);
        }
    }

    /// <summary>And the same port from both servers: the joiner's NAT is fine, the host's stopped it (a symmetric NAT there; open joiners would still have got in).</summary>
    [Fact]
    public void AFailedPunchBlamesTheHostsRouterWhenTheJoinersIsFine()
    {
        using var srv = new FakeAnetServer();
        using var other = new FakeAnetServer();
        using var natA = new NatBox(1, NatBox.Mapping.Symmetric);
        using var natB = new NatBox(2, NatBox.Mapping.EndpointIndependent);
        var (host, plain, s) = Setup(srv.Port, natA, null, natB);
        using (host)
        {
            // a joiner with no NAT gets in (the host's probes reach it from whatever port)
            Assert.True(PunchAndConnect(host, plain), plain.Joiner!.Failed);
            plain.Dispose();
            using var joiner = Joiner(srv.Port, s, natB, check: [("127.0.0.1", other.Port)], punchTime: 4);
            Assert.False(PunchAndConnect(host, joiner));
            Assert.Equal(NatVerdict.HostSide, joiner.Joiner!.Verdict);
            Assert.StartsWith("your router is fine, but the host's would not let you in", joiner.Joiner.Failed);
        }
    }

    [Theory, MemberData(nameof(Servers))]
    public void SameNatJoinsAtThePrivateAddress(string server)
    {
        using var srv = StartServer(server, out int port);
        using var nat = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        var (host, joiner, s) = Setup(port, nat, nat, nat);
        using (host) using (joiner)
        {
            // the box does not hairpin: only the private address answers
            Assert.True(PunchAndConnect(host, joiner));
            Assert.Equal(s.KeyAddress.ToEndPoint(), joiner.Joiner!.Reached);
        }
    }

    /// <summary>A 0.5.0 host's listing does not say its Hidden Weapons &amp; Cars: the roster leaves it to the host, rather than refuse hidden cars itself.</summary>
    [Fact]
    public void AListingThatDoesNotSayItsHiddenRuleLeavesItToTheHost()
    {
        using var srv = new FakeAnetServer();
        using var natA = new NatBox(1, NatBox.Mapping.EndpointIndependent);
        using var host = new TestPeer(natA);
        host.Host = new AnetHost(host.Transport, [("127.0.0.1", srv.Port)], () => new AnetGame("Old host", 8, 1, true, User), host.Send, privateOverride: host.Private);
        Assert.True(Loop(() => host.Host.AnyListed, 5, host));
        using var browser = new SessionBrowser();
        SessionBrowser.Found? f = null;
        Assert.True(Loop(() => { browser.List("127.0.0.1", srv.Port); browser.Poll(); return (f = browser.Sessions.FirstOrDefault()) is not null; }, 5, host));
        Assert.False(f!.Info.HiddenKnown);
        Assert.True(f.Info.MayBringHidden);
    }

    [Fact]
    public void ASilentServerIsReportedAndTheHostStillAnswersDirectly()
    {
        // nobody on that port: the host says it is not listed; a joiner given the session still gets in directly
        using var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int dead = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        using var host = new TestPeer();
        host.Host = new AnetHost(host.Transport, [("127.0.0.1", dead)], () => new AnetGame("Lonely", 4, 1, true, User), host.Send);
        Assert.True(Loop(() => host.Host.Status.StartsWith("Not listed"), 10, host), host.Host.Status);
        log.WriteLine(host.Host.Status);
        var s = new AnetSession(AnetAddress.From(new IPEndPoint(IPAddress.Loopback, host.Net.LocalPort)), host.Host.Karma,
            AnetAddress.From(new IPEndPoint(IPAddress.Loopback, host.Net.LocalPort)), 4, 1, AnetSession.FlagsOpen, "Lonely", "", User);
        using var joiner = new TestPeer();
        joiner.Joiner = new AnetJoiner(joiner.Transport, "127.0.0.1", dead, s, joiner.Send);
        Assert.True(PunchAndConnect(host, joiner));
    }
}
