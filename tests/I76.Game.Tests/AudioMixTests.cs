using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The in-game bus layout: the game's sounds limited on Mix, the driver's cues joining after that limiter.</summary>
public class AudioMixTests
{
    [Fact] public void EveryBusSendsToOneMadeBeforeIt()
    {
        var made = new List<string> { AudioMix.Master };
        foreach (var (name, send) in AudioMix.Buses)
        {
            Assert.Contains(send, made);   // Godot mixes a bus into its send only when the send comes earlier
            made.Add(name);
        }
    }

    [Fact] public void TheGameSoundsMeetOnMix()
    {
        foreach (var bus in new[] { AudioMix.Music, AudioMix.Sfx, AudioMix.Voice })
            Assert.Equal(AudioMix.Mix, AudioMix.Buses.Single(b => b.Name == bus).Send);
    }

    [Fact] public void TheCuesSkipMixsLimiter() => Assert.Equal(AudioMix.Master, AudioMix.Buses.Single(b => b.Name == AudioMix.Cues).Send);

    [Fact] public void MasterStaysTheLastGuard() => Assert.True(AudioMix.MixCeilingDb <= AudioMix.MasterCeilingDb && AudioMix.MasterCeilingDb < 0);

    [Theory]
    [InlineData("clockon.wav"), InlineData("CLOCKON.WAV"), InlineData("cammo.wav"), InlineData("msllock1.wav"), InlineData("msllock2.wav"),
     InlineData("msllock3.wav"), InlineData("cradar.wav"), InlineData("cgrowl.wav")]
    public void DriverCuesGoOnCues(string sound) => Assert.Equal(AudioMix.Cues, AudioMix.BusFor(sound));

    [Theory]
    [InlineData("vshif1a.wav"), InlineData("cwstat.wav"), InlineData("wmiss.wav"), InlineData("cjamm.wav"), InlineData("vland.wav")]
    public void OtherSoundsStayOnSfx(string sound) => Assert.Equal(AudioMix.Sfx, AudioMix.BusFor(sound));
}
