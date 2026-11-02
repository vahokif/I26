using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using Xunit;

namespace I76.Net.Tests;

/// <summary>Finding games: the session query and answer, the address book's addresses, a server answering on the LAN.</summary>
public class DiscoveryTests
{
    [Fact]
    public void TheGamesSimulationRateIsListed()
    {
        var w = new NetWriter();
        new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], RateHz: 60).Write(w);
        Assert.Equal(60, SessionInfo.TryRead(w.Span)!.RateHz);
        // not a rate the game offers, or an answer from a build that did not say: unknown (0)
        w = new NetWriter();
        new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], RateHz: 17).Write(w);
        Assert.Equal(0, SessionInfo.TryRead(w.Span)!.RateHz);
        w = new NetWriter();
        new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], RateHz: 60).Write(w);
        Assert.Equal(0, SessionInfo.TryRead(w.Span[..^5])!.RateHz);       // (protocol 22 ends with the lobby, car reset, CB and crosshair bytes, after the rate)
    }

    /// <summary>The host's Car Reset (the reset-upright key) is listed; a build that did not say (no key) is "no".</summary>
    [Fact]
    public void TheGamesCarResetIsListed()
    {
        foreach (bool on in new[] { true, false })
        {
            var w = new NetWriter();
            new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], RateHz: 60, CarReset: on).Write(w);
            var back = SessionInfo.TryRead(w.Span)!;
            Assert.Equal((on, (byte)60), (back.CarReset, back.RateHz));
        }
        var old = new NetWriter();
        new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], RateHz: 60, CarReset: true).Write(old);
        var older = SessionInfo.TryRead(old.Span[..^3])!;
        Assert.False(older.CarReset);
        Assert.True(older.Cb);
        Assert.Equal(60, older.RateHz);
    }

    /// <summary>The host's CB Radio is listed; a build that did not say has it on.</summary>
    [Fact]
    public void TheGamesCbRadioIsListed()
    {
        foreach (bool on in new[] { true, false })
        {
            var w = new NetWriter();
            new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], CarReset: true, Cb: on).Write(w);
            var back = SessionInfo.TryRead(w.Span)!;
            Assert.Equal((on, true), (back.Cb, back.CarReset));
        }
        var old = new NetWriter();
        new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], CarReset: true, Cb: false).Write(old);
        Assert.True(SessionInfo.TryRead(old.Span[..^2])!.Cb);
    }

    /// <summary>The host's Chase Crosshair is listed; a build that did not say does not allow it.</summary>
    [Fact]
    public void TheGamesCrosshairIsListed()
    {
        foreach (bool on in new[] { true, false })
        {
            var w = new NetWriter();
            new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], CarReset: true, Cb: false, Crosshair: on).Write(w);
            var back = SessionInfo.TryRead(w.Span)!;
            Assert.Equal((on, false, true), (back.Crosshair, back.Cb, back.CarReset));
        }
        var old = new NetWriter();
        new SessionInfo(1, Protocol.Version, "g", "m05", 1, 4, false, false, 0, 0, false, [], Cb: false, Crosshair: true).Write(old);
        var older = SessionInfo.TryRead(old.Span[..^1])!;
        Assert.False(older.Crosshair);
        Assert.False(older.Cb);
    }

    [Fact]
    public void SessionInfoRoundTrips()
    {
        var info = new SessionInfo(0x0123456789abcdefUL, Protocol.Version, "Slick and Quick", "m05", 2, 4, true, false, 20000, 0, false,
            [new SessionDriver("Taurus", "vppirna1.vcf", 1), new SessionDriver("Jade", "vcgcour2.vcf", 3)],
            I76.Sim.Missions.MeleeKind.Ctf, I76.Sim.Missions.MeleeGoal.Captures, 5, 3);
        var w = new NetWriter();
        info.Write(w);
        var back = SessionInfo.TryRead(w.Span);
        Assert.NotNull(back);
        Assert.Equal(info with { Drivers = [] }, back! with { Drivers = [] });
        Assert.Equal(info.Drivers, back.Drivers);
        Assert.False(back.Full);
        Assert.True((back with { Players = 4 }).Full);
        Assert.True((back with { Over = true }).Full);
        // anything else is not an answer
        Assert.Null(SessionInfo.TryRead([]));
        Assert.Null(SessionInfo.TryRead(Discovery.Query()));
        Assert.Null(SessionInfo.TryRead(w.Span[..10]));
        Assert.True(Discovery.IsQuery(Discovery.Query()));
        Assert.False(Discovery.IsQuery(w.Span));
        Assert.False(Discovery.IsQuery([0xff]));
    }

    [Theory]
    [InlineData("192.168.1.20:7777", "192.168.1.20", 7777)]
    [InlineData("example.org", "example.org", Protocol.DefaultPort)]
    [InlineData(" host.lan:7676 ", "host.lan", 7676)]
    [InlineData("[::1]:9000", "::1", 9000)]
    [InlineData(":7680", "", 7680)]
    public void AddressesParse(string text, string host, int port)
    {
        bool ok = Discovery.TryParseAddress(text, out var h, out var p);
        // ":port" is a port alone (a host's own address book entry); it is no address to join
        Assert.Equal(host.Length > 0, ok);
        Assert.Equal(port, p);
        if (ok) Assert.Equal(host, h);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:notaport")]
    [InlineData("host:70000")]
    [InlineData("two words")]
    public void BadAddressesDoNot(string text) => Assert.False(Discovery.TryParseAddress(text, out _, out _));

    /// <summary>
    /// A variant saved by CONFIGURE CHASSIS (the player's own file) is played as
    /// its model's first stock variant; a listen server does not take the cars
    /// in its process's overlay, which only the host has.
    /// </summary>
    [Fact]
    public void ASavedVariantPlaysAsItsModelsStockCar()
    {
        if (HostTests.InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = HostTests.InstallDir, Arena = "m01" });
        Assert.Equal("vcgcour2.vcf", host.CarFor("vcgcour2.vcf"));
        Assert.Equal("vcgcour1.vcf", host.CarFor("vcgcour9.vcf"));
        Assert.Equal(host.Options.DefaultCar, host.CarFor("nosuch7.vcf"));
        AssetVfs.Addon["vfpalmn7.vcf"] = host.Vfs.Read("vfpalmn2.vcf");
        try
        {
            Assert.Equal("vfpalmn1.vcf", host.CarFor("vfpalmn7.vcf"));
            Assert.Equal("vfpalmn1.vcf", host.Join("p", "vfpalmn7.vcf", 0).Player.CarFile);
        }
        finally { AssetVfs.Addon.TryRemove("vfpalmn7.vcf", out _); }
    }

    /// <summary>
    /// A running server answers a query at its address and a LAN search, with
    /// its name, arena, seats and drivers, and the answer follows a join.
    /// </summary>
    [Fact]
    public void AServerAnswersTheAddressBookAndTheLan()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "m01", Port = 0, Name = "Crater Grudge", MaxPlayers = 3, TeamPlay = true, MaxScore = 5000, InviteOnly = true,
        });
        server.Start();
        using var browser = new SessionBrowser();
        SessionBrowser.Found? Wait(Func<SessionBrowser.Found, bool> ok, Action ask)
        {
            for (int i = 0; i < 100; i++)
            {
                if (i % 10 == 0) ask();
                browser.Poll();
                if (browser.Sessions.FirstOrDefault(ok) is { } f) return f;
                Thread.Sleep(20);
            }
            return null;
        }
        var found = Wait(_ => true, () => browser.Ask("127.0.0.1", server.Port));
        Assert.NotNull(found);
        var i = found!.Info;
        Assert.Equal(("Crater Grudge", "m01", 0, 3, true, true, 5000, false), (i.Name, i.Arena, i.Players, i.MaxPlayers, i.TeamPlay, i.InviteOnly, i.MaxScore, i.Over));
        Assert.Equal(server.Port, found.EndPoint.Port);

        using var client = new MeleeClient();
        client.Connect("127.0.0.1", server.Port, "Groover", "vcgcour9.vcf", team: 2);
        // invite only: the join waits for the host's answer (here, the console's)
        for (int t = 0; t < 200 && !client.WaitingForHost; t++) { client.Poll(); Thread.Sleep(20); }
        Assert.True(client.WaitingForHost);
        Assert.Equal("Groover", server.PendingInvite?.Name);
        server.AnswerInvite(InviteAnswer.Accept);
        for (int t = 0; t < 200 && client.State == ClientState.Connecting; t++) { client.Poll(); Thread.Sleep(20); }
        Assert.Equal(ClientState.Joined, client.State);
        var after = Wait(f => f.Info.Players == 1, () => browser.Ask("127.0.0.1", server.Port));
        Assert.NotNull(after);
        Assert.Equal([new SessionDriver("Groover", "vcgcour1.vcf", 2)], after!.Info.Drivers);

        // a LAN search: one answer per game whichever way it came (broadcast, or this machine's own address)
        using var lan = new SessionBrowser();
        for (int k = 0; k < 100 && lan.Sessions.Count == 0; k++)
        {
            if (k % 10 == 0) lan.SearchLan(server.Port);
            lan.Poll();
            Thread.Sleep(20);
        }
        Assert.Single(lan.Sessions);
        Assert.Equal("Crater Grudge", lan.Sessions[0].Info.Name);
    }
}
