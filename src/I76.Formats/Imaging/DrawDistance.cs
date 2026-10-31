namespace I76.Formats.Imaging;

/// <summary>
/// The port's Draw Distance (Graphic Detail), independent of the look: Original draws terrain and objects only to the
/// original's far clip (the world camera rebuild: 600 with Visibility Range on) with the sky's horizon beyond; Far draws to
/// 20 km as this port always did. View only: the game's own ranges (AI, weapons, visibility checks) are unaffected.
/// </summary>
public static class DrawDistance
{
    public const int Original = 0, Far = 1;
    public static readonly string[] Names = ["Original", "Far"];

    /// <summary>The original's far clip, metres from the eye.</summary>
    public const float OriginalClip = 600f;
    public const float FarClip = 20000f;
    /// <summary>The modern look's distance fade at Original: clear to FogBegin, wholly sky by FogEnd, a little inside the clip so no edge shows.</summary>
    public const float FogBegin = 200f, FogEnd = 560f;

    /// <summary>A stored value as a setting: anything unknown or missing is Far, the default.</summary>
    public static int Parse(int? raw) => raw is Original ? Original : Far;

    /// <summary>The camera's far plane for a setting.</summary>
    public static float CameraFar(int setting) => setting == Original ? OriginalClip : FarClip;

    /// <summary>Whether the modern look fades its distance into the sky (it hides the clip at Original; at Far nothing is clipped).</summary>
    public static bool ModernFog(int setting) => setting == Original;

    /// <summary>
    /// Whether the modern sky draws the mission's horizon panels: at Original, in place of the 3D ring, and only for a mission that has panels
    /// (a Nitro arena may name none, n01 and n03 among them; the unset texture array would read as white).
    /// </summary>
    public static bool ModernPanels(int setting, bool missionHasPanels) => ModernFog(setting) && missionHasPanels;
}
