using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

public class CarInputTests
{
    [Fact]
    public void RoundTripsThroughItsTwelveBytes()
    {
        var i = new CarInput
        {
            Buttons = CarButtons.ThrottleUp | CarButtons.Hardpoint3 | CarButtons.Cockpit | CarButtons.Analog,
            Commands = CarCommands.Reverse | CarCommands.TargetNext | CarCommands.Skip,
            Steer = -12345, Throttle = 32767,
        };
        var b = new byte[CarInput.Size];
        i.Write(b);
        Assert.Equal(i, CarInput.Read(b));
        Assert.True(i.Hardpoint(2));
        Assert.False(i.Hardpoint(1));
        Assert.Equal(12, CarInput.Size);
        // the commands past bit 15 survive the wire
        i.Commands = CarCommands.ShiftUp | CarCommands.Special3 | CarCommands.RadarCamera | CarCommands.SelfDestruct | CarCommands.TargetPrev | CarCommands.Skip;
        i.Write(b);
        Assert.Equal(i, CarInput.Read(b));
    }

    [Fact]
    public void TheQueueKeepsTheWiderCommands()
    {
        var q = new CommandQueue();
        q.Push(CarCommands.TargetPrev | CarCommands.SelfDestruct);
        q.Push(CarCommands.TargetPrev);
        Assert.Equal(CarCommands.TargetPrev | CarCommands.SelfDestruct, q.Take());
        Assert.Equal(CarCommands.TargetPrev, q.Take());
    }

    [Fact]
    public void ACommandQueueSendsEachKindOnceATick()
    {
        var q = new CommandQueue();
        q.Push(CarCommands.WeaponCycle);
        q.Push(CarCommands.WeaponCycle);
        q.Push(CarCommands.Lights | CarCommands.Reverse);
        Assert.Equal(CarCommands.WeaponCycle | CarCommands.Lights | CarCommands.Reverse, q.Take());
        Assert.Equal(CarCommands.WeaponCycle, q.Take());
        Assert.Equal(CarCommands.None, q.Take());
    }
}

public class CrewArmsTests
{
    [Fact]
    public void AnArmRisesInFiveFramesAndAShotHoldsItThree()
    {
        var arms = new CrewArms();
        Assert.True(arms.Idle(1) && arms.Idle(2));
        int ticks = 0;
        while (!arms.Ready(1)) { arms.Want(1, true); arms.Step(SimRate.Default.TickSeconds); ticks++; }
        Assert.InRange(ticks * SimRate.Default.TickSeconds, 0.4, 0.5);      // the first frame at once, then one a tenth of a second
        Assert.False(arms.Idle(1));
        Assert.True(arms.Idle(2));
        arms.Shoot(1);
        Assert.False(arms.Ready(1));
        for (int k = 0; k < (int)(0.9f / SimRate.Default.TickSeconds) + 1 && !arms.Ready(1); k++) arms.Step(SimRate.Default.TickSeconds);
        Assert.True(arms.Ready(1));
        arms.Reset();
        Assert.True(arms.Idle(1));
    }
}

public class FarPolicyTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void ANetworkSessionMeasuresFromThePlayersNotTheCamera()
    {
        var w = new SimWorld(new Flat(), new CrtRand()) { Far = FarPolicy.Humans };
        var a = new Vehicle(); var b = new Vehicle { Pos = new(2000, 0, 0) }; var ai = new Vehicle { Pos = new(2500, 0, 0) };
        w.Vehicles.AddRange([a, b, ai]);
        w.Humans.AddRange([a, b]);
        Assert.False(w.IsFar(ai));                    // 500 from b
        ai.Pos = new(5000, 0, 0);
        Assert.True(w.IsFar(ai));
        Assert.True(w.IsHuman(b) && !w.IsHuman(ai));
    }

    /// <summary>
    /// Single player measures the near/far path from the player's car, not the camera (deviations.md, "Near/far
    /// path"): the original's 25 + 848.5 from wherever its camera was, here from the car whatever the view.
    /// </summary>
    [Fact]
    public void SinglePlayerMeasuresFromThePlayersCar()
    {
        var w = new SimWorld(new Flat(), new CrtRand());
        var p = new Vehicle { Pos = new(1000, 0, 1000) }; var ai = new Vehicle { Pos = new(1000, 0, 1850) };
        w.Vehicles.AddRange([p, ai]);
        Assert.False(w.IsFar(ai));                    // no player's car: nothing is far
        w.Player = p;
        Assert.False(w.IsFar(ai));                    // 850 off: within 873
        ai.Pos = new(1000, 0, 1900);
        Assert.True(w.IsFar(ai));
        Assert.False(w.IsFar(p));
    }

    /// <summary>No view reaches the simulation: none of its classes takes a position (a camera) from the game layer.</summary>
    [Fact]
    public void TheSimulationHasNoCameraHook()
    {
        var hooks = typeof(SimWorld).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("I76.Sim") == true && t.Name != "ScriptCamera")
            .SelectMany(t => t.GetProperties().Where(pr => pr.PropertyType == typeof(Func<Vector3>) || pr.PropertyType == typeof(Func<Vector3?>)).Select(pr => $"{t.Name}.{pr.Name}"))
            .ToList();
        Assert.Empty(hooks);
    }
}

