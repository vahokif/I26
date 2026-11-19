using I76.Formats.Imaging;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The respray's colour list is one list for both CONFIGURE CHASSIS forms (#205): the 3D garage's RESPRAY and the 2D form's COLOUR row offer
/// the same 24 paints and name a colour the same way, so a colour picked in either shows under the same name in the other.
/// </summary>
public class CarPaintsNamesTests
{
    [Fact]
    public void TheClassicPaintsAreTwentyFourDistinctColours()
    {
        Assert.Equal(24, CarPaints.Classic.Length);
        Assert.Equal(24, CarPaints.Classic.Select(c => c.Colour).Distinct().Count());
        Assert.Equal(24, CarPaints.Classic.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AColourIsNamedFactoryClassicOrCustom()
    {
        Assert.Equal("Factory", CarPaints.NameOf(null));
        Assert.Equal("Grabber Blue", CarPaints.NameOf(new PaintColour(0x1E, 0x9B, 0xE0)));
        // a colour mixed by hue, saturation and brightness in the 3D garage
        Assert.Equal("Custom", CarPaints.NameOf(new PaintColour(0x1E, 0x9B, 0xE1)));
    }
}
