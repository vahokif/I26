using I76.Game.Sim;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The modern look's rear lamps' red light on the ground behind the car (RearLampLight): faint with the lights on, brighter braking, a night's only.</summary>
public class RearLampLightTests
{
    [Fact]
    public void TheRearLampsLightTheGroundFaintlyWithTheLightsOnAndMoreBraking()
    {
        // a night: the tail filament's glow, the brake's several times brighter, nothing with both off
        Assert.Equal(0f, RearLampLight.Energy(0f, 0f, 0f));
        Assert.Equal(RearLampLight.TailEnergy, RearLampLight.Energy(1f, 0f, 0f));
        Assert.Equal(RearLampLight.BrakeEnergy, RearLampLight.Energy(1f, 1f, 0f));
        Assert.Equal(RearLampLight.BrakeEnergy, RearLampLight.Energy(0f, 1f, 0f));
        Assert.True(RearLampLight.TailEnergy > 0f);
        Assert.True(RearLampLight.BrakeEnergy >= 3f * RearLampLight.TailEnergy);
        // under the old brake light's energy (1, an omni light under the car), a fraction of the headlamps' beam (14)
        Assert.True(RearLampLight.BrakeEnergy < 1f);
        // the ramp: half-way up the brake filament is half-way between the two
        Assert.Equal((RearLampLight.TailEnergy + RearLampLight.BrakeEnergy) / 2f, RearLampLight.Energy(1f, 0.5f, 0f), 5);
    }

    [Fact]
    public void TheRearLampsLightNoGroundInFullSun()
    {
        Assert.Equal(0f, RearLampLight.Energy(1f, 1f, 1f));
        Assert.Equal(0f, RearLampLight.Energy(1f, 0f, 1f));
        // dusk (a preset's sun intensity 0.625): between
        float dusk = RearLampLight.Energy(1f, 1f, Daylight.Of(true, 0.625f));
        Assert.InRange(dusk, 0.3f * RearLampLight.BrakeEnergy, 0.4f * RearLampLight.BrakeEnergy);
        Assert.Equal(0f, Daylight.Of(false, 0.9f));
    }
}
