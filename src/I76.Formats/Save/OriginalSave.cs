namespace I76.Formats.Save;

/// <summary>
/// One row of the original's bookmark list (savegame.dir): u32 count, then count 60-byte records. The typed name is
/// written as the whole buffer (stale bytes after the NUL are normal); the reader opens "file.cmp" and drops a row
/// whose file is missing. Bytes after the last record are ignored, as the original's reader stops at the count.
/// </summary>
/// <param name="Scene">The scene the bookmark was saved in.</param>
/// <param name="State">The game state the shell was entered with: 1 after a won mission (it plays the next scene), 8 in the garage (it plays this one).</param>
/// <param name="Flags">The last dword; 0 in every game-written file seen.</param>
public sealed record OriginalSaveEntry(int Scene, string Name, string File, int State, int Flags)
{
    public const int Size = 60;

    /// <summary>The scene the LOAD board shows ("Scene N.") and the bookmark plays: one on after a win.</summary>
    public int Plays => Scene + (State == 1 ? 1 : 0);

    public static List<OriginalSaveEntry> ReadDir(byte[] d)
    {
        if (d.Length < 4) throw new InvalidDataException("savegame.dir: shorter than its count");
        int n = Bin.I32(d, 0);
        if (n < 0 || 4L + (long)Size * n > d.Length) throw new InvalidDataException($"savegame.dir: {d.Length} bytes cannot hold {n} records");
        var l = new List<OriginalSaveEntry>(n);
        for (int i = 0; i < n; i++)
        {
            int p = 4 + Size * i;
            l.Add(new OriginalSaveEntry(Bin.I32(d, p), Bin.CStr(d, p + 4, 32), Bin.CStr(d, p + 36, 16), Bin.I32(d, p + 52), Bin.I32(d, p + 56)));
        }
        return l;
    }
}

/// <summary>Where a part is in the original's inventory: on the car, in the van, queued for repair, or left in the field (Field Salvage).</summary>
public enum OriginalPartState { Mounted = 1, Van = 2, Repair = 3, Salvage = 4 }

/// <summary>
/// One 0x74-byte inventory record of a .cmp: a 0x20-byte list node (three runtime pointers, the condition, the state,
/// runtime leftovers) then the part's 0x54-byte record as the parts database holds it.
/// </summary>
/// <param name="Condition">Hit points, in the part file's units; graded against <see cref="Full"/> in thirds for the list colours.</param>
/// <param name="State">An <see cref="OriginalPartState"/>, as written.</param>
/// <param name="Type">2 engine, 3 suspension, 4 brakes, 5 tyre, 7 gun, 8 dropper, 13 special.</param>
/// <param name="Class">The component class id (eng01, slg02, whe01; empty for specials).</param>
/// <param name="Def">The definition file (gmmedium.gdf, eng02, wauto_1b.wdf, spc05): what the repair queue is matched on.</param>
/// <param name="Full">The condition at full health: the part's maximum.</param>
public sealed record OriginalPart(int Condition, int State, string Name, int Type, int Unk_0x42, string Class, string Def, int Unk_0x68, int Full, float Weight)
{
    public const int Size = 0x74;

    public static OriginalPart Read(byte[] d, int at) => new(
        Bin.I32(d, at + 0x0c), Bin.I32(d, at + 0x10), Bin.CStr(d, at + 0x20, 30), Bin.I32(d, at + 0x3e), Bin.I32(d, at + 0x42),
        Bin.CStr(d, at + 0x4e, 13), Bin.CStr(d, at + 0x5b, 13), Bin.I32(d, at + 0x68), Bin.I32(d, at + 0x6c), Bin.F32(d, at + 0x70));
}

/// <summary>One record of the garage's damage-panel registry: the slot type and its label (a weapon's names its hardpoint); X, Y place it on the panel.</summary>
public sealed record OriginalSlot(int Type, string Label, int X, int Y);

/// <summary>
/// A bookmark's body (saveNNN.cmp; reconfig.spc and trip4.spc have the same layout): the shell's 0x8c4-byte garage
/// record, the inventory (count, records) and the repair queue (count, records: copies of the queued inventory
/// records, in the order of the repair bench). The size is exactly 0x8c4 + 8 + 0x74 × (inventory + queue).
/// </summary>
public sealed class OriginalSave
{
    public const int GarageSize = 0x8c4;
    /// <summary>The equipped names, in this order: engine, suspension, brakes, tyres FR FL RR RL, four weapons in the registry's weapon order, three specials.</summary>
    public const int EquippedCount = 14;
    public const string EmptyLabel = "Empty";