/// <summary>The session's tick, ids, snapshots and the network melee, on the real arenas (skipped without an install).</summary>
public class SessionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static MissionSetup Arena(string mission, IReadOnlyList<string> roster, MeleeRules rules)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", mission + ".msn")!);
        return MissionSetup.Build(vfs, w, asm, melee: roster, meleeRules: rules, seed: 7);
    }

    private static readonly MeleeRules Network = new() { Network = true };

    /// <summary>Scripted keys for a human, by tick and player: driving, turning, firing, the weapon and target keys.</summary>
    private static CarInput Keys(int t, int who)
    {
        var b = CarButtons.None;
        int u = t + who * 97;
        if (u % 400 < 330) b |= CarButtons.ThrottleUp;
        if (u % 400 is >= 350 and < 380) b |= CarButtons.ThrottleDown;
        if (u % 250 is > 60 and < 110) b |= who == 0 ? CarButtons.SteerLeft : CarButtons.SteerRight;
        if (u % 300 is > 200 and < 215) b |= CarButtons.EBrake;
        if (u % 120 < 50) b |= CarButtons.Fire;
        if (u % 500 is > 300 and < 330) b |= CarButtons.Hardpoint2;
        if (who == 1 && t > 900) b |= CarButtons.Analog;
        var c = CarCommands.None;
        if (u % 200 == 10) c |= CarCommands.TargetNearest;
        if (u % 450 == 222) c |= CarCommands.WeaponCycle;
        if (u % 700 == 333) c |= CarCommands.WeaponLink;
        if (u % 600 == 599) c |= CarCommands.Reverse;
        if (u % 900 == 450) c |= CarCommands.TargetNext;
        return new CarInput { Buttons = b, Commands = c, Steer = (short)(8000 * MathF.Sin(t * 0.01f)), Throttle = 25000 };
    }

    private static (SimSession Session, MissionSetup Setup) NetworkMelee(int humans, IReadOnlyList<string> roster)
    {
        var setup = Arena("m01", roster, Network);
        var game = setup.Melee!;
        string[] cars = ["valepre1.vcf", "vppirna1.vcf", "vcgcour1.vcf"];
        for (int i = 0; i < humans; i++) game.AddPlayer(cars[i % cars.Length], $"p{i}");
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        foreach (var p in game.Players) s.AddDriver(p.Car);
        return (s, setup);
    }

    /// <summary>
    /// The input log replays exactly: a 60 s network melee with two scripted
    /// humans and two AI cars, every tick's inputs written to a byte log; a
    /// fresh session fed the log hashes the same, tick for tick.
    /// </summary>
    [Fact]
    public void AnInputLogReplaysTheSameMeleeTickForTick()
    {
        if (InstallDir is null) return;
        int ticks = (int)(60 / SimRate.Default.TickSeconds);
        var log = new MemoryStream();
        var hashes = new List<ulong>();
        var (a, sa) = NetworkMelee(2, ["vppirna1.vcf", "vcgcour1.vcf"]);
        int shots = 0;
        var buf = new byte[CarInput.Size];
        for (int t = 0; t < ticks; t++)
        {
            CarInput[] inputs = [Keys(t, 0), Keys(t, 1)];
            foreach (var i in inputs) { i.Write(buf); log.Write(buf); }
            a.Tick(inputs);
            shots += sa.Combat.Events.Count(e => e is ShotFired);
            sa.Combat.Events.Clear(); sa.Damage.Events.Clear();
            hashes.Add(a.StateHash());
        }
        Assert.True(shots > 50, $"{shots} shots");
        Assert.All(a.World.Vehicles, v => Assert.NotEqual(0, v.NetId));
        Assert.Equal(a.World.Vehicles.Count, a.World.Vehicles.Select(v => v.NetId).Distinct().Count());
        output.WriteLine(string.Join(" | ", sa.Script!.Host.Entities.Where(e => e.Vehicle is not null).Select(e => $"{e.Name} {e.Health:0.#} {e.Order} {e.OrderTarget?.Name} at {e.Position.X:0},{e.Position.Z:0}")));
        output.WriteLine($"{shots} shots; scores {string.Join(", ", sa.Melee!.Players.Select(p => $"{p.Name} {p.Score}/{p.Kills}/{p.Deaths}"))}; ids up to {a.Ids.Next(NetKind.Round):x}");

        var (b, sb) = NetworkMelee(2, ["vppirna1.vcf", "vcgcour1.vcf"]);
        log.Position = 0;
        for (int t = 0; t < ticks; t++)
        {
            var inputs = new CarInput[2];
            for (int k = 0; k < 2; k++) { log.ReadExactly(buf); inputs[k] = CarInput.Read(buf); }
            b.Tick(inputs);
            sb.Combat.Events.Clear(); sb.Damage.Events.Clear();
            Assert.True(hashes[t] == b.StateHash(), $"tick {t}: the replay diverged");
        }
    }

    /// <summary>A driver's car restored from a snapshot steps again exactly as it did (client prediction's replay).</summary>
    [Fact]
    public void ASnapshotRestoresACarExactly()
    {
        if (InstallDir is null) return;
        var (s, setup) = NetworkMelee(1, []);
        var d = s.Drivers[0];
        var car = d.Car;
        for (int t = 0; t < 60; t++) s.Tick([Keys(t, 0)]);
        // something to restore in the damage state and the armoury
        var hit = new DamagePacket { Attacker = null, Mask = 1, Kind = Damage.DamageKind.Round };
        hit.Ch[0] = 60;
        setup.Damage.Apply(car, hit, new Vector3(0.9f, 0.5f, 1.5f));
        car.Armory!.Cycle();

        var snap = s.Capture(d);
        uint seed = s.World.Rand.Seed;
        setup.Combat.Rounds.Clear();
        List<ulong> Run()
        {
            var hashes = new List<ulong>();
            for (int t = 60; t < 260; t++)
            {
                s.Tick([Keys(t, 0) with { Commands = t % 50 == 0 ? CarCommands.WeaponCycle | CarCommands.Lights | CarCommands.Reverse : 0 }]);
                if (t == 120)
                {
                    var p = new DamagePacket { Mask = 8, Kind = Damage.DamageKind.Collision };
                    p.Ch[3] = 150;
                    setup.Damage.Apply(car, p, new Vector3(-0.9f, 0.5f, -1.5f));
                }
                var h = new StateHash();
                h.Add(car);
                h.Add(d.State.HoldUp); h.Add(d.State.HoldRight); h.Add(d.State.HoldLeft);
                hashes.Add(h.Value);
            }
            return hashes;
        }
        var first = Run();
        Assert.NotEqual(first[0], first[^1]);
        s.Restore(snap);
        s.World.Rand.Seed = seed;
        setup.Combat.Rounds.Clear();
        var second = Run();
        for (int k = 0; k < first.Count; k++) Assert.True(first[k] == second[k], $"step {k}: the restored car went its own way");
    }

    /// <summary>
    /// A kill scores by mass; the victim comes back only by asking (RAISE_DEAD): refused before 5 s, never
    /// on its own however long it waits, granted after.
    /// </summary>
    [Fact]
    public void AKillScoresByMassAndTheVictimRespawnsWhenItAsks()
    {
        if (InstallDir is null) return;
        var (s, setup) = NetworkMelee(2, []);
        var game = setup.Melee!;
        var (a, b) = (game.Players[0], game.Players[1]);
        Kill(setup, b.Car, a.Car);
        Assert.Equal((1, (int)(1000f * b.Car.P.Mass / a.Car.P.Mass)), (a.Kills, a.Score));
        int gen = b.Car.NetGeneration, id = b.Car.NetId;
        s.Tick([]);
        Assert.True(b.Destroyed);
        Assert.Equal(1, b.Deaths);
        Assert.Equal(0, b.Score);      // killed by another: a death, no points lost
        for (int t = 0; t < 4.5f / SimRate.Default.TickSeconds; t++) s.Tick([]);
        Assert.StartsWith("too soon", game.RequestRespawn(b));
        s.Tick([]);
        Assert.True(b.Destroyed);
        // no automatic respawn, however long it waits (the wreck timer's call is never the player's own car's)
        for (int t = 0; t < 30f / SimRate.Default.TickSeconds; t++) s.Tick([]);
        Assert.True(b.Destroyed);
        Assert.Null(game.RequestRespawn(b));
        s.Tick([]);
        Assert.False(b.Destroyed);
        Assert.False(Damage.DamageModel.IsDead(b.Car));
        Assert.Equal((gen + 1, id), (b.Car.NetGeneration, b.Car.NetId));
        Assert.Contains(game.Spawns, sp => System.Math.Abs(sp.Pos.X - b.Car.Pos.X) < 1 && System.Math.Abs(sp.Pos.Z - b.Car.Pos.Z) < 1);
        Assert.False(game.Over);
    }

    /// <summary>A throttle held through a death drives the respawned car (the key ramps start again with it).</summary>
    [Fact]
    public void AKeyHeldThroughARespawnDrivesTheNewCar()
    {
        if (InstallDir is null) return;
        var (s, setup) = NetworkMelee(2, []);
        var b = setup.Melee!.Players[1];
        var held = new CarInput { Buttons = CarButtons.ThrottleUp };
        for (int t = 0; t < 120; t++) s.Tick([default, held]);
        Assert.True(b.Car.Throttle > 0.9f);
        Kill(setup, b.Car, setup.Melee.Players[0].Car);
        for (int t = 0; t < 5.1f / SimRate.Default.TickSeconds; t++) s.Tick([default, held]);
        s.Tick([default, held with { Commands = CarCommands.RaiseDead }]);     // the session ignores it; the host asks the game
        Assert.Null(setup.Melee.RequestRespawn(b));
        s.Tick([default, held]);
        Assert.False(b.Destroyed);
        for (int t = 0; t < 30; t++) s.Tick([default, held]);
        Assert.True(b.Car.Throttle > 0.9f, $"throttle {b.Car.Throttle} after the respawn");
    }

    [Fact]
    public void ATeammatesDeathScoresNothing_TheTeamEndsAtItsScore()
    {
        if (InstallDir is null) return;
        var setup = Arena("m01", [], new MeleeRules { Network = true, TeamPlay = true, MaxScore = 900 });
        var game = setup.Melee!;
        var a = game.AddPlayer("vppirna1.vcf", "a", team: 1);
        var b = game.AddPlayer("vppirna1.vcf", "b", team: 1);
        var c = game.AddPlayer("vppirna1.vcf", "c", team: 3);
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        foreach (var p in game.Players) s.AddDriver(p.Car);
        Assert.Equal(setup.Combat.Team(a.Car), setup.Combat.Team(b.Car));
        Assert.NotEqual(setup.Combat.Team(a.Car), setup.Combat.Team(c.Car));
        Kill(setup, b.Car, a.Car);
        Assert.Equal(0, a.Score);
        s.Tick([]);
        Assert.False(game.Over);
        Kill(setup, c.Car, a.Car);
        Assert.Equal(1000, a.Score);
        s.Tick([]);
        // the goal is reached: the game is decided, and ends 10 s later (the original's start-up), the other team's cars blown up
        Assert.True(game.Ended);
        Assert.False(game.Over);
        for (int t = 0; t < 700 && !game.Over; t++) s.Tick([]);
        Assert.True(game.Over);
        Assert.True(I76.Sim.Damage.DamageModel.IsDead(c.Car));
    }

    [Fact]
    public void ANetworkMeleeEndsAtItsTimeLimit_RegenPadsArePerPlayer()
    {
        if (InstallDir is null) return;
        var setup = Arena("m01", [], new MeleeRules { Network = true, TimeLimit = 40 });
        var game = setup.Melee!;
        var a = game.AddPlayer("vppirna1.vcf", "a");
        var b = game.AddPlayer("vppirna1.vcf", "b");
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        s.AddDriver(a.Car); s.AddDriver(b.Car);
        // b leaves its spawn point for a regen pad, and waits there for the 30 s since its spawn to pass
        s.Tick([]);
        var pad = game.Regens[0].Pos;
        b.Car.Pos = new(pad.X, setup.Ground.BareHeight(pad.X, pad.Z), pad.Z);
        int gen = b.Car.NetGeneration;
        string? said = null;
        game.Message += m => said = m;
        while (!game.Ended) s.Tick([]);
        // the Nitro Pack: "*** Time limit has elapsed!", everyone a winner, nobody blown up, the end 10 s later
        Assert.InRange(setup.Script!.Host.Now, 40f, 40.1f);
        Assert.True(game.TimeUp);
        Assert.Equal("*** Time limit has elapsed!", said);
        Assert.Null(game.Winner);
        while (!game.Over) s.Tick([]);
        Assert.InRange(setup.Script!.Host.Now, 50f, 50.1f);
        Assert.False(Damage.DamageModel.IsDead(a.Car) || Damage.DamageModel.IsDead(b.Car));
        Assert.Equal((0, 400), (a.Score, b.Score));
        Assert.Equal(gen + 1, b.Car.NetGeneration);
    }

    /// <summary>
    /// A selected weapon shot to nothing cycles to the next live one in the next tick, with
    /// cammo.wav on the car for its driver; a tick that changes nothing asks for none.
    /// </summary>
    [Fact]
    public void ADeadSelectedWeaponCyclesWithCammo()
    {
        if (InstallDir is null) return;
        var (s, setup) = NetworkMelee(1, []);
        var car = s.Drivers[0].Car;
        var arm = car.Armory!;
        Assert.True(arm.Weapons.Count >= 2);
        s.Tick([]);
        setup.Combat.Events.Clear();
        s.Tick([]);
        Assert.DoesNotContain(setup.Combat.Events, e => e is WeaponClick { Sound: "cammo.wav" });
        int was = arm.Selected;
        arm.Weapons[was].Hp = 0;
        s.Tick([]);
        Assert.NotEqual(was, arm.Selected);
        var click = Assert.Single(setup.Combat.Events.OfType<WeaponClick>(), c => c.Sound == "cammo.wav");
        Assert.Same(car, click.Shooter);
    }

    /// <summary>Nitro's regen pad pays no 400 points in a network CTF game; it still renews the car.</summary>
    [Fact]
    public void ARegenPadPaysNoPointsInANetworkCtfGame()
    {
        if (InstallDir is null) return;
        var setup = Arena("m01", [], new MeleeRules { Network = true, Kind = MeleeKind.Ctf, TeamPlay = true, TimeLimit = 40 });
        var game = setup.Melee!;
        var a = game.AddPlayer("vppirna1.vcf", "a", 1);
        var b = game.AddPlayer("vppirna1.vcf", "b", 2);
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        s.AddDriver(a.Car); s.AddDriver(b.Car);
        s.Tick([]);
        var pad = game.Regens[0].Pos;
        b.Car.Pos = new(pad.X, setup.Ground.BareHeight(pad.X, pad.Z), pad.Z);
        int gen = b.Car.NetGeneration;
        while (!game.Over) s.Tick([]);
        Assert.Equal(0, b.Score);
        Assert.Equal(gen + 1, b.Car.NetGeneration);
    }

    /// <summary>
    /// A regen pad in a single-player melee is a brand-new car (the original replaces the vehicle): a blown
    /// tyre comes back whole, its rest height with it, not still × 0.688.
    /// </summary>
    [Fact]
    public void ARegenPadMendsABlownTyre()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, "valepre1.vcf", melee: [], seed: 7);
        var game = setup.Melee!;
        game.Start();
        var s = new SimSession(setup);
        var car = setup.World.Player!;
        var wheel = ((I76.Sim.Damage.WheelPart)car.Health!.Slots[0]!).Wheel;
        float rest = wheel.Rest;
        Assert.True(((I76.Sim.Damage.WheelPart)car.Health.Slots[0]!).BlowOut());
        Assert.True(wheel.Blown);
        Assert.Equal(rest * 0.688f, wheel.Rest, 5);
        bool renewed = false;
        game.PlayerRenewed += v => renewed |= v == car;
        s.Tick([]);
        var pad = game.Regens[0].Pos;
        for (int t = 0; t < (int)(44 / s.TickSeconds) && !renewed; t++)
        {
            car.Pos = new(pad.X, setup.Ground.BareHeight(pad.X, pad.Z), pad.Z);
            s.Tick([]);
        }
        Assert.True(renewed);
        Assert.False(wheel.Blown);
        Assert.Equal(rest, wheel.Rest, 5);
    }

    /// <summary>The driver's .45s are not part of the car's build: a regen pad's fresh armoury must still carry them, loaded.</summary>
    [Fact]
    public void ARegenPadKeepsTheDriversHandguns()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", melee: [], seed: 7);
        var game = setup.Melee!;
        game.Start();
        var s = new SimSession(setup);
        var car = setup.World.Player!;
        car.Armory!.AddHandguns(new Bwd2File(vfs.TryRead("vppirnha.vdf")!), true, true);
        var gun = car.Armory.Handguns[0]!;
        gun.Ammo = 3;
        var oldArmory = car.Armory;
        bool renewed = false;
        game.PlayerRenewed += v => renewed |= v == car;
        s.Tick([]);
        var pad = game.Regens[0].Pos;
        for (int t = 0; t < (int)(44 / s.TickSeconds) && !renewed; t++)
        {
            car.Pos = new(pad.X, setup.Ground.BareHeight(pad.X, pad.Z), pad.Z);
            s.Tick([]);
        }
        Assert.True(renewed);
        Assert.NotSame(oldArmory, car.Armory);
        Assert.NotNull(car.Armory!.Handguns[0]);
        Assert.NotNull(car.Armory.Handguns[1]);
        Assert.True(car.Armory.Handguns[0]!.Ammo > 3);
    }

    /// <summary>A round from the attacker that destroys the victim (as many as it takes).</summary>
    private static void Kill(MissionSetup setup, Vehicle victim, Vehicle attacker)
    {
        for (int k = 0; k < 200 && !Damage.DamageModel.IsDead(victim); k++)
        {
            var p = new DamagePacket { Attacker = attacker, Mask = 2, Kind = Damage.DamageKind.Round };
            p.Ch[1] = 5000;
            setup.Damage.Apply(victim, p, new Vector3(0, 0.5f, 2));
        }
        Assert.True(Damage.DamageModel.IsDead(victim));
    }
}

