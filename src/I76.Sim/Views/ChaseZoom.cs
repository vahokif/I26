namespace I76.Sim.Views;

/// <summary>
/// The port's additions to the chase view (F2), all view-only: the mouse wheel's zoom and the mouse orbit's return behind the car.
/// Each is a factor on the original's camera, which stays exactly as it was at the defaults.
/// </summary>
public static class ChaseZoom
{
    /// <summary>The wheel's distance factor on the original's distance: 1 is that distance; the bounds are close behind the car and well back.</summary>
    public const float Default = 1f, Min = 0.35f, Max = 2.5f;
    /// <summary>One wheel notch scales the distance by this (up is nearer).</summary>
    public const float Step = 1.15f;

    public static float Clamp(float scale) => float.IsFinite(scale) ? System.Math.Clamp(scale, Min, Max) : Default;

    /// <summary>The factor after a wheel turn of <paramref name="notches"/> (positive: up, nearer).</summary>
    public static float Notch(float scale, float notches) => Clamp(scale * MathF.Pow(Step, -notches));

    /// <summary>Eases <paramref name="cur"/> to <paramref name="want"/> over a frame, evenly in the ratio (so a zoom out and in feel alike).</summary>
    public static float Ease(float cur, float want, float dt, float rate = 9f)
    {
        float k = 1f - MathF.Exp(-rate * dt);
        return cur * MathF.Pow(want / cur, k);
    }

}

/// <summary>
/// The mouse orbit's offset from the car's rear: the motion adds to it, and after <see cref="IdleSeconds"/> without motion (mouse or stick) it eases back
/// to zero (unless the return is off: then it stays), as GTA V's vehicle camera does. The pitch offset is limited.
/// </summary>
public sealed class MouseOrbitOffset
{
    public const float IdleSeconds = 4.5f, ReturnRate = 3.5f, PitchDown = -0.45f, PitchUp = 0.8f;
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    private float _idle;

    public void Add(float yaw, float pitch)
    {
        if (yaw == 0 && pitch == 0) return;
        Yaw += yaw;
        Pitch = System.Math.Clamp(Pitch + pitch, PitchDown, PitchUp);
        _idle = 0;
    }

    public void Step(float dt, bool returnBehind = true)
    {
        _idle += dt;
        if (!returnBehind || _idle < IdleSeconds) return;
        // the short way round
        Yaw -= MathF.Round(Yaw / MathF.Tau) * MathF.Tau;
        float k = 1f - MathF.Exp(-ReturnRate * dt);
        Yaw -= Yaw * k;
        Pitch -= Pitch * k;
        if (MathF.Abs(Yaw) < 1e-4f) Yaw = 0;
        if (MathF.Abs(Pitch) < 1e-4f) Pitch = 0;
    }

    public void Reset() { Yaw = Pitch = 0; _idle = 0; }
}
