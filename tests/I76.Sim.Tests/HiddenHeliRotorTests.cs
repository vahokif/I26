using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The Nitro Pack's P13: the Huey waits hidden until 90 s after the first target falls, then lifts off and hunts the
/// player. The original's rotor loop runs from the load: hide clears the engine flag but leaves the loop playing, and
/// startCar's ignition never ends for a helicopter, so a loop stopped at hide would never come back (a silent Huey).
/// </summary>
public class HiddenHeliRotorTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void P13sHiddenHueyKeepsItsRotorLoop()
    {
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "p13.msn")!), asm);
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        var heli = setup.World.Vehicles.Single(v => v.IsHeli);
        bool stopped = false;
        var input = new CarInput();
        for (int i = 0; i < 40; i++)
        {
            s.Tick([input]);
            stopped |= setup.Damage.Events.OfType<EngineStopped>().Any(e => e.Vehicle == heli);
            setup.Damage.Events.Clear();
        }
        Assert.False(stopped);
        // hidden, as the original's 0x81 became 0x80: the flag goes, the loop stays
        Assert.Equal(0, (int)(heli.Flags & I76.Sim.Vehicles.VehicleFlags.EngineOn));
    }
}
