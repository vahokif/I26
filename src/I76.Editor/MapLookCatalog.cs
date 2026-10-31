using System.Text.RegularExpressions;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Editor;

/// <summary>One thing a map's look can be set to: the name its WRLD field holds, a line to show, the stock maps that use it.</summary>
/// <param name="Panels">A horizon's 16 panel textures, in order (what the .hzd lists); null for the rest.</param>
/// <param name="Same">Other names with the same content (the 8-bit twins of a horizon list, say).</param>
/// <param name="Palettes">An 8-bit one's palettes: those the stock maps use with it, the most used first (empty if no stock map uses it). Null for a 16-bit one, which carries its own colours.</param>
public sealed record LookChoice(string Name, string Label, IReadOnlyList<string> UsedBy, string[]? Panels = null, IReadOnlyList<string>? Same = null,
    IReadOnlyList<string>? Palettes = null)
{
    public string Tip => $"{Label}\n{(UsedBy.Count == 0 ? "not used by a stock map" : "used by " + string.Join(", ", UsedBy.Take(8)) + (UsedBy.Count > 8 ? $" and {UsedBy.Count - 8} more" : ""))}";
}

/// <summary>
/// Every ground, sky and horizon the install's archive holds that a map's WRLD can name (docs/map-editor.md "Map style"):
/// the ground families (<c>tp041db6</c>: the largest member of the family, level 1, is what the field names; the 16-bit TP/TN/TM
/// and the 8-bit TT sets), the cloud skies (<c>nk_6cld3</c> 16-bit, <c>nk_1cld2</c> 8-bit), the horizon lists (.hzd, the ones with
/// the same 16 panels counted once), and the palettes (.act), which are not offered but follow (<see cref="Suited"/>). Each carries
/// the stock maps that use it. On Nitro the archive is Nitro's, which holds the same sets plus its own: nothing is hard-coded here
/// but the names' shape.
/// </summary>
public sealed class MapLookCatalog
{
    public IReadOnlyList<LookChoice> Grounds { get; init; } = [];
    public IReadOnlyList<LookChoice> Skies { get; init; } = [];
    public IReadOnlyList<LookChoice> Horizons { get; init; } = [];
    public IReadOnlyList<LookChoice> Palettes { get; init; } = [];

