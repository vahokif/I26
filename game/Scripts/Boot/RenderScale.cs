using Godot;

namespace I76.Game.Boot;

/// <summary>
/// Graphic Detail's Render Scale: the 3D view drawn at 100, 150 or 200 % of the window's size in each direction and scaled back down
/// (supersampling; the viewport's 3D scaling), a cleaner picture for a GPU with room to spare. The 2D layers (the HUD, the menus) are
/// not scaled. 100 % is the engine's own, as before. Also the Anti-Aliasing row's 8x step (4x on the Compatibility renderer, which may lack it).
/// </summary>
public static class RenderScale
{
    /// <summary>Percent, per choice; the first is the default.</summary>
    public static readonly int[] Percents = [100, 150, 200];
    public static readonly string[] Names = ["100%", "150%", "200%"];

    /// <summary>The choice for a shell.cfg [port] render_scale value (percent); anything not offered is 100.</summary>
    public static int Parse(int? percent) => percent is { } p && System.Array.IndexOf(Percents, p) is var i and >= 0 ? i : 0;

    public static float Scale(int choice) => Percents[System.Math.Clamp(choice, 0, Percents.Length - 1)] / 100f;

    /// <summary>The Anti-Aliasing row's choices: Off, 2x, 4x (the original's) and the port's 8x.</summary>
    public static readonly string[] AntiAliasing = ["Off", "2x", "4x", "8x"];

    /// <summary>The engine's MSAA for a row's choice; 8x is 4x where the renderer has no 8x (Compatibility, where it may be missing).</summary>
    public static Viewport.Msaa Msaa(int choice, bool compatibility) => choice switch
    {
        <= 0 => Viewport.Msaa.Disabled,
        1 => Viewport.Msaa.Msaa2X,
        2 => Viewport.Msaa.Msaa4X,
        _ => compatibility ? Viewport.Msaa.Msaa4X : Viewport.Msaa.Msaa8X,
    };

    /// <summary>Sets the main window's 3D scale, live.</summary>
    public static void Apply(I76.Game.Shell.ShellSettings s)
    {
        if (Engine.GetMainLoop() is not SceneTree { Root: { } root }) return;
        root.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
        root.Scaling3DScale = Scale(s.RenderScale);
    }
}
