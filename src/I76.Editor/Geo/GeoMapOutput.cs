using System.Globalization;
using System.Text;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Geo;

/// <summary>Writing a finished geographic map: the game files and the README that names the area, the sources and the licences.</summary>
public static class GeoMapOutput
{
    /// <summary>The default stem for a name: letters, digits and underscores of it, lower case, the first 8.</summary>
    public static string StemOf(string name)
    {
        var s = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();
        return s.Length > MapProject.MaxStem ? s[..MapProject.MaxStem] : s;
    }

    /// <summary>The stock-style look of a grassland map: Nitro's P17 sky, grass, horizon and palette (both games' archives hold them), mid-morning light.</summary>
    public static MapLook GrasslandLook(int hour = 9) => new("p17_16.act", "nk_6cld7.map", "tp011gr6.map", "p17_16.hzd", hour);

    /// <summary>The look with every name checked against the install's catalogue (a missing one falls back to the first the catalogue has of its kind); problems are returned in <paramref name="notes"/>.</summary>
    public static MapLook Checked(MapLook look, MapLookCatalog catalog, List<string> notes)
    {
        string Pick(IReadOnlyList<LookChoice> from, string name, string what)
        {
            if (MapLookCatalog.Find(from, name) is { } c) return c.Name;
            notes.Add($"{what} '{name}' is not in this install's archive");
            return from.Count > 0 ? from[0].Name : name;
        }
        var l = look with
        {
            Sky = Pick(catalog.Skies, look.Sky, "sky"), Ground = Pick(catalog.Grounds, look.Ground, "ground"),
            Horizon = Pick(catalog.Horizons, look.Horizon, "horizon"), Palette = Pick(catalog.Palettes, look.Palette, "palette"),
        };
        return catalog.Suited(l);
    }

    public static IReadOnlyList<string> Save(MapProject project, GeoMapOptions opt, string dir, bool i76, bool nitro, out string? lvl, out string? cbt)
    {
        var files = new List<string>();
        lvl = cbt = null;
        if (i76) { var p = project.Extension == ".lvl" ? project : project.CloneAs(project.Name, null, ".lvl"); files.AddRange(p.SaveTo(dir)); lvl = p.MissionFileName; }
        if (nitro) { var p = project.Extension == ".cbt" ? project : project.CloneAs(project.Name, null, ".cbt"); files.AddRange(p.SaveTo(dir)); cbt = p.MissionFileName; }
        return files.Distinct().ToList();
    }

    public static string Readme(GeoMapOptions opt, GeoMapReport r, string? lvl, string? cbt, bool allRoads, IEnumerable<string> notes)
    {
        var inv = CultureInfo.InvariantCulture;
        var g = r.Grid;
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        string F(FormattableString f) => f.ToString(inv);
        L(F($"{opt.DisplayName}  ({opt.Name})"));
        L(new string('=', 40));
        L();
        L("A playable Interstate '76 / Nitro Pack map made from real terrain and roads by I26 (the map editor's New map from real world).");
        L();
        L("Area (WGS84)");
        L(F($"  corner A  {opt.LatA:0.000000}, {opt.LonA:0.000000}"));
        L(F($"  corner B  {opt.LatB:0.000000}, {opt.LonB:0.000000}"));
        L(F($"  {g.BoxWidth / 1000:0.0} km east-west by {g.BoxHeight / 1000:0.0} km north-south; the map is {g.Cols} x {g.Rows} tiles = {g.Tiles} tiles of 640 m ({g.Width / 1000:0.00} x {g.Height / 1000:0.00} km), centred on the box."));
        if (g.CropNote is { } c) L("  Note: " + c + ".");
        L("  North is +Z (up the screen in the map editor), east is +X, one world unit is one metre.");
        L();
        L("Terrain");
        L(F($"  Elevation in the area: {r.Heights.MinMetres:0} to {r.Heights.MaxMetres:0} m above sea level."));
        L(F($"  Stored as tenths of a metre above {r.Heights.OffsetMetres:0.0} m (the lowest ground less {PackedHeights.LowMargin:0} m); the highest sample is {r.Heights.Raw.Max() / 10.0:0.0} m of the format's 409.5 m."));
        L(r.Heights.Compressed ? F($"  The relief did not fit, so every height is multiplied by {r.Heights.Scale:0.000} (vertically compressed).") : "  No vertical compression.");
        L("  Resampled bicubically from AWS terrain tiles (zoom 14, about 6.7 m a pixel) onto the game's 5 m grid in a local equirectangular projection.");
        L();
        L("Roads");
        L(F($"  {r.Roads} roads, {r.RoadPoints} points, {r.RoadMetres / 1000:0.0} km, from OpenStreetMap highway ways ({(allRoads ? "motorway to residential, living streets and service roads" : "motorway, trunk, primary, secondary and tertiary and their links, unclassified and track")})."));
        L("  Motorway to tertiary (and links, and paved others) are ASPHALT; unclassified, track and ways tagged unpaved are DIRT.");
        L("  The ground is cut and filled along every road (10 % grade limit, 8 m flat, 14 m shoulder) as the editor's road tool does; a bridge keeps a straight grade between its ends with the ground filled up to the deck (the engine puts a road on the ground, and no bridge pieces are laid), a tunnel is left on the surface.");
        foreach (var kv in r.ByHighway.OrderByDescending(k => k.Value.Metres)) L(F($"    {kv.Key,-16} {kv.Value.Roads,5} roads {kv.Value.Metres / 1000,7:0.0} km"));
        L(F($"  {r.Bridges} bridge roads (the deepest fill {r.MaxBridgeFill:0} m) and {r.Tunnels} tunnel roads. Junctions are plain overlaps of roads (no intersection pieces)."));
        if (!allRoads) L("  Residential streets and other minor roads are not in this map (run with --roads all).");
        L();
        L("Play");
        L(F($"  {r.Spawns} spawn points and {r.Regens} regen pads along the main roads; a melee map."));
        L();
        L("Files");
        foreach (var f in new[] { lvl, cbt, opt.Name + ".ter" }.OfType<string>()) L("  " + f);
        L(lvl is not null && cbt is not null ? "  (" + lvl + " is for Interstate '76, " + cbt + " for the Nitro Pack; both use the same .ter)" : lvl is not null ? "  (for Interstate '76)" : "  (for the Nitro Pack)");
        L();
        L("Install");
        L("  Copy these files into the game's addon\\ folder. The map appears in the melee arena list as \"" + opt.DisplayName + "\".");
        L();
        foreach (var n in notes) L("Note: " + n);
        L("Sources and attributions");
        L("  Elevation: Mapzen/AWS Terrain Tiles (https://registry.opendata.aws/terrain-tiles/), which for the United States come from USGS 3DEP.");
        L("  Roads: (c) OpenStreetMap contributors, Open Database Licence (ODbL), https://www.openstreetmap.org/copyright, through the Overpass API. This map contains data derived from OpenStreetMap; if you share it, keep this credit.");
        L("  Made with I26 (https://github.com/vahokif/i26).");
        return sb.ToString();
    }

}
