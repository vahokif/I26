using System.Numerics;

namespace I76.Sim.Views;

/// <summary>
/// The in-car head sway: the driver's eye tilts with the car's acceleration
/// as if on a soft suspension. Three heights, left front, right front and back, ease towards targets made from
/// the acceleration along the body's forward and right axes; the plane through them at (∓2·track, 2·wheelbase) and
/// (0, −2·wheelbase) gives the up axis of the tilt. The camera applies it in the eye's frame after the head turn
/// (Euler · Sway · Eye). Game axes (x right, y up, z ahead); presentation only, nothing here is sim state.
/// </summary>
public sealed class HeadSway
{
    /// <summary>The gain on each acceleration component, m/s² to height (−0.008).</summary>
    public const float Gain = -0.008f;
    /// <summary>The back height is −16 times the forward term.</summary>
    public const float BackGain = -16f;

    /// <summary>The eased heights: left front, right front, back.</summary>
    public float Left, Right, Back;

    /// <summary>
    /// One render frame: a = −0.008·acc·forward, b = −0.008·acc·right; the targets a − b (left), a + b (right), −16a
    /// (back), each eased at f = 2·dt while 0 ≤ f ≤ 0.95 and it is at least 0.005 away, else taken at once.
    /// </summary>
    public void Step(float accForward, float accRight, float dt)
    {
        float a = accForward * Gain, b = accRight * Gain, f = 2 * dt;
        Left = Ease(Left, a - b, f);
        Right = Ease(Right, a + b, f);
        Back = Ease(Back, BackGain * a, f);
    }

    /// <summary>The same, from a world acceleration and the body's right and forward axes.</summary>
    public void Step(Vector3 accel, Vector3 right, Vector3 forward, float dt) =>
        Step(Vector3.Dot(accel, forward), Vector3.Dot(accel, right), dt);

    private static float Ease(float x, float target, float f) =>
        f >= 0 && f <= 0.95f && MathF.Abs(target - x) >= 0.005f ? x + f * (target - x) : target;

    /// <summary>Back to level (a new car, or the view entered afresh).</summary>
    public void Reset() => Left = Right = Back = 0;

    /// <summary>
    /// The tilt's up axis: the normal of the plane through A (2T, right, 2W), B (0, back, −2W) and C (−2T, left, 2W),
    /// normalize((B − A) × (C − A)), T the front track, W the wheelbase.
    /// </summary>
    public Vector3 Up(float track, float wheelbase)
    {
        var a = new Vector3(2 * track, Right, 2 * wheelbase);
        var b = new Vector3(0, Back, -2 * wheelbase);
        var c = new Vector3(-2 * track, Left, 2 * wheelbase);
        var n = Vector3.Cross(b - a, c - a);
        return n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    /// <summary>
    /// The tilt as rows (right, up, forward) in the eye's frame (the original's up/forward basis with forward (0, 0, 1)):
    /// right = normalize(up × ẑ), forward = right × up.
    /// </summary>
    public (Vector3 Right, Vector3 Up, Vector3 Forward) Basis(float track, float wheelbase)
    {
        var up = Up(track, wheelbase);
        var right = Vector3.Normalize(Vector3.Cross(up, Vector3.UnitZ));
        return (right, up, Vector3.Cross(right, up));
    }
}
