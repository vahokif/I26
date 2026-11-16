namespace I76.Game.Boot;

/// <summary>
/// The in-game bus layout (Main.ApplyAudio). Music, SFX and Voice meet on Mix, whose look-ahead limiter keeps the sum from
/// clipping at the output; the driver's own cues go on Cues, which joins them only after that limiter, so a loud fight never
/// pulls a warning down with it. Master keeps a limiter of its own as the last guard (a cue on top of a full mix, and whatever
/// plays straight on Master: the menus, the movies).
/// </summary>
public static class AudioMix
{
    public const string Master = "Master", Mix = "Mix", Music = "Music", Sfx = "SFX", Voice = "Voice", Cues = "Cues";

    /// <summary>The buses Main makes, in this order (a bus may only send to one made before it), with where each sends.</summary>
    public static readonly (string Name, string Send)[] Buses =
    [
        (Mix, Master), (Music, Mix), (Sfx, Mix), (Voice, Mix), (Cues, Master),
    ];

    /// <summary>
    /// Mix's limiter ceiling, Master's: a cue on top of a mix held there loses about 0.8 dB to Master's (measured in a fight; the
    /// cue lost 1-4 dB under the one limiter it shared with the fight before), and the game is as loud as before. A lower ceiling
    /// would leave Master less to do but take 1 dB off the whole game.
    /// </summary>
    public const float MixCeilingDb = -1f;

    /// <summary>Master's limiter ceiling, the last guard against clipping at the output.</summary>
    public const float MasterCeilingDb = -1f;

    /// <summary>
    /// The sounds that speak to the driver rather than come from the fight: the radar missile's lock warning (clockon.wav), the
    /// weapon selection's click (cammo.wav), the 4Get-U-Not's lock tones (msllock1-3.wav) and the radar's ping and growl
    /// (cradar.wav, cgrowl.wav). They play on the Cues bus, at the SFX level.
    /// </summary>
    public static bool IsDriverCue(string sound) => System.IO.Path.GetFileNameWithoutExtension(sound).ToLowerInvariant()
        is "clockon" or "cammo" or "msllock1" or "msllock2" or "msllock3" or "cradar" or "cgrowl";

    /// <summary>The bus a sound on the player's own car plays on.</summary>
    public static string BusFor(string sound) => IsDriverCue(sound) ? Cues : Sfx;
}
