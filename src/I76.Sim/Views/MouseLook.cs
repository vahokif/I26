namespace I76.Sim.Views;

/// <summary>
/// The port's mouse orbit for the chase view (Mouse Camera): relative mouse motion turned into the same yaw and pitch targets the
/// track keys move, so the camera's own clamps, easing and "stays where it was left" apply. Moving the mouse right swings the view
/// right (the camera round to the car's left); moving it up looks up (the camera drops). Invert Y flips the vertical.
/// </summary>
public static class MouseLook
{
    /// <summary>Off, then three speeds; the setting's levels.</summary>
    public static readonly string[] Names = ["Off", "Slow", "Normal", "Fast"];
    public const int Off = 0, Normal = 2;

    /// <summary>Radians of orbit per mouse count at a level (Normal is about 0.17 degrees a count).</summary>
    public static float Rate(int level) => level switch { 1 => 0.0015f, 2 => 0.003f, 3 => 0.006f, _ => 0f };

    /// <summary>The change in the chase camera's (yaw, pitch) targets for a motion of (dx, dy) counts, game signs (yaw+ swings the camera to the car's right, pitch+ raises it).</summary>
    public static (float Yaw, float Pitch) Orbit(float dx, float dy, int level, bool invertY)
    {
        float r = Rate(level);
        return (-dx * r, (invertY ? -dy : dy) * r);
    }
}
