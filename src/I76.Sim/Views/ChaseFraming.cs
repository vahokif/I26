namespace I76.Sim.Views;

/// <summary>
/// The framing the F2 chase view opens at and that End puts it back to. The original starts it at 30 degrees and zoom 3
/// (its <c>track_pitch</c> and <c>track_distance</c> on a01); the port opens it lower and nearer — 16 degrees and zoom 1.7,
/// the framing a modern chase camera usually has — which is a deliberate difference (docs/deviations.md) that Play Options'
/// <b>Original Chase Framing</b> row switches back (<c>[port] chase_framing_original</c> in shell.cfg). <c>I76_CHASE_PITCH</c>
/// and <c>I76_CHASE_ZOOM</c> win over both, since they are there to frame a capture. View only: nothing the simulation
/// decides follows it.
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

    /// <summary>The pitch the view opens at and End returns to, at the current choice.</summary>
    public static float Pitch => Original ? OriginalPitch : PortPitch;

    /// <summary>The distance the view opens at and End returns to, at the current choice.</summary>
    public static float Zoom => Original ? OriginalZoom : PortZoom;
}
