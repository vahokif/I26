namespace I76.Game.Shell;

/// <summary>
/// The port's "All Scenes" (from the shell's Developer panel): one bookmark per mission of the trip, each at the start of its scene in that
/// mission's own stock car (vppt%02d.vcf, the car the trip gives the player there), so a fresh install can start any scene
/// without having played up to it. Pure (the caller supplies the names and the car texts), so the tests run it.
/// </summary>
public static class SceneBookmarks
{
    public const int Count = 17;

    /// <summary>The mission's stock car, as NEW TRIP and the post-mission router launch it.</summary>
    public static string CarFile(int scene) => $"vppt{scene:00}.vcf";

    /// <summary>The bookmark's name: the mission's title ("Mission N" when the install has none), without its number, which the list shows as "Scene N. ".</summary>
    public static string NameOf(I76.Formats.Vfs.GameInstall install, int scene)
    {
        var t = I76.Formats.Mission.MissionTitles.TripTitle(install, scene);
        var prefix = $"{scene}. ";
        return t.StartsWith(prefix, StringComparison.Ordinal) ? t[prefix.Length..] : t;
    }

    /// <summary>
    /// Adds a bookmark for every scene that has none of that name yet (a scene's bookmark of the same name, whoever made it, is
    /// left as it is), each under the next free stem with <paramref name="writeCar"/> given the stem and the car's text.
    /// Returns how many were made and how many were already there; the caller saves <paramref name="marks"/>.
    /// </summary>
    public static (int Made, int Had) Generate(Bookmarks marks, Func<int, string> name, Func<int, string> carJson, Action<string, string> writeCar)
    {
        int made = 0, had = 0;
        for (int scene = 1; scene <= Count; scene++)
        {
            var n = name(scene);
            if (marks.Entries.Any(e => e.Scene == scene && e.Name == n)) { had++; continue; }
            var stem = marks.NextStem();
            writeCar(stem, carJson(scene));
            marks.Entries.Add(new Bookmarks.Entry(scene, n, stem, 0, false));
            made++;
        }
        marks.Entries.Sort((x, y) => string.CompareOrdinal(x.Stem, y.Stem));
        return (made, had);
    }
}
