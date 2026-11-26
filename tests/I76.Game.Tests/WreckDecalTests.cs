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

    /// <summary>
    /// A tunnel destroyed (a mortar takes a whole tunnel; the mission maker's sheet: nothing remains in the original): its
    /// road strips ET1_CR11 and ET1_CR21, painted into the terrain at load, have no 'A' twin, so their paint goes. A
    /// building's lot falls into a lot and stays painted; a part still standing keeps its paint.
    /// </summary>
    [InstallFact]
    public void ADestroyedPartsPaintGoesUnlessItsTwinLiesOnTheGround()
    {
        var tunnel = new ModelAssembler(TestInstall.Vfs).Assemble("betunnl1.sdf")!;
        var tunnelWreck = new ModelAssembler(TestInstall.Vfs) { DestroyedNode = _ => true }.Assemble("betunnl1.sdf");
        var gone = GroundDecals.PaintedOut(tunnel, tunnelWreck, _ => true);
        float lowest = GroundDecals.LowestY(tunnel);
        var painted = tunnel.Parts.Where(p => p.Mesh.Faces.Any(f => GroundDecals.IsGroundFace(p, f, lowest))).Select(p => p.NodeName).ToList();
        Assert.Equal(["ET1_CR11", "ET1_CR21"], painted);
        Assert.All(painted, n => Assert.True(gone(n), n));
        Assert.False(GroundDecals.PaintedOut(tunnel, tunnelWreck, _ => false)("ET1_CR11"));

        var bank = new ModelAssembler(TestInstall.Vfs).Assemble("bcbank1.sdf")!;
        var bankWreck = new ModelAssembler(TestInstall.Vfs) { DestroyedNode = _ => true }.Assemble("bcbank1.sdf");
        Assert.False(GroundDecals.PaintedOut(bank, bankWreck, _ => true)("CB1_LOT1"));
        // nothing drawn at all (a wreck of no parts): every painted part goes
        Assert.True(GroundDecals.PaintedOut(bank, null, _ => true)("CB1_LOT1"));
    }

    /// <summary>
    /// A placement thrown whole (an intersection piece or a building placed as a Sign, a splash on it): its painted ground
    /// faces go with it, so where it stood is bare ground, as the original draws it (the piece is just an object there).
    /// Its lot is not the placement's (drawn with the ground) and stays.
    /// </summary>
    [InstallFact]
    public void AThrownPlacementsPaintGoesButItsLot()
    {
        var piece = new ModelAssembler(TestInstall.Vfs).Assemble("idisect1.sdf")!;
        float lowest = GroundDecals.LowestY(piece);
        var painted = piece.Parts.Where(p => p.Mesh.Faces.Any(f => GroundDecals.IsGroundFace(p, f, lowest))).Select(p => p.NodeName).ToList();
        Assert.NotEmpty(painted);
        Assert.All(painted, n => Assert.True(GroundDecals.ThrownOut(piece)(n), n));
        var bank = new ModelAssembler(TestInstall.Vfs).Assemble("bcbank1.sdf")!;
        Assert.False(GroundDecals.ThrownOut(bank)("CB1_LOT1"));
        Assert.True(GroundDecals.ThrownOut(bank)(bank.Parts.First(p => !bank.Lots.Contains(p.NodeName)).NodeName));
    }
}
