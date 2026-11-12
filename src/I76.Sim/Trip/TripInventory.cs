using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Weapons;

namespace I76.Sim.Trip;

/// <summary>Where a part node is: in the car, in the van, in the repair order, or still in the field.</summary>
public enum PartState { Car = 1, Van = 2, Repair = 3, Field = 4 }

/// <summary>One part the trip owns (the 0x20-byte node): the part, its health, where it is, the car slot it fills, and the state it had before going into repair.</summary>
public sealed class PartNode(PartDef part, int health, PartState state, int slot = -1)
{
    public PartDef Part { get; set; } = part;
    public int Health { get; set; } = health;
    public PartState State { get; set; } = state;
    public int Slot { get; set; } = slot;
    public PartState Saved { get; set; } = state;
    /// <summary>A tyre written into the car's file as its own wheel file, not renamed to the car's wheel family (the 3D garage's wheel picks: any car's wheels).</summary>
    public bool Exact { get; set; }
    public int Severity => PartsDatabase.Severity(Health, Part.MaxHp);
    public int RepairCost => PartsDatabase.RepairCost[System.Math.Clamp(Part.Type, 0, 14)] * Severity;
}

/// <summary>A car slot (the shell vehicle record's slots): its type, label, the hardpoint and mount style for a weapon slot.</summary>
public sealed record CarSlot(int Type, string Label, int Hardpoint = -1, int MountStyle = 0, int Facing = 0);

/// <summary>
/// The trip's car and everything it owns between missions: the car's
/// slots (ENGINE, SUSP, BRAKE, one tyre slot per wheel, one per WEPN mount
/// typed by its hardpoint's style, one per special), the part nodes, the
/// repair order and its 32-point budget.
/// </summary>
public sealed class TripInventory
{
    public const int RepairBudget = 32;
    public TripCar Car { get; private set; } = new();
    public List<CarSlot> Slots { get; } = [];
    public List<PartNode> Nodes { get; } = [];
    /// <summary>The repair order, in the order parts were added.</summary>
    public List<PartNode> Repairs { get; } = [];
    public string MakeModel { get; private set; } = "";
    /// <summary>The body's own mass (from the VDFC): 1320 for the Piranha.</summary>
    public float VehicleMass { get; private set; }

    /// <summary>The install, to ask whether a rewritten wheel file exists (the armadillo's wheel comes in one size only).</summary>
    private AssetVfs? _wheelFiles;

    private static readonly string[] WheelLabels = ["WHL FR", "WHL FL", "WHL MR", "WHL ML", "WHL BR", "WHL BL"];

