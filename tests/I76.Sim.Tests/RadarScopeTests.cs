using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

public class RadarScopeTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

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
    public void TheSweepShowsThirtyFramesATurn()
    {
        // the step table cycles 0..29: every frame ZRADF000..029 shows once, back to 0 after 30 steps of 0.2 s, 6 s a turn
        var seen = new HashSet<int>();
        int cur = 0;
        for (int i = 0; i < RadarScope.SweepFrames; i++) { seen.Add(cur); cur = RadarScope.NextSweep(cur); }
        Assert.Equal(0, cur);
        Assert.Equal(30, seen.Count);
        Assert.Contains(29, seen);
    }

    [Fact]
    public void ABlipPingsOnEveryPassOfTheSweep()
    {
        // a blip ahead-right (45 degrees): one ping window a turn holds it, turn after turn
        var (x, z) = (0.3f, 0.3f);
        int cur = 0, pings = 0;
        for (int step = 0; step < 3 * RadarScope.SweepFrames; step++)
        {
            int prev = cur; cur = RadarScope.NextSweep(cur);
            if (RadarScope.InPingWedge(prev, cur, x, z)) pings++;
        }
        Assert.Equal(3, pings);
    }

    [Fact]
    public void ThePingWindowsUseTwentyNineStepsSoTheLastOneIsEmpty()
    {
        // 2π/29 a step: the 28 -> 29 window ends at 2π, and 29 -> 0 holds only the bearing 0
        Assert.True(RadarScope.InPingWedge(28, 29, -0.01f, 1));
        Assert.False(RadarScope.InPingWedge(29, 0, -0.01f, 1));
        Assert.False(RadarScope.InPingWedge(29, 0, 0.01f, 1));
        // 90 degrees is 7.25 steps of 2π/29, not 7.5 as the beam's frames are
        Assert.True(RadarScope.InPingWedge(7, 8, 1, 0));
        Assert.False(RadarScope.InPingWedge(6, 7, 1, 0));
    }

    [Fact]
    public void AWindowWhoseStartIsPastItsEndHoldsNothing()
    {
        // each end is taken into (-π, π] on its own and compared as it is: the 14 -> 15 step crosses the bottom of the
        // scope (15 steps of 2π/29 are past π) and pings nothing, whatever lies behind the car
        Assert.False(RadarScope.InPingWedge(14, 15, 0.01f, -1));
        Assert.False(RadarScope.InPingWedge(14, 15, -0.01f, -1));
        Assert.True(RadarScope.InPingWedge(13, 14, 0.2f, -1));
    }

    [Fact]
    public void ALockedRadarsWindowRunsFromTheStoppedSweepToTheTarget()
    {
        // the sweep stopped on step 2; the target just behind abeam right (frame 8): a car at 45 degrees lies in the window,
        // one behind does not; with the target ahead-left (frame 24, past π) the window's start is past its end: nothing
        int target = RadarScope.LockedFrame(1, -0.3f);
        Assert.Equal(8, target);
        Assert.True(RadarScope.InPingWedge(2, target, 0.3f, 0.3f));
        Assert.False(RadarScope.InPingWedge(2, target, 0, -1));
        Assert.False(RadarScope.InPingWedge(2, RadarScope.LockedFrame(-1, 0.5f), 0.3f, 0.3f));
    }

    [Fact]
    public void TheLedListsACarPastTheShortRangesCircle()
    {
        // at the 100 m range a car 300 m ahead is off the scope but on the 600 m list, which lights the LED and growls
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var me = new Vehicle { Flags = VehicleFlags.EngineOn };
        var near = new Vehicle { Pos = new Double3(0, 0, 300), Flags = VehicleFlags.EngineOn };
        var far = new Vehicle { Pos = new Double3(0, 0, 650), Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(me); world.Vehicles.Add(near); world.Vehicles.Add(far);
        var c = new Combat(world, _ => null);
        Assert.Null(RadarScope.Project(Facing(0), default, near.Pos.ToVector3(), 100));
        Assert.True(c.IsContact(me, near));
        Assert.False(c.IsContact(me, far));
    }

    [Fact]
    public void TheWeaponCameraClearsTheMasksIndexOneCornersAndNothingElse() =>
        Assert.Equal([true, false, false, true], RadarScope.WeaponCameraClear([1, 255, 40, 1]));
}
