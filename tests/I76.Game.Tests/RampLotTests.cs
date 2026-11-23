using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// A model placed with the Ramp or Bridge class id keeps its decks drawn, but what of it lies on the ground (its lot, the SGEO
/// nodes of type 0x51; a floor at its foot; all of a flat intersection piece) is painted into the terrain as a grounded
/// building's is. Drawn, such a face fought the terrain for depth and flickered (mission makers' reports: every building with a
/// parking area placed as a ramp; the intersection pieces and the factory buildings' floors placed as ramps or bridges).
/// </summary>
public class RampLotTests
{
    private static Placement Place(string def, uint type)
    {
        var raw = new byte[0x78];
        BitConverter.GetBytes(type).CopyTo(raw, 0x5c);
        return new Placement(new PlacedObject(def[..^4], 0, [1, 0, 0, 0, 1, 0, 0, 0, 1], Vector3.Zero, raw), def);
    }

    private static IEnumerable<(string Part, I76.Formats.Geo.GeoFace Face)> Painted(Placement p, AssembledModel m, bool groundList = true) =>
        GroundDecals.PaintedFaces(p, m, groundList).Select(x => (x.Part.NodeName, x.Face));

    [InstallTheory]
    [InlineData("bfparad1.sdf", "FP1_LOT1")]
    [InlineData("bfsince1.sdf", "FS1_LOT1")]
    [InlineData("bcaik6.sdf", "CA1_LT61")]
    public void ARampClassBuildingsLotIsPaintedAndTheRestDrawn(string def, string lot)
    {
        var m = new ModelAssembler(TestInstall.Vfs).Assemble(def)!;
        float lowest = GroundDecals.LowestY(m);
        foreach (uint type in new uint[] { 0xb, 0xc })
        {
            var p = Place(def, type);
            Assert.True(GroundDecals.Applies(p));
            Assert.EndsWith(SceneryGroup.Ground, SceneryGroup.Key(p));
            var painted = Painted(p, m).ToList();
            Assert.Contains(painted, x => x.Part.Equals(lot, StringComparison.OrdinalIgnoreCase));
            // what stands above the ground (its walls, roofs, decks) stays in its mesh
            foreach (var part in m.Parts)
                foreach (var f in part.Mesh.Faces.Where(f => !GroundDecals.IsGroundFace(part, f, lowest)))
                    Assert.DoesNotContain((part.NodeName, f), painted);
        }
    }

    /// <summary>
    /// The factory buildings (T12's bcaik_s3, s7, s8) placed as a ramp or a bridge: the grey floor inside the door is painted as
    /// the forecourt is, so neither flickers against the terrain; the walls are drawn.
    /// </summary>
    [InstallTheory]
    [InlineData("bcaik_s3.sdf", "CA1_IN36", "CA1_LT36")]
    [InlineData("bcaik_s7.sdf", "CA1_IN76", "CA1_LT76")]
    [InlineData("bcaik_s8.sdf", "CA1_IN86", "CA1_LT86")]
    public void ARampClassFactoryBuildingsFloorIsPainted(string def, string inner, string lot)
    {
        var m = new ModelAssembler(TestInstall.Vfs).Assemble(def)!;
        foreach (uint type in new uint[] { 0xb, 0xc })
        {
            var parts = Painted(Place(def, type), m).Select(x => x.Part).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(new[] { inner, lot }.Order(StringComparer.OrdinalIgnoreCase), parts.Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// An intersection piece (a flat plane at its origin's height) placed as a ramp or a bridge is painted whole, as it is under its
    /// own class id and as Struct1: nothing of it is left to fight the terrain (a mission maker's list of pieces that flickered).
    /// </summary>
    [InstallTheory]
    [InlineData("idisect2.sdf")]
    [InlineData("iga21_1.sdf")]
    [InlineData("ila23_2.sdf")]
    [InlineData("iyisect8.sdf")]
    [InlineData("ica21_1.sdf")]
    [InlineData("ieisect1.sdf")]
    public void ARampClassIntersectionPieceIsPaintedWhole(string def)
    {
        var m = new ModelAssembler(TestInstall.Vfs).Assemble(def)!;
        int faces = m.Parts.Where(m.Drawn).Sum(x => x.Mesh.Faces.Length);
        Assert.True(faces > 0);
        foreach (uint type in new uint[] { 0x50, 2, 0xb, 0xc })
            Assert.Equal(faces, Painted(Place(def, type), m).Count());
    }

    /// <summary>A ramp has no face on the ground: nothing of it is painted, every face drawn.</summary>
    [InstallFact]
    public void ARampWithoutALotPaintsNothing()
    {
        var ramp = new ModelAssembler(TestInstall.Vfs).Assemble("aaramp1.sdf")!;
        Assert.Empty(ramp.Lots);
        Assert.Empty(Painted(Place("aaramp1.sdf", 0xb), ramp));
        Assert.True(GroundDecals.Applies(Place("bfparad1.sdf", 2)));
    }
}
