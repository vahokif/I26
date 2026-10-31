namespace I76.Sim.Views;

/// <summary>
/// The port's frame rate limiter (Graphic Detail's Max FPS): a cap on how often the window draws, nothing more. The simulation
/// steps on its own clock (Play Options' Simulation Rate) and the cars are drawn interpolated between its ticks, so a cap below
/// the tick rate leaves the sim running at its rate with several ticks to a frame (docs/frame-rate.md, "The frame rate limiter").
/// </summary>
public static class FrameLimit
{
    /// <summary>The caps offered; 0 is Unlimited.</summary>
    public static readonly int[] Caps = [30, 60, 75, 120, 144, 165, 240, 0];
    public static readonly string[] Names = ["30", "60", "75", "120", "144", "165", "240", "Unlimited"];
    public const int Unlimited = 7;

    /// <summary>The cap, in frames a second, a choice stands for (0: none).</summary>
    public static int Fps(int choice) => Caps[System.Math.Clamp(choice, 0, Caps.Length - 1)];

    /// <summary>
    /// The choice for a shell.cfg [port] max_fps value (frames a second, 0 for none). A missing key or a value that is not one of the
    /// caps (a hand edit) keeps today's behaviour, no cap.
    /// </summary>
    public static int Parse(int? fps) => fps is { } f && System.Array.IndexOf(Caps, f) is var i and >= 0 ? i : Unlimited;
}
