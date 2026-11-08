using Godot;
using I76.Game.Sim;
using I76.Net.Hud;

namespace I76.Game.Ui;

/// <summary>
/// The external instruments:
/// with Graphic Detail's Ext. Instrum. Disp. on, every view but the cockpit
/// gets the 2D dash's panels laid flat over it: radar, damage panel, compass,
/// speedometer, tachometer, PRND21, specials and weapons, each the top-left
/// w×h of its live texture at its place on the 640x480 frame (mode 5's row of
/// the layout table). The original draws them at their own size whatever the
/// display; here the 640x480 frame is scaled with the window's height and
/// centred, as the CB handset is, nearest-neighbour in both looks. Palette
/// index 255 is transparent.
///
/// The same panels are the cockpit's 2D dash (dash3d == 0, F1 twice): all nine
/// (the mirror too, from its live view), the reticles, and none of
/// them while the head is turned from forward; the CB handset is the Hud's. The maps keep their rows bottom-up, so each
/// region is the stored image's last rows, drawn flipped.
///
/// The ghost cockpit (']' over the 3D dash) is the same overlay with only its first three panels and the reticles.
/// </summary>
public partial class ExtInstruments : Control
{
    public required Func<Cockpit?> Pit { get; init; }
    /// <summary>The cockpit view is current: the 2D dash rather than the external instruments.</summary>
    public required Func<bool> InCockpit { get; init; }

    private static ExtInstruments? _live;

    /// <summary>
    /// The 2D instruments' places in window pixels while they are drawn (the external ones in a chase view, or the 2D dash), or
    /// none: the network HUD keeps its side columns clear of them (HudLayout.TopInstruments).
    /// </summary>
    public static HudRect[] Blocks(float w, float h, bool mirror = true)
    {
        if (_live is not { } l || !l.IsInsideTree() || l.Pit() is not { } pit) return [];
        bool cockpit = l.InCockpit();
        if (cockpit && pit.Binoculars) return [];
        // the 3D cockpit's instruments are the car's model at the same places; the 2D dash and the chase view's own set are these panels
        if (cockpit && pit.Dash3D && !pit.Ghost) return HudLayout.TopInstruments(w, h, mirror && pit.MirrorFace is not null, dash3d: true);
        if (cockpit && pit.Ghost) return HudLayout.TopInstruments(w, h, false);
        return l.Visible ? HudLayout.TopInstruments(w, h, mirror && cockpit && pit.MirrorFace is not null) : [];
    }

    public override void _EnterTree() => _live = this;
    public override void _ExitTree() { if (_live == this) _live = null; }

    private ColorRect? _mirror;
    private ShaderMaterial? _mirrorMat;

    /// <summary>In draw order; a null panel is the radar's current frame.</summary>
    private static readonly (string? Panel, int X, int Y, int W, int H)[] Slots =
    [
        (null, 0, 0, 174, 119),
        ("zsy_.map", 392, 0, 241, 123),
        ("zcm_.map", 15, 433, 64, 64),        // no clip: the whole texture, its transparent foot off the frame
        ("zspeedo6.map", 220, 373, 80, 80),
        ("ztach6.map", 350, 373, 80, 80),
        ("zgear101.map", 220, 452, 213, 30),
        ("zbks_.map", 458, 404, 183, 79),
        ("zbk_.map", 190, 0, 183, 127),
    ];

    public override void _Ready()
    {
        AnchorRight = 1; AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
        _mirrorMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/mirror_2d.gdshader") };
        _mirror = new ColorRect { Name = "Mirror", Material = _mirrorMat, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        AddChild(_mirror);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        QueueRedraw();
        // slot 7, the mirror (hud on, so the 2D dash only), at (465, 125), not while the head is turned. The original's
        // 175x55 clip ran off the 640 frame's right edge and cut the bezel; here the whole 256x64 art is drawn, moved
        // left until its right end is on the frame (x 384)
        var pit = Pit();
        bool show = InCockpit() && pit is not null && !pit.Ghost && !pit.LookingAway && pit.MirrorFace is not null;
        _mirror!.Visible = show;
        if (!show) return;
        var (view, art) = pit!.MirrorFace!.Value;
        float k = GetViewportRect().Size.Y / 480f;
        _mirror.Position = new Vector2((GetViewportRect().Size.X - 640 * k) / 2 + (640 - 256) * k, 125 * k);
        _mirror.Size = new Vector2(256, 64) * k;
        _mirrorMat!.SetShaderParameter("art", art);
        _mirrorMat.SetShaderParameter("view", view);
        _mirrorMat.SetShaderParameter("view_nearest", view);
        _mirrorMat.SetShaderParameter("faithful", pit.Faithful);
    }

    public override void _Draw()
    {
        if (Pit() is not { } pit) return;
        var size = GetViewportRect().Size;
        float k = size.Y / 480f;
        var origin = new Vector2((size.X - 640 * k) / 2, 0);
        bool dash = InCockpit();
        int slot = 0;
        foreach (var (panel, x, y, w, h) in Slots)
        {
            if (dash && pit.Ghost && slot++ == 3) break;   // the ghost cockpit: radar, damage diagram and compass only
            if (dash && pit.LookingAway) break;   // The nine panels wait for the head to come forward
            // a panel not drawn into yet is the original's empty descriptor: its slot is skipped
            if ((panel is null ? pit.RadarTexture : pit.Panels.Texture(panel)) is not { } tex) continue;
            int th = tex.GetHeight();
            // y flipped about the slot: local row 0 (stored row th − h, the picture's row h − 1) lands at the slot's foot
            DrawSetTransformMatrix(new Transform2D(new Vector2(k, 0), new Vector2(0, -k), origin + new Vector2(x, y + h) * k));
            DrawTextureRectRegion(tex, new Rect2(0, 0, w, h), new Rect2(0, th - h, w, h));
        }
        DrawSetTransformMatrix(Transform2D.Identity);
        // the reticles are drawn whatever the head does, the 2D dash and the ghost cockpit only
        if (dash && pit.Reticle() is { } r)
            DrawTextureRect(r.Tex, new Rect2(origin + new Vector2(r.X, r.Y) * k, new Vector2(32, 32) * k), false);
    }
}
