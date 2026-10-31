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

/// <summary>
/// The Nitro Pack's caltrops (gcaltrop.gdf, ordnance kind 0xe; CaltropTyreHit): a car over one has the tyre nearest it set to 25 of 100, which blows it.
/// </summary>
public class CaltropTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>A four-wheeled car with 100-point tyres, its wheels at (±1, 0.5, ±1.5).</summary>
    private static (Vehicle Car, DamageModel Damage) Car(params int[] specials)
    {
        var car = new Vehicle { Pos = new Double3(0, 0, 0) };
        var d = car.Health = new VehicleDamage(car, false);
        (Wheel W, int Slot, float X, float Z)[] wheels = [(car.FrontRight, 0, 1, 1.5f), (car.FrontLeft, 1, -1, 1.5f), (car.RearRight, 4, 1, -1.5f), (car.RearLeft, 5, -1, -1.5f)];
        foreach (var (w, slot, x, z) in wheels)
        {
            w.Offset = new Vector3(x, 0.5f, z);
            w.InitSuspension();
            w.Grip = 1f; w.Rolling = 1f;
            d.Slots[slot] = new WheelPart(w, 100);
        }
        for (int i = 0; i < specials.Length; i++) d.Slots[17 + i] = new SpecialPart(specials[i]);
        var world = new SimWorld(new Flat(), new CrtRand(1));
        world.Vehicles.Add(car);
        var dm = new DamageModel(world, new Combat(world, _ => null));
        return (car, dm);
    }

    [Fact]
    public void TheNearestTyreGoesToTwentyFiveOfAHundredAndBlows()
    {
        var (car, dm) = Car();
        // under the rear left wheel, a little inboard
        dm.CaltropTyreHit(car, new Vector3(-0.8f, 0, -1.4f));
        var rl = (WheelPart)car.Health!.Slots[5]!;
        Assert.Equal((25, 100), (rl.Hp, rl.Max));
        Assert.True(car.RearLeft.Blown);
        Assert.Equal(0.5f * 0.688f, car.RearLeft.Rest, 5);
        // The caltrop hit sets the hit points alone: grip and rolling are as they were
        Assert.Equal((1f, 1f), (car.RearLeft.Grip, car.RearLeft.Rolling));
        Assert.Equal(new TyreBlown(car, 5), Assert.Single(dm.Events));
        Assert.False(car.FrontLeft.Blown || car.FrontRight.Blown || car.RearRight.Blown);
        // every frame over it does it again: a tyre at 10 goes back up to 25, and blows only once
        rl.Hp = 10;
        dm.CaltropTyreHit(car, new Vector3(-0.8f, 0, -1.4f));
        Assert.Equal((25, 100), (rl.Hp, rl.Max));
        Assert.Single(dm.Events);
    }

    [Fact]
    public void APlayersTyreTakesTheHundredMaxToo()
    {
        var (car, dm) = Car();
        var fr = (WheelPart)car.Health!.Slots[0]!;
        fr.Hp = fr.Max = 400;   // the player's tyres are ×4
        dm.CaltropTyreHit(car, new Vector3(1.2f, 0, 1.9f));
        Assert.Equal((25, 100), (fr.Hp, fr.Max));
        Assert.True(car.FrontRight.Blown);
    }

    [Fact]
    public void TireCoversTakeAQuarterAFrameAndBlowUnderTwentySix()
    {
        // special 11 counts in any slot, whatever its state (the special's type, not the active test)
        var (car, dm) = Car(5, 11);
        ((SpecialPart)car.Health!.Slots[18]!).Charges = 0;
        var fl = (WheelPart)car.Health.Slots[1]!;
        var at = new Vector3(-1, 0, 1.5f);
        int[] seen = new int[5];
        for (int i = 0; i < 5; i++)
        {
            dm.CaltropTyreHit(car, at);
            seen[i] = fl.Hp;
            Assert.Equal(i == 4, car.FrontLeft.Blown);
        }
        // 100 → 75 → 56 → 42 → 31 → 23 (truncated); 23 < 26 and 23/100 ≤ 0.25 blow it
        Assert.Equal([75, 56, 42, 31, 23], seen);
        Assert.Equal(100, fl.Max);
        Assert.Equal(new TyreBlown(car, 1), Assert.Single(dm.Events));
    }

    [Fact]
    public void HelicoptersAndGunsAreLeftAlone()
    {
        var (car, dm) = Car();
        car.ObjectClass = 9;
        dm.CaltropTyreHit(car, new Vector3(1, 0, 1.5f));
        Assert.Equal(100, car.Health!.Slots[0]!.Hp);
        Assert.Empty(dm.Events);
    }

    /// <summary>The Nitro Pack's Piranha with caltrops (vppirna4.vcf) drops one; a car standing on it loses that tyre.</summary>
    [Fact]
    public void ACarOverADroppedCaltropBlowsThatTyre()
    {
        if (NitroDir is null) return;
        var vfs = new AssetVfs(new GameInstall(NitroDir));
        var world = new SimWorld(new Flat(), new CrtRand(3));
        var dropper = VehicleFactory.Create(vfs, "vppirna4.vcf");
        world.Vehicles.Add(dropper);
        world.Player = dropper;
        var effects = new Dictionary<string, EffectDef?>(StringComparer.OrdinalIgnoreCase);
        var combat = new Combat(world, n => effects.TryGetValue(n, out var e) ? e : effects[n] = vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var dm = new DamageModel(world, combat);
        dropper.Health = VehicleDamage.Create(dropper, dropper.Build!, true, world.Rand);
        var a = dropper.Armory!;
        var caltrops = a.Weapons.Single(w => w.Def.Kind == 0xe);
        Assert.Equal((6u, 20f, 0), (caltrops.Def.Class, caltrops.Def.Life, (int)caltrops.Def.Ordnance!.Mask));
        var keys = new bool[5];
        keys[a.Weapons.IndexOf(caltrops)] = true;
        const float dt = 102 / 2048f;
        a.PlayerTriggers(false, keys);
        combat.Frame(dt);
        var r = Assert.Single(combat.Rounds, x => x.Kind == 0xe);
        // it lies behind the car, on the ground
        Assert.True(r.Pos.Z < (float)dropper.Pos.Z - 1f, $"the caltrop lies at {r.Pos}");
        Assert.Equal(0f, r.Pos.Y, 3);

        // a car stands with its front left tyre over it
        var victim = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(victim);
        victim.Health = VehicleDamage.Create(victim, victim.Build!, false, world.Rand);
        var fl = victim.FrontLeft.Offset;
        victim.Pos = new Double3(r.Pos.X - fl.X, 0, r.Pos.Z - fl.Z);
        combat.Events.Clear();
        combat.Frame(dt);
        var wp = (WheelPart)victim.Health.Slots[1]!;
        Assert.Equal((25, 100), (wp.Hp, wp.Max));
        Assert.True(victim.FrontLeft.Blown);
        Assert.Contains(dm.Events, e => e is TyreBlown { Slot: 1 } t && t.Vehicle == victim);
        Assert.Contains(combat.Events, e => e is Impact { Kind: 0xe } i && i.Victim == victim && i.Sound.StartsWith("tcaltrop", StringComparison.OrdinalIgnoreCase));
        // the caltrop stays for its 20 s
        int frames = 2;
        while (combat.Rounds.Contains(r) && frames < 1000) { combat.Frame(dt); frames++; }
        Assert.InRange(frames * dt, 20f - 2 * dt, 20f + 2 * dt);
    }
}
