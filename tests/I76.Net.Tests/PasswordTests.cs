using System.Diagnostics;
using I76.Net.Anet;
using I76.Server;
using Xunit;

namespace I76.Net.Tests;

/// <summary>A game's password (protocol 12): the join request, the refusals and their reasons, the locked flag in both listings.</summary>
public class PasswordTests
{
    private static ClientState Join(NetMeleeServer server, string password, out string? reason, int port = -1, bool leave = true)
    {
        var client = new MeleeClient();
        client.Connect("127.0.0.1", port < 0 ? server.Port : port, "Groover", "vcgcour1.vcf", password: password);
        for (int t = 0; t < 250 && client.State == ClientState.Connecting; t++) { client.Poll(); Thread.Sleep(20); }
        var state = client.State;
        reason = client.Reason;
        if (leave && state == ClientState.Joined) client.Leave();
        client.Dispose();
        return state;
    }

    private static NetMeleeServer Locked(string install, string password = "hunter2")
    {
        var s = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Locked melee", MaxPlayers = 4, Password = password });
        s.Start();
        return s;
    }

    [Fact]
    public void JoinRequestCarriesThePasswordAndAnOlderOneStillReads()
    {
        var w = new NetWriter();
        new JoinRequest(Protocol.Version, "Taurus", "vppirna1.vcf", 3, [1, 2, 3], "secret").Write(w);
        var r = new NetReader(w.Span);
        Assert.Equal("secret", JoinRequest.Read(ref r).Password);

        // protocol 11 ended after the configuration: it reads, with no password, and is turned away for its version
        var old = new NetWriter();
        old.Str(Protocol.Magic); old.U16(11); old.Str("Taurus"); old.Str("vppirna1.vcf"); old.U8(3); old.BlobU16([1, 2, 3]);
        r = new NetReader(old.Span);
        var j = JoinRequest.Read(ref r);
        Assert.Equal((11, ""), (j.Version, j.Password));
    }

    [Fact]
    public void RightWrongAndMissingPasswords()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = Locked(install);
        Assert.Equal(ClientState.Rejected, Join(server, "", out var why));
        Assert.Equal(Protocol.NeedsPassword, why);
        // the ask without a password was no try: the right one follows at once
        Assert.Equal(ClientState.Joined, Join(server, "hunter2", out _));

