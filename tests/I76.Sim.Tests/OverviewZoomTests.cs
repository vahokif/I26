using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class OverviewZoomTests
{
    [Fact]
    public void HeldPageDownRampsTheHeightUpAndPageUpDown()
    {
        var z = new OverviewZoom();
        int first = -1;
        for (int i = 1; i <= 15; i++) { int d = z.Frame(true, false, 2.5f, 20000); if (d > 0 && first < 0) first = i; }
        Assert.Equal(15, first);                               // ramp 144/frame: delta 1 at 15 frames (0.25 s)
        Assert.True(z.Height > 100f);
        for (int i = 0; i < 200; i++) z.Frame(true, false, 2.5f, 20000);
        Assert.Equal(17000f, z.Height);                        // held to 0.85 * far
        Assert.Equal(10, z.Frame(true, false, 2.5f, 20000));   // the ramp tops out at 0x5555: delta 10
        z.Frame(false, false, 2.5f, 20000);                    // released: the ramp starts over
        Assert.Equal(-1, z.Frame(false, true, 2.5f, 20000));   // (the arithmetic shift floors: -144 → -1)
        for (int i = 0; i < 400; i++) z.Frame(false, true, 2.5f, 20000);
        Assert.Equal(10f, z.Height);                           // held to 4 * radius
    }
}
