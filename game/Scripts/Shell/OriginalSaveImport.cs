using I76.Formats.Bwd2;
using I76.Formats.Save;
using I76.Formats.Vfs;
using I76.Sim.Trip;

namespace I76.Game.Shell;

/// <summary>One of the original's bookmarks as an I26 bookmark: the list row's fields, the car and parts, and what could not be carried.</summary>
public sealed record ImportedSave(OriginalSaveEntry Entry, TripInventory Inventory, List<string> Notes)
{
    /// <summary>The bookmark row: the same scene, name and state rules as the original's list; salvage management off where the row's flags say so.</summary>
    public int Mission => Entry.Scene;
    public string Name => Entry.Name;
    public int State => Entry.State;
    public bool NoSalvage => (Entry.Flags & 2) != 0;
}

/// <summary>
/// The original's bookmark body (<see cref="OriginalSave"/>) mapped onto the trip's car and parts. Pure (no engine
/// calls), so the tests run it. The car is built from the garage record as the original's garage would show it on
/// loading: each slot holds the mounted part its equipped name names, every other part keeps its place (van, repair
/// order, field salvage) and its condition.
/// </summary>
public static class OriginalSaveImport
{
    /// <summary>The weapons list's length: on loading, the original's garage puts guns and droppers not in the field into it in file order, and the twelfth and later into Field Salvage.</summary>
    public const int WeaponListCap = 11;

    private static readonly string[] TyreLabels = ["WHL FR", "WHL FL", "WHL BR", "WHL BL"];