        Assert.Equal(ClientState.Rejected, Join(server, "hunter3", out why));
        Assert.Equal(Protocol.WrongPassword, why);
    }

    [Fact]
    public void AWrongPasswordSlowsTheNextTryFromThatAddress()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = Locked(install);
        Assert.Equal(ClientState.Rejected, Join(server, "nope", out var why));
        Assert.Equal(Protocol.WrongPassword, why);
        // at once: refused even with the right word
        Assert.Equal(ClientState.Rejected, Join(server, "hunter2", out why));
        Assert.Equal(Protocol.PasswordTooSoon, why);
        Thread.Sleep((int)(Protocol.PasswordRetrySeconds * 1000) + 300);
        Assert.Equal(ClientState.Joined, Join(server, "hunter2", out _));
    }

    [Fact]
    public void AnOpenGameIgnoresAPasswordAndALockedOneSaysSoInItsAnswer()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var open = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Name = "Open melee" });
        open.Start();
        Assert.Equal(ClientState.Joined, Join(open, "whatever", out _));
        Assert.False(open.Host.Info().Locked);

        using var locked = Locked(install);
        var info = locked.Host.Info();
        Assert.True(info.Locked);
        var w = new NetWriter();
        info.Write(w);
        Assert.True(SessionInfo.TryRead(w.Span)!.Locked);
        // the text of the password is nowhere in the answer
        Assert.False(w.Span.IndexOf("hunter2"u8) >= 0);

        // an answer from before the flag (it ends at the map hash's byte) reads as open; the flag is followed by the
        // restrictions' flags and pair count (none here), the co-op byte, the rate's, the lobby's, the car reset's, the CB's and the crosshair's, eight bytes more,
        // then the arena's name and the drivers' makes and models (protocol 22)
        var bytes = w.ToArray();
        Assert.False(SessionInfo.TryRead(bytes.AsSpan(0, bytes.Length - 9 - DiscoveryTests.NamesOf(info)))!.Locked);

        using var browser = new SessionBrowser();
        SessionBrowser.Found? f = null;
        for (int i = 0; i < 100 && f is null; i++)
        {
            if (i % 10 == 0) browser.Ask("127.0.0.1", locked.Port);
            browser.Poll();
            f = browser.Sessions.FirstOrDefault();
            Thread.Sleep(20);
        }
        Assert.True(f!.Info.Locked);
    }

    [Fact]
    public void TheListingTagMarksLockedWithoutUsingTheSessionsPasswordField()
    {
        var tag = new I26SessionTag(Protocol.Version, false, false, false, false, default, 2, default, "m05.msn", Locked: true);
        Assert.True(I26SessionTag.Read(tag.Write())!.Locked);
        Assert.False(I26SessionTag.Read((tag with { Locked = false }).Write())!.Locked);
        // the other bits of byte 13 are unchanged
        var shared = new I26SessionTag(Protocol.Version, true, false, false, false, I76.Sim.Missions.MeleeKind.Race, 2, I76.Sim.Missions.MeleeGoal.Laps, "N35.RAC", true, true, true);
        var back = I26SessionTag.Read(shared.Write())!;
        Assert.Equal((true, true, true), (back.CustomMap, back.MapShared, back.Locked));
    }

    public static IEnumerable<object[]> Servers() => Anet.AnetPunchTests.Servers();

    [Theory, MemberData(nameof(Servers))]
    public void ALockedGameIsListedLockedAndJoinedThroughTheLobbyWithItsPassword(string kind)
    {
        if (HostTests.InstallDir is not { } install) return;
        IDisposable srv;
        int port;
        if (kind == "real") { var r = new Anet.RealAnetServer(); srv = r; port = r.Port; }
        else { var f = new Anet.FakeAnetServer(); srv = f; port = f.Port; }
        using var _ = srv;
        using var server = Locked(install);
        server.ListOn([("127.0.0.1", port)]);
        var sw = Stopwatch.StartNew();
        while (!server.Anet!.AnyListed && sw.Elapsed.TotalSeconds < 5) Thread.Sleep(10);
        Assert.True(server.Anet!.AnyListed, server.AnetStatus);

        using var browser = new SessionBrowser();
        SessionBrowser.Found? game = null;
        sw.Restart();
        while ((game = browser.Sessions.FirstOrDefault(f => f.Anet is not null)) is null && sw.Elapsed.TotalSeconds < 6) { browser.List("127.0.0.1", port); browser.Poll(); Thread.Sleep(10); }
        Assert.NotNull(game);
        Assert.True(game!.Info.Locked);
        // the record the server holds has no password text in it
        Assert.Equal("", game.Anet!.Session.Password);

        string? Via(string password)
        {
            using var client = new MeleeClient();
            client.ConnectVia("127.0.0.1", port, game.Anet.Session, "joiner", "vcgcour1.vcf", password: password);
            sw.Restart();
            while (client.State == ClientState.Connecting && sw.Elapsed.TotalSeconds < 25) { client.Poll(); Thread.Sleep(10); }
            var result = client.State == ClientState.Joined ? "joined" : client.Reason;
            if (client.State == ClientState.Joined) client.Leave();
            return result;
        }
        Assert.Equal(Protocol.NeedsPassword, Via(""));
        Assert.Equal("joined", Via("hunter2"));
    }

    [Fact]
    public void AnOlderProtocolIsRefusedWithItsReasonNotSilently()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = Locked(install);
        var net = new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener());
        var listener = new LiteNetLib.EventBasedNetListener();
        string? reason = null;
        bool done = false;
        listener.PeerDisconnectedEvent += (p, info) => { reason = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : info.Reason.ToString(); done = true; };
        net = new LiteNetLib.NetManager(listener);
        net.Start();
        var old = new NetWriter();
        old.Str(Protocol.Magic); old.U16(11); old.Str("Old"); old.Str("vcgcour1.vcf"); old.U8(0); old.BlobU16([]);
        var data = new LiteNetLib.Utils.NetDataWriter();
        data.Put(old.ToArray());
        net.Connect("127.0.0.1", server.Port, data);
        for (int t = 0; t < 250 && !done; t++) { net.PollEvents(); Thread.Sleep(20); }
        net.Stop();
        Assert.Contains("protocol 11", reason);
        Assert.Contains($"speaks {Protocol.Version}", reason);
    }
}

public class PasswordRedactionTests
{
    [Fact]
    public void RecordsDoNotPrintThePassword()
    {
        var o = new ServerOptions { Install = "x", Password = "swordfish" };
        Assert.DoesNotContain("swordfish", o.ToString());
        Assert.DoesNotContain("swordfish", new JoinRequest(Protocol.Version, "n", "c.vcf", 0, [], "swordfish").ToString());
    }
}
