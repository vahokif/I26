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

    [Fact]
    public void The_choices_start_with_none_then_the_default_then_the_devices()
        => Assert.Equal(["", "Default", "USB mic", "Headset"], MicNames.Choices(["Default", "USB mic", "Headset"]));

    [Fact]
    public void None_is_the_default_and_never_opens_the_input()
    {
        Assert.Equal("", new ShellSettings().CbMic);
        Assert.Equal("None", MicNames.Of(""));
        Assert.False(MicNames.OpensInput(""));
        Assert.True(MicNames.OpensInput("Default"));
        Assert.True(MicNames.OpensInput("USB mic"));
    }

    [Fact]
    public void The_project_leaves_the_audio_input_off_until_a_device_is_chosen()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "project.godot"))) dir = Path.GetDirectoryName(dir)!;
        Assert.Contains("driver/enable_input=false", File.ReadAllText(Path.Combine(dir!, "game", "project.godot")));
    }
}
