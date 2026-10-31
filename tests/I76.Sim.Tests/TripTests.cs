using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Rng;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

public class TripTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The examples: salvage cannot outrun the story.</summary>
    [Fact]
    public void TheTierClampWalksBackToWhatTheNextMissionAllows()
    {
        // a 30 mm cannon off a car killed in T05 (level 6) arrives as a 20 mm cannon
        Assert.Equal("gclight.gdf", SalvagePool.Clamp("gcheavy.gdf", 1, 6, 2));
        // a turret is never refused, only downgraded to the family's base
        Assert.Equal("tmlight.gdf", SalvagePool.Clamp("tcheavy.gdf", 1, 6, 2));
        Assert.Equal("wauto_0b.wdf", SalvagePool.Clamp("wauto_0d.wdf", 0, 6, 2));
        Assert.Equal("eng02", SalvagePool.Clamp("eng03", 2, 6, 2));
        Assert.Equal("gcheavy.gdf", SalvagePool.Clamp("gcheavy.gdf", 1, 12, 2));
        Assert.Null(SalvagePool.Clamp("gh45.gdf", 1, 6, 2));          // not in the tables: never salvageable
    }

    [Fact]
    public void ThePoolRenamesWheelsDropsTheUnknownAndThins()
    {
        var pool = new SalvagePool("t01");
        Assert.Equal(2, pool.Level);
        Assert.Equal(2, pool.Kind);
        for (int i = 0; i < 6; i++) pool.Add("wauto_1a.wdf", -1, true);
        pool.Add("foo.bar", 10, false);
        foreach (var g in new[] { "gdumb.gdf", "gmlight.gdf", "goilslck.gdf", "gmlight.gdf" }) pool.Add(g, 50, false);
        pool.Add("spc05", 1, false);
        pool.Add("spc05", 1, false);
        Assert.All(pool.Items.Where(i => i.Name.EndsWith(".wdf")), i => Assert.Equal("wauto_0a.wdf", i.Name));
        Assert.DoesNotContain(pool.Items, i => i.Name == "foo.bar");
        var rng = new CrtRand(7);
        var kept = pool.Finish(_ => null, rng.Next)!;
        Assert.Equal(4, kept.Count(i => i.Name.EndsWith(".wdf")));
        Assert.Equal(1, kept.Count(i => i.Name.StartsWith("spc")));
        // a group per name: the three lone weapons always stay, the two 30cal MGs keep 1 or 2
        Assert.Equal(1, kept.Count(i => i.Name == "gdumb.gdf"));
        Assert.Equal(1, kept.Count(i => i.Name == "goilslck.gdf"));
        Assert.InRange(kept.Count(i => i.Name == "gmlight.gdf"), 1, 2);
    }

    /// <summary>
    /// The original's own field salvage, replayed: every add the original saw
    /// (dead cars, then the .fsi), in order, and every rand() the original
    /// drew, measured in the running game (T01–T05 wins, some with every
    /// other car killed). The same adds and the same draws must leave the
    /// same pool, item for item and in the same order.
    /// </summary>
    [Fact]
    public void ThinningReproducesTheOriginalsMeasuredRuns()
    {
        var rows = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "salvage-measured.csv"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(','));
        int runs = 0;
        foreach (var run in rows.GroupBy(r => r[0]))
        {
            var pool = new SalvagePool(run.First(r => r[1] == "stem")[2]);
            foreach (var r in run.Where(r => r[1] == "add")) pool.Add(r[2], int.Parse(r[3]), r[4] == "1");
            var draws = new Queue<int>(run.Where(r => r[1] == "rand").Select(r => int.Parse(r[2])));
            var kept = pool.Finish(_ => null, () => draws.Dequeue())!;
            var want = run.Where(r => r[1] == "kept").Select(r => $"{r[2]}/{r[3]}/{r[4]}").ToList();
            Assert.Equal(want, kept.Select(i => $"{i.Name}/{i.Health}/{(i.RandomCondition ? 1 : 0)}").ToList());
            Assert.True(draws.Count == 0, $"{run.Key}: {draws.Count} draws left over");
            runs++;
        }
        Assert.True(runs >= 6, $"{runs} runs in the fixture");
    }

    /// <summary>
    /// Tester: "you only get one 14in tire when you should get four". The
    /// .fsi's four tyres are a group of their own unless a wreck's tyres
    /// clamp to the same name, so after T03 they all arrive beside however
    /// many 13in tyres the wrecks gave (measured: 4 × 13in Stock and
    /// 4 × 14in Rally).
    /// </summary>
    [Fact]
    public void TheFsiTyresStayFourBesideAWrecksTyres()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        for (uint seed = 1; seed <= 20; seed++)
        {
            var pool = new SalvagePool("t03");
            for (int car = 0; car < 12; car++)
                for (int k = 0; k < 4; k++) pool.Add("wauto_3a.wdf", 30, false);
            var kept = pool.Finish(vfs.TryRead, new CrtRand(seed).Next)!;
            Assert.Equal(4, kept.Count(i => i.Name == "wauto_0a.wdf"));
            Assert.Equal(4, kept.Count(i => i.Name == "wauto_0b.wdf"));
            // and the .fsi's lone pieces are never thinned away
            foreach (var one in new[] { "gdumb.gdf", "gmmedium.gdf", "eng01", "bra01", "sus01" })
                Assert.Equal(1, kept.Count(i => i.Name == one));
        }
    }

    /// <summary>Measured: a T01 win with nothing killed offers every t01.fsi line -- three weapons, four 13in tyres, engine, brakes, suspension.</summary>
    [Fact]
    public void AT01WinWithNothingKilledOffersTheWholeFsi()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        for (uint seed = 1; seed <= 20; seed++)
        {
            var kept = new SalvagePool("t01").Finish(vfs.TryRead, new CrtRand(seed).Next)!;
            Assert.Equal(["gdumb.gdf", "gmlight.gdf", "goilslck.gdf", "wauto_0a.wdf", "wauto_0a.wdf", "wauto_0a.wdf", "wauto_0a.wdf", "eng01", "bra01", "sus01"],
                kept.Select(i => i.Name).ToArray());
        }
    }

    [Fact]
    public void T12WritesNothingT04KeepsNoSalvageAndTheOptionTurnsItOff()
    {
        Assert.Null(new SalvagePool("t12").Finish(_ => null, () => 0));
        var t04 = new SalvagePool("t04");
        t04.Add("gdumb.gdf", 10, false);
        Assert.Empty(t04.Finish(_ => null, () => 0)!);
        var off = new SalvagePool("t05", noSalvageManagement: true);
        off.Add("gdumb.gdf", 10, false);
        Assert.Empty(off.Items);
    }

    [Fact]
    public void TheMissionsFsiListJoinsOnAWin()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var pool = new SalvagePool("t01");
        var kept = pool.Finish(vfs.TryRead, new CrtRand(1).Next)!;
        // t01.fsi: three weapons, four wauto_1a (as wauto_0a), eng01 bra01 sus01, all at a random condition
        Assert.Contains(kept, i => i.Name == "wauto_0a.wdf");
        Assert.Contains(kept, i => i.Name == "eng01");
        Assert.All(kept, i => Assert.True(i.RandomCondition && i.Health == -1));
    }

    [Fact]
    public void ThePartsDatabaseHasTheComponentsNamedAndTyped()
    {
        if (InstallDir is null) return;
        var db = PartsDatabase.Build(new AssetVfs(new GameInstall(InstallDir)));
        Assert.Equal("261ci  6 cyl", db.ByStem("eng01")!.Id);
        Assert.Equal((2, 0, 300), (db.ByStem("eng01")!.Type, db.ByStem("eng01")!.Ordinal, db.ByStem("eng01")!.MaxHp));
        Assert.Equal(4, db.ByStem("bra01")!.Type);
        Assert.Equal(3, db.ByStem("sus01")!.Type);
        Assert.Equal(5, db.ByStem("wauto_1a.wdf")!.Type);
        Assert.Equal(7, db.ByStem("gmlight.gdf")!.Type);
        Assert.Equal(8, db.ByStem("goilslck.gdf")!.Type);
        Assert.True(db.ByStem("tmlight.gdf")!.IsTurret);
        Assert.Equal("Structo Bmpr", db.ByStem("spc05")!.Id);
        Assert.Equal(10, db.Parts.Count(p => p.IsEmpty));
    }

    /// <summary>A stock car read and written back builds the same vehicle, served from the addon overlay.</summary>
    [Fact]
    public void ACarWrittenBackBuildsTheSameVehicle()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var car = TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        try
        {
            AssetVfs.Addon["vehtrip_back.vcf"] = car.ToVcf();
            var a = VehicleFactory.Create(vfs, "vppt01.vcf");
            var b = VehicleFactory.Create(vfs, "vehtrip_back.vcf");
            Assert.Equal(a.P.Mass, b.P.Mass, 3);
            Assert.Equal(a.Build!.Armour, b.Build!.Armour);
            Assert.Equal(a.Armory!.Weapons.Select(w => w.Def.File), b.Armory!.Weapons.Select(w => w.Def.File));
            var again = TripCar.FromVcf(AssetVfs.Addon["vehtrip_back.vcf"]);
            Assert.Equal(car.Weapons.OrderBy(w => w.Hardpoint), again.Weapons.OrderBy(w => w.Hardpoint));   // written in reverse, as the engine does
            Assert.Equal(car.Wdf, again.Wdf);
        }
        finally { AssetVfs.Addon.TryRemove("vehtrip_back.vcf", out _); }
    }

    [Fact]
    public void DamageCarriesOverButNotArmour()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var v = VehicleFactory.Create(vfs, "vppt01.vcf");
        v.Health = VehicleDamage.Create(v, v.Build!, true, new CrtRand(1));
        v.Health.Engine!.Hp = 600;          // half of 300 × 4
        v.Health.Slots[0]!.Hp = 100;
        v.Armory!.LoadOrder[0].Hp = 50;
        v.Health.Armour[0] = 10;
        var state = CarState.Capture(v);
        Assert.Equal(150, state.EngineHp);
        Assert.Equal(25, state.Wheels[0]);

        var w = VehicleFactory.Create(vfs, "vppt01.vcf");
        w.Health = VehicleDamage.Create(w, w.Build!, true, new CrtRand(1));
        state.Apply(w);
        Assert.Equal(600, w.Health.Engine!.Hp);
        Assert.Equal(1200, w.Health.Engine.Max);
        Assert.Equal(100, w.Health.Slots[0]!.Hp);
        Assert.Equal(50, w.Armory!.LoadOrder[0].Hp);
        Assert.Equal(w.Health.ArmourMax[0], w.Health.Armour[0]);
    }

    [Fact]
    public void AStockCarsSlotsGoBackIntoTheSameCar()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var db = PartsDatabase.Build(vfs);
        var car = TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        var inv = TripInventory.FromCar(car, db, vfs);
        Assert.Equal(["ENGINE", "SUSP", "BRAKE", "WHL FR", "WHL FL", "WHL BR", "WHL BL"], inv.Slots.Take(7).Select(s => s.Label));
        Assert.Contains(inv.Slots, s => s.Type == 0xc);              // the stock cars carry spc05
        Assert.Null(inv.Validate());
        Assert.Equal("Picard Piranha", inv.MakeModel);
        Assert.Equal(3921, inv.Weight());                            // the garage's "3921 LBS" (measured)
        var back = inv.ToCar();
        Assert.Equal((car.Engine, car.Suspension, car.Brakes), (back.Engine, back.Suspension, back.Brakes));
        Assert.Equal(car.Wdf, back.Wdf);
        Assert.Equal(car.Weapons.OrderBy(w => w.Hardpoint), back.Weapons.OrderBy(w => w.Hardpoint));
        Assert.Equal(car.Specials, back.Specials);
    }

    [Fact]
    public void ArmourMovedIntoThePoolLightensTheGarageWeight()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var db = PartsDatabase.Build(vfs);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        int w0 = inv.Weight();
        inv.Car.Armour[0] -= 100; inv.Car.Ninth += 100;       // FRONT armour 10.0 lower: the pool takes it
        Assert.Equal(w0 - 10, inv.Weight());                  // 0.1 lb per unit, the pool not counted
        inv.Car.Ninth -= 100; inv.Car.Armour[2] += 100;        // and back into another zone
        Assert.Equal(w0, inv.Weight());
    }

    [Fact]
    public void SalvageArrivesInTheFieldAndRepairsComeBackAfterAWin()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var db = PartsDatabase.Build(vfs);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        inv.AddSalvage([new SalvageItem("eng02", -1, true), new SalvageItem("gdumb.gdf", 77, false), new SalvageItem("nonsense", 5, false)], db, () => 60);
        Assert.Equal(2, inv.Nodes.Count(n => n.State == PartState.Field));
        Assert.Equal(150, inv.Nodes.First(n => n.Part.Stem == "eng02").Health);      // r = 60: half
        Assert.Equal(77, inv.Nodes.First(n => n.Part.Stem == "gdumb.gdf" && n.State == PartState.Field).Health);

        var engine = inv.InSlot(0)!;
        engine.Health = 50;                                                          // a third of 300 → severity 3, cost 4 × 3
        Assert.True(inv.QueueRepair(engine));
        Assert.Equal((12, 1), inv.RepairTotal());
        inv.FinishRepairs();
        Assert.Equal((300, PartState.Van), (engine.Health, engine.State));
        Assert.Equal(21, inv.Validate());                                             // the engine went to the van
        inv.LeaveField();
        Assert.DoesNotContain(inv.Nodes, n => n.State == PartState.Field);
    }

    [Fact]
    public void AfterTheDreamTheCarIsAsItLeftTheGarage()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var db = PartsDatabase.Build(vfs);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        inv.AddSalvage([new SalvageItem("gdumb.gdf", 77, false)], db, () => 0);
        var engine = inv.InSlot(0)!;
        engine.Health = 50;
        Assert.True(inv.QueueRepair(engine));
        var wheel = inv.InSlot(3)!;
        int wheelHp = wheel.Health;
        // T04 won: no damage taken (the car is the launch's), the field emptied, the part ordered into repair before T04 repaired (full health, in the van: the original sets health to max)
        inv.DreamOver();
        Assert.DoesNotContain(inv.Nodes, n => n.State == PartState.Field);
        Assert.Equal((engine.Part.MaxHp, PartState.Van, -1), (engine.Health, engine.State, engine.Slot));
        Assert.Equal((0, 0), inv.RepairTotal());
        Assert.Equal(wheelHp, wheel.Health);
    }

    [Fact]
    public void TheInventoryKeepsBetweenSessions()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var db = PartsDatabase.Build(vfs);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        inv.AddSalvage([new SalvageItem("gdumb.gdf", 77, false)], db, () => 0);
        inv.InSlot(0)!.Health = 100;
        inv.QueueRepair(inv.InSlot(0)!);
        var back = TripInventory.Load(inv.Save(), db, vfs)!;
        Assert.Equal(inv.Nodes.Select(n => (n.Part.Stem, n.Health, n.State, n.Slot)), back.Nodes.Select(n => (n.Part.Stem, n.Health, n.State, n.Slot)));
        Assert.Single(back.Repairs);
        Assert.Equal("eng01", back.Repairs[0].Part.Stem);
    }

    private static (AssetVfs, PartsDatabase, TripInventory) Stock()
    {
        var vfs = new AssetVfs(new GameInstall(InstallDir!));
        var db = PartsDatabase.Build(vfs);
        return (vfs, db, TripInventory.FromCar(TripCar.FromVcf(vfs.Read("vppt01.vcf"), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs));
    }

    private static List<PartNode> Salvaged(TripInventory inv, PartsDatabase db, string stem, int n)
    {
        inv.AddSalvage(Enumerable.Repeat(new SalvageItem(stem, 60, false), n), db, () => 0);
        var got = inv.Nodes.Where(x => x.State == PartState.Field && x.Part.Stem == stem).ToList();
        got.ForEach(x => x.State = PartState.Van);
        return got;
    }

    /// <summary>Tester: "adding a second tyre of the same size removes the first". Every part is its own node.</summary>
    [Fact]
    public void FourTyresOfOneSizeFitAsFourParts()
    {
        if (InstallDir is null) return;
        var (_, db, inv) = Stock();
        var spares = Salvaged(inv, db, "wauto_0b.wdf", 4);
        Assert.Equal(4, spares.Distinct().Count());
        int[] slots = [.. inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => x.i)];
        Assert.Equal(4, slots.Length);
        for (int k = 0; k < 4; k++) inv.Install(slots[k], spares[k]);
        Assert.All(slots.Select((sl, k) => (inv.InSlot(sl), spares[k])), x => Assert.Same(x.Item2, x.Item1));
        Assert.Equal(4, inv.Nodes.Count(n => n.State == PartState.Car && n.Part.Type == 5 && n.Part.Stem == "wauto_0b.wdf"));
        Assert.Equal(4, inv.Nodes.Count(n => n.State == PartState.Van && n.Part.Stem == "wauto_1a.wdf"));     // the old four, in the van
        Assert.Null(inv.Validate());
        Assert.Equal(["wauto_1b.wdf", "null", "wauto_1b.wdf"], inv.ToCar().Wdf);   // the salvage's wauto_0b, in the car's own wauto_1 wheels

        // the same size under another file name (the car's wauto_1b, the salvage's wauto_0b) still makes a matched pair
        var own = db.ByStem("wauto_1b.wdf")!;
        var other = new PartNode(own, own.MaxHp, PartState.Van);
        inv.Nodes.Add(other);
        inv.Install(slots[0], other);
        Assert.Null(inv.Validate());
    }

    /// <summary>The melee garage's catalogue hands out an entry per pick: four of one size stay four.</summary>
    [Fact]
    public void ACataloguePickNeverEmptiesAnotherSlotOfTheSameName()
    {
        if (InstallDir is null) return;
        var (_, db, inv) = Stock();
        int[] slots = [.. inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => x.i)];
        var tyre = db.ByStem("wauto_0c.wdf")!;
        foreach (int s in slots) inv.Install(s, new PartNode(tyre, tyre.MaxHp, PartState.Van), catalogue: true);
        Assert.Equal(4, inv.Nodes.Count(n => n.State == PartState.Car && n.Part.Type == 5));
        Assert.All(slots, s => Assert.Equal("wauto_0c.wdf", inv.InSlot(s)!.Part.Stem));
        Assert.Equal(4, inv.Nodes.Where(n => n.Part.Type == 5).Distinct().Count());     // the old ones are gone, none doubled

        // two machine guns on two mounts are fine; a special by name is the exception (one of a kind)
        var guns = inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 7).Select(x => x.i).ToArray();
        var mg = db.ByStem("gmlight.gdf")!;
        foreach (int g in guns) inv.Install(g, new PartNode(mg, mg.MaxHp, PartState.Van), catalogue: true, sameName: null);
        Assert.All(guns, g => Assert.Equal("gmlight.gdf", inv.InSlot(g)!.Part.Stem));
        var specials = inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 0xc).Select(x => x.i).ToArray();
        var spc = db.ByStem("spc03")!;
        inv.Install(specials[0], new PartNode(spc, 0, PartState.Van), catalogue: true, sameName: specials);
        inv.Install(specials[1], new PartNode(spc, 0, PartState.Van), catalogue: true, sameName: specials);
        Assert.Null(inv.InSlot(specials[0]));
        Assert.NotNull(inv.InSlot(specials[1]));
    }

    /// <summary>Tester: "between missions the salvage only gives one 14in tyre when it should be four".</summary>
    [Fact]
    public void AWrecksFourWheelsReachTheFieldAsFourParts()
    {
        if (InstallDir is null) return;
        var (vfs, db, inv) = Stock();
        var v = VehicleFactory.Create(vfs, "vppt01.vcf");
        v.Health = VehicleDamage.Create(v, v.Build!, false, new CrtRand(1));
        var pool = new SalvagePool("t03");
        pool.AddDeadCar(v);
        Assert.Equal(4, pool.Items.Count(i => i.Name.EndsWith(".wdf")));
        var items = pool.Finish(_ => null, new CrtRand(5).Next)!;
        Assert.Equal(4, items.Count(i => i.Name.EndsWith(".wdf")));
        inv.AddSalvage(items, db, () => 0);
        var wheels = inv.Nodes.Where(n => n.State == PartState.Field && n.Part.Type == 5).ToList();
        Assert.Equal(4, wheels.Count);
        Assert.Equal(4, wheels.Distinct().Count());
        wheels[0].State = PartState.Van;                                               // taking one leaves the other three
        Assert.Equal(3, inv.Nodes.Count(n => n.State == PartState.Field && n.Part.Type == 5));
        // the whole survives a session
        var back = TripInventory.Load(inv.Save(), db, vfs)!;
        Assert.Equal(4, back.Nodes.Count(n => n.Part.Type == 5 && n.State != PartState.Car));
    }

    /// <summary>
    /// #31, as measured in the real game (a bookmark at Scene 13 with the 261ci in the car, two more in the van and parts in
    /// repair): YOU ARE UNCONSCIOUS writes ENG NUM 3 into vehscn.vcf, the van keeps its two 261ci and nothing else, the
    /// repair order comes back repaired, and the garage after T13 lists "(C) 595ci V-10".
    /// </summary>
    [Fact]
    public void WakingUpBeforeT13FitsTheV10AndRepairsEverything()
    {
        if (InstallDir is null) return;
        var (_, db, inv) = Stock();
        int engineSlot = inv.Slots.FindIndex(sl => sl.Type == 2);
        var old = inv.InSlot(engineSlot)!;
        Assert.Equal("eng01", old.Part.Code);
        foreach (var n in inv.Nodes.Where(n => n.State == PartState.Car)) n.Health = n.Part.MaxHp / 3;
        foreach (var n in Salvaged(inv, db, "eng01", 2)) { n.State = PartState.Van; n.Health = n.Part.MaxHp; }
        var damagedSpare = Salvaged(inv, db, "bra01", 1)[0];
        damagedSpare.State = PartState.Van;
        damagedSpare.Health = 3;                                            // a damaged spare already in the van (the original repairs it)
        var brakes = Salvaged(inv, db, "bra02", 1)[0];
        brakes.State = PartState.Van;
        brakes.Health = 8;
        Assert.True(inv.QueueRepair(brakes));

        inv.WakeUp(db);

        var engine = inv.InSlot(engineSlot)!;
        Assert.Same(old, engine);                                           // the node keeps its place, with the V-10 in it
        Assert.Equal("eng04", engine.Part.Code);
        Assert.Equal(3, engine.Part.Ordinal);
        Assert.Equal(engine.Part.MaxHp, engine.Health);
        Assert.Equal(3, inv.Nodes.Count(n => n.Part.Type == 2));           // the old 261ci is gone, not in the van
        Assert.Equal(2, inv.In(PartState.Van, 0).Count(n => n.Part.Code == "eng01"));
        Assert.All(inv.Nodes.Where(n => n.State == PartState.Car), n => Assert.Equal(n.Part.MaxHp, n.Health));
        Assert.Empty(inv.Repairs);
        Assert.Equal(PartState.Van, brakes.State);
        Assert.Equal(brakes.Part.MaxHp, brakes.Health);
        Assert.Equal(damagedSpare.Part.MaxHp, damagedSpare.Health);
        Assert.Null(inv.Validate());
        Assert.Equal(3, inv.ToCar().Engine);                               // vehscn.vcf's ENG NUM, what T13 builds

        inv.WakeUp(db);                                                     // entering again changes nothing
        Assert.Equal(3, inv.Nodes.Count(n => n.Part.Type == 2));
        Assert.Equal("eng04", inv.InSlot(engineSlot)!.Part.Code);
    }

    /// <summary>Tester: "you can't save your progress" -- what a bookmark keeps comes back whole.</summary>
    [Fact]
    public void ABookmarksCarComesBackWithItsPartsSlotsAndRepairOrder()
    {
        if (InstallDir is null) return;
        var (vfs, db, inv) = Stock();
        var spares = Salvaged(inv, db, "wauto_0b.wdf", 2);
        inv.Install(inv.Slots.FindIndex(s => s.Type == 5), spares[0]);
        inv.AddSalvage([new SalvageItem("eng02", 100, false)], db, () => 0);             // still in the field
        inv.InSlot(inv.Slots.FindIndex(s => s.Type == 7))!.Health = 20;
        Assert.True(inv.QueueRepair(inv.InSlot(inv.Slots.FindIndex(s => s.Type == 7))!));
        inv.Car.Armour[0] += 10;
        inv.Car.Variant = "Mine";

        var back = TripInventory.Load(inv.Save(), db, vfs)!;
        Assert.Equal(inv.Slots, back.Slots);
        Assert.Equal(inv.MakeModel, back.MakeModel);
        Assert.Equal(inv.Weight(), back.Weight());
        Assert.Equal(inv.Nodes.Select(n => (n.Part.Stem, n.Health, n.State, n.Slot, n.Saved)), back.Nodes.Select(n => (n.Part.Stem, n.Health, n.State, n.Slot, n.Saved)));
        Assert.Equal(inv.Repairs.Count, back.Repairs.Count);
        Assert.Same(back.Nodes.Single(n => n.State == PartState.Repair), back.Repairs[0]);
        Assert.Equal(("Mine", inv.Car.Armour[0]), (back.Car.Variant, back.Car.Armour[0]));
        Assert.Contains(back.Nodes, n => n.State == PartState.Field && n.Part.Stem == "eng02");
        Assert.Equal(inv.Validate(), back.Validate());
        // a launch in between (ToCar rewrites the car's tyre pairs) loses no slot
        inv.ToCar();
        var again = TripInventory.Load(inv.Save(), db, vfs)!;
        Assert.Equal(inv.Slots, again.Slots);
    }

    /// <summary>The F9 list's OP car: valid for the garage's DONE, the best parts, the heaviest guns on the Piranha's mounts.</summary>
    [Fact]
    public void TheDebugCarIsBuiltFromTheCatalogueAndPassesTheGarageChecks()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var db = PartsDatabase.Build(vfs);
        var car = DebugCar.Build(TripCar.FromVcf(vfs.TryRead("vppirna1.vcf")!), db, vfs);
        Assert.Equal((3, 3, 3), (car.Engine, car.Suspension, car.Brakes));
        Assert.Equal("gcheavy.gdf", car.Weapons.Single(w => w.Hardpoint == 2).Gdf);
        Assert.Equal("glandmin.gdf", car.Weapons.Single(w => w.Hardpoint == 0).Gdf);
        Assert.DoesNotContain(car.Weapons, w => w.Gdf == "null");
        var inv = TripInventory.FromCar(car, db, vfs);
        Assert.Null(inv.Validate());
        Assert.All(car.Armour.Concat(car.Chassis), v => Assert.Equal(DebugCar.ArmourPerZone, v));
        // it survives the vcf round trip the launch goes through
        var back = TripCar.FromVcf(car.ToVcf());
        Assert.Equal(car.Weapons.OrderBy(w => w.Hardpoint), back.Weapons.OrderBy(w => w.Hardpoint));
        Assert.Equal(car.Engine, back.Engine);
    }
}
