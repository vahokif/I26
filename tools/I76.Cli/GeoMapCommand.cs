using System.Globalization;
using I76.Editor;
using I76.Editor.Geo;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// i76 geomap --bbox LAT1,LON1,LAT2,LON2 --name NAME --out DIR [--roads major|all] [--game i76|nitro|both] [--stem STEM] [--hour H] [--install DIR]
/// A playable map from real terrain (AWS terrain tiles) and roads (OpenStreetMap): docs/map-editor.md "Geographic maps".
/// The downloads are cached under ~/.cache/i26-geo/. --install (or I76_INSTALL) lets the map's look be checked against the archive.
/// </summary>
public static class GeoMapCommand
{
    public static int Run(List<string> argv, string? installDir)
    {
        string? Opt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
        if (Opt("--bbox") is not { } bb || Opt("--name") is not { } name || Opt("--out") is not { } outDir)
        {
            Console.Error.WriteLine("usage: i76 geomap --bbox LAT1,LON1,LAT2,LON2 --name NAME --out DIR [--roads major|all] [--game i76|nitro|both] [--stem STEM] [--hour H] [--install DIR]");
            return 2;
        }
        try
        {
            var v = bb.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (v.Length != 4 || v.Any(double.IsNaN) || Math.Abs(v[0]) > 85 || Math.Abs(v[2]) > 85 || Math.Abs(v[1]) > 180 || Math.Abs(v[3]) > 180) throw new ArgumentException("--bbox LAT1,LON1,LAT2,LON2 in degrees");
            string roads = Opt("--roads") ?? "major", game = Opt("--game") ?? "i76";
            if (roads is not ("major" or "all")) throw new ArgumentException("--roads major|all");
            if (game is not ("i76" or "nitro" or "both")) throw new ArgumentException("--game i76|nitro|both");
            string stem = Opt("--stem") ?? GeoMapOutput.StemOf(name);
            string display = name.Length <= MapSaver.MaxDisplayName ? name : name[..MapSaver.MaxDisplayName];
            if (!MapSaver.ValidName(stem)) throw new ArgumentException($"map file name '{stem}': 1-{MapProjectStem} letters, digits and underscores (--stem)");
            if (!MapSaver.ValidDisplayName(display)) throw new ArgumentException($"listed name '{display}': 1-{MapSaver.MaxDisplayName} printable characters");
            int hour = int.Parse(Opt("--hour") ?? "9", CultureInfo.InvariantCulture);

            var grid = GeoGrid.Plan(v[0], v[1], v[2], v[3]);
            Console.WriteLine($"{grid.BoxWidth / 1000:0.00} x {grid.BoxHeight / 1000:0.00} km -> {grid.Cols} x {grid.Rows} = {grid.Tiles} tiles" + (grid.CropNote is { } n ? $"\n  {n}" : ""));
            var fetch = new GeoFetch { Log = s => Console.WriteLine("  " + s) };
            var (s0, w0, n0, e0) = grid.Bounds(60);
            var terrain = fetch.Terrain(s0, w0, n0, e0);
            var ways = OsmRoads.Parse(fetch.Overpass(GeoFetch.HighwayQuery(s0, w0, n0, e0)));
            Console.WriteLine($"  downloaded {fetch.Downloaded / 1024.0 / 1024:0.0} MB this run ({fetch.TilesDownloaded} terrain tiles); {ways.Count} highway ways");

            var notes = new List<string>();
            var look = GeoMapOutput.GrasslandLook(hour);
            if (installDir is not null && Directory.Exists(installDir))
                look = GeoMapOutput.Checked(look, MapLookCatalog.Scan(new AssetVfs(new GameInstall(installDir))), notes);
            else notes.Add("the look was not checked against an install (--install)");
            var opt = new GeoMapOptions(v[0], v[1], v[2], v[3], stem, display, roads == "all", game == "nitro", look);
            var (project, rep) = GeoMapBuilder.Build(opt, grid, terrain, ways);

            Directory.CreateDirectory(outDir);
            var files = GeoMapOutput.Save(project, opt, outDir, game is "i76" or "both", game is "nitro" or "both", out var lvl, out var cbt);
            File.WriteAllText(Path.Combine(outDir, "README.txt"), GeoMapOutput.Readme(opt, rep, lvl, cbt, roads == "all", notes.Where(x => !x.StartsWith("the look was not")).ToList()));

            Console.WriteLine($"heights {rep.Heights.MinMetres:0.0}..{rep.Heights.MaxMetres:0.0} m -> sample 0 = {rep.Heights.OffsetMetres:0.0} m, highest sample {rep.Heights.Raw.Max() / 10.0:0.0} m, scale {rep.Heights.Scale:0.###}");
            Console.WriteLine($"roads: {rep.Roads} ({rep.RoadPoints} points, {rep.RoadMetres / 1000:0.0} km, {rep.Bridges} bridge (deepest fill {rep.MaxBridgeFill:0.0} m), {rep.Tunnels} tunnel, {rep.Closed} loops); max cut {rep.MaxCut:0.0} m, fill {rep.MaxFill:0.0} m, steepest profile grade {rep.MaxGrade:0.000}");
            Console.WriteLine($"ground along the roads: steepest step {rep.FinalMaxGrade:0.000} ({rep.WorstGrade}), {rep.StepsOver20} of {rep.Steps} steps over 0.20, largest edge-to-edge fall {rep.MaxCrossFall:0.00} m");
            if (Environment.GetEnvironmentVariable("I76_GEOMAP_VERBOSE") is not null) foreach (var x in rep.Steep) Console.WriteLine("  steep: " + x);
            Console.WriteLine($"ways: {rep.Import.Ways} read, {rep.Import.NotForCars} not wanted, {rep.Import.OutsideMap} outside, {rep.Import.TooShort} too short, {rep.Import.Split} roads split at tight bends");
            foreach (var kv in rep.Import.LeftOutByHighway.OrderByDescending(k => k.Value)) Console.WriteLine($"  left out  {kv.Key,-16} {kv.Value}");
            foreach (var kv in rep.ByHighway.OrderByDescending(k => k.Value.Metres)) Console.WriteLine($"  imported  {kv.Key,-16} {kv.Value.Roads} roads {kv.Value.Metres / 1000:0.0} km");
            Console.WriteLine($"{rep.Spawns} spawns, {rep.Regens} regens");
            foreach (var x in notes) Console.WriteLine("note: " + x);
            foreach (var f in files) Console.WriteLine(f);
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine("geomap: " + e.Message);
            return 1;
        }
    }

    private const int MapProjectStem = 8;
}