/// <summary>The commands of hotkeys step 4, on a real arena (skipped without an install).</summary>
public class HotkeyCommandTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (SimSession S, Vehicle Car) Session()
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, melee: ["vcgcour1.vcf"], meleeRules: new MeleeRules { Network = true }, seed: 7);
        var game = setup.Melee!;
        var p = game.AddPlayer("vppirna1.vcf", "a");
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        s.AddDriver(p.Car);
        s.Tick([]);
        return (s, p.Car);
    }

    private static CarInput Cmd(CarCommands c) => new() { Commands = c };

    [Fact]
    public void ShiftingMovesTheDriveSelectorWithinZeroToFive()
    {
        if (InstallDir is null) return;
        var (s, car) = Session();
        Assert.Equal(3, car.DriveState);
        for (int i = 0; i < 5; i++) s.Tick([Cmd(CarCommands.ShiftUp)]);
        Assert.Equal(5, car.DriveState);
        for (int i = 0; i < 8; i++) s.Tick([Cmd(CarCommands.ShiftDown)]);
        Assert.Equal(0, car.DriveState);
        Assert.Equal(1f, car.Gear);
        s.Tick([Cmd(CarCommands.ShiftUp)]);   // 1 is reverse
        Assert.Equal(1, car.DriveState);
        Assert.Equal(-1f, car.Gear);
    }

    [Fact]
    public void NitroNeedsAChargedType2SpecialAndRunsFifteenSeconds()
    {
        if (InstallDir is null) return;
        var (s, car) = Session();
        var slots = car.Health!.Slots;
        (slots[17], slots[18], slots[19]) = (new Damage.SpecialPart(1), new Damage.SpecialPart(4), null);
        s.Tick([Cmd(CarCommands.Special1)]);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.TurboDrive));
        s.Tick([Cmd(CarCommands.Special2)]);
        Assert.Equal(1, car.Unk_0xf4);
        s.Tick([Cmd(CarCommands.Special3)]);   // an empty slot
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.TurboDrive));
        var nitro = new Damage.SpecialPart(2);
        slots[19] = nitro;
        Assert.Equal(3, nitro.Charges);
        for (int n = 2; n >= 0; n--)
        {
            car.Timer = 0;
            car.Flags &= ~VehicleFlags.TurboDrive;
            s.Tick([Cmd(CarCommands.Special3)]);
            Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.TurboDrive));
            Assert.InRange(car.Timer, 14.8f, 15f);
            Assert.Equal(n, nitro.Charges);
        }
        // the charges are the sim's: a fourth press (a client spamming the key) does nothing
        car.Timer = 0;
        car.Flags &= ~VehicleFlags.TurboDrive;
        s.Tick([Cmd(CarCommands.Special3)]);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.TurboDrive));
        Assert.Equal(0, nitro.Charges);
    }

    [Fact]
    public void ReverseAfterAShiftInOneTickFollowsTheOriginalsOrder()
    {
        if (InstallDir is null) return;
        var (s, car) = Session();
        // Shift 3 -> 4, e8 = +1, then reverse: e8 = -1, selector 1
        s.Tick([Cmd(CarCommands.ShiftUp | CarCommands.Reverse)]);
        Assert.Equal(1, car.DriveState);
        Assert.Equal(-1f, car.Gear);
    }

    [Fact]
    public void RadarRangeAndCameraFlipAndSelfDestructKills()
    {
        if (InstallDir is null) return;
        var (s, car) = Session();
        var combat = s.Combat!;
        s.Tick([Cmd(CarCommands.RadarRange)]);
        Assert.Equal(0, combat.RadarRange[car]);
        s.Tick([Cmd(CarCommands.RadarRange)]);
        Assert.Equal(1, combat.RadarRange[car]);
        s.Tick([Cmd(CarCommands.RadarCamera)]);
        Assert.DoesNotContain(car, combat.RadarCamera);           // with a target only
        combat.RadarTarget[car] = s.World.Vehicles.First(v => v != car);
        s.Tick([Cmd(CarCommands.RadarCamera)]);
        Assert.Contains(car, combat.RadarCamera);
        s.Tick([Cmd(CarCommands.SelfDestruct)]);
        Assert.True(Damage.DamageModel.IsDead(car));
    }

    [Fact]
    public void TheRadarCameraGoesOffWithTheTargetPickers()
    {
        if (InstallDir is null) return;
        var (s, car) = Session();
        var combat = s.Combat!;
        var enemy = s.World.Vehicles.First(v => v != car);
        void On() { combat.RadarTarget[car] = enemy; if (!combat.RadarCamera.Contains(car)) combat.RadarCamera.Add(car); }
        On(); s.Tick([Cmd(CarCommands.TargetReset)]);
        Assert.DoesNotContain(car, combat.RadarCamera);
        On(); s.Tick([Cmd(CarCommands.TargetNearest)]);
        Assert.DoesNotContain(car, combat.RadarCamera);   // even when it picks a target
        On(); s.Tick([Cmd(CarCommands.TargetNext)]);      // no E press for 5 s: off
        Assert.DoesNotContain(car, combat.RadarCamera);
    }

    [Fact]
    public void SSwitchesTheEngineOffAtOnceAndCranksItOnAfterTheIgnitionTime()
    {
        if (InstallDir is null) return;
        var (s, car) = Session();
        var dm = (Damage.DamageModel)s.Combat!.Damage!;
        Assert.NotNull(car.Ignition);
        s.Tick([Cmd(CarCommands.StartEngine)]);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.EngineOn));
        Assert.Contains(dm.Events, e => e is Damage.EngineStopped);
        s.Tick([Cmd(CarCommands.StartEngine)]);
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.EngineOn));
        float len = car.Timer;
        Assert.True(len > 0);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.Started));
        s.Tick([Cmd(CarCommands.StartEngine)]);   // while cranking: ignored
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.EngineOn));
        for (int i = 0; i < (int)(len / SimRate.Default.TickSeconds) + 8; i++) s.Tick([Cmd(0)]);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.EngineOn));
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.Started));
    }
}

