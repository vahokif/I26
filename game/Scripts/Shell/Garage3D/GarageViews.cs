using Godot;

namespace I76.Game.Shell.Garage3D;

/// <summary>Where the camera goes for what the garage's menu is on: the whole car, a wheel, a hardpoint, one side of the body, the bonnet.</summary>
public static class GarageViews
{
    /// <summary>The longest side the bay holds a car at: a 16 m Huey is taller than the room and its camera runs into the walls.</summary>
    public const float MaxLength = 5.2f, MinLength = 2.6f;

    /// <summary>The scale a car of this longest side is shown at on the turntable: 1 for the cars the bay is made for, less for a helicopter or a jet, more for the Armadillo.</summary>
    public static float FitFor(float longest) => longest > MaxLength ? MaxLength / longest : longest is > 0.01f && longest < MinLength ? MinLength / longest : 1f;

    /// <summary>How far to stand to see the whole car at this look's field of view.</summary>
    public static float Reach(GarageCar c, bool mode1997) => Mathf.Clamp(c.Bounds.GetLongestAxisSize() * c.Fit * (mode1997 ? 1.35f : 1.9f), 4.5f, 11.5f);

    public static FocusSpec Overview(GarageCar c, bool mode1997, float closer = 1f) =>
        new(c.Bounds.GetCenter(), false, -135, 15, Reach(c, mode1997) * closer);

    /// <summary>The right-hand wheel of an axle, seen from the front of that side for the front axle, from the rear for the rear.</summary>
    public static FocusSpec Wheel(GarageCar c, I76.Sim.Trip.Axle axle, bool mode1997)
    {
        int slot = (int)axle * 2;
        if (!c.Wheels.TryGetValue(slot, out var at)) return Overview(c, mode1997);
        float yaw = axle == I76.Sim.Trip.Axle.Front ? 128 : axle == I76.Sim.Trip.Axle.Rear ? 52 : 90;
        return new(at + new Vector3(0, 0.05f, 0), true, yaw, 9, (mode1997 ? 2.6f : 3.4f) * c.Fit);
    }

    /// <summary>A hardpoint: the camera outside it, looking in along the line from the car's middle; a roof mount from above.</summary>
    public static FocusSpec Hardpoint(GarageCar c, int hardpoint, bool mode1997)
    {
        if (!c.Hardpoints.TryGetValue(hardpoint, out var h)) return Overview(c, mode1997);
        var mid = c.Bounds.GetCenter();
        var d = new Vector3(h.Pos.X - mid.X, 0, h.Pos.Z - mid.Z);
        float yaw = d.Length() < 0.45f ? -150 : Mathf.RadToDeg(MathF.Atan2(d.X, d.Z)) + (h.Pos.X >= mid.X ? -22 : 22);
        bool roof = h.Pos.Y > c.Bounds.Position.Y + c.Bounds.Size.Y * 0.72f;
        return new(h.Pos, true, yaw, roof ? 34 : 17, (mode1997 ? 2.7f : 3.6f) * c.Fit);
    }

    /// <summary>One side of the body (0 front, 1 right, 2 left, 3 rear): the camera square on to it.</summary>
    public static FocusSpec Side(GarageCar c, int zone, bool mode1997)
    {
        float[] yaw = [180 - 18, 90 + 10, -90 - 10, 18];
        return new(c.Bounds.GetCenter(), true, yaw[Math.Clamp(zone, 0, 3)], 11, Reach(c, mode1997) * 0.85f);
    }

    /// <summary>Under the bonnet: the front of the car from a little above and to its right.</summary>
    public static FocusSpec Engine(GarageCar c, bool mode1997)
    {
        var b = c.Bounds;
        return new(new Vector3(0, b.Position.Y + b.Size.Y * 0.55f, b.Position.Z + 0.9f), true, 158, 24, (mode1997 ? 3.6f : 4.8f) * c.Fit);
    }

    /// <summary>The underside: a low view of the front wheel and its spring.</summary>
    public static FocusSpec Underside(GarageCar c, bool mode1997)
    {
        if (!c.Wheels.TryGetValue(0, out var at)) return Overview(c, mode1997);
        return new(at + new Vector3(-0.5f, 0.1f, 0.2f), true, 112, 3, (mode1997 ? 3.2f : 4.2f) * c.Fit);
    }

    /// <summary>The roof and the rear deck, where the specials sit.</summary>
    public static FocusSpec Specials(GarageCar c, bool mode1997) =>
        new(c.Bounds.GetCenter() + new Vector3(0, c.Bounds.Size.Y * 0.2f, c.Bounds.Size.Z * 0.12f), true, 38, 30, Reach(c, mode1997) * 0.75f);
}
