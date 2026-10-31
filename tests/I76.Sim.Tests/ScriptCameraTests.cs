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
}
