using Godot;
using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Game.Shell;

namespace I76.Game.Ui;

/// <summary>
/// "SOMEWHERE IN THE SOUTHWEST...": the original's start-up shows addon\loadscr.pcx
/// while a mission loads, the 640x480 picture blitted to the
/// display, then the mission (and its briefing) follows. Here it is kept 4:3
/// and centred over black; in the modern look its line is drawn again in the
/// system's Impact at the window's resolution, fitted to the picture
/// (tools/fit-impact.py: 33 px, no tracking, 18° slant). It fades in from
/// black as the original's palette ramp does (32
/// steps of a meant 10 ms; the wait's test is the wrong way round, so the
/// original ran it at its present rate) and, the mission built, back out.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    public required GameInstall Install { get; init; }
    /// <summary>The mission being loaded (Nitro picks its card by the name).</summary>
    public string? Mission { get; init; }
    public bool Faithful { get; init; }

    /// <summary>The line's box and its fit in the picture's pixels.</summary>
    private static readonly ImpactLabels.Spec Line = new("SOMEWHERE IN THE SOUTHWEST...", 33f, 0f, 9.441f, 34.919f, -0.062f);
    private const int BoxX = 94, BoxY = 386, BoxW = 451, BoxH = 42;
    private string? _shotPath;

    private int _shots;

    /// <summary>Every frame the screen is drawn, as PATH_n.png (the fade in, the hold, the fade out).</summary>
    private void Shot()
    {
        if (_shotPath is null || !IsInsideTree()) return;
        GetViewport().GetTexture().GetImage().SavePng(_shotPath.Replace(".png", $"_{_shots++:00}.png"));
    }

    /// <summary>From black to the picture over 32 steps.</summary>
    public SignalAwaiter FadeIn() => Ramp(Colors.White);

    /// <summary>Back to black.</summary>
    public SignalAwaiter FadeOut() => Ramp(Colors.Black);

    private SignalAwaiter Ramp(Color to)
    {
        var t = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        t.TweenProperty(_root, "modulate", to, Fade);
        return ToSignal(t, Tween.SignalName.Finished);
    }

    public override void _ExitTree()
    {
        if (_shotPath is not null) RenderingServer.FramePostDraw -= Shot;
    }

    /// <summary>The upscale of the card at <paramref name="pcxPath"/> (loadscr, p01load...), or null.</summary>
    public static Image? Upscale(string pcxPath)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(pcxPath).ToLowerInvariant();
        var f = HdAssets.Find($"{ShellBackdrop.NitroSub}/load/{stem}");
        if (f is null) return null;
        var img = HdAssets.LoadImage(f);
        if (img is null) GD.PushWarning($"loading screen: {f} unreadable");
        return img;
    }

    private Control _root = null!;
    private Label? _status;
    private const double Fade = 32 * 0.01;

    /// <summary>A line under the picture: what a network join is doing ("Joining via server...").</summary>
    public string? Status
    {
        set
        {
            if (_status is null) return;
            _status.Text = value ?? "";
        }
    }

    /// <summary>
    /// A question over the picture with a button per answer (the port's: a network host's custom map, "Download?"):
    /// the index of the one clicked, or −1 for Esc. Enter takes the first. The mouse is shown while it is up.
    /// </summary>
    public System.Threading.Tasks.Task<int> Ask(string question, string[] answers)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<int>();
        Input.MouseMode = Input.MouseModeEnum.Visible;   // (Main's cursor state keeps it: a loading screen is a menu)
        var panel = new PanelContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f, GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.85f), BorderColor = new Color(0.8f, 0.7f, 0.4f), BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2, ContentMarginLeft = 24, ContentMarginRight = 24, ContentMarginTop = 18, ContentMarginBottom = 18 });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 16);
        var text = new Label { Text = question, HorizontalAlignment = HorizontalAlignment.Center };
        text.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(text);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        void Finish(int i)
        {
            if (done.Task.IsCompleted) return;
            _ask = null;
            panel.QueueFree();
            done.SetResult(i);
        }
        for (int i = 0; i < answers.Length; i++)
        {
            int k = i;
            var b = new Button { Text = answers[i], CustomMinimumSize = new Vector2(120, 36) };
            b.AddThemeFontSizeOverride("font_size", 18);
            b.Pressed += () => Finish(k);
            row.AddChild(b);
            if (i == 0) b.CallDeferred(Control.MethodName.GrabFocus);
        }
        box.AddChild(row);
        panel.AddChild(box);
        AddChild(panel);
        _ask = Finish;
        // capture hook: I76_LOADING_ANSWER=N clicks answer N once the question has been up 60 frames
        if (int.TryParse(System.Environment.GetEnvironmentVariable("I76_LOADING_ANSWER"), out int scripted)) AnswerLater(scripted);
        return done.Task;
    }

    private async void AnswerLater(int answer)
    {
        for (int i = 0; i < 60; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"loading screen: I76_LOADING_ANSWER answers {answer}");
        _ask?.Invoke(answer);
    }

    private System.Action<int>? _ask;

    /// <summary>Takes a question down unanswered (its answer −1).</summary>
    public void Dismiss() => _ask?.Invoke(-1);

    public override void _UnhandledInput(InputEvent e)
    {
        if (_ask is null || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.Keycode == Key.Escape) { _ask(-1); GetViewport().SetInputAsHandled(); }
    }

    public override void _Ready()
    {
        Layer = 100;
        ProcessMode = ProcessModeEnum.Always;
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        // everything but the black backing fades: the picture and its Impact line, multiplied from black
        AddChild(_root = new Control { AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = Colors.Black });
        _status = new Label
        {
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1, OffsetTop = -40, OffsetBottom = -12,
            HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _status.AddThemeFontSizeOverride("font_size", 18);
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 5);
        AddChild(_status);
        // Nitro: a mission named p.. shows addon\pNNload.pcx, anything else loadscr.pcx
        var path = (Install.IsNitro && Mission is { Length: > 1 } m && char.ToLowerInvariant(m[0]) == 'p' ? Install.Resolve("addon", System.IO.Path.GetFileNameWithoutExtension(m) + "load.pcx") : null)
            ?? Install.Resolve("addon", "loadscr.pcx") ?? Install.FindFile("loadscr.pcx");
        // I76_LOADING_CARD=b01: shows that card (a capture hook; the game itself shows b01 and the rest as loadscr)
        if (System.Environment.GetEnvironmentVariable("I76_LOADING_CARD") is { Length: > 0 } card && Install.Resolve("addon", card + "load.pcx") is { } cardPath) path = cardPath;
        if (path is null) return;
        // the modern look: an upscale of the card, nitro/load/<stem>.webp (.png, .jpg) in the backgrounds' folder, which has its text
        Image? img = Faithful ? null : Upscale(path);
        bool upscaled = img is not null;
        // Nitro's cards in the modern look, set again in Impact at the window's resolution (NitroCards); without Impact, the bitmap sharpened
        var lines = !Faithful && !upscaled && Install.IsNitro ? NitroCards.Of(System.IO.Path.GetFileNameWithoutExtension(path)) : null;
        bool typeset = lines is not null && NitroCardLayer.HasImpact();
        if (img is null)
        {
            var pcx = Pcx.Parse(System.IO.File.ReadAllBytes(path));
            img = Image.CreateEmpty(pcx.Width, pcx.Height, false, Image.Format.Rgb8);
            for (int y = 0; y < pcx.Height; y++)
                for (int x = 0; x < pcx.Width; x++)
                {
                    int c = pcx.Pixels[y * pcx.Width + x] * 3;
                    img.SetPixel(x, y, Color.Color8(pcx.Palette[c], pcx.Palette[c + 1], pcx.Palette[c + 2]));
                }
        }
        else img.GenerateMipmaps();
        if (typeset) _root.AddChild(new NitroCardLayer { Lines = lines! });
        else _root.AddChild(new TextureRect
        {
            Texture = ImageTexture.CreateFromImage(img), AnchorRight = 1, AnchorBottom = 1,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = Faithful ? CanvasItem.TextureFilterEnum.Nearest : upscaled ? CanvasItem.TextureFilterEnum.LinearWithMipmaps : CanvasItem.TextureFilterEnum.Linear,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            // Nitro's cards are white Impact on black: without an upscale, the modern look sharpens them at the window's resolution
            Material = !Faithful && !upscaled && Install.IsNitro ? new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/sharp_card.gdshader") } : null,
        });
        // capture hook: I76_LOADING_SHOT=PATH saves the loading screen's frames
        if (System.Environment.GetEnvironmentVariable("I76_LOADING_SHOT") is { } shot)
        {
            _shotPath = shot;
            RenderingServer.FramePostDraw += Shot;
        }
        // the line drawn again in Impact is I76's own ("SOMEWHERE IN THE SOUTHWEST..."); Nitro's cards have other text, kept as the picture has it
        if (Faithful || Install.IsNitro) return;
        var impact = new ImpactLayer { Name = "Impact" };
        _root.AddChild(impact);
        if (impact.Available) impact.Put(BoxX, BoxY, BoxW, BoxH, Line, Colors.White);
    }
}
