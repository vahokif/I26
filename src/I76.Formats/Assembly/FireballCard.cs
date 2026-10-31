namespace I76.Formats.Assembly;

/// <summary>
/// The Ball O'Flame's fireball (Nitro, ordnance kind 0x15). Its round is the MBALFIRE quad, flagged a camera-facing card at
/// spawn (the Nitro Pack flags it as a card), and the engine gives it the first row of the
/// XFS1_101 texture table, XFS1_101..106, as a loop of 1/6 s a frame; the animation is let go when the round ends.
/// </summary>
public static class FireballCard
{
    public const int Frames = 6;
    public const float FramesPerSecond = 6f;

    /// <summary>The texture's stem for a round this many seconds old.</summary>
    public static string Frame(float age) => $"XFS1_10{(int)(System.MathF.Max(0, age) * FramesPerSecond) % Frames + 1}";
}