    // tp041db6, tt031sd_, tn101md_, tm151as_: t, a letter, the family's number (2), the level digit (1 for the largest), 3 more
    private static readonly Regex GroundName = new(@"^t[a-z][0-9]{2}1[a-z0-9_]{3}\.(m16|map)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SkyName = new(@"^nk_[0-9]cld[0-9a-e]\.(m16|map)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // the cockpit's and the shell's palettes are not a world's
    private static readonly HashSet<string> NotWorld = new(StringComparer.OrdinalIgnoreCase) { "superpal.act", "vpit.act" };

    private static string Kind(string stem) => stem.Length >= 7 ? stem.Substring(5, 2).ToLowerInvariant() switch
    {
        "db" => "brown desert", "dr" => "red desert", "sd" => "sand", "gr" => "grass", "sw" => "snow", "sr" => "rough snow", "as" => "asphalt", "md" => "dirt", _ => "ground",
    } : "ground";

    public static MapLookCatalog Scan(AssetVfs vfs)
    {
        var used = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var looks = new List<WorldSettings>();
        void Use(string name, string map) { if (name.Length == 0) return; if (!used.TryGetValue(name, out var l)) used[name] = l = []; if (!l.Contains(map)) l.Add(map); }
        foreach (var ext in GameInstall.MissionExtensions)
            foreach (var path in vfs.Install.ListMissions(ext, addon: false).Where(MapFileSet.IsStockMission))
                try
                {
                    if (WorldSettings.Read(new MissionFile(path)) is not { } w) continue;
                    string map = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
                    Use(w.Palette, map); Use(w.Sky, map); Use(w.Ground, map); Use(w.Horizon, map);
                    looks.Add(w);
                }
                catch (Exception e) when (e is IOException or InvalidDataException) { }
        IReadOnlyList<string> UsedBy(params string[] names) => [.. names.SelectMany(n => used.GetValueOrDefault(n) ?? []).Distinct()];
        static string Stem(string n) => Path.GetFileNameWithoutExtension(n).ToLowerInvariant();
        // the palettes the stock maps use with any of these names in a field, the most used first
        IReadOnlyList<string> PalettesWith(Func<WorldSettings, string> field, params string[] names) =>
        [
            .. looks.Where(w => names.Contains(field(w), StringComparer.OrdinalIgnoreCase) && vfs.Exists(w.Palette))
                .GroupBy(w => w.Palette.ToLowerInvariant()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key),
        ];
        bool Wide(string stem) => vfs.Exists(Stem(stem) + ".m16");

        var grounds = vfs.Names.Where(n => GroundName.IsMatch(n)).Select(Stem).Distinct().OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => new LookChoice(s + ".map", $"{s}  {Kind(s)}, {(Wide(s) ? "16-bit" : "8-bit")}", UsedBy(s + ".map"),
                Palettes: Wide(s) ? null : PalettesWith(w => w.Ground, s + ".map"))).ToList();
        var skies = vfs.Names.Where(n => SkyName.IsMatch(n)).Select(Stem).Distinct().OrderBy(s => s[3] == '6' ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal)
            .Select(s => new LookChoice(s + ".map", $"{s}  {(Wide(s) ? "16-bit" : "8-bit")} clouds", UsedBy(s + ".map"),
                Palettes: Wide(s) ? null : PalettesWith(w => w.Sky, s + ".map"))).ToList();
        var palettes = vfs.Names.Where(n => n.EndsWith(".act", StringComparison.OrdinalIgnoreCase) && !NotWorld.Contains(n)).Select(n => n.ToLowerInvariant()).Distinct()
            .OrderBy(n => n, StringComparer.Ordinal).Select(n => new LookChoice(n, n, UsedBy(n))).ToList();

        // horizon lists: one entry for each distinct set of 16 panels, named by its first list (a _16 one when there is one)
        var horizons = new List<LookChoice>();
        foreach (var g in vfs.Names.Where(n => n.EndsWith(".hzd", StringComparison.OrdinalIgnoreCase))
                     .Select(n => (Name: n.ToLowerInvariant(), Panels: HorizonList.Parse(vfs.TryRead(n))))
                     .Where(x => x.Panels is not null).GroupBy(x => string.Join(' ', x.Panels!.Select(p => p.ToLowerInvariant()))))
        {
            var names = g.Select(x => x.Name).OrderBy(n => n.Contains("_16") ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToList();
            var panels = g.First().Panels!;
            horizons.Add(new LookChoice(names[0], $"{Stem(names[0])}  panels {Stem(panels[0])[3..5]}", UsedBy([.. names]), panels, names,
                panels.All(Wide) ? null : PalettesWith(w => w.Horizon, [.. names])));
        }
        horizons.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new MapLookCatalog { Grounds = grounds, Skies = skies, Horizons = horizons, Palettes = palettes };
    }

    /// <summary>
    /// <paramref name="look"/> with a palette that suits it. The palette colours only the 8-bit textures (the 16-bit ones carry their
    /// own colours, and every prop, building and car has 16-bit ones), and the palettes differ in the 64 colours the 8-bit grounds,
    /// skies and horizons are drawn with, so one of those is only right through a palette made for it. The first 8-bit one of the
    /// ground, sky and horizon decides: the palette stays if a stock map uses it with that one, else it becomes the one they use most.
    /// All 16-bit (or an 8-bit one no stock map uses): the palette as it is.
    /// </summary>
    public MapLook Suited(MapLook look)
    {
        var driver = new[] { Find(Grounds, look.Ground), Find(Skies, look.Sky), look.Horizon.Length > 0 ? Find(Horizons, look.Horizon) : null }
            .FirstOrDefault(c => c?.Palettes is { Count: > 0 });
        return driver is null || driver.Palettes!.Contains(look.Palette, StringComparer.OrdinalIgnoreCase) ? look : look with { Palette = driver.Palettes![0] };
    }

    /// <summary>The choice that holds <paramref name="name"/> (any case; a horizon by any of its twins), or null.</summary>
    public static LookChoice? Find(IReadOnlyList<LookChoice> from, string name) =>
        from.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Same?.Contains(name, StringComparer.OrdinalIgnoreCase) == true);
}
