using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// start_engine (S): a one-tick request the car's state step takes and clears. With the engine off and not cranking it
/// starts the ignition, which runs the engine when its sound's time is up; with the engine on it stops it at once;
/// during the cranking it does nothing.
/// </summary>
public class IgnitionKeyTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void EachPressTogglesTheEngineOnceAndCrankingIgnoresIt()
    {
        if (InstallDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("m04")!), asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), melee: new List<string>(), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup);
        var v = setup.World.Player!;
        s.AddDriver(v);
        var events = new List<object>();
        void Tick(CarCommands c)
        {
            s.Tick([new CarInput { Commands = c }]);
            events.AddRange(setup.Damage!.Events);
            setup.Damage.Events.Clear();
        }
        for (int i = 0; i < 10; i++) Tick(0);
        Assert.NotEqual(0, (int)(v.Flags & VehicleFlags.EngineOn));          // a melee car starts running

        Tick(CarCommands.StartEngine);                                        // on: off at once
        Assert.Equal(0, v.StartEngineKey);                                    // taken and cleared within the tick
        Assert.Equal(0, (int)(v.Flags & VehicleFlags.EngineOn));
        Assert.Contains(events, e => e is EngineStopped);
        for (int i = 0; i < 10; i++) Tick(0);
        Assert.Equal(0, (int)(v.Flags & VehicleFlags.EngineOn));             // no key: it stays off
        Assert.Empty(events.OfType<IgnitionStarted>());

        Tick(CarCommands.StartEngine);                                        // off: the ignition
        Assert.Single(events.OfType<IgnitionStarted>());
        Assert.NotEqual(0, (int)(v.Flags & VehicleFlags.Started));
        Assert.Equal(0, (int)(v.Flags & VehicleFlags.EngineOn));
        Tick(CarCommands.StartEngine);                                        // while cranking: nothing
        Assert.Single(events.OfType<IgnitionStarted>());
        int ticks = 0;
        while ((v.Flags & VehicleFlags.EngineOn) == 0 && ticks++ < 200) Tick(0);
        Assert.InRange(ticks, 2, 199);                                        // the ignition sound's time
        Assert.Contains(events, e => e is EngineStarted);
        for (int i = 0; i < 10; i++) Tick(0);
        Assert.NotEqual(0, (int)(v.Flags & VehicleFlags.EngineOn));
    }
}
