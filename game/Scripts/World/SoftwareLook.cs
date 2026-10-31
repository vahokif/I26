using Godot;
using I76.Formats.Imaging;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Game.World;

/// <summary>
/// The 1997 look's Software renderer and the modern look's palette grade
/// (docs/deviations.md). The original drew a mission either through a 3D card (the 1997 look as it stands) or
/// through its own 8-bit rasteriser, from the miss8 copy of the mission: its own palette (t01.act where the card
/// had t01_16.act), sky (nk_1…) and ground family (tt…sd_), every pixel a palette index, light through the .lum
/// rows (a highlight toward white above row 7, a shade below), textures point-sampled, terrain lit once per face,
/// 640x480. Software on in the 1997 look: those assets, the software ramp (SoftwarePalette.Ramp) in place of the
/// card's, point sampling and the faces' light (software.gdshaderinc), then one fullscreen pass to the nearest
/// palette colour on a 480-line grid (software_grade.gdshader), the palette's Monitor Brightness curve after it
/// (level 3 the identity) instead of the 3D card's curve. The palette grade in the modern look: that
/// last pass alone, over the modern frame. Software also brings the 1997 CD build's sky band in place of the horizon
/// panels (sky_dome.gdshader, SoftwarePalette.SkyGradient) and its green radar blips (Cockpit, SoftwarePalette.RadarGreen).
/// </summary>
public partial class SoftwareLook : Node
{
    /// <summary>The software renderer's frame height: 640x480, the only mode it ran in.</summary>
    public const int Lines = 480;

    private Palette? _palette;
    private byte[]? _lum;
    private float[]? _ramp;
    private ImageTexture? _lut, _ground, _band;
    private Texture2D? _sky;
    private CanvasLayer? _layer;
    private ShaderMaterial? _grade;
    private System.Threading.Tasks.Task<byte[]>? _lutJob;
    private int _lutSide;
    private bool _software, _grading;
    /// <summary>A Nitro Pack install: its exe is the GOG build, whose software path draws the horizon panels; the CD's band is the base game's.</summary>
    private bool _gogHorizon;

    /// <summary>The Software renderer is drawing (the 1997 look, the setting on, the tables loaded): the radar's blips go green.</summary>
    public static bool Active { get; private set; }

    /// <summary>What the mission's miss8 copy named (null: the 16-bit mission's own, the copy missing).</summary>
    public string? PaletteName { get; private set; }
    public string? SkyName { get; private set; }
    public string? GroundName { get; private set; }

    /// <summary>
    /// The miss8 copy of <paramref name="mission"/> (else the 16-bit one's palette with any _16 dropped, as
    /// t01_16.act's software twin is t01.act), its palette, .lum, sky and ground family level 2, loaded through
    /// the mission's own resolver with the software palette. <paramref name="fallbackLum"/>: the 16-bit mission's .lum.
    /// </summary>
    public void Load(GameInstall install, AssetVfs vfs, I76.Formats.Assembly.TextureResolver resolver, string mission,
        string? fallbackPalette, string? fallbackLum)
    {
        _gogHorizon = install.IsNitro;
        WorldSettings? ws = null;
        if (install.Resolve("miss8", mission + ".msn") is { } p8)
            try { ws = WorldSettings.Read(new MissionFile(p8)); } catch (Exception e) { GD.Print($"software look: {p8}: {e.Message}"); }
        string? pal = ws?.Palette is { Length: > 0 } a ? a
            : fallbackPalette?.Replace("_16.", ".", StringComparison.OrdinalIgnoreCase);
        byte[]? pd = pal is null ? null : vfs.TryRead(pal);
        if (pd is null && fallbackPalette is not null) { pal = fallbackPalette; pd = vfs.TryRead(pal); }
        if (pd is not { Length: Palette.Size }) { GD.Print($"software look: no palette ({pal})"); return; }
        _palette = new Palette(pd);
        PaletteName = pal;
        _lum = (ws?.Luminance is { Length: > 0 } l ? vfs.TryRead(l) : null) ?? (fallbackLum is null ? null : vfs.TryRead(fallbackLum));
        var textures = new I76.Game.Assets.TextureFactory(resolver, _palette);
        if (ws?.Sky is { Length: > 0 } sky && resolver.FindByStem(sky) is { } sm && textures.Get(sm) is { } st) { _sky = st.Texture; SkyName = sky; }
        if (ws?.Ground is { Length: >= 8 } g && resolver.FindByStem(g[..4] + '2' + g[5..]) is { } gm && textures.Get(gm) is { } gt) { _ground = gt.Texture; GroundName = gm; }
        // the lookup off the main thread (64³ nearest searches, ~70 M distance sums)
        var palette = _palette;
        _lutJob = System.Threading.Tasks.Task.Run(() => SoftwarePalette.BuildLut(palette, 64, out _lutSide));
        if (_lum is not null)
        {
            var ramp = SoftwarePalette.Ramp(_palette, _lum);
            _ramp = new float[64];
            for (int j = 0; j < 32; j++) { _ramp[2 * j] = Byte8(ramp[j].A); _ramp[2 * j + 1] = Byte8(ramp[j].B); }
            // the sky band's 32 rows of palette 0xef
            var band = Image.CreateEmpty(32, 1, false, Image.Format.Rgb8);
            var rows = SoftwarePalette.SkyGradient(_palette, _lum);
            for (int j = 0; j < 32; j++) band.SetPixel(j, 0, Color.Color8(rows[j].R, rows[j].G, rows[j].B));
            _band = ImageTexture.CreateFromImage(band);
        }
        GD.Print($"software look: palette {PaletteName}, sky {SkyName ?? "-"}, ground {GroundName ?? "-"}");
    }