    /// <summary>
    /// A car loaded as callback [5] does, its parts in their
    /// slots at full health: the slots in order ENGINE, SUSP, BRAKE, the tyres
    /// whose pair has a .wdf, one per hardpoint of the body in its order (<see cref="TripCar.Mounts"/>: empty ones
    /// included; style 4 dropper, else weapon), then the three special rows.
    /// </summary>
    public static TripInventory FromCar(TripCar car, PartsDatabase db, AssetVfs vfs)
    {
        var inv = new TripInventory { Car = car, _wheelFiles = vfs };
        var hps = vfs.TryRead(car.Vdf) is { } vd ? Hardpoint.Read(new Bwd2File(vd)) : [];
        if (vfs.TryRead(car.Vdf) is { } vd2 && new Bwd2File(vd2).First("VDFC") is { } c)
        {
            inv.MakeModel = I76.Formats.Bin.CStr(vd2, c.PayloadOffset, 20);
            inv.VehicleMass = I76.Formats.Bin.F32(vd2, c.PayloadOffset + 0x30);
        }
        PartDef Component(int type, int ordinal) => db.Parts.FirstOrDefault(p => p.Type == type && !p.IsEmpty && p.Ordinal == ordinal) ?? db.Empty(type);
        inv.Fit(new CarSlot(2, "ENGINE"), Component(2, car.Engine));
        inv.Fit(new CarSlot(3, "SUSP"), Component(3, car.Suspension));
        inv.Fit(new CarSlot(4, "BRAKE"), Component(4, car.Brakes));
        for (int k = 0; k < 6; k++)
        {
            var wdf = car.Wdf[k / 2];
            if (wdf.Length == 0 || wdf.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            inv.Fit(new CarSlot(5, WheelLabels[k]), db.ByStem(wdf) ?? db.Empty(5));
        }
        foreach (var (hp, gdf) in car.Mounts(hps))
        {
            var slot = ChassisRules.WeaponSlot(hp, hps.FirstOrDefault(x => x.Id == hp));
            inv.Fit(slot, db.ByStem(gdf) ?? db.Empty(slot.Type));
        }
        // three special rows always (the garage's placeholders where the car has none)
        for (int k = 0; k < 3; k++)
        {
            int s = car.Specials[k];
            if (s > 0 && s <= PartsDatabase.SpecialNames.Length) inv.Fit(new CarSlot(0xc, PartsDatabase.SpecialNames[s - 1]), db.ByStem($"spc{s:00}") ?? db.Empty(13));
            else inv.Fit(new CarSlot(0xc, "SPECIAL"), db.Empty(13));
        }
        return inv;
    }

    private void Fit(CarSlot slot, PartDef part)
    {
        Slots.Add(slot);
        if (!part.IsEmpty) Nodes.Add(new PartNode(part, part.MaxHp, PartState.Car, Slots.Count - 1));
    }

    public PartNode? InSlot(int slot) => Nodes.FirstOrDefault(n => n.State == PartState.Car && n.Slot == slot);

    /// <summary>A car node that fills one of the car's slots (a stripped dropper in an old save is marked in the car with none).</summary>
    private bool Fitted(PartNode n) => n.State == PartState.Car && n.Slot >= 0 && n.Slot < Slots.Count;

    /// <summary>
    /// The 2D garage's strip (a click left of a weapon, dropper or special row's arrow): the part out of its slot and into
    /// the van. The original's form writes the van state for weapons and specials but not for a dropper, which stays
    /// marked "(C)" in no slot; here it goes to the van as the others do (docs/deviations.md).
    /// </summary>
    public void Strip(PartNode n)
    {
        if (n.State != PartState.Car) return;
        n.State = PartState.Van;
        n.Slot = -1;
    }

    /// <summary>The van's parts by list (the six inventory lists): engines, suspensions, brakes, tyres, weapons, specials.</summary>
    public static int ListOf(int type) => type switch { 2 => 0, 3 => 1, 1 or 4 => 2, 5 => 3, >= 6 and <= 11 => 4, 12 or 13 => 5, _ => -1 };

    public IEnumerable<PartNode> In(PartState state, int list) => Nodes.Where(n => n.State == state && ListOf(n.Part.Type) == list);

    /// <summary>The damage the last mission left (CarState) on the car's nodes: engine, brakes, suspension, tyres in slot order, weapons by hardpoint.</summary>
    public void ApplyState(CarState s)
    {
        foreach (var n in Nodes.Where(Fitted))
        {
            var slot = Slots[n.Slot];
            switch (slot.Type)
            {
                case 2: n.Health = s.EngineHp; break;
                case 4: n.Health = s.BrakeHp; break;
                case 3: n.Health = s.SuspensionHp; break;
                case 5: n.Health = s.Wheels[Array.IndexOf(WheelLabels, slot.Label) is var k and >= 0 ? k : 0]; break;
                case 7 or 8 when s.Weapons.FirstOrDefault(w => w.Hardpoint == slot.Hardpoint) is { Hardpoint: >= 0 } w: n.Health = w.Hp; break;
            }
            n.Health = System.Math.Clamp(n.Health, 0, System.Math.Max(n.Part.MaxHp, 0));
        }
    }

    /// <summary>
    /// The field salvage as state-4 nodes: a random-condition
    /// item is at full health on r &lt; 5, three quarters on r &lt; 30, a quarter on
    /// r &lt; 50, half otherwise (r = rand() % 100); the rest at their health.
    /// Items the database has no part for are left out.
    /// </summary>
    public void AddSalvage(IEnumerable<SalvageItem> items, PartsDatabase db, Func<int> rand)
    {
        foreach (var it in items)
        {
            if (db.ByStem(it.Name) is not { } part) continue;
            int hp = it.Health;
            if (it.Health == -1 && it.RandomCondition)
            {
                int r = rand() % 100;
                hp = r < 5 ? part.MaxHp : r < 30 ? part.MaxHp * 3 / 4 : r < 50 ? part.MaxHp / 4 : part.MaxHp / 2;
            }
            Nodes.Add(new PartNode(part, hp, PartState.Field));
        }
    }

    /// <summary>
    /// The shell's entry after a won trip mission (the original front end's main loop, the router's entry): the damage the mission left on the car,
    /// the repairs of the last order that fit, whatever was left in the field gone, then the new field
    /// salvage (none when the mission wrote no state, as T12).
    /// </summary>
    public void MissionWon(CarState? state, IEnumerable<SalvageItem>? salvage, PartsDatabase db, Func<int> rand)
    {
        if (state is not null) ApplyState(state);
        FinishRepairs();
        LeaveField();
        if (salvage is not null) AddSalvage(salvage, db, rand);
    }

    /// <summary>
    /// After the dream (T04 won, salvage management on): the shell reloads trip4.spc, the car and inventory as they left the
    /// garage after T03, so the dream's damage never happened. That reader drops the field salvage
    /// and turns parts in repair (3) into van parts (2) at full health (the reader sets health to the part's maximum, so the
    /// order made before T04 is carried out); the order itself is not kept.
    /// </summary>
    public void DreamOver()
    {
        Nodes.RemoveAll(n => n.State == PartState.Field);
        foreach (var n in Repairs) { n.Health = n.Part.MaxHp; n.State = PartState.Van; n.Slot = -1; }
        Repairs.Clear();
    }

    /// <summary>Leaving the field: whatever is still there is gone ("anything still in 'Field Salvage' gotta be left behind").</summary>
    public void LeaveField() => Nodes.RemoveAll(n => n.State == PartState.Field);

    /// <summary>
    /// A pick: the slot's old part to the van (a catalogue pick, which has no inventory, drops it), the picked
    /// part into the slot and out of the repair order. Every node is its own part: two tyres of one size are two nodes, so
    /// a second pick never touches the first. Only <paramref name="sameName"/> slots (the specials, which "match by
    /// name") lose a part of the picked one's name; a part that already sits in another slot simply moves.
    /// </summary>
    public void Install(int slot, PartNode picked, bool catalogue = false, IEnumerable<int>? sameName = null)
    {
        void Out(PartNode n) { if (catalogue) Nodes.Remove(n); else { n.State = PartState.Van; n.Slot = -1; } }
        if (InSlot(slot) is { } old && old != picked) Out(old);
        foreach (int s in sameName ?? [])
            if (s != slot && InSlot(s) is { } o && o != picked && o.Part.Id == picked.Part.Id) Out(o);
        if (picked.State == PartState.Repair) Unqueue(picked);
        if (catalogue && !Nodes.Contains(picked)) Nodes.Add(picked);
        picked.State = PartState.Car;
        picked.Slot = slot;
    }

    /// <summary>
    /// The Nitro Pack's melee tyre pick: the picked tyre goes into its slot and into the other wheel of
    /// the axle (<paramref name="mate"/>, -1 when the car has none) unless that one already holds a tyre of the same name.
    /// The base game's picker sets the one slot.
    /// </summary>
    public void InstallAxle(int slot, int mate, PartNode picked)
    {
        Install(slot, picked, catalogue: true);
        if (mate < 0 || mate == slot) return;
        if (InSlot(mate) is { } other && other.Part.Id == picked.Part.Id) return;
        Install(mate, new PartNode(picked.Part, picked.Health, PartState.Van), catalogue: true);
    }

    /// <summary>
    /// YOU ARE UNCONSCIOUS, on entry with salvage management on (the same code in the GOG and Nitro shells
    /// in the 1997 retail one): every part in the car back to full health, every part in the
    /// repair order into the van at full health, and the car's engine node given the 595ci V-10 -- the first engine whose
    /// code has '4' as its fifth character (eng04). The old engine is not put in the van: its node now holds the V-10. The
    /// shell also writes the V-10 into the vehicle record's engine slot, so a car with no engine in it gets one too.
    /// Doing it twice changes nothing.
    /// </summary>
    public void WakeUp(PartsDatabase db)
    {
        var v10 = db.Parts.FirstOrDefault(p => p.Type == 2 && p.Code.Length > 4 && p.Code[4] == '4');
        foreach (var n in Nodes)
        {
            // The user.rpr writer sets each part still in the van to its maximum as it writes the van lists
            if (n.State is PartState.Car or PartState.Van) n.Health = n.Part.MaxHp;
            else if (n.State == PartState.Repair) { n.Health = n.Part.MaxHp; n.State = PartState.Van; n.Slot = -1; }
        }
        Repairs.Clear();
        if (v10 is null) return;
        var engine = Nodes.FirstOrDefault(n => n.State == PartState.Car && n.Part.Type == 2);
        if (engine is not null) engine.Part = v10;   // health stays the old part's max: every engine has 300
        else if (Slots.FindIndex(sl => sl.Type == 2) is var slot and >= 0) Nodes.Add(new PartNode(v10, v10.MaxHp, PartState.Car, slot));
    }

    /// <summary>The cost of the repair order and how many items fit the budget (user.rpr's k).</summary>
    public (int Cost, int Fit) RepairTotal()
    {
        int cost = 0, fit = 0;
        foreach (var n in Repairs)
        {
            cost += n.RepairCost;
            if (cost <= RepairBudget) fit++;
        }
        return (cost, fit);
    }

    /// <summary>A part into the repair order if it is damaged and fits the budget; false if it would not fit ("too much").</summary>
    public bool QueueRepair(PartNode n)
    {
        if (n.Health >= n.Part.MaxHp || n.State == PartState.Repair) return true;
        if (RepairTotal().Cost + n.RepairCost > RepairBudget) return false;
        n.Saved = n.State;
        n.State = PartState.Repair;
        Repairs.Add(n);
        return true;
    }

    public void Unqueue(PartNode n)
    {
        if (!Repairs.Remove(n)) return;
        n.State = n.Saved;
    }

    /// <summary>
    /// The next shell entry after a won mission: the items of
    /// the order that fit the budget come back at full health, in the van;
    /// the rest wait for the next order.
    /// </summary>
    public void FinishRepairs()
    {
        var (_, fit) = RepairTotal();
        for (int i = 0; i < Repairs.Count; i++)
        {
            var n = Repairs[i];
            if (i < fit) { n.Health = n.Part.MaxHp; n.State = PartState.Van; n.Slot = -1; }
            else n.State = n.Saved;
        }
        Repairs.Clear();
    }

    /// <summary>
    /// the total weight in lb: the parts in the car, a tenth of the
    /// eight zone armour and chassis values (not the ninth, the unassigned pool: the code sums
    /// the eight zone values only, so moving armour into the pool lightens the car), and 2.2046 × the vehicle's own mass
    /// (3921 for the stock Piranha, as measured).
    /// </summary>
    public int Weight() =>
        (int)(Nodes.Where(n => n.State == PartState.Car && n.Slot >= 0).Sum(n => n.Part.Weight)
              + 0.1f * (Car.Armour.Sum() + Car.Chassis.Sum())
              + 2.2046f * VehicleMass);

    /// <summary>
    /// DONE's checks, in order, as the bank 0x24 notice each
    /// failure shows (21 no engine, 22 no suspension, 23 no brakes, 24 fewer
    /// than four tyres, 27 axles not matched, 26 more than one turret, 28 a
    /// turret off the roof), or null when the car may go.
    /// </summary>
    /// <param name="unlimTurrets">The port's Unlim. Turrets (Play Options; docs/deviations.md): any number of turrets, each still on a roof mount (notice 28 stays, 26 is skipped).</param>
    public int? Validate(bool unlimTurrets = false)
    {
        bool Has(int type) => Slots.Select((s, i) => (s, i)).Any(x => x.s.Type == type && InSlot(x.i) is not null);
        if (!Has(2)) return 21;
        if (!Has(3)) return 22;
        if (!Has(4)) return 23;
        var tyres = Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => InSlot(x.i)).ToList();
        // a body with no wheel slots (the hidden helicopters, the balloon, the UFO: unreachable in the original's garage) has no tyres to ask for
        bool wheelless = tyres.Count == 0 && (Car.Vdf.StartsWith("vu", StringComparison.OrdinalIgnoreCase) || Car.Vdf.Equals("vxufo.vdf", StringComparison.OrdinalIgnoreCase));
        if (!wheelless)
        {
            if (tyres.Count < 4 || tyres.Take(4).Any(t => t is null)) return 24;
            // a pair matches when its tyres are the same size and style as the garage names them: the salvage's plain
            // wauto_0x and the car's own wauto_1x are the same "13in Stock" to the player, whatever their files
            if (!tyres[0]!.Part.Id.Equals(tyres[1]!.Part.Id, StringComparison.OrdinalIgnoreCase)
                || !tyres[2]!.Part.Id.Equals(tyres[3]!.Part.Id, StringComparison.OrdinalIgnoreCase)) return 27;
        }
        // the turret checks hold for the helicopters too: a turret on a Huey's side gun passed the 2D form's DONE while the 3D garage never offers one (#131); only the one-turret limit is not asked of them (the Huey has two)
        var turrets = Nodes.Where(n => Fitted(n) && n.Part.IsTurret).ToList();
        if (turrets.Count > 1 && !unlimTurrets && !wheelless) return 26;   // the Huey's own two stay
        if (turrets.Any(t => !ChassisRules.TurretMount(Slots[t.Slot]))) return 28;
        return null;
    }

