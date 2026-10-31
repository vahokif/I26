using Godot;

namespace I76.Game.Boot;

/// <summary>
/// Which of Godot's renderers the game runs on. Forward+ (Vulkan or D3D12) is the one it is made for; a machine
/// whose driver has neither falls back to Compatibility (OpenGL), which lacks some of what the modern look uses
/// (docs/compatibility-renderer.md): there the game turns those off or swaps in a plainer way, and says so once.
/// </summary>
public static class RenderBackend
{
    /// <summary>"forward_plus", "mobile" or "gl_compatibility".</summary>
    public static string Method => RenderingServer.GetCurrentRenderingMethod();

    /// <summary>True when running on the Compatibility (OpenGL) renderer.</summary>
    public static bool Compatibility => Method == "gl_compatibility";

    /// <summary>The shell's compatibility notice is shown once a run.</summary>
    public static bool NoticeShown { get; set; }

    /// <summary>The notice's text (the shell's font has capitals and no more than three lines of it fit).</summary>
    public const string Notice = "NO VULKAN OR D3D12 DRIVER FOUND. COMPATIBILITY MODE: SOME EFFECTS ARE OFF.";

    /// <summary>The line the log opens its graphics section with: method, driver and device.</summary>
    public static string Describe() =>
        $"renderer: {Method} ({RenderingServer.GetVideoAdapterApiVersion()}, {RenderingServer.GetVideoAdapterName()})";
}
