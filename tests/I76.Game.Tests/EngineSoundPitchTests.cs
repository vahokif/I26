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
}