    /// <summary>The car as the shell writes it: components' ordinals, the front tyre's stem front, the rear's rear, each weapon on its slot's hardpoint, the specials' types.</summary>
    public TripCar ToCar()
    {
        // A tyre part names a size ("13in Stock" = ...a.wdf), and the garage rewrites the first seven characters of its
        // stem to the car's own wheel stem (a strncpy of the vehicle wheel stem, 7 characters, 13 characters when the
        // part is wm113_1...), so the wheel drawn is the car's model in the size picked. The car's own axle names the stem
        // (a bus keeps wbtck behind and wftck in front); a salvaged wauto_0b on a wauto_9 car is wauto_9b.
        string Own(int axle, PartNode? tyre)
        {
            if (tyre is null) return "null";
            var stem = tyre.Part.Stem;
            // another car's wheels picked on purpose (the 3D garage), or the file's own wheel left as it is: written as they are
            if (tyre.Exact || stem.Equals(Car.Wdf[axle], StringComparison.OrdinalIgnoreCase)) return stem;
            var own = Car.Wdf[axle];
            if (own.Length == 0 || own.Equals("null", StringComparison.OrdinalIgnoreCase)) own = Car.Wdf[axle == 1 ? 2 : axle];
            if (own.Length < 7 || own.Equals("null", StringComparison.OrdinalIgnoreCase) || stem.Length < 7) return stem;
            var made = stem.StartsWith("wm113_1", StringComparison.OrdinalIgnoreCase) ? own : own[..7] + stem[7..];
            // a car whose wheels exist in one size only (wdilo_1a) keeps them: the original would name a file that is not there
            return _wheelFiles is { } v && !v.Exists(made) ? own : made;
        }
        int Ordinal(int type) => Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == type).Select(x => InSlot(x.i)?.Part.Ordinal).FirstOrDefault() ?? 0;
        var tyres = Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => InSlot(x.i)).ToList();
        var car = new TripCar
        {
            Variant = Car.Variant, Vdf = Car.Vdf, Vtf = Car.Vtf, Ninth = Car.Ninth,
            Armour = (int[])Car.Armour.Clone(), Chassis = (int[])Car.Chassis.Clone(),
            Engine = Ordinal(2), Suspension = Ordinal(3), Brakes = Ordinal(4),
            Wdf = [Own(0, tyres.ElementAtOrDefault(0)), tyres.Count > 4 ? Own(1, tyres[2]) : "null", Own(2, tyres.ElementAtOrDefault(tyres.Count > 4 ? 4 : 2))],
        };
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Hardpoint >= 0) car.Weapons.Add((Slots[i].Hardpoint, InSlot(i)?.Part.Stem ?? "null"));
        var specials = Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 0xc).Select(x => InSlot(x.i)).ToList();
        for (int k = 0; k < 3; k++) car.Specials[k] = specials.ElementAtOrDefault(k) is { } sp ? sp.Part.Ordinal + 1 : 0;
        Car = car;
        return car;
    }

    /// <summary>The state the next mission starts in (the shell's vsf): its parts' health, guns refilled (−1).</summary>
    public CarState State()
    {
        int Hp(int type) => Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == type).Select(x => InSlot(x.i)?.Health).FirstOrDefault() ?? 0;
        var wheels = new int[6];
        for (int k = 0; k < 6; k++)
            wheels[k] = Slots.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Type == 5 && x.s.Label == WheelLabels[k]) is var x && x.s is not null ? InSlot(x.i)?.Health ?? 0 : -1;
        var weapons = Slots.Select((s, i) => (s, i)).Where(x => x.s.Hardpoint >= 0 && InSlot(x.i) is not null).Select(x => (x.s.Hardpoint, InSlot(x.i)!.Health, -1)).ToList();
        return new CarState(Hp(2), Hp(4), Hp(3), wheels, weapons);
    }

    // ---- keeping it between sessions ----

    private sealed record SavedNode(string Stem, int Type, string Id, int Health, int State, int Slot, int Saved);
    private sealed record Saved(TripCarDto Car, List<SavedNode> Nodes, List<int> Repairs, List<CarSlot>? Slots = null, string? MakeModel = null, float VehicleMass = 0);
    private sealed record TripCarDto(string Variant, string Vdf, string Vtf, int Engine, int Suspension, int Brakes, string[] Wdf, int[] Armour, int[] Chassis, int Ninth, int[] Specials, List<int> Hardpoints, List<string> Gdfs);

    /// <summary>The car and every node as JSON (what the bookmark .cmp and reconfig.spc keep).</summary>
    public string Save()
    {
        var c = Car;
        var dto = new TripCarDto(c.Variant, c.Vdf, c.Vtf, c.Engine, c.Suspension, c.Brakes, c.Wdf, c.Armour, c.Chassis, c.Ninth, c.Specials,
            c.Weapons.Select(w => w.Hardpoint).ToList(), c.Weapons.Select(w => w.Gdf).ToList());
        var nodes = Nodes.Select(n => new SavedNode(n.Part.Stem, n.Part.Type, n.Part.Id, n.Health, (int)n.State, n.Slot, (int)n.Saved)).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new Saved(dto, nodes, Repairs.Select(r => Nodes.IndexOf(r)).ToList(), Slots, MakeModel, VehicleMass));
    }

    public static TripInventory? Load(string json, PartsDatabase db, AssetVfs vfs)
    {
        Saved? s;
        try { s = System.Text.Json.JsonSerializer.Deserialize<Saved>(json); }
        catch (System.Text.Json.JsonException) { return null; }
        if (s is null) return null;
        var d = s.Car;
        var car = new TripCar
        {
            Variant = d.Variant, Vdf = d.Vdf, Vtf = d.Vtf, Engine = d.Engine, Suspension = d.Suspension, Brakes = d.Brakes,
            Wdf = d.Wdf, Armour = d.Armour, Chassis = d.Chassis, Ninth = d.Ninth, Specials = d.Specials,
            Weapons = d.Hardpoints.Zip(d.Gdfs).ToList(),
        };
        // the slots as they were kept (the car's own file may have changed since, and its empty mounts must stay slots);
        // a file from before they were kept rebuilds them from the car
        var inv = s.Slots is { Count: > 0 } ? new TripInventory { Car = car, MakeModel = s.MakeModel ?? "", VehicleMass = s.VehicleMass, _wheelFiles = vfs } : FromCar(car, db, vfs);
        if (s.Slots is { Count: > 0 }) inv.Slots.AddRange(s.Slots);
        inv.Nodes.Clear();
        foreach (var n in s.Nodes)
        {
            var part = db.Parts.FirstOrDefault(p => !p.IsEmpty && p.Type == n.Type && p.Stem.Equals(n.Stem, StringComparison.OrdinalIgnoreCase))
                       ?? db.ByStem(n.Stem);
            inv.Nodes.Add(part is null ? null! : new PartNode(part, n.Health, (PartState)n.State, n.Slot) { Saved = (PartState)n.Saved });
        }
        var keep = inv.Nodes.ToList();
        inv.Nodes.RemoveAll(n => n is null);
        // a dropper stripped before Strip sent it to the van was kept marked in the car in no slot: it is in the van
        foreach (var n in inv.Nodes)
        {
            if (n.Slot >= 0 && n.Slot < inv.Slots.Count) continue;
            if (n.State == PartState.Car) { n.State = PartState.Van; n.Slot = -1; }
            else if (n.State == PartState.Repair && n.Saved == PartState.Car) n.Saved = PartState.Van;   // out of the order, to the van
        }
        foreach (int i in s.Repairs) if (i >= 0 && i < keep.Count && keep[i] is { } r) inv.Repairs.Add(r);
        return inv;
    }
}
