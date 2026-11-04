using Godot;
using I76.Sim.Vehicles;

namespace I76.Game.Ui;

/// <summary>
/// What is drawn over the view: the external instruments outside the car
/// (with Ext. Instrum. Disp.), the target brackets, the CB handset and the
/// line on the CB. The cockpit's own instruments are its textures (Cockpit).
/// </summary>
public partial class Hud : CanvasLayer
{
    public Func<Vehicle?>? Car { get; set; }
    public I76.Sim.Missions.MissionScript? Script { get; set; }
    public I76.Sim.Weapons.Combat? Combat { get; set; }
    public Func<System.Numerics.Vector3>? Origin { get; set; }
    /// <summary>The node a vehicle is drawn as, for marks that sit on the car as seen.</summary>
    public Func<Vehicle, Godot.Node3D?>? CarOf { get; set; }
    /// <summary>True while the in-car view is up: its own instruments are on the dash, and the external ones are not drawn.</summary>
    public Func<bool>? InCockpit { get; set; }
    /// <summary>Graphic Detail's Ext. Instrum. Disp.: the 2D panels drawn outside the cockpit.</summary>
    public Func<bool>? ExtInstruments { get; init; }
    /// <summary>The player's cockpit, whose panel textures the external instruments draw.</summary>
    public Func<I76.Game.Sim.Cockpit?>? Pit { get; set; }
    /// <summary>True while the script holds the camera (pushCam, camera mode 5): the cut-scene shows no driving readouts.</summary>
    public Func<bool>? Cutscene { get; set; }
    /// <summary>The player is talking on the CB in a network game (the push-to-talk key is held): the handset is raised, as for the player's own line in a mission.</summary>
    public Func<bool>? Transmitting { get; set; }
    /// <summary>A texture member by its stem (the CB handset's frames), or null.</summary>
    public Func<string, Texture2D?>? Texture { get; set; }
    /// <summary>The driver variant (1-5): picks the handset art ZCBH6{v}01.</summary>
    public int Variant { get; set; } = 1;
    /// <summary>True in the 1997 look: the handset's pixels kept.</summary>
    public Func<bool>? Faithful { get; set; }
    /// <summary>The mission's palette (the 1997 target bar's colours are its entries).</summary>
    public I76.Formats.Imaging.Palette? Palette { get; set; }
    private ExtInstruments? _instruments;
    private TextureRect? _handset;
    private Texture2D? _handsetHalf, _handsetUp;
    private double _handsetT = -1;   // seconds since the handset began to rise (or, negative, to fall)
    public I76.Formats.Mission.ObjectiveText? Objectives { get; set; }
    public string? Mission { get; set; }
    /// <summary>True while the debug text shows (Ctrl+F3): the port's CB line rides with it.</summary>
    public Func<bool>? Debug { get; set; }
    private Label _banner = null!, _cb = null!, _subtitle = null!;
    private I76.Formats.Subtitles.SubtitleTrack? _subtitles;
    /// <summary>A file from the game's data by name (the binoculars' masks), or null.</summary>
    public Func<string, byte[]?>? ReadFile { get; set; }
    private ColorRect? _binoMask;

    public override void _Ready()
    {
        // the panels first: the target brackets are drawn over them (drawn after the panels), the handset over both
        if (Pit is not null) AddChild(_instruments = new ExtInstruments { Pit = Pit, InCockpit = () => InCockpit?.Invoke() == true, Name = "ExtInstruments", Visible = false });
        _cb = Label(18);
        _cb.HorizontalAlignment = HorizontalAlignment.Left;
        _cb.AnchorTop = _cb.AnchorBottom = 1;
        _cb.OffsetLeft = 244; _cb.OffsetTop = -60;
        AddChild(_cb);
        if (Combat is not null && Origin is not null) AddChild(new TargetIndicator { Player = () => Car?.Invoke(), Combat = Combat, Origin = Origin, CarOf = CarOf, Name = "Target",
            Look1997 = new TargetIndicator1997 { On = () => Faithful?.Invoke() == true, Combat = Combat, Origin = Origin, CarOf = CarOf, Texture = n => Texture?.Invoke(n), Palette = Palette } });
        // the binoculars' frame: over the view, the brackets and the handset, under the text (drawn after the
        // frame and before the text panes)
        if (BinocularMaskMaterial() is { } bm)
        {
            _binoMask = new ColorRect { Name = "Binoculars", Material = bm, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _binoMask.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_binoMask);
        }
        _banner = Label(40);
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        _banner.AnchorLeft = 0; _banner.AnchorRight = 1; _banner.AnchorTop = _banner.AnchorBottom = 0.3f;
        _banner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_banner);

