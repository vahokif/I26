using System.Numerics;
using I76.Sim.Math;

namespace I76.Sim.Vehicles;

/// <summary>
/// RESET_UPRIGHT (the port's key, U "The old F12 trick"): the car goes back on the bare terrain
/// directly under it (structures, bridges and ramps are not looked at), its up row the terrain's normal there, its heading
/// the one it had, at rest. This is the effect the original's F12 render-mode switch was said to have, as an explicit key.
/// The same placement as the script's teleport: position (x, z) kept and y the terrain's, velocity and
/// rotation rates zero. The car's damage is untouched.
/// </summary>
public static class UprightReset
{
    /// <summary>Whether this car can be reset at all: a living wheeled car (not a helicopter or an emplacement).</summary>
    public static bool Can(Vehicle v) => v.ObjectClass == 1 && !v.IsHeli && !v.IsEmplacement && (v.Flags & VehicleFlags.Dead) == 0;

    /// <summary>Puts the car on the terrain under it, upright and still.</summary>
    public static void Apply(SimWorld world, Vehicle v)
    {
        var ground = world.Ground;
        double x = v.Pos.X, z = v.Pos.Z;
        float h = ground.BareHeight(x, z);
        var up = ground.BareNormal(x, z);
        if (up.Y < 0.05f) up = Vector3.UnitY;   // a wall, or off the map: level
        up = Vector3.Normalize(up);

        // the heading: the nose's, or with the nose near vertical the way the roof leans
        var fwd = new Vector3(v.Rot.Forward.X, 0, v.Rot.Forward.Z);
        if (fwd.LengthSquared() < 0.04f)
        {
            var lean = new Vector3(v.Rot.Up.X, 0, v.Rot.Up.Z);
            if (lean.LengthSquared() > 1e-6f) fwd = v.Rot.Forward.Y > 0 ? -lean : lean;
            else fwd = new Vector3(v.Rot.Right.Z, 0, -v.Rot.Right.X);
            if (fwd.LengthSquared() < 1e-6f) fwd = Vector3.UnitZ;
        }
        fwd = Vector3.Normalize(fwd);
        // The forward row lifted into the terrain's plane, then right = up × forward
        var f = fwd;
        if (up.Y > 1e-4f) f.Y = -(up.Z * f.Z + up.X * f.X) / up.Y;
        var right = Vector3.Normalize(Vector3.Cross(up, f));
        v.Rot = new Rows(right, up, Vector3.Cross(right, up));
        v.Pos = new Double3(x, h, z);

        v.Vel = Vector3.Zero;
        v.AngVel = Vector3.Zero;
        v.Accel = Vector3.Zero;
        v.Speed = 0;
        v.Steer = 0;
        v.Throttle = 0;
        // standing on the ground: no flight, no landing in progress, no slide, no contact waiting for a response.
        // CommandedYaw (0x1000) is not a state but the car's grip model (the factory sets it on every car): cleared, a car falls back
        // to the per-wheel force model, which spins a long one (the Limo) in circles on a slope.
        v.Flags &= ~(VehicleFlags.Airborne | VehicleFlags.JustLanded | VehicleFlags.Sliding | VehicleFlags.Blending);
        v.Rec = default;
        v.GroundNormal = up;
        v.GroundHeight = h;
        v.HeightAboveGround = 0;
        foreach (var w in v.Wheels)
        {
            w.Travel = 0;
            w.PrevRate = 0;
            w.Ground = h;
            w.AtLimit = false;
        }
        // the car jumped: a network client's prediction and interpolation start again from the next snapshot
        v.NetGeneration++;
    }
}
