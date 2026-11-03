using System.Diagnostics;
using System.Text.RegularExpressions;
using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Trip;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// The names the join roster shows for a listed game (a tester's report: its MAKE / MODEL column showed "NF8023A9BVCGCOUR.VCF",
/// the file a car is served under, and the game list a map's file name): each car by a name the asker can read its make and model
/// from (an install's car file, or the body of a car served by its content), with the host's words only for a body the asker may
/// lack; the game by its GAME NAME, else its arena's AREA OF PLAY name, never a file name.
/// </summary>
[Collection("AddonOverlay")]
public class RosterNamesTests(ITestOutputHelper output)
{
    private static string? Nitro =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The file name a car served by its content goes by (n&lt;hash&gt;&lt;stem&gt;.vcf).</summary>
    private static bool Served(string car) => Regex.IsMatch(car, "^n[0-9a-f]{8}", RegexOptions.IgnoreCase);

    /// <summary>The listing as an asker reads it: written and read back off the wire.</summary>
    private static SessionInfo Wire(SessionInfo i)
    {
        var w = new NetWriter();
        i.Write(w);
        Assert.True(w.Length <= SessionInfo.AnswerBytes);
        return SessionInfo.TryRead(w.Span)!;
    }

    /// <summary>A driver's MAKE / MODEL as an asker with <paramref name="vfs"/> reads it from the listing (the game's MeleeData.CarName, less its table).</summary>
    private static string NameOf(AssetVfs vfs, SessionDriver d) => CarNames.Of(vfs, d.Car) ?? d.Model;

    [Fact]
    public void ASharedVariantIsListedByItsBodyNotTheFileItIsServedUnder()
    {
        if (HostTests.InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = HostTests.InstallDir, Arena = "m01" });
        var vfs = host.Vfs;
        var vcf = VariantTests.Custom(vfs);
        var mine = host.Join("me", "vppirna4.vcf", 0, null, vcf);
        var stock = host.Join("you", "vcgcour2.vcf", 0);
        Assert.True(Served(mine.Player.CarFile), mine.Player.CarFile);

        var listed = Wire(host.Info()).Drivers;
        output.WriteLine(string.Join("; ", listed.Select(d => $"{d.Name} {d.Car} \"{d.Model}\" = {NameOf(vfs, d)}")));
        var me = listed.Single(d => d.Name == "me");
        Assert.Equal(CarNames.BodyOf(vcf), me.Car);
        Assert.Equal("", me.Model);                                          // the archive's body: every asker has it
        string piranha = CarNames.Of(vfs, "vppirna1.vcf")!;
        Assert.False(string.IsNullOrEmpty(piranha));
        Assert.Equal(piranha, NameOf(vfs, me));
        var you = listed.Single(d => d.Name == "you");
        Assert.Equal("vcgcour2.vcf", you.Car);
        Assert.Equal(CarNames.Of(vfs, "vcgcour1.vcf"), NameOf(vfs, you));

