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

    /// <summary>
    /// A strip of <paramref name="tiles"/> x 1 tiles shaped like a player's canyon map: a rim 100 m and more high, uneven, round the map's edge, a basin of
    /// 10 to 60 m inside it with a ridge across the middle, so that from most places the ground ends a few hundred metres off on two sides
    /// (inside the original's 600 m clip) and kilometres off along the strip, at a height that is well above the eye.
    /// </summary>
    public static string CanyonMap(string dir, string name, int tiles)
    {
        int w = tiles * 128, h = 128;
        var project = MapProject.CreateBlank(name, name, ".lvl", tiles, 1, 400);
        var (r0, _, c0, _) = project.Terrain.Bounds();
        for (int sz = 0; sz < h; sz++)
            for (int sx = 0; sx < w; sx++)
            {
                double edge = Math.Min(Math.Min(sx, w - 1 - sx), Math.Min(sz, h - 1 - sz));   // samples in from the rim
                double rim = Math.Clamp((edge - 2) / 14.0, 0, 1);                              // 0 on the rim, 1 in the basin
                double basin = 100 + 500 * (0.5 + 0.5 * Math.Sin(sx / 23.0) * Math.Cos(sz / 17.0)) * (0.6 + 0.4 * Math.Sin(sx / 61.0 + 1));
                double bump = 450 * Math.Exp(-Math.Pow((sz - 64) / 12.0, 2)) * (0.5 + 0.5 * Math.Cos(sx / 28.0));   // a ridge across the strip, with gaps
                double rimTop = 1100 + 140 * Math.Sin(sx / 9.0 + sz / 5.0) * Math.Cos(sx / 31.0);   // a rim that is not level: 96 to 124 m
                double v = rimTop * (1 - rim * rim * (3 - 2 * rim)) + (basin + bump) * (rim * rim * (3 - 2 * rim));
                project.Terrain.SetHeight(c0 * 128 + sx, r0 * 128 + sz, (int)Math.Clamp(v, 0, 4095));
            }
        var b = project.WorldBounds();
        project.Mission.Objects.Add(MapObject.Spawn(new System.Numerics.Vector3((b.X + b.Z) / 2, 30, (b.Y + b.W) / 2)));
        return project.SaveTo(dir).First(f => f.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase));
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
