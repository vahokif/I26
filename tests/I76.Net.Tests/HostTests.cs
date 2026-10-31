using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The server's game without sockets, on a real arena (skipped without an install).</summary>
public class HostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>A client's mirror of the arena: the same MissionSetup as a network melee with no cars.</summary>
    internal static MissionSetup Mirror(string arena)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", arena + ".msn")!);
        return MissionSetup.Build(vfs, w, asm, melee: [], meleeRules: new MeleeRules { Network = true });
    }

    /// <summary>
    /// The server builds the car from its own files by name: a name that is
    /// not a plain car file of its install gets the default car; nothing the
    /// client says about the car's numbers is used.
    /// </summary>
    [Fact]
    public void TheServerBuildsCarsFromItsOwnInstall()
    {
        if (InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = InstallDir, Arena = "m01" });
        var a = host.Join("a", "vcgcour1", 0);
        var b = host.Join("b\n\u0007b", "../../etc/passwd.vcf", 0);
        var c = host.Join("", "nosuchcar.vcf", 0);
        Assert.Equal("vcgcour1.vcf", a.Player.CarFile);
        Assert.Equal(host.Options.DefaultCar, b.Player.CarFile);
        Assert.Equal(host.Options.DefaultCar, c.Player.CarFile);
        Assert.Equal("bb", b.Name);
        Assert.Equal("Player 3", c.Name);
        Assert.Equal([0, 1, 2], host.Seats.Select(s => s.Slot));
        // a slot freed by a leaver goes to the next to join
        host.Leave(b, "test");
        // its car is blown up and left as a wreck, taken out 10 s later
        Assert.True(I76.Sim.Damage.DamageModel.IsDead(b.Player.Car));
        Assert.DoesNotContain(b.Player.Car, host.Setup.World.Humans);
        for (int t = 0; t < 11 / host.Session.TickSeconds; t++) host.Step();
        Assert.DoesNotContain(b.Player.Car, host.Setup.World.Vehicles);
        var d = host.Join("d", "valepre1.vcf", 0);
        Assert.Equal(1, d.Slot);
        for (int t = 0; t < 100; t++) host.Step();
        var events = host.TakeEvents();
        Assert.Contains(events, e => e is LeftEvent { Slot: 1 });
        Assert.Contains(events, e => e is RemoveEvent r && r.Car == b.CarSerial);
        Assert.Contains(events, e => e is SpawnEvent s && s.Car.Serial == d.CarSerial && s.Car.Owner == 1);
    }

    /// <summary>
    /// A client can only send inputs: a car given nonsense every tick (every
    /// bit set, the extreme analog values, a tick far ahead) drives no faster
    /// than the car can, and a skip or poem command never reaches the sim.
    /// </summary>
    [Fact]
    public void NonsenseInputsDriveTheCarNoFasterThanItCan()
    {
        if (InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = InstallDir, Arena = "m01" });
        var honest = host.Join("honest", "vppirna1.vcf", 0);
        var cheat = host.Join("cheat", "vppirna1.vcf", 0);
        float top = 0, topHonest = 0;
        for (uint t = 1; t < 1000; t++)
        {
            honest.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp }]));
            var junk = new CarInput { Buttons = (CarButtons)0xffffffff, Commands = (CarCommands)0xffff, Steer = short.MinValue, Throttle = short.MaxValue };
            cheat.Inputs.Add(new InputMessage(t * 50, [junk, junk, junk]));
            host.Step();
            top = MathF.Max(top, cheat.Player.Car.Speed);
            topHonest = MathF.Max(topHonest, honest.Player.Car.Speed);
            host.TakeEvents();
        }
        output.WriteLine($"top speed: honest {topHonest:0.0}, nonsense {top:0.0}; corrected {cheat.Inputs.Corrected}, dropped {cheat.Inputs.Dropped}");
        Assert.True(cheat.Inputs.Corrected > 900);
        Assert.True(top < 80, $"{top}");
        Assert.True(top <= topHonest * 1.5f + 5);
    }

    /// <summary>A car's state written into a client's copy of the same car reads back as the server has it.</summary>
    [Fact]
    public void ASnapshotWritesTheMirrorAsTheServerHasIt()
    {
        if (InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = InstallDir, Arena = "m01", Ai = 2 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        for (uint t = 1; t < 300; t++)
        {
            seat.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp | CarButtons.Fire | (t % 100 < 30 ? CarButtons.SteerLeft : 0), Commands = t == 5 ? CarCommands.TargetNearest | CarCommands.Lights : 0 }]));
            host.Step();
        }
        var w = new NetWriter();
        var (cars, rounds) = host.Capture();
        Snapshot.WriteHeader(w, host.Tick, seat.Inputs.LastApplied, host.CaptureOwn(seat));
        Snapshot.WriteBody(w, cars, rounds);
        output.WriteLine($"{cars.Count} cars, {rounds.Count} rounds: {w.Length} bytes");
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.Snapshot, (MsgType)r.U8());
        var snap = Snapshot.Read(ref r);
        Assert.Equal(299u, snap.AckedInput);

        var accept = host.AcceptFor(seat);
        var mirror = new ClientWorld(Mirror("m01"), accept);
        Assert.Equal(host.Setup.World.Vehicles.Count, mirror.Cars.Count);
        var client = new FakeFeed(snap);
        mirror.Advance(0, client.Client);
        var mine = mirror.Own!.Vehicle;
        var real = seat.Player.Car;
        Assert.True(System.Math.Abs(mine.Pos.X - real.Pos.X) < 0.01 && System.Math.Abs(mine.Pos.Z - real.Pos.Z) < 0.01);
        Assert.Equal(real.Flags, mine.Flags);
        // the ground under the mirror is the server's (the force feedback's road feel and the tyre sounds read it; the snapshot does not carry it)
        if ((real.Flags & I76.Sim.Vehicles.VehicleFlags.Airborne) == 0) Assert.Equal(real.Surface, mine.Surface);
        Assert.Equal(real.Armory!.Weapons.Select(x => x.Ammo), mine.Armory!.Weapons.Select(x => x.Ammo));
        Assert.Equal(real.Health!.Armour, mine.Health!.Armour);
        Assert.Equal(host.Setup.Combat.RadarTarget.GetValueOrDefault(real)?.NetId, mirror.Setup.Combat.RadarTarget.GetValueOrDefault(mine)?.NetId);
        Assert.Equal(rounds.Count, mirror.Setup.Combat.Rounds.Count);
        foreach (var (serial, m) in mirror.Cars)
        {
            var v = host.Setup.World.Vehicles.First(x => (x.NetId & 0xffffff) == serial);
            Assert.True(Vector3.Distance(v.Rot.Forward, m.Vehicle.Rot.Forward) < 1e-3f);
            Assert.Equal(v.Health!.ZoneRatio(0), m.Vehicle.Health!.ZoneRatio(0));
        }
    }

    /// <summary>The server's rate is every player's: the join accept carries it and the client's mirror and queues are built from it.</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    public void TheServersRateIsSentInTheJoinAcceptAndAppliesToTheMirror(int hz)
    {
        if (InstallDir is null) return;
        var rate = I76.Sim.Session.SimRate.FromLabel(hz);
        var host = new MeleeHost(new ServerOptions { Install = InstallDir, Arena = "m01", Rate = rate });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        Assert.Equal(rate.TickSeconds, host.Session.TickSeconds);
        var w = new NetWriter();
        host.AcceptFor(seat).Write(w);
        var r = new NetReader(w.Span);
        r.U8();
        var accept = JoinAccept.Read(ref r);
        Assert.Equal(rate, accept.Rate);
        var mirror = new ClientWorld(Mirror("m01"), accept);
        Assert.Equal(rate.TickSeconds, mirror.Session.TickSeconds);
        // its queue holds 0.288 s of inputs whatever the rate
        for (uint t = 1; t <= 100; t++) seat.Inputs.Add(new InputMessage(t, [new CarInput()]));
        Assert.Equal(Protocol.MaxQueuedInputs(rate), seat.Inputs.Waiting);
    }

    /// <summary>Feeds a ClientWorld one snapshot as if it had just come.</summary>
    private sealed class FakeFeed
    {
        public MeleeClient Client { get; } = new();
        public FakeFeed(Snapshot s) => Client.Inject(s);
    }
}
