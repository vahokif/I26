using I76.Formats.Save;
using I76.Formats.Tests;
using I76.Game.Shell;
using I76.Sim.Trip;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The original's bookmarks become I26 bookmarks: the car as its garage showed it, every part where it was (needs an install for the parts and the body).</summary>
public class OriginalSaveImportTests
{
    private static readonly Lazy<PartsDatabase> Db = new(() => PartsDatabase.Build(TestInstall.Vfs));

    private static OriginalPart P(string name, int type, string def, int cond, int state, int full) => new(cond, state, name, type, 0, "", def, 0, full, 1f);

    /// <summary>A Piranha with the stock engine and tyres, a 30cal MG on its second weapon hardpoint, NitrousOxide, and parts in the van, the repair order and the field.</summary>
    private static OriginalSave Piranha(Func<PartDef> engine, Func<PartDef> tyre, PartDef gun, PartDef dropper, int extraGuns = 0)
    {
        var e = engine();
        var t = tyre();
        var s = new OriginalSave
        {
            CarName = "Picard Piranha", Variant = "Stock (Orange)", Vdf = "vppirnha.vdf", Vtf = "piranha2.vtf",
            Wdf = [t.Stem, "", t.Stem], Armour = [910, 570, 570, 700], Chassis = [300, 250, 250, 280], Ninth = 30,
        };
        var hps = I76.Formats.Bwd2.Hardpoint.Read(new I76.Formats.Bwd2.Bwd2File(TestInstall.Vfs.Read("vppirnha.vdf")));
        s.Registry.Add(new OriginalSlot(2, "ENGINE", 0, 0));
        foreach (var h in hps.Take(4)) s.Registry.Add(new OriginalSlot(h.Style == 4 ? 8 : 7, h.Name, 0, 0));
        var gunHp = hps.Take(4).Select((h, i) => (h, i)).First(x => x.h.Style != 4).i;
        s.Equipped = [e.Id, "", "", t.Id, t.Id, t.Id, t.Id, "Empty", "Empty", "Empty", "Empty", "NitrousOxide", "", ""];
        s.Equipped[7 + gunHp] = gun.Id;
        s.Inventory.Add(P(e.Id, 2, e.Stem, 150, 1, e.MaxHp));                 // 0 mounted, damaged
        for (int k = 0; k < 4; k++) s.Inventory.Add(P(t.Id, 5, t.Stem, t.MaxHp, 1, t.MaxHp));
        s.Inventory.Add(P(gun.Id, gun.Type, gun.Stem, 40, 1, gun.MaxHp));    // 5 mounted
        s.Inventory.Add(P(gun.Id, gun.Type, gun.Stem, 20, 3, gun.MaxHp));    // 6 queued
        s.Inventory.Add(P(gun.Id, gun.Type, gun.Stem, 20, 3, gun.MaxHp));    // 7 in repair, not queued
        s.Inventory.Add(P(dropper.Id, 8, dropper.Stem, 90, 2, dropper.MaxHp)); // 8 van
        s.Inventory.Add(P("NitrousOxide", 13, "spc02", 0, 1, 0));            // 9 mounted special
        s.Inventory.Add(P(gun.Id, gun.Type, gun.Stem, 60, 4, gun.MaxHp));    // 10 field salvage
        for (int k = 0; k < extraGuns; k++) s.Inventory.Add(P(gun.Id, gun.Type, gun.Stem, 100, 2, gun.MaxHp));
        s.RepairQueue.Add(s.Inventory[6]);
        return s;
    }

    private static OriginalSave Stock(int extraGuns = 0)
    {
        var db = Db.Value;
        return Piranha(() => db.Parts.First(p => p.Type == 2 && !p.IsEmpty), () => db.ByStem("wauto_1a.wdf")!,
            db.Parts.First(p => p.Type == 7 && !p.IsTurret && !p.IsEmpty), db.Parts.First(p => p.Type == 8 && !p.IsEmpty), extraGuns);
    }

