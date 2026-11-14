using System;
using I76.Formats.Input;

namespace I76.Sim.Views;

/// <summary>
/// The port's mouse orbit for the chase view (Mouse Camera) and in-car free look (Cockpit Mouse Look): relative mouse motion turned into
/// yaw and pitch targets. Moving the mouse right swings the view right; moving it up looks up. Invert Y flips the vertical.
/// </summary>
public static class MouseLook
{
    /// <summary>Off, then three preset speeds; the setting's legacy levels.</summary>
    public static readonly string[] Names = ["Off", "Slow", "Normal", "Fast"];
    public const int Off = 0, Slow = 1, Normal = 2, Fast = 3;

    /// <summary>Sensitivity scale: 1.0 (100%) is Normal (0.003 rad/count), stepped in 10% increments from 10% to 200%.</summary>
    public const float DefaultSensitivity = 1.0f, MinSensitivity = 0.1f, MaxSensitivity = 2.0f, SensitivityStep = 0.1f;
    public const float NormalRate = 0.003f;

    /// <summary>Radians of orbit per mouse count at a preset level (Normal is about 0.17 degrees a count).</summary>
    public static float Rate(int level) => level switch { Slow => 0.0015f, Normal => NormalRate, Fast => 0.006f, _ => 0f };

    /// <summary>Clamps a sensitivity factor to [10%, 200%] rounded to the nearest 10% step.</summary>
    public static float ClampSensitivity(float s) => MathF.Round(System.Math.Clamp(s, MinSensitivity, MaxSensitivity) * 10f) / 10f;

    /// <summary>Steps the sensitivity by <paramref name="dir"/> · 10%, wrapping or clamping at [10%, 200%].</summary>
    public static float NextSensitivity(float current, int dir, bool wrap = false)
    {
        int pct = (int)MathF.Round(ClampSensitivity(current) * 100f) + System.Math.Sign(dir) * 10;
        if (wrap)
        {
            if (pct > 200) pct = 10;
            else if (pct < 10) pct = 200;
        }
        return ClampSensitivity(pct / 100f);
    }

    /// <summary>The sensitivity factor a legacy Mouse Camera level (1 Slow, 2 Normal, 3 Fast) maps to when migrating settings.</summary>
    public static float SensitivityFromLevel(int level) => level switch { Slow => 0.5f, Fast => 2.0f, _ => DefaultSensitivity };

    /// <summary>The legacy Mouse Camera level nearest a sensitivity factor (or Off when disabled).</summary>
    public static int LevelFromSensitivity(bool enabled, float sensitivity) =>
        !enabled ? Off : sensitivity <= 0.75f ? Slow : sensitivity >= 1.5f ? Fast : Normal;

    /// <summary>The change in the chase camera's (yaw, pitch) targets for a motion of (dx, dy) counts, game signs (yaw+ swings the camera to the car's right, pitch+ raises it).</summary>
    public static (float Yaw, float Pitch) Orbit(float dx, float dy, int level, bool invertY)
    {
        float r = Rate(level);
        return (-dx * r, (invertY ? -dy : dy) * r);
    }

    /// <summary>The change in the chase camera's (yaw, pitch) targets at a fine-grained sensitivity scale (1.0 = NormalRate).</summary>
    public static (float Yaw, float Pitch) Orbit(float dx, float dy, bool enabled, float sensitivity, bool invertY)
    {
        if (!enabled) return (0f, 0f);
        float r = NormalRate * ClampSensitivity(sensitivity);
        return (-dx * r, (invertY ? -dy : dy) * r);
    }
}

/// <summary>
/// The port's mouse free-look offset in the 3D cockpit: accumulates head (yaw, pitch) in radians from <see cref="MouseLook.Orbit"/> deltas
/// (where orbit yaw is negated so moving the mouse right turns the pilot's head right, and pitch+ looks down toward the dash). Eases back
/// to forward after <see cref="ReturnDelay"/> seconds when <c>autoReturn</c> (Camera Return) is on, and clears when a glance key is held.
/// Looking down is held to <see cref="CockpitReach"/> at the head's yaw, so the cut-off ends of the arms never show (#165).
/// </summary>
public sealed class MouseCockpitOffset
{
    public const float ReturnDelay = 1.5f;
    public const float ReturnHalfLife = 0.25f;
    /// <summary>Reach in radians: ±160° yaw (to the side windows and over the shoulder), −22° up and +25° down from the −13° rest pitch (less to the sides: <see cref="MaxDownAt"/>).</summary>
    public const float MaxYaw = 160f * MathF.PI / 180f;
    public const float MaxUp = 22f * MathF.PI / 180f;
    public const float MaxDown = 25f * MathF.PI / 180f;

    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Idle { get; private set; } = ReturnDelay;
    public bool HasOffset => Yaw != 0f || Pitch != 0f;

    /// <summary>How far below the rest pitch the head may look at <paramref name="yaw"/> (radians): <see cref="MaxDown"/>, or less where <see cref="CockpitReach"/> says.</summary>
    public static float MaxDownAt(float yaw) =>
        MathF.Min(MaxDown, (CockpitReach.MaxPitch(yaw * 180f / MathF.PI) - FreeLook.Rest) * MathF.PI / 180f);

    public void Reset()
    {
        Yaw = Pitch = 0f;
        Idle = ReturnDelay;
    }

    /// <summary>Steps one frame of cockpit mouse look from the <see cref="MouseLook.Orbit"/> deltas <paramref name="orbitYaw"/> and <paramref name="orbitPitch"/>.</summary>
    public void Step(float dt, bool autoReturn, bool keyHeld, float orbitYaw, float orbitPitch)
    {
        if (keyHeld) { Reset(); return; }
        if (orbitYaw != 0f || orbitPitch != 0f)
        {
            Yaw = System.Math.Clamp(Yaw - orbitYaw, -MaxYaw, MaxYaw);
            Pitch = System.Math.Clamp(Pitch + orbitPitch, -MaxUp, MathF.Max(-MaxUp, MaxDownAt(Yaw)));
            Idle = 0f;
            return;
        }
        if (!autoReturn) return;
        Idle += dt;
        if (Idle < ReturnDelay) return;
        float k = MathF.Pow(0.5f, dt / ReturnHalfLife);
        Yaw *= k;
        Pitch *= k;
        if (MathF.Abs(Yaw) < 1e-4f) Yaw = 0f;
        if (MathF.Abs(Pitch) < 1e-4f) Pitch = 0f;
        Pitch = MathF.Min(Pitch, MathF.Max(-MaxUp, MaxDownAt(Yaw)));   // easing back round may pass a heading that allows less
    }
}
