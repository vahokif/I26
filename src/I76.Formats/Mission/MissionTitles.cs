using System.Text;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Formats.Mission;

/// <summary>One map of the editor's list: its stem (t05), the title the game shows for it and the list it belongs to.</summary>
public sealed record MapListing(string Stem, string Title, string Group)
{
    /// <summary>"Jorczak's Peak (M14)"; the code alone when there is no title.</summary>
    public string Label => Title.Length == 0 || Title.Equals(Stem, StringComparison.OrdinalIgnoreCase) ? Stem.ToUpperInvariant() : $"{Title} ({Stem.ToUpperInvariant()})";
}

/// <summary>
/// The titles of the install's stock missions, from the places the game takes them from:
/// the base melee lobby's own tables (15 arenas and 7 scenarios); the
/// Nitro lobby's callback +0x58, which reads WRLD + 0x143 of each file; Nitro's campaign list, addon\SCENARIO.DAT. The base
/// campaign (A01, T01-T17) has no titles in a data file: the trip numbers its missions ("T.R.I.P. Missions 1-17"), but the original executable holds
/// their 17 titles (the unused Save Points pad's), so they read "7. The Blood On The Highway"; "Mission 7" when the exe is not there.
/// </summary>
public static class MissionTitles
{
    public const string Campaign = "Campaign", Melee = "Melee arenas", Nitro = "Nitro";

    public static readonly (string Name, string Msn)[] BaseArenas =
    [
        ("The Crater", "m01"), ("Dunes", "m02"), ("Air Base", "m03"), ("Suburbia", "m04"), ("Slick Track", "m05"),
        ("Tombstone", "m06"), ("Hope Springs", "m07"), ("Creeper Canyon", "m08"), ("Mesa Maze", "m09"), ("Salt Flats", "m10"),
        ("Vigilantes' Paradise", "m11"), ("Night Driver", "m12"), ("Dodge", "m13"), ("Jorczak's Peak", "m14"), ("A Lot of Asphalt", "m15"),
    ];

    /// <summary>In list order, which is not file order.</summary>
    public static readonly (string Name, string Msn)[] BaseScenarios =
    [
        ("Oil Well Well", "s03"), ("Mondo Burger", "s02"), ("Seminole", "s04"), ("Pretty Bus", "s05"),
        ("Race No Shoot", "s06"), ("Trailer Pk. Madness", "s01"), ("Milk Toast", "s07"),
    ];