    /// <summary>A ramp value as the 8-bit texture it used to be held in kept it (the look's pixels stay as they were).</summary>
    private static float Byte8(float v) => (byte)Math.Clamp(v * 255.0, 0.0, 255.0) / 255f;

    public bool Ready => _palette is not null;

    /// <summary>
    /// Switch: <paramref name="software"/> the Software renderer (the 1997 look only), <paramref name="grade"/> the
    /// modern look's palette grade. The 3D card's ramp, the dome's own clouds and the card's transfer curve
    /// (<paramref name="cardRamp"/>, <paramref name="cardSky"/>, <paramref name="cardCurve"/>) are what it puts back. <paramref name="brightness"/>: Monitor Brightness, 1..10 (3 the shipped default).
    /// </summary>
    public void Apply(bool look1997, bool software, bool grade, float[] cardRamp, ShaderMaterial? dome, Texture2D? cardSky,
        Godot.Environment env1997, Texture? cardCurve, int brightness)
    {
        _software = Ready && look1997 && software;
        Active = _software;
        _grading = Ready && ((_software && !NoFinalPass) || (!look1997 && grade));
        RenderingServer.GlobalShaderParameterSet("i76_software", _software);
        I76.Game.World.ObjectLight.SetRamp(_software && _ramp is not null ? _ramp : cardRamp);
        if (_ground is not null) RenderingServer.GlobalShaderParameterSet("i76_sw_ground", _ground);
        RenderingServer.GlobalShaderParameterSet("i76_sw_ground_on", _software && _ground is not null);
        if (dome is not null && cardSky is not null) dome.SetShaderParameter("clouds", _software && _sky is not null ? _sky : cardSky);
        if (dome is not null)
        {
            // the band instead of the horizon panels (the 1997 CD build had no panels, and ran no Nitro Pack map; the GOG build's software path draws them, so a Nitro install keeps them: #103)
            if (_band is not null) dome.SetShaderParameter("band", _band);
            dome.SetShaderParameter("software_band", _software && _band is not null && !_gogHorizon);
            dome.SetShaderParameter("band_top", SoftwarePalette.GradientTop);
        }
        // the palette's own brightness (re-ramped after the match) in place of the card's curve (no display gamma)
        env1997.AdjustmentColorCorrection = _software ? null : cardCurve;
        env1997.AdjustmentBrightness = 1f;
        Brightness = brightness;
        EnsureLayer();
        _layer!.Visible = _grading;
        _grade!.SetShaderParameter("lines", _software ? LinesOverride ?? Lines : 0f);
        _grade.SetShaderParameter("gamma", _software ? Gamma(brightness) : 1f);
        _grade.SetShaderParameter("strength", _software ? 1f : GradeStrength);
        _grade.SetShaderParameter("dither", _software ? 0f : GradeDither);
    }

    private int Brightness { get; set; } = 3;

    /// <summary>The band's lines each frame: the camera's forward (Godot space) and the lowest ground near it (game height).</summary>
    public static void UpdateBand(ShaderMaterial dome, Vector3 forward, float lowestGround)
    {
        dome.SetShaderParameter("view_forward", forward);
        dome.SetShaderParameter("band_ground", lowestGround);
    }

    public override void _ExitTree() => Active = false;

    /// <summary>
    /// Monitor Brightness as the palette curve the original applies:
    /// γ 0.8, 0.9, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0, 2.2, 2.5 for 1..10, level 3 the identity (MonitorBrightness).
    /// </summary>
    public static float Gamma(int level) => (float)MonitorBrightness.PaletteGamma(level);

    /// <summary>I76_SW_LINES: the grid's height in software mode (0: the full resolution).</summary>
    private static readonly float? LinesOverride = float.TryParse(System.Environment.GetEnvironmentVariable("I76_SW_LINES"), out float v) ? v : null;
    /// <summary>I76_SW_GRADE=0: the Software renderer without its final palette pass (to see the shading alone).</summary>
    private static readonly bool NoFinalPass = System.Environment.GetEnvironmentVariable("I76_SW_GRADE") == "0";
    /// <summary>I76_PALETTE_GRADE_STRENGTH: how far the modern grade goes (1: the palette colour).</summary>
    private static readonly float GradeStrength = float.TryParse(System.Environment.GetEnvironmentVariable("I76_PALETTE_GRADE_STRENGTH"),
        System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : 1f;

    /// <summary>I76_PALETTE_GRADE_DITHER: the modern grade's ordered dither amplitude (0..1 of full scale).</summary>
    private static readonly float GradeDither = float.TryParse(System.Environment.GetEnvironmentVariable("I76_PALETTE_GRADE_DITHER"),
        System.Globalization.CultureInfo.InvariantCulture, out float dd) ? dd : 0.08f;

    private void EnsureLayer()
    {
        if (_layer is not null) return;
        // over the world, the cockpit and the HUD (an 8-bit screen had nothing else), under Wiggle (30) and the menus
        _layer = new CanvasLayer { Layer = 29, Name = "SoftwareGrade", Visible = false };
        _grade = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/software_grade.gdshader") };
        var rect = new ColorRect { Material = _grade, MouseFilter = Control.MouseFilterEnum.Ignore };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(rect);
        AddChild(_layer);
    }

    public override void _Process(double delta)
    {
        if (_lut is null && _lutJob is { IsCompleted: true } job && _grade is not null)
        {
            var img = Image.CreateFromData(_lutSide, _lutSide, false, Image.Format.Rgb8, job.Result);
            _lut = ImageTexture.CreateFromImage(img);
            _grade.SetShaderParameter("lut", _lut);
            _lutJob = null;
        }
        if (_layer is not null) _layer.Visible = _grading && _lut is not null;
    }
}
