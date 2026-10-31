using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Tester: "when you choose the wheels some cars get another car's wheel models". The garage rewrites the first seven characters
/// of a tyre part's stem to the car's own wheel stem, so any tyre picked on any car is that car's wheel model
/// in the size picked. Every stock car of both installs, every tyre part in the install, every tyre slot.
/// </summary>
[Collection("AddonFiles")]   // the scratch car goes through the process-wide AssetVfs.Addon
public class TyreChoiceWheelTests
{
    private static IEnumerable<AssetVfs> Installs()
    {
        if (TestInstall.Dir is not null) yield return TestInstall.Vfs;
        if (NitroFactAttribute.Dir is not null) yield return new AssetVfs(new GameInstall(NitroFactAttribute.Dir));
    }

    private static bool Named(string n) => n.Length > 0 && !n.Equals("null", StringComparison.OrdinalIgnoreCase);

    [InstallFact]
    public void AnyTyreOnAnyStockCarIsThatCarsWheelModel()
    {
        int cars = 0, picks = 0;
        foreach (var vfs in Installs())
        {
            var db = PartsDatabase.Build(vfs);
            var tyres = db.Parts.Where(p => p.Type == 5 && !p.IsEmpty).ToList();
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var stock = TripCar.FromVcf(vfs.Read(n), c => VehicleFactory.EngineCompId(vfs, c));
                if (!Named(stock.Wdf[0]) || !vfs.Exists(stock.Vdf)) continue;
                var baseModel = new ModelAssembler(vfs).Assemble(n);
                if (baseModel is null) continue;
                cars++;
                foreach (var tyre in tyres)
                {
                    var inv = TripInventory.FromCar(stock, db, vfs);
                    var node = new PartNode(tyre, tyre.MaxHp, PartState.Van);
                    inv.Nodes.Add(node);
                    var slots = inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => x.i).ToList();
                    foreach (var s in slots)
                    {
                        // a fresh node per slot, as every part is its own node
                        var each = new PartNode(tyre, tyre.MaxHp, PartState.Van);
                        inv.Nodes.Add(each);
                        inv.Install(s, each);
                    }
                    var car = inv.ToCar();
                    for (int axle = 0; axle < 3; axle++)
                    {
                        if (!Named(stock.Wdf[axle])) { Assert.False(Named(car.Wdf[axle]), $"{n} axle {axle}"); continue; }
                        // the car's stem in the size picked; the armadillo's wheel comes in one size, which it keeps
                        var want = stock.Wdf[axle][..7] + tyre.Stem[7..];
                        Assert.Equal(vfs.Exists(want) ? want : stock.Wdf[axle], car.Wdf[axle], ignoreCase: true);
                    }
                    // and the model built from the file the shell writes
                    const string Scratch = "zztyre.vcf";
                    AssetVfs.Addon[Scratch] = car.ToVcf();
                    try
                    {
                        var m = new ModelAssembler(vfs).Assemble(Scratch);
                        Assert.NotNull(m);
                        Assert.True(baseModel.WheelGeos.Keys.Order().SequenceEqual(m!.WheelGeos.Keys.Order()), $"{n} with {tyre.Stem}: wheels [{string.Join(",", m.WheelGeos.Keys.Order())}] for {string.Join("/", car.Wdf)}");
                        foreach (var (slot, g) in baseModel.WheelGeos)
                            Assert.True(m.WheelGeos[slot].Intact.Name == g.Intact.Name, $"{n} with {tyre.Stem}: slot {slot} draws {m.WheelGeos[slot].Intact.Name}, the car's own is {g.Intact.Name}");
                    }
                    finally { AssetVfs.Addon.TryRemove(Scratch, out _); }
                    picks++;
                }
            }
        }
        Assert.True(cars > 60 && picks > 1000, $"{cars} cars, {picks} picks");
    }

    /// <summary>The tester's report: "16in Billets on the Jefferson gave the Corvelle's wheel model".</summary>
    [InstallFact]
    public void BilletsOnTheJeffersonAreTheJeffersonsWheels()
    {
        var vfs = TestInstall.Vfs;
        var db = PartsDatabase.Build(vfs);
        var jeff = vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
            .Select(n => (n, c: TripCar.FromVcf(vfs.Read(n), c => VehicleFactory.EngineCompId(vfs, c))))
            .Where(x => x.c.Vdf.Equals("vjsovern.vdf", StringComparison.OrdinalIgnoreCase) && Named(x.c.Wdf[0])).ToList();
        Assert.NotEmpty(jeff);
        foreach (var (n, c) in jeff)
            foreach (var pick in new[] { "wauto_9d.wdf" /* the Cavera/Corvelle family's */, "wauto_0d.wdf" /* salvage */ })
            {
                var inv = TripInventory.FromCar(c, db, vfs);
                foreach (var s in inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => x.i).ToList())
                {
                    var node = new PartNode(db.ByStem(pick)!, 100, PartState.Van);
                    inv.Nodes.Add(node);
                    inv.Install(s, node);
                }
                Assert.All(inv.ToCar().Wdf.Where(Named), w => Assert.Equal(c.Wdf[0][..7] + "d.wdf", w, ignoreCase: true));
            }
    }

    /// <summary>A variant that names another car's wheel files (the 3D garage's wheel picks) builds, simulates and loads with those wheels, as written.</summary>
    [InstallFact]
    public void AVariantWithAnotherCarsWheelFilesKeepsThem()
    {
        var vfs = TestInstall.Vfs;
        var db = PartsDatabase.Build(vfs);
        var stock = TripCar.FromVcf(vfs.Read("s05vjs01.vcf"));     // a Jefferson Sovereign, wauto_2 wheels
        Assert.StartsWith("wauto_2", stock.Wdf[0]);
        var ownModel = new ModelAssembler(vfs).Assemble("s05vjs01.vcf")!;
        var foreign = TripCar.FromVcf(vfs.Read("s05vjs01.vcf"));
        foreign.Wdf = ["wauto_9d.wdf", "null", "wauto_9d.wdf"];     // 16in Billets of the Corvelle's family
        const string Scratch = "zzsaved.vcf";
        AssetVfs.Addon[Scratch] = foreign.ToVcf();
        try
        {
            var m = new ModelAssembler(vfs).Assemble(Scratch)!;
            Assert.Equal(ownModel.WheelGeos.Keys.Order(), m.WheelGeos.Keys.Order());
            // the Corvelle's wheel model, not the Jefferson's RWL2
            Assert.DoesNotContain("RWL2", m.WheelGeos[0].Intact.Name, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(ownModel.WheelGeos[0].Intact.Name, m.WheelGeos[0].Intact.Name);
            // the trip car built from it keeps them, and writes them back as they are
            var inv = TripInventory.FromCar(TripCar.FromVcf(AssetVfs.Addon[Scratch]), db, vfs);
            Assert.Equal(["wauto_9d.wdf", "null", "wauto_9d.wdf"], inv.ToCar().Wdf);
            Assert.Equal(["wauto_9d.wdf", "null", "wauto_9d.wdf"], inv.Car.Wdf);
            // and the sim's wheels are that file's
            Assert.Equal("wauto_9d.wdf", VehicleFactory.Create(vfs, Scratch).Loadout.Wdf[0], ignoreCase: true);
        }
        finally { AssetVfs.Addon.TryRemove(Scratch, out _); }
    }
}