    /// <summary>The stock missions of the install, grouped (campaign, melee arenas or Nitro) and in the game's own order.</summary>
    public static List<MapListing> Stock(GameInstall install)
    {
        var dir = install.MissionDir;
        var stems = dir is null ? [] : Directory.EnumerateFiles(dir).Where(GameInstall.IsMissionFile)
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).Distinct().ToList();
        var o = new List<(string Group, int Rank, string Sort, MapListing L)>();
        ScenarioDat? campaign = null;
        if (install.IsNitro && install.Resolve("addon", "SCENARIO.DAT") is { } sc)
        {
            try { campaign = ScenarioDat.Parse(File.ReadAllText(sc, Encoding.Latin1)); }
            catch (IOException) { }
        }
        foreach (var stem in stems)
        {
            string group, title = ""; int rank;
            int num = stem.Length >= 3 && int.TryParse(stem.AsSpan(1), out int n) ? n : 0;
            if (install.IsNitro)
            {
                group = Nitro;
                var listed = campaign?.Characters.SelectMany(c => c.Missions).Select((m, i) => (m, i)).FirstOrDefault(x => x.m.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase));
                if (listed?.m is { } lm) { title = lm.Title; rank = listed.Value.i; }
                else { title = FileName(install, stem); rank = stem[0] switch { 'b' => 100, 'p' => 101, _ => 200 }; }
            }
            else if (stem[0] is 'a' or 't') { group = Campaign; rank = stem[0] == 'a' ? 0 : 1; title = stem[0] == 't' ? TripTitle(install, num) : "Training ground"; }
            else
            {
                group = Melee;
                int ai = Array.FindIndex(BaseArenas, a => a.Msn == stem), si = Array.FindIndex(BaseScenarios, a => a.Msn == stem);
                if (ai >= 0) { title = BaseArenas[ai].Name; rank = ai; }
                else if (si >= 0) { title = BaseScenarios[si].Name; rank = 100 + si; }
                else { title = FileName(install, stem); rank = 200; }
            }
            o.Add((group, rank, stem, new MapListing(stem, title.Trim(), group)));
        }
        return o.OrderBy(x => Array.IndexOf([Campaign, Melee, Nitro], x.Group)).ThenBy(x => x.Rank).ThenBy(x => x.Sort, StringComparer.Ordinal).Select(x => x.L).ToList();
    }

    /// <summary>
    /// The story missions a co-op game can play (docs/multiplayer.md "Co-op story missions"): the stock scripted ones, I76's A01, T01-T17 and
    /// S01-S07 or the Nitro Pack's campaign (P01-P19, B01), in <see cref="Stock"/>'s order (the game's own: the campaign first, then the
    /// scenarios in the shell's list order; Nitro's SCENARIO.DAT order, then B and P). No melee arena, no mission a player added.
    /// </summary>
    public static List<MapListing> Coop(GameInstall install)
    {
        var dir = install.MissionDir;
        if (dir is null) return [];
        var stock = Directory.EnumerateFiles(dir).Where(f => MapFileSet.IsStockMission(f) && f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), f => f);
        var o = new List<MapListing>();
        foreach (var m in Stock(install))
        {
            if (m.Stem[0] is not ('a' or 'b' or 'p' or 's' or 't') || !stock.TryGetValue(m.Stem, out var path)) continue;
            try { if (new MissionFile(path).ReadFsmProgram() is not { Code.Length: > 0 }) continue; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { continue; }
            o.Add(m);
        }
        return o;
    }

    /// <summary>The title a co-op mission goes by in the lists (the stem's upper case for one the install does not list).</summary>
    public static string CoopTitle(GameInstall install, string mission) =>
        Coop(install).FirstOrDefault(m => m.Stem.Equals(mission, StringComparison.OrdinalIgnoreCase))?.Title is { Length: > 0 } t ? t : mission.ToUpperInvariant();

    private static readonly Dictionary<string, string[]> TripTitles = new();

    /// <summary>"7. The Blood On The Highway": trip mission N's number and the title the original executable holds for it ("Mission N" without).</summary>
    public static string TripTitle(GameInstall install, int n)
    {
        var t = TripTitleList(install);
        return n >= 1 && n <= t.Length && t[n - 1].Length > 0 ? $"{n}. {t[n - 1]}" : $"Mission {n}";
    }

    /// <summary>
    /// The 17 titles, T01 first. The exe has them as 17 consecutive C strings (4-byte aligned) from "The Truth Is Out There" (T17) to
    /// "Meet Taurus" (T01), found by those two; none (empty) when the exe is missing, another build or not laid out so.
    /// </summary>
    internal static string[] TripTitleList(GameInstall install)
    {
        var path = install.FindFile("i76.exe");
        if (path is null) return [];
        lock (TripTitles)
        {
            if (TripTitles.TryGetValue(path, out var c)) return c;
            string[] r = [];
            try
            {
                var b = File.ReadAllBytes(path);
                int at = IndexOf(b, "The Truth Is Out There\0"u8);
                if (at >= 0)
                {
                    var l = new List<string>();
                    for (int p = at; l.Count < 17 && p < b.Length;)
                    {
                        if (b[p] == 0) { p++; continue; }
                        int e = Array.IndexOf(b, (byte)0, p);
                        if (e < 0) break;
                        l.Add(Encoding.Latin1.GetString(b, p, e - p));
                        p = e;
                    }
                    if (l.Count == 17 && l[16] == "Meet Taurus") { l.Reverse(); r = [.. l]; }
                }
            }
            catch (IOException) { }
            return TripTitles[path] = r;
        }
    }

    private static int IndexOf(byte[] b, ReadOnlySpan<byte> pat) => b.AsSpan().IndexOf(pat);

    /// <summary>The name in the mission file itself (WRLD + 0x143), as the Nitro lobby lists it; empty if none.</summary>
    private static string FileName(GameInstall install, string stem)
    {
        try { return install.FindMission(stem) is { } p ? WorldSettings.ArenaName(new MissionFile(p))?.Trim() ?? "" : ""; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return ""; }
    }
}
