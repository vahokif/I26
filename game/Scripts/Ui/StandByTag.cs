using Godot;
using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Game.Shell;

namespace I76.Game.Ui;

/// <summary>
/// The boot screen's "PLEASE STAND BY . . ." (its line's lettering and fit, I76's or Nitro's), small in the
/// bottom-right corner of a movie's picture (the picture's own rect, not the bars beside it): a briefing played while its mission
/// loads fades it in over <see cref="FadeSeconds"/> when the player waits on the mission (a skip asked for before the mission can
/// start, or the movie run out), and goes with the movie when the mission starts (MoviePlayer.Waiting). Set again in
/// Impact at the window's resolution in both looks, as the boot screen's line is, with a faint shadow so it reads over a bright
/// frame; without Impact on the system, the picture's own pixels (loadgame.pcx), scaled down into its palette.
/// </summary>
public partial class StandByTag : Control
{
    public required GameInstall Install { get; init; }
    /// <summary>The picture's size in its own pixels (the movie's), kept to its aspect inside this control as the movie is.</summary>
    public Vector2 Picture { get; set; } = new(ShellFrame.W, ShellFrame.H);

    /// <summary>The tag's size against the boot screen's line, and its distance from the picture's corner in picture pixels.</summary>
    private const float Scale = 0.33f, Margin = 14f;
    /// <summary>How opaque it is once faded in (fully: its shadow stays faint), and how long the fade takes.</summary>
    private const float Opacity = 1f;
    public const double FadeSeconds = 0.4;
    private bool _fading;

