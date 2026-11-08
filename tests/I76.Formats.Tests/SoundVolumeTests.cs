using I76.Formats.Audio;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The GAS0 header's volume slot (int32 at offset 16, -1 = 100 percent) and its decibel law.</summary>
public class SoundVolumeTests
{
    private static byte[] Header(int volume)
    {
        var d = new byte[40];
        "GAS0"u8.CopyTo(d);
        BitConverter.GetBytes(60).CopyTo(d, 4);
        BitConverter.GetBytes(2).CopyTo(d, 8);
        BitConverter.GetBytes(1).CopyTo(d, 12);
        BitConverter.GetBytes(volume).CopyTo(d, 16);
        BitConverter.GetBytes(-1).CopyTo(d, 20);
        return d;
    }

    [Fact] public void TheVolumeSlotIsRead() => Assert.Equal(60, SoundFiles.Volume(Header(60)));
    [Fact] public void MinusOneMeansFullVolume() => Assert.Equal(100, SoundFiles.Volume(Header(-1)));
    [Fact] public void AFileWithoutAHeaderIsFullVolume() => Assert.Equal(100, SoundFiles.Volume("RIFF....WAVEfmt "u8.ToArray().Concat(new byte[20]).ToArray()));

    [Fact] public void FourDecibelsPerHalving()
    {
        Assert.Equal(0f, SoundFiles.VolumeDb(100), 3);
        Assert.Equal(-4f, SoundFiles.VolumeDb(50), 3);
        Assert.Equal(-2.948f, SoundFiles.VolumeDb(60), 3);
    }

    [InstallFact]
    public void EimarxEngineAsksForSixtyPercent()
    {
        Assert.Equal(60, SoundFiles.Volume(TestInstall.Vfs, "eimarx.wav"));
        Assert.Equal(100, SoundFiles.Volume(TestInstall.Vfs, "nosuchsound.wav"));
    }
}
