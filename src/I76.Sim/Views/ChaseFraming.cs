namespace I76.Sim.Views;

/// <summary>
/// The framing the F2 chase view opens at and that End puts it back to. The original starts it at 30 degrees and zoom 3
/// (its <c>track_pitch</c> and <c>track_distance</c> on a01); the port opens it lower and nearer — 16 degrees and zoom 1.7,
/// the framing a modern chase camera usually has — which is a deliberate difference (docs/deviations.md) that Play Options'
/// <b>Original Chase Framing</b> row switches back (<c>[port] chase_framing_original</c> in shell.cfg). The 1997 look opens
/// at the original's numbers whatever the row says (<see cref="Mode1997"/>), as its whole picture is the original's; the row
/// keeps its value for the modern look. <c>I76_CHASE_PITCH</c> and <c>I76_CHASE_ZOOM</c> win over all of it, since they are
/// there to frame a capture. View only: nothing the simulation decides follows it.
/// </summary>
public static class ChaseFraming
{
    /// <summary>The original's starting pitch: 30 degrees.</summary>
    public const float OriginalPitch = 0.5235988f;
    /// <summary>The original's starting distance: its <c>track_distance</c> on a01 (the setting is clamped 1..10).</summary>
    public const float OriginalZoom = 3f;
    /// <summary>The port's starting pitch: what the docs call 16 degrees (16.3 to the tenth).</summary>
    public const float PortPitch = 0.2839724f;
    /// <summary>The port's starting distance: a little over half the original's.</summary>
    public const float PortZoom = 1.7f;

    /// <summary>Play Options' Original Chase Framing: open and reset at the original's numbers. Off unless the player chose it.</summary>
    public static bool Original { get; set; }

    /// <summary>
    /// The look in play. The 1997 look opens the chase view at the original's numbers whatever the row says, since its frame is
    /// the original's own; the row is only read, never written, so the modern look finds the choice again on the next F12. The
    /// camera takes the framing as it is built, so the boot that settled the look sets this and every switch repeats it.
    /// </summary>
    public static bool Mode1997 { get; set; }

    /// <summary>Whether the view opens at the original's numbers now: because the row was chosen, or because the 1997 look always does.</summary>
    public static bool UsesOriginal => Original || Mode1997;

    /// <summary>The pitch the view opens at and End returns to, at the current look and choice.</summary>
    public static float Pitch => UsesOriginal ? OriginalPitch : PortPitch;

    /// <summary>The distance the view opens at and End returns to, at the current look and choice.</summary>
    public static float Zoom => UsesOriginal ? OriginalZoom : PortZoom;
}
