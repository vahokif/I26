using System.Threading.Tasks;
using Godot;
using I76.Formats.Shell;
using I76.Game.Shell;

namespace I76.Game.Boot;

/// <summary>
/// The boot screen. The original blits loadgame.pcx (the logo over "PLEASE
/// STAND BY . . .", Nitro's its own) while it starts. Godot's own boot splash
/// is shown before any script runs, so it is a committed file that carries no
/// game art: project.godot points application/boot_splash/image at
/// res://Assets/boot_splash.png, black with "PLEASE STAND BY..." in Impact where
/// the original's line is (tools/make-boot-splash.py). Once scripts run this
/// puts the install's own picture over it, always (read from the player's
/// install, never stored): the picture scaled to 1920x1440 on black, its line
/// drawn again over a black box in the system's Impact at that resolution,
/// fitted to the picture (tools/fit-impact.py --crop), so the swap from the
/// engine's splash does not jump. An upscale in the backgrounds' folder
/// (nitro/load/loadgame.webp) is used as it is. The 1997 look takes the original
/// picture with its own text, never the HD one. Without Impact, or for a picture
/// whose line is not one of the two below, the picture's own text stays.
/// </summary>
public static class BootSplash
{
    private const string EnginePath = "res://Assets/boot_splash.png";
    private const int W = 1920, H = 1440;

    /// <summary>A picture's line: the box its ink fills exactly (which picture it is), the black box drawn over it, and the fit in that box.</summary>
    private sealed record Line(Rect2I Ink, Rect2I Box, ImpactLabels.Spec Spec);

    private static readonly Line[] Lines =
    [
        // I76's loadgame.pcx: the dots spaced as words
        new(new(213, 395, 249, 27), new(205, 388, 265, 40), new("PLEASE STAND BY . . .", 32f, 0f, 6.695f, 33.094f, 0.062f, Slant: 18.5f)),
        // Nitro's addon\LOADGAME.PCX: larger and lower, the dots kerned under the Y
        new(new(195, 415, 263, 31), new(187, 408, 280, 44), new("PLEASE STAND BY...", 37f, 0f, 6.340f, 36.884f, 0.062f, Slant: 18.625f)),
    ];

    /// <summary>The boot screen's line (I76's or Nitro's): its box and fit, for other "please stand by" moments to match it.</summary>
    internal static (Rect2I Box, ImpactLabels.Spec Spec) StandBy(bool nitro) => (Lines[nitro ? 1 : 0].Box, Lines[nitro ? 1 : 0].Spec);
    /// <summary>Where the boot screen's own line is drawn in its picture (I76's or Nitro's loadgame.pcx).</summary>
    internal static Rect2I StandByInk(bool nitro) => Lines[nitro ? 1 : 0].Ink;

    private static CanvasLayer? _overlay;
    private static bool _shown;
    private static TextureRect? _overlayPic;

    /// <summary>A game switch: the overlay goes at once and the next game's picture is shown as at a first start.</summary>
    internal static void ResetForGameSwitch() { Dismiss(now: true); _shown = false; }

