using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The Nitro Pack's melee tyre picker fits the other wheel of the axle too.</summary>
public class AxlePairTests
{
    private static PartDef Tyre(string id) => new(id, 5, 0, id, id.ToLowerInvariant(), 0, 10, 1);

    private static TripInventory Car(PartDef t, params int[] slots)
    {
        var inv = new TripInventory();
        foreach (int s in slots) { inv.Slots.Add(new CarSlot(5, "WHL")); inv.Nodes.Add(new PartNode(t, 10, PartState.Car, s)); }
        return inv;
    }

    [Fact]
    public void ThePickGoesIntoBothWheelsOfTheAxleOnly()
    {
        var small = Tyre("WAUTO_0A.A"); var big = Tyre("WAUTO_0A.C");
        var inv = Car(small, 0, 1, 2, 3);
        inv.InstallAxle(0, 1, new PartNode(big, 10, PartState.Van));
        Assert.Equal(big, inv.InSlot(0)!.Part);
        Assert.Equal(big, inv.InSlot(1)!.Part);
        Assert.Equal(small, inv.InSlot(2)!.Part);
        Assert.Equal(small, inv.InSlot(3)!.Part);
        Assert.Equal(4, inv.Nodes.Count);                  // the old tyres are dropped, not hoarded
        Assert.NotSame(inv.InSlot(0), inv.InSlot(1));      // two nodes, so a later pick never moves both
    }

    [Fact]
    public void ACarWithoutTheMateTakesTheOneSlot()
    {
        var small = Tyre("A"); var big = Tyre("B");
        var inv = Car(small, 0);
        inv.InstallAxle(0, -1, new PartNode(big, 10, PartState.Van));
        Assert.Equal(big, inv.InSlot(0)!.Part);
        Assert.Single(inv.Nodes);
    }
}
