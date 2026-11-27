using Godot;

namespace I76.Game.World;

/// <summary>
/// The haze lying over the desert, and the sun in it (docs/desert-haze.md). The air is counted as denser low
/// than high, so it pools in the valleys and the plains and thins off up the peaks, and the light it sends back
/// is brightest looking toward the sun: a horizon you drive into goes gold, the one behind you stays the plain
/// colour of the sky. Nothing is drawn and nothing is sampled — the pass over the scene (Shaders/sky_fog.gdshader)
/// counts the air along each ray and lays the sky's own colour in that direction over what it found, so the
/// whole extended map and the distant ring are hazed by the same few numbers, at any draw distance.
/// The numbers, the day and night shares, the reference level and the look are decided here; the shader only
/// reads them back. The 1997 look has no haze at all: the original drew the world to its 600 clip and the sky's
/// horizon panels beyond, with no air in between (docs/deviations.md).
/// </summary>
public static class DesertHaze
{
    /// <summary>
    /// What a metre of the air at the reference level takes, so a kilometre of level air at that height is worth
    /// 0.1 (1e-4 a metre): the reach a hazy desert valley is measured at, and the reason a car at 500 m is
    /// untouched while a range of hills 8 km off goes pale. I76_HAZE_DENSITY overrides it (0 turns the feature off).
    /// </summary>
    public static readonly float Density = Env("I76_HAZE_DENSITY", 1.0e-4f);

    /// <summary>
    /// The height of the layer, in metres: this much climb above the reference level leaves a third of the air
    /// below. 400 is the depth of the dust and heat a desert plain keeps under the open sky, and it puts the
    /// maps' own relief (a hundred metres or so between a wash and a ridge) across half of that.
    /// I76_HAZE_SCALE overrides it.
    /// </summary>
    public static readonly float ScaleHeight = Env("I76_HAZE_SCALE", 400.0f);

    /// <summary>
    /// How much of the tint the forward-scattering lobe adds by day (I76_HAZE_MIE, 0 leaves the haze the plain
    /// sky colour). A little over a half: the airlight is already the sky's own colour, and this is only the
    /// sun's share of it, which should be able to over-brighten a distant ridge without leaving it.
    /// </summary>
    public static readonly float Mie = Env("I76_HAZE_MIE", 0.55f);

    /// <summary>
    /// The same by moonlight: the moon is a fortieth of the sun and the light it leaves in the air is read as a
    /// cold wash over the distance rather than a colour, so it is kept well above a fortieth and well under day.
    /// </summary>
    public static readonly float NightMie = Mie * 0.2f;

    /// <summary>The lobe's forward bias (haze.gdshaderinc's HAZE_G), and the reference maths beside it.</summary>
    public const float G = 0.6f;

    /// <summary>How far below the reference level the law is trusted, in scale heights (haze.gdshaderinc's HAZE_CLIMB).</summary>
    public const float Climb = 1.2f;

    /// <summary>
    /// The sky's own two colours for the sun's light (sky_modern.gdshaderinc's tint, which is what the sun's disc
    /// and its glow are drawn with), so the lit air and the sun hanging in it cannot disagree.
    /// </summary>
    private static readonly Vector3 Low = new(1.0f, 0.55f, 0.28f);
    private static readonly Vector3 High = new(1.0f, 0.96f, 0.9f);

    /// <summary>And the moon's: cold, the sky's own night blue rather than the desert's ochre.</summary>
    private static readonly Vector3 Night = new(0.35f, 0.45f, 0.72f);

    private static bool _enabled = true;    // Graphic Detail's Desert Haze
    private static bool _mode1997;
    private static float _mie;              // the mission's, before the look and the setting turn it off
    private static float _ground;           // the mission's mean ground, in game metres
    private static Vector3 _origin;         // Coords.ToGodot of where Rebase put the map's centre

    /// <summary>
    /// The mission's mean ground height (MissionWorld.AverageGroundHeight), the level the air is counted from:
    /// a ray down a wash gathers air and a ray over a ridge from the same wash does not. Once per mission with
    /// it, as the mean walks every sample of the map — the horizon ring measures it the same way.
    /// </summary>
    public static void SetGround(float meanGameHeight)
    {
        _ground = meanGameHeight;
        PushFloor();
    }

