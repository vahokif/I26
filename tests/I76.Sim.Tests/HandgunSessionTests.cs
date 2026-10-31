using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The handgun kill through the whole session tick, on a real arena's ground (skipped without an install).</summary>
public class HandgunSessionTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>
    /// The player in the cockpit glances out of the right window (the arm rises) and holds fire at a
    /// melee opponent in the red (a damaged brake) kept abeam on m01's uneven ground: the side arc
    /// tips the .45 at it and the driver is killed (0x4020), not the car blown up.
    /// </summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(12f)]
    [InlineData(15f)]
    public void TheHandgunKillsTheDriverOfACarInTheRedAbeam(float dist)
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", melee: ["vppirna1.vcf"], seed: 7);
        setup.Melee?.Start();
        var s = new SimSession(setup);
        var me = setup.World.Player!;
        s.AddDriver(me);
        me.Armory!.AddHandguns(new Bwd2File(vfs.TryRead("vppirnha.vdf")!), true, true);
        var gun = me.Armory.Handguns[0]!;
        Vehicle? foe = null;
        VehicleKilled? killed = null;
        for (int t = 0; t < 600 && killed is null; t++)
        {
            if (t == 5)
            {
                foe = setup.World.Vehicles.First(v => v != me && !v.IsEmplacement);
                foe.Health!.Brake!.Hp = foe.Health.Brake.Max / 2;
            }
            if (foe is not null)
            {
                // held abeam of the right window (its height left to its own physics after the first placing)
                var p = me.Pos.ToVector3() + me.Rot.ToWorld(gun.MuzzlePos) + me.Rot.ToWorld(gun.BaseRot.Forward) * dist;
                foe.Pos = new I76.Sim.Math.Double3(p.X, t == 5 ? setup.World.Ground.BareHeight(p.X, p.Z) + 0.5 : foe.Pos.Y, p.Z);
                foe.Vel = default; foe.Speed = 0;
            }
            s.Tick([new CarInput { Buttons = CarButtons.Cockpit | CarButtons.RaiseRightArm | (t > 20 ? CarButtons.Fire : 0) }]);
            killed = setup.Damage.Events.OfType<VehicleKilled>().FirstOrDefault(k => k.Vehicle == foe);
            setup.Damage.Events.Clear();
            setup.Combat.Events.Clear();
            if (foe is not null && (t % 30 == 0 || killed is not null))
                log.WriteLine($"t {t}: foe {foe.Health!.HealthPercent():0.00}% flags 0x{(uint)foe.Flags:x}, its height {foe.Pos.Y - me.Pos.Y:+0.0;-0.0;0.0} m, side arc {(setup.Combat.TargetFor(me, 8) == foe ? "on it" : "-")}, .45 pitch {gun.GunPitch * 180 / MathF.PI:0.0} deg");
        }
        Assert.NotNull(killed);
        Assert.True(killed!.DriverKilled);
    }
}
