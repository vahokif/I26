namespace I76.Formats.Input;

/// <summary>
/// The 3D / 2D dash flag and what F1 does to it (while already in the cockpit, <c>dash3d ^= 1</c>;
/// from any other view it just enters the cockpit in the mode it has). The mode is remembered across views and kept with the settings.
/// </summary>
public static class DashToggle
{
    /// <summary>F1 pressed: the new dash flag. Only a press made while already in the cockpit flips it.</summary>
    public static bool OnF1(bool inCockpit, bool dash3d) => inCockpit ? !dash3d : dash3d;

    /// <summary>The pad's free look turns the head in proportion, which a flat dash cannot follow: it works with the 3D dash only (the glance poses stay).</summary>
    public static bool FreeLookAllowed(bool dash3d) => dash3d;
}
