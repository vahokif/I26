namespace I76.Sim.Views;

/// <summary>
/// The port's Chase HUD rows (Play Options): how big the 2D instrument panels over an external view are drawn. The original
/// draws them at full size in both halves of its 640x480 instrument frame — the top row the radar, the weapon housing and the
/// damage panel, the bottom row the compass, the speedometer, the tacho, the gear strip and the specials — and here either row
/// can be drawn at a quarter, a half or full size. A row shrinks as a row: each panel keeps its place in it, so the panels come
/// closer to one another as they get smaller instead of spreading out to the corners. The cockpit's own dash is never scaled (its panels are the car's
/// interior), and neither is the CB handset. The rows are the modern look's choice: the 1997 look draws the original's frame at the original's own
/// sizes whatever is set (<see cref="ForLook"/>), as it has no cloud shadows and no haze. View only: nothing the simulation decides follows it.
/// </summary>
public static class HudScale
{
    /// <summary>The offered sizes, as a fraction of the original's; the index is a choice (see <see cref="Names"/>).</summary>
    public static readonly float[] Fractions = [0.25f, 0.5f, 1f];
    public static readonly string[] Names = ["1/4", "1/2", "1/1"];

    /// <summary>Play Options' defaults: the top row at a half of the original, the bottom row as the original drew it.</summary>
    public const int TopDefault = 1, BottomDefault = 2;

    /// <summary>The instrument frame the panels are laid out in, as the original laid it out.</summary>
    public const float FrameWidth = 640, FrameHeight = 480;

    public static int Clamp(int choice) => System.Math.Clamp(choice, 0, Fractions.Length - 1);

    /// <summary>The size a choice draws at.</summary>
    public static float Of(int choice) => Fractions[Clamp(choice)];

    /// <summary>A choice's size as the whole number shell.cfg keeps: the denominator (4, 2 or 1).</summary>
    public static int Denominator(int choice) => (int)System.Math.Round(1 / Of(choice));

    /// <summary>
    /// The sizes the two rows are drawn at in a look. The 1997 look draws the instrument frame as the original laid it out — both rows at their own
    /// size whatever Play Options say, as it has neither cloud shadows nor haze — and the modern look draws them at the chosen sizes. The choices
    /// are only read, never changed: they keep what the player set and the modern look finds them again on the next F12.
    /// </summary>
    public static (float Top, float Bottom) ForLook(bool mode1997, int top, int bottom) =>
        mode1997 ? (1f, 1f) : (Of(top), Of(bottom));

    /// <summary>The choice for a shell.cfg [port] value (the denominator); anything not offered is <paramref name="fallback"/>.</summary>
    public static int Parse(int denominator, int fallback)
    {
        for (int i = 0; i < Fractions.Length; i++)
            if (Denominator(i) == denominator) return i;
        return Clamp(fallback);
    }

    /// <summary>
    /// A slot of the 640x480 frame shrunk to <paramref name="s"/> about the frame's middle line and its row's own edge: the top row
    /// hangs from the top of the frame and the bottom row stands on the bottom, so a smaller row eats screen towards the middle of
    /// the view and not at its edges. Scaling a row about one line keeps the panels in their order and their gaps shrink with them,
    /// which is what makes a small row read as one small instrument panel and not as five loose ones. 1 leaves the slot where the
    /// original drew it.
    /// </summary>
    public static (float X, float Y, float W, float H) Shrink(float x, float y, float w, float h, float s)
    {
        if (s == 1f) return (x, y, w, h);
        float ay = y + h / 2 < FrameHeight / 2 ? 0 : FrameHeight;
        const float ax = FrameWidth / 2;
        return (ax + (x - ax) * s, ay + (y - ay) * s, w * s, h * s);
    }
}
