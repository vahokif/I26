using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// The rules of the melee CONFIGURE CHASSIS form that do not depend on how it is drawn: the
/// pickers' catalogues, the DEFENSE buttons, the Phaedra Rattler's name secrets and what DONE does. The 2D form
/// (<c>GarageScreen</c>) and the 3D garage both use them, so a variant made in either is the same file.
/// </summary>
public static class ChassisRules
{
    /// <summary>
    /// The melee pickers' lists (the catalogue sorted by type; type is the form's:
    /// 2 engines, 3 suspensions, 4 brakes (types 1 and 4), 5 tyres, 7 weapons, 8 droppers, 0xc specials): bare names, no "Empty".
    /// Weapons are without the shell's banned ones (the base game's list or Nitro's shorter one, half of it open to a variant
    /// named "natk…"), sorted by class and number; Hidden Weapons &amp; Cars adds the complete hidden weapons.
    /// </summary>
    public static List<PartNode> Catalogue(PartsDatabase db, int type, bool nitro, bool hidden, string variantName)
    {
        bool natk = variantName.StartsWith("natk", StringComparison.OrdinalIgnoreCase);
        bool Shown(PartDef p) => HiddenContent.Offered(p.Stem, nitro, natk, hidden);
        var l = db.Parts.Where(p => !p.IsEmpty && type switch
        {
            2 or 3 or 5 => p.Type == type,
            4 => p.Type is 1 or 4,
            7 => p.Type is 6 or 7 or 9 or 10 or 11 && Shown(p),
            8 => p.Type == 8 && Shown(p),
            0xc => p.Type is 12 or 13,
            _ => false,
        }).Select(p => new PartNode(p, p.MaxHp, PartState.Van)).ToList();
        return type == 7 ? [.. l.OrderBy(n => WeaponClass(n.Part)).ThenBy(n => CodeNumber(n.Part))] : l;
    }

    /// <summary>
    /// The part types a slot kind takes (the form's slot types: 2 engine, 3 suspension, 4 brakes (types 1 and 4), 5 tyre, 7 weapon (6, 7, 9, 10,
    /// 11), 8 dropper, 0xc special (12, 13)). The original's pickers are the catalogue by this alone (<see cref="Catalogue"/>).
    /// </summary>
    public static bool TypeFits(int slotType, int partType) => slotType switch
    {
        2 or 3 or 5 or 8 => partType == slotType,
        4 => partType is 1 or 4,
        7 => partType is 6 or 7 or 9 or 10 or 11,
        0xc => partType is 12 or 13,
        _ => false,
    };

    /// <summary>
    /// The one hardpoint rule of the original's garage (DONE's check, notice 28; the strict network check's "off a turret mount"):
    /// a turret (a gun's t*.gdf twin, tier 100 and up) goes on a roof turret mount (hardpoint style 1, "P") and nowhere else. Guns of every
    /// other class go on any weapon hardpoint (styles 1, 2 "S", 5 "I"), droppers only on a dropper mount (style 4; the slot's type 8, which
    /// <see cref="TypeFits"/> keeps apart); what a slot's file says about its style is all that decides it.
    /// </summary>
    public static bool Accepts(CarSlot slot, PartDef part) =>
        TypeFits(slot.Type, part.Type) && (slot.Type != 7 || !part.IsTurret || TurretMount(slot));

    /// <summary>A WEPN mount's garage slot: a dropper slot (8) on a dropper mount (style 4), else a weapon slot (7); a mount the body lacks counts as style 1.</summary>
    public static CarSlot WeaponSlot(int hardpoint, Hardpoint? h)
    {
        uint style = h?.Style ?? 1;
        return new CarSlot(style == 4 ? 8 : 7, $"HP {hardpoint}", hardpoint, (int)style, (int)(h?.Facing ?? 1));
    }

    /// <summary>What the garage offers for a weapon slot: the catalogue of its kind (<see cref="Catalogue"/>) that the slot accepts.</summary>
    public static List<PartDef> WeaponsFor(PartsDatabase db, CarSlot slot, bool nitro, bool hidden) =>
        [.. Catalogue(db, slot.Type, nitro, hidden, "").Select(n => n.Part).Where(p => Accepts(slot, p))];

    /// <summary>A roof turret mount: the style the turret check names.</summary>
    public static bool TurretMount(CarSlot slot) => slot.MountStyle == 1;

