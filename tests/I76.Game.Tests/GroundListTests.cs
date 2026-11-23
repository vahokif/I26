using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The original draws every placement's lot (SGEO type 0x51) and every Intersection-class placement in its road pass, which runs
/// only on a map with road data (RoadPieceObjects.GroundListDrawn). On a map without roads it shows a building without its paved
/// ground and no intersection piece; a piece placed as a structure still shows (measured: T13 with its road data taken out, and
/// a mission maker's Mondo Burger on a map without roads). M02 and M15 ship without roads: M15's donut stand has no lot.
/// </summary>
public class GroundListTests
{
    private static MissionWorld World(string msn) =>
        new(TestInstall.Vfs, new ModelAssembler(TestInstall.Vfs), TestInstall.Install.Resolve("miss16", msn + ".msn")!);

    private static List<string> Painted(MissionWorld w, string name, uint type, bool groundList)
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        return w.Placements.Where(p => p.Object.Name == name && p.Object.ObjectType == type)
            .SelectMany(p => GroundDecals.PaintedFaces(p, asm.Assemble(p.Definition!), groundList)).Select(x => x.Part.NodeName).ToList();
    }

    [InstallTheory]
    [InlineData("m02", false)]
    [InlineData("m15", false)]
    [InlineData("t01", true)]
    [InlineData("t13", true)]
    public void AMapDrawsItsLotsAndRoadPiecesOnlyWithRoads(string msn, bool drawn) =>
        Assert.Equal(drawn, RoadPieceObjects.GroundListDrawn(World(msn).Mission));

    /// <summary>T13 with its roads taken away (the map editor's road override is the same as a file without them).</summary>
    [InstallFact]
    public void WithoutRoadsNoLotAndNoIntersectionIsPainted()
    {
        var w = World("t13");
        Assert.True(RoadPieceObjects.GroundListDrawn(w.Mission));
        // Spanner's truck stop (Struct1): its lot; the asphalt T (Intersection class); the dirt piece beside it, placed as Struct1
        Assert.Contains("FN1_LOT1", Painted(w, "bfnspan1", 2, true));
        Assert.NotEmpty(Painted(w, "itisect1", 0x50, true));
        var dirt = Painted(w, "idisect1", 2, true);
        Assert.NotEmpty(dirt);

        w.Mission.RoadOverride = [];
        Assert.False(RoadPieceObjects.GroundListDrawn(w.Mission));
        Assert.Empty(Painted(w, "bfnspan1", 2, false));
        Assert.Empty(Painted(w, "itisect1", 0x50, false));
        Assert.Equal(dirt, Painted(w, "idisect1", 2, false));
    }

    /// <summary>M15's donut stand stands on the bare arena: its lot is neither painted nor drawn; the stand itself is.</summary>
    [InstallFact]
    public void M15sDonutStandHasNoLot()
    {
        var w = World("m15");
        bool groundList = RoadPieceObjects.GroundListDrawn(w.Mission);
        Assert.Empty(Painted(w, "bddonut1", 2, groundList));
        var m = new ModelAssembler(TestInstall.Vfs).Assemble("bddonut1.sdf")!;
        Assert.True(RoadPieceObjects.OffTheGroundList(2, m, "DD1_LOT1", groundList));
        Assert.True(RoadPieceObjects.OffTheGroundList(2, m, "DD1ALOT1", groundList));
        Assert.False(RoadPieceObjects.OffTheGroundList(2, m, "DD1_BLD1", groundList));
        Assert.False(RoadPieceObjects.OffTheGroundList(2, m, "DD1_LOT1", groundList: true));
        // a road piece is off the list whole, whatever the node; placed as a structure, it is not
        Assert.True(RoadPieceObjects.OffTheGroundList(0x52, m, "DD1_BLD1", groundList));
    }
}
