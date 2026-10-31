using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

public class RadarScopeTests
{
    private static Rows Facing(float yawDeg)
    {
        float y = yawDeg * MathF.PI / 180;
        var f = new Vector3(MathF.Sin(y), 0, MathF.Cos(y));
        return new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
    }

    [Fact]
    public void AContactAheadAndRightLandsUpAndRightOnTheScope()
    {
        var rot = Facing(0);
        var p = RadarScope.Project(rot, new Vector3(10, 0, 10), new Vector3(10, 0, 10) + rot.Forward * 300 + rot.Right * 150, 600);
        Assert.NotNull(p);
        Assert.Equal(0.25f, p!.Value.X, 3);
        Assert.Equal(0.5f, p.Value.Z, 3);
    }

    [Fact]
    public void TheScopeIsHeadingUpWhateverTheCarFaces()
    {
        // facing +X: a contact at +X is dead ahead, one at -Z (this frame's right) is to the right
        var rot = Facing(90);
        var ahead = RadarScope.Project(rot, default, new Vector3(100, 0, 0), 600)!.Value;
        var right = RadarScope.Project(rot, default, new Vector3(0, 0, -100), 600)!.Value;
        Assert.Equal(0, ahead.X, 3); Assert.True(ahead.Z > 0);
        Assert.Equal(0, right.Z, 3); Assert.True(right.X > 0);
    }

    [Fact]
    public void ABlipPastTheRangeCircleIsDropped() =>
        Assert.Null(RadarScope.Project(Facing(0), default, new Vector3(500, 0, 500), 600));

    [Theory]
    [InlineData(0, 100, 0)]        // dead ahead
    [InlineData(100, 0, 7)]        // abeam right: 90 degrees = frame 7.5 truncated
    [InlineData(0, -100, 15)]      // behind: 180 degrees
    [InlineData(-100, 0, 22)]      // abeam left: 270 degrees
    [InlineData(-1, 100, 29)]      // just left of ahead wraps to the last frame
    [InlineData(0, 0, 0)]          // on top of the car: the scope takes 0
    public void ALockedRadarShowsTheZradbFrameOfTheTargetsBearing(float x, float z, int frame) =>
        Assert.Equal(frame, RadarScope.LockedFrame(x, z));

    [Fact]
    public void TheWeaponCameraClearsTheMasksIndexOneCornersAndNothingElse() =>
        Assert.Equal([true, false, false, true], RadarScope.WeaponCameraClear([1, 255, 40, 1]));
}
