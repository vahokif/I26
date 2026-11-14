using I76.Formats.Vegetation;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's plants fade in beyond the headlamps' reach (seen at night: bushes materialising in the headlights): at
/// every Vegetation level, for the beam's visible reach and for longer ones, each kind of bush (the shrubs, the ground layer's
/// larger plants, the rolling tumbleweeds) is wholly undrawn until past the lamps' reach plus the chase view's offset.
/// </summary>
public class VegReachTests
{
    // the modern look's beam, and shorter and longer ones (what a different beam would ask), at Low and High
    public static TheoryData<int, float> Cases
    {
        get
        {
            var d = new TheoryData<int, float>();
            foreach (float r in new[] { VegLevels.LampReach, 56f, 126f, 163f, 201f })
                foreach (int level in new[] { 1, 2 }) d.Add(level, r);
            return d;
        }
    }

    [Fact]
    public void TheVisibleReachLiesInsideTheRangeAndGrowsWithIt()
    {
        float reach = VegLevels.LampReach;
        // the modern look's beam (110 m under a 0.6 fall-off) lights visibly to about 90 m
        Assert.InRange(reach, 85f, 95f);
        Assert.True(reach < I76.Game.Sim.CarNode.HeadRange);
        // at the reach the light is the visible share of the light just ahead of the hood
        float near = VegLevels.Falloff(VegLevels.Near, I76.Game.Sim.CarNode.HeadRange, I76.Game.Sim.CarNode.HeadAttenuation);
        Assert.Equal(VegLevels.VisibleShare, VegLevels.Falloff(reach, I76.Game.Sim.CarNode.HeadRange, I76.Game.Sim.CarNode.HeadAttenuation) / near, 3);
        Assert.True(VegLevels.VisibleReach(150f, 0.6f) > reach && VegLevels.VisibleReach(110f, 0.4f) > reach);
    }

    [Fact]
    public void TheAppearDistanceIsTheReachPlusTheMargin()
    {
        Assert.True(VegLevels.LampMargin >= 15f);
        foreach (float r in new[] { 80f, 126f, 201f })
            Assert.Equal(r + VegLevels.LampMargin, VegLevels.BushAppear(r));
    }

    [Theory, MemberData(nameof(Cases))]
    public void ShrubsFadeInBeyondTheLamps(int level, float lamp)
    {
        var l = VegLevels.For(level, lamp);
        Assert.True(l.ShrubFade >= VegLevels.BushAppear(lamp), $"level {level}, lamps {lamp} m: shrubs fully drawn from {l.ShrubFade} m");
        Assert.True(l.ShrubEnd > l.ShrubFade);
    }

    [Theory, MemberData(nameof(Cases))]
    public void TheGroundLayersLargerPlantsFadeInBeyondTheLamps(int level, float lamp)
    {
        var l = VegLevels.For(level, lamp);
        Assert.True(l.ForbFade >= VegLevels.BushAppear(lamp), $"level {level}, lamps {lamp} m: forbs fully drawn from {l.ForbFade} m");
        Assert.True(l.ForbEnd > l.ForbFade);
        Assert.Equal(l.ForbEnd, l.GroundEnd);
    }

    [Theory, MemberData(nameof(Cases))]
    public void AtNightTheShrubsFarMeshStartsBeyondTheLamps(int level, float lamp)
    {
        // the far mesh (a quarter of the clusters) filling in to the middle one read as a shrub materialising in the beam
        var night = VegLevels.For(level, lamp, night: true);
        Assert.True(TierBands.Band(night.ShrubFar).Start >= VegLevels.BushAppear(lamp) - 0.01f, $"level {level}, lamps {lamp} m: far mesh from {TierBands.Band(night.ShrubFar).Start} m");
        Assert.True(TierBands.Band(night.ShrubFar).Start >= night.ShrubEnd - 0.01f);
        // by day the level's own boundary stands
        Assert.Equal((level >= 2 ? VegLevels.High : VegLevels.Low).ShrubFar, VegLevels.For(level, lamp).ShrubFar);
    }

    [Theory, MemberData(nameof(Cases))]
    public void RollingTumbleweedsFadeInBeyondTheLamps(int level, float lamp)
    {
        float range = VegLevels.TumbleRange(level, lamp);
        var (start, end) = TumbleweedField.FadeIn(range);
        Assert.True(start >= VegLevels.BushAppear(lamp), $"level {level}, lamps {lamp} m: tumbleweeds fully drawn from {start} m");
        Assert.True(end > start && end < range);
    }

    [Theory, MemberData(nameof(Cases))]
    public void TheLevelsOwnReachHoldsWhereTheLampsAllow(int level, float lamp)
    {
        var own = level >= 2 ? VegLevels.High : VegLevels.Low;
        var l = VegLevels.For(level, lamp);
        Assert.True(l.ShrubFade >= own.ShrubFade && l.ForbFade >= own.ForbFade);
        Assert.Equal(own.ShrubEnd - own.ShrubFade, l.ShrubEnd - l.ShrubFade, 3);
        // the grass tufts keep their short reach (their cost); they grow in out of the sward instead (vegetation.gdshader grow_in)
        Assert.Equal((own.GrassFade, own.GrassEnd), (l.GrassFade, l.GrassEnd));
    }

    [Fact]
    public void TheOldReachFailedTheRule()
    {
        // before: the ground layer's larger plants fully drawn from 35 m on Low (60 m on High), inside the beam's visible reach (90 m);
        // Low's rolling tumbleweeds (a 150 m range) fully drawn from 105 m, inside the reach plus the margin
        float reach = VegLevels.LampReach;
        Assert.True(35f < reach && 60f < reach);
        Assert.True(TumbleweedField.FadeIn(150f).Start < VegLevels.BushAppear(reach));
    }
}
