using I76.Formats.Vfs;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The PARTS list of the melee forms: engine, suspension, brakes by name, then the car's tyres (not its specials).</summary>
public class MeleePartsTests
{
    [Fact]
    public void AStockVariantListsItsEngineSuspensionBrakesAndTyres()
    {
        foreach (var env in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var vfs = new AssetVfs(new GameInstall(dir));
            var db = PartsDatabase.Build(vfs);
            var car = TripCar.FromVcf(vfs.Read("vppirna1.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
            var names = car.PartNames(db, vfs.Install.IsNitro);
            Assert.Equal(7, names.Count);
            Assert.Equal(db.Parts.First(p => p.Type == 2 && p.Ordinal == car.Engine && !p.IsEmpty).Id, names[0]);
            Assert.Equal(db.Parts.First(p => p.Type == 3 && p.Ordinal == car.Suspension && !p.IsEmpty).Id, names[1]);
            Assert.Equal(db.Parts.First(p => p.Type == 4 && p.Ordinal == car.Brakes && !p.IsEmpty).Id, names[2]);
            var tyre = db.ByStem(car.Wdf[0])!;
            Assert.Equal(5, tyre.Type);
            Assert.All(names.Skip(3), t => Assert.Equal(tyre.Id, t));
            Assert.Contains("in ", tyre.Id);          // "13in Stock"
        }
    }

    [Fact]
    public void SpecialsAreNeverListed()
    {
        foreach (var env in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var vfs = new AssetVfs(new GameInstall(dir));
            var db = PartsDatabase.Build(vfs);
            int withSpecials = 0;
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)))
            {
                if (vfs.TryRead(n) is not { } d) continue;
                TripCar car;
                try { car = TripCar.FromVcf(d, e => VehicleFactory.EngineCompId(vfs, e)); } catch { continue; }
                var names = car.PartNames(db, vfs.Install.IsNitro);
                Assert.InRange(names.Count, 3, 7);
                Assert.DoesNotContain(names, x => PartsDatabase.SpecialNames.Contains(x));
                if (car.Specials.Any(s => s > 0) && !car.Wdf[0].Equals("null", StringComparison.OrdinalIgnoreCase)) { withSpecials++; Assert.Equal(7, names.Count); }
            }
            Assert.True(withSpecials > 0, env);
        }
    }
}
