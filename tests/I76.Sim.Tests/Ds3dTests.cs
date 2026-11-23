using System.Numerics;
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

    private static readonly Vector3 Here = Vector3.Zero, Ahead = new(0, 0, 100);

    [Theory]
    [InlineData(0f, 1f)]                 // standing: no shift
    [InlineData(-50f, 343f / 303f)]      // closing at 50 m/s, 40 after the factor 0.8: 1.132
    [InlineData(50f, 343f / 383f)]       // drawing away: 0.896
    [InlineData(-1000f, 2f)]             // limited to half the speed of sound: never above 2
    [InlineData(1000f, 2f / 3f)]         // nor below 2/3
    public void ASourceMovingAlongTheLine(float vz, float pitch) =>
        Assert.Equal(pitch, Ds3d.DopplerPitch(Ahead, new Vector3(0, 0, vz), Here, Vector3.Zero), 4);

    [Fact]
    public void OnlyTheRelativeVelocityAlongTheLineCounts()
    {
        // the listener closing on a standing source is the source closing on a standing listener
        Assert.Equal(343f / 303f, Ds3d.DopplerPitch(Ahead, Vector3.Zero, Here, new Vector3(0, 0, 50)), 4);
        // driving alongside (the chase camera behind its car): no shift
        var v = new Vector3(3, 0, 40);
        Assert.Equal(1f, Ds3d.DopplerPitch(Ahead, v, Here, v), 6);
        // passing across the line of sight: no shift at the closest point
        Assert.Equal(1f, Ds3d.DopplerPitch(Ahead, new Vector3(60, 0, 0), Here, Vector3.Zero), 6);
        // 50 m/s at 60° off the line: 25 m/s of it closes, 20 after the factor
        Assert.Equal(343f / 323f, Ds3d.DopplerPitch(Ahead, new Vector3(50f * MathF.Sqrt(3) / 2, 0, -25f), Here, Vector3.Zero), 4);
    }

    [Fact]
    public void NoShiftWithoutALine()
    {
        Assert.Equal(1f, Ds3d.DopplerPitch(Here, new Vector3(0, 0, 80), Here, Vector3.Zero));            // at the listener itself
        Assert.Equal(1f, Ds3d.DopplerPitch(Ahead, new Vector3(float.NaN, 0, 0), Here, Vector3.Zero));    // a lost velocity
        Assert.Equal(1f, Ds3d.DopplerPitch(new Vector3(float.NaN, 0, 0), Vector3.UnitZ, Here, Vector3.Zero));
    }
}
