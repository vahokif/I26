using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>LampSupport: which lamp faces a damaged body still holds.</summary>
public class LampSupportTests
{
    private static bool Lamp(ModelPart p) => p.NodeName.Length >= 8 && p.NodeName[4..8].ToUpperInvariant() is "TLGT" or "BLGT" or "HLGT";

    private static AssembledModel Rear(string def, char c) =>
        new ModelAssembler(TestInstall.Vfs) { SkipParts = [], PartCondition = part => part.ToUpperInvariant() is "BDYB" or "TLGT" or "BLGT" ? c : '1' }.Assemble(def)!;

    [InstallFact]
    public void TheCrushedPiranhaRearDropsItsTailLamps()
    {
        var intact = new ModelAssembler(TestInstall.Vfs) { SkipParts = [] }.Assemble("vppirna1.vcf")!;
        Assert.Empty(LampSupport.Floating(intact, intact, Lamp, p => !Lamp(p)));
        // level 1's rear: the PP12 lamps, byte for byte the PP11 ones, on the intact PP11BDYB
        Assert.Empty(LampSupport.Floating(Rear("vppirna1.vcf", '2'), intact, Lamp, p => !Lamp(p)));
        // levels 2 and 3: the crushed PP13BDYB leaves the same lamps in the air, every face of them
        var crushed = Rear("vppirna1.vcf", '3');
        Assert.Contains(crushed.Parts, p => p.NodeName == "PP13BDYB");
        var floating = LampSupport.Floating(crushed, intact, Lamp, p => !Lamp(p));
        var rearLamps = crushed.Parts.Where(p => p.NodeName is "PP12TLGT" or "PP12BLGT").SelectMany(p => p.Mesh.Faces.Select(f => (p, f))).ToList();
        Assert.Equal(4, rearLamps.Count);
        Assert.All(rearLamps, x => Assert.Contains(x, floating));
        Assert.DoesNotContain(floating, x => x.Part.NodeName.EndsWith("HLGT"));   // the front is whole
    }
}
