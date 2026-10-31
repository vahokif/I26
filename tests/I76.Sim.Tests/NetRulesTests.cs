using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The original's network melee rules (docs/multiplayer.md, "The original's rules"): what a death books
///, the end's blow-up, a leaver's car, the eighth team, and a reconfigured respawn.
/// Skipped without an install.
/// </summary>
public class NetRulesTests
{
    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (SimSession Session, MissionSetup Setup) Melee(MeleeRules rules, params (string Car, int Team)[] players)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, melee: [], meleeRules: rules, seed: 7);
        var game = setup.Melee!;
        for (int i = 0; i < players.Length; i++) game.AddPlayer(players[i].Car, $"p{i}", players[i].Team);
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        foreach (var p in game.Players) s.AddDriver(p.Car);
        return (s, setup);
    }

    private static void Kill(MissionSetup setup, Vehicle victim, Vehicle? attacker)
    {
        for (int k = 0; k < 200 && !Damage.DamageModel.IsDead(victim); k++)
        {
            var p = new DamagePacket { Attacker = attacker, Mask = 2, Kind = Damage.DamageKind.Round };
            p.Ch[1] = 5000;
            setup.Damage.Apply(victim, p, new Vector3(0, 0.5f, 2));
        }
        Assert.True(Damage.DamageModel.IsDead(victim));
    }

    private static void Wait(SimSession s, float seconds)
    {
        for (int t = 0; t < seconds / s.Rate.TickSeconds; t++) s.Tick([]);
    }

    /// <summary>Killed by one's own weapon (a mine, a mortar's splash): nothing is booked, no death, no −500.</summary>
    [Fact]
    public void AKillWithOnesOwnWeaponBooksNothing()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var a = setup.Melee!.Players[0];
        a.Score = 1200;
        Kill(setup, a.Car, a.Car);
        s.Tick([]);
        Assert.True(a.Destroyed);
        Assert.Equal((0, 0, 1200), (a.Deaths, a.Kills, a.Score));
    }

    /// <summary>A teammate's kill books nothing for the victim (and scores nothing for the killer); another team's books a death and costs nothing.</summary>
    [Fact]
    public void ATeamKillBooksNoDeath_AnotherTeamsKillADeathOnly()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true, TeamPlay = true }, ("vppirna1.vcf", 1), ("vppirna1.vcf", 1), ("vppirna1.vcf", 2));
        var (a, b, c) = (setup.Melee!.Players[0], setup.Melee.Players[1], setup.Melee.Players[2]);
        b.Score = 800;
        Kill(setup, b.Car, a.Car);
        c.Score = 800;
        Kill(setup, c.Car, a.Car);
        s.Tick([]);
        Assert.Equal((0, 800), (b.Deaths, b.Score));
        Assert.Equal((1, 800), (c.Deaths, c.Score));
        Assert.Equal(1, a.Kills);
    }

    /// <summary>
    /// Fire held through a death lets go when the key does (the original reads the keys every frame): a dead car's triggers once
    /// followed the last live tick's fire for good, and the wreck fired until its guns were empty.
    /// </summary>
    [Fact]
    public void FireHeldThroughADeathStopsWhenTheKeyIsLetGo()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var a = setup.Melee!.Players[0];
        var fire = new CarInput { Buttons = CarButtons.Fire };
        for (int t = 0; t < 10; t++) s.Tick([fire, default(CarInput)]);
        s.Tick([fire with { Commands = CarCommands.SelfDestruct }, default(CarInput)]);
        for (int t = 0; t < 10; t++) s.Tick([fire, default(CarInput)]);
        Assert.True(Damage.DamageModel.IsDead(a.Car));
        s.Tick([default(CarInput), default(CarInput)]);
        s.Tick([default(CarInput), default(CarInput)]);
        int Ammo() => a.Car.Armory!.Weapons.Sum(w => w.Ammo);
        int before = Ammo();
        for (int t = 0; t < 120; t++) s.Tick([default(CarInput), default(CarInput)]);
        Assert.Equal(before, Ammo());
        Assert.All(a.Car.Armory!.Weapons, w => Assert.False(w.WasFiring));
    }

    /// <summary>#109: a wreck with the trigger held spends no ammo and fires nothing.</summary>
    [Fact]
    public void ADeadCarWithTheTriggerHeldFiresNothing()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var a = setup.Melee!.Players[0];
        var fire = new CarInput { Buttons = CarButtons.Fire };
        s.Tick([new CarInput { Commands = CarCommands.SelfDestruct }, default(CarInput)]);
        s.Tick([fire, default(CarInput)]);
        Assert.True(Damage.DamageModel.IsDead(a.Car));
        int Ammo() => a.Car.Armory!.Weapons.Sum(w => w.Ammo);
        int before = Ammo();
        for (int t = 0; t < 120; t++) s.Tick([fire, default(CarInput)]);
        Assert.Equal(before, Ammo());
        Assert.All(a.Car.Armory!.Weapons, w => Assert.False(w.WasFiring));
    }

    /// <summary>A death with no killer (a crash, SELF_DESTRUCT): a death and −500, to a floor of 0.</summary>
    [Fact]
    public void ADeathWithNoKillerCostsADeathAndFiveHundred()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var (a, b) = (setup.Melee!.Players[0], setup.Melee.Players[1]);
        a.Score = 1200;
        b.Score = 300;
        Kill(setup, a.Car, null);
        s.Tick([default(CarInput), new CarInput { Commands = CarCommands.SelfDestruct }]);
        s.Tick([]);
        Assert.Equal((1, 700), (a.Deaths, a.Score));
        Assert.Equal((1, 0), (b.Deaths, b.Score));
    }

    /// <summary>
    /// The goal reached: the losers' cars blow up (the original's start-up) with no killer, so a live loser takes a death and
    /// −500; a loser already dead is left as it is; nobody comes back once won.
    /// </summary>
    [Fact]
    public void LosersBlownUpAtTheEndPayFiveHundred()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true, MaxScore = 1000 }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var game = setup.Melee!;
        var (a, b, c) = (game.Players[0], game.Players[1], game.Players[2]);
        b.Score = 900;
        Kill(setup, c.Car, a.Car);     // c is dead before the end: a death, nothing lost
        s.Tick([]);
        a.Score = 1000;
        s.Tick([]);
        Assert.True(game.Ended);
        Assert.Equal(a, game.Winner);
        s.Tick([]);
        Assert.True(Damage.DamageModel.IsDead(b.Car));
        Assert.Equal((1, 400), (b.Deaths, b.Score));
        Assert.Equal(1, c.Deaths);
        Assert.False(Damage.DamageModel.IsDead(a.Car));
        Wait(s, 5.5f);
        Assert.Equal("the game is over", game.RequestRespawn(b));
    }

    /// <summary>A leaver's live car is blown up and stays as a wreck, out of the world 10 s after.</summary>
    [Fact]
    public void ALeaversCarIsBlownUpAndClearedLater()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var game = setup.Melee!;
        var b = game.Players[1];
        var car = b.Car;
        Vehicle? removed = null;
        game.Removed += v => removed = v;
        s.RemoveDriver(s.Drivers[1]);
        game.RemovePlayer(b);
        Assert.DoesNotContain(b, game.Players);
        Assert.True(Damage.DamageModel.IsDead(car));
        Assert.Contains(car, setup.World.Vehicles);
        Assert.Null(removed);
        Wait(s, 9f);
        Assert.Null(removed);
        Wait(s, 2f);
        Assert.Equal(car, removed);
        Assert.Equal(0, game.Players[0].Kills);
    }

    /// <summary>The eighth team (Men In Black) is a team of its own, apart from the AI fill's nobody's 0.</summary>
    [Fact]
    public void TheEighthTeamIsATeam()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true, TeamPlay = true }, ("vppirna1.vcf", 8), ("vppirna1.vcf", 7), ("vppirna1.vcf", 8));
        var (a, b, c) = (setup.Melee!.Players[0], setup.Melee.Players[1], setup.Melee.Players[2]);
        Assert.Equal((8, 7, 8), (a.Team, b.Team, c.Team));
        Assert.Equal(8, setup.Combat.Team(a.Car));
        Kill(setup, c.Car, a.Car);
        s.Tick([]);
        Assert.Equal((0, 0), (a.Kills, c.Deaths));
    }

    /// <summary>
    /// A reconfigured respawn (I26's Reconfigure): the next respawn builds the new car in place, the same vehicle object and
    /// id, another model, its damage its own, the car's build counted; the respawn after keeps it.
    /// </summary>
    [Fact]
    public void AReconfiguredRespawnRebuildsTheCarInPlace()
    {
        if (InstallDir is null) return;
        var (s, setup) = Melee(new MeleeRules { Network = true }, ("vppirna1.vcf", 0), ("vppirna1.vcf", 0));
        var game = setup.Melee!;
        var b = game.Players[1];
        var car = b.Car;
        int id = car.NetId;
        Kill(setup, car, game.Players[0].Car);
        s.Tick([]);
        b.NextCar = "vcgcour1.vcf";
        Wait(s, 5.1f);
        Assert.Null(game.RequestRespawn(b));
        s.Tick([]);
        Assert.False(b.Destroyed);
        Assert.Same(car, b.Car);
        Assert.Equal(id, car.NetId);
        Assert.Equal(("vcgcour1.vcf", 1), (b.CarFile, b.CarEpoch));
        Assert.Null(b.NextCar);
        Assert.Equal("vcgcour1.vcf", car.Definition, ignoreCase: true);
        Assert.Same(car, car.Health!.V);
        Assert.False(Damage.DamageModel.IsDead(car));
        // it drives
        for (int t = 0; t < 60; t++) s.Tick([default(CarInput), new CarInput { Buttons = CarButtons.ThrottleUp }]);
        Assert.True(car.Speed > 1, $"speed {car.Speed}");
        Kill(setup, car, game.Players[0].Car);
        s.Tick([]);
        Wait(s, 5.1f);
        Assert.Null(game.RequestRespawn(b));
        s.Tick([]);
        Assert.Equal(("vcgcour1.vcf", 1), (b.CarFile, b.CarEpoch));
    }
}
