using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The 2D garage's strip (a click left of a row's arrow) on a dropper. The original's form empties the slot but leaves the
/// dropper marked in the car ("(C)") in no slot; I26 sends it to the van as a weapon or special (docs/deviations.md),
/// where the dropper picker offers it again. A trip saved before that keeps such a dropper and is moved on load.
/// #153: a won mission after the old strip dropped to the main menu with an index error.
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

    private static PartsDatabase Db()
    {
        var db = new PartsDatabase();
        db.Parts.AddRange([Engine, Gun, Mines]);
        return db;
    }

    private static CarState Damaged() => new(120, 0, 0, new int[6], [(0, 20, -1), (1, 10, -1)]);

    [Fact]
    public void AStrippedDropperGoesToTheVanWithItsSlotEmptyAndItsWeightOff()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        int before = inv.Weight();
        inv.Strip(mines);
        Assert.Equal((PartState.Van, -1), (mines.State, mines.Slot));
        Assert.Null(inv.InSlot(2));
        Assert.Equal(before - 60, inv.Weight());
        Assert.Contains(mines, inv.In(PartState.Van, TripInventory.ListOf(8)));
    }

    [Fact]
    public void TheDropperPickerOffersTheStrippedDropperAndFitsItBack()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        int before = inv.Weight();
        inv.Strip(mines);
        Assert.Equal([mines], inv.PickerParts(8));
        inv.Install(2, mines);
        Assert.Same(mines, inv.InSlot(2));
        Assert.Equal((PartState.Car, 2, 40), (mines.State, mines.Slot, mines.Health));
        Assert.Equal(before, inv.Weight());
        Assert.Empty(inv.In(PartState.Van, TripInventory.ListOf(8)));
    }

    [Fact]
    public void AWonMissionAfterTheStripGoesOnAsUsual()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        inv.Strip(mines);
        inv.MissionWon(Damaged(), null, Db(), () => 0);
        Assert.Equal(120, inv.InSlot(0)!.Health);
        Assert.Equal(20, inv.InSlot(1)!.Health);
        Assert.Equal((PartState.Van, -1, 40), (mines.State, mines.Slot, mines.Health));   // not on the car: unharmed
        // the next mission's car has no dropper; fitted again it has
        Assert.DoesNotContain(inv.State().Weapons, w => w.Hardpoint == 1);
        inv.Install(2, mines);
        Assert.Contains(inv.State().Weapons, w => w.Hardpoint == 1);
    }

    /// <summary>A trip saved before the fix: the dropper still marked in the car with slot −1. The won mission does not throw (#153).</summary>
    [Fact]
    public void AWonMissionWithAnOldStuckDropperDoesNotThrow()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        mines.Slot = -1;
        inv.MissionWon(Damaged(), null, Db(), () => 0);
        Assert.Equal(120, inv.InSlot(0)!.Health);
        Assert.Equal(20, inv.InSlot(1)!.Health);
        Assert.Equal(40, mines.Health);
    }

    /// <summary>Loading such a trip puts the dropper in the van, where the picker fits it again; nothing else changes.</summary>
    [Fact]
    public void LoadingAnOldStuckDropperPutsItInTheVanAndChangesNothingElse()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        mines.Slot = -1;
        var db = Db();
        var back = TripInventory.Load(inv.Save(), db, null!)!;
        var moved = back.Nodes.Single(n => n.Part == Mines);
        Assert.Equal((PartState.Van, -1), (moved.State, moved.Slot));
        mines.State = PartState.Van;   // the only difference
        Assert.Equal(inv.Save(), back.Save());
        Assert.Equal([moved], back.PickerParts(8));
        back.Install(2, moved);
        Assert.Same(moved, back.InSlot(2));
        back.MissionWon(Damaged(), null, db, () => 0);
        Assert.Equal(10, moved.Health);   // on the car again: the damage lands on it
    }

    /// <summary>A stuck dropper that was put in the repair order: it stays in the order and comes out to the van, not to "(C)".</summary>
    [Fact]
    public void AnOldStuckDropperInTheRepairOrderComesOutToTheVan()
    {
        var inv = Car();
        var mines = inv.InSlot(2)!;
        mines.Slot = -1;
        mines.Health = 20;
        Assert.True(inv.QueueRepair(mines));
        var back = TripInventory.Load(inv.Save(), Db(), null!)!;
        var queued = back.Repairs.Single();
        Assert.Equal((PartState.Repair, PartState.Van), (queued.State, queued.Saved));
        back.Unqueue(queued);
        Assert.Equal((PartState.Van, -1), (queued.State, queued.Slot));
    }

    [Fact]
    public void ATripWithNothingStuckLoadsUnchanged()
    {
        var inv = Car();
        inv.Strip(inv.InSlot(1)!);
        Assert.Equal(inv.Save(), TripInventory.Load(inv.Save(), Db(), null!)!.Save());
    }
}
