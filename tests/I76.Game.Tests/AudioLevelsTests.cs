using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The SFX / Voice slider's law: 4 dB per halving, level 8 held at 0 dB.</summary>
public class AudioLevelsTests
{
    [Fact] public void Level8StaysAtZero() => Assert.Equal(0f, AudioLevels.EffectDb(8), 3);

    [Fact] public void StepsRelativeToLevel10MatchTheOriginal()
    {
        float ten = AudioLevels.EffectDb(10);
        Assert.Equal(-1.29f, AudioLevels.EffectDb(8) - ten, 2);
        Assert.Equal(-4f, AudioLevels.EffectDb(5) - ten, 2);
        Assert.Equal(-9.29f, AudioLevels.EffectDb(2) - ten, 2);
    }

    [Fact] public void ADoublingIsFourDecibels() => Assert.Equal(4f, AudioLevels.EffectDb(10) - AudioLevels.EffectDb(5), 3);
}
