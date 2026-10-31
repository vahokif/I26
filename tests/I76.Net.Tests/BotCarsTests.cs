using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Session;
using I76.Sim.Trip;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// The cars the server drives (bots and the AI fill) under WEAPON RESTRICTIONS: the same car on every machine, the host's own
/// screen as a client's, the weapons the game forbids taken off by the server (the same model and paint, never another car)
/// and sent to the clients as the file, so each draws what the server has. Also after the host leaves.
/// </summary>
[Collection("AddonOverlay")]
public class BotCarsTests(ITestOutputHelper output)
{
    /// <summary>What a machine builds for a car as the server named it: the model, paint, variant and the weapons' files.</summary>
    private record Look(string Vdf, string Vtf, string Variant, string[] Weapons, int Mounted)
    {
        public bool Same(Look o) => Vdf == o.Vdf && Vtf == o.Vtf && Variant == o.Variant && Weapons.SequenceEqual(o.Weapons) && Mounted == o.Mounted;
        public override string ToString() => $"{Vdf} {Vtf} \"{Variant}\" [{string.Join(",", Weapons)}] mounted {Mounted}";
    }

    private static Look LookOf(CarInfo c, AssetVfs vfs)
    {
        var vcf = c.Vcf ?? vfs.Read(c.Def);
        var t = TripCar.FromVcf(vcf);
        return new Look(t.Vdf, t.Vtf, t.Variant, [.. t.Weapons.Select(w => w.Gdf.ToLowerInvariant())], 0);
    }

    /// <summary>A weaponless own variant for the humans (the garage's: the game forbids every weapon).</summary>
    private static byte[] Unarmed(AssetVfs vfs, string stem, string name)
    {
        var car = TripCar.FromVcf(vfs.Read(stem + "1.vcf"));
        car.Variant = name;
        car.Weapons = [];
        return car.ToVcf();
    }

