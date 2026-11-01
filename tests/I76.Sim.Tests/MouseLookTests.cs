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

    [Fact]
    public void FineSensitivityScalesFromTenToTwoHundredPercentInTenPercentSteps()
    {
        Assert.Equal((0f, 0f), MouseLook.Orbit(100, -50, false, 1.0f, false));
        Assert.Equal(-0.03f, MouseLook.Orbit(100, 0, true, 0.1f, false).Yaw, 4);
        Assert.Equal(-0.15f, MouseLook.Orbit(100, 0, true, 0.5f, false).Yaw, 4);
        Assert.Equal(-0.30f, MouseLook.Orbit(100, 0, true, 1.0f, false).Yaw, 4);
        Assert.Equal(-0.60f, MouseLook.Orbit(100, 0, true, 2.0f, false).Yaw, 4);

        Assert.Equal(0.1f, MouseLook.ClampSensitivity(0.01f), 4);
        Assert.Equal(2.0f, MouseLook.ClampSensitivity(5.0f), 4);
        Assert.Equal(1.1f, MouseLook.NextSensitivity(1.0f, 1), 4);
        Assert.Equal(0.9f, MouseLook.NextSensitivity(1.0f, -1), 4);
        Assert.Equal(2.0f, MouseLook.NextSensitivity(2.0f, 1, wrap: false), 4);
        Assert.Equal(0.1f, MouseLook.NextSensitivity(2.0f, 1, wrap: true), 4);
        Assert.Equal(2.0f, MouseLook.NextSensitivity(0.1f, -1, wrap: true), 4);
    }

    [Fact]
    public void LegacyMouseCameraLevelsMigrateToExactSensitivityRates()
    {
        Assert.Equal(0.5f, MouseLook.SensitivityFromLevel(MouseLook.Slow), 4);
        Assert.Equal(1.0f, MouseLook.SensitivityFromLevel(MouseLook.Normal), 4);
        Assert.Equal(2.0f, MouseLook.SensitivityFromLevel(MouseLook.Fast), 4);
        Assert.Equal(MouseLook.Off, MouseLook.LevelFromSensitivity(false, 1.0f));
        Assert.Equal(MouseLook.Slow, MouseLook.LevelFromSensitivity(true, 0.5f));
        Assert.Equal(MouseLook.Normal, MouseLook.LevelFromSensitivity(true, 1.0f));
        Assert.Equal(MouseLook.Fast, MouseLook.LevelFromSensitivity(true, 2.0f));
    }

    [Fact]
    public void CockpitMouseLookTurnsHeadRightOnMouseRightAndReturnsOrHolds()
    {
        var head = new MouseCockpitOffset();
        var (oy, op) = MouseLook.Orbit(100, -50, true, 1.0f, false);
        head.Step(0.016f, autoReturn: true, keyHeld: false, oy, op);
        Assert.True(head.Yaw > 0);     // moving mouse right turns pilot's head right (+yaw)
        Assert.True(head.Pitch < 0);   // moving mouse up tilts pilot's head up (-pitch)
        float startYaw = head.Yaw;

        // with autoReturn off, the head stays where the mouse left it
        head.Step(5.0f, autoReturn: false, keyHeld: false, 0, 0);
        Assert.Equal(startYaw, head.Yaw, 4);

        // with autoReturn on, waits ReturnDelay then eases back to forward
        head.Step(1.0f, autoReturn: true, keyHeld: false, 0, 0);
        Assert.Equal(startYaw, head.Yaw, 4);
        head.Step(1.0f, autoReturn: true, keyHeld: false, 0, 0);
        Assert.True(head.Yaw < startYaw && head.Yaw > 0);

        // holding a glance key clears the mouse offset immediately
        head.Step(0.016f, autoReturn: true, keyHeld: true, 0, 0);
        Assert.False(head.HasOffset);
    }
}
