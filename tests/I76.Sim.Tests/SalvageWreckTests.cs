using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Rng;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// What a wreck yields to field salvage, record by record as the original exports it: a fitted special at amount 1
/// whatever its health, and a part the tier table does not know ends the wreck's collection there.
/// </summary>
public class SalvageWreckTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static Vehicle Wreck(AssetVfs vfs, string vcf)
    {
        var v = VehicleFactory.Create(vfs, vcf);
        v.Health = VehicleDamage.Create(v, v.Build!, false, new CrtRand(1));
        return v;
    }

    [Fact]
    public void AFittedSpecialComesAtOneEvenWhenItsHealthIsGone()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var car = vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => { try { return Wreck(vfs, n); } catch { return null; } })
            .FirstOrDefault(v => v?.Health is { } d && v.Loadout is not null && d.Slots[17] is SpecialPart
                && Enumerable.Range(10, 7).All(k => d.Slots[k] is not WeaponPart wp || SalvagePool.Clamp(wp.Weapon.Def.File.ToLowerInvariant(), 1, 17, 2) is not null));
        Assert.NotNull(car);
        var specials = Enumerable.Range(17, 3).Select(k => car!.Health!.Slots[k]).OfType<SpecialPart>().ToList();
        specials[0].Hp = 0;
        if (specials.Count > 1) specials[1].Hp = 7;
        var pool = new SalvagePool("t17");
        pool.AddDeadCar(car!);
        var got = pool.Items.Where(i => i.Name.StartsWith("spc")).ToList();
        Assert.Equal(specials.Count, got.Count);
        Assert.All(got, i => Assert.Equal(1, i.Health));
    }

    [Theory]
    [InlineData("vstank1.vcf")]
    [InlineData("vstank2.vcf")]
    [InlineData("vxhowit1.vcf")]
    public void AnUntabledWeaponEndsTheWrecksCollection(string vcf)
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var v = Wreck(vfs, vcf);
        var d = v.Health!;
        int first = Enumerable.Range(10, 7).First(k => d.Slots[k] is WeaponPart wp && SalvagePool.Clamp(wp.Weapon.Def.File.ToLowerInvariant(), 1, 17, 2) is null);
        var pool = new SalvagePool("t16");
        pool.AddDeadCar(v);
        var weapons = pool.Items.Where(i => i.Name.EndsWith(".gdf")).Select(i => i.Name).ToList();
        // the weapons before it are collected, none from it on, and no special
        Assert.Equal(Enumerable.Range(10, first - 10).Count(k => d.Slots[k] is WeaponPart { Hp: > 0 }), weapons.Count);
        Assert.DoesNotContain(pool.Items, i => i.Name.StartsWith("spc"));
        Assert.Contains(pool.Items, i => i.Name.StartsWith("eng"));     // records 0-9 come first
    }
}
