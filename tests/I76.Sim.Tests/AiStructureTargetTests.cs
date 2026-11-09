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
    /// diner (nav 9) has brought them, 60-300 m off. Their bearings, ranges and steering take the diner's own routine
    /// point, the centre of its bounds (its lot left out), 6 m east of its deck; taken from the placement they drove past
    /// it with their guns 30-60 degrees off and barely hit it. The script gives each attack three seconds and then 15 on
    /// the nearest enemy, so the window is the first two attacks (the order plus 25 s). Measured in the original, played
    /// the same way (the stand destroyed when the player's rounds take it here, the player's car kept 30 m behind Taurus):
    /// the order came at 297 s as here; in the first attack t01al01 fired 27-40 times from about 100 m and never hit, and
    /// in the second (19-22 s after the order) t01fc01 hit it 11 and 17 times with its MG and rockets in two seeds of
    /// three and nothing in the third. Here over seeds 1-16 they hurt it in 7, so sixteen seeds are run and six must.
    /// </summary>
    [Fact]
    public void T01sAttackersShootTheDinerWhenTaurusGetsThere()
    {
        if (InstallDir is null) return;
        int hurt = 0;
        var log = new List<string>();
        foreach (uint seed in (uint[])[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16])
        {
            var (ordered, shots, damage, fireworks, data29) = PlayT01ToTheDiner(seed);
            Assert.True(fireworks, $"seed {seed}: the player's rounds took the fireworks stand");
            Assert.True(ordered > 0, $"seed {seed}: no attack order on the diner (data[29] = {data29})");
            if (shots >= 10 && damage >= 100) hurt++;
            log.Add($"seed {seed}: ordered at {ordered:0}s, {shots} shots, diner -{damage}");
        }
        Assert.True(hurt >= 6, string.Join("; ", log));
    }

    /// <summary>
    /// Plays T01 from the start with a driver at the player's wheel until the attackers' first two rounds on the diner are
    /// over (their order on it plus 25 s): the attack order's time, the attackers' shots while ordered at the diner, the diner's
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
        while (setup.Script.Result is null && h.Now < (orderedAt < 0 ? 360 : orderedAt + 25))
        {
            var hd = player.Health!;
            Array.Copy(hd.ArmourMax, hd.Armour, 4); Array.Copy(hd.ChassisMax, hd.Chassis, 4);
            var input = new CarInput();
            if (h.Now >= 21.3f)   // the intro over
            {
                // objective 2 open: drive at the stand, stop short and fire once the guns bear; otherwise 15 m behind Taurus
                bool stand = !fireworks.Dead && (h.Objectives.Flags[1] & (Objectives.Hidden | Objectives.Succeeded)) == 0;
                var goal = stand ? fireworks.Static!.Centre : taurus.Position - taurus.Rot.Forward * 15;
                // Taurus, cornering on load-based grip, can stop right in the line to the stand: go round him
                var tt = taurus.Position - player.Pos.ToVector3();
                if (stand && tt.Length() < 15 && Vector3.Dot(tt, player.Rot.Forward) > 0)
                    goal += new Vector3(player.Rot.Forward.Z, 0, -player.Rot.Forward.X) * 12;
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

    /// <summary>
    /// The point the AI aims at on a structure is the centre of its bounds, and a lot (the paved ground a building stands
    /// on, its own node) is no part of those: the original takes it out of the placement when it loads and draws it with
    /// the ground. Measured in the original: T02's Jim Bowie school's point is (8351.5, 21.85, 49468.96), inside the
    /// building; with its 55 x 70 m lot counted it was the lot's middle, 4 m beside the building, where T02's gang put all
    /// their rounds. T01's Wagon Wheel diner's is (3602.03, 12.20, 40179.28).
    /// </summary>
    [Theory]
    [InlineData("t02.msn", "bhschol1", 8351.5f, 21.85f, 49468.96f)]
    [InlineData("t01.msn", "bdwgnwl1", 3602.03f, 12.20f, 40179.28f)]
    public void AStructuresAimPointLeavesItsLotOut(string mission, string name, float x, float y, float z)
    {
        if (InstallDir is null) return;
        var (setup, _) = Mission("miss16", mission);
        var c = setup.Entities.First(e => e.Name == name).Static!.Centre;
        Assert.True(Vector3.Distance(c, new Vector3(x, y, z)) < 0.1f, $"{name} centre {c}");
    }

    /// <summary>
    /// T02 played as far as the Leadsled gang's run at the school: the player's car follows Taurus through the race until
    /// the script sends the gang (data[68]), then waits 250 m west of the school out of their way. The three race there and
    /// are ordered at it from 150 m; within 60 s of the first order the school must be destroyed in each of four seeds (11-36 s here). A
    /// tester found their gunfire always right beside it: they aimed at its lot's middle, and it lost 0-45 in those seeds.
    /// </summary>
    [Fact]
    public void T02sGangShootsTheSchool()
    {
        if (InstallDir is null) return;
        var log = new List<string>();
        int hurt = 0;
        foreach (uint seed in (uint[])[1, 2, 3, 4])
        {
            var (setup, s) = Mission("miss16", "t02.msn", seed);
            var h = setup.Script!.Host;
            var school = setup.Entities.First(e => e.Name == "bhschol1");
            var taurus = setup.Entities.First(e => e.Name == "t03js01");
            var player = setup.World.Player!;
            int hp0 = HitPoints(school), shots = 0;
            float orderedAt = -1;
            bool parked = false; float destroyedAt = -1;
            while (setup.Script.Result is null && h.Now < (orderedAt < 0 ? 900 : orderedAt + 60))
            {
                var hd = player.Health!;
                Array.Copy(hd.ArmourMax, hd.Armour, 4); Array.Copy(hd.ChassisMax, hd.Chassis, 4);
                var input = new CarInput();
                if (setup.Script.Vm!.Data[68] == 1)
                {
                    if (!parked)
                    {
                        var at = school.Position - new Vector3(250, 0, 0);
                        player.Pos = new Double3(at.X, setup.Ground.BareHeight(at.X, at.Z), at.Z);
                        player.Vel = default;
                        parked = true;
                    }
                }
                else if (!h.View.Active)
                {
                    // 15 m behind Taurus
                    var to = taurus.Position - taurus.Rot.Forward * 15 - player.Pos.ToVector3();
                    float err = MathF.Atan2(to.X, to.Z) - MathF.Atan2(player.Rot.Forward.X, player.Rot.Forward.Z);
                    while (err > MathF.PI) err -= 2 * MathF.PI;
                    while (err < -MathF.PI) err += 2 * MathF.PI;
                    float want = taurus.Vehicle!.Speed + System.Math.Clamp(new Vector2(to.X, to.Z).Length() * 0.5f, -15f, 20f);
                    input.Buttons = CarButtons.Analog;
                    input.Steer = (short)(System.Math.Clamp(-err * 1.5f, -1f, 1f) * 32767);
                    input.Throttle = (short)(System.Math.Clamp((want - player.Speed) * 0.3f, -1f, 1f) * 32767);
                }
                s.Tick([input]);
                if (orderedAt < 0 && setup.Entities.Any(e => e.Name.StartsWith("t02f") && e.OrderTarget == school)) orderedAt = h.Now;
                shots += setup.Combat.Events.OfType<ShotFired>().Count(e => h.Of(e.Shooter) is { } a && a.OrderTarget == school);
                setup.Combat.Events.Clear();
                setup.Damage.Events.Clear();
                if (destroyedAt < 0 && school.Static!.Destroyed) destroyedAt = h.Now - orderedAt;
            }
            int loss = hp0 - HitPoints(school);
            if (orderedAt >= 0 && school.Static!.Destroyed) hurt++;
            log.Add($"seed {seed}: ordered at {orderedAt:0}s, {shots} shots, school -{loss}, destroyed {(destroyedAt >= 0 ? $"{destroyedAt:0.0} s after the order" : "no")}");
        }
        Assert.True(hurt == 4, string.Join("; ", log));
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
