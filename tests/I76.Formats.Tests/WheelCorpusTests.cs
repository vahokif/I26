using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Every .vcf of the base game and the Nitro Pack: the wheels the port builds are the wheels the file names, slot by slot
/// (the VCFC front axle -> WLOC 0/1, middle -> 2/3, rear -> 4/5; "null" is no axle), on the side the WLOC says.
/// </summary>
[Collection("AddonFiles")]
public class WheelCorpusTests
{
    private static IEnumerable<AssetVfs> Installs()
    {
        if (TestInstall.Dir is not null) yield return TestInstall.Vfs;
        if (NitroFactAttribute.Dir is not null) yield return new AssetVfs(new GameInstall(NitroFactAttribute.Dir));
    }

    private static bool Named(string n) => n.Length > 0 && !n.Equals("null", StringComparison.OrdinalIgnoreCase);

    [InstallFact]
    public void EveryVariantBuildsTheWheelsItsFileNames()
    {
        int cars = 0;
        var missing = new List<string>();
        foreach (var vfs in Installs())
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var vcf = new Bwd2File(vfs.Read(n));
                var c = vcf.First("VCFC")!.Value;
                var m = new ModelAssembler(vfs).Assemble(n);
                if (m is null) { missing.Add(n); continue; }
                var expect = new List<int>();
                for (int axle = 0; axle < 3; axle++)
                {
                    var wdf = Bin.CStr(vcf.Data, c.PayloadOffset + 0x36 + 13 * axle, 13);
                    if (!Named(wdf)) continue;
                    expect.Add(axle * 2); expect.Add(axle * 2 + 1);
                    // the wheel is the one the .wdf's own WGEO holds: right wheels RWL on even slots, left LWL on odd
                    foreach (var slot in new[] { axle * 2, axle * 2 + 1 })
                    {
                        var (geo, _, node) = m.WheelGeos[slot];
                        Assert.Contains(slot % 2 == 0 ? "RWL" : "LWL", geo.Name, StringComparison.OrdinalIgnoreCase);
                        float wx = m.Locations.First(l => l.Tag == "WLOC" && l.Index == slot).Transform.Translation[0];
                        var xs = geo.Vertices.Select(v => node.Apply(v).X).ToList();
                        float mid = (xs.Min() + xs.Max()) / 2;
                        Assert.True(mid * wx >= 0 || Math.Abs(wx) < 0.2f, $"{n} slot {slot} is on the wrong side");
                    }
                }
                Assert.True(expect.SequenceEqual(m.WheelGeos.Keys.Order()), $"{n}: the file names slots [{string.Join(",", expect)}], the port builds [{string.Join(",", m.WheelGeos.Keys.Order())}]");
                cars++;
            }
        Assert.True(cars > 100, string.Join(" ", missing));
        // two stray .vcf files the games ship name a .vdf they do not (both installs)
        Assert.True(missing.All(n => n is "norlepr.vcf" or "norroyl.vcf"), "no model (its .vdf is not in the install): " + string.Join(" ", missing));
    }

    [InstallFact]
    public void TheSimHasAMiddlePairExactlyWhereTheFileNamesOne()
    {
        foreach (var vfs in Installs())
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var vcf = new Bwd2File(vfs.Read(n));
                var c = vcf.First("VCFC")!.Value;
                bool middle = Named(Bin.CStr(vcf.Data, c.PayloadOffset + 0x43, 13));
                var vdfName = vcf.VcfcFields()!.Value.Vdf;
                if (!vfs.Exists(vdfName)) continue;   // a stray .vcf whose .vdf is not shipped (the model test names them)
                var v = VehicleFactory.Create(vfs, n);
                Assert.Equal(middle, v.MiddleRight is not null && v.MiddleLeft is not null);
                if (middle)
                {
                    Assert.Equal(6, v.Wheels.Concat(v.MiddleWheels).Count());
                }
            }
    }

    [NitroFact]
    public void TheMilkTankerIsASixWheeler()
    {
        var nitro = new AssetVfs(new GameInstall(NitroFactAttribute.Dir!));
        foreach (var n in new[] { "vxmilk1.vcf", "vxmilk2.vcf", "vxmilk3.vcf", "nomilk3.vcf" })
        {
            var m = new ModelAssembler(nitro).Assemble(n)!;
            Assert.Equal([0, 1, 2, 3, 4, 5], m.WheelGeos.Keys.Order());
            var v = VehicleFactory.Create(nitro, n);
            Assert.NotNull(v.MiddleRight);
            Assert.NotNull(v.MiddleLeft);
            // the middle wheels sit between the axles, at their own WLOCs
            Assert.InRange(v.MiddleRight!.Offset.Z, v.RearRight.Offset.Z, v.FrontRight.Offset.Z);
            Assert.NotEqual(v.MiddleRight.Offset.Z, v.RearRight.Offset.Z);
            // their hit points are the damage slots 2 and 3, in the pick rows 4 and 2
            var d = I76.Sim.Damage.VehicleDamage.Create(v, v.Build!, false, new I76.Sim.Rng.CrtRand());
            Assert.Same(v.MiddleRight, (d.Slots[2] as I76.Sim.Damage.WheelPart)!.Wheel);
            Assert.Same(v.MiddleLeft, (d.Slots[3] as I76.Sim.Damage.WheelPart)!.Wheel);
            Assert.Equal(new[] { 2 }, d.Rows[4].Intersect([2]));
            Assert.Equal(new[] { 3 }, d.Rows[2].Intersect([3]));
        }
        // a bus has no middle axle
        var bus = VehicleFactory.Create(nitro, "vxbus1.vcf");
        Assert.Null(bus.MiddleRight);
        Assert.Equal(4, new ModelAssembler(nitro).Assemble("vxbus1.vcf")!.WheelGeos.Count);
    }
}
