using Godot;

namespace I76.Game.Ui;

/// <summary>
/// The port's "Checkpoint" notice (docs/savestate.md): a word in the game's Lee handwriting at the bottom of the
/// view when a checkpoint is kept, held a moment and faded out. Scaled with the 640x480 frame as the HUD is.
/// </summary>
public partial class CheckpointNotice : CanvasLayer
{
    public required string InstallDir { get; init; }
    public bool Faithful { get; init; }
    public string Text { get; init; } = "Checkpoint";
    private const double Hold = 1.6, Fade = 0.9;
    private Label _label = null!;
    private double _t;

    public override void _Ready()
    {
        Layer = 30;
        ProcessMode = ProcessModeEnum.Always;
        _label = new Label { Text = Text, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        var lee = new FontFile { Antialiasing = Faithful ? TextServer.FontAntialiasing.None : TextServer.FontAntialiasing.Gray };
        if (lee.LoadDynamicFont(new I76.Formats.Vfs.GameInstall(InstallDir).FindFile("Lee_____.ttf") ?? System.IO.Path.Combine(InstallDir, "Lee_____.ttf")) == Error.Ok)
            _label.AddThemeFontOverride("font", lee);
        _label.AddThemeColorOverride("font_color", new Color(1, 0.92f, 0.75f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        AddChild(_label);
        Place();
    }

    private void Place()
    {
        var size = GetViewport().GetVisibleRect().Size;
        float k = size.Y / 480f;
        _label.AddThemeFontSizeOverride("font_size", Mathf.Max(12, (int)(26 * k)));
        _label.AddThemeConstantOverride("outline_size", Mathf.Max(2, (int)(5 * k)));
        _label.Size = new Vector2(size.X, 40 * k);
        _label.Position = new Vector2(0, size.Y - 172 * k);
    }

    public override void _Process(double delta)
    {
        // the game's clock: a pause holds the notice too
        if (GetTree().Paused) return;
        _t += delta;
        Place();
        _label.Modulate = new Color(1, 1, 1, _t < Hold ? 1f : (float)System.Math.Max(0, 1 - (_t - Hold) / Fade));
        if (_t > Hold + Fade) QueueFree();
    }
}
