using I76.Sim.Damage;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The trip's field salvage from a mission's end to the FIELD SALVAGE screen: the mission run with its cars destroyed
/// (as in the original game: every other car through the FSM's destroy at tick 100), the pool
/// fed where the engine feeds it, thinned, and handed to the trip's inventory as the shell does after a win. The
/// original's numbers are its own measured runs.
/// </summary>
public class SalvageFlowTests
{
    private static List<string[]> Measured(string run) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "salvage-measured.csv"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')).Where(r => r[0] == run).ToList();

    /// <summary>A trip mission in its stock car, every other car destroyed at tick 100 when asked; the pool, the kills and the player.</summary>
    private static (SalvagePool Pool, int Kills, Vehicle Player, SnapshotRig.Loaded L) Run(string dir, string mission, bool kill)
    {
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, mission, $"vppt{mission[1..3]}.vcf"), SimRate.Default, 1);
        var pool = new SalvagePool(mission);
        var player = setup.World.Player!;
        pool.Watch(setup.Damage, player);
        int kills = 0;
        setup.Damage.Salvageable += v => { if (v != player) kills++; };
        if (kill) s.DebugBoom = "100,kill-all";
        for (int t = 0; t < 120; t++) s.Tick([default]);
        return (pool, kills, player, l);
    }

    /// <summary>The pool's groups, copied, so that each draw sequence thins the same pool.</summary>
    private static SalvagePool Copy(SalvagePool p, string mission) { var c = new SalvagePool(mission); c.Load(p.Save()); return c; }

    /// <summary>
    /// Tester: "only getting like 1 car's worth of salvage … when you should be getting better and more stuff". A won
    /// mission with every other car destroyed offers what the original offers for the same kills: over the same rand()
    /// streams, the mean length of the FIELD SALVAGE lists within half an item of the original's own adds thinned alike,
    /// and the count the original measured inside the range the port's runs reach. Nothing killed: the whole .fsi.
    /// </summary>
    [Theory]
    [InlineData("menu-kill-s1/t01", "t01", 5)]
    [InlineData("t01-skip-s1/t01", "t01", 0)]
    [InlineData("menu-kill-s1/t02", "t02", 12)]
    [InlineData("menu-kill-s1/t03", "t03", 12)]
    [InlineData("t05-kill-s2/t05", "t05", 14)]
    public void AWonTripMissionOffersTheOriginalsFieldSalvage(string run, string mission, int originalKills)
    {
        if (SnapshotRig.Install is not { } dir) return;
        var rows = Measured(run);
        var (pool, kills, player, l) = Run(dir, mission, originalKills > 0);
        Assert.Equal(originalKills, kills);
        var db = PartsDatabase.Build(l.Vfs);
        var state = CarState.Capture(player);
        var car = TripCar.FromVcf(l.Vfs.Read($"vppt{mission[1..3]}.vcf"), x => VehicleFactory.EngineCompId(l.Vfs, x));
        int min = int.MaxValue, max = 0;
        double port = 0, orig = 0;
        const int Seeds = 300;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            // the port: the pool as the mission left it, the .fsi from the archive, then the shell's entry after a win
            var items = Copy(pool, mission).Finish(l.Vfs.TryRead, new CrtRand((uint)seed).Next)!;
            var inv = TripInventory.FromCar(car, db, l.Vfs);
            inv.MissionWon(state, items, db, new CrtRand((uint)seed).Next);
            int offered = inv.Nodes.Count(x => x.State == PartState.Field);
            Assert.Equal(items.Count, offered);
            port += offered; min = System.Math.Min(min, offered); max = System.Math.Max(max, offered);
            // the original's adds, thinned by the same draws
            var o = new SalvagePool(mission);
            foreach (var r in rows.Where(r => r[1] == "add")) o.Add(r[2], int.Parse(r[3]), r[4] == "1");
            orig += o.Finish(_ => null, new CrtRand((uint)seed).Next)!.Count;
        }
        port /= Seeds; orig /= Seeds;
        Assert.True(System.Math.Abs(port - orig) <= 0.5, $"{run}: the port offers {port:0.00} on average, the original {orig:0.00}");
        Assert.InRange(rows.Count(r => r[1] == "kept"), min, max);
        if (originalKills == 0) Assert.Equal(rows.Count(r => r[1] == "add"), max);
    }

    /// <summary>
    /// The original takes a wreck's salvage after the explosion and the AI's component wipe (each part loses 50–100 %
    /// of what it has), so wreck parts arrive at under half their health (measured in the original: tyres 2..50 of 100,
    /// engines 11..149 of 300); the port took them before the wipe, at full health.
    /// </summary>
    [Fact]
    public void AWrecksPartsArriveAtWhatTheComponentWipeLeft()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (pool, kills, _, l) = Run(dir, "t03", true);
        Assert.Equal(12, kills);
        var db = PartsDatabase.Build(l.Vfs);
        var wreck = pool.Items.Where(i => !i.RandomCondition && SalvagePool.TypeOf(i.Name) is 0 or 2 or 3 or 4).ToList();
        Assert.True(wreck.Count > 40, $"{wreck.Count} wreck parts");
        foreach (var it in wreck)
        {
            var part = db.ByStem(it.Name)!;
            // the wipe takes (129..256)/256 of a part: at most half is left, and exactly half when the roll is 0
            Assert.True(it.Health > 0 && it.Health * 2 <= part.MaxHp, $"{it.Name} at {it.Health} of {part.MaxHp}");
        }
        // the original's own adds say the same of its wrecks
        foreach (var r in Measured("menu-kill-s1/t03").Where(r => r[1] == "add" && r[4] == "0" && SalvagePool.TypeOf(r[2]) is 2 or 3 or 4))
            if (db.ByStem(r[2]) is { } p) Assert.True(int.Parse(r[3]) * 2 < p.MaxHp, $"original {r[2]} at {r[3]} of {p.MaxHp}");
    }

    /// <summary>
    /// Tester: "sometimes you're missing an engine or suspension when it should be in there". A whole trip, T01 to T16:
    /// each mission launched from the inventory as the shell writes it (vehscn.vcf and its state), damaged at random
    /// (engine, suspension or brakes now and then destroyed), won with its field salvage, then the garage's work: every
    /// damaged car part queued for repair through the pop-up and a spare fitted where DONE would refuse the car, half the
    /// salvage taken, a bookmark saved and loaded, YOU ARE UNCONSCIOUS's auto-repair before T13. The car always launches
    /// with an engine, suspension and brakes, and a win never takes one out of it.
    /// </summary>
    [Fact]
    public void TheCarKeepsItsEngineSuspensionAndBrakesThroughTheTrip()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var vfs = new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        var rand = new CrtRand(76);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), x => VehicleFactory.EngineCompId(vfs, x)), db, vfs);
        bool Has(int type) => inv.Slots.Select((sl, k) => (sl, k)).Any(x => x.sl.Type == type && inv.InSlot(x.k) is not null);
        try
        {
            for (int mission = 1; mission <= 16; mission++)
            {
                // the launch: the car and its state as the shell hands them to the engine
                I76.Formats.Vfs.AssetVfs.Addon["vehscn.vcf"] = inv.ToCar().ToVcf();
                var v = VehicleFactory.Create(vfs, "vehscn.vcf");
                v.Health = VehicleDamage.Create(v, v.Build!, true, new CrtRand(1));
                inv.State().Apply(v);
                Assert.True(v.Health.Engine is not null && v.Health.Suspension is not null && v.Health.Brake is not null, $"T{mission:00} launched without a component");
                // the mission: damage, a component destroyed now and then
                foreach (var part in new Part?[] { v.Health.Engine, v.Health.Suspension, v.Health.Brake }.Concat(v.Health.Slots.Take(6)))
                    if (part is not null) part.Hp = rand.Next() % 4 == 0 ? 0 : part.Hp * (rand.Next() % 100) / 100;
                foreach (var w in v.Armory?.LoadOrder ?? []) w.Hp = w.Hp * (rand.Next() % 100) / 100;
                if (mission == 4 || mission == 12) { inv.MissionWon(mission == 12 ? null : CarState.Capture(v), mission == 12 ? null : [], db, rand.Next); }
                else
                {
                    var pool = new SalvagePool($"t{mission:00}");
                    pool.Add($"eng0{1 + rand.Next() % 4}", 100, false);
                    pool.Add($"sus0{1 + rand.Next() % 4}", 80, false);
                    pool.Add($"bra0{1 + rand.Next() % 4}", 60, false);
                    inv.MissionWon(CarState.Capture(v), pool.Finish(vfs.TryRead, rand.Next), db, rand.Next);
                }
                Assert.True(Has(2) && Has(3) && Has(4), $"the win after T{mission:00} took a component out of the car");
                if (mission == 16) break;
                // FIELD SALVAGE: every other item taken
                foreach (var (n, k) in inv.Nodes.Where(n => n.State == PartState.Field).Select((n, k) => (n, k)).ToList())
                    if (k % 2 == 0) n.State = PartState.Van;
                // a bookmark, saved and loaded
                inv = TripInventory.Load(inv.Save(), db, vfs)!;
                Assert.True(Has(2) && Has(3) && Has(4), $"the bookmark after T{mission:00} lost a component");
                if (mission + 1 == 13) inv.WakeUp(db);
                else
                {
                    // the garage: every damaged car part into repair; where DONE would refuse, the best spare fitted, else back out of the order
                    foreach (var n in inv.Nodes.Where(n => n.State == PartState.Car && n.Health < n.Part.MaxHp).ToList())
                    {
                        int slot = n.Slot;
                        if (!inv.QueueRepair(n) || n.State != PartState.Repair) continue;
                        if (inv.Validate() is null) continue;
                        var spare = inv.Nodes.Where(x => x.State == PartState.Van && x.Part.Type == inv.Slots[slot].Type && x.Health > 0).MaxBy(x => x.Health);
                        if (spare is not null) inv.Install(slot, spare); else inv.Unqueue(n);
                        if (inv.Validate() is not null) inv.Install(slot, n);
                    }
                    inv.LeaveField();
                }
                Assert.Null(inv.Validate());
            }
        }
        finally { I76.Formats.Vfs.AssetVfs.Addon.TryRemove("vehscn.vcf", out _); }
    }
}
