using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #214: a won trip mission stopped with "Salvage Index out of range": the after-mission damage met a part marked in the car in
/// no slot of it (0.12.1 has no guard there). Two more ways the garage and FIELD SALVAGE leave a part marked in the car where it
/// does not sit: a part in the repair order whose slot was refilled went back into the car over the new part when it came out of
/// the order, and a weapon sent to the field (FIELD SALVAGE's full weapons list) stayed in the order. Each is checked alone, then
/// every shell step between missions at random over many seeds.
/// </summary>
public class RepairOrderSlotTests
{
    private static readonly PartDef[] All =
    [
        new("V8", 2, 0, "eng01", "eng01", 1, 300, 400), new("V6", 2, 0, "eng02", "eng02", 2, 300, 350),
        new("Susp", 3, 0, "sus01", "sus01", 1, 100, 100), new("Susp2", 3, 0, "sus02", "sus02", 2, 100, 100),
        new("Brakes", 4, 0, "bra01", "bra01", 1, 100, 50), new("Brakes2", 4, 0, "bra02", "bra02", 2, 100, 50),
        new("13in", 5, 0, "wauto_0a", "wauto_0a.wdf", 0, 100, 20), new("14in", 5, 0, "wauto_0b", "wauto_0b.wdf", 0, 100, 20),
        new("30cal", 7, 0, "gmlight", "gmlight.gdf", 0, 50, 40), new("50cal", 7, 0, "gmmedium", "gmmedium.gdf", 0, 60, 50),
        new("Cannon", 7, 0, "gclight", "gclight.gdf", 0, 70, 60), new("Rocket", 6, 0, "gdumb", "gdumb.gdf", 0, 40, 30),
        new("Blox", 7, 0, "gblox", "gblox.gdf", 0, 50, 30), new("Grenade", 9, 0, "ggrenade", "ggrenade.gdf", 0, 50, 30),
        new("Oil", 8, 0, "goilslck", "goilslck.gdf", 0, 40, 60), new("Mines", 8, 0, "glandmin", "glandmin.gdf", 0, 40, 60),
        new("Nitro", 13, 0, "spc01", "spc01", 0, 1, 10), new("Radar", 13, 0, "spc04", "spc04", 3, 1, 10),
    ];

    private static PartDef P(string stem) => All.First(p => p.Stem == stem);

    private static PartsDatabase Db()
    {
        var db = new PartsDatabase();
        db.Parts.AddRange(All);
        return db;
    }

    /// <summary>Engine, suspension, brakes, four tyres, three guns, a dropper and three special rows, each part at full health.</summary>
    private static TripInventory Car()
    {
        var inv = new TripInventory();
        void Fit(CarSlot s, string stem) { inv.Slots.Add(s); inv.Nodes.Add(new PartNode(P(stem), P(stem).MaxHp, PartState.Car, inv.Slots.Count - 1)); }
        Fit(new CarSlot(2, "ENGINE"), "eng01");
        Fit(new CarSlot(3, "SUSP"), "sus01");
        Fit(new CarSlot(4, "BRAKE"), "bra01");
        foreach (var l in new[] { "WHL FR", "WHL FL", "WHL BR", "WHL BL" }) Fit(new CarSlot(5, l), "wauto_0a.wdf");
        Fit(new CarSlot(7, "#1 Fwd", 0, 0), "gmlight.gdf");
        Fit(new CarSlot(7, "#2 Fwd", 1, 0), "gmlight.gdf");
        Fit(new CarSlot(7, "#3 Top", 2, 1), "gmmedium.gdf");
        Fit(new CarSlot(8, "#4 Drop", 3, 4), "goilslck.gdf");
        Fit(new CarSlot(0xc, "NITRO"), "spc01");
        inv.Slots.Add(new CarSlot(0xc, "SPECIAL"));
        inv.Slots.Add(new CarSlot(0xc, "SPECIAL"));
        return inv;
    }

    /// <summary>What is wrong with the inventory, or null: a car part out of the car's slots, two parts in one slot, a part in the order not marked in repair.</summary>
    private static string? Broken(TripInventory inv)
    {
        foreach (var n in inv.Nodes)
            if (n.State == PartState.Car && (n.Slot < 0 || n.Slot >= inv.Slots.Count)) return $"{n.Part.Id} marked in the car in slot {n.Slot}";
        foreach (var g in inv.Nodes.Where(n => n.State == PartState.Car).GroupBy(n => n.Slot))
            if (g.Count() > 1) return $"{g.Count()} parts in slot {g.Key}";
        foreach (var r in inv.Repairs)
            if (r.State != PartState.Repair || !inv.Nodes.Contains(r)) return $"{r.Part.Id} in the order as {r.State}";
        return null;
    }

