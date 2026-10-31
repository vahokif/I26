using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Issue #43's missions: no car drives before its script wakes it, fires while asleep, or shows on the radar while
/// hidden (WakeAudit). Measured in the original games: S02's sitting enemies are radar contacts at 433 m before the cut-scene (engine on, as here); P19's limo
/// and convoy stay put until the player nears the T junction; P18's tanks wake when the script's phase reaches 4.
/// </summary>
public class WakeAuditTests
{
    private static string? Dir(string v) => Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static WakeAudit Run(string dir, string mission, float seconds)
    {
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission(mission)!), asm, rand: new I76.Sim.Rng.CrtRand(1));
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        var audit = new WakeAudit(setup);
        var input = new CarInput();
        for (float t = 0; t < seconds; t += s.TickSeconds)
        {
            s.Tick([input]);
            audit.Tick(setup.Script!.Host.Now, s.TickSeconds);
            setup.Combat.Events.Clear();
        }
        return audit;
    }

    [Theory]
    [InlineData("s02")]
    [InlineData("t06")]
    public void I76CarsSleepUntilTheirScriptWakesThem(string mission)
    {
        if (Dir("I76_INSTALL") is not { } dir) return;
        Assert.Empty(Run(dir, mission, 90).Findings.Select(c => c.Entity.Name));
    }

    [Theory]
    [InlineData("p18")]
    [InlineData("p19")]
    public void NitroCarsSleepUntilTheirScriptWakesThem(string mission)
    {
        if (Dir("I76_NITRO_INSTALL") is not { } dir) return;
        var audit = Run(dir, mission, 90);
        Assert.Empty(audit.Findings.Select(c => c.Entity.Name));
        if (mission == "p19")
            // the limo (p19jd01) and its police convoy wait for the player at the T junction: with nobody driving, never
            Assert.All(audit.Cars.Where(c => c.Entity.Name is "p19jd01" or "vcpolic2"), c => Assert.Null(c.Woke));
    }
}
