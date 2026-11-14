using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// What a destroyed object lays on the ground (a community mission maker's sheet: the phone booth's, the propane tank's and
/// the pipeline's flattened remains, the race-track ticket box's black square, the burnt plane were missing). The wreck's
/// mesh left out every ground-level face, taking them for the lot already painted into the terrain, so a scorch mark that
/// only the 'A' geometry has was neither drawn nor painted. The lot is still left out.
/// </summary>
public class WreckDecalTests
{
    [InstallTheory]
    [InlineData("aphoneb1.sdf", "AP1ABTH1")]
    [InlineData("arpropn1.sdf", "AR1ATNK1")]
    [InlineData("a3pline1.sdf", "A31APIP1")]
    public void AWrecksScorchMarkIsDrawn(string def, string part)
    {
        var intact = new ModelAssembler(TestInstall.Vfs).Assemble(def)!;
        var wreck = new ModelAssembler(TestInstall.Vfs) { Destroyed = true }.Assemble(def)!;
        var painted = GroundDecals.Painted(intact);
        var p = wreck.Parts.Single(x => x.NodeName.Equals(part, StringComparison.OrdinalIgnoreCase));
        float lowest = GroundDecals.LowestY(wreck);
        // the old rule (the wreck's own ground faces) left it all out
        Assert.Contains(p.Mesh.Faces, f => GroundDecals.IsGroundFace(p, f, lowest));
        Assert.DoesNotContain(p.Mesh.Faces, f => painted(p, f));
    }

    [InstallFact]
    public void AWrecksLotStaysPainted()
    {
        var intact = new ModelAssembler(TestInstall.Vfs).Assemble("bcbank1.sdf")!;
        var wreck = new ModelAssembler(TestInstall.Vfs) { Destroyed = true }.Assemble("bcbank1.sdf")!;
        var painted = GroundDecals.Painted(intact);
        var lot = wreck.Parts.Single(x => x.NodeName.Equals("CB1ALOT1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lot.Mesh.Faces, f => painted(lot, f));
    }
}
