namespace I76.Formats.Shell;

/// <summary>
/// Which of the two games' play settings a run uses. The originals kept them in two files (I76PLYR.DEF and nitplyr.def);
/// I26 keeps one shell.cfg: the engine-level settings (graphics, audio, the port's own switches) are shared, the Play Options
/// flags and the difficulty are per game ([shell] play / difficulty for Interstate '76, [nitro] for the Nitro Pack).
/// </summary>
public static class PlayProfile
{
    /// <summary>
    /// Nitro's Play Options has only Arcade Phys. (0x01), Unlim. Wiper Fluid (0x20) and Difficulty: the
    /// salvage-management and the three cheat bits (0x1e) are the base game's.
    /// </summary>
    public const int NitroMask = 0x21;

    /// <summary>The flags a game keeps: Nitro's lose the base game's cheat and salvage bits.</summary>
    public static int Mask(bool nitro, int flags) => nitro ? flags & NitroMask : flags;

    /// <summary>
    /// The flags and difficulty a game starts with. Its own saved values win. A game with none yet (a shell.cfg from before the
    /// split) takes the shared ones that were there: the same settings as before for both games, Nitro's without the bits it
    /// does not have, so a cheat ticked in the base game cannot mark Nitro runs as cheated.
    /// </summary>
    public static (int Play, int Difficulty) Resolve(bool nitro, (int Play, int Difficulty)? own, (int Play, int Difficulty)? baseGame)
    {
        if (own is { } o) return (Mask(nitro, o.Play), o.Difficulty);
        var b = baseGame ?? (0, 0);
        return (Mask(nitro, b.Play), b.Difficulty);
    }
}