    /// <summary>
    /// Puts up a full-screen layer (4:3, on black) that starts as the engine's own splash and becomes the
    /// install's picture (its line in Impact) in a frame or two; Dismiss fades it out. Nothing when the
    /// install has no picture: the engine's splash stays until the shell draws over it.
    /// </summary>
    public static void Show(Node host, string installDir, bool faithful)
    {
        try
        {
            // once per process: Main's _Ready runs again at every scene reload (each launch from the shell),
            // where the mission's own loading screen is the picture
            if (_overlay is not null || _shown) return;
            _shown = true;
            var (path, hdPath) = Plan(installDir);
            if (path is null) return;
            _overlay = new CanvasLayer { Layer = 200, Name = "BootSplash" };
            var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
            black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _overlayPic = new TextureRect
            {
                Texture = EngineSplash(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _overlayPic.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _overlay.AddChild(black);
            _overlay.AddChild(_overlayPic);
            host.AddChild(_overlay);
            Compose(host.GetTree(), path, hdPath, faithful);
        }
        catch (System.Exception ex) { GD.PushWarning($"boot splash: no overlay ({ex.Message})"); }
    }

    private static Texture2D? EngineSplash()
    {
        try { return GD.Load<Texture2D>(EnginePath); }
        catch { return null; }
    }

    private static async void Compose(SceneTree tree, string path, string? hdPath, bool faithful)
    {
        try
        {
            var pic = _overlayPic;
            var (picture, line) = await Task.Run(() => Prepare(path, hdPath, faithful, ImpactInstalled()));
            var shot = line is null ? picture : await Render(tree, picture, line);
            if (pic is not null && GodotObject.IsInstanceValid(pic) && _overlayPic == pic)
                pic.Texture = ImageTexture.CreateFromImage(shot);
        }
        catch (System.Exception ex) { GD.PushWarning($"boot splash: picture not made ({ex.Message})"); }
    }

    /// <summary>Fades the overlay out (quickly), if there is one; <paramref name="now"/> takes it away at once (before a build that holds the frame, where a fade would not run).</summary>
    public static void Dismiss(bool now = false)
    {
        if (_overlay is not { } o || !GodotObject.IsInstanceValid(o)) { _overlay = null; return; }
        _overlay = null; _overlayPic = null;
        if (now) { o.QueueFree(); return; }
        var t = o.CreateTween();
        // the layer has no modulate: its controls do
        foreach (var c in o.GetChildren().OfType<CanvasItem>()) t.Parallel().TweenProperty(c, "modulate:a", 0f, 0.25);
        t.TweenCallback(Callable.From(o.QueueFree));
    }

    private static (string? Path, string? HdPath) Plan(string installDir)
    {
        // not through GameInstall, which wants I76's own files: any install with the picture will do (Nitro's is addon\LOADGAME.PCX)
        var path = Find(installDir, "addon", "loadgame.pcx") ?? Find(installDir, "loadgame.pcx");
        if (path is null) return (null, null);
        // an upscale of the picture (nitro/load/loadgame.webp in the backgrounds' folder), if the player has one: it has its text
        return (path, HdAssets.Find($"{ShellBackdrop.NitroSub}/load/loadgame"));
    }

    /// <summary>The system's Impact, as ImpactLayer finds it (another family in its place won't do).</summary>
    private static bool ImpactInstalled()
    {
        return I76.Game.Shell.HdAssets.Impact(TextServer.FontAntialiasing.Gray) is not null;
    }

    /// <summary>The picture, scaled to the splash's size, and the line to draw over it (null: the picture as it is).</summary>
    private static (Image Picture, Line? Line) Prepare(string path, string? hdPath, bool faithful, bool impact)
    {
        if (!faithful && hdPath is not null && HdAssets.LoadImage(hdPath) is { } hd)
        {
            hd.Convert(Image.Format.Rgb8);
            if (hd.GetWidth() != W || hd.GetHeight() != H) hd.Resize(W, H, Image.Interpolation.Lanczos);
            return (hd, null);
        }
        var pcx = Pcx.Parse(System.IO.File.ReadAllBytes(path));
        var rgb = new byte[pcx.Width * pcx.Height * 3];
        for (int i = 0; i < pcx.Pixels.Length; i++)
            System.Array.Copy(pcx.Palette, pcx.Pixels[i] * 3, rgb, i * 3, 3);
        var ink = Ink(pcx, rgb);
        var line = !faithful && impact && pcx.Width == ShellFrame.W && pcx.Height == ShellFrame.H ? Lines.FirstOrDefault(l => l.Ink == ink) : null;
        var img = Image.CreateFromData(pcx.Width, pcx.Height, false, Image.Format.Rgb8, rgb);
        // the picture's own line out first, so none of it is smeared past the box by the scaling
        if (line is not null) img.FillRect(line.Box, Colors.Black);
        img.Resize(W, H, Image.Interpolation.Lanczos);
        return (img, line);
    }

    /// <summary>A path below the root, matched one component at a time without regard to case.</summary>
    private static string? Find(string root, params string[] parts)
    {
        string? cur = root;
        foreach (var part in parts)
            cur = System.IO.Directory.Exists(cur)
                ? System.IO.Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(p => string.Equals(System.IO.Path.GetFileName(p), part, System.StringComparison.OrdinalIgnoreCase))
                : null;
        return cur is not null && System.IO.File.Exists(cur) ? cur : null;
    }

    /// <summary>The bounds of everything not black below the logo (y 300 on): where a picture's line is.</summary>
    private static Rect2I Ink(Pcx pcx, byte[] rgb)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 300; y < pcx.Height; y++)
            for (int x = 0; x < pcx.Width; x++)
            {
                int i = (y * pcx.Width + x) * 3;
                if (rgb[i] + rgb[i + 1] + rgb[i + 2] <= 60) continue;
                x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x); y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
            }
        return x1 < 0 ? default : new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>One frame of a SubViewport the picture's size: the scaled picture, the line over it.</summary>
    private static async Task<Image> Render(SceneTree tree, Image picture, Line line)
    {
        var vp = new SubViewport { Size = new Vector2I(W, H), Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        vp.AddChild(new TextureRect
        {
            Texture = ImageTexture.CreateFromImage(picture), AnchorRight = 1, AnchorBottom = 1,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
        });
        var impact = new ImpactLayer();
        vp.AddChild(impact);
        tree.Root.AddChild(vp);
        try
        {
            impact.Put(line.Box.Position.X, line.Box.Position.Y, line.Box.Size.X, line.Box.Size.Y, line.Spec, Colors.White);
            // two frames: the first in which the viewport is in the tree may have been drawn before it was
            for (int i = 0; i < 2; i++)
                await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var shot = vp.GetTexture().GetImage();
            shot.Convert(Image.Format.Rgb8);
            return shot;
        }
        finally { vp.QueueFree(); }
    }
}
