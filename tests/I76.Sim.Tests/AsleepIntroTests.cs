using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>T12's intro: Groove asleep, the car's engine off. The hide order's enter stops the idle loop too.</summary>
public class AsleepIntroTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void T12StartsWithThePlayersEngineOffAndItsLoopStopped()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t12.msn")!), asm);
        var s = new SimSession(setup);
        var p = setup.World.Player!;
        s.AddDriver(p);
        bool stopped = false;
        var input = new CarInput();
        for (int i = 0; i < 40; i++)
        {
            s.Tick([input]);
            stopped |= setup.Damage.Events.OfType<EngineStopped>().Any(e => e.Vehicle == p);
            setup.Damage.Events.Clear();
        }
        Assert.True(stopped);
        Assert.Equal(0, (int)(p.Flags & VehicleFlags.EngineOn));
    }
}
