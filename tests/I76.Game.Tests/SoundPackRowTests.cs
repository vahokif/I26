using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Audio Control's "HD Sounds" row: there only when a sound pack is installed, and on until the player turns it off.</summary>
public class SoundPackRowTests
{
    [Fact]
    public void TheRowIsHiddenWithoutAPack()
    {
        Assert.DoesNotContain(AudioControlRows.HdSounds, AudioControlRows.Labels(soundPack: false));
        Assert.Equal([AudioControlRows.Mic, AudioControlRows.HdSounds], AudioControlRows.Labels(soundPack: true));
    }

    [Fact]
    public void ThePacksSoundsAreOnByDefault() => Assert.True(new ShellSettings().HdSounds);
}