    /// <summary>The digits of a component code (atoi): eng01 → 1; 0 for a placeholder.</summary>
    public static int CodeNumber(PartDef p) => p.Code.Length > 3 && int.TryParse(new string(p.Code.Skip(3).TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0;

    /// <summary>The inventory weapons' order: slg, spp, flm, mor, then the rest.</summary>
    public static int WeaponClass(PartDef p) => p.Code.Length < 3 ? 4 : p.Code[..3] switch { "slg" => 0, "spp" => 1, "flm" => 2, "mor" => 3, _ => 4 };

    /// <summary>The DEFENSE rows in the form's order (FRONT, RIGHT, LEFT, REAR) as the car's zones keep them (front, left, right, rear).</summary>
    public static readonly int[] Zone = [0, 2, 1, 3];

    /// <summary>One DEFENSE button: ±10 units between a zone's armour (or chassis) and the unassigned pool, a zone at least trunc(total/32). True when it moved anything.</summary>
    public static bool Adjust(TripCar c, int zone, bool chassis, bool more)
    {
        int total = c.Armour.Sum() + c.Chassis.Sum() + c.Ninth, min = total / 32;
        var z = chassis ? c.Chassis : c.Armour;
        if (more) { if (c.Ninth < 10) return false; z[zone] += 10; c.Ninth -= 10; }
        else { if (z[zone] - 10 < min) return false; z[zone] -= 10; c.Ninth += 10; }
        return true;
    }

    /// <summary>BALANCE: the whole total shared by the eight values, the remainder on the front armour, the pool emptied.</summary>
    public static void Balance(TripCar c)
    {
        int total = c.Armour.Sum() + c.Chassis.Sum() + c.Ninth;
        for (int z = 0; z < 4; z++) { c.Armour[z] = total / 8; c.Chassis[z] = total / 8; }
        c.Armour[0] += total % 8;
        c.Ninth = 0;
    }

    /// <summary>
    /// The Phaedra Rattler's secrets, applied as the variant is saved: named
    /// "natk…" or "knat…" it becomes the tank (vstank.vdf, tank1.vtf, the tank
    /// tracks front and rear; all eight armour and chassis values 6000 for
    /// "natk", 1500 for "knat"); "retpocileh…" the helicopter (vuhuey.vdf,
    /// huey2.vtf, front armour and chassis 3000); on Nitro also "hotair…" the
    /// balloon and "thetruthishere…" the UFO, the same way.
    /// </summary>
    public static void Secrets(TripCar car, string makeModel, bool nitro)
    {
        if (!makeModel.Equals("phaedra rattler", StringComparison.OrdinalIgnoreCase)) return;
        var v = car.Variant;
        bool natk = v.StartsWith("natk", StringComparison.OrdinalIgnoreCase);
        if (natk || v.StartsWith("knat", StringComparison.OrdinalIgnoreCase))
        {
            (car.Vdf, car.Vtf, car.Wdf[0], car.Wdf[2]) = ("vstank.vdf", "tank1.vtf", "wctnk_1a.wdf", "wdtnk_1a.wdf");
            for (int z = 0; z < 4; z++) car.Armour[z] = car.Chassis[z] = natk ? 6000 : 1500;
        }
        else if (v.StartsWith("retpocileh", StringComparison.OrdinalIgnoreCase)) Body("vuhuey.vdf", "huey2.vtf");
        // Nitro's own two: the balloon and the UFO, front armour and chassis 3000 as the Huey
        else if (nitro && v.StartsWith("hotair", StringComparison.OrdinalIgnoreCase)) Body("vuballon.vdf", "balloon1.vtf");
        else if (nitro && v.StartsWith("thetruthishere", StringComparison.OrdinalIgnoreCase)) Body("vxufo.vdf", "ufo1.vtf");

        void Body(string vdf, string vtf)
        {
            (car.Vdf, car.Vtf) = (vdf, vtf);
            car.Armour[0] = car.Chassis[0] = 3000;
        }
    }

    /// <summary>What DONE does (melee), as the form decides it before it asks anything.</summary>
    public enum DoneKind
    {
        /// <summary>The checks failed: the notice <see cref="DonePlan.Notice"/> (bank 0x24).</summary>
        Invalid,
        /// <summary>Nothing changed: straight back with the variant as it was.</summary>
        Unchanged,
        /// <summary>A stock variant, or a stock variant's name: notice 30 (a new name is asked for).</summary>
        NeedsName,
        /// <summary>"Overwrite the current variant?": yes saves as <see cref="DonePlan.Save"/>.</summary>
        AskOverwrite,
        /// <summary>"Wanna save the new variant?": yes saves as <see cref="DonePlan.Save"/>; no restores the name and paint (base) or stays (Nitro).</summary>
        AskSaveNew,
    }

    public sealed record DonePlan(DoneKind Kind, int Notice = 0, int Save = 0);

    /// <summary>
    /// DONE on the chassis form: the checks, then — unchanged, straight back; changed but not renamed, a stock variant asks
    /// for a new name (notice 30), a saved one "Overwrite the current variant?"; renamed, "Wanna save the new variant?" or,
    /// for a saved variant's name, the overwrite question (a stock variant's name: notice 30, our deviation).
    /// <paramref name="variant"/> is the 1-based variant on display, <paramref name="variants"/> the model's names and <paramref name="stock"/> how many are stock.
    /// </summary>
    public static DonePlan PlanDone(TripInventory inv, bool changed, bool renamed, int variant, IReadOnlyList<string> variants, int stock, bool unlimTurrets = false)
    {
        if (inv.Validate(unlimTurrets) is { } box) return new(DoneKind.Invalid, Notice: box);
        if (!renamed)
        {
            if (!changed) return new(DoneKind.Unchanged);
            if (variant <= stock) return new(DoneKind.NeedsName, Notice: 30);
            return new(DoneKind.AskOverwrite, Save: variant);
        }
        int idx = -1;
        for (int i = 0; i < variants.Count; i++) if (variants[i].Equals(inv.Car.Variant, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx < 0) return new(DoneKind.AskSaveNew, Save: variants.Count + 1);
        if (idx < stock) return new(DoneKind.NeedsName, Notice: 30);
        return new(DoneKind.AskOverwrite, Save: idx + 1);
    }

    /// <summary>The car as DONE writes it into the variant file: the shell's car (<see cref="TripInventory.ToCar"/>) with the Rattler's secrets applied.</summary>
    public static byte[] VariantFile(TripInventory inv, string makeModel, bool nitro, out TripCar car)
    {
        car = inv.ToCar();
        Secrets(car, makeModel, nitro);
        return car.ToVcf();
    }
}

/// <summary>Which end of the car a wheel pair is on: the .vcf names a .wdf for each (front, middle, rear).</summary>
public enum Axle { Front = 0, Middle = 1, Rear = 2 }

/// <summary>
/// The melee CONFIGURE CHASSIS form's working state without its drawing: the car in a <see cref="TripInventory"/> (the same
/// object the 2D form edits) and what a pick does to it. The 3D garage drives this; the 2D form does the same
/// operations on its own copy through <see cref="ChassisRules"/> and the inventory, so both write the same .vcf.
/// </summary>
public sealed class ChassisModel
{
    public TripInventory Inv { get; }
    private readonly PartsDatabase _db;
    private readonly AssetVfs _vfs;
    public bool Nitro { get; }
    public bool Hidden { get; }
    /// <summary>The variant's name and paint as the form was opened on it (CANCEL and "no" restore them).</summary>
    public string EntryName { get; }
    public string EntryVtf { get; }
    public bool Changed { get; private set; }
    public bool Renamed { get; private set; }

    /// <summary>
    /// The variant's custom paint colours (I26's respray; not part of the .vcf, which stays as the original wrote it: they go to the variant's
    /// sidecar, <see cref="I76.Formats.Imaging.CarSidecar"/>). Null is the factory paint; a finish can be set before any colour is.
    /// </summary>
    public I76.Formats.Imaging.CarPaint? Colours { get; private set; }
    public I76.Formats.Imaging.CarPaint? EntryColours { get; }

    public ChassisModel(TripCar car, PartsDatabase db, AssetVfs vfs, bool nitro, bool hidden, I76.Formats.Imaging.CarPaint? colours = null)
    {
        _db = db; _vfs = vfs; Nitro = nitro; Hidden = hidden;
        Inv = TripInventory.FromCar(car, db, vfs);
        (EntryName, EntryVtf) = (Inv.Car.Variant, Inv.Car.Vtf);
        Colours = EntryColours = colours;
    }

    /// <summary>Sets the custom colours (null: the factory paint). A change counts as an edit, so DONE saves it.</summary>
    public void SetColours(I76.Formats.Imaging.CarPaint? colours)
    {
        if (Equals(colours, Colours)) return;
        Colours = colours;
        Changed = true;
    }

    /// <summary>What the sidecar of the saved variant should hold (null: none, the sidecar goes).</summary>
    public I76.Formats.Imaging.CarPaint? ColoursToSave => Colours is { IsNone: false } c ? c : null;

    /// <summary>The car as its file would be written now (without the Rattler's secrets): the 3D garage's preview.</summary>
    public TripCar Snapshot() => TripCar.FromVcf(Inv.ToCar().ToVcf());

    public string MakeModel => Inv.MakeModel;

    // ---- the slots ----

    public int SlotOf(int type, string? label = null) => Inv.Slots.FindIndex(s => s.Type == type && (label is null || s.Label == label));

    /// <summary>A weapon, dropper or special slot as the form lists it.</summary>
    public sealed record Mount(int Slot, string Label, int Type, int Hardpoint);

    /// <summary>
    /// The weapon and dropper rows in the order the 2D form lists them (all Fwd, then all Top, then all Rear, the
    /// droppers last, numbered within each group).
    /// </summary>
    public List<Mount> Mounts()
    {
        int Group(CarSlot s) => s.MountStyle is 1 or 3 ? 1 : s.Facing == 2 ? 2 : 0;
        string[] names = ["Fwd", "Top", "Rear"];
        int[] count = new int[3];
        var o = new List<Mount>();
        foreach (var (s, i) in Inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 7).OrderBy(x => Group(x.s)))
        {
            int g = Group(s);
            o.Add(new Mount(i, $"#{++count[g]} {names[g]}", 7, s.Hardpoint));
        }
        int d = 0;
        foreach (var (s, i) in Inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 8)) o.Add(new Mount(i, $"#{++d} Dropper", 8, s.Hardpoint));
        return o;
    }

    /// <summary>The specials' slots (three at most).</summary>
    public List<Mount> SpecialSlots()
    {
        var o = new List<Mount>();
        int k = 0;
        foreach (var (s, i) in Inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 0xc).Take(3)) o.Add(new Mount(i, $"Special {++k}", 0xc, -1));
        return o;
    }

