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
    /// <summary>A network game (melee or co-op), and whether its host allows the chase crosshair (<see cref="I76.Sim.Views.CrosshairPolicy"/>).</summary>
    public Func<bool>? Network { get; set; }
    public Func<bool>? HostAllowsCrosshair { get; set; }
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
    /// <summary>True while the debug text shows (Ctrl+F3): the port's CB line rides with it.</summary>
    public Func<bool>? Debug { get; set; }
    private Label _banner = null!, _cb = null!;
    /// <summary>A file from the game's data by name (the binoculars' masks), or null.</summary>
    public Func<string, byte[]?>? ReadFile { get; set; }
    private ColorRect? _binoMask;

    public override void _Ready()
    {
        // the crosshair under the 2D instrument panels, the target brackets over them, the handset over all
        if (Combat is not null && Origin is not null)
            AddChild(new ChaseCrosshair { Player = () => Car?.Invoke(), Combat = Combat, Origin = Origin, CarOf = CarOf, Pit = Pit, InCockpit = () => InCockpit?.Invoke() == true, Faithful = () => Faithful?.Invoke() == true, Name = "Crosshair" });
        if (Pit is not null) AddChild(_instruments = new ExtInstruments { Pit = Pit, InCockpit = () => InCockpit?.Invoke() == true, Name = "ExtInstruments", Visible = false });
        _cb = Label(18);
        _cb.HorizontalAlignment = HorizontalAlignment.Left;
        _cb.AnchorTop = _cb.AnchorBottom = 1;
        _cb.OffsetLeft = 244; _cb.OffsetTop = -60;
        AddChild(_cb);
        if (Combat is not null && Origin is not null)
            AddChild(new TargetIndicator { Player = () => Car?.Invoke(), Combat = Combat, Origin = Origin, CarOf = CarOf, Name = "Target",
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
    }

    public override void _UnhandledInput(InputEvent e)
    {
    }

    /// <summary>The line on the CB (the objectives are the notepad's, N: MissionPicture).</summary>
    private void UpdateScript()
    {
        if (Script is null) { _banner.Visible = _cb.Visible = false; return; }
        var h = Script.Host;
        _cb.Visible = h.Cb.Current is not null && Debug?.Invoke() != false;
        if (h.Cb.Current is { } l) _cb.Text = $"CB: {(l.Speaker as I76.Sim.Missions.Entity)?.Name ?? "radio"} ({l.Name})";
        _banner.Visible = false;      // the end of a mission is the outro or the end menu (EscapeMenu), as the original's start-up does
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
        if (GetNodeOrNull<CanvasItem>("Crosshair") is { } ch)
            ch.Visible = !cut && I76.Sim.Views.CrosshairPolicy.Shown(ChaseCrosshair.Enabled, Network?.Invoke() == true, HostAllowsCrosshair?.Invoke() == true);
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

/// <summary>
/// A 3D-projected aiming crosshair for the F2 chase view (Mouse Options -> Chase Crosshair): where a round of the selected weapon
/// fired now would land, as the simulation's own hit test finds it (<see cref="I76.Sim.Weapons.Combat.LineOfFire"/>: the first
/// car, structure or ground along the muzzle's line, out to the weapon's reach), drawn on the car as it is shown. Red when that
/// round (or, with the weapons linked, one of the linked ones') would hit a car. Only for a weapon whose rounds fly straight from
/// the muzzle (<see cref="I76.Sim.Weapons.Combat.FliesStraight"/>: guns, cannons, the FireRite, the Ball O'Flame); none for a
/// lobbed, guided, flame or dropped one, where a line would mislead. View only: it asks and changes nothing.
/// </summary>
public partial class ChaseCrosshair : Control
{
    /// <summary>The player's choice, Play Options' Chase Crosshair (shell.cfg [port] chase_crosshair); online the host must allow it too.</summary>
    public static bool Enabled;
    public required Func<Vehicle?> Player { get; init; }
    public required I76.Sim.Weapons.Combat Combat { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public Func<Vehicle, Node3D?>? CarOf { get; init; }
    public Func<I76.Game.Sim.Cockpit?>? Pit { get; init; }
    public Func<bool>? InCockpit { get; init; }
    public Func<bool>? Faithful { get; init; }

    public override void _Ready()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public override void _Process(double delta) => QueueRedraw();

    /// <summary>The weapons that fire with the trigger now: the selected one, and with the link on the ones linked to it.</summary>
    private static IEnumerable<I76.Sim.Weapons.Weapon> Firing(I76.Sim.Weapons.Armory a)
    {
        if (a.Selected < 0 || a.Selected >= a.Weapons.Count) yield break;
        yield return a.Weapons[a.Selected];
        if (!a.Link) yield break;
        foreach (int i in a.Group)
            if (i != a.Selected && i >= 0 && i < a.Weapons.Count) yield return a.Weapons[i];
    }

    public override void _Draw()
    {
        if (!Enabled || InCockpit?.Invoke() == true || Player() is not { } p || I76.Sim.Weapons.Combat.IsDead(p) || p.Armory is not { } a) return;
        var cam = GetViewport().GetCamera3D();
        if (cam is not I76.Game.Camera.ChaseCamera { Fixed: null }) return;

        // the selected weapon's line, unless a linked one's would hit a car where it would not (the nearest such)
        I76.Sim.Weapons.Weapon? w = null;
        I76.Sim.Weapons.FireLine line = default;
        foreach (var f in Firing(a))
        {
            if (f.HpRatio <= 0 || Combat.LineOfFire(p, f) is not { } l) continue;
            if (w is null || (l.Car && (!line.Car || l.Distance < line.Distance))) (w, line) = (f, l);
        }
        if (w is null) return;

        // the line as the car is drawn (the view's pose of it, which the simulation's lags by up to a tick), at the distance found
        var node = CarOf?.Invoke(p) is { } n && IsInstanceValid(n) ? n : null;
        Vector3 muzzleG = node is not null ? node.GlobalTransform * I76.Game.Sim.Coords.ToGodot(w.MuzzlePos)
            : I76.Game.Sim.Coords.ToGodot(p.Pos.ToVector3() + p.Rot.ToWorld(w.MuzzlePos) - Origin());
        Vector3 dirG = node is not null ? (node.GlobalTransform.Basis * I76.Game.Sim.Coords.ToGodot(w.MuzzleRot.Forward)).Normalized()
            : I76.Game.Sim.Coords.ToGodot(p.Rot.ToWorld(w.MuzzleRot.Forward)).Normalized();
        if (dirG.LengthSquared() < 1e-4f) return;
        float dist = line.Distance;
        bool onTarget = line.Car;

        Vector3 aimG = muzzleG + dirG * dist;
        if (cam.IsPositionBehind(aimG)) return;
        var size = GetViewportRect().Size;
        var pos = cam.UnprojectPosition(aimG);
        if (pos.X < 0 || pos.Y < 0 || pos.X > size.X || pos.Y > size.Y) return;
        pos = pos.Round();

        float u = Mathf.Max(1f, size.Y / 480f);
        float gap = Mathf.Round(3.5f * u), len = Mathf.Round(4f * u), hw = Mathf.Max(1f, Mathf.Round(0.85f * u));
        var col = onTarget ? new Color(1f, 0.25f, 0.2f, 0.72f) : new Color(0.95f, 0.95f, 0.95f, 0.48f);
        var border = new Color(0f, 0f, 0f, onTarget ? 0.35f : 0.22f);

        ReadOnlySpan<Rect2> bars =
        [
            new(pos.X - gap - len, pos.Y - hw, len, hw * 2f),
            new(pos.X + gap, pos.Y - hw, len, hw * 2f),
            new(pos.X - hw, pos.Y - gap - len, hw * 2f, len),
            new(pos.X - hw, pos.Y + gap, hw * 2f, len),
        ];
        foreach (var b in bars)
        {
            DrawRect(b.Grow(1f), border);
            DrawRect(b, col);
        }
    }
}
