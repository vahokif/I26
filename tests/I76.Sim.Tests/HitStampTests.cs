using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// isShot / isRammed: the original stamps a hit with its frame counter, which it advances right after the script
/// machines, and the actions are true when the stamp is this frame's counter or the one before. A hit recorded between
/// two frames' machines (the collisions, the rounds) is therefore seen by two frames' machines, and road_ram's
/// collision rule (the stamp equal to the counter) by the first only.
/// </summary>
public class HitStampTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void AHitBetweenFramesIsSeenByTheNextTwoFramesMachines()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t01.msn")!), asm, "vppirna1.vcf");
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        var script = setup.Script!;
        var host = script.Host;
        var vm = script.Vm!;
        for (int i = 0; i < 30; i++) s.Tick([new CarInput()]);
        var shooter = host.Player!;
        var victims = host.Entities.Where(e => e != shooter && e.Vehicle is not null && !e.Dead).ToList();

        // what the next frame's machines see as the counter, before that frame runs
        int next = vm.TickCount;
        Assert.Equal(next, host.FsmTick);
        foreach (var v in victims) { host.RecordDamage(v, shooter, ScriptHost.Hit.Shot); host.RecordDamage(v, shooter, ScriptHost.Hit.Ram); }
        Assert.All(victims, v => Assert.Equal((next, next), (v.ShotTick, v.RammedTick)));
        // frame k's machines do not read it (those that do consume it); frame k + 1's still see it (its counter is next + 1)
        s.Tick([new CarInput()]);
        Assert.Equal(next + 1, vm.TickCount);
        Assert.Equal(vm.TickCount, host.FsmTick);
        var unread = victims.Where(v => v.ShotTick == next && v.RammedTick == next).ToList();
        Assert.NotEmpty(unread);
        foreach (var v in unread)
        {
            Assert.True(host.ConsumeShot(v));
            Assert.False(host.ConsumeShot(v));   // consumed
            Assert.True(host.ConsumeRammed(v));
        }
    }
}
