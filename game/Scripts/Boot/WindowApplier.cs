using System.Linq;
using Godot;

namespace I76.Game.Boot;

/// <summary>
/// Applies Graphic Detail's Resolution to the engine, live: a size is a centred window of that size, the Desktop choice is
/// the fullscreen the game starts in (the engine's borderless fullscreen at the desktop's size, not exclusive, so no mode change
/// and no restart are needed). A --windowed, --fullscreen or --resolution on the command line keeps the start as given.
/// </summary>
public static class WindowApplier
{
    private static bool CommandLineOwnsStart => OS.GetCmdlineArgs().Any(a => a is "--windowed" or "-w" or "--fullscreen" or "-f" or "--resolution" or "--maximized" or "-m" or "--fullscreen-exclusive");

    public static (int W, int H) Desktop { get { var s = DisplayServer.ScreenGetSize(); return (s.X, s.Y); } }

    /// <summary>The Resolution last applied (or found at start): a Cancel, a Reset or a menu's restore that leaves it as it is changes nothing, so a window the command line or Alt+Enter chose stays.</summary>
    private static string? _applied;

    public static void Apply(I76.Game.Shell.ShellSettings s, bool atStart = false)
    {
        if (!atStart && s.Resolution == _applied) return;
        _applied = s.Resolution;
        if (DisplayServer.GetName() == "headless" || (atStart && CommandLineOwnsStart)) return;
        if (WindowSizes.Parse(s.Resolution) is not { } size)
        {
            if (!atStart) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            return;
        }
        var scr = DisplayServer.ScreenGetUsableRect();
        var (w, h) = WindowSizes.Fit(size, scr.Size.X, scr.Size.Y);
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetSize(new Vector2I(w, h));
        DisplayServer.WindowSetPosition(scr.Position + (scr.Size - new Vector2I(w, h)) / 2);
    }
}
