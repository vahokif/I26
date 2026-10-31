using System.Numerics;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

public class CombatTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private sealed class Sink : IDamageSink
    {
        public readonly List<(object Victim, DamagePacket P, Vector3 Local)> Hits = [];
        public void Apply(object victim, DamagePacket p, Vector3 hitLocal) => Hits.Add((victim, p, hitLocal));
    }

    /// <summary>
    /// FireRite (rate 4.5, burst 3, delay 1.0), measured in the original game: a one-frame tap fires three
    /// rockets 0.22 s apart, the salvo finishing with the key up, then 1 s of
    /// nothing; a hold repeats salvos.
    /// </summary>
    [Fact]
    public void AFireRiteTapFiresOneThreeRocketSalvo()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = VehicleFactory.Create(vfs, "vppt01.vcf");
        world.Vehicles.Add(car);
        world.Player = car;
        var a = car.Armory!;
        int fr = a.Weapons.FindIndex(w => w.Def.Kind == 2);
        Assert.True(fr >= 0, string.Join(", ", a.Weapons.Select(w => $"{w.Def.Name} kind {w.Def.Kind}")));
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = new Sink() };
        var shots = new List<int>();
        int ammo0 = a.Weapons[fr].Ammo;
        for (int f = 0; f < 80; f++)
        {
            var keys = new bool[5]; keys[fr] = f == 0;
            a.PlayerTriggers(false, keys);
            combat.Frame(0.036f);
            int n = combat.Events.OfType<ShotFired>().Count(e => e.Weapon == a.Weapons[fr]);
            for (int k = 0; k < n; k++) shots.Add(f);
            combat.Events.Clear();
        }
        Assert.Equal(3, ammo0 - a.Weapons[fr].Ammo);
        Assert.Equal(3, shots.Count);
        // 0.222 s apart: six or seven 36 ms ticks (the game, at its own frame times, measured 6 and 6)
        Assert.Equal(0, shots[0]);
        Assert.InRange(shots[1] - shots[0], 6, 7);
        Assert.InRange(shots[2] - shots[1], 6, 7);
    }

    [Fact]
    public void AnMgHitsTheCarAheadAtItsRate()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        // the Piranha's roof gun sits 9 cm over an identical car's COLP box; the original tips it down at the target
        target.Pos = new Double3(0, 0, 40);
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        world.Player = shooter;
        Assert.NotNull(shooter.Armory);
        var a = shooter.Armory!;
        int mg = a.Weapons.FindIndex(w => w.Def.Kind == 1 && w.MountKind == 1);
        Assert.True(mg >= 0, string.Join(", ", a.Weapons.Select(w => $"{w.Def.Name} kind {w.Def.Kind} mount {w.MountKind}")));
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink };
        int ammo0 = a.Weapons[mg].Ammo;
        var keys = new bool[5]; keys[mg] = true;
        for (int f = 0; f < 100; f++)    // 3.6 s
        {
            a.PlayerTriggers(false, keys);
            combat.Frame(0.036f);
        }
        int shots = ammo0 - a.Weapons[mg].Ammo;
        var w0 = a.Weapons[mg];
        var dbg = $"box y {string.Join(",", target.BoxY)} z {string.Join(",", target.BoxZ)} x {string.Join(",", target.BoxX)}; muzzle {w0.MuzzlePos} fwd {w0.MuzzleRot.Forward} style {w0.Style}; events: " + string.Join("; ", combat.Events.OfType<Impact>().Take(5).Select(i => $"{i.Victim} at {i.At}"));
        float rate = a.Weapons[mg].Def.Rate;
        Assert.InRange(shots, 20, (int)(3.6f * rate) + 1);   // the timer loop's frame-rate coupling fires slower than the rate
        var onTarget = sink.Hits.Where(h => h.Victim == target).ToList();
        Assert.True(onTarget.Count > shots / 2, $"{onTarget.Count} hits of {shots} shots; {dbg}");
        Assert.All(onTarget, h => Assert.Equal((ushort)1, h.P.Mask));
        Assert.All(onTarget, h => Assert.Equal((int)a.Weapons[mg].Def.Damage, h.P.Ch[0]));
        Assert.All(onTarget, h => Assert.True(h.Local.Z < 0, "hits land on the rear of the car ahead"));
        Assert.DoesNotContain(sink.Hits, h => h.Victim == shooter);
    }

    /// <summary>A dropped mine settles, then throws the car that drives onto it and is gone.</summary>
    [Fact]
    public void AMineSettlesAndThrowsTheCarThatFindsIt()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var layer = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        victim.Pos = new Double3(0, 0, -40);
        world.Vehicles.Add(layer); world.Vehicles.Add(victim);
        world.Player = layer;
        var a = layer.Armory!;
        int mine = a.Weapons.FindIndex(w => w.Def.Kind == 0xf);
        Assert.True(mine >= 0);
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink };
        var keys = new bool[5]; keys[mine] = true;
        a.PlayerTriggers(false, keys);
        combat.Frame(0.036f);
        var round = Assert.Single(combat.Rounds);
        for (int f = 0; f < 100; f++) combat.Frame(0.036f);
        Assert.True(round.Settled, $"settled: pos {round.Pos} vel {round.Vel}");
        Assert.InRange(round.Pos.Y, -0.01f, 0.01f);
        Assert.True(round.Pos.Z < -2f, $"dropped behind: {round.Pos}");
        // the other car rolls onto it
        victim.Pos = new Double3(round.Pos.X, 0, round.Pos.Z - 0.5);
        victim.Vel = new System.Numerics.Vector3(0, 0, 10); victim.Speed = 10;
        combat.Frame(0.036f);
        Assert.Empty(combat.Rounds);
        Assert.Contains(sink.Hits, h => h.Victim == victim);
        Assert.True(victim.Vel.Y >= 8f, $"thrown: vel {victim.Vel}");
        Assert.True((victim.Flags & VehicleFlags.Airborne) != 0);
        Assert.InRange(victim.Vel.Z, 7.4f, 7.6f);   // ×0.75 above 2 u/s
    }

    [Fact]
    public void TheHandgunsFireOutOfTheirWindowsAtThreeAShot()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(car);
        world.Player = car;
        var a = car.Armory!;
        a.AddHandguns(new Bwd2File(vfs.TryRead("vppirnha.vdf")!), true, true);
        var right = a.Handguns[0]!;
        var left = a.Handguns[1]!;
        // GRPF / GLPF: right of the car and left of it, pointing out of the windows
        Assert.True(right.MuzzlePos.X > 0.3f && left.MuzzlePos.X < -0.6f, $"{right.MuzzlePos} {left.MuzzlePos}");
        Assert.Equal((8, 0x10), (right.MountKind, left.MountKind));   // Type-6 hardpoints facing 3 / 4: the side arcs
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var shot = new List<int>();
        combat.HandgunShot = (v, ch) => shot.Add(ch);
        for (int f = 0; f < 28; f++)    // ~1 s
        {
            right.Trigger = true;
            combat.Frame(0.036f);
        }
        Assert.InRange(shot.Count, 2, 4);   // 3 shots/s
        Assert.All(shot, ch => Assert.Equal(1, ch));
        Assert.Equal(0x0fffffff - shot.Count, right.Ammo);
    }

    private static (SimWorld World, Vehicle Car, Combat Combat, Sink Sink, AssetVfs Vfs) Rig(string vcf, uint seed = 1)
    {
        var vfs = new AssetVfs(new GameInstall(InstallDir!));
        var world = new SimWorld(new Flat(), new CrtRand(seed));
        var car = VehicleFactory.Create(vfs, vcf);
        world.Vehicles.Add(car);
        world.Player = car;
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink };
        return (world, car, combat, sink, vfs);
    }

    /// <summary>
    /// The original goes on with the rest of the frame after a shot (no reset):
    /// a 20/s gun at the 36 ms tick fires two frames in three, as the game did
    /// in the original game (the Piranha's 7.62 at 28 fps: 1 1 0 1 1 0 ...).
    /// </summary>
    [Fact]
    public void ATwentyAShotGunFiresTwoTicksInThree()
    {
        if (InstallDir is null) return;
        var (_, car, combat, _, _) = Rig("vppirna2.vcf");
        var a = car.Armory!;
        int mg = a.Weapons.FindIndex(w => w.Def.Kind == 1 && w.Def.Rate == 20);
        Assert.True(mg >= 0);
        var keys = new bool[5]; keys[mg] = true;
        var pattern = "";
        for (int f = 0; f < 12; f++)
        {
            a.PlayerTriggers(false, keys);
            combat.Frame(0.036f);
            pattern += combat.Events.OfType<ShotFired>().Count(e => e.Weapon == a.Weapons[mg]);
            combat.Events.Clear();
        }
        Assert.Equal("110110110110", pattern);
    }

    /// <summary>
    /// The napalm hose's stream: a shot starts it
    /// at two cross-sections and adds one; a frame without a shot takes two off.
    /// Deviation: a tick with the trigger held counts as a shot for the stream
    /// even when the timing loop fires none (at the 36 ms tick it fires two
    /// frames in three), so the stream grows every tick as at 20 fps or below;
    /// the ammo follows the timing loop.
    /// </summary>
    [Theory]
    [InlineData(0.036f, "2345678910", 6)]
    [InlineData(0.055f, "234567891011121314151617181919", 19)]
    public void TheNapalmStreamGrowsByTheTickHeldAndShrinksByTheFrame(float dt, string expected, int shots)
    {
        if (InstallDir is null) return;
        var (_, car, combat, _, _) = Rig("vppirna2.vcf");
        var a = car.Armory!;
        int hose = a.Weapons.FindIndex(w => w.Def.Kind == 11);
        Assert.True(hose >= 0);
        var keys = new bool[5]; keys[hose] = true;
        var seen = "";
        for (int f = 0; f < (dt < 0.04f ? 9 : 19); f++)
        {
            a.PlayerTriggers(false, keys);
            combat.Frame(dt);
            seen += combat.Flames.FirstOrDefault() is { } fl ? fl.Count.ToString() : "-";
        }
        Assert.Equal(expected, seen);
        Assert.Equal(600 - shots, a.Weapons[hose].Ammo);
    }

    /// <summary>Held for a second at the 36 ms tick the hose's stream is 19 cross-sections; released, it shrinks away two a tick.</summary>
    [Fact]
    public void TheNapalmStreamReachesNineteenAndShrinksAwayOnRelease()
    {
        if (InstallDir is null) return;
        var (_, car, combat, _, _) = Rig("vppirna2.vcf");
        var a = car.Armory!;
        int hose = a.Weapons.FindIndex(w => w.Def.Kind == 11);
        var keys = new bool[5]; keys[hose] = true;
        for (int f = 0; f < 28; f++) { a.PlayerTriggers(false, keys); combat.Frame(0.036f); }
        Assert.Equal(19, combat.Flames.Single().Count);
        var none = new bool[5];
        int ticks = 0;
        while (combat.Flames.Count > 0 && ticks < 30) { a.PlayerTriggers(false, none); combat.Frame(0.036f); ticks++; }
        Assert.Empty(combat.Flames);
        Assert.InRange(ticks, 8, 10);
    }

    /// <summary>
    /// The original's burn: each stream point from the second that is in a
    /// car's sphere (+1) and whose segment crosses its box gives a fire packet
    /// (mask 4) of max(1, ftol(45 · dt)): 1 at the 36 ms tick, 2 at 18 fps,
    /// one per point per frame. The cross-sections are a metre apart along the
    /// muzzle's forward (the hose points back) and the stream lies flat from the
    /// first point that touches.
    /// </summary>
    [Fact]
    public void TheNapalmStreamBurnsWhatItTouchesEveryFrame()
    {
        if (InstallDir is null) return;
        var (world, car, combat, sink, vfs) = Rig("vppirna2.vcf");
        var a = car.Armory!;
        int hose = a.Weapons.FindIndex(w => w.Def.Kind == 11);
        var w = a.Weapons[hose];
        var fwd = Rows.Concat(w.MuzzleRot, car.Rot).Forward;
        Assert.True(fwd.Z < -0.9f, $"the hose points back: {fwd}");
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var muzzle = car.Pos.ToVector3() + car.Rot.ToWorld(w.MuzzlePos);
        victim.Pos = new Double3(muzzle.X, 0, muzzle.Z - 10);
        world.Vehicles.Add(victim);
        var keys = new bool[5]; keys[hose] = true;
        for (int f = 0; f < 25; f++) { a.PlayerTriggers(false, keys); combat.Frame(0.055f); }
        var fl = Assert.Single(combat.Flames);
        Assert.Equal(19, fl.Count);
        var burns = sink.Hits.Where(h => h.Victim == victim).ToList();
        Assert.NotEmpty(burns);
        Assert.All(burns, h => { Assert.Equal((ushort)4, h.P.Mask); Assert.Equal(2, h.P.Ch[2]); Assert.Equal(car, h.P.Attacker); });
        Assert.DoesNotContain(sink.Hits, h => h.Victim == car);
        Assert.True(fl.FirstHit > 0, "the stream met the car");
        // the points before the car are a metre apart, then an eighth of a metre on its surface
        var p1 = fl.Cross[0] + fl.Cross[1] + fl.Cross[2];
        var p2 = fl.Cross[3] + fl.Cross[4] + fl.Cross[5];
        Assert.InRange(System.Numerics.Vector3.Distance(p1 / 3, p2 / 3), 0.95f, 1.05f);
        // one frame's burn: one packet per touching point
        sink.Hits.Clear();
        a.PlayerTriggers(false, keys); combat.Frame(0.055f);
        int n = sink.Hits.Count(h => h.Victim == victim);
        Assert.InRange(n, 1, 18);
        Assert.Contains(combat.Events.OfType<Explosion>(), e => e.Effect.File.Equals("xflht1.xdf", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The oil slick (rate 60): standing still the dribble is a drop a second
    /// (B = 1 + (1/60 − 1)·0.05·v), at 20 u/s and up two per 36 ms tick; each
    /// lands behind the car on the ground and lies there for its 20 s life.
    /// </summary>
    [Fact]
    public void TheOilSlickDribblesByTheSpeedAndLiesBehind()
    {
        if (InstallDir is null) return;
        var (_, car, combat, _, _) = Rig("vppirna3.vcf");
        var a = car.Armory!;
        int oil = a.Weapons.FindIndex(w => w.Def.Kind == 0xc);
        Assert.True(oil >= 0, string.Join(", ", a.Weapons.Select(w => $"{w.Def.Name} kind {w.Def.Kind}")));
        var keys = new bool[5]; keys[oil] = true;
        var frames = new List<int>();
        for (int f = 0; f < 60; f++)
        {
            a.PlayerTriggers(false, keys);
            int before = combat.Rounds.Count;
            combat.Frame(0.036f);
            if (combat.Rounds.Count > before) frames.Add(f);
        }
        Assert.Equal([0, 28], frames.Take(2));
        Assert.All(combat.Rounds, r => { Assert.Equal(0.0001f, r.Pos.Y, 4); Assert.True(r.Pos.Z < -1.5f, $"behind: {r.Pos}"); });
        Assert.Equal(20f, combat.Rounds[0].Def.Life);
        // at speed (a fresh car: the standing dribble's 1 s wait would still run)
        var (_, car2, combat2, _, _) = Rig("vppirna3.vcf");
        var a2 = car2.Armory!;
        car2.Speed = 25; car2.Vel = new System.Numerics.Vector3(0, 0, 25);
        for (int f = 0; f < 10; f++) { a2.PlayerTriggers(false, keys); combat2.Frame(0.036f); }
        Assert.Equal(20, combat2.Rounds.Count);
    }

    /// <summary>
    /// A car over a slick (a 1 m probe up from it hits its box)
    /// gets the slick's effect, 2 s on the shared timer with flags 0x80000400, every
    /// frame it is there; the dropper itself only after 2 s; no damage.
    /// </summary>
    [Fact]
    public void ACarOverTheSlickLosesItsGrip()
    {
        if (InstallDir is null) return;
        var (world, car, combat, sink, vfs) = Rig("vppirna3.vcf");
        var a = car.Armory!;
        int oil = a.Weapons.FindIndex(w => w.Def.Kind == 0xc);
        var keys = new bool[5]; keys[oil] = true;
        a.PlayerTriggers(false, keys);
        combat.Frame(0.036f);
        var slick = Assert.Single(combat.Rounds);
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        victim.Pos = new Double3(slick.Pos.X, 0, slick.Pos.Z);
        world.Vehicles.Add(victim);
        combat.Frame(0.036f);
        Assert.Equal(2f, victim.Timer);
        Assert.Equal(0x80000400u, (uint)victim.Flags & 0x80000400u);
        Assert.Equal(0x400u, (uint)car.Flags & 0x400u ^ 0x400u);   // the dropper is not over it
        Assert.Contains(combat.Events.OfType<Impact>(), i => i.Victim == victim && i.Sound.Equals("vcdoil.wav", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sink.Hits, h => h.Victim == victim);
        Assert.Single(combat.Rounds);
        // the shared timer runs it out and takes the flag off
        var damage = new I76.Sim.Damage.DamageModel(world, combat);
        world.Vehicles.Remove(victim);
        for (int i = 0; i < 56; i++) damage.Timer(victim, 0.036f);
        Assert.Equal(0u, (uint)victim.Flags & 0x80000400u);
    }

    /// <summary>AI fire class 6: drop when the target behind is about to drive over the shooter's spot.</summary>
    [Fact]
    public void TheAiDropsOnACarAboutToDriveOverItsSpot()
    {
        if (InstallDir is null) return;
        var (world, car, combat, _, vfs) = Rig("vppirna3.vcf");
        var oil = car.Armory!.Weapons.First(w => w.Def.Kind == 0xc);
        var chaser = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(chaser);
        car.Vel = new System.Numerics.Vector3(0, 0, 20); car.Speed = 20;
        chaser.Pos = new Double3(0, 0, -30);
        chaser.Vel = new System.Numerics.Vector3(0, 0, 25); chaser.Speed = 25;
        Assert.True(combat.AiShouldFire(car, chaser, oil, 1, 1));
        chaser.Pos = new Double3(5, 0, -30);                       // passes 5 m to the side
        Assert.False(combat.AiShouldFire(car, chaser, oil, 1, 1));
        chaser.Pos = new Double3(0, 0, 30);                        // ahead
        Assert.False(combat.AiShouldFire(car, chaser, oil, 1, 1));
        chaser.Pos = new Double3(0, 0, -200);                      // 8 s away
        Assert.False(combat.AiShouldFire(car, chaser, oil, 1, 1));
    }

    /// <summary>An Aim-Nein (gsheat.gdf, kind 3) on a fixed front mount, alone in the shooter's armoury, and a target ahead.</summary>
    private static (Combat C, Sink S, Vehicle Shooter, Vehicle Target, Weapon W) AimNein(AssetVfs vfs)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        var def = GizmoDef.Read("gsheat.gdf", vfs.Read("gsheat.gdf"))!;
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("gsheat", 1, 1, 1, new Transform([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 1, 2])),
            MountKind = 1, MuzzlePos = new(0, 1, 2), BaseRot = Rows.Identity, Hp = 10, MaxHp = 10, Damage = (int)def.Damage, Ammo = 10,
        };
        shooter.Armory!.Weapons.Clear();
        shooter.Armory.Weapons.Add(w);
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink };
        return (combat, sink, shooter, target, w);
    }

    private static void Drive(Vehicle v, float heading, float dt)
    {
        var f = new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading));
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        v.Vel = f * 15; v.Speed = 15;
        v.Pos = new Double3(v.Pos.X + f.X * 15 * dt, v.Pos.Y, v.Pos.Z + f.Z * 15 * dt);
    }

    /// <summary>
    /// The Aim-Nein's heat seeking (at launch and in flight): it locks a car
    /// driving away within 20 degrees of its own heading, snaps its pose onto the car's collider
    /// centre after every step, follows it as it weaves, and hits it.
    /// </summary>
    [Fact]
    public void AnAimNeinFollowsAWeavingCarAndHitsIt()
    {
        if (InstallDir is null) return;
        var (combat, sink, shooter, target, w) = AimNein(new AssetVfs(new GameInstall(InstallDir)));
        target.Pos = new Double3(4, 0, 60);   // off to one side, beyond the launch's straight line
        const float dt = 0.036f;
        float h = 0.1f;
        bool locked = false;
        int t;
        for (t = 0; t < 70 && sink.Hits.Count == 0; t++)
        {
            h += 0.4f * dt * MathF.Sin(t * 0.15f);   // weaving, within the lock's 20 degrees
            Drive(target, h, dt);
            if (t == 0) w.Trigger = true;
            combat.Frame(dt);
            if (combat.Rounds.FirstOrDefault(r => r.Kind == 3) is { } m && m.Target == target)
            {
                locked = true;
                var to = Vector3.Normalize(Combat.Collider(target).Centre - m.Pos);
                Assert.True(Vector3.Dot(to, m.Rot.Forward) > 0.999f, $"t {t}: the missile looks {m.Rot.Forward}, the car is along {to}");
            }
        }
        Assert.True(locked);
        Assert.Contains(sink.Hits, x => x.Victim == target);
    }

    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>A DrRadar (gsradar.gdf, kind 8) on a fixed front mount, alone in the shooter's armoury.</summary>
    private static (Combat C, Sink S, Vehicle Shooter, Vehicle Target, Weapon W) DrRadar(AssetVfs vfs)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        world.Player = shooter;
        var def = GizmoDef.Read("gsradar.gdf", vfs.Read("gsradar.gdf"))!;
        Assert.Equal(8u, def.Kind);
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("gsradar", 1, 1, 1, new Transform([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 1, 2])),
            MountKind = 1, MuzzlePos = new(0, 1, 2), BaseRot = Rows.Identity, Hp = 10, MaxHp = 10, Damage = (int)def.Damage, Ammo = 10,
        };
        shooter.Armory!.Weapons.Clear();
        shooter.Armory.Weapons.Add(w);
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink };
        return (combat, sink, shooter, target, w);
    }

    /// <summary>
    /// A DrRadar fired at the radar's target, a car crossing 80 m ahead, turns after it and hits it at
    /// 20 and 28 Hz, in the base game's data and Nitro's (only the pose turns, the
    /// velocity stays in the round's frame, so the missile flies the new heading). It used to keep its world velocity
    /// through the turn and fly straight on.
    /// </summary>
    [Theory]
    [InlineData(false, 102f / 2048f)]
    [InlineData(false, 0.036f)]
    [InlineData(true, 102f / 2048f)]
    [InlineData(true, 0.036f)]
    public void ADrRadarHomesOnTheRadarTarget(bool nitro, float dt)
    {
        var dir = nitro ? NitroDir : InstallDir;
        if (dir is null) return;
        var (combat, sink, shooter, target, w) = DrRadar(new AssetVfs(new GameInstall(dir)));
        target.Pos = new Double3(-25, 0, 80);
        target.Flags |= VehicleFlags.EngineOn;
        combat.RadarTarget[shooter] = target;
        float closest = float.MaxValue, lastCos = -1;
        Round? m = null;
        int t;
        for (t = 0; t < 150 && sink.Hits.Count == 0; t++)
        {
            Drive(target, MathF.PI / 2, dt);   // crossing left to right at 15 m/s
            if (t == 0) w.Trigger = true;
            combat.Frame(dt);
            if (t == 0) { m = Assert.Single(combat.Rounds, r => r.Kind == 8); Assert.Same(target, m.Target); }
            if (m is not null && combat.Rounds.Contains(m))
            {
                var to = Combat.Collider(target).Centre - m.Pos;
                closest = MathF.Min(closest, to.Length());
                lastCos = Vector3.Dot(Vector3.Normalize(to), Vector3.Normalize(m.Rot.ToWorld(m.LocalVel)));
            }
        }
        Assert.True(sink.Hits.Any(x => x.Victim == target), $"no hit in {t} ticks: closest {closest:F1} m, last cos {lastCos:F3}");
    }

    /// <summary>
    /// A DrRadar measured in the original game (a01, vppt15.vcf,
    /// fired ahead at a radar target behind and to the left): per frame the frame time, the aim point (the target's
    /// collider centre), then the position and forward after the step, the forward speed and the distance flown.
    /// Positions are relative to (1500, 0, 49600).
    /// </summary>
    private static readonly (float K, float Ax, float Ay, float Az, float Px, float Py, float Pz, float Fx, float Fy, float Fz, float V, float Flown)[] DrRadarA01 =
    [
        (0.0356f, 30.57f, 26.78f, 26.06f, 96.35f, 27.11f, 2.30f, 0.9979f, 0.0453f, 0.0467f, 100.90f, 3.60f),
        (0.0352f, 30.59f, 26.78f, 26.23f, 99.91f, 27.29f, 2.65f, 0.9939f, 0.0502f, 0.0983f, 101.74f, 7.17f),
        (0.0361f, 30.60f, 26.78f, 26.41f, 103.57f, 27.50f, 3.20f, 0.9872f, 0.0550f, 0.1497f, 102.58f, 10.88f),
        (0.0356f, 30.62f, 26.78f, 26.59f, 107.17f, 27.72f, 3.95f, 0.9777f, 0.0596f, 0.2014f, 103.38f, 14.56f),
        (0.0352f, 30.63f, 26.78f, 26.77f, 110.70f, 27.95f, 4.87f, 0.9657f, 0.0640f, 0.2518f, 104.15f, 18.23f),
        (0.0361f, 30.65f, 26.78f, 26.95f, 113.99f, 28.26f, 6.72f, 0.8680f, 0.0829f, 0.4896f, 104.90f, 22.02f),
        (0.0361f, 30.66f, 26.78f, 27.13f, 116.71f, 28.63f, 9.38f, 0.7125f, 0.0962f, 0.6950f, 105.63f, 25.83f),
        (0.0356f, 30.68f, 26.78f, 27.30f, 118.64f, 29.02f, 12.61f, 0.5094f, 0.1030f, 0.8543f, 106.32f, 29.62f),
        (0.0352f, 30.70f, 26.78f, 27.48f, 119.67f, 29.41f, 16.21f, 0.2725f, 0.1029f, 0.9566f, 106.98f, 33.38f),
        (0.0361f, 30.72f, 26.78f, 27.66f, 119.74f, 29.78f, 20.08f, 0.0174f, 0.0959f, 0.9952f, 107.63f, 37.27f),
        (0.0356f, 30.74f, 26.78f, 27.83f, 118.82f, 30.10f, 23.82f, -0.2387f, 0.0825f, 0.9676f, 108.25f, 41.13f),
        (0.0352f, 30.76f, 26.78f, 28.01f, 116.98f, 30.34f, 27.17f, -0.4789f, 0.0635f, 0.8756f, 108.84f, 44.96f),
        (0.0410f, 30.78f, 26.78f, 28.21f, 113.89f, 30.52f, 30.43f, -0.6873f, 0.0402f, 0.7253f, 109.51f, 49.45f),
        (0.0381f, 30.80f, 26.78f, 28.40f, 110.33f, 30.58f, 32.63f, -0.8501f, 0.0142f, 0.5264f, 110.10f, 53.64f),
        (0.0317f, 30.82f, 26.78f, 28.55f, 106.97f, 30.54f, 33.66f, -0.9561f, -0.0128f, 0.2927f, 110.57f, 57.15f),
        (0.0342f, 30.84f, 26.78f, 28.73f, 103.18f, 30.39f, 33.81f, -0.9985f, -0.0392f, 0.0391f, 111.07f, 60.95f),
        (0.0371f, 30.86f, 26.78f, 28.91f, 99.05f, 30.19f, 33.64f, -0.9981f, -0.0471f, -0.0406f, 111.59f, 65.09f),
        (0.0361f, 30.89f, 26.79f, 29.09f, 95.01f, 30.00f, 33.48f, -0.9981f, -0.0471f, -0.0405f, 112.08f, 69.14f),
        (0.0386f, 30.92f, 26.79f, 29.27f, 90.68f, 29.80f, 33.30f, -0.9981f, -0.0471f, -0.0405f, 112.58f, 73.48f),
        (0.0293f, 30.94f, 26.79f, 29.42f, 87.37f, 29.64f, 33.17f, -0.9981f, -0.0471f, -0.0401f, 112.94f, 76.79f),
        (0.0361f, 30.98f, 26.79f, 29.59f, 83.28f, 29.45f, 33.00f, -0.9981f, -0.0472f, -0.0400f, 113.38f, 80.89f),
        (0.0356f, 31.01f, 26.79f, 29.77f, 79.24f, 29.26f, 32.84f, -0.9981f, -0.0472f, -0.0404f, 113.79f, 84.94f),
        (0.0352f, 31.04f, 26.79f, 29.94f, 75.23f, 29.07f, 32.68f, -0.9981f, -0.0472f, -0.0409f, 114.19f, 88.96f),
        (0.0361f, 31.08f, 26.79f, 30.12f, 71.10f, 28.87f, 32.51f, -0.9981f, -0.0472f, -0.0406f, 114.58f, 93.10f),
        (0.0356f, 31.11f, 26.79f, 30.29f, 67.01f, 28.68f, 32.34f, -0.9981f, -0.0472f, -0.0410f, 114.95f, 97.20f),
        (0.0352f, 31.15f, 26.78f, 30.46f, 62.96f, 28.49f, 32.17f, -0.9980f, -0.0472f, -0.0414f, 115.31f, 101.25f),
        (0.0361f, 31.18f, 26.78f, 30.64f, 58.79f, 28.29f, 32.00f, -0.9980f, -0.0472f, -0.0411f, 115.66f, 105.43f),
        (0.0381f, 31.22f, 26.78f, 30.83f, 54.38f, 28.08f, 31.82f, -0.9981f, -0.0472f, -0.0407f, 116.02f, 109.85f),
        (0.0337f, 31.26f, 26.78f, 30.99f, 50.47f, 27.90f, 31.66f, -0.9981f, -0.0473f, -0.0409f, 116.32f, 113.77f),
        (0.0352f, 31.29f, 26.78f, 31.16f, 46.38f, 27.70f, 31.49f, -0.9981f, -0.0473f, -0.0410f, 116.63f, 117.87f),
        (0.0361f, 31.33f, 26.78f, 31.34f, 42.16f, 27.50f, 31.32f, -0.9981f, -0.0473f, -0.0407f, 116.93f, 122.09f),
        (0.0356f, 31.37f, 26.78f, 31.52f, 37.99f, 27.31f, 31.15f, -0.9981f, -0.0473f, -0.0402f, 117.22f, 126.27f),
        (0.0352f, 31.40f, 26.78f, 31.68f, 33.88f, 27.01f, 31.36f, -0.9962f, -0.0710f, 0.0501f, 117.50f, 130.40f),
        (0.0361f, 31.44f, 26.78f, 31.86f, 32.03f, 26.85f, 31.66f, -0.9829f, -0.0868f, 0.1627f, 0.00f, 132.27f),
    ];

    /// <summary>
    /// The measured flight replayed: from the state after the first frame, each frame's step (with the game's frame
    /// time, the target where the game had it) turns and moves the missile as the game did: 3° a frame under 15 m
    /// flown, 15° beyond, straight on once within 3.6° of the target, the velocity along the new heading.
    /// </summary>
    [Fact]
    public void ADrRadarTurnsAsMeasuredInTheGame()
    {
        if (InstallDir is null) return;
        var (combat, _, shooter, target, w) = DrRadar(new AssetVfs(new GameInstall(InstallDir)));
        shooter.Pos = new Double3(0, 0, -400);
        target.Flags |= VehicleFlags.EngineOn;
        var offset = Combat.Collider(target).Centre - target.Pos.ToVector3();
        var m0 = DrRadarA01[0];
        var f0 = Vector3.Normalize(new Vector3(m0.Fx, m0.Fy, m0.Fz));
        var r0 = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f0));
        var r = new Round
        {
            Kind = 8, Shooter = shooter, Def = w.Def, Ord = w.Def.Ordnance!, Pos = new(m0.Px, m0.Py, m0.Pz), Rot = new Rows(r0, Vector3.Cross(f0, r0), f0),
            LocalVel = new Vector3(0, 0, m0.V), Life = 10, Count = 1, Damage = w.Damage, Target = target, Travelled = m0.Flown,
        };
        combat.Rounds.Add(r);
        // to frame 30: frame 31 starts at cos 0.9980 to the target, on the 0.998 edge at the log's two decimals
        for (int i = 1; i <= 30; i++)
        {
            var m = DrRadarA01[i];
            var aim = new Vector3(m.Ax, m.Ay, m.Az) - offset;
            target.Pos = new Double3(aim.X, aim.Y, aim.Z);
            combat.Frame(m.K);
            Assert.Contains(r, combat.Rounds);
            var f = r.Rot.Forward;
            Assert.True(Vector3.Distance(f, new Vector3(m.Fx, m.Fy, m.Fz)) < 0.004f, $"frame {i}: forward {f}, the game's ({m.Fx}, {m.Fy}, {m.Fz})");
            Assert.True(Vector3.Distance(r.Pos, new Vector3(m.Px, m.Py, m.Pz)) < 0.08f, $"frame {i}: at {r.Pos}, the game's ({m.Px}, {m.Py}, {m.Pz})");
            Assert.Equal(m.Flown, r.Travelled, 0.1f);
        }
    }

    /// <summary>With no radar target the DrRadar has none to steer at (the original's default case) and flies straight on.</summary>
    [Fact]
    public void ADrRadarWithNoLockFliesStraight()
    {
        if (InstallDir is null) return;
        var (combat, _, shooter, target, w) = DrRadar(new AssetVfs(new GameInstall(InstallDir)));
        target.Pos = new Double3(-25, 0, 80);
        target.Flags |= VehicleFlags.EngineOn;
        w.Trigger = true;
        combat.Frame(102f / 2048f);
        var m = Assert.Single(combat.Rounds);
        Assert.Null(m.Target);
        for (int t = 0; t < 20; t++) combat.Frame(102f / 2048f);
        Assert.True(m.Rot.Forward.Z > 0.999f, $"{m.Rot.Forward}");
        Assert.True(MathF.Abs(m.Pos.X) < 1f, $"{m.Pos}");
    }

    /// <summary>The lock needs the car's heading along the missile's: a car crossing at 90 degrees is never seeked, and a dead car is cold.</summary>
    [Fact]
    public void AnAimNeinIgnoresACrossingCarAndADeadOne()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var (combat, _, _, target, w) = AimNein(vfs);
        target.Pos = new Double3(3, 0, 60);
        Drive(target, MathF.PI / 2, 0);
        w.Trigger = true;
        combat.Frame(0.036f);
        Assert.Null(Assert.Single(combat.Rounds).Target);
        Assert.True(combat.Rounds[0].Rot.Forward.Z > 0.9999f);   // flies straight on

        var (c2, _, _, t2, w2) = AimNein(vfs);
        t2.Pos = new Double3(3, 0, 60);
        Drive(t2, 0, 0);
        t2.Flags |= VehicleFlags.Dead;
        w2.Trigger = true;
        c2.Frame(0.036f);
        Assert.Null(Assert.Single(c2.Rounds).Target);
    }
}