public class AutoReverseTests
{
    private static CarInput Keys(CarButtons b) => new() { Buttons = b };

    [Fact]
    public void APadsThrottleAxisAutoReversesLikeTheKeys()
    {
        static CarInput Axis(short t) => new() { Buttons = CarButtons.Analog, Throttle = t };
        // the left trigger (brake) held on a stopped car: into reverse, and the axis drives backwards
        var o = new AutoReverse().Apply(Axis(-20000), false, 0.1f);
        Assert.True((o.Commands & CarCommands.Reverse) != 0);
        Assert.Equal(20000, o.Throttle);
        // in reverse the brake trigger keeps backing up; the accelerate trigger brakes while still backing up
        Assert.Equal(20000, new AutoReverse().Apply(Axis(-20000), true, -3f).Throttle);
        Assert.Equal(-15000, new AutoReverse().Apply(Axis(15000), true, -3f).Throttle);
        // a resting axis (under a tenth) does nothing; driving forward is untouched
        Assert.Equal(CarCommands.None, new AutoReverse().Apply(Axis(-2000), false, 0.1f).Commands);
        Assert.Equal(30000, new AutoReverse().Apply(Axis(30000), false, 10f).Throttle);
    }

    [Fact]
    public void BrakeOnAStoppedCarShiftsIntoReverseAndDrives()
    {
        var o = new AutoReverse().Apply(Keys(CarButtons.ThrottleDown), false, 0.1f);
        Assert.True((o.Commands & CarCommands.Reverse) != 0);
        Assert.True(o.Has(CarButtons.ThrottleUp));
        Assert.False(o.Has(CarButtons.ThrottleDown));
        // in reverse, the held brake key keeps backing up
        o = new AutoReverse().Apply(Keys(CarButtons.ThrottleDown), true, -3f);
        Assert.Equal(CarCommands.None, o.Commands);
        Assert.True(o.Has(CarButtons.ThrottleUp));
    }

