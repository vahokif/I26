using System.Numerics;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The flame weapons' burn against the real game (i76-godot issue 17).
/// The packet counts are the original game's (m01 Auto Melee at a fixed
/// 50 ms step, the opponent Moth held at rest behind the player's rear flamer, Wimp difficulty as the install's
/// I76PLYR.DEF has it); the bus times are the reporter's, from the original.
/// </summary>
public class FlameDamageTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static string? Dir(string v) => Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

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

    /// <summary>The original run's frame deltas from the first held frame: 49.805 and 50.293 ms (a fixed 50 ms step on the float32 clock).</summary>
    private static float OriginalDt(int f) => f % 5 is 2 or 4 ? 103f / 2048f : 102f / 2048f;

    /// <summary>
    /// The player's car holds its flamer at a car with the Moth's COLP box (vmxmoth.vdf, the original game's opponent)
    /// standing <paramref name="dist"/> along its forward (behind it: the rear flamers), nose-on or broadside; the
    /// target's packets and their ch2 summed, per frame.
    /// </summary>
    private static (int[] Packets, int[] Ch2) Burn(AssetVfs vfs, string car, float dist, bool broadside, int frames)
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var shooter = VehicleFactory.Create(vfs, car);
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var moth = new Bwd2File(vfs.Read("vmxmoth.vdf"));
        var colp = moth.First("COLP")!.Value;
        float C(int i) => I76.Formats.Bin.F32(moth.Data, colp.PayloadOffset + 4 * i);
        target.BoxZ = [C(3), C(2), C(1), C(0)]; target.BoxX = [C(7), C(6), C(5), C(4)]; target.BoxY = [C(11), C(10), C(9), C(8)];
        target.Pos = new Double3(0, 0, dist);
        target.Rot = broadside ? new Rows(Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX) : Rows.Identity;
        world.Vehicles.Add(shooter); world.Vehicles.Add(target);
        world.Player = shooter;
        var sink = new Sink();
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null) { Damage = sink, Difficulty = 0 };
        var a = shooter.Armory!;
        var keys = new bool[5]; keys[a.Weapons.FindIndex(w => w.Def.Kind is 9 or 10 or 11)] = true;
        var packets = new int[frames]; var ch2 = new int[frames];
        for (int f = 0; f < frames; f++)
        {
            a.PlayerTriggers(false, keys);
            sink.Hits.Clear();
            combat.Frame(OriginalDt(f));
            var mine = sink.Hits.Where(h => h.Victim == target).ToList();
            packets[f] = mine.Count;
            ch2[f] = mine.Sum(h => h.P.Ch[2]);
            Assert.All(mine, h => Assert.Equal((ushort)4, h.P.Mask));
        }
        return (packets, ch2);
    }

    /// <summary>
    /// The original wants the segment to enter the box: a point already inside burns nothing. Measured in the game
    /// (mirror off): the Piranha's rear Gas Launcher, the Moth's nose 1.5 m past the muzzle, burns
    /// with 1, 2, 3, 4, 5 points and then 5 a frame (the stream lies flat on the car from its second point, and
    /// only the four eighth-metre steps short of the nose are outside it), at 2 or 3 a packet (30 · 2 · dt: Wimp);
    /// the Napalm Hose the same five at 4; broadside 9; the Cameo's Flamethrower, yawed 5°, misses it. The port
    /// burned every point lying on the car, 18 a frame, before.
    /// </summary>
    [Theory]
    [InlineData("vppirna1.vcf", false, "1 2 3 4 5 5 5 5 5 5 5 5", "2 4 9 8 15 10 10 15 10 15 10 10")]   // flame2, frames 201..212
    [InlineData("vppirna2.vcf", false, "1 2 3 4 5 5 5 5 5 5 5 5", "4 8 12 16 20 20 20 20 20 20 20 20")] // flame5
    [InlineData("vppirna1.vcf", true, "0 1 2 3 4 5 6 7 8 9 9 9", "0 2 6 6 12 10 12 21 16 27 18 18")]     // flame6
    [InlineData("vccameo2.vcf", false, "0 0 0 0 0 0 0 0 0 0 0 0", "0 0 0 0 0 0 0 0 0 0 0 0")]             // flame4
    public void AStreamBurnsOnlyThePointsShortOfTheCar(string car, bool broadside, string packets, string ch2)
    {
        if (Dir("I76_INSTALL") is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var (p, c) = Burn(vfs, car, -7f, broadside, 12);
        Assert.Equal(packets, string.Join(" ", p));
        Assert.Equal(ch2, string.Join(" ", c));
    }

    /// <summary>A weapon alone on a fixed front mount at (0, 1, 2), looking along +z.</summary>
    private static Weapon Mount(AssetVfs vfs, Vehicle shooter, string gdf)
    {
        var def = GizmoDef.Read(gdf, vfs.Read(gdf))!;
        var w = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint(gdf[..^4], 1, 1, 1, new Transform([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 1, 2])),
            MountKind = 1, MuzzlePos = new(0, 1, 2), BaseRot = Rows.Identity, Hp = 10, MaxHp = 10, Damage = (int)def.Damage, Ammo = 100000,
        };
        shooter.Armory!.Weapons.Clear();
        shooter.Armory.Weapons.Add(w);
        return w;
    }

    /// <summary>Seconds of the trigger held until the bus dies (NaN: alive after 60 s), its broadside <paramref name="gap"/> past the muzzle.</summary>
    private static float KillBus(AssetVfs vfs, string gdf, SimRate rate, float gap, uint seed)
    {
        var world = new SimWorld(new Flat(), new CrtRand(seed));
        var shooter = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var bus = VehicleFactory.Create(vfs, "vxbus1.vcf");
        bus.Rot = Rows.AboutY(MathF.PI / 2);
        bus.Pos = new Double3(0, 0, 2 + gap + MathF.Max(-bus.BoxX[0], bus.BoxX[3]));
        world.Vehicles.Add(shooter); world.Vehicles.Add(bus);
        world.Player = shooter;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == world.Player, world.Rand);
        Mount(vfs, shooter, gdf);
        var combat = new Combat(world, n => vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var damage = new DamageModel(world, combat);
        bool dead = false;
        damage.Killed += v => dead |= v == bus;
        var keys = new bool[5]; keys[0] = true;
        for (int f = 0, n = rate.TicksFor(60f); f < n; f++)
        {
            shooter.Armory!.PlayerTriggers(false, keys);
            combat.Frame(rate.TickSeconds);
            if (dead) return (f + 1) * rate.TickSeconds;
        }
        return float.NaN;
    }

    /// <summary>
    /// The reporter's bus (Nitro's vxbus) in the original: Flamethrower 3.8 s, Gas Launcher 3.3 s, Napalm Hose 2.6 s.
    /// Their distance, difficulty and frame rate are not known. The original's default view is the cockpit with the
    /// rear-view mirror up, whose pass burns the streams again every second frame (half as much again);
    /// the port's burn does not depend on the view (docs/deviations.md), so it is the original's from the chase view,
    /// and is held to the reporter's times ×1.5 within 35 %. Champ and the bus's side 1.5 m from the muzzle, the
    /// medians over 12 seeds are about 6.0 / 6.0 / 3.1 s at 20 Hz and 4.3 each at 28 (the per-point burn is 1 for all
    /// three there); the hose is faster than the thrower at 20 Hz. Point-blank the burn falls away fast: with the
    /// side under a metre from the muzzle no point is short of it and nothing burns.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(28)]
    public void TheBusBurnsInTheReportersTimes(int hz)
    {
        if (Dir("I76_NITRO_INSTALL") is not { } nitro) return;
        var vfs = new AssetVfs(new GameInstall(nitro));
        var rate = SimRate.FromLabel(hz);
        float Median(string gdf)
        {
            var ts = Enumerable.Range(1, 12).Select(s => KillBus(vfs, gdf, rate, 1.5f, (uint)s)).Order().ToList();
            return ts[6];
        }
        var (ft, gl, nh) = (Median("gflight.gdf"), Median("gfmedium.gdf"), Median("gfheavy.gdf"));
        log.WriteLine($"R{hz}: Flamethrower {ft:0.00} s, Gas Launcher {gl:0.00} s, Napalm Hose {nh:0.00} s (the original, cockpit and mirror: 3.8, 3.3, 2.6; from the chase view about 5.7, 5.0, 3.9)");
        const float Mirror = 1.5f;   // the original's cockpit mirror pass, which the reporter's times presumably include and the port leaves out
        Assert.InRange(ft, 3.8f * Mirror * 0.65f, 3.8f * Mirror * 1.35f);
        Assert.InRange(gl, 3.3f * Mirror * 0.65f, 3.3f * Mirror * 1.35f);
        Assert.InRange(nh, 2.6f * Mirror * 0.65f, 2.6f * Mirror * 1.35f);
        if (hz == 20) Assert.True(nh < ft);
    }
}
