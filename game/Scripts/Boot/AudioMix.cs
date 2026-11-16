namespace I76.Game.Boot;

/// <summary>
/// The in-game bus layout (Main.ApplyAudio). Music, SFX, Voice and the driver's cues all send to Master, whose look-ahead
/// limiter keeps the sum from clipping at the output. The cues are on a bus of their own at the SFX level, but ahead of the
/// limiter with the rest: the original's mix clipped, which left a cue's level against the fight where the mix put it, and a
/// limiter that pulls the fight and the cue down together keeps that ratio (a cue mixed after it stood 2-4 dB proud of the
/// original's, measured).
/// </summary>
public static class AudioMix
{
    public const string Master = "Master", Music = "Music", Sfx = "SFX", Voice = "Voice", Cues = "Cues";

    /// <summary>The buses Main makes, in this order (a bus may only send to one made before it), with where each sends.</summary>
    public static readonly (string Name, string Send)[] Buses =
    [
        (Music, Master), (Sfx, Master), (Voice, Master), (Cues, Master),
    ];

    /// <summary>Master's limiter ceiling.</summary>
    public const float CeilingDb = -1f;

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