    private static bool Wait(Func<bool> done, Action poll, int ms = 20000)
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && t.ElapsedMilliseconds < ms) { poll(); Thread.Sleep(5); }
        return done();
    }

    /// <summary>The cars the host's own player and a second client are told of, as each machine's mirror builds them.</summary>
    private void Compare(string install, WeaponRestrictions r, bool armed)
    {
        var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Ai = 3, Bots = 4, MaxPlayers = 8, Restrictions = r, ListenHost = true, Seed = 5 });
        server.Log += m => output.WriteLine($"[host] {m}");
        server.Start();
        var vfs = server.Invoke(h => h.Vfs);
        var host = new MeleeClient();
        var other = new MeleeClient();
        try
        {
            // the humans bring cars the game allows: any (none restricted) or their own unarmed ones
            if (armed) { host.Connect("127.0.0.1", server.Port, "Hal", "vppirna1.vcf"); }
            else host.Connect("127.0.0.1", server.Port, "Hal", "vppirna4.vcf", config: Unarmed(vfs, "vppirna", "Hal's"));
            Assert.True(Wait(() => host.State == ClientState.Joined, host.Poll), host.Reason);
            if (armed) other.Connect("127.0.0.1", server.Port, "Cy", "vcgcour1.vcf");
            else other.Connect("127.0.0.1", server.Port, "Cy", "vcgcour4.vcf", config: Unarmed(vfs, "vcgcour", "Cy's"));
            Assert.True(Wait(() => other.State == ClientState.Joined, () => { host.Poll(); other.Poll(); }), other.Reason);

            var truth = server.Invoke(h => h.Setup.World.Vehicles.Where(v => v.NetId != 0).Select(v => ((IModeHost)h).Info(v)).ToList());
            var bots = server.Invoke(h => h.Bots.Select(b => (b.Player.CarFile, b.Player.Car.NetId & 0xffffff)).ToList());
            Assert.Equal(4, bots.Count);
            output.WriteLine($"server: {truth.Count} cars; bots {string.Join(", ", bots.Select(b => b.CarFile))}");

            // each machine builds its mirror from what it was sent, as the game's Godot side does
            foreach (var (who, c) in new[] { ("host's own", host), ("client", other) })
            {
                var accept = c.Accept!;
                var mirror = new ClientWorld(HostTests.Mirror("m01"), accept);
                var told = accept.Cars.Select(x => x.Serial).ToHashSet();       // the host's own player was told before the second joined
                Assert.Equal(told.Count, mirror.Cars.Count);
                Assert.True(told.Count >= truth.Count - 1);
                foreach (var t in truth.Where(t => told.Contains(t.Serial)))
                {
                    var m = mirror.Cars[t.Serial];
                    Assert.Null(m.StandInFor);                                   // built as the server named it, no stand-in car
                    Assert.Equal(t.Def, m.Info.Def);
                    var want = LookOf(t, vfs);
                    var got = LookOf(accept.Cars.Single(x => x.Serial == t.Serial), vfs);
                    Assert.True(want.Same(got), $"{who}: {t.Name}: {got} but the server has {want}");
                    // the car the mirror built: the weapons it carries are the file's
                    int mounted = m.Vehicle.Armory?.Weapons.Count ?? 0;
                    Assert.Equal(want.Weapons.Length, mounted);
                    if (!armed) Assert.Empty(want.Weapons);
                }
                foreach (var (file, serial) in bots)
                {
                    var look = LookOf(accept.Cars.Single(x => x.Serial == serial), vfs);
                    output.WriteLine($"{who}: bot {serial} {file}: {look}");
                    if (!armed) Assert.Equal(0, mirror.Cars[serial].Vehicle.Armory?.Weapons.Count ?? 0);
                    else Assert.NotEmpty(look.Weapons);
                }
            }
            // both machines name the same files and carry the same bytes for each
            Assert.Equal(host.Accept!.Cars.Select(c => (c.Serial, c.Def, c.Vcf?.Length)), other.Accept!.Cars.Where(c => host.Accept.Cars.Any(x => x.Serial == c.Serial)).Select(c => (c.Serial, c.Def, c.Vcf?.Length)));
            if (!armed)
            {
                // a bot is its chosen car, unarmed: the model and paint of the stock car it rolled, never a stand-in
                foreach (var (file, serial) in bots)
                {
                    var info = host.Accept.Cars.Single(c => c.Serial == serial);
                    var mine = LookOf(info, vfs);
                    Assert.True(new[] { "vppirna", "vcgcour", "valepre", "vastryd", "vdstag", "vmxmarx", "vfypony", "vcroyal", "vazamz" }.Any(s => FileStemMatches(vfs, s, mine)), $"{mine}: a model the bots drive");
                }
            }
        }
        finally { host.Dispose(); other.Dispose(); server.Dispose(); }
    }

    /// <summary>The car's body and paint are those of the stem's first variant (a bot's car is its stock car, only unarmed).</summary>
    private static bool FileStemMatches(AssetVfs vfs, string stem, Look l) =>
        Enumerable.Range(1, 3).Select(n => stem + n + ".vcf").Where(vfs.Exists).Select(f => TripCar.FromVcf(vfs.Read(f))).Any(t => t.Vdf == l.Vdf && t.Vtf == l.Vtf);

    [Fact]
    public void NoWeaponsGameBotsAndFillAreTheSameUnarmedCarsOnEveryMachine()
    {
        if (HostTests.InstallDir is not { } install) return;
        Compare(install, WeaponRestrictions.Everything, armed: false);
    }

    [Fact]
    public void WeaponsOnGameBotsAndFillKeepTheirStockArmedCarsOnEveryMachine()
    {
        if (HostTests.InstallDir is not { } install) return;
        Compare(install, WeaponRestrictions.None, armed: true);
    }

    [Fact]
    public void ARestrictedClassIsTakenOffTheBotsCarAndTheRestStays()
    {
        if (HostTests.InstallDir is not { } install) return;
        var r = WeaponRestrictions.Parse("droppers,slugs", null, out _)!;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 4, Ai = 2, MaxPlayers = 8, Restrictions = r, Seed = 5 });
        Assert.Equal(4, host.Bots.Count);
        var armed = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 4, Ai = 2, MaxPlayers = 8, Seed = 5 });
        foreach (var b in host.Bots)
        {
            var car = TripCar.FromVcf(host.Vfs.TryRead(b.Player.CarFile) ?? throw new Exception(b.Player.CarFile));
            Assert.Empty(r.Violations(host.Vfs, car.Weapons.Select(w => w.Gdf)));
            output.WriteLine($"{b.Player.CarFile}: {string.Join(", ", car.Weapons.Select(w => w.Gdf))}");
        }
        // the AI fill too
        foreach (var v in host.Setup.World.Vehicles.Where(v => v.Definition is { Length: > 0 } d && d.StartsWith('n')))
            Assert.Empty(r.Violations(host.Vfs, TripCar.FromVcf(host.Vfs.Read(v.Definition!)).Weapons.Select(w => w.Gdf)));
        Assert.Equal(4, armed.Bots.Count);
    }

    /// <summary>
    /// The host leaves a no-weapons game with bots and the AI fill: the player who takes over (built from the savestate's own
    /// options, so the restrictions too) drives the same cars, unarmed, as the same files, and so do the bots after it.
    /// </summary>
    [Fact]
    public void TheNewHostKeepsTheSameUnarmedBotCars()
    {
        if (HostTests.InstallDir is not { } install) return;
        var server = new NetMeleeServer(new ServerOptions
        {
            Install = install, Arena = "m01", Port = 0, Rounds = true, TimeLimit = 600, HostMigration = true, Name = "migrate", Seed = 5,
            ListenHost = true, Ai = 2, Bots = 3, MaxPlayers = 8, Restrictions = WeaponRestrictions.Everything,
        });
        server.Log += m => output.WriteLine($"[host] {m}");
        server.Start();
        var vfs = server.Invoke(h => h.Vfs);
        var host = new MeleeClient();
        host.Connect("127.0.0.1", server.Port, "Hal", "vppirna4.vcf", config: Unarmed(vfs, "vppirna", "Hal's"));
        var ann = new MeleeClient { CanHost = true, MigrationGiveUp = 6 };
        var standby = new MigrationStandby(install, m => output.WriteLine($"[Ann] {m}"));
        ann.Standby = standby;
        try
        {
            void Poll() { host.Poll(); ann.Poll(); if (ann.State is ClientState.Joined or ClientState.Migrating) ann.SendInput(new CarInput { Buttons = CarButtons.ThrottleUp }); if (host.State == ClientState.Joined) host.SendInput(new CarInput { Buttons = CarButtons.ThrottleUp }); }
            Assert.True(Wait(() => host.State == ClientState.Joined, Poll), host.Reason);
            server.MigrationOwner = host.Accept!.Slot;
            ann.Connect("127.0.0.1", server.Port, "Ann", "vcgcour4.vcf", config: Unarmed(vfs, "vcgcour", "Ann's"));
            Assert.True(Wait(() => ann.State == ClientState.Joined, Poll), ann.Reason);
            uint now = server.Invoke(h => h.Tick);
            Assert.True(Wait(() => server.SuccessorSlot == ann.Accept!.Slot && ann.MigrationLink is { State: not null } l && l.StateTick > now && standby.Ready, Poll, 40000), "no savestate");

            var before = server.Invoke(h => h.Setup.World.Vehicles.Where(v => v.NetId != 0).Select(v => ((IModeHost)h).Info(v)).ToList());
            var botsBefore = server.Invoke(h => h.Bots.Select(b => (b.Slot, b.Player.CarFile)).ToList());
            Assert.Equal(3, botsBefore.Count);
            Assert.All(before, c => Assert.Empty(LookOf(c, vfs).Weapons));

            host.Dispose();
            server.Dispose();
            Assert.True(Wait(() => ann.State == ClientState.Joined && ann.Migrations == 1, Poll, 20000), $"{ann.State} {ann.Reason}");
            var now2 = Assert.IsType<NetMeleeServer>(standby.Server);
            var vfs2 = now2.Invoke(h => h.Vfs);
            var after = now2.Invoke(h => h.Setup.World.Vehicles.Where(v => v.NetId != 0).Select(v => ((IModeHost)h).Info(v)).ToList());
            var botsAfter = now2.Invoke(h => h.Bots.Select(b => (b.Slot, b.Player.CarFile)).ToList());
            output.WriteLine($"before {string.Join(", ", botsBefore.Select(b => b.CarFile))}; after {string.Join(", ", botsAfter.Select(b => b.CarFile))}");
            Assert.Equal(botsBefore, botsAfter);
            // every car of the old game is in the new one as the same file, the same model, paint and (no) weapons
            foreach (var c in before.Where(c => c.Owner < 0 || botsBefore.Any(b => b.Slot == c.Owner) || c.Owner != host.Accept!.Slot))
            {
                var n = after.SingleOrDefault(x => x.Serial == c.Serial);
                Assert.NotNull(n);
                Assert.Equal(c.Def, n!.Def);
                Assert.True(LookOf(c, vfs).Same(LookOf(n, vfs2)), c.Name);
                Assert.Empty(LookOf(n, vfs2).Weapons);
                if (c.Vcf is not null) Assert.True(c.Vcf.AsSpan().SequenceEqual(n.Vcf), c.Name);
            }
        }
        finally { host.Dispose(); ann.Dispose(); standby.Server?.Dispose(); standby.Dispose(); server.Dispose(); }
    }
}
