using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// A building placed with the Ramp or Bridge class id keeps its decks drawn, but its lot (its SGEO nodes of type 0x51, the
/// paved ground round it) is the ground's: the original takes it out of the model whatever the placement's class. The
/// port drew it as part of the structure, where it fought the terrain for depth and flickered (a mission maker's report:
/// every building with a parking area placed as a ramp).
/// </summary>
public class RampLotTests
{
    private static Placement Place(string def, uint type)
    {
        var raw = new byte[0x78];
        BitConverter.GetBytes(type).CopyTo(raw, 0x5c);
        return new Placement(new PlacedObject(def[..^4], 0, [1, 0, 0, 0, 1, 0, 0, 0, 1], Vector3.Zero, raw), def);
    }

    [InstallTheory]
    [InlineData("bfparad1.sdf", "FP1_LOT1")]
    [InlineData("bfsince1.sdf", "FS1_LOT1")]
    [InlineData("bcaik6.sdf", "CA1_LT61")]
    public void ARampClassBuildingsLotIsPaintedAndTheRestDrawn(string def, string lot)
    {
        var m = new ModelAssembler(TestInstall.Vfs).Assemble(def)!;
        foreach (uint type in new uint[] { 0xb, 0xc, 0xd })
        {
            var p = Place(def, type);
            Assert.False(GroundDecals.Applies(p));
            Assert.True(GroundDecals.LotsOnly(p));
            Assert.EndsWith(SceneryGroup.Lots, SceneryGroup.Key(p));
        }
        float lowest = GroundDecals.LowestY(m);
        var lotPart = m.Parts.Single(x => x.NodeName.Equals(lot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lotPart.Mesh.Faces, f => GroundDecals.IsLotFace(m, lotPart, f, lowest));
        // nothing else of it: its decks, roofs and floors stay in its mesh
        foreach (var part in m.Parts.Where(x => !m.Lots.Contains(x.NodeName)))
            Assert.DoesNotContain(part.Mesh.Faces, f => GroundDecals.IsLotFace(m, part, f, lowest));
    }

    /// <summary>A ramp without a lot paints nothing; a grounded building keeps every ground face painted.</summary>
    [InstallFact]
    public void ARampWithoutALotPaintsNothing()
    {
        var ramp = new ModelAssembler(TestInstall.Vfs).Assemble("aaramp1.sdf")!;
        Assert.Empty(ramp.Lots);
        Assert.False(GroundDecals.LotsOnly(Place("bfparad1.sdf", 2)));
        Assert.True(GroundDecals.Applies(Place("bfparad1.sdf", 2)));
    }
}
