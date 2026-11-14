using I76.Game;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The engine loop's pitch follows the original's frequency law in absolute Hz.</summary>
public class EngineSoundPitchTests
{
    [Fact] public void IdleIsThreeQuartersOfAnElevenKiloHertzLoop() =>
        Assert.Equal(0.75f, EngineSoundPitch.Scale(1050f, 11025f), 4);

    [Fact] public void At2294RpmTheScaleIs0938() =>
        Assert.Equal(0.938f, EngineSoundPitch.Scale(2294f, 11025f), 3);

    [Fact] public void FreeRevCapIsOneAndAHalf() =>
        Assert.Equal(1.5f, EngineSoundPitch.Scale(6000f, 11025f), 4);

    [Fact] public void TheFrequencyIsAbsoluteNotRelativeToTheFilesRate() =>
        Assert.Equal(8268.75f / 22050f, EngineSoundPitch.Scale(1050f, 22050f), 5);

    /// <summary>A restored state decides the loop both ways: a car running again (brought back, or woken from hide) sounds again.</summary>
    [Theory]
    [InlineData(0x1001u, false, true)]      // running
    [InlineData(0x1000u, false, false)]     // engine off (hide, the S key)
    [InlineData(0x1021u, false, false)]     // dead
    [InlineData(0x80001008u, false, false)] // cranking: the loop starts at the ignition's end
    [InlineData(0x1001u, true, false)]      // a gun emplacement has no engine
    public void TheLoopRunsWhileTheEngineIsOnAndTheCarAlive(uint flags, bool emplacement, bool runs) =>
        Assert.Equal(runs, EngineSoundPitch.LoopRuns(flags, emplacement));
}
