using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Nitro's hidden turrets, the emplacements' guns (vxsndbg1/2, vxhowit1): the 12.5mm MG and Light m/g "with gunner" (a
/// figure standing at a gun on a pole, G1AT*.geo) and the Howitzer. On the Rattler's turret mount (where the all-weapon
/// shell put them) each is built, fires its own rounds and spends them, and the Howitzer's shell lands and bursts.
/// </summary>
public class GunnerWeaponTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Theory]
    [InlineData("tm125mg.gdf", "12.5mm MG with g")]
    [InlineData("tm125lmg.gdf", "Light m/g with g")]
    [InlineData("tthowitz.gdf", "Howitzer")]
    public void TheHiddenTurretFiresFromTheRattler(string gdf, string name)
    {
        if (NitroDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var car = TripCar.FromVcf(vfs.Read("vfratlr1.vcf"));
        int k = car.Weapons.FindIndex(w => w.Gdf.Equals("tmlight.gdf", StringComparison.OrdinalIgnoreCase));
        Assert.True(k >= 0);
        car.Weapons[k] = (car.Weapons[k].Hardpoint, gdf);
        var vcfName = "zgunner" + gdf[..^4] + ".vcf";
        AssetVfs.Addon[vcfName] = car.ToVcf();
        try
        {
            Assert.True(HiddenContent.IsComplete(gdf));
            var asm = new ModelAssembler(vfs);
            var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "n01.cbt")!);
            var setup = MissionSetup.Build(vfs, w, asm, vcfName, seed: 3);
            var s = new SimSession(setup);
            var me = setup.World.Player!;
            s.AddDriver(me);
            var gun = me.Armory!.Weapons.Single(x => x.Def.Name == name);
            int before = gun.Ammo;
            bool fired = false, burst = false;
            for (int t = 0; t < 400; t++)
            {
                s.Tick([new CarInput { Buttons = CarButtons.Fire }]);
                fired |= setup.Combat.Events.OfType<ShotFired>().Any(e => e.Shooter == me && e.Weapon == gun);
                burst |= setup.Combat.Events.OfType<Impact>().Any() || setup.Combat.Events.OfType<Explosion>().Any();
                setup.Combat.Events.Clear(); setup.Damage.Events.Clear();
            }
            Assert.True(fired);
            Assert.True(gun.Ammo < before);
            if (gdf == "tthowitz.gdf") Assert.True(burst);      // a bullet into open desert may hit nothing; the shell comes down
        }
        finally { AssetVfs.Addon.TryRemove(vcfName, out _); }
    }
}