    [Fact]
    public void AccelerateWhileReversingBrakesThenGoesForward()
    {
        var a = new AutoReverse();
        var o = a.Apply(Keys(CarButtons.ThrottleUp), true, -5f);
        Assert.True(o.Has(CarButtons.ThrottleDown));
        Assert.False(o.Has(CarButtons.ThrottleUp));
        Assert.Equal(CarCommands.None, o.Commands);
        o = a.Apply(Keys(CarButtons.ThrottleUp), true, -0.2f);
        Assert.True((o.Commands & CarCommands.Reverse) != 0);
        Assert.True(o.Has(CarButtons.ThrottleUp));
    }

    [Fact]
    public void BrakeAtSpeedJustBrakesAndASwitchDoesNotFlipFlop()
    {
        var a = new AutoReverse();
        var o = a.Apply(Keys(CarButtons.ThrottleDown), false, 10f);
        Assert.True(o.Has(CarButtons.ThrottleDown));
        Assert.Equal(CarCommands.None, o.Commands);
        // forward in reverse gear (the gear not yet changed after a shift) does not shift again
        a.Apply(Keys(CarButtons.ThrottleDown), false, 0f);
        var again = a.Apply(Keys(CarButtons.ThrottleDown), false, 0f);
        Assert.Equal(CarCommands.None, again.Commands);
    }

    [Fact]
    public void BothOrNeitherKeyIsLeftAlone()
    {
        var a = new AutoReverse();
        var both = Keys(CarButtons.ThrottleUp | CarButtons.ThrottleDown);
        Assert.Equal(both, a.Apply(both, false, 0f));
        Assert.Equal(Keys(CarButtons.None), a.Apply(Keys(CarButtons.None), true, 0f));
    }
}
