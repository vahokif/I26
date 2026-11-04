using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The specials that act once, at the car's post-load: Heated Seats (rounds ×1.1 in every build), and in the Nitro Pack
/// only Dashboard Mary (damage per shot ×1.1) and Tire Covers (wheel HP and maximum ×2). Measured in the original:
/// t13cc03, the one stock car with Heated Seats the player can be given, has 275 rounds in each 30mm Cannon and 44 in
/// its EZK Mortar (the gizmo files say 250 and 40).
/// </summary>
public class LoadSpecialsTests
{
    private static AssetVfs? Vfs(string? dir) => dir is null ? null : new AssetVfs(new GameInstall(dir));

    /// <summary>A copy of <paramref name="vcf"/> under a new name whose SPEC chunks are <paramref name="types"/>, in place of its own.</summary>
    private static string WithSpecials(AssetVfs vfs, string vcf, string tag, params int[] types)
    {
        var src = vfs.Read(vcf);
        var specs = new Bwd2File(src).All("SPEC").ToList();
        Assert.NotEmpty(specs);
        var own = specs[^1].Offset + specs[^1].Size;
        var spec = types.SelectMany(t => "SPEC"u8.ToArray().Concat(BitConverter.GetBytes(12)).Concat(BitConverter.GetBytes(t)));
        var bytes = src[..specs[0].Offset].Concat(spec).Concat(src[own..]).ToArray();
        Assert.Equal(types, new Bwd2File(bytes).All("SPEC").Select(c => Bin.I32(bytes, c.PayloadOffset)));
        var name = $"zspec{tag}{(vfs.Install.IsNitro ? "n" : "i")}{string.Concat(types)}{vcf}";
        AssetVfs.Addon[name] = bytes;
        return name;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(9, 9)]
    [InlineData(10, 11)]
    [InlineData(15, 16)]
    [InlineData(25, 27)]
    [InlineData(40, 44)]
    [InlineData(150, 165)]
    [InlineData(250, 275)]
    [InlineData(275, 302)]
    public void ATenthMoreIsRoundedDown(int x, int expected) => Assert.Equal(expected, Specials.PlusTenth(x));

    [Fact]
    public void HeatedSeatsGiveT13cc03TheRoundsMeasuredInTheGame()
    {
        if (Vfs(SnapshotRig.Install) is not { } vfs) return;
        var v = VehicleFactory.Create(vfs, "t13cc03.vcf");
        Assert.Equal(8, v.Build!.Specials[0]);
        var guns = v.Armory!.LoadOrder;
        Assert.Equal([275, 275, 44], guns.Select(w => w.Ammo).Order().Reverse());
        Assert.Equal([250, 250, 40], guns.Select(w => (int)w.Def.Ammo).Order().Reverse());
        foreach (var w in guns)
        {
            Assert.Equal((int)w.Def.HitPoints, w.Hp);       // HP as it was
            Assert.Equal((int)w.Def.Damage, w.Damage);     // the base game has no Dashboard Mary
        }
        // the player's damage state is built from it as usual: the special part is there, the weapons' HP not rescaled
        var d = VehicleDamage.Create(v, v.Build, true, CrtRand.Oracle([1, 2, 3]));
        Assert.IsType<SpecialPart>(d.Slots[17]);
        Assert.All(guns, w => Assert.Equal((int)w.Def.HitPoints, w.Hp));
    }

    [Fact]
    public void TwoHeatedSeatsApplyTwiceEachRoundedDown()
    {
        if (Vfs(SnapshotRig.Install) is not { } vfs) return;
        var v = VehicleFactory.Create(vfs, WithSpecials(vfs, "t13cc03.vcf", "two", 8, 8));
        // 250 → 275 → 302 (302.5), 40 → 44 → 48 (48.4): ×1.21 with a truncation at each step
        Assert.Equal([302, 302, 48], v.Armory!.LoadOrder.Select(w => w.Ammo).Order().Reverse());
        var none = VehicleFactory.Create(vfs, WithSpecials(vfs, "t13cc03.vcf", "none", 0));
        Assert.Equal([250, 250, 40], none.Armory!.LoadOrder.Select(w => w.Ammo).Order().Reverse());
    }

