using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Game.Shell;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The Play Options the simulation reads (<see cref="ShellSettings.ApplyTo"/>), as the mission applies them at its start,
/// on resuming from the pad and over a restored checkpoint: the difficulty is the factor on the player's own rounds
/// (Wimp ×2, Champion ×1, Badass ×0.75), never in a network game.
/// </summary>
public class PlayOptionsSimTests
{
    private static (SimSession Session, MissionSetup Setup) A01(uint seed = 1)
    {
        var vfs = TestInstall.Vfs;
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, TestInstall.Install.Resolve("miss16", "a01.msn")!), asm, rand: new CrtRand(seed));
        var s = new SimSession(setup);
        s.AddDriver(setup.World.Player!);
        return (s, setup);
    }

    [InstallFact]
    public void TheDifficultyReachesTheSimAtTheStartAndAfterThePad()
    {
        var (_, setup) = A01();
        var c = setup.Combat;
        var player = setup.World.Player!;
        var settings = new ShellSettings();                  // Wimp, as the shipped player file
        settings.ApplyTo(c);
        Assert.Equal(0, c.Difficulty);
        Assert.Equal(2f, c.DifficultyFactor(player));
        settings.Difficulty = 2;                             // Badass on the pad, then resume
        settings.ApplyTo(c);
        Assert.Equal(0.75f, c.DifficultyFactor(player));
        settings.Difficulty = 1;
        settings.ApplyTo(c);
        Assert.Equal(1f, c.DifficultyFactor(player));
        // an AI car's rounds are never scaled
        Assert.Equal(1f, c.DifficultyFactor(setup.World.Vehicles.First(v => v != player)));
    }

    [InstallFact]
    public void ANetworkGamePlaysChampionWhateverTheSetting()
    {
        var (_, setup) = A01();
        var c = setup.Combat;
        c.NetSession = true;
        var settings = new ShellSettings { Difficulty = 0 };
        settings.ApplyTo(c);
        Assert.Equal(1, c.Difficulty);
        Assert.Equal(1f, c.DifficultyFactor(setup.World.Player!));
    }

    [InstallFact]
    public void OverARestoredCheckpointTheSettingAsItIsNowWins()
    {
        // the checkpoint taken at Badass
        var (s, setup) = A01();
        new ShellSettings { Difficulty = 2 }.ApplyTo(setup.Combat);
        for (int i = 0; i < 20; i++) s.Tick([new CarInput()]);
        var snap = s.Snapshot();
        // the retry: the player has gone to Wimp; the mission is rebuilt, the options applied, the checkpoint restored over it
        var now = new ShellSettings { Difficulty = 0 };
        var (retry, again) = A01(7);
        now.ApplyTo(again.Combat);
        retry.Restore(snap);
        Assert.Equal(2, again.Combat.Difficulty);            // the snapshot's copy
        now.ApplyTo(again.Combat);                           // and the options as they are now over it
        Assert.Equal(2f, again.Combat.DifficultyFactor(again.World.Player!));
    }
}
