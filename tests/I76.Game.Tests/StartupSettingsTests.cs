using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Log and Renderer rows write one override file for Godot's next start; Auto with the log off is no file.</summary>
public class StartupSettingsTests
{
    [Fact]
    public void AutoAndLogOffIsNoFile() => Assert.Equal("", StartupSettings.Text(false, RendererChoice.Auto, true));

    [Fact]
    public void OpenGLAsksForTheCompatibilityRenderer()
    {
        string t = StartupSettings.Text(false, RendererChoice.OpenGL, false);
        Assert.Contains("rendering_method=\"gl_compatibility\"", t);
        Assert.DoesNotContain("file_logging", t);
    }

    [Fact]
    public void VulkanNamesTheDriverOnBothDesktops()
    {
        string t = StartupSettings.Text(false, RendererChoice.Vulkan, true);
        Assert.Contains("driver.windows=\"vulkan\"", t);
        Assert.Contains("driver.linuxbsd=\"vulkan\"", t);
    }

    [Fact]
    public void D3D12IsOnlyWrittenWhereItIsOffered()
    {
        Assert.Contains("driver.windows=\"d3d12\"", StartupSettings.Text(false, RendererChoice.D3D12, true));
        Assert.Equal("", StartupSettings.Text(false, RendererChoice.D3D12, false));
    }

    [Fact]
    public void LogAndRendererShareTheFile()
    {
        string t = StartupSettings.Text(true, RendererChoice.OpenGL, false);
        Assert.Contains("[debug]", t);
        Assert.Contains("[rendering]", t);
    }

    [Fact]
    public void AChosenMicrophoneTurnsTheAudioInputOnAtTheNextStart()
    {
        Assert.Equal("", StartupSettings.Text(false, RendererChoice.Auto, true, micInput: false));
        Assert.Equal("[audio]\n\ndriver/enable_input=true\n", StartupSettings.Text(false, RendererChoice.Auto, true, micInput: true));
        Assert.Contains("[audio]", StartupSettings.Text(true, RendererChoice.OpenGL, false, micInput: true));
    }

    // #133: AMD's Windows Vulkan driver draws the 1997 look's ground black; D3D12 draws it right
    [Theory]
    [InlineData("Windows", "AMD", RendererChoice.D3D12)]                       // Vulkan and D3D12 report the vendor so
    [InlineData("Windows", "ATI Technologies Inc.", RendererChoice.D3D12)]     // OpenGL
    [InlineData("Windows", "Advanced Micro Devices, Inc.", RendererChoice.D3D12)]
    [InlineData("Windows", "NVIDIA", RendererChoice.Auto)]
    [InlineData("Windows", "Intel", RendererChoice.Auto)]
    [InlineData("Windows", "", RendererChoice.Auto)]
    [InlineData("Linux", "AMD", RendererChoice.Auto)]                          // RADV draws it right
    [InlineData("macOS", "AMD", RendererChoice.Auto)]
    public void AutoPicksD3D12OnlyOnWindowsWithAnAmdCard(string os, string vendor, RendererChoice want) =>
        Assert.Equal(want, StartupSettings.AutoPick(os, vendor));

    [Fact]
    public void AutosPickWritesTheD3D12File() =>
        Assert.Equal(StartupSettings.Text(false, RendererChoice.D3D12, true),
            StartupSettings.Text(false, StartupSettings.AutoPick("Windows", "AMD"), true));

    // #198: the row says what Auto runs on
    [Theory]
    [InlineData(RendererChoice.Auto, true, "vulkan", "Auto (Vulkan)")]
    [InlineData(RendererChoice.Auto, true, "d3d12", "Auto (D3D12)")]
    [InlineData(RendererChoice.Auto, false, "opengl3", "Auto (OpenGL)")]
    [InlineData(RendererChoice.Auto, false, "", "Auto")]
    [InlineData(RendererChoice.D3D12, false, "vulkan", "Auto (Vulkan)")]       // a D3D12 choice carried to Linux is Auto there
    [InlineData(RendererChoice.Vulkan, true, "vulkan", "Vulkan")]
    [InlineData(RendererChoice.OpenGL, true, "vulkan", "OpenGL")]              // the next start's choice, not what runs now
    public void TheRendererRowNamesWhatAutoRuns(RendererChoice choice, bool d3d12Offered, string driver, string want) =>
        Assert.Equal(want, StartupSettings.RowText(choice, d3d12Offered, driver));
}