    public string CarName = "", Variant = "", Ident = "";
    public List<OriginalSlot> Registry = [];
    /// <summary>What the garage shows fitted, by part name; an empty hardpoint reads "Empty".</summary>
    public string[] Equipped = new string[EquippedCount];
    /// <summary>Armour and chassis by zone in tenths of a point (910 shows as 91), in the order the shell copies them into the car file.</summary>
    public int[] Armour = new int[4], Chassis = new int[4];
    /// <summary>The dword after the eight zone values, copied into the car file with them.</summary>
    public int Ninth;
    public string Vdf = "", Vtf = "";
    /// <summary>Front, middle (empty on four wheels) and rear wheel files.</summary>
    public string[] Wdf = ["", "", ""];
    public int[] Unk_0x864 = new int[13];
    public List<OriginalPart> Inventory = [];
    public List<OriginalPart> RepairQueue = [];

    public static OriginalSave Read(byte[] d)
    {
        if (d.Length < GarageSize + 8) throw new InvalidDataException($"{d.Length} bytes is shorter than a garage record and two counts");
        int nA = Bin.I32(d, GarageSize);
        long atC = GarageSize + 4 + (long)OriginalPart.Size * nA;
        if (nA < 0 || atC + 4 > d.Length) throw new InvalidDataException($"an inventory of {nA} runs past the end ({d.Length} bytes)");
        int nC = Bin.I32(d, (int)atC);
        long want = GarageSize + 8 + (long)OriginalPart.Size * ((long)nA + nC);
        if (nC < 0 || d.Length != want) throw new InvalidDataException($"{d.Length} bytes, not the {want} an inventory of {nA} and a queue of {nC} take");
        var s = new OriginalSave
        {
            CarName = Bin.CStr(d, 0x00, 20), Variant = Bin.CStr(d, 0x14, 20), Ident = Bin.CStr(d, 0x28, 16),
            Vdf = Bin.CStr(d, 0x820, 13), Vtf = Bin.CStr(d, 0x82d, 13),
            Wdf = [Bin.CStr(d, 0x83a, 13), Bin.CStr(d, 0x847, 13), Bin.CStr(d, 0x854, 13)],
            Ninth = Bin.I32(d, 0x81c),
        };
        int reg = Bin.I32(d, 0x3c);
        for (int i = 0; i < reg && 0x40 + 32 * (i + 1) <= 0x400; i++)
        {
            int p = 0x40 + 32 * i;
            s.Registry.Add(new OriginalSlot(Bin.I32(d, p), Bin.CStr(d, p + 4, 16), Bin.I32(d, p + 20), Bin.I32(d, p + 24)));
        }
        for (int k = 0; k < EquippedCount; k++) s.Equipped[k] = Bin.CStr(d, 0x400 + 30 * k, 30);
        for (int z = 0; z < 4; z++) { s.Armour[z] = Bin.I32(d, 0x7fc + 4 * z); s.Chassis[z] = Bin.I32(d, 0x80c + 4 * z); }
        for (int k = 0; k < 13; k++) s.Unk_0x864[k] = Bin.I32(d, 0x864 + 4 * k);
        for (int i = 0; i < nA; i++) s.Inventory.Add(OriginalPart.Read(d, GarageSize + 4 + OriginalPart.Size * i));
        for (int j = 0; j < nC; j++) s.RepairQueue.Add(OriginalPart.Read(d, (int)atC + 4 + OriginalPart.Size * j));
        return s;
    }

    /// <summary>An equipped name that fits nothing: blank, or the literal "Empty".</summary>
    public static bool IsEmpty(string label) => label.Length == 0 || label.Equals(EmptyLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The repair queue as inventory indices, as the original's reader links it: each queued record to the first
    /// inventory record in repair (state 3) with the same condition and definition file not already linked. A queued
    /// record with no such match is dropped.
    /// </summary>
    public List<int> QueuedInventory()
    {
        var l = new List<int>();
        foreach (var r in RepairQueue)
        {
            int i = -1;
            for (int k = 0; k < Inventory.Count && i < 0; k++)
            {
                var p = Inventory[k];
                if (p.State == (int)OriginalPartState.Repair && p.Condition == r.Condition && p.Def.Equals(r.Def, StringComparison.OrdinalIgnoreCase) && !l.Contains(k)) i = k;
            }
            if (i >= 0) l.Add(i);
        }
        return l;
    }
}