        if (!string.IsNullOrEmpty(Mission))
            _subtitles = I76.Formats.Subtitles.SubtitleLoader.Load(Mission);
        _subtitle = Label(22);
        _subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        _subtitle.VerticalAlignment = VerticalAlignment.Bottom;
        _subtitle.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _subtitle.OffsetLeft = 80;
        _subtitle.OffsetRight = -80;
        _subtitle.OffsetBottom = -36;
        _subtitle.OffsetTop = -140;
        _subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _subtitle.Visible = false;
        AddChild(_subtitle);
    }

    public override void _UnhandledInput(InputEvent e)
    {
    }

    /// <summary>The line on the CB (the objectives are the notepad's, N: MissionPicture).</summary>
    private void UpdateScript()
    {
        if (Script is null) { _banner.Visible = _cb.Visible = _subtitle.Visible = false; return; }
        var h = Script.Host;
        _cb.Visible = h.Cb.Current is not null && Debug?.Invoke() != false;
        if (h.Cb.Current is { } l) _cb.Text = $"CB: {(l.Speaker as I76.Sim.Missions.Entity)?.Name ?? "radio"} ({l.Name})";
        _banner.Visible = false;      // the end of a mission is the outro or the end menu (EscapeMenu), as the original's start-up does

        if (I76.Game.Shell.ShellSettings.Load().Subtitles && h.Cb.Current is { } cur)
        {
            string? text = _subtitles?.BySound(cur.Name);
            if (string.IsNullOrEmpty(text))
            {
                var soundTrack = I76.Formats.Subtitles.SubtitleLoader.Load(cur.Name);
                text = soundTrack?.BySound(cur.Name);
                if (string.IsNullOrEmpty(text) && soundTrack?.Cues.Count == 1)
                    text = soundTrack.Cues[0].Text;
            }
            if (!string.IsNullOrEmpty(text))
            {
                // For long cues (e.g. poem stanzas) show at most two wrapped lines (~160 chars at
                // the subtitle font size).  Trim at the last word boundary before the limit so the
                // label never shows a wall of text.
                string display = I76.Formats.Subtitles.SubtitleTrack.Page(text, (h.Cb.NowMs - cur.StartedMs) / 1000.0, cur.Length);
                if (_subtitle.Text != display) _subtitle.Text = display;
                _subtitle.Visible = true;
            }
            else _subtitle.Visible = false;
        }
        else _subtitle.Visible = false;
    }

    private static Label Label(int size)
    {
        var l = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", new Color(1, 0.92f, 0.75f));
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        return l;
    }

    /// <summary>
    /// The CB handset:
    /// raised while a line the player speaks is on the air (a speaker with
    /// the player's flag 0x10 starts it; its end or a cut lowers it), so it
    /// comes up for Groove's request, down for Taurus's poem, and up again for
    /// the sign-off. The art is zcbh6101.tmt: two 256x128 frames, the hand
    /// half raised (ZCBH6101) and up (ZCBH6102), played at the table's 10 a
    /// second; drawn at the bottom centre of the 640x480 frame, scaled with it,
    /// in the cockpit, and in the chase view with Ext. Instrum. Disp. on (it is one of
    /// the external instruments); nearest-neighbour in both looks.
    /// </summary>
    private void UpdateHandset(double delta)
    {
        if (_handset is null)
        {
            // the frames' rows are stored bottom up, as every texture's are (a mesh's UVs take care of that; a
            // bitmap on the screen has to be turned the right way up)
            static Texture2D? Upright(Texture2D? t)
            {
                if (t?.GetImage() is not { } img) return null;
                img.FlipY();
                return ImageTexture.CreateFromImage(img);
            }
            _handsetHalf = Upright(Texture?.Invoke($"ZCBH6{Variant}01"));
            _handsetUp = Upright(Texture?.Invoke($"ZCBH6{Variant}02"));
            if (_handsetUp is null) return;
            _handset = new TextureRect { Name = "Handset", StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            AddChild(_handset);
            if (_binoMask is not null) MoveChild(_handset, _binoMask.GetIndex());
        }
        var h = Script?.Host;
        // the handset is one of the external instruments: in the chase view only
        // with Ext. Instrum. Disp. on; in the cockpit always, as the 1997 build drew it there
        bool shown = InCockpit?.Invoke() == true || ExtInstruments?.Invoke() != false;
        bool talking = (h?.Cb.Current is { Speaker: { } sp } && sp == h.Player || Transmitting?.Invoke() == true) && Cutscene?.Invoke() != true && shown;
        const double Frame = 0.1;
        if (talking) _handsetT = _handsetT < 0 ? 0 : _handsetT + delta;
        else if (_handsetT >= 0) _handsetT = -delta;
        else if (_handsetT > -1) _handsetT -= delta;
        // up: half raised for a frame, then up; down: half raised for a frame, then gone
        Texture2D? tex = _handsetT >= Frame ? _handsetUp : _handsetT >= 0 || _handsetT > -Frame ? _handsetHalf : null;
        _handset.Visible = tex is not null;
        if (tex is null) return;
        _handset.Texture = tex;
        _handset.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;   // its pixels kept in both looks
        var size = GetViewport().GetVisibleRect().Size;
        float k = size.Y / 480f;
        _handset.Size = new Vector2(256, 128) * k;
        _handset.Position = new Vector2(size.X / 2 - 128 * k, size.Y - 128 * k);
    }

    public override void _Process(double delta)
    {
        // a destroyed player's screen is the death orbit alone: the instruments, the brackets and the handset go until a
        // network game's respawn gives the car back (the original draws none of them for a dead driver)
        bool dead = Car?.Invoke() is { } me && (me.Flags & VehicleFlags.Dead) != 0;
        if (_handset is not null && dead) _handsetT = -1;
        UpdateHandset(delta);
        UpdateScript();
        bool cut = Cutscene?.Invoke() == true;
        if (dead) cut = true;
        // outside the cockpit the instruments are the original's external ones, drawn only with Ext. Instrum. Disp. on;
        // in it they are the 2D dash (F1 twice), whatever the option says, but not in the binoculars (mode 2 draws only the handset)
        if (_instruments is not null)
        {
            var pit = Pit?.Invoke();
            bool inCar = InCockpit?.Invoke() == true;
            _instruments.Visible = Car?.Invoke() is not null && !cut && (inCar ? pit is { Dash3D: false, Binoculars: false } : ExtInstruments?.Invoke() == true);
        }
        if (GetNodeOrNull<CanvasItem>("Target") is { } target) target.Visible = !cut;
        if (_binoMask is not null)
        {
            _binoMask.Visible = !cut && InCockpit?.Invoke() == true && Pit?.Invoke() is { Binoculars: true };
            if (_binoMask.Visible && _binoMask.Material is ShaderMaterial sm)
            {
                sm.SetShaderParameter("faithful", Faithful?.Invoke() == true);
                sm.SetShaderParameter("screen_size", _binoMask.Size);
            }
        }
    }

    /// <summary>
    /// The binoculars' mask (I76.Formats.Imaging.BinocularMask) as the shader's two textures: the 640x480 one as it is for
    /// the 1997 look, the 800x600 one softened by a box blur of 2 pixels for the modern look (its edge is cut at the
    /// middle of the blur, so it keeps the shape and loses the steps). Null without the files.
    /// </summary>
    private ShaderMaterial? BinocularMaskMaterial()
    {
        if (ReadFile?.Invoke("bmask640.msk") is not { } d640 || ReadFile("bmask800.msk") is not { } d800) return null;
        static ImageTexture Tex(byte[] px, int w, int h) => ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.L8, px));
        var hard = I76.Formats.Imaging.BinocularMask.Decode(d640, 640, 480);
        for (int i = 0; i < hard.Length; i++) hard[i] *= 255;
        var soft = Blur(I76.Formats.Imaging.BinocularMask.Decode(d800, 800, 600), 800, 600, 2);
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/binocular_mask.gdshader") };
        m.SetShaderParameter("mask_nearest", Tex(hard, 640, 480));
        m.SetShaderParameter("mask_soft", Tex(soft, 800, 600));
        return m;
    }

    /// <summary>A 0/1 mask box-blurred by r pixels each way (edges clamped), as bytes 0..255.</summary>
    private static byte[] Blur(byte[] mask, int w, int h, int r)
    {
        var a = new float[mask.Length];
        var b = new float[mask.Length];
        for (int i = 0; i < mask.Length; i++) a[i] = mask[i];
        float n = 2 * r + 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) s += a[y * w + System.Math.Clamp(x + k, 0, w - 1)];
                b[y * w + x] = s / n;
            }
        var o = new byte[mask.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) s += b[System.Math.Clamp(y + k, 0, h - 1) * w + x];
                o[y * w + x] = (byte)System.Math.Round(255 * s / n);
            }
        return o;
    }
}
