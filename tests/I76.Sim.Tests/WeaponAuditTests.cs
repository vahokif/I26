using System.Numerics;
using I76.Formats.Assembly;
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

/// <summary>
/// The weapon gaps of the October 2026 audit: the cluster bomb's bomblets, the EZK
/// Mortar's elevation, Nitro's Chemical Mortar and dropper rate, the auto-cycle and link, the radar-missile warning,
/// the Ball O'Flame's impact and the network jam.
/// </summary>
public class WeaponAuditTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private const float Dt = 102 / 2048f;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private sealed class Sink : IDamageSink
    {
        public readonly List<(object Victim, DamagePacket P)> Hits = [];
        public void Apply(object victim, DamagePacket p, Vector3 hitLocal) => Hits.Add((victim, p));
    }

    private static readonly Transform Ident = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

    private static Weapon Arm(GizmoDef def, int mount = 1, int hp = 45, uint style = 1) => new()
    {
        Def = def, Hardpoint = new Hardpoint("hp", 0, 1, style, Ident), Style = style, MountKind = mount, BaseRot = Rows.Identity,
        Hp = hp, MaxHp = 45, Damage = (int)def.Damage, Ammo = (int)def.Ammo,
    };

    private static GizmoDef Def(uint cls, uint tier) => Armory.HandgunDef(1) with { Class = cls, Tier = tier, Ammo = 100 };

    private static Armory Armoury(params Weapon[] ws)
    {
        var a = new Armory();
        a.Weapons.AddRange(ws);
        a.LoadOrder.AddRange(ws);
        a.Selected = 0;
        return a;
    }

    // ---- 5. the auto-cycle and the link ----

    [Fact]
    public void ASelectedWeaponShotToNothingCyclesToTheNextLiveOne()
    {
        var a = Armoury(Arm(Def(1, 0)), Arm(Def(1, 0)), Arm(Def(4, 0)));
        Assert.Equal(0, a.Select(false, false, out bool changed));
        Assert.False(changed);
        a.Weapons[0].Hp = 0;
        Assert.Equal(1, a.Select(false, false, out changed));
        Assert.True(changed);
        Assert.Equal(1, a.Selected);
        // nothing changes the next frame: no more cammo
        Assert.Equal(0, a.Select(false, false, out changed));
        Assert.False(changed);
    }

    [Fact]
    public void TheCycleSkipsTheLinkedGroupAndTheLinkRebuildsIt()
    {
        var a = Armoury(Arm(Def(1, 0)), Arm(Def(1, 0)), Arm(Def(4, 0)), Arm(Def(4, 0)));
        // L: the link builds the group of the selected one's class and mount kind (one cammo per weapon linked)
        Assert.Equal(1, a.Select(false, true, out _));
        Assert.True(a.Link);
        Assert.Equal([1], a.Group);
        // the group stands while it is not empty: a linked weapon shot away stays in it
        a.Weapons[1].Hp = 0;
        Assert.Equal(0, a.Select(false, false, out _));
        Assert.Equal([1], a.Group);
        a.Weapons[1].Hp = 45;
        // the selected one dies: the cycle passes over its group member to the rockets, and the link builds their group
        a.Weapons[0].Hp = 0;
        Assert.Equal(2, a.Select(false, false, out bool changed));
        Assert.True(changed);
        Assert.Equal(2, a.Selected);
        Assert.Equal([3], a.Group);
        // L again: unlinked, one cammo for the weapon let go
        Assert.Equal(1, a.Select(false, true, out _));
        Assert.False(a.Link);
        Assert.Empty(a.Group);
    }

    [Fact]
    public void ACycleWithNowhereToGoChangesNothingAndIsSilent()
    {
        var a = Armoury(Arm(Def(1, 0)), Arm(Def(1, 0), hp: 0));
        Assert.Equal(0, a.Select(true, false, out bool changed));
        Assert.False(changed);
        Assert.Equal(0, a.Selected);
    }

    [Fact]
    public void WithEveryHardpointDeadTheKeysDoNothing()
    {
        var a = Armoury(Arm(Def(1, 0), hp: 0), Arm(Def(1, 0), hp: 0));
        Assert.Equal(0, a.Select(true, true, out _));
        Assert.False(a.Link);
        Assert.Equal(0, a.Selected);
    }

    [Fact]
    public void ASelectedFourGetUNotLinksTheOtherMissilesButIsNeverLinkedItself()
    {
        var a = Armoury(Arm(Def(4, 4)), Arm(Def(4, 1)), Arm(Def(4, 4)));
        a.Select(false, true, out _);
        Assert.Equal([1], a.Group);
    }

    // ---- 6. clockon.wav (the round's contrail) ----

    private static (Combat C, Vehicle Shooter, Vehicle Player, Weapon W) RadarRig()
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var player = new Vehicle { Pos = new(0, 0, 100), Flags = VehicleFlags.EngineOn };
        var shooter = new Vehicle { Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(player); world.Vehicles.Add(shooter);
        world.Player = player;
        var def = Def(4, 2) with
        {
            Kind = 8, Speed = 100, Rate = 0.5f, Life = 5,
            Ordnance = new OrdnanceDef(8, 125, 0, 2, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = Arm(def);
        shooter.Armory = Armoury(w);
        // a network session: the AI's radar is not blinded by the player's jam (the AI's radar-target update), so the test is the warning's own
        var c = new Combat(world, _ => null) { NetSession = true };
        c.RadarTarget[shooter] = player;
        return (c, shooter, player, w);
    }

    [Fact]
    public void ARadarMissileLaunchedAtThePlayerWarnsItsDriver()
    {
        var (c, shooter, player, w) = RadarRig();
        w.Trigger = true;
        c.Frame(Dt);
        Assert.Single(c.Rounds);
        var warn = Assert.Single(c.Events.OfType<WeaponClick>());
        Assert.Same(player, warn.Shooter);
        Assert.Equal("clockon.wav", warn.Sound);
        Assert.True(Combat.IsDriverSound(warn.Sound));
    }

    [Fact]
    public void AJammingPlayerHearsNoWarning()
    {
        var (c, _, player, w) = RadarRig();
        player.Flags |= VehicleFlags.Jamming;
        w.Trigger = true;
        c.Frame(Dt);
        Assert.DoesNotContain(c.Events, e => e is WeaponClick);
    }

    // ---- 9. the network jam (the original's network branch) ----

    private static (Combat C, Vehicle Car, Weapon W) JamRig(bool net, int hp)
    {
        var world = new SimWorld(new Flat(), new CrtRand(7));
        var car = new Vehicle();
        world.Vehicles.Add(car);
        world.Humans.Add(car);
        var def = Def(1, 0) with { Rate = 60, Ammo = 100000 };
        var w = Arm(def, hp: hp);
        car.Armory = Armoury(w);
        return (new Combat(world, _ => null) { NetSession = net }, car, w);
    }

    [Fact]
    public void ADamagedWeaponJamsInANetworkGameAsOftenAsTheOriginalSays()
    {
        // HP 22 of 45: p = (1 − 0.489) − 0.3 = 0.211 per triggered frame; a jam costs ftol(dt·rate) = 2 rounds
        var (c, _, w) = JamRig(true, 22);
        int frames = 4000, jams = 0;
        for (int f = 0; f < frames; f++)
        {
            int ammo = w.Ammo;
            w.Trigger = true;
            c.Frame(Dt);
            int shots = c.Events.OfType<ShotFired>().Sum(_ => 1);
            if (shots == 0 && ammo - w.Ammo == 2) jams++;
            c.Events.Clear();
            c.Rounds.Clear();
        }
        Assert.InRange(jams / (float)frames, 0.18f, 0.24f);
    }

    [Fact]
    public void AWholeWeaponNeverJamsAndSinglePlayerNeverJams()
    {
        foreach (var (net, hp) in new[] { (true, 45), (true, 32), (false, 5) })
        {
            var (c, _, w) = JamRig(net, hp);
            for (int f = 0; f < 200; f++)
            {
                w.Trigger = true;
                c.Frame(Dt);
                Assert.NotEmpty(c.Events.OfType<ShotFired>());
                c.Events.Clear();
                c.Rounds.Clear();
            }
        }
    }

    [Fact]
    public void AnEmptyWeaponInANetworkGameLetsGoWithoutAClick()
    {
        var (c, car, w) = JamRig(true, 45);
        car.Armory!.Weapons[0].Ammo = 0;
        w.Trigger = true;
        c.Frame(Dt);
        Assert.DoesNotContain(c.Events, e => e is WeaponClick);
        var (c2, _, w2) = JamRig(false, 45);
        w2.Ammo = 0;
        w2.Trigger = true;
        c2.Frame(Dt);
        Assert.Contains(c2.Events, e => e is WeaponClick { Sound: "wclick.wav" });
    }

    // ---- 4. Nitro's dropper rate (the Nitro Pack) ----

    private static int Drops(bool nitro, float speed, float seconds)
    {
        var world = new SimWorld(new Flat(), new CrtRand(3));
        var car = new Vehicle { Speed = speed };
        world.Vehicles.Add(car);
        world.Player = car;
        var def = Def(6, 1) with
        {
            Kind = 0xc, Rate = 60, Burst = 1, Delay = 0, Speed = 0, Life = 20, Spread = 0,
            Ordnance = new OrdnanceDef(0xc, 50, 1, 0, 0, ["", "", "", ""], ["", "", "", ""]),
        };
        var w = Arm(def, mount: 2, style: 4);
        w.Ammo = 100000;
        car.Armory = Armoury(w);
        var c = new Combat(world, _ => null) { NitroRules = nitro };
        int n = 0;
        for (int f = 0; f * Dt < seconds; f++)
        {
            w.Trigger = true;
            c.Frame(Dt);
            n += c.Events.OfType<ShotFired>().Count();
            c.Events.Clear();
            c.Rounds.Clear();
        }
        return n;
    }

    [Theory]
    [InlineData(0f, 1.2f)]
    [InlineData(20f, 4.8f)]
    [InlineData(50f, 10.2f)]
    public void NitroDropsAtPointEighteenTimesTheSpeedPlusOnePointTwoASecond(float speed, float perSecond)
    {
        float got = Drops(true, speed, 20f) / 20f;
        Assert.InRange(got, perSecond * 0.95f, perSecond * 1.05f + 0.05f);
    }

    [Fact]
    public void I76StillDribblesBelowTwentyAndPoursAbove()
    {
        Assert.InRange(Drops(false, 0f, 20f) / 20f, 0.95f, 1.1f);     // B = 1 s at rest
        Assert.InRange(Drops(false, 30f, 5f) / 5f, 55f, 61f);          // 60 a second
    }

    // ---- 7. the Ball O'Flame's impact (the bullet step) ----

    [Fact]
    public void TheBallOFlamesImpactIsBackedOffTwoMetres()
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = new Vehicle { Pos = new(0, 2, 0) };
        world.Vehicles.Add(car);
        var def = Def(4, 0) with { Kind = 0x15, Speed = 100, Life = 5, Spread = 0, Ordnance = new OrdnanceDef(0x15, 100, 0, 4, 0, ["xa", "xa", "xa", ""], ["", "", "", ""]) };
        var w = Arm(def);
        car.Armory = Armoury(w);
        var c = new Combat(world, _ => null);
        var r = new Round { Kind = 0x15, Shooter = car, Def = def, Ord = def.Ordnance!, Pos = new(0, 1, 0), PrevPos = new(0, 1, 0),
            Rot = Rows.Concat(Rows.AboutX(0.5f), Rows.Identity), LocalVel = new(0, 0, 50), Life = 5, Age = 2 };
        var rot0 = r.Rot;
        c.Rounds.Add(r);
        for (int f = 0; f < 40 && c.Rounds.Count > 0; f++)
        {
            Assert.Equal(rot0.Forward, r.Rot.Forward);   // never re-aimed
            c.Frame(Dt);
        }
        var hit = Assert.Single(c.Events.OfType<Impact>());
        Assert.Equal(2f, Vector3.Distance(hit.At, hit.Hit), 3);
        Assert.True(hit.At.Y > hit.Hit.Y);
    }

    // ---- the cluster bomb's barrel on a side hardpoint (the original's mirror, issue 89) ----

    /// <summary>
    /// A cluster bomb's gizmo leans its barrel up 16 degrees, however it is mounted. On a side (S) hardpoint of a car's left
    /// (a front one at x &lt; 0, a back one at x &gt; 0) the original turns the muzzle's matrix about when it mounts it
    ///; without that the bomb left such a hardpoint pointing down into the car.
    /// </summary>
    [Fact]
    public void AClusterBombOnASideHardpointLeansUpOnEitherSideOfTheCar()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var g = GizmoDef.Read("gcluster", vfs.Read("gcluster.gdf"))!;
        int checkedMounts = 0, left = 0;
        foreach (var n in vfs.Names.Where(x => x.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("vu") && !x.StartsWith("vm")).OrderBy(x => x))
            foreach (var hp in Hardpoint.Read(new Bwd2File(vfs.Read(n))).Where(h => h.Style == 2 && h.Facing is 1 or 2))
            {
                var at = Affine.From(hp.Transform);
                var muzzle = Armory.MuzzleOn(g, hp, 2);
                var fwd = new Vector3(muzzle.R[2], muzzle.R[5], muzzle.R[8]);
                Assert.True(fwd.Y > 0.2f, $"{n} {hp.Name} (facing {hp.Facing}, x {at.T.X:0.0}) leans {MathF.Asin(fwd.Y) * 57.3f:0} degrees");
                checkedMounts++;
                if (hp.Facing == 1 ? at.T.X < 0 : at.T.X > 0) left++;
            }
        Assert.True(checkedMounts > 10 && left > 3, $"{checkedMounts} side mounts, {left} on the mirrored side");
    }

    // ---- 1. the cluster bomb's bomblets ----

    private static (Combat C, SimWorld W, Vehicle Car, Sink S) ClusterRig(uint seed)
    {
        var world = new SimWorld(new Flat(), new CrtRand(seed));
        var car = new Vehicle { Pos = new(0, 0, -50) };
        world.Vehicles.Add(car);
        var fx = new EffectDef("xclst1.xdf", 1, 1f, 45f, 20f, []);
        var c = new Combat(world, n => n.Equals("xclst1.xdf", StringComparison.OrdinalIgnoreCase) ? fx : null);
        var s = new Sink();
        c.Damage = s;
        return (c, world, car, s);
    }

    private static Round Cluster(Vehicle shooter, Vector3 at)
    {
        var def = Def(3, 2) with { Kind = 5, Damage = 45, Speed = 100, Life = 4, Ordnance = new OrdnanceDef(5, 100, 4, 2, 0, ["xclst1.xdf", "xclst1.xdf", "xclst1.xdf", ""], ["", "", "", ""]) };
        // a round about to reach the ground under at
        return new Round { Kind = 5, Shooter = shooter, Def = def, Ord = def.Ordnance!, Pos = at + new Vector3(0, 0.5f, 0), PrevPos = at + new Vector3(0, 0.5f, 0),
            Rot = Rows.Concat(Rows.AboutX(1.2f), Rows.Identity), LocalVel = new(0, 0, 30), Life = 3, Age = 1.5f, Damage = 45 };
    }

    [Fact]
    public void AClusterBombScattersItsBombletsAsTheOriginalDoes()
    {
        for (uint seed = 1; seed <= 20; seed++)
        {
            var (c, w, car, sink) = ClusterRig(seed);
            var target = new Vehicle { Pos = new(3, 0, 0) };
            w.Vehicles.Add(target);
            c.Rounds.Add(Cluster(car, Vector3.Zero));
            c.Frame(Dt);
            Assert.Empty(c.Rounds);
            var b = Assert.Single(c.Clusters);
            Assert.InRange(b.Left, 8, 12);
            int filled = b.Live.Count(x => x);
            Assert.Equal(b.Left + 1, filled);
            int n = b.Left;
            foreach (var (p, i) in b.At.Select((p, i) => (p, i)).Where(t => b.Live[t.i]))
            {
                Assert.InRange(p.X, -10f, 10f);
                Assert.InRange(p.Z, -10f, 10f);
                Assert.Equal(0f, p.Y);
                Assert.InRange(b.Due[i] - w.Time, 0f, 1.8f);
            }
            // the landing's own impact: one xclst1 and its splash on the target
            Assert.Single(c.Events.OfType<Explosion>());
            c.Events.Clear();
            sink.Hits.Clear();
            int booms = 0; float last = 0;
            for (int f = 0; f < 60; f++)
            {
                w.Time += Dt;
                c.Frame(Dt);
                int k = c.Events.OfType<Explosion>().Count();
                if (k > 0) last = f * Dt;
                booms += k;
                c.Events.Clear();
            }
            // n go off, or all n + 1 when the last two fall in one frame; within the halved 1.8 s
            Assert.InRange(booms, n, n + 1);
            Assert.InRange(last, 0f, 1.0f);
            // each one's splash reaches a car 3 m from the landing: 45 explosive, no falloff
            var onTarget = sink.Hits.Where(h => h.Victim == target).ToList();
            Assert.Equal(booms, onTarget.Count);
            Assert.All(onTarget, h => { Assert.Equal(45, h.P.Ch[1]); Assert.Equal(2, h.P.Mask); });
            Assert.Equal(booms == n ? 0 : 1, c.Clusters.Count);
        }
    }

    [Fact]
    public void ASecondClusterLandingBeforeTheFirstIsDoneLosesItsRest()
    {
        // the original frees a record by setting the list's head to its successor: the newer record ahead goes with it
        int dropped = 0;
        for (uint seed = 1; seed <= 30; seed++)
        {
            var (c, w, car, _) = ClusterRig(seed);
            c.Rounds.Add(Cluster(car, Vector3.Zero));
            c.Frame(Dt);
            w.Time += Dt; c.Frame(Dt);
            c.Rounds.Add(Cluster(car, new Vector3(100, 0, 0)));
            w.Time += Dt; c.Frame(Dt);
            Assert.Equal(2, c.Clusters.Count);
            var first = c.Clusters[1];
            var second = c.Clusters[0];
            int n2 = second.Left, booms2 = 0;
            c.Events.Clear();
            for (int f = 0; f < 60; f++)
            {
                bool firstFreed = !c.Clusters.Contains(first);
                w.Time += Dt;
                c.Frame(Dt);
                booms2 += c.Events.OfType<Explosion>().Count(x => x.At.X > 50);
                c.Events.Clear();
                if (!firstFreed && !c.Clusters.Contains(first) && !c.Clusters.Contains(second) && second.Live.Count(x => x) > 1)
                {
                    dropped++;
                    Assert.True(booms2 < n2);
                }
            }
        }
        Assert.True(dropped > 0, "no seed freed the first record while the second still had bomblets");
    }

    // ---- 2. the EZK Mortar's elevation ----

    private static (Combat C, SimWorld W, Vehicle Car, Weapon Ezk) EzkRig()
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = new Vehicle { Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(car);
        world.Player = car;
        var def = Def(3, 3) with { Kind = 4, Speed = 100, Life = 5, Spread = 0, Rate = 1, Ordnance = new OrdnanceDef(4, 100, 4, 2, 0, ["", "", "", ""], ["", "", "", ""]) };
        var w = Arm(def);
        car.Armory = Armoury(w);
        return (new Combat(world, _ => null), world, car, w);
    }

    [Fact]
    public void WithNoTargetTheEzkFiresAtFourteenDegrees()
    {
        var (c, _, _, w) = EzkRig();
        w.Trigger = true;
        c.Frame(Dt);
        Assert.Equal(0.2443461f, w.GunPitch, 6);
        var shot = Assert.Single(c.Events.OfType<ShotFired>());
        Assert.Equal(MathF.Sin(0.2443461f), shot.Forward.Y, 4);
    }

    [Fact]
    public void TheEzkLaysItsElevationForTheTargetsPredictedPlace()
    {
        foreach (float range in new[] { 30f, 80f, 150f })
        {
            var (c, w, car, ezk) = EzkRig();
            var t = new Vehicle { Pos = new(0, 0, range), Vel = new(8, 0, 0), Flags = VehicleFlags.EngineOn };
            w.Vehicles.Add(t);
            c.RadarTarget[car] = t;
            ezk.Trigger = true;
            c.Frame(Dt);
            Assert.InRange(ezk.GunPitch, 0.0001f, 0.7854f);
            // one of the 120 angles, steps of π/480 (measured: 0.0262, 0.0785, 0.2160 rad, 4, 12 and 33 steps)
            float steps = ezk.GunPitch / 0.006544985f;
            Assert.Equal(MathF.Round(steps), steps, 2);
            // the predictor's own flight (no drag, 4 g, the frame delta) from the laid muzzle lands near the target's predicted place
            var v0 = c.Events.OfType<ShotFired>().Single().Forward * 100f;
            Vector3 p = Vector3.Zero; float time = 0;
            do { p += new Vector3(v0.X, v0.Y - Dt * 19.6f, v0.Z) * Dt; v0.Y -= Dt * 39.2f; time += Dt; } while (p.Y > 0);
            var aim = Combat.Collider(t).Centre + t.Vel * time;
            // the solver turns by 2·atan(θ/2), the shot by θ: close, not exact
            Assert.True(new Vector2(p.X - aim.X, p.Z - aim.Z).Length() < range * 0.12f, $"{range} m: lands {p}, wanted {aim}");
        }
    }

    [Fact]
    public void ATargetOutOfReachGetsFortyFiveDegrees()
    {
        var (c, w, car, ezk) = EzkRig();
        var t = new Vehicle { Pos = new(0, 0, 600), Flags = VehicleFlags.EngineOn };
        w.Vehicles.Add(t);
        c.RadarTarget[car] = t;
        ezk.Trigger = true;
        c.Frame(Dt);
        Assert.Equal(0.7853981f, ezk.GunPitch, 6);
    }

    // ---- 3. Nitro: the Chemical Mortar kills the driver (the Nitro Pack) ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheChemicalMortarKillsTheDriverUnderNitroRules(bool nitro)
    {
        string? dir = nitro ? NitroDir : InstallDir;
        if (dir is null) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(victim);
        var combat = new Combat(world, _ => null) { NitroRules = nitro };
        var dm = new DamageModel(world, combat);
        victim.Health = VehicleDamage.Create(victim, victim.Build!, false, world.Rand);
        var chem = new DamagePacket { Mask = 2, Flags = 0x10, Kind = DamageKind.Round };
        chem.Ch[1] = 45;
        dm.Apply(victim, chem, new Vector3(0, 0.5f, 2f));
        if (nitro)
        {
            Assert.Equal((VehicleFlags)0x4020, victim.Flags & (VehicleFlags)0x4020);
            var killed = Assert.Single(dm.Events.OfType<VehicleKilled>());
            Assert.True(killed.DriverKilled);
            Assert.DoesNotContain(combat.Events, e => e is Explosion);   // no x1_carx1: the driver-killed path
        }
        else Assert.False(DamageModel.IsDead(victim));
    }

    // ---- 8. Nitro CTF: the regen pads pay no points (the Nitro Pack) is in MeleeGame; see NitroModeTests ----
}
