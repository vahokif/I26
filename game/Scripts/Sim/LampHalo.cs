namespace I76.Game.Sim;

/// <summary>
/// [port] lamp_halo: the modern look's lamp lens glows and glares (the halo round a lit head, brake or tail lamp) as a
/// percentage of their strengths in CarNode, which stay the 100 mark; 65 by default, a third under. The lights themselves (the
/// beams on the road, the brake's red light) keep their energy. I76_LAMP_HALO overrides the file for a run.
/// </summary>
public static class LampHalo
{
    public const int Default = 65;
    public static int Percent = Default;
    public static readonly int? Hook = int.TryParse(System.Environment.GetEnvironmentVariable("I76_LAMP_HALO"), out int h) ? h : null;

    /// <summary>The share in force: the hook's, else the file's, 0 to 2.</summary>
    public static float Factor => Math.Clamp(Hook ?? Percent, 0, 200) / 100f;

    /// <summary>The file's word (0 to 200), the default when absent or not a number.</summary>
    public static int Parse(string? s) => int.TryParse(s, out int p) ? Math.Clamp(p, 0, 200) : Default;
}
