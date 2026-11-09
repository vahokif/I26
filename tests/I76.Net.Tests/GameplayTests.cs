using I76.Net;
using I76.Server;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>What a network melee lets a player do: the ignition key, the hidden helicopters, the driver's handguns.</summary>
public class GameplayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Send(MeleeHost host, Seat seat, uint t, CarInput i)
    {
        seat.Inputs.Add(new InputMessage(t, [i]));
        host.Step();
        host.TakeEvents();
    }

    /// <summary>The ignition key (S) stops a running engine at once and cranks it again, as offline.</summary>
    [Fact]
    public void TheIgnitionKeyStopsAndStartsTheEngine()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var car = seat.Player.Car;
        uint t = 1;
        for (; t <= 10; t++) Send(host, seat, t, default);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
        Send(host, seat, t++, new CarInput { Commands = CarCommands.StartEngine });
        for (int k = 0; k < 5; k++) Send(host, seat, t++, default);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.EngineOn));
        Send(host, seat, t++, new CarInput { Commands = CarCommands.StartEngine });
        for (int k = 0; k < 200; k++) Send(host, seat, t++, default);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
    }

    /// <summary>
    /// The host's ignition lock (#108): the key never switches a running engine off in that game, the melee's rules carry the lock,
    /// and an engine that is off (switched off before the lock, here) still cranks.
    /// </summary>
    [Fact]
    public void TheIgnitionLockRefusesTheEngineOff()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7, IgnitionStaysOn = true });
        Assert.True(host.Session.Melee!.Rules.IgnitionStaysOn);
        Assert.True(host.Session.IgnitionStaysOn);
        Assert.True(host.Info().IgnitionStaysOn);
        // a host migration's successor reads the options from the savestate's extras (JSON): the lock goes across
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<ServerOptions>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(host.Options with { Install = "" }, json), json)!.IgnitionStaysOn);
        // a co-op host's session has it too (no melee rules there)
        Assert.True(new MeleeHost(new ServerOptions { Install = install, Arena = "t01", Coop = "t01", MaxPlayers = 2, IgnitionStaysOn = true }).Session.IgnitionStaysOn);
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var car = seat.Player.Car;
        uint t = 1;
        for (; t <= 10; t++) Send(host, seat, t, default);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
        for (int k = 0; k < 3; k++)
        {
            Send(host, seat, t++, new CarInput { Commands = CarCommands.StartEngine });
            for (int j = 0; j < 5; j++) Send(host, seat, t++, default);
            Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
        }
        // an engine that is off still starts with the lock on
        car.Flags &= ~VehicleFlags.EngineOn;
        Send(host, seat, t++, new CarInput { Commands = CarCommands.StartEngine });
        for (int k = 0; k < 200; k++) Send(host, seat, t++, default);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
    }

    /// <summary>The session's own flag (a co-op host's, a client mirror's from its accept) does the same without a melee's rules.</summary>
    [Fact]
    public void TheSessionsIgnitionFlagRefusesTheEngineOff()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        Assert.False(host.Session.IgnitionStaysOn);
        host.Session.IgnitionStaysOn = true;
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var car = seat.Player.Car;
        uint t = 1;
        for (; t <= 10; t++) Send(host, seat, t, default);
        Send(host, seat, t++, new CarInput { Commands = CarCommands.StartEngine });
        for (int k = 0; k < 5; k++) Send(host, seat, t++, default);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
        host.Session.IgnitionStaysOn = false;
        Send(host, seat, t++, new CarInput { Commands = CarCommands.StartEngine });
        for (int k = 0; k < 5; k++) Send(host, seat, t++, default);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.EngineOn));
    }

    /// <summary>
    /// The horn key sets the vehicle's horn flag while it is held (network games only), and the other players' snapshots carry it:
    /// that is what makes a remote car honk. It clears on release and when the car is dead.
    /// </summary>
    [Fact]
    public void TheHornKeySetsTheHonkBitTheSnapshotsCarry()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var car = seat.Player.Car;
        uint t = 1;
        for (; t <= 5; t++) Send(host, seat, t, default);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.Honking));
        Send(host, seat, t++, new CarInput { Buttons = CarButtons.Horn });
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.Honking));
        Assert.NotEqual(0, (int)(CarState.Capture(car).Flags & VehicleFlags.Honking));
        Send(host, seat, t++, default);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.Honking));
        Assert.Equal(0x200000, (int)VehicleFlags.Honking);
    }

    /// <summary>radar_range_reset (unbound by default) puts the radar back on its long range; the wire carries it.</summary>
    [Fact]
    public void RadarRangeResetPutsTheRadarBackOnItsLongRange()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var car = seat.Player.Car;
        var combat = host.Session.Combat!;
        uint t = 1;
        for (; t <= 5; t++) Send(host, seat, t, default);
        Send(host, seat, t++, new CarInput { Commands = CarCommands.RadarRange });
        Assert.Equal(0, combat.RadarRange[car]);
        Send(host, seat, t++, new CarInput { Commands = CarCommands.RadarRangeReset });
        Assert.Equal(1, combat.RadarRange[car]);
        Assert.True((InputQueue.Commands & CarCommands.RadarRangeReset) != 0);
    }

    /// <summary>The own car's prediction stops and starts its engine with the key, at once, and hands the views its engine events.</summary>
    [Fact]
    public void ThePredictionStopsTheEngineAtOnceAndTellsTheViews()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(seat));
        var client = new MeleeClient();
        var predictor = new OwnCarPredictor(mirror, client);
        uint t = 1;
        void Go(CarInput i)
        {
            seat.Inputs.Add(new InputMessage(t, [i]));
            host.Step();
            host.TakeEvents();
            predictor.Step(t, i);
            client.Inject(PredictionTests.Through(host, seat));
            predictor.Reconcile();
            t++;
        }
        for (int k = 0; k < 10; k++) Go(default);
        var events = mirror.Setup.Damage!.Events;
        events.Clear();
        Go(new CarInput { Commands = CarCommands.StartEngine });
        Assert.Contains(events, e => e is I76.Sim.Damage.EngineStopped);
        Assert.Equal(0, (int)(mirror.Own!.Vehicle.Flags & VehicleFlags.EngineOn));
        Assert.Equal(0, (int)(seat.Player.Car.Flags & VehicleFlags.EngineOn));
        events.Clear();
        Go(new CarInput { Commands = CarCommands.StartEngine });
        Assert.Contains(events, e => e is I76.Sim.Damage.IgnitionStarted);
        for (int k = 0; k < 200; k++) Go(default);
        Assert.NotEqual(0, (int)(mirror.Own.Vehicle.Flags & VehicleFlags.EngineOn));
        Assert.Equal(seat.Player.Car.Flags, mirror.Own.Vehicle.Flags);
    }

    /// <summary>
    /// The prediction of a helicopter: its flight state (the eased controls, the height it holds) rides in the own
    /// motion, so the client's car is the server's at every acked tick, with the snapshots 6 ticks late.
    /// </summary>
    [Fact]
    public void AHelicoptersPredictionIsTheServers()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7, AllowHidden = true });
        var seat = host.Join("a", "vuchuey1.vcf", 0);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(seat));
        var client = new MeleeClient();
        var predictor = new OwnCarPredictor(mirror, client);
        var late = new Queue<Snapshot>();
        for (uint t = 1; t <= 400; t++)
        {
            var input = new CarInput { Buttons = (t % 160 < 100 ? CarButtons.ThrottleUp : CarButtons.ThrottleDown) | (t % 70 < 25 ? CarButtons.SteerLeft : t % 70 < 40 ? CarButtons.SteerRight : 0) };
            seat.Inputs.Add(new InputMessage(t, [input]));
            host.Step();
            host.TakeEvents();
            predictor.Step(t, input);
            late.Enqueue(PredictionTests.Through(host, seat));
            while (late.Count > 6) { client.Inject(late.Dequeue()); predictor.Reconcile(); }
        }
        var real = seat.Player.Car;
        output.WriteLine($"{predictor.Compared} compared, {predictor.Inexact} not exact, max {predictor.MaxError:0.000} m; speed {real.Speed:0.0}, flags {real.Flags}");
        Assert.True(predictor.Compared > 380);
        Assert.Equal(0, predictor.Inexact);
        Assert.True((real.Flags & VehicleFlags.Dead) == 0 && real.Speed > 3);
    }

    /// <summary>
    /// A network car has its driver's .45s (the arms its cockpit model has, as the game fits them offline), and they fire
    /// the original's way: only with the cockpit view and the arm raised to its window, the fire key then working that
    /// arm's gun and not the hardpoints.
    /// </summary>
    [Theory]
    [InlineData("vppirna1.vcf", true)]
    [InlineData("vppirna1.vcf", false)]
    public void ANetworkCarFiresItsDriversHandgun(string car, bool right)
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7 });
        var seat = host.Join("a", car, 0);
        var v = seat.Player.Car;
        var guns = v.Armory!.Handguns;
        output.WriteLine($"{car}: right {guns[0] is not null}, left {guns[1] is not null}");
        Assert.True(guns[0] is not null || guns[1] is not null);
        int ch = right ? 0 : 1;
        if (guns[ch] is null) return;
        var arm = right ? CarButtons.RaiseRightArm : CarButtons.RaiseLeftArm;
        uint t = 1;
        int shots = 0;
        void Run(int n, CarButtons b)
        {
            for (int k = 0; k < n; k++, t++)
            {
                seat.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = b }]));
                host.Step();
                shots += host.TakeEvents().OfType<ShotEvent>().Count(e => e.Weapon == RoundState.Handgun + ch);
            }
        }
        Run(20, CarButtons.Cockpit | CarButtons.Fire);        // arms down: the hardpoints answer, not the handgun
        Assert.Equal(0, shots);
        Run(40, CarButtons.Cockpit | CarButtons.Fire | arm);  // the arm up
        Assert.True(shots > 0, "no .45 shots");
        shots = 0;
        Run(40, CarButtons.Fire | arm);                       // not in the cockpit: the arms rest down
        Assert.Equal(0, shots);
    }

    private static string? Nitro =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e && Directory.Exists(e) ? e : null;

    /// <summary>
    /// A hidden helicopter, the balloon, the UFO and the Phantom fly in a network melee: they take off, hold their height
    /// above the ground and move (before, they were stepped as cars and spun).
    /// </summary>
    [Theory]
    [InlineData("vuchuey1.vcf", false)]
    [InlineData("vugcobr1.vcf", false)]
    [InlineData("vubalon1.vcf", true)]
    [InlineData("vxufo1.vcf", true)]
    [InlineData("vuphant1.vcf", true)]
    public void AHiddenHelicopterFliesOnline(string car, bool nitro)
    {
        if ((nitro ? Nitro : HostTests.InstallDir) is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = nitro ? "N35.RAC" : "m01", Seed = 7, AllowHidden = true });
        var seat = host.Join("a", car, 0);
        var v = seat.Player.Car;
        Assert.Equal(car, seat.Player.CarFile);
        Assert.True(v.IsHeli);
        uint t = 1;
        for (; t <= 200; t++) Send(host, seat, t, new CarInput { Buttons = CarButtons.ThrottleUp | (t > 100 ? CarButtons.SteerLeft : 0) });
        float over = (float)v.Pos.Y - host.Setup.World.Ground.Query((float)v.Pos.X, (float)v.Pos.Y, (float)v.Pos.Z).Height;
        output.WriteLine($"{car}: {over} m up, speed {v.Speed}, flags {v.Flags}");
        Assert.True((v.Flags & VehicleFlags.Dead) == 0);
        Assert.InRange(over, 5, 80);
        Assert.True(v.Speed > 3);
    }

    /// <summary>
    /// #97: a helicopter that takes off from a spawn point on a slope does not meet the ground: the flight model crashes it the
    /// moment the ground ahead is above it, and it began exactly on the ground.
    /// </summary>
    [Theory]
    [InlineData("vuchuey1.vcf", false, "m01")]
    [InlineData("vuchuey1.vcf", false, "m03")]
    [InlineData("vubalon1.vcf", true, "N35.RAC")]
    [InlineData("vxufo1.vcf", true, "N35.RAC")]
    public void AHelicopterTakesOffFromEverySpawnPointAlive(string car, bool nitro, string arena)
    {
        if ((nitro ? Nitro : HostTests.InstallDir) is not { } install) return;
        var dead = new List<string>();
        int tried = 0;
        for (int seed = 1; seed <= 24; seed++)
        {
            var host = new MeleeHost(new ServerOptions { Install = install, Arena = arena, Seed = seed, AllowHidden = true });
            var seat = host.Join("a", car, 0);
            var v = seat.Player.Car;
            tried++;
            for (uint t = 1; t <= 120; t++) Send(host, seat, t, new CarInput { Buttons = CarButtons.ThrottleUp });
            if ((v.Flags & VehicleFlags.Dead) != 0) dead.Add($"seed {seed} at {v.Pos.X:0},{v.Pos.Z:0}");
        }
        Assert.True(tried > 0);
        Assert.Empty(dead);
    }
}
