namespace I76.Game.Boot;

/// <summary>
/// The movies before the shell at start-up: the original plays introf01 then credf01 at every start. Play Options' Skip Intro Movies
/// (the port's own, off) leaves them out, and only them: briefings, outros and mission cut-scenes still play.
/// </summary>
public static class IntroMovies
{
    public static readonly string[] Original = ["introf01.smk", "credf01.smk"];

    /// <summary>
    /// The start-up's movies: the one --movie names; none once the menu has been shown this run, under --skip-movies (which skips
    /// every movie), or with Skip Intro Movies on; else the original's two.
    /// </summary>
    public static string[] Startup(string? only, bool menuShown, bool skipMovies, bool skipIntro) =>
        only is not null ? [only] : menuShown || skipMovies || skipIntro ? [] : Original;
}
