using Godot;

namespace I76.Game.World;

/// <summary>
/// The shadow of the mission sky's own clouds (docs/cloud-shadows.md). A shader slides a point of the world up
/// the sun onto the sky's dome and takes the sun out of it wherever the sheet shows cloud, so a shadow arrives
/// under the cloud hanging over it and the two drift together. Nothing is drawn and no light is cast: that is
/// what lets it cover the whole extended terrain and the distant ring without a shadow cascade, and cost a
/// texture fetch instead. Only the modern look asks for it — the 1997 look lights the world from the mission's
/// own ramp and has no sun to lose (docs/deviations.md).
/// The strength, the sky's fill and the look are decided here; the shaders only multiply by the result.
/// </summary>
public static class CloudShadow
{
    /// <summary>
    /// How much of the sun a thick cloud takes by day. What is left is the sky's fill, which the shaders give
    /// back, so this is the depth of the shade and not of black: 0.65 leaves a desert shade about half as bright
    /// as the sun beside it. I76_CLOUD_SHADOW overrides it (0 turns the feature off).
    /// </summary>
    public static readonly float DayStrength = Env("I76_CLOUD_SHADOW", 0.65f);

    /// <summary>
    /// The same by moonlight, which is a fiftieth of daylight and needs the ground lit enough to drive on.
    /// </summary>
    public static readonly float NightStrength = DayStrength * 0.4f;

    /// <summary>
    /// The sky's fill is the environment's ambient, which for the modern look is the sky's own radiance times
    /// AmbientLightEnergy; this scales the estimate the shaders add back (I76_CLOUD_FILL). Too high and a shade
    /// looks milky, too low and it looks like a bruise: matched on captures, as the night sky's gain was.
    /// </summary>
    public static readonly float FillGain = Env("I76_CLOUD_FILL", 1.0f);

    private static float _strength;       // the mission's, before the look turns it off
    private static bool _mode1997;
    private static Vector3 _fill;

    /// <summary>
    /// The cloud sheet, its dome and its levels come off the mission's modern sky material, so the shadow
    /// cannot drift from the sky it belongs to: same texture, same dome scale, same scroll, same thresholds.
    /// Null, or a sky with no sheet, and there is nothing to cast a shadow.
    /// </summary>
    public static void Configure(ShaderMaterial? sky, Color skyMean)
    {
        if (sky is null) return;
        if (sky.GetShaderParameter("clouds").As<Texture2D>() is not { } tex) return;
        // the sheet through the rendering server and not ObjectLight: what that remembers is put back over a live
        // mission, and a mission's texture freed by then would go with it
        RenderingServer.GlobalShaderParameterSet("i76_cloud_tex", tex);
        var radius = Param(sky, "radius", 6000.0f);
        var drop = Param(sky, "drop", 5400.0f);
        ObjectLight.Set("i76_cloud_dome", new Vector2(radius, drop));
        ObjectLight.Set("i76_cloud_scroll", Param(sky, "scroll_per_second", 0.02997f));
        // the sheet's open-sky and cloud levels, measured per mission by MissionBuilder.MeasureSky
        ObjectLight.Set("i76_cloud_span", new Vector2(Param(sky, "sky_lum", 0.15f), Param(sky, "cloud_lum", 0.8f)));
        var mean = skyMean.SrgbToLinear();   // the shader works in linear, the measured colours are stored sRGB
        _fill = new Vector3(mean.R, mean.G, mean.B) * FillGain;
    }

    /// <summary>
    /// The sun (Godot frame, toward it, as the object shaders have it), whether it is day, and how much ambient
    /// the environment sends with that sky. Called whenever the sky is rebuilt, which is how the strength and
    /// the fill follow the hour.
    /// </summary>
    public static void SetSun(Vector3 sun, bool day, float ambientEnergy)
    {
        _strength = day ? DayStrength : NightStrength;
        ObjectLight.Set("i76_cloud_sun", sun);
        // the fill is what the sky gives a surface per unit albedo, and the environment's ambient is that
        ObjectLight.Set("i76_cloud_fill", _fill * ambientEnergy);
        Apply();
    }

    /// <summary>The 1997 look: no sun, so nothing for a cloud to step in front of.</summary>
    public static void SetLook(bool mode1997)
    {
        _mode1997 = mode1997;
        Apply();
    }

    /// <summary>
    /// Rebase moved a new part of the map to the centre; the pattern stays where the map has it, so a shadow
    /// does not jump sideways as the trip covers a tile.
    /// </summary>
    public static void SetOrigin(System.Numerics.Vector3 gameOrigin) =>
        ObjectLight.Set("i76_cloud_anchor", I76.Game.Sim.Coords.ToGodot(gameOrigin));

    private static void Apply() => ObjectLight.Set("i76_cloud_shadow", _mode1997 ? 0.0f : _strength);

    /// <summary>The look's and the mission's switch for scenes that light themselves: the 3D garage, the map editor.</summary>
    public static void Off() => RenderingServer.GlobalShaderParameterSet("i76_cloud_shadow", 0.0f);

    private static float Param(ShaderMaterial m, string name, float fallback) =>
        m.GetShaderParameter(name).VariantType == Variant.Type.Float ? m.GetShaderParameter(name).AsSingle() : fallback;

    private static float Env(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? Mathf.Clamp(v, 0.0f, 1.0f) : fallback;
}
