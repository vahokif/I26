using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The F2 chase view's starting framing (docs/deviations.md): the original's 30 degrees and zoom 3, the port's lower and
/// nearer 16 degrees and zoom 1.7, Play Options' Original Chase Framing row choosing between them, and the 1997 look always
/// choosing the original's. The tests that drive the row and the look drive the two statics they set and put them back
/// afterwards, so they live in this one class (xUnit runs the methods of a class one after another, classes side by side).
/// </summary>
public class ChaseFramingTests
{
    /// <summary>The two framings as the game keeps them: radians. 30° and 16°.</summary>
    private const float Thirty = 0.5235988f, Sixteen = 0.2839724f;

    [Fact] public void TheOriginalOpensAtThirtyDegrees() => Assert.Equal(Thirty, ChaseFraming.OriginalPitch, 5);
    [Fact] public void TheOriginalOpensAtZoomThree() => Assert.Equal(3f, ChaseFraming.OriginalZoom);
    [Fact] public void ThePortOpensAtSixteenDegrees() => Assert.Equal(Sixteen, ChaseFraming.PortPitch, 5);
    [Fact] public void ThePortOpensAtZoomOnePointSeven() => Assert.Equal(1.7f, ChaseFraming.PortZoom);

    /// <summary>The original's own numbers: its track_pitch on a01 is 30 degrees and its track_distance 3.</summary>
    [Fact]
    public void TheOriginalsPitchIsThirtyDegrees() =>
        Assert.Equal(30f, ChaseFraming.OriginalPitch * (180f / MathF.PI), 3);

    /// <summary>The port's is the 16 degrees its comment and docs call it, to the tenth of a degree (the constant is 16.27).</summary>
    [Fact]
    public void ThePortsPitchIsSixteenDegrees() =>
        Assert.Equal(16.3f, ChaseFraming.PortPitch * (180f / MathF.PI), 1);

    /// <summary>The port's is the lower and nearer of the two, which is the difference the row switches back.</summary>
    [Fact]
    public void ThePortSitsLowerAndNearerThanTheOriginal() =>
        Assert.True(ChaseFraming.PortPitch < ChaseFraming.OriginalPitch && ChaseFraming.PortZoom < ChaseFraming.OriginalZoom);

    /// <summary>Nobody asked for anything: the view opens at the port's framing, which is what every settings file so far meant.</summary>
    [Fact]
    public void ANewPlayerGetsThePortsFraming()
    {
        Assert.False(ChaseFraming.Original);
        Assert.False(ChaseFraming.Mode1997);
        Assert.Equal((Sixteen, 1.7f), (ChaseFraming.Pitch, ChaseFraming.Zoom));
    }

    /// <summary>Both numbers move together: the row never leaves a view at the original's distance and the port's height.</summary>
    [Theory]
    [InlineData(false, Sixteen, 1.7f)]
    [InlineData(true, Thirty, 3f)]
    public void TheRowPicksBothNumbers(bool original, float pitch, float zoom)
    {
        var was = (ChaseFraming.Original, ChaseFraming.Mode1997);
        try
        {
            (ChaseFraming.Original, ChaseFraming.Mode1997) = (original, false);
            Assert.Equal((pitch, zoom), (ChaseFraming.Pitch, ChaseFraming.Zoom));
        }
        finally { (ChaseFraming.Original, ChaseFraming.Mode1997) = was; }
    }

    /// <summary>The 1997 look opens at the original's framing whatever the row says: its frame is the original's own picture, and it
    /// has no cloud shadows and no haze a lower, nearer view would be there to clear.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The1997LookOpensAtTheOriginalsFraming(bool original)
    {
        var was = (ChaseFraming.Original, ChaseFraming.Mode1997);
        try
        {
            (ChaseFraming.Original, ChaseFraming.Mode1997) = (original, true);
            Assert.Equal((Thirty, 3f), (ChaseFraming.Pitch, ChaseFraming.Zoom));
            Assert.True(ChaseFraming.UsesOriginal);
        }
        finally { (ChaseFraming.Original, ChaseFraming.Mode1997) = was; }
    }

    /// <summary>The look never writes the row: the choice the 1997 look drew past is what the modern look finds on the next F12.</summary>
    [Fact]
    public void The1997LookLeavesTheRowAlone()
    {
        var was = (ChaseFraming.Original, ChaseFraming.Mode1997);
        try
        {
            (ChaseFraming.Original, ChaseFraming.Mode1997) = (false, true);
            Assert.Equal((Thirty, 3f), (ChaseFraming.Pitch, ChaseFraming.Zoom));
            ChaseFraming.Mode1997 = false;
            Assert.Equal((Sixteen, 1.7f), (ChaseFraming.Pitch, ChaseFraming.Zoom));
        }
        finally { (ChaseFraming.Original, ChaseFraming.Mode1997) = was; }
    }

    /// <summary>The framing a view opens at is inside the reach of the track keys (pitch 1°..90°, zoom clamped 1..10).</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EveryFramingIsInsideTheKeysReach(bool original, bool mode1997)
    {
        var was = (ChaseFraming.Original, ChaseFraming.Mode1997);
        try
        {
            (ChaseFraming.Original, ChaseFraming.Mode1997) = (original, mode1997);
            Assert.InRange(ChaseFraming.Pitch, ChaseOrbit.MinPitch, ChaseOrbit.MaxPitch);
            Assert.InRange(ChaseFraming.Zoom, 1f, 10f);
        }
        finally { (ChaseFraming.Original, ChaseFraming.Mode1997) = was; }
    }
}
