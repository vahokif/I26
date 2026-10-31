using Godot;
using I76.Sim.Weapons;
using I76.Sim.Vehicles;
using I76.Sim.Views;

namespace I76.Game.Ui;

/// <summary>
/// The wiggleburger cheat, after the world, the cockpit and the HUD are
/// drawn, blends the frame 50/50 (the world's translucency table, the original's bitmap blit mode 2) with itself moved up and left
/// by (x, y) pixels: double vision. x and y each ease towards a new rand() &amp; 15 every frame by the real frame
/// time (dt ∈ [0, 0.95] and at least 0.005 away, else they jump there). A hit of more than 20 on the player's
/// impact channel in the cockpit view turns it into a jolt instead: 19 px easing to
/// 0 at 2·dt (x, and half of it in y), which then switches the whole effect off, cheat included. The original does
/// this only on its software renderer; the port in both looks (its exact blit in the 1997 look, a smooth one in the modern look). The jolt is the Crash Blur setting:
/// when it is on a big cockpit hit starts it cheat or no cheat, as the original; when off it runs only while the cheat is on.
/// The state outlives the mission, as the original's globals do.
/// </summary>
public partial class Wiggle : CanvasLayer
{
    /// <summary>The state outlives the mission, as the original's globals do.</summary>
    private static readonly ScreenWobble State = new();
    /// <summary>The effect is on.</summary>
    public static bool On => State.On;
    /// <summary>Crash Blur resolved for this look (CrashBlurMode.Resolve): a big cockpit hit starts the jolt by itself.</summary>
    public static bool CrashBlur { get; set; }
    /// <summary>The 1997 look (either renderer): whole pixels, point-sampled, exactly the original's blit. The modern look: sub-pixel and filtered.</summary>
    public static bool Exact { get; set; }

    /// <summary>The cockpit view is up (the hud flag the jolt asks for).</summary>
    public Func<bool>? InCockpit { get; set; }
    public Func<Vehicle?>? Player { get; set; }

    private ColorRect _rect = null!;
    private ShaderMaterial _exact = null!, _soft = null!;

    private const string Shader = """
        shader_type canvas_item;
        uniform sampler2D screen : hint_screen_texture, FILTER;
        uniform vec2 shift;
        void fragment() {
            vec4 dst = texture(screen, SCREEN_UV);
            vec2 at = SCREEN_UV + shift * SCREEN_PIXEL_SIZE;
            // the blit copies only where the moved frame lands: beyond its edge the frame stays as it was
            if (at.x > 1.0 || at.y > 1.0) { COLOR = dst; }
            else { COLOR = vec4(mix(dst.rgb, texture(screen, at).rgb, 0.5), 1.0); }
        }
        """;

    /// <summary>Wiggleburger on or off.</summary>
    public static void Toggle() => State.Toggle();

    public override void _Ready()
    {
        Layer = 30;   // over the HUD and the cockpit, under the pictures and menus
        _exact = new ShaderMaterial { Shader = new Shader { Code = Shader.Replace("FILTER", "filter_nearest") } };
        _soft = new ShaderMaterial { Shader = new Shader { Code = Shader.Replace("FILTER", "filter_linear") } };
        _rect = new ColorRect { Material = _exact, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
    }

    /// <summary>A damage packet reaching a car (DamageModel.Damaging): the player's big impact in the cockpit starts the jolt.</summary>
    public void Damaging(Vehicle v, DamagePacket p)
    {
        if (!CrashBlur && !State.On) return;
        if (ScreenWobble.IsJoltHit(v == Player?.Invoke(), p.Mask, p.Ch[3], InCockpit?.Invoke() == true)) State.StartJolt();
    }

    private static readonly int CaptureAt = int.TryParse(System.Environment.GetEnvironmentVariable("I76_CRASH_BLUR_AT"), out int at) ? at : -1;
    private int _frames;

    public override void _Process(double delta)
    {
        // I76_CRASH_BLUR_AT=N (capture hook): at frame N the player takes the impact an AI car at 25 m/s head-on dealt (783), as the sim would send it
        if (CaptureAt >= 0 && ++_frames == CaptureAt)
        {
            var pk = new DamagePacket { Mask = 8 };
            pk.Ch[3] = 783;
            if (Player?.Invoke() is { } pv) Damaging(pv, pk);
        }
        _rect.Visible = State.On;
        if (!State.On) return;
        float dt = Math.Clamp((float)delta, 0.001f, 0.2f);   // the raw frame delta
        var (sx, sy) = State.Step(dt, Exact);
        _rect.Material = Exact ? _exact : _soft;
        // the frame the software renderer drew is 640x480; scale the shift with the window
        float scale = GetViewport().GetVisibleRect().Size.Y / 480f;
        ((ShaderMaterial)_rect.Material).SetShaderParameter("shift", new Vector2(sx, sy) * scale);
    }
}
