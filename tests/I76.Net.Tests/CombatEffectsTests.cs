using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// Combat effects on a network client (docs/multiplayer.md, "Own shots at once", "Flames and lingering hazards"): the own
/// shots drawn the moment they are fired and kept square with the server's, the flame throwers' streams on every client,
/// and the Chemical Mortar's cloud hurting on the server and showing on the clients.
/// </summary>
public partial class CombatEffectsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? Nitro =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>A client of one seat, headless: its mirror, its connection's buffers and its prediction.</summary>
    private sealed class Client
    {
        public required Seat Seat;
        public required ClientWorld Mirror;
        public readonly MeleeClient Net = new();
        public OwnCarPredictor? Predictor;
        public Combat Combat => Mirror.Setup.Combat;
    }

    private static Client Connect(MeleeHost host, Seat seat, MissionSetup mirror, bool predict = true, bool predictFire = true)
    {
        var c = new Client { Seat = seat, Mirror = new ClientWorld(mirror, host.AcceptFor(seat)) };
        if (predict) c.Predictor = new OwnCarPredictor(c.Mirror, c.Net, predictFire);
        return c;
    }

    /// <summary>A Nitro arena's mirror (as HostTests.Mirror, from the Nitro install).</summary>
    private static MissionSetup NitroMirror(string arena)
    {
        var install = new GameInstall(Nitro!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission(arena)!);
        return MissionSetup.Build(vfs, w, asm, melee: [], meleeRules: new MeleeRules { Network = true });
    }

    private static CarButtons Key(Vehicle car, Func<Weapon, bool> which)
    {
        int i = car.Armory!.Weapons.FindIndex(w => which(w));
        Assert.True(i >= 0, "the car has no such weapon");
        return (CarButtons)((uint)CarButtons.Hardpoint1 << i);
    }

    /// <summary>
    /// One tick for everyone: each client's input goes to the server and is predicted at once, the server steps, and the
    /// snapshot and events go out, reaching the clients <paramref name="late"/> ticks later.
    /// </summary>
    private sealed class Rig(MeleeHost host, int late)
    {
        public uint T;
        private readonly Queue<(uint Tick, List<(Client C, Snapshot S)> Snaps, EventBatch? Events)> _wire = new();
        public readonly List<Client> Clients = [];
        /// <summary>Server's own-car shot events by client, in order: (tick, muzzle).</summary>
        public readonly Dictionary<Client, List<(uint Tick, Vector3 Muzzle)>> ServerShots = [];
        public readonly Dictionary<Client, List<(uint Tick, Vector3 Muzzle)>> DrawnShots = [];
        /// <summary>Own shot events that reached a client's views from the server (should be none while it predicts its fire).</summary>
        public readonly Dictionary<Client, int> ServerShotsDrawn = [];
        /// <summary>The impacts each client's views got (all the server's).</summary>
        public readonly Dictionary<Client, List<Impact>> Impacts = [];
        /// <summary>The explosions and clicks each client's views got.</summary>
        public readonly Dictionary<Client, List<Explosion>> Explosions = [];
        public readonly Dictionary<Client, List<WeaponClick>> Clicks = [];

        public void Tick(Func<Client, CarInput> input)
        {
            T++;
            foreach (var c in Clients)
            {
                var i = input(c);
                c.Seat.Inputs.Add(new InputMessage(T, [i]));
                c.Predictor?.Step(T, i);
                // what the views draw at once: the predicted shots
                foreach (var e in c.Combat.Events.OfType<ShotFired>().Where(e => e.Shooter == c.Mirror.Own?.Vehicle))
                    (DrawnShots.TryGetValue(c, out var l) ? l : DrawnShots[c] = []).Add((T, e.Muzzle));
                c.Combat.Events.Clear();
            }
            host.Step();
            var events = host.TakeEvents();
            foreach (var c in Clients)
                foreach (var s in events.OfType<ShotEvent>().Where(s => s.Car == c.Seat.CarSerial))
                    (ServerShots.TryGetValue(c, out var l) ? l : ServerShots[c] = []).Add((T, s.Muzzle));
            _wire.Enqueue((host.Tick, Clients.Select(c => (c, PredictionTests.Through(host, c.Seat))).ToList(), events.Count > 0 ? new EventBatch(host.Tick, events) : null));
            while (_wire.Count > late)
            {
                var (_, snaps, batch) = _wire.Dequeue();
                foreach (var (c, s) in snaps)
                {
                    if (batch is not null) c.Net.Events.Enqueue(batch);
                    c.Net.Inject(s);
                }
            }
            foreach (var c in Clients)
            {
                c.Mirror.Advance(host.Session.TickSeconds, c.Net);
                c.Predictor?.Reconcile();
                c.Predictor?.Smooth(host.Session.TickSeconds);
                ServerShotsDrawn[c] = ServerShotsDrawn.GetValueOrDefault(c) + c.Combat.Events.OfType<ShotFired>().Count(e => e.Shooter == c.Mirror.Own?.Vehicle);
                (Impacts.TryGetValue(c, out var il) ? il : Impacts[c] = []).AddRange(c.Combat.Events.OfType<Impact>());
                (Explosions.TryGetValue(c, out var xl) ? xl : Explosions[c] = []).AddRange(c.Combat.Events.OfType<Explosion>());
                (Clicks.TryGetValue(c, out var kl) ? kl : Clicks[c] = []).AddRange(c.Combat.Events.OfType<WeaponClick>());
                c.Combat.Events.Clear();
            }
        }
    }

    /// <summary>
    /// The own machine gun's shots are drawn the tick the trigger is pulled, from the predicted car's muzzle, where the
    /// server then fires them (the same muzzle within a centimetre, driving at speed, the snapshots four ticks late); the
    /// server's copies (shot events and rounds) are not drawn again. Then the server destroys the gun: the shots the
    /// client drew before it heard are taken back, and it draws no more.
    /// </summary>
    [Fact]
    public void OwnShotsAreDrawnAtOnceAndTheServersAreNotDrawnAgain()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 3 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var c = Connect(host, seat, HostTests.Mirror("m01"));
        var rig = new Rig(host, late: 4);
        rig.Clients.Add(c);
        var gun = seat.Player.Car.Armory!.Weapons.First(w => w.Def.Kind == 1);
        var key = Key(seat.Player.Car, w => w == gun);
        bool fire = false;
        int drawnAtFirst = -1, predictedRoundsSeen = 0, ownServerRoundsShown = 0;
        CarInput Input(Client _) => new() { Buttons = CarButtons.ThrottleUp | (fire ? key : 0) };
        for (int i = 0; i < 60; i++) rig.Tick(Input);
        fire = true;
        for (int i = 0; i < 60; i++)
        {
            rig.Tick(Input);
            if (i == 0) drawnAtFirst = rig.DrawnShots.GetValueOrDefault(c)?.Count ?? 0;
            predictedRoundsSeen = Math.Max(predictedRoundsSeen, c.Combat.PredictedRounds.Count);
            ownServerRoundsShown += c.Combat.Rounds.Count(r => r.Shooter == c.Mirror.Own!.Vehicle);
        }
        var drawn = rig.DrawnShots[c];
        var server = rig.ServerShots[c];
        output.WriteLine($"speed {seat.Player.Car.Speed * 2.237:0} mph: {drawn.Count} shots drawn at once, {server.Count} fired by the server; first tick drew {drawnAtFirst}; " +
            $"predicted rounds in flight up to {predictedRoundsSeen}; shots {c.Predictor!.Shots!.Predicted}, rejected {c.Predictor.Shots.Rejected}, adopted {c.Predictor.Shots.Adopted}");
        Assert.True(seat.Player.Car.Speed > 15, "not at speed");
        Assert.True(drawnAtFirst >= 1, "the first shot was not drawn on the tick of the trigger");
        Assert.True(predictedRoundsSeen > 0);
        // the same shots, from the same muzzle, as the server's
        Assert.Equal(server.Count, drawn.Count);
        double worst = drawn.Zip(server, (a, b) => Vector3.Distance(a.Muzzle, b.Muzzle)).Max();
        output.WriteLine($"muzzles: worst {worst * 100:0.00} cm apart");
        Assert.True(worst < 0.01, $"{worst} m");
        Assert.True(drawn.Zip(server).All(p => p.First.Tick == p.Second.Tick));
        // the server's copies were never drawn
        Assert.Equal(0, rig.ServerShotsDrawn[c]);
        Assert.Equal(0, ownServerRoundsShown);
        Assert.Equal(0, c.Predictor.Shots.Rejected);
        Assert.Equal(0, c.Predictor.Shots.Adopted);

        // the server destroys the gun: what was drawn before the client heard is taken back, then nothing more is drawn
        gun.Hp = 0;
        int before = drawn.Count;
        for (int i = 0; i < 12; i++) rig.Tick(Input);
        int after = drawn.Count;
        for (int i = 0; i < 20; i++) rig.Tick(Input);
        output.WriteLine($"gun destroyed: {after - before} drawn before the client heard, {drawn.Count - after} after; rejected {c.Predictor.Shots.Rejected}, removed {c.Predictor.Shots.Removed}");
        Assert.True(after > before, "the client drew nothing before it heard (the test needs some)");
        Assert.Equal(after - before, c.Predictor.Shots.Rejected);
        Assert.Equal(after, drawn.Count);
        Assert.DoesNotContain(c.Combat.PredictedRounds, r => r.Def == gun.Def);
    }

    /// <summary>
    /// A mine dropped by the own car is drawn at once, and when the server's mine reaches the view it takes the predicted
    /// one's place (the server's is the one that counts, for everyone).
    /// </summary>
    [Fact]
    public void AnOwnMineIsDrawnAtOnceThenTheServersTakesItsPlace()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 3 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var c = Connect(host, seat, HostTests.Mirror("m01"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(c);
        var key = Key(seat.Player.Car, w => w.Def.Kind == 0xf);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        rig.Tick(_ => new CarInput { Buttons = key });
        Assert.Contains(c.Combat.PredictedRounds, r => r.Kind == 0xf);
        Assert.DoesNotContain(c.Combat.Rounds, r => r.Kind == 0xf);
        int ticks = 0;
        while (!c.Combat.Rounds.Any(r => r.Kind == 0xf) && ticks++ < 40) rig.Tick(_ => default);
        output.WriteLine($"the server's mine reached the view {ticks} ticks later; handed over {c.Predictor!.Shots!.HandedOver}");
        Assert.Contains(c.Combat.Rounds, r => r.Kind == 0xf && r.Shooter == c.Mirror.Own!.Vehicle);
        Assert.DoesNotContain(c.Combat.PredictedRounds, r => r.Kind == 0xf);
        Assert.Equal(1, c.Predictor.Shots.HandedOver);
        Assert.Equal(1, c.Combat.Rounds.Count(r => r.Kind == 0xf));
    }

    /// <summary>
    /// The oil slick held down while driving: the drops the own car's view holds (the server's, with the predicted ones
    /// until the server's reach it) never fall short of what the server holds, and all of them stay for the drop's life.
    /// </summary>
    [Fact]
    public void AnOwnOilTrailStaysOnTheScreen()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 3 });
        var seat = host.Join("a", "vppirna3.vcf", 0);
        var c = Connect(host, seat, HostTests.Mirror("m01"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(c);
        var key = Key(seat.Player.Car, w => w.Def.Kind == 0xc);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        int worstGap = 0, maxServer = 0;
        for (int i = 0; i < 300; i++)
        {
            seat.Player.Car.Speed = 20; seat.Player.Car.Vel = seat.Player.Car.Rot.Forward * 20;
            rig.Tick(_ => new CarInput { Buttons = i < 150 ? key : 0 });
            int server = host.Setup.Combat.Rounds.Count(r => r.Kind == 0xc);
            int view = c.Combat.Rounds.Count(r => r.Kind == 0xc) + c.Combat.PredictedRounds.Count(r => r.Kind == 0xc);
            maxServer = Math.Max(maxServer, server);
            if (i > 20 && i % 10 == 0) output.WriteLine($"tick {i}: server {server}, view {view} (server's {c.Combat.Rounds.Count(r => r.Kind == 0xc)}, predicted {c.Combat.PredictedRounds.Count(r => r.Kind == 0xc)})");
            if (i > 20) worstGap = Math.Max(worstGap, server - view);
        }
        output.WriteLine($"worst shortfall {worstGap} of up to {maxServer}");
        Assert.True(maxServer > 30);
        // the drops reach the view a few ticks late (two to a tick), never fewer than that, and all are there in the end
        Assert.True(worstGap <= 16, $"the view lacked {worstGap} drops");
        var drops = c.Combat.Rounds.Where(r => r.Kind == 0xc).ToList();
        Assert.Equal(host.Setup.Combat.Rounds.Count(r => r.Kind == 0xc), drops.Count);
        // the slick views fade a drop by its life left and spread it by its age: both run on a client as on the server
        // (they stood at 0, so every drop of the server's was a fully faded one the moment it replaced the predicted copy)
        Assert.All(drops, r => Assert.True(r.Life > 5 && r.Life < r.Def.Life, $"life {r.Life} of {r.Def.Life}"));
        Assert.All(drops, r => Assert.True(r.Age > 1, $"age {r.Age}"));
        Assert.All(drops, r => Assert.InRange(r.Life + r.Age, r.Def.Life - 1, r.Def.Life + 1));
    }

    /// <summary>
    /// A flame thrower's stream reaches every client: another player's is in the snapshot and built on each client from
    /// the car as it is drawn; the own car's is the prediction's (or the snapshot's without it).
    /// </summary>
    [Fact]
    public void FlamesShowOnEveryClient()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 3 });
        var a = host.Join("a", "vppirna1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var ca = Connect(host, a, HostTests.Mirror("m01"));
        var cb = Connect(host, b, HostTests.Mirror("m01"));
        var rig = new Rig(host, late: 2);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        var flame = a.Player.Car.Armory!.Weapons.First(w => w.Def.Kind is 9 or 10 or 11);
        var key = Key(a.Player.Car, w => w == flame);
        for (int i = 0; i < 30; i++) rig.Tick(c => c == ca ? new CarInput { Buttons = key } : default);
        Assert.Contains(host.Setup.Combat.Flames, f => f.Owner == a.Player.Car);
        int count = host.Setup.Combat.Flames.First(f => f.Owner == a.Player.Car).Count;
        // b sees a's stream, built where its copy of a's car is
        var seen = cb.Combat.Flames.SingleOrDefault(f => f.Owner == cb.Mirror.Car(a.CarSerial));
        Assert.NotNull(seen);
        output.WriteLine($"server stream {count} sections, b's copy {seen!.Count}; a's own (predicted) {ca.Combat.Flames.FirstOrDefault()?.Count}");
        Assert.InRange(seen.Count, 2, 19);
        var muzzle = seen.Owner.Pos.ToVector3() + seen.Owner.Rot.ToWorld(seen.Weapon.MuzzlePos);
        Assert.True(Vector3.Distance(seen.Cross[0], muzzle) < 1f, "the stream does not start at the muzzle");
        // a's own is predicted: its own copy, built from the predicted car
        var own = ca.Combat.Flames.SingleOrDefault(f => f.Owner == ca.Mirror.Own!.Vehicle);
        Assert.NotNull(own);
        Assert.Equal(count, own!.Count);
        // the stream stops on b's screen when a lets go
        for (int i = 0; i < 20; i++) rig.Tick(_ => default);
        Assert.Empty(cb.Combat.Flames);
        Assert.Empty(ca.Combat.Flames);

        // without the fire predicted, a's own stream comes from the snapshot too
        var cn = Connect(host, a, HostTests.Mirror("m01"), predictFire: false);
        var rig2 = new Rig(host, late: 2) { T = rig.T };
        rig2.Clients.Add(cn);
        for (int i = 0; i < 20; i++) rig2.Tick(_ => new CarInput { Buttons = key });
        Assert.Contains(cn.Combat.Flames, f => f.Owner == cn.Mirror.Own!.Vehicle && f.Count >= 2);
    }

    /// <summary>
    /// The Chemical Mortar on a network melee (Nitro, N01): the server's round lands and lingers as a cloud, and a
    /// player's car driven into it is hurt every tick on the server (armour bypassed); the clients get the cloud (a
    /// landed kind-0x16 round), the victim's client the impacts, and the shooter's own predicted round gives way to the
    /// server's cloud once it lands.
    /// </summary>
    [Fact]
    public void TheChemicalCloudHurtsOnTheServerAndShowsOnTheClients()
    {
        if (Nitro is null) return;
        var vfs = new AssetVfs(new GameInstall(Nitro));
        var vcf = System.Text.Encoding.Latin1.GetString(vfs.Read("vppirna1.vcf"));
        var config = System.Text.Encoding.Latin1.GetBytes(vcf.Replace("gfmedium.gdf", "ggchem.gdf\0\0"));
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N01.CBT", Seed = 3 });
        var a = host.Join("a", "vppirna5.vcf", 0, null, config);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var chem = a.Player.Car.Armory!.Weapons.SingleOrDefault(w => w.Def.Kind == 0x16);
        Assert.NotNull(chem);
        var ca = Connect(host, a, NitroMirror("N01.CBT"));
        var cb = Connect(host, b, NitroMirror("N01.CBT"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        var key = Key(a.Player.Car, w => w == chem);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        rig.Tick(c => c == ca ? new CarInput { Buttons = key } : default);
        Assert.Contains(ca.Combat.PredictedRounds, r => r.Kind == 0x16);
        Round? cloud = null;
        for (int i = 0; i < 60 && cloud is null; i++)
        {
            rig.Tick(_ => default);
            cloud = host.Setup.Combat.Rounds.FirstOrDefault(r => r.Kind == 0x16 && r.Settled);
        }
        Assert.NotNull(cloud);
        // b drives into it
        var victim = b.Player.Car;
        int hpBefore = victim.Health!.Slots.Where(p => p is not null).Sum(p => p!.Hp) + victim.Health.Armour.Sum() + victim.Health.Chassis.Sum();
        // (the cloud is moved under b where b stands, in the open: where it landed may be inside a rock, out of which b would be pushed)
        var ground = host.Setup.World.Ground;
        cloud!.Pos = cloud.PrevPos = new Vector3((float)victim.Pos.X, ground.BareHeight(victim.Pos.X, victim.Pos.Z), (float)victim.Pos.Z);
        victim.Vel = default;
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        int hpAfter = victim.Health.Slots.Where(p => p is not null).Sum(p => p!.Hp) + victim.Health.Armour.Sum() + victim.Health.Chassis.Sum();
        // the clients see the cloud: a landed chemical round in their mirrors
        for (int i = 0; i < 6; i++) rig.Tick(_ => default);
        int impacts = rig.Impacts[cb].Count(i => i.Kind == 0x16 && i.Victim is not null);
        output.WriteLine($"cloud at {cloud.Pos}; victim's hit points {hpBefore} -> {hpAfter}, dead {Combat.IsDead(victim)}; a handed over {ca.Predictor!.Shots!.HandedOver}; {impacts} cloud impacts on b's screen");
        // the Nitro Pack: the cloud's packet (flag 0x10) kills the driver outright (0x4020), the car does not explode
        Assert.True(Combat.IsDead(victim), $"{hpBefore} -> {hpAfter}");
        Assert.Equal((I76.Sim.Vehicles.VehicleFlags)0x4020, victim.Flags & (I76.Sim.Vehicles.VehicleFlags)0x4020);
        Assert.True(impacts >= 5, $"{impacts} impacts reached b");
        Assert.Contains(cb.Combat.Rounds, r => r.Kind == 0x16 && r.Settled);
        Assert.Contains(ca.Combat.Rounds, r => r.Kind == 0x16 && r.Settled);
        Assert.DoesNotContain(ca.Combat.PredictedRounds, r => r.Kind == 0x16);
        Assert.Equal(1, ca.Predictor.Shots.HandedOver);
    }

    /// <summary>
    /// Driving over a chemical cloud online (Nitro, N01): b, a predicted client, drives at 12 m/s past the cloud's landing
    /// point at a lateral offset. The Nitro Pack's cloud is a 1 m probe straight up from that point (the segment
    /// test): a car whose box passes over it loses its driver at once, on the first pass, on the tick the
    /// server's car (which the prediction agrees with) covers it; one that passes beside it is not touched, however thick
    /// the drawn puffs. Returns the tick b's box first covered the point and the tick b died (0: never).
    /// </summary>
    private (uint Covered, uint Died) OverTheCloud(float offset)
    {
        var vfs = new AssetVfs(new GameInstall(Nitro!));
        var vcf = System.Text.Encoding.Latin1.GetString(vfs.Read("vppirna1.vcf"));
        var config = System.Text.Encoding.Latin1.GetBytes(vcf.Replace("gfmedium.gdf", "ggchem.gdf\0\0"));
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N01.CBT", Seed = 3 });
        var a = host.Join("a", "vppirna5.vcf", 0, null, config);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var chem = a.Player.Car.Armory!.Weapons.Single(w => w.Def.Kind == 0x16);
        var ca = Connect(host, a, NitroMirror("N01.CBT"));
        var cb = Connect(host, b, NitroMirror("N01.CBT"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        var key = Key(a.Player.Car, w => w == chem);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        rig.Tick(c => c == ca ? new CarInput { Buttons = key } : default);
        Round? cloud = null;
        for (int i = 0; i < 60 && cloud is null; i++)
        {
            rig.Tick(_ => default);
            cloud = host.Setup.Combat.Rounds.FirstOrDefault(r => r.Kind == 0x16 && r.Settled);
        }
        Assert.NotNull(cloud);
        // the cloud 25 m ahead of b, beside its line by the offset
        var victim = b.Player.Car;
        var f = Vector3.Normalize(new Vector3(victim.Rot.Forward.X, 0, victim.Rot.Forward.Z));
        var right = Vector3.Cross(Vector3.UnitY, f);
        var at = victim.Pos.ToVector3() + f * 25 + right * offset;
        var ground = host.Setup.World.Ground;
        cloud!.Pos = cloud.PrevPos = new Vector3(at.X, ground.BareHeight(at.X, at.Z), at.Z);
        cloud.Age = 2;
        uint covered = 0, died = 0;
        for (int i = 0; i < 120 && died == 0; i++)
        {
            rig.Tick(c => c == cb ? new CarInput { Buttons = cb.Mirror.Own!.Vehicle.Speed < 12 ? CarButtons.ThrottleUp : 0 } : default);
            var l = victim.Rot.ToLocal(cloud.Pos - victim.Pos.ToVector3());
            // (the front bumper reaching it, give or take a centimetre: the probe runs between the tick's two car steps at 20 Hz)
            if (covered == 0 && l.X >= victim.BoxX[0] && l.X <= victim.BoxX[3] && l.Z >= victim.BoxZ[0] && l.Z <= victim.BoxZ[3] + 0.05f) covered = rig.T;
            if (Combat.IsDead(victim)) died = rig.T;
        }
        output.WriteLine($"offset {offset} m: b's box over the cloud's point on tick {covered}, b's driver killed on tick {died}; b's prediction off by {cb.Predictor!.MaxError:0.000} m at most");
        return (covered, died);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    public void DrivingOverTheChemicalCloudKillsOnTheFirstPass(float offset)
    {
        if (Nitro is null) return;
        var (covered, died) = OverTheCloud(offset);
        Assert.True(covered > 0, "b never passed over the cloud");
        Assert.InRange(died, covered, covered + 1);
    }

    [Fact]
    public void PassingBesideTheChemicalCloudDoesNothing()
    {
        if (Nitro is null) return;
        var (_, died) = OverTheCloud(2f);
        Assert.Equal(0u, died);
    }

    /// <summary>
    /// The Nitro Pack's caltrops online: a's caltrop under b's front left tyre blows it on the server (CaltropTyreHit),
    /// and both clients have the caltrop and b's flat tyre (the car's blown bits and the TyreBlown event).
    /// </summary>
    [Fact]
    public void ACaltropBlowsATyreOnTheServerAndTheClientsSeeIt()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N01.CBT", Seed = 3 });
        var a = host.Join("a", "vppirna4.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var caltrops = a.Player.Car.Armory!.Weapons.SingleOrDefault(w => w.Def.Kind == 0xe);
        Assert.NotNull(caltrops);
        var ca = Connect(host, a, NitroMirror("N01.CBT"));
        var cb = Connect(host, b, NitroMirror("N01.CBT"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        var key = Key(a.Player.Car, w => w == caltrops);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        rig.Tick(c => c == ca ? new CarInput { Buttons = key } : default);
        Round? drop = null;
        for (int i = 0; i < 10 && drop is null; i++)
        {
            rig.Tick(_ => default);
            drop = host.Setup.Combat.Rounds.FirstOrDefault(r => r.Kind == 0xe);
        }
        Assert.NotNull(drop);
        // the caltrop is put under b's front left tyre where b stands
        var victim = b.Player.Car;
        victim.Vel = default;
        var fl = victim.Pos.ToVector3() + victim.Rot.ToWorld(victim.FrontLeft.Mount);
        drop!.Pos = drop.PrevPos = new Vector3(fl.X, host.Setup.World.Ground.BareHeight(fl.X, fl.Z), fl.Z);
        for (int i = 0; i < 8; i++) rig.Tick(_ => default);
        Assert.True(victim.FrontLeft.Blown, "the server's tyre is whole");
        Assert.Equal(25, victim.Health!.Slots[1]!.Hp);
        // b's own (predicted) car and a's copy of b have the flat tyre, and both draw the caltrop
        Assert.True(cb.Mirror.Own!.Vehicle.FrontLeft.Blown);
        var bOnA = ca.Mirror.Car(b.CarSerial);
        Assert.NotNull(bOnA);
        Assert.True(bOnA!.FrontLeft.Blown);
        Assert.Contains(cb.Combat.Rounds, r => r.Kind == 0xe);
        Assert.Contains(ca.Combat.Rounds.Concat(ca.Combat.PredictedRounds), r => r.Kind == 0xe);
    }

    /// <summary>
    /// The 4Get-U-Not online: a's lock builds on the server and in a's prediction (msllock1.wav, then msllock2.wav), the
    /// missile fires once it has 1.6 s, and b, the human it holds, hears the warning (the own-car block's LockedOn bit).
    /// </summary>
    [Fact]
    public void TheFourGetUNotLocksAndFiresOnline()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N01.CBT", Seed = 3 });
        var a = host.Join("a", "vwclown1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var me = a.Player.Car;
        var msl = me.Armory!.Weapons.SingleOrDefault(w => w.Def.Kind == 0x14);
        Assert.NotNull(msl);
        me.Armory.Selected = me.Armory.Weapons.IndexOf(msl!);
        var ca = Connect(host, a, NitroMirror("N01.CBT"));
        var cb = Connect(host, b, NitroMirror("N01.CBT"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        // b stands 50 m ahead of a, in a's front arc
        var ground = host.Setup.World.Ground;
        var ahead = me.Pos.ToVector3() + me.Rot.Forward * 50f;
        b.Player.Car.Pos = new Double3(ahead.X, ground.BareHeight(ahead.X, ahead.Z) + 0.5f, ahead.Z);
        b.Player.Car.Rot = me.Rot;
        me.Vel = b.Player.Car.Vel = default;
        var tones = new HashSet<string>();
        bool warned = false;
        int fired = -1;
        for (int t = 0; t < 80 && fired < 0; t++)
        {
            rig.Tick(c => c == ca ? new CarInput { Buttons = CarButtons.Fire } : default);
            if (ca.Mirror.Own?.Vehicle is { } own && ca.Combat.LockTone.TryGetValue(own, out var tone)) tones.Add(tone);
            warned |= cb.Mirror.Own?.Vehicle is { } bv && cb.Combat.LockedOn.Contains(bv);
            if (host.Setup.Combat.Rounds.FirstOrDefault(r => r.Kind == 0x14) is { } r) { fired = t; Assert.Same(b.Player.Car, r.Target); }
        }
        fired++;   // ticks held
        output.WriteLine($"fired at tick {fired} ({fired * host.Session.TickSeconds:0.00} s), tones {string.Join(" ", tones)}, b warned {warned}");
        Assert.InRange(fired * host.Session.TickSeconds, 1.6f, 2.2f);
        Assert.Contains("msllock1.wav", tones);
        Assert.Contains("msllock2.wav", tones);
        // the warning lasts the one tick the lock was 1.6 s before the shot spent it; it reaches b late
        for (int t = 0; t < 6; t++)
        {
            rig.Tick(_ => default);
            warned |= cb.Mirror.Own?.Vehicle is { } bv && cb.Combat.LockedOn.Contains(bv);
        }
        Assert.True(warned, "b never heard the warning");
    }

    /// <summary>
    /// The 4Get-U-Not's launch at b plays the lock warning on b's car (the round's contrail): the server's click reaches
    /// every client on b's car, and the views play it for b's driver alone (Combat.IsDriverSound).
    /// </summary>
    [Fact]
    public void ARadarMissileLaunchWarnsItsTargetOnline()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N01.CBT", Seed = 3 });
        var a = host.Join("a", "vwclown1.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var me = a.Player.Car;
        var msl = me.Armory!.Weapons.Single(w => w.Def.Kind == 0x14);
        me.Armory.Selected = me.Armory.Weapons.IndexOf(msl);
        var ca = Connect(host, a, NitroMirror("N01.CBT"));
        var cb = Connect(host, b, NitroMirror("N01.CBT"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        var ground = host.Setup.World.Ground;
        var ahead = me.Pos.ToVector3() + me.Rot.Forward * 50f;
        b.Player.Car.Pos = new Double3(ahead.X, ground.BareHeight(ahead.X, ahead.Z) + 0.5f, ahead.Z);
        b.Player.Car.Rot = me.Rot;
        me.Vel = b.Player.Car.Vel = default;
        for (int t = 0; t < 60 && !host.Setup.Combat.Rounds.Any(r => r.Kind == 0x14); t++)
            rig.Tick(c => c == ca ? new CarInput { Buttons = CarButtons.Fire } : default);
        Assert.Contains(host.Setup.Combat.Rounds, r => r.Kind == 0x14);
        for (int t = 0; t < 6; t++) rig.Tick(_ => default);
        var warn = Assert.Single(rig.Clicks[cb], c => c.Sound == "clockon.wav");
        Assert.Same(cb.Mirror.Own!.Vehicle, warn.Shooter);
        Assert.True(Combat.IsDriverSound(warn.Sound));
        // a's client has it too, on b's car (not a's own), which its views leave alone
        var onA = Assert.Single(rig.Clicks[ca], c => c.Sound == "clockon.wav");
        Assert.NotSame(ca.Mirror.Own!.Vehicle, onA.Shooter);
    }

    /// <summary>A cluster bomb's bomblets go off on the server and every client draws each one (xclst1).</summary>
    [Fact]
    public void AClusterBombsBombletsShowOnEveryClient()
    {
        if (Nitro is null) return;
        var host = new MeleeHost(new ServerOptions { Install = Nitro, Arena = "N01.CBT", Seed = 3 });
        var a = host.Join("a", "vppt16.vcf", 0);
        var b = host.Join("b", "vppirna1.vcf", 0);
        var cluster = a.Player.Car.Armory!.Weapons.Single(w => w.Def.Kind == 5);
        var ca = Connect(host, a, NitroMirror("N01.CBT"));
        var cb = Connect(host, b, NitroMirror("N01.CBT"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(ca);
        rig.Clients.Add(cb);
        var key = Key(a.Player.Car, w => w == cluster);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        rig.Tick(c => c == ca ? new CarInput { Buttons = key } : default);
        int scheduled = 0;
        for (int i = 0; i < 120; i++)
        {
            rig.Tick(_ => default);
            if (host.Setup.Combat.Clusters.Count > 0) scheduled = System.Math.Max(scheduled, host.Setup.Combat.Clusters[0].Left);
        }
        Assert.InRange(scheduled, 8, 12);
        Assert.DoesNotContain(host.Setup.Combat.Clusters, c => c.Live.Count(x => x) > 1);
        foreach (var c in new[] { ca, cb })
        {
            int booms = rig.Explosions[c].Count(x => x.Effect.File.Equals("xclst1.xdf", StringComparison.OrdinalIgnoreCase));
            Assert.InRange(booms, 1 + scheduled, 2 + scheduled);   // the landing's own, then n or n + 1 bomblets
        }
    }

    /// <summary>The own fire control and the flames go over the wire and back as they were.</summary>
    [Fact]
    public void TheFireControlAndTheFlamesRoundTrip()
    {
        var fire = new OwnFire();
        fire.Weapons.Add(new OwnFire.Entry { Weapon = 0, Shots = 65535 });
        fire.Weapons.Add(new OwnFire.Entry { Weapon = 2, WaitA = 0.123f, WaitB = 0.0498f, BurstLeft = 2, WasFiring = true, OnTarget = true, AimYaw = -1.25f, AimPitch = 0.1f, GunPitch = 0.05f, GunPitchWant = 0.06f, LockTime = 1.7f, Shots = 7 });
        fire.Weapons.Add(new OwnFire.Entry { Weapon = 0x81, WaitB = 0.3f, Shots = 1 });
        var w = new NetWriter();
        Snapshot.WriteHeader(w, 100, 95, null, 0, null, fire);
        Snapshot.WriteBody(w, [], [], null, [new FlameState(5, 2, 19), new FlameState(300, 0x80, 2)]);
        var r = new NetReader(w.Span);
        Assert.Equal((byte)MsgType.Snapshot, r.U8());
        var s = Snapshot.Read(ref r);
        Assert.Equal(3, s.Fire!.Weapons.Count);
        foreach (var (x, y) in fire.Weapons.Zip(s.Fire.Weapons))
        {
            Assert.Equal((x.Weapon, x.Shots, x.WaitA, x.WaitB, x.BurstLeft, x.WasFiring, x.OnTarget), (y.Weapon, y.Shots, y.WaitA, y.WaitB, y.BurstLeft, y.WasFiring, y.OnTarget));
            Assert.Equal((x.AimYaw, x.AimPitch, x.GunPitch, x.GunPitchWant, x.LockTime), (y.AimYaw, y.AimPitch, y.GunPitch, y.GunPitchWant, y.LockTime));
        }
        Assert.Equal([new FlameState(5, 2, 19), new FlameState(300, 0x80, 2)], s.Flames);
        // a weapon at rest is four bytes
        var rest = new NetWriter();
        var one = new OwnFire();
        one.Weapons.Add(new OwnFire.Entry { Weapon = 1, Shots = 3 });
        one.Write(rest);
        Assert.Equal(1 + 4, rest.Length);
    }
}
