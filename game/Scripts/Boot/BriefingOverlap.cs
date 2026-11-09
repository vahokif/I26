namespace I76.Game.Boot;

/// <summary>
/// Whether a mission start plays its briefing while the world builds ([port] briefing_while_loading, Play Options' Load During
/// Briefing; docs/deviations.md). The original loads the whole mission behind its loading card, then plays the WRLD briefing,
/// then starts; here the build runs on a thread under the movie, so the seconds of the build are spent in a briefing of 17 to
/// 158 s. Both looks; a single-player start only.
/// </summary>
public static class BriefingOverlap
{
    public static bool Plan(bool setting, string? briefing, bool movieFound, bool skipMovies, bool network, bool editor, bool retry)
        => setting && briefing is { Length: > 0 } && movieFound && !skipMovies && !network && !editor && !retry;
}
