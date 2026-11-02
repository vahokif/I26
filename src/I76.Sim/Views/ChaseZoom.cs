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

    /// <summary>Motion elsewhere (the other of mouse and stick, <see cref="ChaseOrbit"/>): the return waits as if this had moved.</summary>
    public void Touch() => _idle = 0;
}

/// <summary>
/// The chase view's (F2) turn off its track keys' targets. The pad's right stick (and any axis bound to the camera) is an offset
/// on those targets eased with them at 2·dt, the short way round, as the track keys always were. The mouse (Mouse Camera) is an
/// offset of its own on top, eased at <see cref="MouseEase"/>·dt so the view follows the hand closely. Both go back behind the car
/// a few seconds after the last of them moved (Camera Return). Without mouse motion the view is the stick's and keys' alone.
/// </summary>
public sealed class ChaseOrbit
{
    public const float MinPitch = 0.017453292f, MaxPitch = 1.5707963f, MouseEase = 18f;
    private readonly MouseOrbitOffset _stick = new(), _mouse = new();

    /// <summary>The eased pitch and yaw of the keys' targets with the stick's offset.</summary>
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    /// <summary>The mouse's eased offset on top of them.</summary>
    public float MousePitch { get; private set; }
    public float MouseYaw { get; private set; }

    /// <summary>The view's pitch (clamped as the original clamps its target) and yaw.</summary>
    public (float Pitch, float Yaw) View => (System.Math.Clamp(Pitch + MousePitch, MinPitch, MaxPitch), Yaw + MouseYaw);

    /// <summary>A frame's motion: the stick's turn and the mouse's (radians), and the return behind the car (Camera Return).</summary>
    public void Move(float stickYaw, float stickPitch, float mouseYaw, float mousePitch, float dt, bool returnBehind)
    {
        _stick.Add(stickYaw, stickPitch);
        _mouse.Add(mouseYaw, mousePitch);
        if (mouseYaw != 0 || mousePitch != 0) _stick.Touch();
        if (stickYaw != 0 || stickPitch != 0) _mouse.Touch();
        _stick.Step(dt, returnBehind);
        float before = _mouse.Yaw;
        _mouse.Step(dt, returnBehind);
        // the return takes whole turns off the mouse's offset (the short way round): the eased value drops them too, so it does not spin back
        MouseYaw += MathF.Round((_mouse.Yaw - before) / MathF.Tau) * MathF.Tau;
    }

    /// <summary>Both offsets back to nothing at once (a new start, the reset key, another view).</summary>
    public void Reset()
    {
        _stick.Reset();
        _mouse.Reset();
        MousePitch = MouseYaw = 0;
    }

    /// <summary>A frame's easing toward the keys' targets (<paramref name="pitchT"/> already clamped).</summary>
    public void Ease(float pitchT, float yawT, float dt)
    {
        float f = dt + dt;
        float pitchWant = System.Math.Clamp(pitchT + _stick.Pitch, MinPitch, MaxPitch);
        float yawWant = yawT + _stick.Yaw;
        Pitch = EaseTo(Pitch, pitchWant, f);
        // the short way round to the target, so a wrapped offset (the camera swung several turns) never spins back
        Yaw = EaseTo(Yaw, Yaw + Wrap(yawWant - Yaw), f);
        float mf = MathF.Min(0.95f, MouseEase * dt);
        MousePitch = EaseTo(MousePitch, _mouse.Pitch, mf);
        MouseYaw = EaseTo(MouseYaw, _mouse.Yaw, mf);
    }

    /// <summary>The original's easing: a fraction <paramref name="f"/> of the way, or there when close or when f is out of 0..0.95.</summary>
    public static float EaseTo(float cur, float want, float f) =>
        f >= 0 && f <= 0.95f && MathF.Abs(want - cur) >= 0.005f ? f * (want - cur) + cur : want;

    /// <summary>An angle into −π..π, as the engine's wrap does it (what the chase view always used).</summary>
    public static float Wrap(float a)
    {
        const float min = -MathF.PI, range = MathF.PI - min;
        return min + ((a - min) % range + range) % range;
    }
}
