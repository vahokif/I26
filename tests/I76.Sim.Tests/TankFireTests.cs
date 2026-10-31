using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The player's tank (vstankf.vcf, picked on CHOOSE YOUR VEHICLE before T17) arrives armed and fires in T17 (skipped without an install).</summary>
public class TankFireTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The pick goes through the trip inventory (vehscn.vcf): all three WEPN entries, the cannon included, come back out of it.</summary>
    [Fact]
    public void ThePickedTankKeepsItsThreeGunsThroughTheTripInventory()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var car = TripCar.FromVcf(vfs.Read("vstankf.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        var inv = TripInventory.FromCar(car, PartsDatabase.Build(vfs), vfs);
        var vcf = new Bwd2File(inv.ToCar().ToVcf());
        var a = Armory.Create(vfs, vcf, new Bwd2File(vfs.Read("vstank.vdf")));
        Assert.Equal(["30mm Cannon", "Tank Cannon", "50cal MG"], a.Weapons.Select(w => w.Def.Name));
    }

    /// <summary>Once T17's opening cut-scene lets go, the fire key works every gun and the hardpoint keys each their own, the cannon every two seconds.</summary>
    [Fact]
    public void TheTankFiresItsGunsInT17AfterTheIntro()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "t17.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vstankf.vcf", seed: 7);
        var s = new SimSession(setup);
        var me = setup.World.Player!;
        s.AddDriver(me);
        var fired = new HashSet<string>();
        for (int t = 0; t < 1500; t++)
        {
            s.Tick([new CarInput { Buttons = CarButtons.Hardpoint1 | CarButtons.Hardpoint2 | CarButtons.Hardpoint3 }]);
            if (!s.Cutscene) foreach (var e in setup.Combat.Events.OfType<ShotFired>().Where(e => e.Shooter == me)) fired.Add(e.Weapon.Def.Name);
            setup.Combat.Events.Clear(); setup.Damage.Events.Clear();
        }
        Assert.False(s.Cutscene);
        Assert.Equal(["30mm Cannon", "50cal MG", "Tank Cannon"], fired.Order());
    }
}
