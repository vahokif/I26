using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// An AI ordered to attack a structure shoots at it (the original's targeting takes any object: a structure is still,
/// aimed at its collider centre, the guns' tolerance 20). Measured in the original game:
/// T01's three attackers, sent at the Wagon Wheel diner from 110-190 m, answered "fire" on a fifth of their calls and
/// took its deck node DW1_DCK1 from 400 to 55 HP in three seconds. The port's fire decision took only cars, so they
/// drove at the diner and never fired.
/// </summary>
public class AiStructureTargetTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (MissionSetup Setup, SimSession Session) Mission(string folder, string file, uint seed = 1)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve(folder, file)!), asm, rand: new I76.Sim.Rng.CrtRand(seed));
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        return (setup, s);
    }

    private static int HitPoints(Entity e) => e.Static!.Hp + e.Static.Nodes.Sum(n => n.Hp);

    /// <summary>Ticks until <paramref name="seconds"/> of mission time, the player's car kept whole; returns the shots fired by cars whose name starts with <paramref name="who"/>.</summary>
    private static int Run(MissionSetup setup, SimSession s, float seconds, string who, Action? each = null)
    {
        var input = new CarInput();
        var h = setup.Script!.Host;
        int shots = 0;
        while (h.Now < seconds && setup.Script.Result is null)
        {
            each?.Invoke();
            var hd = setup.World.Player!.Health!;
            Array.Copy(hd.ArmourMax, hd.Armour, 4); Array.Copy(hd.ChassisMax, hd.Chassis, 4);
            s.Tick([input]);
            shots += setup.Combat.Events.OfType<ShotFired>().Count(e => h.Of(e.Shooter)?.Name.StartsWith(who) == true);
            setup.Combat.Events.Clear();
            setup.Damage.Events.Clear();
        }
        return shots;
    }

    [Fact]
    public void T01sAttackersShootTheDiner()
    {
        if (InstallDir is null) return;
        var (setup, s) = Mission("miss16", "t01.msn");
        var diner = setup.Entities.First(e => e.Name == "bdwgnwl1");
        var fireworks = setup.Entities.First(e => e.Name == "bcfirew1");
        int hp0 = HitPoints(diner);
        Run(setup, s, 30, "t01");
        // the fireworks stand gone (the script's destroy-object action's way, until type 2's quartering gives) and data[29], which the
        // script sets when Taurus nears the diner: the attackers' threads now order `attack(enemy, wagon, 1)`
        while (!fireworks.Dead) setup.Damage.DestroyStructure(fireworks.Static!);
        setup.Script!.Vm!.Data[29] = 1;
        bool ordered = false;
        int shots = Run(setup, s, 36, "t01", () => ordered |= setup.Entities.Any(e => e.Name.StartsWith("t01f") && e.OrderTarget == diner));
        Assert.True(ordered);
        Assert.True(shots >= 10, $"{shots} shots");
        Assert.True(HitPoints(diner) <= hp0 - 150, $"diner {hp0} -> {HitPoints(diner)}");
    }

    /// <summary>
    /// T01 played through as a player would: follow Taurus to Seagraves, blow up the fireworks stand with the car's own
    /// guns, follow him on to the Wagon Wheel. Nothing is forced: the stand goes to the player's rounds and data[29] is set
    /// by Taurus's arrival, so the attackers' threads order `attack(enemy, wagon, 1)` from wherever their ring round the
    /// diner (nav 9) has brought them, 60-110 m off. Their bearings, ranges and steering take the diner's own routine
    /// point, its collider centre, 53 m from its placement; taken from the placement they drove past it with their guns
    /// 30-60 degrees off and barely hit it. Whether a run's tactics bring them round to face it in the three seconds the
    /// script gives each attack is the dice, so four seeds are run and the attackers must hurt it in three.
    /// </summary>
    [Fact]
    public void T01sAttackersShootTheDinerWhenTaurusGetsThere()
    {
        if (InstallDir is null) return;
        int hurt = 0;
        var log = new List<string>();
        foreach (uint seed in (uint[])[1, 2, 3, 4])
        {
            var (ordered, shots, damage, fireworks, data29) = PlayT01ToTheDiner(seed);
            Assert.True(fireworks, $"seed {seed}: the player's rounds took the fireworks stand");
            Assert.True(ordered > 0, $"seed {seed}: no attack order on the diner (data[29] = {data29})");
            if (shots >= 10 && damage >= 100) hurt++;
            log.Add($"seed {seed}: ordered at {ordered:0}s, {shots} shots, diner -{damage}");
        }
        Assert.True(hurt >= 3, string.Join("; ", log));
    }

    /// <summary>
    /// Plays T01 from the start with a driver at the player's wheel until the attackers' first round on the diner is over
    /// (their order on it plus 20 s): the attack order's time, the attackers' shots while ordered at the diner, the diner's
    /// loss, whether the fireworks stand went and the script's data[29].
    /// </summary>
    private static (float Ordered, int Shots, int Damage, bool Fireworks, int Data29) PlayT01ToTheDiner(uint seed)
    {
        var (setup, s) = Mission("miss16", "t01.msn", seed);
        var h = setup.Script!.Host;
        var diner = setup.Entities.First(e => e.Name == "bdwgnwl1");
        var fireworks = setup.Entities.First(e => e.Name == "bcfirew1");
        var taurus = setup.Entities.First(e => e.Name == "t01js01");
        var player = setup.World.Player!;
        int hp0 = HitPoints(diner);
        float orderedAt = -1;
        int shots = 0;
        while (setup.Script.Result is null && h.Now < (orderedAt < 0 ? 360 : orderedAt + 20))
        {
            var hd = player.Health!;
            Array.Copy(hd.ArmourMax, hd.Armour, 4); Array.Copy(hd.ChassisMax, hd.Chassis, 4);
            var input = new CarInput();
            if (h.Now >= 21.3f)   // the intro over
            {
                // objective 2 open: drive at the stand, stop short and fire once the guns bear; otherwise 15 m behind Taurus
                bool stand = !fireworks.Dead && (h.Objectives.Flags[1] & (Objectives.Hidden | Objectives.Succeeded)) == 0;
                var goal = stand ? fireworks.Static!.Centre : taurus.Position - taurus.Rot.Forward * 15;
                var to = goal - player.Pos.ToVector3();
                float err = MathF.Atan2(to.X, to.Z) - MathF.Atan2(player.Rot.Forward.X, player.Rot.Forward.Z);
                while (err > MathF.PI) err -= 2 * MathF.PI;
                while (err < -MathF.PI) err += 2 * MathF.PI;
                float dist = new Vector2(to.X, to.Z).Length();
                float want = stand ? (dist > 45 ? 8 : 0) : taurus.Vehicle!.Speed + System.Math.Clamp(dist * 0.5f, -15f, 20f);
                input.Buttons = CarButtons.Analog | (stand && dist < 60 && MathF.Abs(err) < 0.05f ? CarButtons.Fire : 0);
                input.Steer = (short)(System.Math.Clamp(-err * 1.5f, -1f, 1f) * 32767);
                input.Throttle = (short)(System.Math.Clamp((want - player.Speed) * 0.3f, -1f, 1f) * 32767);
            }
            s.Tick([input]);
            if (orderedAt < 0 && setup.Entities.Any(e => e.Name.StartsWith("t01") && e != taurus && e.OrderTarget == diner)) orderedAt = h.Now;
            shots += setup.Combat.Events.OfType<ShotFired>().Count(e => h.Of(e.Shooter) is { } a && a.Name.StartsWith("t01") && a != taurus && a.OrderTarget == diner);
            setup.Combat.Events.Clear();
            setup.Damage.Events.Clear();
        }
        return (orderedAt, shots, hp0 - HitPoints(diner), fireworks.Dead, setup.Script.Vm!.Data[29]);
    }

    /// <summary>
    /// T06 at night: the Creepers sent to the Paradise gas station (data[72], as the script's own trigger sets it) shoot it.
    /// Whether a given run brings them round to face it is the tactics' dice (in some seeds they stand off at 90 m facing
    /// away), so three seeds are run and two must hit it.
    /// </summary>
    [Fact]
    public void T06sCreepersShootTheGasStation()
    {
        if (InstallDir is null) return;
        int hits = 0;
        var log = new List<string>();
        foreach (uint seed in (uint[])[1, 2, 3])
        {
            var (setup, s) = Mission("miss16", "t06.msn", seed);
            var station = setup.Entities.First(e => e.Name == "bfparad2");
            int hp0 = HitPoints(station);
            Run(setup, s, 15, "t06");
            setup.Script!.Vm!.Data[72] = 1;
            int shots = Run(setup, s, 70, "t06");
            Assert.True(shots >= 10, $"seed {seed}: {shots} shots");
            if (HitPoints(station) < hp0) hits++;
            log.Add($"seed {seed}: station {hp0} -> {HitPoints(station)}");
        }
        Assert.True(hits >= 2, string.Join("; ", log));
    }

    /// <summary>
    /// T06 played: the player's car reaches the Paradise gas station (nav 3) after the intro and the script's own trigger
    /// sets data[72], sending enemy1 and enemy2 (t06cc03, t06cr01) at it. Their threads (@3178, @3313) hold
    /// `attack(enemy, petrol)` until `isAttacked`; only then `nearestEnemy` for 60 s, and back to the station. Measured in
    /// the game (the player parked at nav 3 on frame 1200): every fire decision of the two is at
    /// the station, their threads never leave the attack loop, FP1_BLD2 is at 6120 of 12000 30 s after the order and the
    /// station dies at 41 s. Here it dies at 34-42 s (seeds 1-4); before 480f82e they aimed at its placement and it lost
    /// 225 in 40 s. A round from the player turns them on him for 60 s.
    /// </summary>
    [Fact]
    public void T06sCreepersStayOnTheStationUntilShot()
    {
        if (InstallDir is null) return;
        var (setup, s) = Mission("miss16", "t06.msn");
        var h = setup.Script!.Host;
        var station = setup.Entities.First(e => e.Name == "bfparad2");
        var p = setup.World.Player!;
        var player = setup.Entities.First(e => e.Vehicle == p);
        Run(setup, s, 40, "t06");
        p.Pos = new Double3(9150, setup.Ground.BareHeight(9150, 44630), 44630);
        p.Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        p.Vel = default;
        Run(setup, s, 50, "t06");
        Assert.Equal(1, setup.Script.Vm!.Data[72]);
        // enemy2 attacks from where it is put; enemy1 drives to its nav point first (about 20 s, as in the game)
        var creepers = setup.Entities.Where(e => e.Name.StartsWith("t06c") && e.Vehicle is not null).ToList();
        int hp0 = HitPoints(station);
        bool turned = false;
        // up to 30 s, or as soon as both are on the station, 20 rounds fired and 3000 off it: a seed whose tactics shoot
        // it down sooner must not bring it down before the player's round
        float t0 = h.Now;
        int shots = 0;
        List<Entity> attackers;
        do
        {
            shots += Run(setup, s, h.Now + 1, "t06c", () => turned |= creepers.Any(c => c.OrderTarget == player));
            attackers = creepers.Where(c => c.OrderTarget == station).ToList();
        }
        while (h.Now < t0 + 30 && !(attackers.Count == 2 && shots >= 20 && hp0 - HitPoints(station) >= 3000));
        Assert.False(turned, "a creeper turned on the player unprovoked");
        Assert.Equal(2, attackers.Count);
        Assert.True(shots >= 20 && hp0 - HitPoints(station) >= 3000, $"{shots} shots, station {hp0} -> {HitPoints(station)}");

        // a round from the player on each: isAttacked, nearestEnemy (the player) for 60 s, then the station again
        float hit = h.Now;
        foreach (var c in attackers)
        {
            var pk = new DamagePacket { Attacker = p, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
            pk.Ch[1] = 1;
            setup.Damage.Apply(c.Vehicle!, pk, Vector3.Zero);
        }
        Run(setup, s, hit + 1, "t06c");
        Assert.All(attackers, c => Assert.Same(player, c.OrderTarget));
        var back = attackers.ToDictionary(c => c, _ => -1f);
        Run(setup, s, hit + 70, "t06c", () =>
        {
            foreach (var c in attackers) if (back[c] < 0 && c.OrderTarget == station) back[c] = h.Now - hit;
        });
        Assert.All(back.Values, b => Assert.True(b is > 59 and < 62, $"back on the station after {b:0.0} s"));
    }

    /// <summary>T08: with the player at Fletcher &amp; Sons, its attackers turn their guns on the warehouse between rounds on the player.</summary>
    [Fact]
    public void T08sAttackersShootTheWarehouse()
    {
        if (InstallDir is null) return;
        var (setup, s) = Mission("miss16", "t08.msn");
        var warehouse = setup.Entities.First(e => e.Name == "bcywhse1");
        int hp0 = HitPoints(warehouse);
        Run(setup, s, 15, "t08");
        var p = setup.World.Player!;
        p.Pos = new Double3(1150, setup.Ground.BareHeight(1150, 47700), 47700);
        p.Rot = new Rows(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        p.Vel = default;
        Run(setup, s, 75, "t08");
        Assert.True(HitPoints(warehouse) < hp0, $"warehouse {hp0} -> {HitPoints(warehouse)}");
    }
}