    [Fact]
    public void HeatedSeatsAreTheSameInTheNitroPack()
    {
        if (Vfs(SnapshotRig.NitroInstall) is not { } vfs || vfs.TryRead("t13cc03.vcf") is null) return;
        var v = VehicleFactory.Create(vfs, "t13cc03.vcf");
        Assert.All(v.Armory!.LoadOrder, w => Assert.Equal(Specials.PlusTenth((int)w.Def.Ammo), w.Ammo));
    }

    [Fact]
    public void AMeleeOpponentHasNoSpecialsAndNoneOfTheirLoadTimeEffects()
    {
        if (Vfs(SnapshotRig.Install) is not { } vfs) return;
        var name = WithSpecials(vfs, "t13cc03.vcf", "opp", 8, 3);
        var own = VehicleFactory.Create(vfs, name);
        Assert.Equal(1.25f, own.P.EngineScale);
        var opponent = VehicleFactory.Create(vfs, name, meleeBrakes: true, specials: false);
        Assert.Empty(opponent.Build!.Specials);
        Assert.Equal(1f, opponent.P.EngineScale);
        Assert.All(opponent.Armory!.LoadOrder, w => Assert.Equal((int)w.Def.Ammo, w.Ammo));
    }

    [Fact]
    public void DashboardMaryAddsATenthToEachShotInTheNitroPackOnly()
    {
        foreach (var (dir, nitro) in new[] { (SnapshotRig.Install, false), (SnapshotRig.NitroInstall, true) })
        {
            if (Vfs(dir) is not { } vfs) continue;
            var v = VehicleFactory.Create(vfs, WithSpecials(vfs, "t13cc03.vcf", "mary", 10));
            var guns = v.Armory!.LoadOrder;
            Assert.NotEmpty(guns);
            foreach (var w in guns)
            {
                int dmg = (int)w.Def.Damage;
                Assert.Equal(nitro ? Specials.PlusTenth(dmg) : dmg, w.Damage);
                Assert.Equal((int)w.Def.Ammo, w.Ammo);
                Assert.Equal((int)w.Def.HitPoints, w.Hp);
            }
            if (nitro) Assert.Contains(guns, w => w.Damage > w.Def.Damage);
        }
    }

    [Fact]
    public void TireCoversDoubleEveryWheelInTheNitroPackOnly()
    {
        foreach (var (dir, nitro) in new[] { (SnapshotRig.Install, false), (SnapshotRig.NitroInstall, true) })
        {
            if (Vfs(dir) is not { } vfs) continue;
            var plain = VehicleFactory.Create(vfs, WithSpecials(vfs, "t13cc03.vcf", "tyre", 0));
            var covered = VehicleFactory.Create(vfs, WithSpecials(vfs, "t13cc03.vcf", "tyre", 11));
            foreach (bool player in new[] { false, true })
            {
                var a = VehicleDamage.Create(plain, plain.Build!, player, CrtRand.Oracle([1, 2, 3]));
                var b = VehicleDamage.Create(covered, covered.Build!, player, CrtRand.Oracle([1, 2, 3]));
                for (int k = 0; k < 6; k++)
                {
                    if (a.Slots[k] is not WheelPart wa) { Assert.Null(b.Slots[k]); continue; }
                    var wb = (WheelPart)b.Slots[k]!;
                    Assert.Equal(nitro ? 2 * wa.Max : wa.Max, wb.Max);
                    Assert.Equal(nitro ? 2 * wa.Hp : wa.Hp, wb.Hp);
                }
                // nothing else changes
                Assert.Equal(a.Engine!.Max, b.Engine!.Max);
            }
            Assert.Equal(plain.Armory!.LoadOrder.Select(w => w.Damage), covered.Armory!.LoadOrder.Select(w => w.Damage));
        }
    }

