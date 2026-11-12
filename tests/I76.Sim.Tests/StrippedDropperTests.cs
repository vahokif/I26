using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #153: after a dropper was stripped in the 2D garage, the next won trip mission (T09 in the report) dropped to the main
/// menu with an index error. The strip left the dropper marked in the car with no slot, and the shell's entry after a win
/// looked its slot up.
/// </summary>
public class StrippedDropperTests
{
    private static readonly PartDef Engine = new("V8", 2, 0, "eng01", "eng01", 1, 300, 400);
    private static readonly PartDef Gun = new("30cal MG", 7, 0, "gmlight", "gmlight.gdf", 0, 50, 40);
    private static readonly PartDef Mines = new("Mine Dropper", 8, 0, "glandmin", "glandmin.gdf", 0, 40, 60);

    /// <summary>An engine, a forward gun and a dropper, each in its slot, at full health.</summary>
    private static TripInventory Car()
    {
        var inv = new TripInventory();
        inv.Slots.Add(new CarSlot(2, "ENGINE"));
        inv.Slots.Add(new CarSlot(7, "#1 Fwd", Hardpoint: 0, MountStyle: 1));
        inv.Slots.Add(new CarSlot(8, "#2 Dropper", Hardpoint: 1, MountStyle: 4));
        inv.Nodes.Add(new PartNode(Engine, 300, PartState.Car, 0));
        inv.Nodes.Add(new PartNode(Gun, 50, PartState.Car, 1));
        inv.Nodes.Add(new PartNode(Mines, 40, PartState.Car, 2));
        return inv;
    }

    private static CarState Damaged() => new(120, 0, 0, new int[6], [(0, 20, -1), (1, 10, -1)]);

    [Fact]
    public void AStrippedDropperGoesToTheVanAsAWeaponDoes()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        inv.Strip(mines);
        inv.Strip(inv.InSlot(1)!);
        Assert.Equal((PartState.Van, -1), (mines.State, mines.Slot));
        Assert.Null(inv.InSlot(2));
        Assert.Equal(2, inv.In(PartState.Van, 4).Count());
        // the next won mission: the damage lands on the engine, the stripped parts keep their health
        inv.MissionWon(Damaged(), null, new PartsDatabase(), () => 0);
        Assert.Equal(120, inv.InSlot(0)!.Health);
        Assert.Equal(40, mines.Health);
    }

    /// <summary>A trip saved before the fix: the dropper is still marked in the car with slot −1. The won mission does not throw.</summary>
    [Fact]
    public void AWonMissionAfterAnOldStripDoesNotThrow()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        mines.Slot = -1;                               // what the old strip left
        inv.MissionWon(Damaged(), null, new PartsDatabase(), () => 0);
        Assert.Equal(120, inv.InSlot(0)!.Health);
        Assert.Equal(20, inv.InSlot(1)!.Health);
        Assert.Equal(40, mines.Health);
    }

    /// <summary>Loading such a trip puts the dropper in the van, where the garage can fit it again.</summary>
    [Fact]
    public void LoadingAnOldStripPutsTheDropperInTheVan()
    {
        var inv = Car();
        inv.InSlot(2)!.Slot = -1;
        var db = new PartsDatabase();
        db.Parts.AddRange([Engine, Gun, Mines]);
        var back = TripInventory.Load(inv.Save(), db, null!)!;
        var mines = back.Nodes.Single(n => n.Part == Mines);
        Assert.Equal((PartState.Van, -1), (mines.State, mines.Slot));
        Assert.Equal(PartState.Car, back.InSlot(1)!.State);
        back.MissionWon(Damaged(), null, db, () => 0);
    }
}
