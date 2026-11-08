using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The rear lamps (#10, measured in the real game on M01's Piranha): lights and brake off, no lamp face shows (the body's
/// ribbed lens); braking, the lit brake lamp; lights on, the dim tail lamp, the headlamps' child node.
/// </summary>
public class RearLampTests
{
    [Fact]
    public void TheTailLampIsOutWithTheLightsOff() => Assert.Equal((false, false), LampSupport.RearFaces(braking: false, lights: false, frontOk: true, hasBrake: true));

    [Fact]
    public void BrakingShowsTheLitBrakeLampOnly()
    {
        Assert.Equal((true, false), LampSupport.RearFaces(braking: true, lights: false, frontOk: true, hasBrake: true));
        Assert.Equal((true, false), LampSupport.RearFaces(braking: true, lights: true, frontOk: true, hasBrake: true));
    }

    [Fact]
    public void TheTailLampFollowsTheHeadlamps()
    {
        Assert.Equal((false, true), LampSupport.RearFaces(braking: false, lights: true, frontOk: true, hasBrake: true));
        // the front under half its armour puts the headlamps out, and their child the tail lamp with them
        Assert.Equal((false, false), LampSupport.RearFaces(braking: false, lights: true, frontOk: false, hasBrake: true));
        // a car with no brake lamp part keeps its tail lamp while braking
        Assert.Equal((false, true), LampSupport.RearFaces(braking: true, lights: true, frontOk: true, hasBrake: false));
    }

    [Fact]
    public void TheBrakeLampFollowsTheThrottleAsTheOriginalsDoes()
    {
        // below -0.2 it is lit; the tick the throttle rises it is out
        Assert.True(LampSupport.BrakeLit(-0.5f, 0, hold: false));
        Assert.False(LampSupport.BrakeLit(-0.1f, 0, hold: false));
        Assert.False(LampSupport.BrakeLit(0.5f, 50, hold: false));
    }

    [Fact]
    public void TheModernLookHoldsTheBrakeLampAMoment()
    {
        Assert.True(LampSupport.BrakeLit(0.5f, 149, hold: true));
        Assert.False(LampSupport.BrakeLit(0.5f, 150, hold: true));
        Assert.False(LampSupport.BrakeLit(0.5f, null, hold: true));
    }
}