    /// <summary>
    /// A trip mission: the shell writes −1 for every weapon's rounds, so the car starts with the loaded rounds and the
    /// bonus, mission after mission (no compounding). Carried rounds (the engine's own record) are read before the
    /// post-load, so they get the bonus too.
    /// </summary>
    [Fact]
    public void ATripCarGetsTheBonusOnTheRoundsItStartsWith()
    {
        if (Vfs(SnapshotRig.Install) is not { } vfs) return;
        var v = VehicleFactory.Create(vfs, "t13cc03.vcf");
        v.Health = VehicleDamage.Create(v, v.Build!, true, CrtRand.Oracle([1, 2, 3]));
        var guns = v.Armory!.LoadOrder;
        var shell = new CarState(300, 150, 200, [100, 100, -1, -1, 100, 100], [.. guns.Select(w => ((int)w.Hardpoint.Id, 100, -1))]);
        shell.Apply(v);
        Assert.Equal([275, 275, 44], guns.Select(w => w.Ammo).Order().Reverse());
        Assert.All(guns, w => Assert.Equal(100, w.Hp));
        var carried = new CarState(300, 150, 200, [100, 100, -1, -1, 100, 100], [.. guns.Select(w => ((int)w.Hardpoint.Id, 100, 30))]);
        carried.Apply(v);
        Assert.All(guns, w => Assert.Equal(33, w.Ammo));
        // the shell's own trip state never carries rounds
        Assert.All(CarState.Capture(v).Weapons, x => Assert.Equal(33, x.Ammo));
    }

    /// <summary>A savestate holds the boosted numbers; restoring it into a freshly loaded mission does not add the bonus again.</summary>
    [Fact]
    public void ARestoreDoesNotApplyTheBonusAgain()
    {
        var ran = new List<bool>();
        foreach (var dir in new[] { SnapshotRig.Install, SnapshotRig.NitroInstall })
        {
            if (dir is null) continue;
            var l = new SnapshotRig.Loaded(dir);
            if (l.Vfs.TryRead("t13cc03.vcf") is null) continue;
            ran.Add(l.Vfs.Install.IsNitro);
            string mission = l.Vfs.Install.IsNitro ? SnapshotRig.Missions(dir).First() : "t01";
            var car = WithSpecials(l.Vfs, "t13cc03.vcf", "save", 8, 10, 11);
            var b = SnapshotRig.BuildOf(dir, mission, car: car);
            var (s, _) = SnapshotRig.Make(l, b, SimRate.R20, 5);
            var p = s.World.Player!;
            var ammo = p.Armory!.LoadOrder.Select(w => w.Ammo).ToList();
            var dmg = p.Armory.LoadOrder.Select(w => w.Damage).ToList();
            var wheels = p.Health!.Slots.Take(6).OfType<WheelPart>().Select(w => (w.Hp, w.Max)).ToList();
            Assert.Equal(p.Armory.LoadOrder.Select(w => Specials.PlusTenth((int)w.Def.Ammo)), ammo);
            Assert.Equal(p.Armory.LoadOrder.Select(w => l.Vfs.Install.IsNitro ? Specials.PlusTenth((int)w.Def.Damage) : (int)w.Def.Damage), dmg);
            for (int i = 0; i < 5; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); }
            p.Armory.LoadOrder[0].Ammo -= 7;
            var snap = s.Snapshot();
            var (fresh, _) = SnapshotRig.Make(l, b, SimRate.R20, 9);
            fresh.Restore(snap);
            var q = fresh.World.Player!;
            Assert.Equal(ammo[0] - 7, q.Armory!.LoadOrder[0].Ammo);
            Assert.Equal(ammo.Skip(1), q.Armory.LoadOrder.Skip(1).Select(w => w.Ammo));
            Assert.Equal(dmg, q.Armory.LoadOrder.Select(w => w.Damage));
            Assert.Equal(wheels, q.Health!.Slots.Take(6).OfType<WheelPart>().Select(w => (w.Hp, w.Max)));
        }
        Assert.Equal(new[] { SnapshotRig.Install, SnapshotRig.NitroInstall }.Count(d => d is not null), ran.Count);
    }
}
