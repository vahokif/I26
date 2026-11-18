using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Game.Boot;
using I76.Game.Sim;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// A helicopter's in-car view (#177): the interior's blade parts are drawn apart from the cab, so the modern look shows its
/// rotor discs there and the 1997 look the original's blades, as outside.
/// </summary>
public class CabRotorTests
{
    [InstallFact]
    public void TheCobrasCabLeavesTheBladesToTheRotors()
    {
        // (the Huey's interior has no blade parts: its in-car view shows no rotor in either look)
        var interior = new ModelAssembler(TestInstall.Vfs) { Detail = '5' }.Assemble("vugcobr1.vcf");
        Assert.NotNull(interior);
        var blades = interior!.Parts.Where(CarNode.IsRotor).ToList();
        Assert.NotEmpty(blades);
        Assert.DoesNotContain(interior.Parts, p => Main.CabPart(interior, p) && CarNode.IsRotor(p));
        Assert.Contains(interior.Parts, p => Main.CabRotorPart(interior, p));
        Assert.Contains(interior.Parts, p => Main.CabPart(interior, p));
        // the in-car discs: one about the vertical (the main rotor) at least
        var discs = CarNode.Rotors(interior, p => Main.CabRotorPart(interior, p));
        Assert.Contains(discs, r => r.Axis == 1);
        Assert.Equal(interior.Parts.Count(p => Main.CabRotorPart(interior, p)), discs.Count);
    }
}
