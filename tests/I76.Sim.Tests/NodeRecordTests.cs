using I76.Sim.Collision;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A destroyed twin ('A') is only geometry: the original swaps the node's geometry to it and keeps the node, with the
/// flags and the type of the intact record it was made from. So the twin's collider is the intact node's class and type,
/// whatever the twin's own record says.
/// </summary>
public class NodeRecordTests
{
    /// <summary>
    /// The Wagon Wheel (T01): its fallen lot DW1ALOT1 is typed 2 in its own record, its node DW1_LOT1 is a lot (0x51). A
    /// lot is drawn with the ground and has no collider; the wreck's lot was a 70 m flat box.
    /// </summary>
    [Fact]
    public void AFallenLotIsNoCollider()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 }) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bdwgnwl1", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bdwgnwl1", 4100, 36700);
        int lot = o.Nodes.FindIndex(n => n.Name.Equals("DW1_LOT1", StringComparison.OrdinalIgnoreCase));
        Assert.True(lot >= 0);
        var wreck = o.Remains!;
        Assert.NotEmpty(wreck.Boxes);
        Assert.DoesNotContain(wreck.Boxes, b => b.Node == lot);
        Assert.DoesNotContain(wreck.Polys, p => p.Node == lot);
    }

    /// <summary>
    /// The Catch-E-Na Inn: its upper roof CN1_RF23 is a type-0x37 node of class 0x1000 (no collider); its fallen twin's own
    /// record says type 2, box. The wreck keeps the node's: no box for it.
    /// </summary>
    [Fact]
    public void AFallenRoofKeepsItsNodesClass()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 }) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcnofcb3", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcnofcb3", 4100, 36700);
        int roof = o.Nodes.FindIndex(n => n.Name.Equals("CN1_RF23", StringComparison.OrdinalIgnoreCase));
        int lower = o.Nodes.FindIndex(n => n.Name.Equals("CN1_RF13", StringComparison.OrdinalIgnoreCase));
        Assert.True(roof >= 0 && lower >= 0);
        var wreck = o.Remains!;
        Assert.DoesNotContain(wreck.Boxes, b => b.Node == roof);
        // the lower roof, a type-2 box node, keeps its box
        Assert.Contains(wreck.Boxes, b => b.Node == lower);
    }
}
