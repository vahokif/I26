using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class MouseLookTests
{
    [Fact]
    public void OffDoesNothing() => Assert.Equal((0f, 0f), MouseLook.Orbit(100, -50, MouseLook.Off, false));

    [Fact]
    public void RightSwingsTheCameraToTheCarsLeftAndUpDropsIt()
    {
        var (yaw, pitch) = MouseLook.Orbit(100, -50, MouseLook.Normal, false);
        Assert.True(yaw < 0);     // the camera's yaw target goes down: it moves round to the left, the view turns right
        Assert.True(pitch < 0);   // up the screen lowers the camera
        Assert.Equal(-0.3f, yaw, 4);
    }

    [Fact]
    public void InvertYFlipsOnlyThePitch()
    {
        var a = MouseLook.Orbit(10, 10, MouseLook.Normal, false);
        var b = MouseLook.Orbit(10, 10, MouseLook.Normal, true);
        Assert.Equal(a.Yaw, b.Yaw);
        Assert.Equal(a.Pitch, -b.Pitch);
    }

    [Fact]
    public void FasterLevelsTurnFurther()
    {
        float Yaw(int l) => System.Math.Abs(MouseLook.Orbit(10, 0, l, false).Yaw);
        Assert.True(Yaw(1) < Yaw(2) && Yaw(2) < Yaw(3));
    }
}
