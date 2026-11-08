using System.Numerics;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

public class ScriptCameraTests
{
    /// <summary>
    /// t04's intro re-issues camTransObj every frame until camIsArrived. The
    /// original keeps its place on the path while the path
    /// stays the same; resetting it on every call left the camera at nav[0].
    /// A log of t04 in the original game has the camera 0.558 m further along
    /// per 0.036 s frame at speed 1550, its x/z eased 0.3 a frame after the point.
    /// </summary>
    [Fact]
    public void ATravellingShotKeepsItsPlaceWhenReissued()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        var path = new List<Vector3> { new(0, 0, 0), new(0, 0, 100), new(0, 0, 200) };
        for (int i = 0; i < 40; i++)
        {
            cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], path);
            cam.Update(0.036f);
        }
        // 39 steps at 0.558 put the point at 21.8; the eye stands where the camera was before this frame's
        // easing, which trails the point by about 0.7/0.3 of a step
        Assert.InRange(cam.Eye.Z, 19.5f, 21.8f);
        Assert.Equal(3f, cam.Eye.Y, 3);
        Assert.False(cam.ConsumeArrived());
    }

    [Fact]
    public void ANonArrivingPathCallClearsCamIsArrived()
    {
        // the original clears the flag on every call that places the camera: a script that arrives and starts another
        // path without asking camIsArrived reads false on its next test
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        var a = new List<Vector3> { new(0, 0, 0), new(0, 0, 10) };
        var b = new List<Vector3> { new(50, 0, 0), new(50, 0, 100) };
        bool held = false;
        for (int i = 0; i < 200 && !held; i++)
        {
            cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], a);
            cam.Update(0.036f);
            held = cam.Eye.Z > 5 && cam.PrevEye == cam.Eye;   // the arriving call places nothing
        }
        Assert.True(held);
        cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], b);
        cam.Update(0.036f);
        Assert.False(cam.ConsumeArrived());
    }

    [Fact]
    public void ItArrivesOnTheLastSegmentAndHolds()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        var path = new List<Vector3> { new(0, 0, 0), new(0, 0, 10), new(0, 0, 20) };
        bool arrived = false;
        for (int i = 0; i < 200 && !arrived; i++)
        {
            cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], path);
            cam.Update(0.036f);
            arrived = cam.ConsumeArrived();
        }
        Assert.True(arrived);
        // arrives past the end of the last segment (count <= seg + 2), the eye trailing the point
        Assert.InRange(cam.Eye.Z, 16f, 20f);
    }
    /// <summary>
    /// The view is drawn between the last two ticks' placements. A cut has nothing to blend from, also when the op stays the
    /// same: t04's intro cuts camObjObj to camObjObj between the speakers, and the frame between the two placements showed
    /// the camera halfway across, 11 to 36 m from either (#140). The same op with the same arguments, as a travelling
    /// shot is re-issued every frame, still blends.
    /// </summary>
    [Fact]
    public void ACutToANewPlacementOfTheSameOpIsNotBlended()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        var a = new Entity("a", [], 1, 0) { StaticPosition = new(0, 0, 0) };
        var b = new Entity("b", [], 1, 0) { StaticPosition = new(30, 0, 20) };
        var who = new Entity("who", [], 1, 0) { StaticPosition = new(10, 0, 10) };
        cam.Push();
        for (int i = 0; i < 3; i++) { cam.Set("camObjObj", [0, 0, 300, -500, 0], [a, null, null, null, who], null); cam.Update(0.05f); }
        Assert.Equal(cam.Eye, cam.PrevEye);
        cam.Set("camObjObj", [0, 0, 300, -500, 0], [b, null, null, null, who], null);
        cam.Update(0.05f);
        Assert.Equal(new Vector3(30, 3, 15), cam.Eye);
        Assert.Equal(cam.Eye, cam.PrevEye);
        Assert.Equal(cam.LookAt, cam.PrevLookAt);
        // other arguments to the same op are a cut too
        cam.Set("camObjObj", [0, 200, 300, -500, 0], [b, null, null, null, who], null);
        cam.Update(0.05f);
        Assert.Equal(cam.Eye, cam.PrevEye);

        // the object moving under the same placement is not a cut: that blends
        b.StaticPosition = new(31, 0, 20);
        cam.Set("camObjObj", [0, 200, 300, -500, 0], [b, null, null, null, who], null);
        cam.Update(0.05f);
        Assert.Equal(1f, (cam.Eye - cam.PrevEye).Length(), 3);
    }

    [Fact]
    public void AReissuedTravellingShotStillBlends()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        var path = new List<Vector3> { new(0, 0, 0), new(0, 0, 100), new(0, 0, 200) };
        for (int i = 0; i < 10; i++)
        {
            cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], path);
            cam.Update(0.05f);
        }
        Assert.True(cam.Eye.Z - cam.PrevEye.Z > 0.3f);
        // a new path for the same op starts at its first point: a cut
        var other = new List<Vector3> { new(500, 0, 0), new(500, 0, 100) };
        cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], other);
        cam.Update(0.05f);
        Assert.Equal(500f, cam.Eye.X, 3);
        Assert.Equal(cam.Eye, cam.PrevEye);
    }

    [Theory]
    [InlineData("camObjObj")]
    [InlineData("camObjDir")]
    [InlineData("camF12")]
    public void AFixedPlacementClearsCamIsArrived(string op)
    {
        // the fixed placements clear the flag as the path calls do: a path that arrived and is followed by one of
        // these without a camIsArrived test reads false afterwards
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        var path = new List<Vector3> { new(0, 0, 0), new(0, 0, 10) };
        bool held = false;
        for (int i = 0; i < 200 && !held; i++)
        {
            cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], path);
            cam.Update(0.036f);
            held = cam.Eye.Z > 5 && cam.PrevEye == cam.Eye;
        }
        Assert.True(held);
        var a = new Entity("a", [], 1, 0) { StaticPosition = new(0, 0, 0) };
        var b = new Entity("b", [], 1, 0) { StaticPosition = new(30, 0, 20) };
        cam.Set(op, [0, 0, 300, -500, 0, 0, 0], [a, b, null, null, b], null);
        cam.Update(0.036f);
        Assert.False(cam.ConsumeArrived());
    }
}
