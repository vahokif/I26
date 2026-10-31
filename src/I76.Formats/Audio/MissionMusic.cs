namespace I76.Formats.Audio;

/// <summary>
/// Which CD track plays when, in a mission. The 1997 retail game's play call sends MCI PLAY from the
/// track's start to the start of track max(N+1, 15): a mission track below 15 plays on through track 14 without a break, a
/// track of 15 or more (the shell's) plays alone. A poller (every frame) looks at the
/// transport every 60 s in a mission (5 s in the shell) and, once it has stopped, starts disc track 2 (and so 2..14 again),
/// at the next 60 s boundary. The 1998 build GOG ships had that range cut back to one track (TO = N+1).
/// The Nitro Pack has the 1997 rule; its disc has fewer tracks, so the range runs to the disc's end.
/// Track numbers here are the disc's, not music/N.mp3's (CdMusic maps them).
/// </summary>
public sealed class MissionMusic
{
    /// <summary>Seconds between the poller's looks in a mission (GetTickCount + 60000).</summary>
    public const double PollSeconds = 60;

    /// <summary>The track the poller starts when the music has ended (it passes 2 in a mission).</summary>
    public const int FallbackTrack = 2;

    /// <summary>The track the 1997 play call's TO is at least (0xf).</summary>
    public const int RangeEnd = 15;

    public enum Policy
    {
        /// <summary>The 1997 retail game's (and Nitro's): from the mission's track on to track 14, then 2..14 on and on, each restart on a 60 s poll.</summary>
        Retail1997,
        /// <summary>The 1998 build GOG ships: the mission's track once, then track 2 on and on, each start on a 60 s poll.</summary>
        Gog,
        /// <summary>The default: a CD deck's, on repeat: on to the next track, back to track 2 after the last, at once.</summary>
        Disc,
    }

    private readonly int _last;
    private readonly int _rangeEnd;
    public Policy Mode { get; }

    /// <param name="lastAudioTrack">The disc's last audio track (17 for I76's disc 2, 11 for Nitro's).</param>
    /// <param name="rangeEnd">The track whose start ends a play (15 on I76's disc; Nitro's has no track 15: the disc's end, last + 1).</param>
    public MissionMusic(int lastAudioTrack, Policy mode = Policy.Disc, int rangeEnd = RangeEnd)
    {
        _last = Math.Max(lastAudioTrack, FallbackTrack);
        _rangeEnd = Math.Min(rangeEnd, _last + 1);
        Mode = mode;
    }

    /// <summary>
    /// The track the mission starts with: WRLD's, verbatim. Disc track 1 is the data track; asked to play from it the CD version
    /// starts at the first audio track (S01 measured in 86box). A track past the disc's end is the same as asking for none: track 2.
    /// </summary>
    public int First(int wrldTrack) => wrldTrack < FallbackTrack || wrldTrack > _last ? FallbackTrack : wrldTrack;

    /// <summary>Whether the track after <paramref name="ended"/> follows at once (the same MCI play, or a deck).</summary>
    public bool Continues(int ended) => Mode switch
    {
        Policy.Disc => ended >= FallbackTrack && ended < _last,
        Policy.Retail1997 => ended >= FallbackTrack && ended + 1 < _rangeEnd && ended + 1 <= _last,
        _ => false,
    };

    /// <summary>The track that follows <paramref name="ended"/>.</summary>
    public int Next(int ended) => Continues(ended) ? ended + 1 : FallbackTrack;

    /// <summary>
    /// Seconds from <paramref name="ended"/>'s end to the next track's start. <paramref name="sinceStart"/> is how long the music has been going
    /// (the poller's clock runs from the mission's first play and does not restart): when the play does not go on by itself the
    /// next 60 s boundary is waited for (none if the end falls on one).
    /// </summary>
    public double Wait(int ended, double sinceStart)
    {
        if (Mode == Policy.Disc || Continues(ended)) return 0;
        double into = sinceStart - Math.Floor(sinceStart / PollSeconds) * PollSeconds;
        return into < 1e-9 ? 0 : PollSeconds - into;
    }

    /// <summary>The policy named by I76_MUSIC_SEQUENCE ("retail", "gog" for the originals' poller; else a disc's: on at once).</summary>
    public static Policy PolicyFromEnvironment(string? value) =>
        string.Equals(value, "retail", StringComparison.OrdinalIgnoreCase) ? Policy.Retail1997 :
        string.Equals(value, "gog", StringComparison.OrdinalIgnoreCase) ? Policy.Gog : Policy.Disc;
}