    [Fact]
    public void APartWhoseSlotWasRefilledComesOutOfTheOrderToTheVan()
    {
        var inv = Car();
        var v8 = inv.InSlot(0)!;
        v8.Health = 100;
        Assert.True(inv.QueueRepair(v8));
        var v6 = new PartNode(P("eng02"), 300, PartState.Van);
        inv.Nodes.Add(v6);
        inv.Install(0, v6);
        inv.Unqueue(v8);
        Assert.Equal((PartState.Van, -1), (v8.State, v8.Slot));
        Assert.Same(v6, inv.InSlot(0));
        Assert.Null(Broken(inv));
    }

    [Fact]
    public void APartWhoseSlotWasRefilledIsRepairedIntoTheVan()
    {
        var inv = Car();
        var v8 = inv.InSlot(0)!;
        v8.Health = 100;
        Assert.True(inv.QueueRepair(v8));
        var v6 = new PartNode(P("eng02"), 300, PartState.Van);
        inv.Nodes.Add(v6);
        inv.Install(0, v6);
        var back = TripInventory.Load(inv.Save(), Db(), null!)!;
        back.MissionWon(new CarState(250, 100, 100, [100, 100, 0, 0, 100, 100], []), null, Db(), () => 0);
        Assert.Equal(PartState.Van, back.Nodes.Single(n => n.Part.Stem == "eng01").State);
        Assert.Equal(("eng02", 250), (back.InSlot(0)!.Part.Stem, back.InSlot(0)!.Health));
        Assert.Null(Broken(back));
    }

    /// <summary>A part picked back into its own slot from the order is in the car again, out of the order.</summary>
    [Fact]
    public void APartPickedBackFromTheOrderIsInItsSlot()
    {
        var inv = Car();
        var v8 = inv.InSlot(0)!;
        v8.Health = 100;
        Assert.True(inv.QueueRepair(v8));
        inv.Install(0, v8);
        Assert.Same(v8, inv.InSlot(0));
        Assert.Empty(inv.Repairs);
        Assert.Null(Broken(inv));
    }

    [Fact]
    public void AWeaponSentToTheFieldLeavesTheOrderAndStaysBehind()
    {
        var inv = Car();
        var gun = inv.InSlot(7)!;
        gun.Health = 10;
        Assert.True(inv.QueueRepair(gun));
        inv.ToField(gun);
        Assert.Empty(inv.Repairs);
        Assert.Equal((PartState.Field, -1), (gun.State, gun.Slot));
        Assert.Null(Broken(inv));
        inv.MissionWon(null, null, Db(), () => 0);
        Assert.DoesNotContain(gun, inv.Nodes);
    }

    // FIELD SALVAGE's lists (FieldSalvageScreen: Build and Move), as the screen sorts and moves the parts
    private static int ScreenList(int type) => type switch { 2 => 0, 12 or 13 => 1, >= 6 and <= 11 => 2, 5 => 3, 1 or 4 => 4, 3 => 5, _ => -1 };
    private static readonly int[] Rows = [3, 9, 11, 11, 5, 4];
    private static readonly int[] SalvageOf = [6, 8, 7, 9, 10, 11];
    private static readonly int[] Twin = [6, 8, 7, 9, 10, 11, 0, 2, 1, 3, 4, 5];

    private static List<PartNode>[] Build(TripInventory inv)
    {
        var lists = Enumerable.Range(0, 12).Select(_ => new List<PartNode>()).ToArray();
        foreach (var n in inv.Nodes.ToList())
        {
            int l = ScreenList(n.Part.Type);
            if (l < 0) continue;
            if (n.State == PartState.Field) { if (l == 1) n.Health = n.Part.MaxHp; lists[SalvageOf[l]].Add(n); continue; }
            if (l == 2 && lists[2].Count > 10) { inv.ToField(n); lists[7].Add(n); continue; }
            if (l == 1 && n.State == PartState.Car && n.Health < n.Part.MaxHp) continue;
            lists[l].Add(n);
        }
        return lists;
    }

    private static void Move(TripInventory inv, List<PartNode>[] lists, int from, int k)
    {
        int to = Twin[from];
        if (to < 6 && lists[to].Count >= Rows[to]) return;
        var n = lists[from][k];
        lists[from].RemoveAt(k);
        if (n.State == PartState.Field) n.State = PartState.Van;
        else inv.ToField(n);
        lists[to].Add(n);
    }

