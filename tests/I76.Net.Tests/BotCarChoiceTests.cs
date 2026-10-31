using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Trip;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// The host's choice of each bot's car (docs/multiplayer.md "Bots", ServerOptions.BotChoices): bot seat N drives choice N whenever a bot
/// enters that seat, the same model, variant and weapons on every machine; Random still rolls; the game's rules still apply (restricted
/// weapons come off a chosen car, hidden cars need the setting); and with every weapon restricted a bot left on Random prefers the host's
/// saved variants that carry none. They share the process-wide addon overlay (where a host's saved variants live) with the other bot
/// tests that restrict weapons, so those run apart from these ("AddonOverlay").
/// </summary>
[Collection("AddonOverlay")]
public class BotCarChoiceTests(ITestOutputHelper output)
{
    private const string Saved = "vfdclyd5.vcf";

    private static bool Wait(Func<bool> done, Action poll, int ms = 20000)
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && t.ElapsedMilliseconds < ms) { poll(); Thread.Sleep(5); }
        return done();
    }

    /// <summary>The bots in seat order: bot seat 1 is slot 63, seat 2 slot 62.</summary>
    private static List<BotSeat> BySeat(MeleeHost h) => [.. h.Bots.OrderByDescending(b => b.Slot)];

    private static (string Vdf, string Vtf, string Variant, string[] Weapons) Look(byte[] vcf)
    {
        var t = TripCar.FromVcf(vcf);
        return (t.Vdf, t.Vtf, t.Variant, [.. t.Weapons.Select(w => w.Gdf.ToLowerInvariant())]);
    }

    /// <summary>A saved variant of a stock model: its first variant renamed and stripped of its weapons (or with only the first kept).</summary>
    private static byte[] SavedVariant(AssetVfs vfs, string stem, string name, bool unarmed)
    {
        var car = TripCar.FromVcf(vfs.Read(stem + "1.vcf"));
        car.Variant = name;
        car.Weapons = unarmed ? [] : [.. car.Weapons.Take(1)];
        return car.ToVcf();
    }

    private static bool Same((string, string, string, string[]) a, (string, string, string, string[]) b) =>
        a.Item1 == b.Item1 && a.Item2 == b.Item2 && a.Item3 == b.Item3 && a.Item4.SequenceEqual(b.Item4);

    /// <summary>Each bot is its seat's chosen car, identical (model, paint, variant, weapons) on the host's and a client's machine; the unchosen seat rolls.</summary>
    [Fact]
    public void EachBotIsItsSeatsChosenCarOnEveryMachine()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "m01", Port = 0, ListenHost = true, Seed = 5, Bots = 3, MaxPlayers = 6,
            BotChoices = ["vcgcour2.vcf", "", "vdstag:3"],
        });
        server.Log += m => output.WriteLine($"[host] {m}");
        server.Start();
        var vfs = server.Invoke(h => h.Vfs);
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "Cy", "vppirna1.vcf");
        Assert.True(Wait(() => c.State == ClientState.Joined, c.Poll), c.Reason);
        var bots = server.Invoke(h => BySeat(h).Select(b => (b.Slot, b.Player.CarFile, Serial: b.Player.Car.NetId & 0xffffff, Armed: b.Player.Car.Armory?.Weapons.Count ?? 0)).ToList());
        Assert.Equal([63, 62, 61], bots.Select(b => b.Slot));
        Assert.Equal("vcgcour2.vcf", bots[0].CarFile);
        Assert.Equal("vdstag3.vcf", bots[2].CarFile);
        Assert.Contains(bots[1].CarFile, new MeleeHost(new ServerOptions { Install = install, Arena = "m01" }).Options.BotCars);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), c.Accept!);
        foreach (var b in bots)
        {
            var info = c.Accept!.Cars.Single(x => x.Serial == b.Serial);
            Assert.Equal(b.CarFile, info.Def);
            var theirs = Look(info.Vcf ?? vfs.Read(info.Def));
            var ours = Look(vfs.Read(b.CarFile));
            Assert.True(Same(ours, theirs), $"{b.CarFile}: {string.Join(",", theirs.Weapons)}");
            Assert.Equal(b.Armed, mirror.Cars[b.Serial].Vehicle.Armory?.Weapons.Count ?? 0);
            Assert.Equal(ours.Weapons.Length, b.Armed);
        }
        Assert.Equal("vcgcour", Look(vfs.Read(bots[0].CarFile)).Vdf[..7]);
        Assert.Equal(TripCar.FromVcf(vfs.Read("vcgcour2.vcf")).Variant, Look(vfs.Read(bots[0].CarFile)).Variant);
    }

    /// <summary>Seats left on Random still roll: different cars from seed to seed, all from the bot list.</summary>
    [Fact]
    public void RandomStillRolls()
    {
        if (HostTests.InstallDir is not { } install) return;
        var seen = new HashSet<string>();
        for (int seed = 1; seed <= 3; seed++)
        {
            var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 8, MaxPlayers = 12, Seed = seed, BotChoices = ["", "random", ""] });
            Assert.Equal(8, host.Bots.Count);
            foreach (var b in host.Bots) { Assert.Contains(b.Player.CarFile, host.Options.BotCars); seen.Add(b.Player.CarFile); }
        }
        output.WriteLine(string.Join(", ", seen));
        Assert.True(seen.Count > 2);
    }

    /// <summary>A restriction takes its weapons off the chosen car (the model, paint and variant name stay), served as a file; a car with none is as it was.</summary>
    [Fact]
    public void ARestrictionStripsTheChosenCar()
    {
        if (HostTests.InstallDir is not { } install) return;
        var plain = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, MaxPlayers = 4, BotChoices = ["vppirna2.vcf"] });
        var stock = TripCar.FromVcf(plain.Vfs.Read("vppirna2.vcf"));
        Assert.Equal("vppirna2.vcf", plain.Bots[0].Player.CarFile);
        Assert.NotEmpty(stock.Weapons);
        var first = stock.Weapons[0].Gdf.Replace(".gdf", "", StringComparison.OrdinalIgnoreCase);
        foreach (var spec in new[] { first, "all" })
        {
            var r = spec == "all" ? WeaponRestrictions.Everything : WeaponRestrictions.Parse(spec, plain.Vfs, out _)!;
            var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, MaxPlayers = 4, BotChoices = ["vppirna2.vcf"], Restrictions = r });
            var file = host.Bots[0].Player.CarFile;
            Assert.NotEqual("vppirna2.vcf", file);
            var car = TripCar.FromVcf(host.Vfs.Read(file));
            Assert.Empty(r.Violations(host.Vfs, car.Weapons.Select(w => w.Gdf)));
            Assert.True(car.Weapons.Count < stock.Weapons.Count, spec);
            Assert.Equal((stock.Vdf, stock.Vtf, stock.Variant), (car.Vdf, car.Vtf, car.Variant));
            Assert.Equal(car.Weapons.Count, host.Bots[0].Player.Car.Armory?.Weapons.Count ?? 0);
        }
    }

    /// <summary>A hidden model is the chosen car only when the host allows hidden content; otherwise the seat rolls a stock car.</summary>
    [Fact]
    public void AHiddenCarNeedsHiddenContent()
    {
        if (HostTests.InstallDir is not { } install) return;
        var probe = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var hidden = HiddenContent.HiddenModels(probe.Vfs, probe.OfferedModels);
        if (hidden.Count == 0) return;
        var file = hidden[0] + "1.vcf";
        var off = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, MaxPlayers = 4, BotChoices = [file] });
        Assert.NotEqual(file, off.Bots[0].Player.CarFile);
        Assert.Contains(off.Bots[0].Player.CarFile, off.Options.BotCars);
        var on = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, MaxPlayers = 4, BotChoices = [file], AllowHidden = true });
        Assert.Equal(file, on.Bots[0].Player.CarFile);
    }

    /// <summary>The Nitro Pack's own cars: a Nitro-only model is chosen as it is, and a chosen variant is that variant.</summary>
    [Fact]
    public void ANitroInstallsCarsAreChosenFromItsList()
    {
        var nitro = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL");
        if (nitro is not { Length: > 0 } || !Directory.Exists(nitro)) return;
        var probe = new MeleeHost(new ServerOptions { Install = nitro, Arena = "N26.RAC", Kind = I76.Sim.Missions.MeleeKind.Race });
        var rows = I76.Formats.Shell.CarTable.NitroMelee(probe.Vfs.Install);
        var row = rows.Last(r => r.BuiltIn >= 2 && probe.Vfs.TryRead(r.Stem + "2.vcf") is not null);
        var file = row.Stem + "2.vcf";
        var host = new MeleeHost(new ServerOptions { Install = nitro, Arena = "N26.RAC", Kind = I76.Sim.Missions.MeleeKind.Race, Bots = 1, MaxPlayers = 4, BotChoices = [file] });
        Assert.Equal(file, host.Bots[0].Player.CarFile);
    }

    /// <summary>Seat N's choice goes with the seat: a bot sent away for a human and the one that comes back in its place have their seats' cars.</summary>
    [Fact]
    public void ASeatKeepsItsCarWhenItsBotLeavesAndComesBack()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m05", Bots = 3, MaxPlayers = 4, BotChoices = ["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"] });
        string[] Cars() => [.. BySeat(host).Select(b => b.Player.CarFile)];
        Assert.Equal(["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"], Cars());
        var a = host.Join("a", "vppirna1.vcf", 0);                  // four seats: still three bots
        Assert.Equal(["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"], Cars());
        var b2 = host.Join("b", "vppirna1.vcf", 0);                 // the first bot (seat 1, slot 63) left
        Assert.Equal([62, 61], BySeat(host).Select(b => b.Slot));
        Assert.Equal(["vdstag3.vcf", "vfypony1.vcf"], Cars());
        var c2 = host.Join("c", "vppirna1.vcf", 0);
        Assert.Equal(["vfypony1.vcf"], Cars());
        host.Leave(c2, "left");                                     // a bot comes back to the first free seat, with that seat's car
        Assert.Equal(["vcgcour2.vcf", "vfypony1.vcf"], Cars());
        host.Leave(b2, "left");
        Assert.Equal(["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"], Cars());
        host.Leave(a, "left");
        Assert.Equal(["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"], Cars());
        Assert.Equal([63, 62, 61], BySeat(host).Select(b => b.Slot));
    }

    /// <summary>
    /// The host's saved variants: one is offered as a bot's car (served by content, the same bytes on the client), and under a game that allows
    /// no weapons a bot on Random prefers the saved variants that carry none; with some armed ones only, or none, it is the stripped stock car.
    /// </summary>
    [Fact]
    public void SavedVariantsAreChoosableAndPreferredUnderRestrictions()
    {
        if (HostTests.InstallDir is not { } install) return;
        var probe = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var armed = SavedVariant(probe.Vfs, "vfdclyd", "Host's armed", unarmed: false);
        var bare = SavedVariant(probe.Vfs, "vfdclyd", "Host's bare", unarmed: true);
        try
        {
            AssetVfs.Addon[Saved] = armed;
            // chosen: the file reaches the client by content, the host's own and the client's builds agree
            using (var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, ListenHost = true, Seed = 5, Bots = 1, MaxPlayers = 4, BotChoices = [Saved] }))
            {
                server.Start();
                using var c = new MeleeClient();
                c.Connect("127.0.0.1", server.Port, "Cy", "vppirna1.vcf");
                Assert.True(Wait(() => c.State == ClientState.Joined, c.Poll), c.Reason);
                var (file, serial) = server.Invoke(h => (h.Bots[0].Player.CarFile, h.Bots[0].Player.Car.NetId & 0xffffff));
                var info = c.Accept!.Cars.Single(x => x.Serial == serial);
                Assert.NotEqual(Saved, file);                                   // named by its content, since the client has no such file
                Assert.Equal(file, info.Def);
                Assert.NotNull(info.Vcf);
                Assert.True(armed.AsSpan().SequenceEqual(info.Vcf), "the host's saved variant, byte for byte");
                Assert.Equal("Host's armed", Look(info.Vcf!).Variant);
            }
            // Random with an armed saved variant and every weapon restricted: the stripped stock car, not the saved one
            var all = WeaponRestrictions.Everything;
            var h1 = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 4, MaxPlayers = 8, Seed = 3, Restrictions = all });
            Assert.All(h1.Bots, b => Assert.Empty(Look(h1.Vfs.Read(b.Player.CarFile)).Weapons));
            Assert.DoesNotContain(h1.Bots, b => Look(h1.Vfs.Read(b.Player.CarFile)).Variant == "Host's armed");
            // a saved variant with none: every bot on Random takes it (the only saved one that is legal)
            AssetVfs.Addon["vfdclyd6.vcf"] = bare;
            var h2 = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 4, MaxPlayers = 8, Seed = 3, Restrictions = all });
            Assert.All(h2.Bots, b => { var l = Look(h2.Vfs.Read(b.Player.CarFile)); Assert.Equal("Host's bare", l.Variant); Assert.Empty(l.Weapons); });
            // a chosen seat is still its choice (stripped), the rest prefer the saved variant
            var h3 = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 3, MaxPlayers = 8, Seed = 3, Restrictions = all, BotChoices = ["vppirna1.vcf"] });
            var seats = BySeat(h3).Select(b => Look(h3.Vfs.Read(b.Player.CarFile))).ToList();
            Assert.Equal(TripCar.FromVcf(h3.Vfs.Read("vppirna1.vcf")).Variant, seats[0].Variant);
            Assert.All(seats.Skip(1), l => Assert.Equal("Host's bare", l.Variant));
            // without restrictions Random does not look at the saved variants
            var h4 = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 8, MaxPlayers = 12, Seed = 3 });
            Assert.All(h4.Bots, b => Assert.Contains(b.Player.CarFile, h4.Options.BotCars));
            // a restriction that leaves the armed saved variant legal as it is: Random takes it (and not the bare one, which is legal too: either)
            AssetVfs.Addon.TryRemove("vfdclyd6.vcf", out _);
            var group = new[] { "slugs", "mortars", "missiles", "flames", "droppers" }.First(g => WeaponRestrictions.Parse(g, null, out _)!.Violations(probe.Vfs, TripCar.FromVcf(armed).Weapons.Select(w => w.Gdf)).Count == 0);
            var some = WeaponRestrictions.Parse(group, null, out _)!;
            var h5 = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 4, MaxPlayers = 8, Seed = 3, Restrictions = some });
            Assert.All(h5.Bots, b => Assert.Equal("Host's armed", Look(h5.Vfs.Read(b.Player.CarFile)).Variant));
        }
        finally { AssetVfs.Addon.TryRemove(Saved, out _); AssetVfs.Addon.TryRemove("vfdclyd6.vcf", out _); }
    }

    /// <summary>The successor takes the choices over with the game (they are in the savestate's options): the bots keep their cars, and a seat refilled afterwards is its choice.</summary>
    [Fact]
    public void HostMigrationKeepsTheChoices()
    {
        if (HostTests.InstallDir is not { } install) return;
        var o = new ServerOptions { Install = install, Arena = "m05", Bots = 3, MaxPlayers = 3, Seed = 7, BotChoices = ["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"], HostMigration = true };
        var host = new MeleeHost(o);
        var a = host.Join("a", "vppirna1.vcf", 1);
        for (int k = 0; k < 20 * 10; k++) host.Step();
        var state = host.Session.Snapshot();
        // the options as the savestate carries them (JSON in its extras), as a successor reads them
        var json = System.Text.Json.JsonSerializer.Deserialize<ServerOptions>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o with { Install = "" }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }), new System.Text.Json.JsonSerializerOptions { IncludeFields = true })!;
        Assert.Equal(o.BotChoices, json.BotChoices);
        var fresh = new MeleeHost(json with { Install = install });
        fresh.RestoreMigrated(state);
        Assert.Equal(BySeat(host).Select(b => (b.Slot, b.Player.CarFile)), BySeat(fresh).Select(b => (b.Slot, b.Player.CarFile)));
        // the human leaves the successor's game: the seat's bot comes back with its choice
        Assert.Equal(["vdstag3.vcf", "vfypony1.vcf"], BySeat(fresh).Select(b => b.Player.CarFile));
        var seat = fresh.Seats.FirstOrDefault();
        output.WriteLine($"seats {fresh.Seats.Count} bots {fresh.Bots.Count}");
        Assert.NotNull(seat);
        fresh.Leave(seat, "left");
        Assert.Equal(["vcgcour2.vcf", "vdstag3.vcf", "vfypony1.vcf"], BySeat(fresh).Select(b => b.Player.CarFile));
    }

    [Theory]
    [InlineData("vppirna", "vppirna1.vcf")]
    [InlineData("VPPIRNA:2", "vppirna2.vcf")]
    [InlineData("vcgcour3.vcf", "vcgcour3.vcf")]
    [InlineData("vdstag3", "vdstag3.vcf")]
    [InlineData("random", null)]
    [InlineData("", null)]
    [InlineData("../x", null)]
    public void ASpecNamesACarFile(string spec, string? file) => Assert.Equal(file, MeleeHost.ParseCarSpec(spec));
}
