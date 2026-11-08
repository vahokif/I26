using Godot;
using I76.Game.Camera;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The debug fly camera takes the pointer only while it is on screen (#106).</summary>
public class FlyCameraMouseTests
{
    [Fact] public void AHiddenCameraLeavesThePointerAlone()
    {
        Assert.Null(FlyCamera.LookMode(false, MouseButton.Right, true));
        Assert.Null(FlyCamera.LookMode(false, MouseButton.Right, false));
    }

    [Fact] public void TheCameraOnScreenLooksWhileTheRightButtonIsHeld()
    {
        Assert.Equal(Input.MouseModeEnum.Captured, FlyCamera.LookMode(true, MouseButton.Right, true));
        Assert.Equal(Input.MouseModeEnum.Visible, FlyCamera.LookMode(true, MouseButton.Right, false));
        Assert.Null(FlyCamera.LookMode(true, MouseButton.Left, true));
    }
}