    [InstallFact]
    public void The_car_parts_conditions_and_places_carry_over()
    {
        var s = Stock();
        var imp = OriginalSaveImport.Convert(new OriginalSaveEntry(3, "Easy Groove!", "save013", 1, 0), s, Db.Value, TestInstall.Vfs);
        var inv = imp.Inventory;
        Assert.Equal((3, "Easy Groove!", 1, false), (imp.Mission, imp.Name, imp.State, imp.NoSalvage));
        Assert.Equal(("vppirnha.vdf", "piranha2.vtf"), (inv.Car.Vdf, inv.Car.Vtf));
        Assert.Equal([910, 570, 570, 700], inv.Car.Armour);
        Assert.Equal([300, 250, 250, 280], inv.Car.Chassis);
        Assert.Equal(30, inv.Car.Ninth);
        Assert.Equal(s.Inventory.Count, inv.Nodes.Count);
        // the engine, four tyres, the gun and the special in their slots, the engine and gun damaged as saved
        var onCar = inv.Nodes.Where(n => n.State == PartState.Car).ToList();
        Assert.Equal(7, onCar.Count);
        Assert.Equal(150, inv.Nodes.Single(n => n.State == PartState.Car && n.Part.Type == 2).Health);
        var gun = inv.Nodes.Single(n => n.State == PartState.Car && n.Part.Type == 7);
        Assert.Equal(40, gun.Health);
        Assert.Contains(inv.Car.Weapons, w => w.Gdf == gun.Part.Stem && w.Hardpoint == inv.Slots[gun.Slot].Hardpoint);
        Assert.Equal(2, inv.Car.Specials[0]);
        Assert.Equal(4, inv.Slots.Select((sl, i) => (sl, i)).Count(x => x.sl.Type == 5 && inv.InSlot(x.i) is not null));
        // the queued gun first, then the one in repair the queue did not name; the van's dropper; the field's gun
        Assert.Equal(2, inv.Repairs.Count);
        Assert.All(inv.Repairs, r => Assert.Equal(PartState.Repair, r.State));
        Assert.Same(inv.Nodes[6], inv.Repairs[0]);
        Assert.Equal(PartState.Van, inv.Nodes[8].State);
        Assert.Equal(PartState.Field, inv.Nodes[10].State);
        // the bookmark's car file loads back the same
        var back = TripInventory.Load(inv.Save(), Db.Value, TestInstall.Vfs)!;
        Assert.Equal(inv.Save(), back.Save());
        Assert.Equal(150, back.State().EngineHp);
    }

    [InstallFact]
    public void The_twelfth_weapon_goes_to_field_salvage_as_the_original_garage_puts_it()
    {
        // 3 guns and a dropper outside the field, 9 more in the van: 13, so the last two are field salvage
        var imp = OriginalSaveImport.Convert(new OriginalSaveEntry(5, "", "save001", 8, 0), Stock(extraGuns: 9), Db.Value, TestInstall.Vfs);
        var n = imp.Inventory.Nodes;
        Assert.Equal(PartState.Field, n[^1].State);
        Assert.Equal(PartState.Field, n[^2].State);
        Assert.Equal(PartState.Van, n[^3].State);
        Assert.Equal(2, imp.Notes.Count(x => x.Contains("Field Salvage")));
    }

    [InstallFact]
    public void A_named_part_that_is_not_on_the_car_leaves_its_slot_empty()
    {
        var s = Stock();
        s.Equipped[0] = "No Such Engine";
        var imp = OriginalSaveImport.Convert(new OriginalSaveEntry(2, "", "save002", 1, 0), s, Db.Value, TestInstall.Vfs);
        Assert.DoesNotContain(imp.Inventory.Nodes, n => n.State == PartState.Car && n.Part.Type == 2);
        Assert.Equal(PartState.Van, imp.Inventory.Nodes[0].State);
        Assert.Equal(21, imp.Inventory.Validate());     // DONE asks for an engine, as the original's would
    }

    /// <summary>Every bookmark of a folder of the original's saves (I76_ORIGINAL_SAVES, or the install's own) imports and its car file loads back the same.</summary>
    [InstallFact]
    public void The_saves_of_an_install_import()
    {
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("I76_ORIGINAL_SAVES") is { Length: > 0 } o) folders.AddRange(OriginalSaveFolders.Find(o, null));
        folders.AddRange(OriginalSaveFolders.Find(TestInstall.Dir!, null));
        foreach (var f in folders)
            foreach (var (e, s, _) in OriginalSaveFolders.Read(f))
            {
                if (s is null) continue;
                var inv = OriginalSaveImport.Convert(e, s, Db.Value, TestInstall.Vfs).Inventory;
                Assert.Equal(inv.Save(), TripInventory.Load(inv.Save(), Db.Value, TestInstall.Vfs)!.Save());
                Assert.All(inv.Nodes.Where(n => n.State == PartState.Car).GroupBy(n => n.Slot), g => Assert.Single(g));
                Assert.Equal(inv.Nodes.Count(n => n.State == PartState.Repair), inv.Repairs.Count);
            }
    }
}
