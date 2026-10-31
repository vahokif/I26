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

/// <summary>
/// The Nitro Pack's Chemical Mortar (ggchem.gdf, ordnance kind 0x16): it lands
/// and lingers as a cloud for its life, and a car in the cloud takes its packet and impact every frame.
/// </summary>
public class ChemMortarTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

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

    private static (SimWorld World, Combat Combat, Vehicle Car, Weapon Chem, Sink Sink, AssetVfs Vfs) Rig()
    {
        var vfs = new AssetVfs(new GameInstall(NitroDir!));
        var s = System.Text.Encoding.Latin1.GetString(vfs.Read("vppirna1.vcf"));
        var weapon = new[] { "gfmedium.gdf", "gflight.gdf", "gmhe.gdf", "gwhiteph.gdf" }.First(g => s.Contains(g, StringComparison.OrdinalIgnoreCase));
        AssetVfs.Addon["vppirchm.vcf"] = System.Text.Encoding.Latin1.GetBytes(s.Replace(weapon, "ggchem.gdf".PadRight(weapon.Length, '\0')));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = VehicleFactory.Create(vfs, "vppirchm.vcf");
        world.Vehicles.Add(car);
        world.Player = car;
        var effects = new Dictionary<string, EffectDef?>(StringComparer.OrdinalIgnoreCase);
        var combat = new Combat(world, n => effects.TryGetValue(n, out var e) ? e : effects[n] = vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var sink = new Sink();
        combat.Damage = sink;
        var chem = car.Armory!.Weapons.Single(w => w.Def.Kind == 0x16);
        return (world, combat, car, chem, sink, vfs);
    }

    private static void Pull(Vehicle car, Weapon w)
    {
        var a = car.Armory!;
        var keys = new bool[5];
        keys[a.Weapons.IndexOf(w)] = true;
        a.PlayerTriggers(false, keys);
    }

    [Fact]
    public void TheChemicalMortarLandsAndLingersAsACloud()
    {
        if (NitroDir is null) return;
        var (_, combat, car, chem, sink, _) = Rig();
        const float dt = 102 / 2048f;
        Pull(car, chem);
        combat.Frame(dt);
        var r = combat.Rounds.Single(x => x.Kind == 0x16);
        int frames = 1;
        while (!r.Settled && frames < 200) { combat.Events.Clear(); combat.Frame(dt); frames++; }
        Assert.True(r.Settled, "the round never landed");
        Assert.Equal(0f, r.Pos.Y, 3);
        float landedAt = frames * dt;
        Assert.InRange(landedAt, 0.2f, 3f);
        Console.WriteLine($"chem: landed {landedAt:0.00} s, {new Vector2(r.Pos.X - (float)car.Pos.X, r.Pos.Z - (float)car.Pos.Z).Length():0.0} m away");
        // landing on the ground itself makes no impact, and hurts nobody
        Assert.DoesNotContain(combat.Events, e => e is Impact);
        Assert.Empty(sink.Hits);
        // it stays until its life (16.7 s from the shot) runs out
        while (combat.Rounds.Contains(r) && frames < 2000) { combat.Events.Clear(); combat.Frame(dt); frames++; }
        Assert.InRange(frames * dt, chem.Def.Life - 2 * dt, chem.Def.Life + 2 * dt);
        Assert.Empty(sink.Hits);
    }

    [Fact]
    public void ACarInTheCloudIsHurtEveryFrame()
    {
        if (NitroDir is null) return;
        var (world, combat, car, chem, sink, vfs) = Rig();
        const float dt = 102 / 2048f;
        Pull(car, chem);
        combat.Frame(dt);
        var r = combat.Rounds.Single(x => x.Kind == 0x16);
        for (int i = 0; i < 200 && !r.Settled; i++) combat.Frame(dt);
        Assert.True(r.Settled);
        combat.Events.Clear();
        // another car drives into it
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        victim.Pos = new Double3(r.Pos.X, 0, r.Pos.Z);
        world.Vehicles.Add(victim);
        for (int i = 0; i < 4; i++) combat.Frame(dt);
        // each frame: the round's packet (45, armour bypassed: flag 0x10) and the xclst1 impact whose splash (45) reaches it
        var direct = sink.Hits.Where(h => h.Victim == victim && (h.P.Flags & 0x10) != 0).ToList();
        Assert.Equal(4, direct.Count);
        Assert.All(direct, h => Assert.Equal(45, h.P.Total));
        Assert.Equal(4, sink.Hits.Count(h => h.Victim == victim && h.P.Flags == 0 && h.P.Mask == 2 && h.P.Ch[1] == 45));
        Assert.Equal(4, combat.Events.OfType<Impact>().Count(i => i.Victim == victim && i.Kind == 0x16));
        Assert.Contains(combat.Events, e => e is Explosion x && x.Effect.File.Equals("xclst1.xdf", StringComparison.OrdinalIgnoreCase));
        // the shooter beside it is splashed too (no one is spared), and the cloud is still there
        Assert.Contains(combat.Rounds, x => x == r);
    }

    [Fact]
    public void ADirectHitLandsTheCloudUnderTheCar()
    {
        if (NitroDir is null) return;
        const float dt = 102 / 2048f;
        // where the round comes down with nobody in the way (the same rand stream, so the same flight)
        Vector3 Land(Vehicle? victimAt)
        {
            var (world, combat, car, chem, sink, vfs) = Rig();
            Vehicle? victim = null;
            if (victimAt is not null)
            {
                victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
                victim.Pos = victimAt.Pos;
                world.Vehicles.Add(victim);
            }
            Pull(car, chem);
            combat.Frame(dt);
            var r = combat.Rounds.Single(x => x.Kind == 0x16);
            for (int i = 0; i < 100 && !r.Settled; i++) combat.Frame(dt);
            Assert.True(r.Settled);
            if (victim is not null) Assert.Contains(sink.Hits, h => h.Victim == victim && (h.P.Flags & 0x10) != 0);
            return r.Pos;
        }
        var free = Land(null);
        // a car standing where it would land: the round meets its back and comes down under the hit, short of the free landing
        var stand = new Vehicle { Pos = new Double3(free.X, 0, free.Z) };
        var hit = Land(stand);
        float shorter = new Vector2(free.X - hit.X, free.Z - hit.Z).Length();
        Assert.InRange(shorter, 0.5f, 4f);
        Assert.Equal(0f, hit.Y, 3);
    }
}