    private ImpactLayer? _impact;
    private TextureRect? _art;
    private Vector2I _artSize;
    private Rect2I _ink, _box;
    private ImpactLabels.Spec _spec = null!;
    private Vector2 _laidFor = -Vector2.One;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        bool nitro = Install.IsNitro;
        (_box, _spec) = I76.Game.Boot.BootSplash.StandBy(nitro);
        _ink = I76.Game.Boot.BootSplash.StandByInk(nitro);
        Visible = false;   // (until FadeIn)
        AddChild(_impact = new ImpactLayer { Name = "Impact" });
        if (_impact.Available) return;
        _impact.QueueFree();
        _impact = null;
        if (Art() is not { } img) return;
        _artSize = new Vector2I(img.GetWidth(), img.GetHeight());
        AddChild(_art = new TextureRect
        {
            Texture = ImageTexture.CreateFromImage(img), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = TextureFilterEnum.Nearest, MouseFilter = MouseFilterEnum.Ignore,
        });
    }

    /// <summary>The line's own pixels from loadgame.pcx, halved and each put back to its nearest palette colour; black made clear.</summary>
    private Image? Art()
    {
        var path = Install.Resolve("addon", "loadgame.pcx") ?? Install.FindFile("loadgame.pcx");
        if (path is null) return null;
        Pcx pcx;
        try { pcx = Pcx.Parse(System.IO.File.ReadAllBytes(path)); }
        catch (System.Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException) { return null; }
        var ink = _ink;
        if (ink.Size.X <= 0 || ink.End.X > pcx.Width || ink.End.Y > pcx.Height) return null;
        var crop = Image.CreateEmpty(ink.Size.X, ink.Size.Y, false, Image.Format.Rgb8);
        for (int y = 0; y < ink.Size.Y; y++)
            for (int x = 0; x < ink.Size.X; x++)
            {
                int c = pcx.Pixels[(ink.Position.Y + y) * pcx.Width + ink.Position.X + x] * 3;
                crop.SetPixel(x, y, Color.Color8(pcx.Palette[c], pcx.Palette[c + 1], pcx.Palette[c + 2]));
            }
        int w = Mathf.Max(1, Mathf.RoundToInt(ink.Size.X * Scale)), h = Mathf.Max(1, Mathf.RoundToInt(ink.Size.Y * Scale));
        crop.Resize(w, h, Image.Interpolation.Lanczos);
        var outImg = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = crop.GetPixel(x, y);
                int best = 0, bestD = int.MaxValue;
                for (int i = 0; i < 256; i++)
                {
                    int dr = pcx.Palette[i * 3] - (int)(p.R * 255), dg = pcx.Palette[i * 3 + 1] - (int)(p.G * 255), db = pcx.Palette[i * 3 + 2] - (int)(p.B * 255);
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestD) (best, bestD) = (i, d);
                }
                var q = Color.Color8(pcx.Palette[best * 3], pcx.Palette[best * 3 + 1], pcx.Palette[best * 3 + 2]);
                outImg.SetPixel(x, y, q.R8 + q.G8 + q.B8 <= 60 ? Colors.Transparent : q);   // (the line's black ground: the movie shows through)
            }
        return outImg;
    }

    /// <summary>In over <see cref="FadeSeconds"/> (once): the player asked to skip, or the movie ran out, before the mission is ready.</summary>
    public void FadeIn()
    {
        if (_fading) return;
        _fading = true;
        Modulate = new Color(1, 1, 1, 0);
        Visible = true;
        var t = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        t.TweenProperty(this, "modulate:a", Opacity, FadeSeconds);
    }

    public override void _Process(double delta)
    {
        if (Visible && GetViewportRect().Size != _laidFor) Layout();
    }

    /// <summary>The picture's rect on the screen: fitted and centred, as the movie's TextureRect (KeepAspectCentered, the whole window) draws it.</summary>
    private Rect2 PictureRect()
    {
        var view = GetViewportRect().Size;
        float s = Mathf.Min(view.X / Picture.X, view.Y / Picture.Y);
        var size = Picture * s;
        return new Rect2((view - size) / 2, size);
    }

    private void Layout()
    {
        _laidFor = GetViewportRect().Size;
        var r = PictureRect();
        if (r.Size.X <= 0) return;
        float px = r.Size.Y / Picture.Y;   // a picture pixel on the screen
        // the line's top-left in picture pixels: its corner Margin in from the picture's
        var tag = new Vector2(_ink.Size.X, _ink.Size.Y) * Scale;
        var at = new Vector2(Picture.X - Margin - tag.X, Picture.Y - Margin - tag.Y).Round();
        if (_art is not null)
        {
            // whole screen pixels for each of the picture's: crisp
            int k = Mathf.Max(1, Mathf.RoundToInt(px));
            var size = new Vector2(_artSize.X * k, _artSize.Y * k);
            _art.Position = (r.End - Vector2.One * Margin * px).Round() - size - GlobalPosition;   // (the corner kept Margin in from the picture's)
            _art.Size = size;
        }
        if (_impact is not null)
        {
            // ImpactLayer draws in the 640x480 frame kept 4:3 and centred: this point there, the line's box where its ink lands here
            var view = GetViewportRect().Size;
            float s = Mathf.Min(view.X / ShellFrame.W, view.Y / ShellFrame.H);
            var off = (view - new Vector2(ShellFrame.W, ShellFrame.H) * s) / 2;
            var inkOnScreen = r.Position + at * px;
            var boxInFrame = (inkOnScreen - off) / s - new Vector2(_ink.Position.X - _box.Position.X, _ink.Position.Y - _box.Position.Y) * Scale;
            var small = _spec with { Size = _spec.Size * Scale, Track = _spec.Track * Scale, X = _spec.X * Scale, Baseline = _spec.Baseline * Scale, WordSpace = _spec.WordSpace * Scale };
            _impact.Clear();
            int bx = Mathf.RoundToInt(boxInFrame.X), by = Mathf.RoundToInt(boxInFrame.Y);
            _impact.Put(bx + 1, by + 1, 0, 0, small, new Color(0, 0, 0, 0.45f));   // its shadow, faint, a frame pixel down and right
            _impact.Put(bx, by, 0, 0, small, Colors.White);
        }
    }
}
