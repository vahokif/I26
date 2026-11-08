using System.Numerics;
using I76.Sim.Vehicles;

namespace I76.Sim.Ai;

/// <summary>
/// The AI's target predictor: where a vehicle will be after a lead time chosen by the caller plus a fixed 0.09 s.
/// Used by lead pursuit, intercept, ram approach and the mortar and dropper fire gates. It reads the target's
/// position, velocity, speed, yaw rate, gear direction and rows; no acceleration, no shooter, no projectile.
/// </summary>
public static class TargetPredictor
{
    /// <summary>The fixed lead the predictor adds to every caller's time.</summary>
    public const float Lead = 0.09f;

    /// <summary>
    /// The position after tau + 0.09 s. Below 0.05 rad/s of yaw rate a straight line along the velocity (all three axes).
    /// Otherwise a point on the circle of radius speed / |yaw rate| whose centre lies that far along the right row
    /// (x and z only) on the side given by the yaw rate's sign times the gear direction, at the angle
    /// tau' x side x yaw rate from the car, the local offset turned by the car's full rows. As the original's, the
    /// angle's sign keeps the gear but loses the turn: a car whose centre is on its right (turning right in a forward
    /// gear, or left in reverse) is carried back round its circle, towards where it was; turning the other way it is
    /// carried along its travel. In gear 0 the side is 0 and the point is the radius straight ahead, whatever the time.
    /// </summary>
    public static Vector3 Predict(Vehicle v, float tau)
    {
        float t = tau + Lead;
        var p = v.Pos.ToVector3();
        float w = v.AngVel.Y;
        if (MathF.Abs(w) < 0.05f) return p + v.Vel * t;
        float side = (w <= 0 ? 1f : -1f) * v.Gear;
        float r = v.Speed / MathF.Abs(w);
        float a = t * side * w - side * (MathF.PI / 2);
        var centre = p + new Vector3(v.Rot.Right.X, 0, v.Rot.Right.Z) * (side * r);
        return centre + v.Rot.ToWorld(new Vector3(MathF.Sin(a) * r, 0, MathF.Cos(a) * r));
    }
}
