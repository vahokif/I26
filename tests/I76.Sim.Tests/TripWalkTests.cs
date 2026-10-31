using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Rng;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A fresh trip walked through everything the shell does between two missions (the player's report: "after finishing day 2 the
/// game says something went wrong and I can't go on to day 3"): NEW TRIP's stock car, each mission won with damage and its
/// field salvage (the .fsi, and kills), the dream after T04, YOU ARE UNCONSCIOUS before T13, the garage's picks from the salvage
/// into every slot they fit, the bookmark's save and load, and the launch of the next mission in the car as written.
/// </summary>
public class TripWalkTests
{
    private static string? Install => SnapshotRig.Install;

    /// <summary>Every trip mission's stock car, and the four CHOOSE YOUR VEHICLE cars, build into a trip inventory, write back as a car file, and launch.</summary>
    [Fact]
    public void EveryStockTripCarBuildsWritesAndLaunches()
    {
        if (Install is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        string[] cars = [.. Enumerable.Range(1, 17).Select(n => $"vppt{n:00}.vcf"), "vcgcourf.vcf", "vgoonf.vcf", "vstankf.vcf", "vhelif.vcf"];
        int built = 0;
        try
        {
            foreach (var car in cars)
            {
                if (vfs.TryRead(car) is null) continue;
                built++;
                var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read(car), x => VehicleFactory.EngineCompId(vfs, x)), db, vfs);
                inv = TripInventory.Load(inv.Save(), db, vfs)!;
                AssetVfs.Addon["vehtrip_walk.vcf"] = inv.ToCar().ToVcf();
                var v = VehicleFactory.Create(vfs, "vehtrip_walk.vcf");
                v.Health = VehicleDamage.Create(v, v.Build!, true, new CrtRand(1));
                inv.State().Apply(v);
                // WakeUp (T13) and the dream's reload work on any of them
                inv.WakeUp(db);
                inv.DreamOver();
                _ = inv.Weight();
            }
            Assert.True(built >= 17, $"only {built} stock cars found");
        }
        finally { AssetVfs.Addon.TryRemove("vehtrip_walk.vcf", out _); }
    }

    /// <summary>
    /// T01 to T16 as a new trip goes: the car as the last garage wrote it, a random mission's damage and kills, the win's entry
    /// (the dream's reload after T04, the unconscious repair before T13), then the garage taking salvage into every slot it
    /// fits (and DONE refusing what it refuses), a bookmark saved and loaded, the next launch. Several seeds.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(76)]
    public void ANewTripGoesThroughEveryPostMissionScreenToT17(int seed)
    {
        if (Install is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        var rand = new CrtRand((uint)seed);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), x => VehicleFactory.EngineCompId(vfs, x)), db, vfs);
        try
        {
            for (int mission = 1; mission <= 16; mission++)
            {
                string stem = $"t{mission:00}";
                // the launch as LaunchTrip writes it
                AssetVfs.Addon["vehtrip_walk.vcf"] = inv.ToCar().ToVcf();
                var v = VehicleFactory.Create(vfs, "vehtrip_walk.vcf");
                v.Health = VehicleDamage.Create(v, v.Build!, true, new CrtRand(1));
                inv.State().Apply(v);
                // the mission: damage, and kills for the pool (a car's parts, and the .fsi's at the end)
                foreach (var part in new Part?[] { v.Health.Engine, v.Health.Suspension, v.Health.Brake }.Concat(v.Health.Slots.Take(6)))
                    if (part is not null) part.Hp = rand.Next() % 5 == 0 ? 0 : part.Hp * (rand.Next() % 100) / 100;
                foreach (var w in v.Armory?.LoadOrder ?? []) w.Hp = w.Hp * (rand.Next() % 100) / 100;
                var pool = new SalvagePool(stem);
                foreach (var name in new[] { "eng01", "eng02", "sus01", "sus02", "bra01", "bra02", "gmlight.gdf", "gcheavy.gdf", "gdumb.gdf", "goilslck.gdf", "wauto_1a.wdf", "wauto_1b.wdf", "spc05", "spc01" })
                    if (rand.Next() % 2 == 0) pool.Add(name, name.EndsWith(".wdf") ? -1 : 100, name.EndsWith(".wdf"));
                var items = pool.Finish(vfs.TryRead, rand.Next);

                // the shell's entry after the win (FirstScreen)
                bool dream = mission == 4;
                if (dream) inv.DreamOver();
                else inv.MissionWon(mission == 12 ? null : CarState.Capture(v), mission == 12 ? null : items, db, rand.Next);
                // the router: YOU ARE UNCONSCIOUS before T13
                if (mission + 1 == 13) { inv.WakeUp(db); Assert.Null(inv.Validate()); }
                else
                {
                    // FIELD SALVAGE: some of it to the van (a car part sent to the field comes back as the same node)
                    foreach (var n in inv.Nodes.Where(n => n.State == PartState.Field).ToList())
                        if (rand.Next() % 3 != 0) n.State = PartState.Van;
                    inv = TripInventory.Load(inv.Save(), db, vfs)!;
                    // the garage: pick each van part into a slot of its kind
                    foreach (var n in inv.Nodes.Where(n => n.State == PartState.Van).ToList())
                    {
                        if (rand.Next() % 2 == 0) continue;
                        var slots = inv.Slots.Select((s, k) => (s, k)).Where(x => SlotTakes(x.s.Type, n.Part.Type)).Select(x => x.k).ToList();
                        if (slots.Count == 0) continue;
                        int slot = slots[rand.Next() % slots.Count];
                        var old = inv.InSlot(slot);
                        inv.Install(slot, n);
                        // DONE would refuse the car: put the old part back
                        if (inv.Validate() is not null && old is not null) inv.Install(slot, old);
                    }
                    // then the repairs: every damaged part in the car that fits the order
                    foreach (var n in inv.Nodes.Where(n => n.State == PartState.Car && n.Health < n.Part.MaxHp).ToList())
                    {
                        if (inv.QueueRepair(n) && inv.Validate() is not null) inv.Unqueue(n);
                    }
                    // a car DONE refuses is fixed up as the player must: the first van part of the missing kind, else the stock one back
                    for (int guard = 0; guard < 12 && inv.Validate() is { } box; guard++)
                    {
                        int type = box switch { 21 => 2, 22 => 3, 23 => 4, _ => 5 };
                        var spare = inv.Nodes.FirstOrDefault(n => n.State is PartState.Van or PartState.Repair && n.Part.Type == type);
                        int slot = inv.Slots.FindIndex(s => s.Type == type);
                        if (spare is null || slot < 0) break;
                        if (spare.State == PartState.Repair) inv.Unqueue(spare);
                        inv.Install(slot, spare);
                    }
                    inv.LeaveField();
                }
                if (inv.Validate() is { } refused) Assert.Fail($"after T{mission:00} (seed {seed}) the garage's car is refused: notice {refused}");
                inv = TripInventory.Load(inv.Save(), db, vfs)!;
            }
        }
        finally { AssetVfs.Addon.TryRemove("vehtrip_walk.vcf", out _); }
    }

    private static bool SlotTakes(int slotType, int partType) => TripInventory.ListOf(partType) is var l and >= 0 && slotType switch
    {
        2 => partType == 2, 3 => partType == 3, 4 => partType is 1 or 4, 5 => partType == 5,
        7 or 8 => partType is >= 6 and <= 11, 0xc => partType is 12 or 13, _ => false,
    };
}
