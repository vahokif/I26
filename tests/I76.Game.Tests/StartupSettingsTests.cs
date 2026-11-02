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
}
