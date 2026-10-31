using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

public class MicNamesTests
{
    [Theory]
    [InlineData("alsa_input.usb-Blue_Microphones_Yeti_Stereo_Microphone_REV8-00.analog-stereo", "Blue Microphones Yeti Stereo Microphone REV8")]
    [InlineData("alsa_input.pci-0000_00_1f.3.analog-stereo", "Built-in audio")]
    [InlineData("alsa_input.usb-046d_HD_Pro_Webcam_C920_ABCD1234-02.iec958-stereo", "046d HD Pro Webcam C920 ABCD1234 (iec958 stereo)")]
    [InlineData("Headset Microphone", "Headset Microphone")]
    public void A_source_name_reads_as_the_device(string name, string shown) => Assert.Equal(shown, MicNames.Tidy(name));
}
