using System.Text.RegularExpressions;

namespace I76.Game.Shell;

/// <summary>
/// The port's checkpoint bookmarks (Play Options' Checkpoints; docs/deviations.md): the escape menu's Save Bookmark, offered
/// beside Retry from Checkpoint in a trip mission, adds a Load Bookmark entry that is the bookmark the player could have saved
/// before the mission (its scene, its car and parts as the mission was launched with them), named "Checkpoint" and always a new
/// row, with the mission's latest checkpoint kept beside it. The trip's own state (trip.cfg, its car) is not touched: the
/// bookmark carries its own. Pure managed (the files are written through the given writers).
/// </summary>
public static partial class CheckpointBookmarks
{
    /// <summary>The name the entry is listed under ("Scene 5. Checkpoint").</summary>
    public const string Name = "Checkpoint";

    [GeneratedRegex(@"^t(\d\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex TripStem();

    /// <summary>
    /// The trip mission number of a mission's stem (t01..t17), or null: training's a01 and b01, Nitro's missions, scenarios,
    /// melee arenas and anything else are not trip missions and have no Save Bookmark.
    /// </summary>
    public static int? TripMission(string? mission) =>
        mission is not null && TripStem().Match(mission) is { Success: true } m && int.Parse(m.Groups[1].Value) is var n and >= 1 and <= 17 ? n : null;

    /// <summary>
    /// The new entry, added to <paramref name="marks"/> under the next stem (the caller saves the list): the launch's scene, state 0
    /// (before the mission), its salvage setting, its car (<paramref name="writeCar"/>, the .cmp body) and the checkpoint
    /// (<paramref name="writeCheckpoint"/>).
    /// </summary>
    public static Bookmarks.Entry Add(Bookmarks marks, TripStart start, bool noSalvage, byte[] checkpoint,
        Action<string, string> writeCar, Action<string, byte[]> writeCheckpoint)
    {
        string stem = marks.NextStem();
        writeCar(stem, start.Car);
        writeCheckpoint(stem, checkpoint);
        var e = new Bookmarks.Entry(start.Mission, Name, stem, 0, noSalvage, Checkpoint: true);
        marks.Entries.Add(e);
        marks.Entries.Sort((x, y) => string.CompareOrdinal(x.Stem, y.Stem));
        return e;
    }
}