    public PartNode? Fitted(int slot) => slot >= 0 ? Inv.InSlot(slot) : null;

    public List<PartNode> Catalogue(int type) => ChassisRules.Catalogue(_db, type, Nitro, Hidden, Inv.Car.Variant);

    /// <summary>
    /// What the original's rules let a slot take: the catalogue of its kind, a turret only on a roof turret mount (<see cref="ChassisRules.Accepts"/>)
    /// and only one in the car (DONE's notice 26; the 2D form lets the pick through and DONE refuses it, the garage does not offer it).
    /// </summary>
    public List<PartNode> CatalogueFor(int slot)
    {
        var s = Inv.Slots[slot];
        bool other = !UnlimTurrets && Inv.Slots.Select((x, i) => (x, i)).Any(t => t.i != slot && Fitted(t.i) is { Part.IsTurret: true });
        return [.. Catalogue(s.Type).Where(n => ChassisRules.Accepts(s, n.Part) && !(n.Part.IsTurret && other))];
    }

    /// <summary>The catalogue entry of a part already in a slot (by file), for a picker to show it chosen.</summary>
    public bool Is(int slot, PartDef part) => Fitted(slot) is { } n && n.Part.Stem.Equals(part.Stem, StringComparison.OrdinalIgnoreCase) && n.Part.Type == part.Type && n.Part.Ordinal == part.Ordinal;

