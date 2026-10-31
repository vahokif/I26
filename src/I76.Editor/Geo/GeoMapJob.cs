using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Editor.Geo;

public enum GeoGame { I76, Nitro, Both }

/// <summary>What the real-world map dialog asks for.</summary>
public sealed record GeoMapRequest(double LatA, double LonA, double LatB, double LonB, string Stem, string DisplayName, GeoGame Game, bool AllRoads, int Hour, string OutDir);

public enum GeoStage { Checking, Terrain, Roads, Building, Writing, Done }

/// <summary>A progress report: the stage, a line of text and how far through the whole job (0 to 1).</summary>
public readonly record struct GeoProgress(GeoStage Stage, string Text, double Fraction);

/// <summary>The real-world map build the dialog runs on a worker thread: the same steps as <c>i76 geomap</c>, reporting progress and stopping when cancelled.</summary>
public static class GeoMapJob
{
    public static string Run(GeoMapRequest r, GeoFetch fetch, GameInstall? install, Action<GeoProgress> progress, CancellationToken ct)
    {
        fetch.Cancel = ct;
        void P(GeoStage st, string t, double f) { ct.ThrowIfCancellationRequested(); progress(new GeoProgress(st, t, f)); }
        P(GeoStage.Checking, "Checking the area", 0);
        var check = AreaCheck.Of(r.LatA, r.LonA, r.LatB, r.LonB);
        if (!check.Fits) throw new ArgumentException(check.Message);
        if (!MapSaver.ValidName(r.Stem)) throw new ArgumentException("The map name is 1 to 8 letters, digits and underscores.");
        if (!MapSaver.ValidDisplayName(r.DisplayName)) throw new ArgumentException($"The listed name is 1 to {MapSaver.MaxDisplayName} characters.");
        var grid = GeoGrid.Plan(r.LatA, r.LonA, r.LatB, r.LonB);
        var (s0, w0, n0, e0) = grid.Bounds(60);

        fetch.TileProgress = (d, n) => progress(new GeoProgress(GeoStage.Terrain, $"Terrain tile {d} of {n}", 0.05 + 0.5 * d / n));
        P(GeoStage.Terrain, "Downloading terrain", 0.05);
        var terrain = fetch.Terrain(s0, w0, n0, e0);
        fetch.OverpassWait = (round, secs) => progress(new GeoProgress(GeoStage.Roads, $"The road server is busy: trying again in {secs} s (attempt {round + 1})", 0.6));
        fetch.OverpassTrying = (host, secs) => progress(new GeoProgress(GeoStage.Roads, $"Downloading roads from {host}: {secs} s (a big area can take a minute or two)", 0.6));
        P(GeoStage.Roads, "Downloading roads", 0.6);
        var ways = OsmRoads.Parse(fetch.Overpass(GeoFetch.HighwayQuery(s0, w0, n0, e0)));

        P(GeoStage.Building, "Cutting the terrain and laying the roads", 0.75);
        var notes = new List<string>();
        var look = GeoMapOutput.GrasslandLook(Math.Clamp(r.Hour, 0, 23));
        if (install is not null) look = GeoMapOutput.Checked(look, MapLookCatalog.Scan(new AssetVfs(install)), notes);
        var opt = new GeoMapOptions(r.LatA, r.LonA, r.LatB, r.LonB, r.Stem, r.DisplayName, r.AllRoads, r.Game == GeoGame.Nitro, look);
        var (project, rep) = GeoMapBuilder.Build(opt, grid, terrain, ways);

        P(GeoStage.Writing, "Writing the map", 0.95);
        Directory.CreateDirectory(r.OutDir);
        GeoMapOutput.Save(project, opt, r.OutDir, r.Game is GeoGame.I76 or GeoGame.Both, r.Game is GeoGame.Nitro or GeoGame.Both, out var lvl, out var cbt);
        File.WriteAllText(Path.Combine(r.OutDir, r.Stem + "_geo.txt"), GeoMapOutput.Readme(opt, rep, lvl, cbt, r.AllRoads, notes.Where(x => !x.StartsWith("the look was not")).ToList()));
        progress(new GeoProgress(GeoStage.Done, $"{rep.Roads} roads, {rep.RoadMetres / 1000:0.0} km", 1));
        return r.Stem;
    }

    /// <summary>A plain sentence for what went wrong.</summary>
    public static string Explain(Exception e) => e switch
    {
        OperationCanceledException => "Cancelled.",
        ArgumentException a => a.Message,
        HttpRequestException h => "Could not reach the download servers: " + (h.InnerException?.Message ?? h.Message) + " Check the internet connection and try again.",
        IOException io when io.InnerException is HttpRequestException => "Could not reach the download servers (are you offline?). " + io.Message,
        IOException io => io.Message,
        UnauthorizedAccessException u => "Could not write the map: " + u.Message,
        _ => "The map could not be built: " + e.Message,
    };
}
