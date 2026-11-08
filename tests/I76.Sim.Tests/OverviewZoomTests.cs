using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class OverviewZoomTests
{
    const float T60 = 1f / 60f;
    [Fact]
    public void HeldPageDownRampsTheHeightUpAndPageUpDown()
    {
        var z = new OverviewZoom();
        int first = -1;
        for (int i = 1; i <= 15; i++) { int d = z.Frame(true, false, 2.5f, 20000, T60); if (d > 0 && first < 0) first = i; }
        Assert.Equal(15, first);                               // ramp 144/frame: delta 1 at 15 frames (0.25 s)
        Assert.True(z.Height > 100f);
        for (int i = 0; i < 200; i++) z.Frame(true, false, 2.5f, 20000, T60);
        Assert.Equal(17000f, z.Height);                        // held to 0.85 * far
        Assert.Equal(10, z.Frame(true, false, 2.5f, 20000, T60));   // the ramp tops out at 0x5555: delta 10
        z.Frame(false, false, 2.5f, 20000, T60);                    // released: the ramp starts over
        Assert.Equal(-1, z.Frame(false, true, 2.5f, 20000, T60));   // (the arithmetic shift floors: -144 → -1)
        for (int i = 0; i < 400; i++) z.Frame(false, true, 2.5f, 20000, T60);
        Assert.Equal(10f, z.Height);                           // held to 4 * radius
    }

    [Fact]
    public void ARampStepRunsPerSimTickWithTheTicksDt()
    {
        // 20 Hz: dt = 102/2048 s, ftol(182*dt) = 9, so the ramp grows 432 a tick and the delta reaches 1 (pos 2048) at tick 5
        float tick = 102f / 2048f;
        var z = new OverviewZoom();
        int first = -1;
        for (int i = 1; i <= 12; i++) { int d = z.Frame(true, false, 2.5f, 20000, tick); if (d > 0 && first < 0) first = i; }
        Assert.Equal(5, first);
        // the height after 12 ticks follows the ramp 432 a tick (delta 1 from tick 5, 2 from tick 10)
        float h = 100f;
        for (int i = 1; i <= 12; i++) h *= 1f + (int)(((long)System.Math.Min(432 * i, 0x5555) * 32) >> 16) * 0.01f;
        Assert.Equal(h, z.Height, 3);
    }
}
