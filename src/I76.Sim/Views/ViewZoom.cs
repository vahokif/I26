namespace I76.Sim.Views;

/// <summary>
/// The mouse wheel's zoom in every external view, all view-only: a factor on the original's framing (1 is exactly that; the cockpit has none,
/// the wheel stays free there). Views that share one distance concept share one factor: F2, F3 and F9 are one camera (the chase camera, whose
/// distance the Page Up/Down keys also set), F7 and F8 stand at the same side offset, F4 and F5 are the same boom. F11's boom, F6's lens and
/// F10's height are each their own. The factor means a distance for the chase, target, projectile and side views, the tangent of the half
/// field of view for F6 (a lens: its eye is fixed in front of the car) and the height for F10. A wheel notch up is nearer / closer in.
/// </summary>
public static class ViewZoom
{
    public enum Kind { Chase, Target, Projectile, Side, Bumper, Overhead }
    public const int Count = 6;

    public const float Default = 1f;
    /// <summary>One wheel notch scales the factor by this (up is nearer).</summary>
    public const float Step = ChaseZoom.Step;

    public static float Min(Kind k) => k switch
    {
        Kind.Chase => ChaseZoom.Min,
        Kind.Target or Kind.Projectile => 0.4f,
        Kind.Side => 1f,         // the side camera's 1.5 m offset is just outside the car: any closer puts it in the body (out only)
        Kind.Bumper => 0.35f,    // tan of the half field: 120 degrees across becomes about 50
        _ => 0.1f,               // the overhead's own floor (4 radii) holds it above the car
    };

    public static float Max(Kind k) => k switch
    {
        Kind.Chase => ChaseZoom.Max,
        Kind.Target or Kind.Projectile => 3f,
        Kind.Side => 4f,
        Kind.Bumper => 1.2f,     // 120 degrees across becomes about 130
        _ => 6f,                 // 100 m to 600 m (the engine's 0.85 x far still caps it)
    };

    public static float Clamp(Kind k, float f) => float.IsFinite(f) ? System.Math.Clamp(f, Min(k), Max(k)) : Default;

    /// <summary>The factor after a wheel turn of <paramref name="notches"/> (positive: up, nearer).</summary>
    public static float Notch(Kind k, float f, float notches) => Clamp(k, f * MathF.Pow(Step, -notches));

    /// <summary>The F6 lens' vertical field in degrees: the original's 120 across a 4:3 view, its half-field tangent scaled by <paramref name="f"/>.</summary>
    public static float BumperFov(float f) => MathF.Atan(MathF.Tan(60f * MathF.PI / 180f) * 0.75f * f) * 2f * 180f / MathF.PI;
}
