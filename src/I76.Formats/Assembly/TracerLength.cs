namespace I76.Formats.Assembly;

/// <summary>
/// How long a round is drawn behind its head (the port's: the original draws the round's own sprite). Neither look draws it
/// further back than it has come from the muzzle by the frame drawn, so a round just fired does not reach back through the
/// gun, or out of the far side of a turret turned to the side.
/// </summary>
public static class TracerLength
{
    /// <summary>How far the round has flown at the drawn point: its distance so far, less the part of the last step still ahead.</summary>
    public static float Flown(float travelled, float step, float alpha) => System.MathF.Max(0.05f, travelled - step * (1 - alpha));

    /// <summary>The 1997 look's bar: the last step's length, 0.5 to 4 m, but no longer than the round has flown.</summary>
    public static float Bar(float step, float flown) => System.MathF.Min(System.MathF.Min(System.MathF.Max(step, 0.5f), 4f), flown);

    /// <summary>The modern look's streak: its look's length, no longer than the round has flown.</summary>
    public static float Streak(float length, float flown) => System.MathF.Min(length, flown);
}
