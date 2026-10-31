using I76.Sim.Sound;
using Xunit;

namespace I76.Sim.Tests;

public class Ds3dTests
{
    [Theory]
    [InlineData(0f, 0f)]          // on top of the listener: full volume, never louder
    [InlineData(10f, 0f)]         // the min distance
    [InlineData(20f, -6.02f)]     // −6 dB per doubling past it
    [InlineData(40f, -12.04f)]
    [InlineData(400f, -32.04f)]   // the max distance
    [InlineData(2000f, -32.04f)]  // held there: a bump across the map is quiet, not silent
    public void InverseDistanceFromTen(float d, float db) =>
        Assert.Equal(db, Ds3d.AttenuationDb(d), 2);

    [Theory]
    [InlineData(50f, 0f)]
    [InlineData(100f, 0f)]
    [InlineData(200f, -6.02f)]
    [InlineData(5000f, -12.04f)]
    public void TheLoudVoicesFromAHundred(float d, float db) =>
        Assert.Equal(db, Ds3d.AttenuationDb(d, Ds3d.LoudMinDistance), 2);

    [Fact]
    public void LoopsStartUnderSixHundredMetres()
    {
        Assert.True(Ds3d.LoopStarts(599.9f));
        Assert.False(Ds3d.LoopStarts(600f));
    }

    [Theory]
    [InlineData(true, true, true)]     // the cockpit: the player's own engine, horn, wind, tyres sit at the listener (#83: centred)
    [InlineData(true, false, false)]   // a chase or other view: a 3D buffer at the car
    [InlineData(false, true, false)]   // another car's voice, whatever the view
    [InlineData(false, false, false)]
    public void ThePlayersOwnVoicesSitAtTheListenerInTheCockpit(bool playersCar, bool cockpit, bool atListener) =>
        Assert.Equal(atListener, Ds3d.AtListener(playersCar, cockpit));
}