    /// <summary>A pick for a slot: the old part goes, the picked one comes in (a catalogue pick); only a special of the same name is swapped out of the others.</summary>
    public void Fit(int slot, PartDef part, IEnumerable<int>? sameName = null)
    {
        var node = new PartNode(part, part.MaxHp, PartState.Van);
        Inv.Install(slot, node, catalogue: true, sameName);
        Changed = true;
    }

    /// <summary>A weapon, dropper or special stripped from its slot (the form's click on the name).</summary>
    public void Strip(int slot)
    {
        if (Fitted(slot) is not { } n) return;
        Inv.Nodes.Remove(n);
        Changed = true;
    }

    // ---- wheels ----

    /// <summary>The tyre slots of an axle (the left and right wheel), as the car has them: none when the car has no such axle.</summary>
    public List<int> WheelSlots(Axle a)
    {
        string[] labels = a switch { Axle.Front => ["WHL FR", "WHL FL"], Axle.Middle => ["WHL MR", "WHL ML"], _ => ["WHL BR", "WHL BL"] };
        return [.. labels.Select(l => SlotOf(5, l)).Where(i => i >= 0)];
    }

    public bool HasAxle(Axle a) => WheelSlots(a).Count > 0;

    /// <summary>The .wdf the axle runs on now (the part in its first slot), or null.</summary>
    public string? WheelStem(Axle a) => WheelSlots(a) is [var s, ..] && Fitted(s) is { } n ? n.Part.Stem : null;

