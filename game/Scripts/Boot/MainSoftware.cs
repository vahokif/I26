using Godot;
using I76.Formats.Vfs;
using I76.Game.Ui;
using I76.Game.World;

namespace I76.Game.Boot;

/// <summary>
/// The 1997 look's Software renderer and the modern look's palette grade (SoftwareLook, Graphic Detail's
/// "1997 Renderer" and "Palette Grade"). I76_SOFTWARE=0/1 and I76_PALETTE_GRADE=0/1
/// override the settings for a run (captures).
/// </summary>
public partial class Main
{
    private SoftwareLook? _software;
    /// <summary>The 3D card's light ramp (LumaRamp), which the Software renderer swaps out.</summary>
    private Texture2D? _cardRamp;

    private static bool? EnvSwitch(string name) => System.Environment.GetEnvironmentVariable(name) switch
    {
        "1" => true, "0" => false, _ => null,
    };

    /// <summary>After the sky (ApplySky): the mission's miss8 counterpart and the software look's tables.</summary>
    private void AddSoftwareLook(GameInstall install, AssetVfs vfs)
    {
        if (_builder?.World is not { } w || _builder.Resolver is null) return;
        _software = new SoftwareLook { Name = "SoftwareLook" };
        AddChild(_software);
        _software.Load(install, vfs, _builder.Resolver, w.Mission.Name, w.PaletteName, _builder.Settings?.Luminance);
        ApplySoftware();
    }

    /// <summary>Apply the two settings for the current look (on a settings change, a look change, the start).</summary>
    private void ApplySoftware(I76.Game.Shell.ShellSettings? s = null)
    {
        s ??= I76.Game.Shell.ShellSettings.Load();
        // Crash Blur: the software renderer's double image on a big cockpit hit; I76_CRASH_BLUR=0/1 overrides
        bool softRenderer = EnvSwitch("I76_SOFTWARE") ?? s.SoftwareRenderer, soft1997 = _mode1997 && softRenderer;
        Wiggle.CrashBlur = EnvSwitch("I76_CRASH_BLUR") ?? I76.Sim.Views.CrashBlurMode.Resolve(s.CrashBlur);
        Wiggle.Exact = _mode1997;   // the original's whole-pixel blit in the 1997 look (either renderer), smooth in the modern look
        if (_software is null || _cardRamp is null) return;
        bool sw = EnvSwitch("I76_SOFTWARE") ?? s.SoftwareRenderer, grade = EnvSwitch("I76_PALETTE_GRADE") ?? s.PaletteGrade;
        int level = s.Graphics[0xc];
        _software.Apply(_mode1997, sw, grade, _cardRamp, _sky1997, _builder?.SkyTexture, _env1997, CardCurve(level), level);
    }

    private System.Numerics.Vector2 _bandAt = new(float.NaN, float.NaN);
    private float _bandGround;

    /// <summary>
    /// The Software renderer's sky band (sky_dome.gdshader): the camera's forward every frame, and the lowest ground within
    /// the 600 clip (HorizonEdge.Lowest, the original's least drawn terrain height), measured again each 60 m the eye moves.
    /// </summary>
    private void UpdateSoftwareBand()
    {
        if (!SoftwareLook.Active || _sky1997 is null || _builder?.World is not { } w) { _bandAt = new(float.NaN, float.NaN); return; }
        var g = CameraGame();
        var here = new System.Numerics.Vector2(g.X, g.Z);
        if (float.IsNaN(_bandAt.X) || System.Numerics.Vector2.Distance(here, _bandAt) >= 60f)
        {
            _bandAt = here;
            _bandGround = I76.Formats.Assembly.HorizonEdge.Lowest(w, g.X, g.Z);
        }
        SoftwareLook.UpdateBand(_sky1997, -ActiveCamera.GlobalBasis.Z, _bandGround);
    }
}