    public static ImportedSave Convert(OriginalSaveEntry entry, OriginalSave save, PartsDatabase db, AssetVfs vfs)
    {
        var notes = new List<string>();
        var vd = vfs.TryRead(save.Vdf) ?? throw new InvalidDataException($"the install has no {save.Vdf}");
        var hps = Hardpoint.Read(new Bwd2File(vd));

        // the four equipped weapons go to the hardpoints the registry's weapon records name, in their order
        var regWeapons = save.Registry.Where(r => r.Type is 7 or 8).ToList();
        var weaponHp = new int[4];
        for (int k = 0; k < 4; k++)
            weaponHp[k] = k < regWeapons.Count && hps.FirstOrDefault(h => h.Name.Equals(regWeapons[k].Label, StringComparison.OrdinalIgnoreCase)) is { } h ? (int)h.Id : -1;

        PartDef? ById(string name, int type) => db.ById(name, type) is { IsEmpty: false } p ? p : null;
        var car = new TripCar
        {
            Variant = save.Variant, Vdf = save.Vdf, Vtf = save.Vtf,
            Engine = ById(save.Equipped[0], 2)?.Ordinal ?? 0, Suspension = ById(save.Equipped[1], 3)?.Ordinal ?? 0, Brakes = ById(save.Equipped[2], 4)?.Ordinal ?? 0,
            Wdf = [.. save.Wdf.Select(w => w.Length == 0 ? "null" : w)],
            Armour = (int[])save.Armour.Clone(), Chassis = (int[])save.Chassis.Clone(), Ninth = save.Ninth,
        };
        for (int k = 0; k < 3; k++)
        {
            var s = save.Equipped[11 + k];
            int idx = Array.FindIndex(PartsDatabase.SpecialNames, n => n.Equals(s, StringComparison.OrdinalIgnoreCase));
            car.Specials[k] = idx >= 0 ? idx + 1 : 0;
        }
        foreach (var h in hps) car.Weapons.Add(((int)h.Id, "null"));

        // the slots of this body as I26 lays them out; the parts are the save's, not the stock ones
        var inv = TripInventory.FromCar(car, db, vfs);
        inv.Nodes.Clear();
        inv.Repairs.Clear();

        var nodes = new PartNode?[save.Inventory.Count];
        int weapons = 0;
        for (int i = 0; i < save.Inventory.Count; i++)
        {
            var p = save.Inventory[i];
            var def = db.Parts.FirstOrDefault(d => !d.IsEmpty && d.Type == p.Type && d.Stem.Equals(p.Def, StringComparison.OrdinalIgnoreCase)) ?? db.ByStem(p.Def);
            if (def is null) { notes.Add($"{p.Name} ({p.Def}) is not a part of this install: left out"); continue; }
            var state = p.State switch { 1 => PartState.Car, 3 => PartState.Repair, 4 => PartState.Field, _ => PartState.Van };
            if (p.Type is 7 or 8 && state != PartState.Field && ++weapons > WeaponListCap)
            {
                notes.Add($"{p.Name}: past the garage's {WeaponListCap} weapons, in Field Salvage as the original puts it");
                state = PartState.Field;
            }
            // the condition as written (the mission start clamps one above the part's maximum, as the original's does)
            nodes[i] = new PartNode(def, Math.Max(p.Condition, 0), state == PartState.Car ? PartState.Van : state) { Saved = state == PartState.Field ? PartState.Field : PartState.Van };
        }

        // each slot takes the mounted part its equipped name names; with none, it is empty, as the original's garage shows it
        var mounted = Enumerable.Range(0, nodes.Length).Where(i => nodes[i] is not null && save.Inventory[i].State == 1 && nodes[i]!.State != PartState.Field).ToList();
        for (int slot = 0; slot < inv.Slots.Count; slot++)
        {
            var label = LabelFor(inv.Slots, slot, save.Equipped, weaponHp);
            if (label is null || OriginalSave.IsEmpty(label)) continue;
            int list = TripInventory.ListOf(inv.Slots[slot].Type == 0xc ? 13 : inv.Slots[slot].Type);
            int hit = mounted.FirstOrDefault(i => save.Inventory[i].Name == label && TripInventory.ListOf(nodes[i]!.Part.Type) == list, -1);
            if (hit < 0) { notes.Add($"{inv.Slots[slot].Label}: {label} is named but not on the car: empty"); continue; }
            mounted.Remove(hit);
            var n = nodes[hit]!;
            n.State = PartState.Car; n.Saved = PartState.Car; n.Slot = slot;
        }
        foreach (int i in mounted) notes.Add($"{save.Inventory[i].Name}: mounted but in no slot of this car: in the van");
        foreach (var n in nodes) if (n is not null) inv.Nodes.Add(n);

        // the repair order: the queue as the original links it, then any part in repair it does not name (so it can still be taken out)
        var queued = save.QueuedInventory().Where(i => nodes[i] is { State: PartState.Repair }).ToList();
        queued.AddRange(Enumerable.Range(0, nodes.Length).Where(i => nodes[i] is { State: PartState.Repair } && !queued.Contains(i)));
        foreach (int i in queued) inv.Repairs.Add(nodes[i]!);

        inv.ToCar();
        return new ImportedSave(entry, inv, notes);
    }

    /// <summary>The equipped name a slot takes: engine, suspension, brakes, the tyres by position (a six-wheel body's middle pair the front names: the record names four), the weapons by hardpoint, the specials in order.</summary>
    private static string? LabelFor(List<CarSlot> slots, int i, string[] equipped, int[] weaponHp)
    {
        var s = slots[i];
        switch (s.Type)
        {
            case 2: return equipped[0];
            case 3: return equipped[1];
            case 4: return equipped[2];
            case 5:
                int t = Array.IndexOf(TyreLabels, s.Label);
                return t >= 0 ? equipped[3 + t] : s.Label switch { "WHL MR" => equipped[3], "WHL ML" => equipped[4], _ => null };
            case 7 or 8 when s.Hardpoint >= 0:
                int k = Array.IndexOf(weaponHp, s.Hardpoint);
                return k >= 0 ? equipped[7 + k] : null;
            case 0xc:
                return equipped[11 + slots.Take(i).Count(x => x.Type == 0xc)];
        }
        return null;
    }
}
