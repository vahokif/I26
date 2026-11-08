using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class CursorPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void MenusShowTheCursor(bool chase, bool focused, bool confine) =>
        Assert.Equal(CursorMode.Visible, CursorPolicy.For(false, chase, MouseLook.Normal, focused, confine));

    /// <summary>#147: Mouse In Window kept the pointer in only while a mission hid it; in the menus it walked off to another monitor.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MouseInWindowKeepsTheMenusPointerInTheWindow(bool chase) =>
        Assert.Equal(CursorMode.Confined, CursorPolicy.For(false, chase, MouseLook.Normal, true, true));

    [Fact]
    public void ChaseViewWithMouseCameraCaptures() =>
        Assert.Equal(CursorMode.Captured, CursorPolicy.For(true, true, MouseLook.Normal, true, false));

    [Fact]
    public void InteriorViewHidesTheCursor()
    {
        Assert.Equal(CursorMode.Hidden, CursorPolicy.For(true, false, MouseLook.Normal, true, false));
        Assert.Equal(CursorMode.ConfinedHidden, CursorPolicy.For(true, false, MouseLook.Normal, true, true));
    }

    [Fact]
    public void MouseCameraOffHidesInTheChaseViewToo()
    {
        Assert.Equal(CursorMode.Hidden, CursorPolicy.For(true, true, MouseLook.Off, true, false));
        Assert.Equal(CursorMode.ConfinedHidden, CursorPolicy.For(true, true, MouseLook.Off, true, true));
    }

    [Fact]
    public void LostFocusFreesTheCursor() =>
        Assert.Equal(CursorMode.Visible, CursorPolicy.For(true, true, MouseLook.Normal, false, true));

    [Fact]
    public void CockpitViewWithMouseCockpitCapturesEvenWhenChaseMouseIsOff()
    {
        Assert.Equal(CursorMode.Captured, CursorPolicy.For(true, false, MouseLook.Off, true, false, cockpitView: true, mouseCockpit: true));
        Assert.Equal(CursorMode.Hidden, CursorPolicy.For(true, false, MouseLook.Off, true, false, cockpitView: true, mouseCockpit: false));
    }
}
