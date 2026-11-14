using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #153: after a dropper was stripped in the 2D garage, the next won trip mission (T09 in the report) dropped to the main
/// menu with an index error. The strip leaves the dropper marked in the car with no slot, as the original's form does, and
/// the after-mission damage looked its slot up.
/// </summary>
public class StrippedDropperTests
{
    private static readonly PartDef Engine = new("V8", 2, 0, "eng01", "eng01", 1, 300, 400);
    private static readonly PartDef Gun = new("30cal MG", 7, 0, "gmlight", "gmlight.gdf", 0, 50, 40);
    private static readonly PartDef Mines = new("Mine Dropper", 8, 0, "glandmin", "glandmin.gdf", 0, 40, 60);

    /// <summary>An engine, a forward gun and a dropper, each in its slot, at full health; the dropper then stripped as the 2D garage does.</summary>
    private static (TripInventory Inv, PartNode Mines) Stripped()
    {
        var inv = new TripInventory();
        inv.Slots.Add(new CarSlot(2, "ENGINE"));
        inv.Slots.Add(new CarSlot(7, "#1 Fwd", Hardpoint: 0, MountStyle: 1));
        inv.Slots.Add(new CarSlot(8, "#2 Dropper", Hardpoint: 1, MountStyle: 4));
        inv.Nodes.Add(new PartNode(Engine, 300, PartState.Car, 0));
        inv.Nodes.Add(new PartNode(Gun, 50, PartState.Car, 1));
        var mines = new PartNode(Mines, 40, PartState.Car, 2);
        inv.Nodes.Add(mines);
        mines.Slot = -1;   // the strip: the slot emptied, the part still marked "(C)"
        return (inv, mines);
    }

    private static CarState Damaged() => new(120, 0, 0, new int[6], [(0, 20, -1), (1, 10, -1)]);

    [Fact]
    public void AWonMissionAfterAStrippedDropperDoesNotThrow()
    {
        var (inv, mines) = Stripped();
        inv.MissionWon(Damaged(), null, new PartsDatabase(), () => 0);
        Assert.Equal(120, inv.InSlot(0)!.Health);
        Assert.Equal(20, inv.InSlot(1)!.Health);
        Assert.Equal((PartState.Car, -1, 40), (mines.State, mines.Slot, mines.Health));   // as the original keeps it, unharmed
    }

    /// <summary>The same after the trip is saved and loaded.</summary>
    [Fact]
    public void ASavedTripWithAStrippedDropperWinsItsNextMission()
    {
        var (inv, _) = Stripped();
        var db = new PartsDatabase();
        db.Parts.AddRange([Engine, Gun, Mines]);
        var back = TripInventory.Load(inv.Save(), db, null!)!;
        back.MissionWon(Damaged(), null, db, () => 0);
        Assert.Equal(120, back.InSlot(0)!.Health);
        Assert.Equal(PartState.Car, back.Nodes.Single(n => n.Part == Mines).State);
    }
}
