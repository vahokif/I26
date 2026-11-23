using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// Roads, parking lots and road pieces in the map's inactive zone (a ZMAP cell marked empty, which holds no terrain tile): the original
/// draws them with no zone or tile test, so they stand on the ground drawn there. Measured with T13's cell (row 70, col 8), the one under
/// Spanner's truck stop, emptied: the station and its whole lot on flat ground. Only the chunks of the outside ground that a road or a
/// painted face reaches carry a road layer (MissionBuilder.OutsideRoadOrigins), and no stock map has any.
/// </summary>
public class OutsideRoadsTests(ITestOutputHelper log)
{
    private static MissionWorld World(string path) => new(TestInstall.Vfs, new ModelAssembler(TestInstall.Vfs), path);

    /// <summary>What the loader paints: every road quad with the gap fills, and the painted faces (lots, road pieces) by the map's ground-list rule.</summary>
    private static List<(int X0, int Z0)> Outside(MissionWorld w)
    {
        bool groundList = RoadPieceObjects.GroundListDrawn(w.Mission);
        var tris = MissionBuilder.PaintedTops(w, new ModelAssembler(TestInstall.Vfs), groundList)
            .SelectMany(t => Enumerable.Range(1, t.W.Length - 2).Select(k =>
            {
                var (a, b, c) = (t.W[0], t.W[k], t.W[k + 1]);
                var min = System.Numerics.Vector2.Min(System.Numerics.Vector2.Min(a, b), c);
                var max = System.Numerics.Vector2.Max(System.Numerics.Vector2.Max(a, b), c);
                return new RoadRaster.FlatTri(a, b, c, default, default, default, 0, min, max);
            })).ToList();
        var flat = RoadRaster.Flatten(Roads.Build(w.Mission, w.BilinearHeight));
        var fills = RoadRaster.Flatten(RoadGaps.Fill(Roads.Segments(w.Mission, w.BilinearHeight), flat, tris, w.BilinearHeight));
        return MissionBuilder.OutsideRoadOrigins(w, [.. flat, .. fills], tris);
    }

    private static MissionWorld T13(bool emptyCell)
    {
        var w = World(TestInstall.Install.Resolve("miss16", "t13.msn")!);
        if (emptyCell) w.Terrain.Grid[70 * MissionFile.ZmapGrid + 8] = MissionFile.ZmapEmpty;
        return w;
    }

    [InstallFact]
    public void NoStockMapPaintsTheOutsideGround()
    {
        var missions = Directory.EnumerateFiles(TestInstall.Install.MissionDir!).Where(MapFileSet.IsStockMission)
            .Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
        Assert.NotEmpty(missions);
        var reached = new List<string>();
        foreach (var f in missions)
        {
            var o = Outside(World(f));
            if (o.Count > 0) reached.Add($"{Path.GetFileName(f)}: {string.Join(" ", o)}");
        }
        log.WriteLine($"{missions.Count} stock missions");
        Assert.Empty(reached);
    }

    [InstallFact]
    public void SpannersLotInAnEmptyCellIsPainted()
    {
        Assert.Empty(Outside(T13(emptyCell: false)));
        var w = T13(emptyCell: true);
        var got = Outside(w);
        log.WriteLine(string.Join(" ", got));
        // the cell: samples x 1024..1151, z 8960..9087 (x 5120..5760 m, z 44800..45440 m); nothing reaches past it
        Assert.All(got, o => Assert.True(o.X0 is >= 1024 and < 1152 && o.Z0 is >= 8960 and < 9088, $"{o} outside the cell"));
        // the chunk under the station (5465, 45140) and its lot's
        var station = w.Placements.First(p => p.Object.Name == "bfnspan1");
        int sx = (int)MathF.Floor(station.Object.Position.X / 160f) * 32, sz = (int)MathF.Floor(station.Object.Position.Z / 160f) * 32;
        Assert.Contains((sx, sz), got);
    }

    /// <summary>Without road data the original draws no roads, lots or road pieces anywhere: nothing on the outside ground either.</summary>
    [InstallFact]
    public void WithoutRoadsTheEmptyCellHasNone()
    {
        var w = T13(emptyCell: true);
        w.Mission.RoadOverride = [];
        Assert.False(RoadPieceObjects.GroundListDrawn(w.Mission));
        Assert.Empty(Outside(w));
    }
}