    /// <summary>
    /// T01 to T16 with every step the shell offers between two missions at random: the win (damage, repairs, the field salvage, some of it
    /// at a random condition), FIELD SALVAGE's moves both ways, the garage's strips and picks (parts in the order among them), the
    /// CAR/VAN INVENTORY's queue and unqueue, the repair order's click, SALVAGE back from the garage, a bookmark saved, and one loaded
    /// (which comes back through FIELD SALVAGE). After every step no part is marked in the car out of its slot or over another.
    /// </summary>
    [Fact]
    public void EveryShellStepBetweenMissionsKeepsEachCarPartInItsOwnSlot()
    {
        var db = Db();
        for (int seed = 1; seed <= 600; seed++)
        {
            var rng = new Random(seed);
            var inv = Car();
            string last = "";
            void Check(int mission, string what)
            {
                last = what;
                if (Broken(inv) is { } b) Assert.Fail($"seed {seed}, after T{mission:00}: {b} after {what}");
            }
            for (int mission = 1; mission <= 16; mission++)
            {
                var state = new CarState(rng.Next(0, 300), rng.Next(0, 100), rng.Next(0, 100), [.. Enumerable.Range(0, 6).Select(_ => rng.Next(0, 100))],
                    [.. Enumerable.Range(0, 4).Select(h => (h, rng.Next(0, 70), -1))]);
                var salvage = Enumerable.Range(0, rng.Next(0, 20)).Select(_ => All[rng.Next(All.Length)])
                    .Select(p => new SalvageItem(p.Stem, rng.Next(2) == 0 ? -1 : rng.Next(1, p.MaxHp + 1), true)).ToList();
                inv.MissionWon(state, salvage, db, () => rng.Next(32768));
                Check(mission, "the win");
                bool salvageScreen = true;
                for (int op = 0; op < 40; op++)
                {
                    if (salvageScreen)
                    {
                        var lists = Build(inv);
                        Check(mission, "FIELD SALVAGE's lists");
                        for (int m = rng.Next(0, 12); m > 0; m--)
                        {
                            int from = rng.Next(12);
                            if (lists[from].Count == 0) continue;
                            Move(inv, lists, from, rng.Next(lists[from].Count));
                            Check(mission, $"a FIELD SALVAGE move from list {from}");
                        }
                        salvageScreen = false;
                        continue;
                    }
                    switch (rng.Next(7))
                    {
                        case 0:
                        {
                            var fitted = inv.Nodes.Where(n => n.State == PartState.Car && inv.Slots[n.Slot].Type is 7 or 8 or 0xc).ToList();
                            if (fitted.Count == 0) continue;
                            inv.Strip(fitted[rng.Next(fitted.Count)]);
                            Check(mission, "a strip");
                            break;
                        }
                        case 1:
                        {
                            int slot = rng.Next(inv.Slots.Count);
                            int t = inv.Slots[slot].Type;
                            var offered = inv.PickerParts(t).ToList();
                            if (offered.Count == 0) continue;
                            var specials = t == 0xc ? inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 0xc).Select(x => x.i).ToList() : null;
                            inv.Install(slot, offered[rng.Next(offered.Count)], sameName: specials);
                            Check(mission, $"a pick into slot {slot}");
                            break;
                        }
                        case 2:
                        {
                            var listed = inv.Nodes.Where(n => n.State != PartState.Field && TripInventory.ListOf(n.Part.Type) >= 0).ToList();
                            if (listed.Count == 0) continue;
                            var n = listed[rng.Next(listed.Count)];
                            if (inv.Repairs.Contains(n)) inv.Unqueue(n);
                            else if (n.Health < n.Part.MaxHp) inv.QueueRepair(n);
                            else continue;
                            Check(mission, "a CAR/VAN INVENTORY click");
                            break;
                        }
                        case 3:
                        {
                            if (inv.Repairs.Count == 0) continue;
                            int k = rng.Next(inv.Repairs.Count);
                            var n = inv.Repairs[k];
                            inv.Repairs.RemoveAt(k);
                            n.State = PartState.Van;
                            n.Slot = -1;
                            Check(mission, "a click in the repair order");
                            break;
                        }
                        case 4: salvageScreen = true; break;
                        case 5: inv = TripInventory.Load(inv.Save(), db, null!)!; Check(mission, "a bookmark saved and loaded"); break;
                        case 6: inv = TripInventory.Load(inv.Save(), db, null!)!; salvageScreen = true; Check(mission, "a bookmark loaded"); break;
                    }
                }
            }
            Assert.NotEqual("", last);
        }
    }
}
