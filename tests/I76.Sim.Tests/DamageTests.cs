using System.Numerics;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

public class DamageTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Flat, with the target's ground 0.3 higher (the roof gun fires level just over an identical car's COLP box).</summary>
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (z > 15 ? 0.3f : 0f, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Theory]
    [InlineData(0, 0.5f, 2, 0, 0)]      // front
    [InlineData(0, 0.5f, -2, 3, 5)]     // rear
    [InlineData(-1, 0.5f, 0.2f, 1, 1)]  // left, front half
    [InlineData(1, 0.5f, -0.2f, 2, 4)]  // right, rear half
    [InlineData(0, 3, 0.1f, 4, 6)]      // top
    public void ZonesFollowTheHitPoint(float x, float y, float z, int zone, int sub)
    {
        Assert.Equal((zone, sub), VehicleDamage.Zones(new Vector3(x, y, z)));
    }

    [Fact]
    public void PickRowsAndThePlayerRescale()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var v = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var ai = VehicleDamage.Create(v, v.Build!, false, new CrtRand(1));
        var w = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var pl = VehicleDamage.Create(w, w.Build!, true, new CrtRand(1));
        Assert.Equal([0, 1, 7, 8, 9], ai.Rows[0].Take(5));
        Assert.Equal([1, 7, 8, 9], ai.Rows[1].Take(4));
        Assert.Equal([5, 8, 9], ai.Rows[2].Take(3));
        Assert.Equal(6, ai.Rows[6][0]);
        Assert.All(ai.Rows, r => Assert.Equal(-1, r[^1]));
        Assert.Equal([-1, -1, -1], ai.Rows[6].TakeLast(3));
        for (int z = 0; z < 4; z++) Assert.Equal(2 * ai.Armour[z], pl.Armour[z]);
        Assert.Equal(4 * ai.Engine!.Max, pl.Engine!.Max);
        Assert.Equal(300, ai.Engine.Max);
        Assert.Equal(100f, ai.HealthPercent());
        log.WriteLine($"armour {string.Join(",", ai.Armour)} chassis {string.Join(",", ai.Chassis)} rows {string.Join(" | ", ai.Rows.Select(r => string.Join(",", r)))}");
    }

    [Fact]
    public void ShotUntilDeadThenAWreck()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        target.Pos = new Double3(0, 0.3, 30);
        target.Rot = Rows.AboutY(MathF.PI);   // facing the shooter: hits land on its front
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        world.Player = shooter;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == world.Player, world.Rand);
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var damage = new DamageModel(world, combat);
        Vehicle? killed = null;
        damage.Killed += v => killed = v;
        var a = shooter.Armory!;
        var keys = new bool[5];
        for (int i = 0; i < a.Weapons.Count; i++) keys[i] = a.Weapons[i].MountKind == 1 && a.Weapons[i].Def.Class == 2;
        float h0 = target.Health!.HealthPercent();
        int f = 0;
        for (; f < 3000 && killed is null; f++)
        {
            target.Pos = new Double3(0, 0.3, 30);     // hold it in the line of fire
            a.PlayerTriggers(false, keys);
            world.Frame(0.036f);
            combat.Frame(0.036f);
        }
        log.WriteLine($"killed after {f * 0.036f:0.0} s; armour {string.Join(",", target.Health.Armour)}; engine {target.Health.Engine!.Hp}; health {target.Health.HealthPercent():0.##}");
        Assert.Same(target, killed);
        Assert.True(target.Health.Armour[0] < target.Health.ArmourMax[0]);
        Assert.True(DamageModel.IsDead(target));
        Assert.Equal(VehicleFlags.Disabled, target.Flags & VehicleFlags.Disabled);
        Assert.Contains(combat.Events, e => e is Explosion x && x.Effect.File == "xcarx1.xdf");
        for (int k = 0; k < 300 && !target.Wreck; k++) world.Frame(0.036f);
        Assert.True(target.Wreck);
        Assert.True(h0 > 99);
    }

    /// <summary>
    /// Issue #71. The damage smoke takes the same value as the bar (it is fed the health percentage * 0.01):
    /// a car flamed to red smokes black though its armour is whole, and the stages sit at 0.75 / 0.6 / 0.4.
    /// </summary>
    [Fact]
    public void FlameDamageMakesBlackSmoke()
    {
        Assert.Equal(-1, VehicleDamage.SmokeStage(1f));
        Assert.Equal(-1, VehicleDamage.SmokeStage(0.75f));
        Assert.Equal(0, VehicleDamage.SmokeStage(0.74f));
        Assert.Equal(0, VehicleDamage.SmokeStage(0.61f));
        Assert.Equal(1, VehicleDamage.SmokeStage(0.6f));
        Assert.Equal(1, VehicleDamage.SmokeStage(0.41f));
        Assert.Equal(2, VehicleDamage.SmokeStage(0.4f));
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(target);
        target.Health = VehicleDamage.Create(target, target.Build!, false, world.Rand);
        var damage = new DamageModel(world, new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null));
        var h = target.Health!;
        Assert.Equal(-1, h.SmokeStage());
        for (int i = 0; i < 60 && h.GaugeFraction() >= 1f; i++)
        {
            var p = new DamagePacket { Mask = 4, Kind = DamageKind.Flame };
            p.Ch[2] = 3;
            damage.Apply(target, p, new Vector3(0, 0.5f, 2));
        }
        for (int z = 0; z < 4; z++) Assert.Equal(h.ArmourMax[z], h.Armour[z]);
        Assert.Equal(2, h.SmokeStage());
    }

    /// <summary>
    /// Issue #30. The target box's bar is the health percentage: one flame tick, which skips the armour
    /// and scratches a component, puts it in the red, and the same value (&lt;= 33) is the .45's "&gt; 33 is cut to 5 points" test, which the handgun tests below cover.
    /// </summary>
    [Fact]
    public void FlameDamageTurnsTheGaugeRedAndOpensTheHandgunRule()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(target);
        target.Health = VehicleDamage.Create(target, target.Build!, false, world.Rand);
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var damage = new DamageModel(world, combat);
        var h = target.Health!;
        Assert.Equal(1f, h.GaugeFraction());     // unhurt: white
        // flame ticks (mask 4, the fire channel) until some component is hurt; the car must still live
        for (int i = 0; i < 60 && h.GaugeFraction() >= 1f; i++)
        {
            var p = new DamagePacket { Mask = 4, Kind = DamageKind.Flame };
            p.Ch[2] = 3;
            damage.Apply(target, p, new Vector3(0, 0.5f, 2));
        }
        Assert.False(DamageModel.IsDead(target));
        Assert.True(h.GaugeFraction() < 1f);
        Assert.Equal(h.HealthPercent() / 100f, h.GaugeFraction());
        Assert.True(h.HealthPercent() <= 33f, "the gauge reads red, as the original's");
        // the armour is untouched (fire skips it): the old zone-based bar would still have shown green
        for (int z = 0; z < 4; z++) Assert.Equal(h.ArmourMax[z], h.Armour[z]);
    }

    [Fact]
    public void TheHandgunKillsTheDriverOfACarInTheRed()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        shooter.Armory!.AddHandguns(new Bwd2File(vfs.TryRead("vppirnha.vdf")!), true, false);
        var gun = shooter.Armory.Handguns[0]!;
        // abeam, out of the right window, along the muzzle's line
        var f = gun.MuzzleRot.Forward;
        target.Pos = new Double3(gun.MuzzlePos.X + f.X * 12, 0.3, gun.MuzzlePos.Z + f.Z * 12);
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        world.Player = shooter;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == world.Player, world.Rand);
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var damage = new DamageModel(world, combat);
        var h = target.Health!;
        h.Brake!.Hp = h.Brake.Max / 2;   // a damaged brake: the health percentage drops to a 0-1 ratio, under 33
        Assert.True(h.HealthPercent() < 33f);
        for (int t = 0; t < 400 && ((uint)target.Flags & 0x20) == 0; t++)
        {
            gun.Trigger = true;
            combat.Frame(0.036f);
            damage.Timer(target, 0.036f);
        }
        Assert.Equal(0x4020u, (uint)target.Flags & 0x4020u);
        var k = Assert.Single(damage.Events.OfType<VehicleKilled>());
        Assert.True(k.DriverKilled);
        Assert.DoesNotContain(combat.Events, e => e is Explosion x && x.Effect.File.StartsWith("x1_car", StringComparison.OrdinalIgnoreCase));   // no car blast
        Assert.StartsWith("vhorn", VehicleFactory.HornSound(vfs, target.EngineNumber));
    }

    /// <summary>
    /// #109: a healthy car takes many .45 shots (5 points on the armour while its gauge is above a third), and then dies to
    /// the kill roll, which is 0.1 for the player in single player and 2.5 otherwise, a network melee's players included
    /// (k = (!net &amp;&amp; player) ? 0.1 : 2.5); a network car has no player rescale (armour x2, parts x4) either.
    /// </summary>
    [Fact]
    public void ANetworkCarHasNoPlayerRescaleAndFallsToTheHandgunSooner()
    {
        if (InstallDir is null) return;
        double single = ShotsToKill(false), net = ShotsToKill(true);
        Assert.True(net < single * 0.6, $"{net} shots in a network melee, {single} in single player");
    }

    private static double ShotsToKill(bool network)
    {
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var shots = new List<int>();
        for (uint seed = 1; seed <= 12; seed++)
        {
            var world = new SimWorld(new Flat(), new CrtRand(seed));
            var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
            var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
            shooter.Armory!.AddHandguns(new Bwd2File(vfs.TryRead("vppirnha.vdf")!), true, false);
            var gun = shooter.Armory.Handguns[0]!;
            var f = gun.MuzzleRot.Forward;
            target.Pos = new Double3(gun.MuzzlePos.X + f.X * 12, 0.3, gun.MuzzlePos.Z + f.Z * 12);
            world.Vehicles.Add(shooter); world.Vehicles.Add(target);
            world.Player = shooter;
            shooter.Health = VehicleDamage.Create(shooter, shooter.Build!, true, world.Rand, network);
            target.Health = VehicleDamage.Create(target, target.Build!, true, world.Rand, network);
            var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { NetSession = network };
            var damage = new DamageModel(world, combat);
            if (network) Assert.Equal(target.Build!.Armour[0], target.Health.ArmourMax[0]);
            else Assert.Equal(target.Build!.Armour[0] * 2, target.Health.ArmourMax[0]);
            int fired = 0;
            for (int t = 0; t < 6000 && ((uint)target.Flags & 0x20) == 0; t++)
            {
                gun.Trigger = true;
                combat.Frame(0.036f);
                damage.Timer(target, 0.036f);
                fired += combat.Events.OfType<ShotFired>().Count();
                combat.Events.Clear();
            }
            shots.Add(((uint)target.Flags & 0x20) != 0 ? fired : int.MaxValue);
        }
        Assert.DoesNotContain(int.MaxValue, shots);
        return shots.Average();
    }

    /// <summary>The shooter on a 2 m shelf (x &lt; 6), the rest of the world flat at 0.</summary>
    private sealed class Shelf : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (x < 6 ? 2f : 0f, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>
    /// The handgun kill on a crippled car abeam and 2 m below: with the right arm up, the right
    /// side arc (15° about the car's right axis) picks the car, and the original
    /// tips the .45's muzzle down at its collider centre, so the rounds reach it.
    /// Level, they would pass over its roof.
    /// </summary>
    [Fact]
    public void TheRaisedHandgunTipsAtACarAbeamAndKillsItsDriver()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Shelf(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        shooter.Armory!.AddHandguns(new Bwd2File(vfs.TryRead("vppirnha.vdf")!), true, false);
        var gun = shooter.Armory.Handguns[0]!;
        shooter.Pos = new Double3(0, 2, 0);
        target.Pos = new Double3(25, 0, shooter.Rot.ToWorld(gun.MuzzlePos).Z);
        target.Flags &= ~VehicleFlags.EngineOn;   // crippled and stopped: off the radar, the side arc still sees it
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        world.Player = shooter;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == world.Player, world.Rand);
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var damage = new DamageModel(world, combat);
        target.Health!.Brake!.Hp = target.Health.Brake.Max / 2;
        Assert.True(target.Health.HealthPercent() < 33f);
        const float dt = 0.036f;
        combat.Frame(dt);
        Assert.Null(combat.TargetFor(shooter, 8));           // arm down: no side arc
        Assert.Equal(0f, gun.GunPitch);
        int t = 0;
        for (; t < 400 && ((uint)target.Flags & 0x20) == 0; t++)
        {
            shooter.Arms.Want(1, true);
            shooter.Arms.Step(dt);
            if (shooter.Arms.Ready(1)) gun.Trigger = true;
            combat.Frame(dt);
            damage.Timer(target, dt);
            if (t == 20)
            {
                Assert.Equal(target, combat.TargetFor(shooter, 8));
                Assert.InRange(gun.GunPitch, -0.17453292f, -0.05f);   // tipped down, inside the gun's 10 degree limit
            }
        }
        log.WriteLine($"driver killed after {t * dt:0.0} s, pitch {gun.GunPitch * 180 / MathF.PI:0.0} deg");
        Assert.Equal(0x4020u, (uint)target.Flags & 0x4020u);
        Assert.True(Assert.Single(damage.Events.OfType<VehicleKilled>()).DriverKilled);
    }

    /// <summary>The AI's radar-target update: an AI's turrets lose a player whose engine is off (as with a jammer); its fixed guns keep the order target.</summary>
    [Fact]
    public void AnAiTurretLosesAPlayerWhoseEngineIsOffButFixedGunsKeepIt()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Shelf(), new CrtRand(1));
        var ai = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var player = VehicleFactory.Create(vfs, "vppirna1.vcf");
        player.Pos = new Double3(100, 0, 0);
        world.Vehicles.Add(ai); world.Vehicles.Add(player);
        world.Player = player;
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        combat.RadarTarget[ai] = player;                       // what the AI's order sets (ScriptHost.FireAt)
        Assert.Equal(player, combat.TargetFor(ai, 4));
        Assert.Equal(player, combat.TargetFor(ai, 1));
        player.Flags &= ~VehicleFlags.EngineOn;
        Assert.Null(combat.TargetFor(ai, 4));
        Assert.Equal(player, combat.TargetFor(ai, 1));
        player.Flags |= VehicleFlags.EngineOn;
        Assert.Equal(player, combat.TargetFor(ai, 4));
    }

    /// <summary>
    /// Taurus's van (T14) with its right side's armour gone, as measured in the original (retail 1.0 on an emulated
    /// Pentium): seven 45-point rounds through that side each landed whole on one live component of the side's rows (both
    /// right wheels 100 to 55, engine 300 to 255, suspension 200 to 65, brake 150 to 105), the chassis words stayed full
    /// and the van lived. Bullets never touch the chassis; with no component destroyed there is no kill roll.
    /// </summary>
    [Fact]
    public void RoundsThroughAZeroArmourSideGoWholeToALiveComponent()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var van = VehicleFactory.Create(vfs, "t14sv01.vcf");
        world.Vehicles.Add(van);
        van.Health = VehicleDamage.Create(van, van.Build!, false, world.Rand);
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var damage = new DamageModel(world, combat);
        var d = van.Health;
        d.Armour[2] = d.ArmourMirror[2] = 0;
        int Parts() => d.Slots.Where(x => x is not null and not WeaponPart).Sum(x => x!.Hp) + d.Slots.OfType<WeaponPart>().Sum(x => x.Hp);
        for (int i = 0; i < 7; i++)
        {
            int before = Parts();
            var p = new DamagePacket { Mask = 1, Kind = DamageKind.Round };
            p.Ch[0] = 45;
            damage.Apply(van, p, new Vector3(1, 0.5f, i % 2 == 0 ? 0.2f : -0.2f));   // the right side, front and rear half
            Assert.False(van.Health.V.Flags.HasFlag(VehicleFlags.Dead));
            Assert.Contains(before - Parts(), new[] { 0, 45 });   // whole on one component, or lost on the row's empty entry
            Assert.Equal(d.ChassisMax, d.Chassis);
            Assert.Equal(0, d.Armour[2]);
        }
        Assert.All(d.Slots.Where(x => x is not null), x => Assert.True(x!.Hp > 0));
    }
}
