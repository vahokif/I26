using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The interior (detail 5) of every car: its nodes carry their placement in the car's frame even when they name a parent,
/// so each part sits where the same part of the outside model (detail 1) does (#25: the Piranha's side mirrors floated
/// above the roof when composed with their parent's offset).
/// </summary>
public class CockpitPlacementTests
{
    private static IEnumerable<AssetVfs> Installs()
    {
        if (TestInstall.Dir is not null) yield return TestInstall.Vfs;
        if (NitroFactAttribute.Dir is not null) yield return new AssetVfs(new GameInstall(NitroFactAttribute.Dir));
    }

    [InstallFact]
    public void Piranha_side_mirrors_sit_on_the_doors()
    {
        var m = new ModelAssembler(TestInstall.Vfs) { Detail = '5' }.Assemble("vppirna1.vcf")!;
        foreach (var code in new[] { "MIRL", "MIRR" })
        {
            var p = m.Parts.Single(x => x.NodeName.EndsWith(code, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1.053f, p.ToModel.T.Y, 0.002f);   // the door's top, not 0.657 higher on the roof
            Assert.Equal(code == "MIRL" ? -0.83f : 0.85f, p.ToModel.T.X, 0.01f);
        }
    }

    [InstallFact]
    public void Every_interior_part_sits_where_the_outside_one_does()
    {
        int cars = 0, compared = 0;
        var wrong = new List<string>();
        foreach (var vfs in Installs())
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var inside = new ModelAssembler(vfs) { Detail = '5' }.Assemble(n);
                var outside = new ModelAssembler(vfs) { Detail = '1' }.Assemble(n);
                if (inside is null || outside is null) continue;
                cars++;
                var outer = outside.Parts.Where(p => p.NodeName.Length >= 8).GroupBy(p => p.NodeName[3..8].ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.First());
                foreach (var p in inside.Parts.Where(p => p.NodeName.Length >= 8 && p.NodeName[2] == '5'))
                {
                    if (!outer.TryGetValue(p.NodeName[3..8].ToUpperInvariant(), out var o)) continue;
                    compared++;
                    if (System.Numerics.Vector3.Distance(p.ToModel.T, o.ToModel.T) > 0.01f)
                        wrong.Add($"{n} {p.NodeName} {p.ToModel.T} vs {o.NodeName} {o.ToModel.T}");
                }
            }
        Assert.True(cars > 40, $"only {cars} cars");
        Assert.True(compared > 100, $"only {compared} parts compared");
        Assert.Empty(wrong);
    }
}
