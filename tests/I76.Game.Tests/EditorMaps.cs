using I76.Editor;
using I76.Editor.Geo;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Game.Tests;

/// <summary>
/// Maps as the map editor makes them, written to a folder as the editor saves them: a new flat map, a real-world map (from a
/// synthetic elevation: no network), and a stock map with its ground raised.
/// </summary>
public static class EditorMaps
{
    /// <summary>A rolling country: relief of <paramref name="relief"/> metres over a kilometre or two, on a base of <paramref name="baseMetres"/> above sea level.</summary>
    private sealed class Hills(GeoGrid g, double baseMetres, double relief) : IElevation
    {
        public double At(double lat, double lon)
        {
            var (x, z) = g.ToMap(lat, lon);
            double u = x / 900, v = z / 1100;
            return baseMetres + relief * (0.5 + 0.25 * Math.Sin(u) * Math.Cos(v * 1.3) + 0.15 * Math.Sin(2.7 * u + 1) * Math.Sin(2.1 * v) + 0.1 * Math.Sin(0.9 * (u + v)));
        }
    }

    public static string FlatMap(string dir, string name, int tiles, double metres)
    {
        var (_, files) = NewMap.CreateFlat(dir, name, name, tiles, metres, nitro: false);
        return files.First(f => f.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A real-world map of cols x rows tiles over rolling country (what New map from real world builds).</summary>
    public static string RealWorldMap(string dir, string name, int cols, int rows, double baseMetres, double relief)
    {
        double lat = 45.83, lon = -108.34;
        var g = GeoGrid.Plan(lat, lon, lat + cols * 640 / 111_200.0, lon + rows * 640 / 77_600.0);
        var opt = new GeoMapOptions(lat, lon, lat + 1, lon + 1, name, name, Look: GeoMapOutput.GrasslandLook());
        var (proj, _) = GeoMapBuilder.Build(opt, g, new Hills(g, baseMetres, relief), []);
        return proj.SaveTo(dir).First(f => f.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cbt", StringComparison.OrdinalIgnoreCase));
    }
}