    /// <summary>
    /// The sun or moon (Godot frame, toward it, as i76_light_dir has it) and whether it is day. The tint follows
    /// the sky's own rule for the height of the sun, so it is gold with the sun down and near white with it up.
    /// Called whenever the sky is rebuilt, which is how the haze follows the hour.
    /// </summary>
    public static void SetSun(Vector3 sun, bool day)
    {
        ObjectLight.Set("i76_haze_sun", sun);
        if (day)
        {
            ObjectLight.Set("i76_haze_tint", Low.Lerp(High, Mathf.SmoothStep(-0.02f, 0.15f, sun.Y)));
            _mie = Mie;
        }
        else
        {
            ObjectLight.Set("i76_haze_tint", Night);
            _mie = NightMie;
        }
        Apply();
    }

    /// <summary>
    /// Rebase moved a new part of the map to the centre; the haze's levels belong to the map, so the reference
    /// level moves with it and a ridge does not become a valley as the trip covers a tile.
    /// </summary>
    public static void SetOrigin(System.Numerics.Vector3 gameOrigin)
    {
        _origin = I76.Game.Sim.Coords.ToGodot(gameOrigin);
        PushFloor();
    }

    /// <summary>The 1997 look: the world is drawn to its clip and the panels beyond it, with no air in between.</summary>
    public static void SetLook(bool mode1997)
    {
        _mode1997 = mode1997;
        Apply();
    }

    /// <summary>
    /// Graphic Detail's Desert Haze. Off leaves the distance exactly as it was: the clip's fade to the sky at
    /// Original draw distance, and nothing at all laid over the far ground at Far.
    /// </summary>
    public static void SetEnabled(bool on)
    {
        _enabled = on;
        Apply();
    }

    /// <summary>Whether the pass over the scene has anything to draw for the haze's sake (Main.ApplyDrawDistance).</summary>
    public static bool Active => _enabled && !_mode1997 && Density > 0.0f;

    /// <summary>
    /// The shader's i76_haze_depth for an eye this many metres above the reference level: haze.gdshaderinc's
    /// maths in C#, which is what lets the numbers be looked at without a GPU. dirY is the ray's rise per metre
    /// and dist its length; the result is what exp(-) the pass takes with, 0 clear and 4 or so solid.
    /// </summary>
    public static float SlantDepth(float eyeAbove, float dirY, float dist)
    {
        if (Density <= 0.0f || dist <= 0.0f) return 0.0f;
        var h = Math.Max(ScaleHeight, 1.0f);
        var x = dirY * dist / h;
        var spread = Math.Abs(x) < 1e-3f ? 1.0f - 0.5f * x + x * x / 6.0f : (1.0f - MathF.Exp(-x)) / x;
        var atEye = MathF.Exp(-Math.Clamp(eyeAbove / h, -Climb, 30.0f));
        var tau = Density * dist * atEye * spread;
        return tau > 0.0f && !float.IsNaN(tau) ? MathF.Min(tau, 8.0f) : 0.0f;
    }

    /// <summary>How much of what the eye sees along that ray is the air: 1 - exp(-depth), the pass's own opacity.</summary>
    public static float Alpha(float eyeAbove, float dirY, float dist) => 1.0f - MathF.Exp(-SlantDepth(eyeAbove, dirY, dist));

    /// <summary>The shader's i76_haze_phase: the lobe over the cosine of the angle to the sun, 1 straight into it.</summary>
    public static float Phase(float towards)
    {
        var g2 = G * G;
        var f = (1.0f - g2) / MathF.Pow(Math.Max(1.0f + g2 - 2.0f * G * towards, 1e-4f), 1.5f);
        var straight = (1.0f - g2) / MathF.Pow(1.0f + g2 - 2.0f * G, 1.5f);
        return f / straight;
    }

    private static void PushFloor() => ObjectLight.Set("i76_haze_floor", _ground - _origin.Y);

    private static void Apply()
    {
        var on = Active;
        ObjectLight.Set("i76_haze_density", on ? Density : 0.0f);
        ObjectLight.Set("i76_haze_scale", ScaleHeight);
        ObjectLight.Set("i76_haze_mie", on ? _mie : 0.0f);
    }

    private static float Env(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && !float.IsNaN(v) && v >= 0.0f ? v : fallback;
}