    /// <summary>Every wheel file of the install, both games' as the install has them, in file order.</summary>
    public List<PartDef> AllWheels() => [.. _db.Parts.Where(p => p.Type == 5 && !p.IsEmpty).OrderBy(p => p.Stem, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A wheel pair of one axle fitted from any car's wheel file: written into the .vcf as it is (<see cref="PartNode.Exact"/>), unlike the 2D
    /// form's tyre picks, which are the car's own wheel model in the size picked. The .vcf carries any .wdf; the sim takes its grip, mass and hit
    /// points from it (VehicleFactory), and its model is drawn at the car's wheel locations.
    /// </summary>
    public void FitAxle(Axle a, PartDef wheel)
    {
        foreach (int s in WheelSlots(a))
        {
            var node = new PartNode(wheel, wheel.MaxHp, PartState.Van) { Exact = true };
            Inv.Install(s, node, catalogue: true);
        }
        Changed = true;
    }

    /// <summary>A wheel file's own numbers (WDFC): grip, hit points and mass of one wheel.</summary>
    public (float Grip, int Hp, float Mass)? WheelStats(PartDef wheel)
    {
        if (_vfs.TryRead(wheel.Stem) is not { } d || new Bwd2File(d).First("WDFC") is not { } c) return null;
        int p = c.PayloadOffset;
        return (Bin.F32(d, p + 0x30), Bin.I32(d, p + 0x28), Bin.F32(d, p + 0x2c));
    }

    // ---- paint, name, defense ----

    /// <summary>Region 10: every paint scheme sharing the .vtf's first four letters.</summary>
    public List<PartDef> PaintSchemes()
    {
        var stem = Inv.Car.Vtf.Length >= 4 ? Inv.Car.Vtf[..4] : Inv.Car.Vtf;
        return [.. _db.Parts.Where(p => p.Type == 14 && p.Stem.StartsWith(stem, StringComparison.OrdinalIgnoreCase))];
    }

    public PartDef? Paint => _db.Parts.FirstOrDefault(p => p.Type == 14 && p.Stem.Equals(Inv.Car.Vtf, StringComparison.OrdinalIgnoreCase));

    public void SetPaint(string vtf) { Inv.Car.Vtf = vtf; Changed = true; }

    /// <summary>RENAME: at most 15 characters; a name other than the one it came with counts as renamed.</summary>
    public void Rename(string name)
    {
        Inv.Car.Variant = name.Length > 15 ? name[..15] : name;
        Renamed = Inv.Car.Variant != EntryName;
    }

    public bool AdjustDefense(int row, bool chassis, bool more)
    {
        // as the 2D form: a press counts as an edit even where the limit stopped it
        bool moved = ChassisRules.Adjust(Inv.Car, ChassisRules.Zone[row], chassis, more);
        Changed = true;
        return moved;
    }

    public void Balance() { ChassisRules.Balance(Inv.Car); Changed = true; }

    /// <summary>CANCEL (and "no" to saving a new variant on the base game): the name and the paint as they came.</summary>
    public void RestoreEntry() { (Inv.Car.Variant, Inv.Car.Vtf) = (EntryName, EntryVtf); Colours = EntryColours; Renamed = false; }

    public ChassisRules.DonePlan PlanDone(int variant, IReadOnlyList<string> variants, int stock) => ChassisRules.PlanDone(Inv, Changed, Renamed, variant, variants, stock, UnlimTurrets);

    /// <summary>The port's Unlim. Turrets (Play Options): the one-turret rule (notice 26) is lifted; turrets still go on roof mounts only.</summary>
    public bool UnlimTurrets { get; set; }

    /// <summary>The bytes DONE writes to the variant's file.</summary>
    public byte[] VariantFile() => ChassisRules.VariantFile(Inv, Inv.MakeModel, Nitro, out _);
}