        // the join accept names the car by the served file and carries its content: the STANDINGS read the make and model from it
        var info = host.AcceptFor(stock).Cars.Single(c => c.Owner == mine.Slot);
        Assert.True(Served(info.Def));
        Assert.Equal(piranha, CarNames.Of(vfs, CarNames.BodyOf(info.Vcf!)!));
    }

    [Fact]
    public void BotsCarsServedByContentAreListedByTheirBodies()
    {
        if (HostTests.InstallDir is not { } install) return;
        // no weapons allowed: every bot's car is served unarmed, by its content
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 4, MaxPlayers = 8, Restrictions = WeaponRestrictions.Everything, Seed = 5 });
        Assert.All(host.Bots, b => Assert.True(Served(b.Player.CarFile), b.Player.CarFile));
        Check(host, 4);
    }

    /// <summary>The tester's game: a Nitro race with bots, their cars served by their content as the screenshot's (here as no weapons are allowed).</summary>
    [Fact]
    public void ANitroRacesBotsAreListedByMakeAndModel()
    {
        if (Nitro is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "N25.RAC", Bots = 3, MaxPlayers = 8, Seed = 5, Name = "Shooting Bots", Restrictions = WeaponRestrictions.Everything });
        Assert.All(host.Bots, b => Assert.True(Served(b.Player.CarFile), b.Player.CarFile));
        var listed = Check(host, 3);
        var i = Wire(host.Info());
        Assert.Equal(("Shooting Bots", "Mojave Sprint"), (i.Name, i.ArenaName));
        Assert.Equal("Mojave Sprint", NetLines.AreaName(i, "Mojave Sprint"));
        Assert.All(listed, d => Assert.Equal("", d.Model));
    }

    private List<SessionDriver> Check(MeleeHost host, int bots)
    {
        Assert.Equal(bots, host.Bots.Count);
        var listed = Wire(host.Info()).Drivers.ToList();
        output.WriteLine(string.Join("; ", host.Bots.Select(b => b.Player.CarFile)));
        output.WriteLine(string.Join("; ", listed.Select(d => $"{d.Name} {d.Car} \"{d.Model}\" = {NameOf(host.Vfs, d)}")));
        Assert.Equal(bots, listed.Count);
        foreach (var (b, d) in host.Bots.Zip(listed))
        {
            Assert.False(Served(d.Car), d.Car);
            string name = NameOf(host.Vfs, d);
            Assert.False(string.IsNullOrWhiteSpace(name), d.Car);
            Assert.DoesNotContain(".vcf", name, StringComparison.OrdinalIgnoreCase);
            // the bot's own car's model, whatever file it is served under
            Assert.Equal(CarNames.Of(host.Vfs, CarNames.BodyOf(host.Vfs.Read(b.Player.CarFile))!), name);
        }
        return listed;
    }

    /// <summary>A body that is not the archive's (a community car): the host says its make and model, for an asker without it.</summary>
    [Fact]
    public void ACommunityBodyIsListedWithTheHostsWords()
    {
        if (HostTests.InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = HostTests.InstallDir, Arena = "m01" });
        var vfs = host.Vfs;
        var body = (byte[])vfs.Read(CarNames.BodyOf(vfs.Read("vppirna1.vcf"))!).Clone();
        var vdfc = new I76.Formats.Bwd2.Bwd2File(body).First("VDFC")!.Value;
        var words = "Zed Motors Zephyr\0\0\0"u8.ToArray();
        words.CopyTo(body, vdfc.PayloadOffset);
        AssetVfs.Addon["vzzephr.vdf"] = body;
        try
        {
            var vcf = VariantTests.Custom(vfs, c => c.Vdf = "vzzephr.vdf");
            var d = Wire(new SessionInfo(1, Protocol.Version, "g", "m01", 1, 8, false, false, 0, 0, false, [host.Listed("Zed", "nfeedbeefvzzephr.vcf", 0, vcf)])).Drivers.Single();
            Assert.Equal(("vzzephr.vdf", "Zed Motors Zephyr"), (d.Car, d.Model));
            AssetVfs.Addon.TryRemove("vzzephr.vdf", out _);           // an asker without the body still has the host's words
            Assert.Equal("Zed Motors Zephyr", NameOf(vfs, d));
            // an install's own car is listed by its file alone
            Assert.Equal(new SessionDriver("Al", "vppirna1.vcf", 2), host.Listed("Al", "vppirna1.vcf", 2));
        }
        finally { AssetVfs.Addon.TryRemove("vzzephr.vdf", out _); }
    }

    /// <summary>A co-op game in its lobby: a player's own variant by its body, the mission's own car as itself (the popup says so).</summary>
    [Fact]
    public void TheCoopLobbysListingNamesOwnVariantsByTheirBodies()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "t01", Coop = "t01", MaxPlayers = 4, Port = 0, Seed = 7, Name = "", ListenHost = true, Lobby = true, LoadSeconds = 30,
        });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Start();
        var vfs = server.Invoke(h => h.Vfs);
        using var hal = new MeleeClient();
        using var ann = new MeleeClient();
        hal.Connect("127.0.0.1", server.Port, "Hal", Protocol.MissionCar);
        Assert.True(Wait(() => hal.State == ClientState.InLobby, () => hal.Poll()), hal.Reason);
        var vcf = VariantTests.Custom(vfs);
        ann.Connect("127.0.0.1", server.Port, "Ann", "vppirna4.vcf", config: vcf);
        Assert.True(Wait(() => ann.State == ClientState.InLobby && hal.Lobby?.Members.Count == 2, () => { hal.Poll(); ann.Poll(); }), ann.Reason);
        SessionInfo? found = null;
        using (var b = new SessionBrowser())
            Assert.True(Wait(() => (found = b.Sessions.FirstOrDefault()?.Info) is { Players: 2 }, () => { b.Ask("127.0.0.1", server.Port); b.Poll(); hal.Poll(); ann.Poll(); }));
        output.WriteLine(string.Join("; ", found!.Drivers.Select(d => $"{d.Name} {d.Car} \"{d.Model}\"")));
        Assert.Equal([Protocol.MissionCar, CarNames.BodyOf(vcf)], found.Drivers.Select(d => d.Car));
        // no name given: the listing names the game by its mission, as the host form does
        string title = I76.Formats.Mission.MissionTitles.CoopTitle(new GameInstall(install), "t01");
        Assert.Equal((title, title), (found.Name, found.ArenaName));
    }

    private static bool Wait(Func<bool> done, Action poll, int ms = 15000)
    {
        var t = Stopwatch.StartNew();
        while (!done() && t.ElapsedMilliseconds < ms) { poll(); Thread.Sleep(10); }
        return done();
    }

    [Fact]
    public void AGameWithoutANameIsListedByItsArenasName()
    {
        if (HostTests.InstallDir is not { } install) return;
        var i = Wire(new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Name = " " }).Info());
        Assert.Equal(("Slick Track", "Slick Track"), (i.Name, i.ArenaName));
        Assert.Equal("Shooting Bots", Wire(new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Name = "Shooting Bots" }).Info()).Name);
    }

    /// <summary>The roster's words for a listed game: GAME NAME, else the arena's name (the host's for a custom map), never a file name.</summary>
    [Fact]
    public void TheRosterNeverShowsAFileNameForAGame()
    {
        static SessionInfo G(string name, string arena = "daylt2.rac", string arenaName = "Daylight Two", bool custom = true) =>
            new(1, Protocol.Version, name, arena, 1, 8, false, false, 0, 0, false, [], MapHash: custom ? new byte[32] : null, ArenaName: arenaName);
        Assert.Equal("Shooting Bots", NetLines.GameName(G("Shooting Bots"), null));
        Assert.Equal("Daylight Two", NetLines.GameName(G(""), null));
        Assert.Equal("Daylight Two", NetLines.GameName(G("DAYLT2.RAC"), null));
        Assert.Equal("Daylight Two", NetLines.GameName(G("daylt2"), null));
        Assert.Equal("Daylight Two", NetLines.GameName(G("N25.RAC"), null));
        // a custom map: the host's name for it, whatever the asker's copy calls itself; a stock one: the asker's own
        Assert.Equal("Daylight Two", NetLines.AreaName(G("x"), "Old Daylight"));
        Assert.Equal("Mojave Sprint", NetLines.AreaName(G("x", "N25.RAC", "Mojave Sprint", custom: false), "Mojave Sprint"));
        Assert.Equal("Slick Track", NetLines.AreaName(G("x", "m05", "", custom: false), "Slick Track"));
        // nothing known (a listing through the lobby server alone, for a map the asker lacks): the stem, not the file
        Assert.Equal("daylt2", NetLines.GameName(G("", arenaName: "", custom: false), ""));
    }

    [Fact]
    public void TheNamesRoundTripAndAnOlderAnswerHasNone()
    {
        var drivers = new[] { new SessionDriver("Ride", "vcgcour.vdf", 0), new SessionDriver("Zed", "vzzephr.vdf", 1, "Zed Motors Zephyr"), new SessionDriver("Tar Mack (bot)", "vdlight1.vcf", 0) };
        var info = new SessionInfo(1, Protocol.Version, "Shooting Bots", "N25.RAC", 3, 8, false, false, 0, 0, false, drivers, Crosshair: true, ArenaName: "Mojave Sprint");
        var back = Wire(info);
        Assert.Equal(drivers, back.Drivers);
        Assert.Equal("Mojave Sprint", back.ArenaName);
        Assert.True(back.Crosshair);
        // an answer that ends after the crosshair byte (before these names) still reads, with none
        var w = new NetWriter();
        info.Write(w);
        int names = 1 + "Mojave Sprint".Length + 1 + drivers.Sum(d => 1 + d.Model.Length);
        var cut = SessionInfo.TryRead(w.Span[..^names])!;
        Assert.True(cut.Crosshair);
        Assert.Equal("", cut.ArenaName);
        Assert.All(cut.Drivers, d => Assert.Equal("", d.Model));
        // a full game of long names still fits one datagram, the drivers cut to what fits
        var many = Enumerable.Range(0, 64).Select(i => new SessionDriver(new string('n', 24), "nfeedbeefvppirna.vcf", 1, "Twenty Character Car")).ToArray();
        var big = Wire(info with { Drivers = many });
        Assert.InRange(big.Drivers.Count, 10, 63);
        Assert.All(big.Drivers, d => Assert.Equal("Twenty Character Car", d.Model));
    }
}
