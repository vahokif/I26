using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Audio Control's "HiFi Audio" row (the front end's and the mission menu's): there only when a pack is installed, and on until the player turns it off.</summary>
public class HiFiAudioRowTests
{
    [Fact]
    public void TheRowIsHiddenWithoutAPack()
    {
        Assert.DoesNotContain(AudioControlRows.HiFiAudio, AudioControlRows.Labels(pack: false));
        Assert.Equal([AudioControlRows.Mic, AudioControlRows.HiFiAudio], AudioControlRows.Labels(pack: true));
        Assert.Equal("HiFi Audio", AudioControlRows.HiFiAudio);
    }

    [Fact]
    public void ThePacksAudioIsOnByDefault() => Assert.True(new ShellSettings().HiFiAudio);
}
