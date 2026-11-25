using I76.Game;
using I76.Sim.Views;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The wheel's zoom lasts as long as the view it was made in: a mission or a switch into a view starts at the original's framing.</summary>
[Collection("MouseCameraStatics")]
public class ViewZoomResetTests
{
    [Fact] public void ResetAllZoomsRestoresEveryViewAndCountsAnEpoch()
    {
        MouseCamera.WheelView = ViewZoom.Kind.Chase;
        MouseCamera.Wheel(3);
        MouseCamera.WheelView = ViewZoom.Kind.Overhead;
        MouseCamera.Wheel(-2);
        Assert.NotEqual(ViewZoom.Default, MouseCamera.ZoomOf(ViewZoom.Kind.Chase));
        Assert.NotEqual(ViewZoom.Default, MouseCamera.ZoomOf(ViewZoom.Kind.Overhead));
        int epoch = MouseCamera.ZoomEpoch;
        MouseCamera.ResetAllZooms();
        for (int i = 0; i < ViewZoom.Count; i++) Assert.Equal(ViewZoom.Default, MouseCamera.Zooms[i]);
        Assert.Equal(epoch + 1, MouseCamera.ZoomEpoch);
        MouseCamera.WheelView = null;
    }

    [Fact] public void TheWheelStillZoomsWithinAView()
    {
        MouseCamera.ResetAllZooms();
        MouseCamera.WheelView = ViewZoom.Kind.Chase;
        MouseCamera.Wheel(1);
        Assert.True(MouseCamera.ZoomOf(ViewZoom.Kind.Chase) < 1f);
        MouseCamera.ResetZoom(ViewZoom.Kind.Chase);
        Assert.Equal(1f, MouseCamera.ZoomOf(ViewZoom.Kind.Chase));
        MouseCamera.WheelView = null;
    }

    [Fact] public void OpeningAndClosingTheMenuIsNoViewSwitch()
    {
        // F2 in a mission, then the Esc menu over it, then back: the remembered view stands and nothing resets (#190)
        var (v, reset) = MouseCamera.TrackView(ViewZoom.Kind.Chase, null, overlaid: true, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.False(reset);
        (v, reset) = MouseCamera.TrackView(v, ViewZoom.Kind.Chase, overlaid: false, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.False(reset);
    }

    [Fact] public void AViewSwitchStillResetsTheZoom()
    {
        var (v, reset) = MouseCamera.TrackView(ViewZoom.Kind.Chase, ViewZoom.Kind.Overhead, overlaid: false, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Overhead, v);
        Assert.True(reset);
        // View Zoom Reset off: the switch is remembered but nothing resets
        (v, reset) = MouseCamera.TrackView(ViewZoom.Kind.Overhead, ViewZoom.Kind.Chase, overlaid: false, keepZoom: true);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.False(reset);
        // a switch made under a pause counts once the pause ends
        (v, reset) = MouseCamera.TrackView(ViewZoom.Kind.Chase, ViewZoom.Kind.Side, overlaid: true, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.False(reset);
        (v, reset) = MouseCamera.TrackView(v, ViewZoom.Kind.Side, overlaid: false, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Side, v);
        Assert.True(reset);
    }

    [Fact] public void TheHandBackFromACutSceneIsNoViewSwitch()
    {
        // F2, then a jump's camera holds the view (no view of the player's on screen), then hands F2 back: nothing resets (#222)
        var (v, reset) = MouseCamera.TrackView(ViewZoom.Kind.Chase, null, overlaid: false, keepZoom: false, held: true);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.False(reset);
        (v, reset) = MouseCamera.TrackView(v, ViewZoom.Kind.Chase, overlaid: false, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.False(reset);
        // a cut-scene that hands back another view than it took (the target view it cannot restore: the chase view) is a switch
        (v, reset) = MouseCamera.TrackView(ViewZoom.Kind.Target, null, overlaid: false, keepZoom: false, held: true);
        (v, reset) = MouseCamera.TrackView(v, ViewZoom.Kind.Chase, overlaid: false, keepZoom: false);
        Assert.Equal(ViewZoom.Kind.Chase, v);
        Assert.True(reset);
    }

    [Fact] public void TheWheelZoomOutlastsAJumpCamera()
    {
        // the frames of a jump on T05 in F2, as Main tracks them: the wheel's zoom is the player's again after the jump's camera (#222)
        MouseCamera.ResetAllZooms();
        MouseCamera.WheelView = ViewZoom.Kind.Chase;
        MouseCamera.Wheel(-4);
        float zoomed = MouseCamera.ZoomOf(ViewZoom.Kind.Chase);
        int epoch = MouseCamera.ZoomEpoch;
        ViewZoom.Kind? v = ViewZoom.Kind.Chase;
        foreach (var (now, held) in new (ViewZoom.Kind?, bool)[] { (ViewZoom.Kind.Chase, false), (null, true), (null, true), (ViewZoom.Kind.Chase, false) })
        {
            (v, bool reset) = MouseCamera.TrackView(v, now, overlaid: false, keepZoom: false, held);
            if (reset) MouseCamera.ResetAllZooms();
        }
        Assert.Equal(zoomed, MouseCamera.ZoomOf(ViewZoom.Kind.Chase));
        Assert.Equal(epoch, MouseCamera.ZoomEpoch);
        MouseCamera.WheelView = null;
    }

    [Fact] public void SideMouseButtonsResolveToMouseDeviceAndNotJoystick()
    {
        Assert.Contains("Mouse4Btn", Controls.MouseNames);
        Assert.Contains("Mouse5Btn", Controls.MouseNames);
        Assert.Equal(("Mouse", "Mouse4Btn"), Controls.InputOf("Mouse4Btn"));
        Assert.Equal(("Mouse", "Mouse5Btn"), Controls.InputOf("Mouse5Btn"));
        Assert.Equal(("joystick1", "Button4"), Controls.InputOf("Button4"));
    }

    [Fact] public void WheelCycleWeaponDisablesWheelZoomWhenEnabled()
    {
        MouseCamera.ResetAllZooms();
        MouseCamera.WheelView = ViewZoom.Kind.Chase;
        MouseCamera.WheelCycleWeapon = true;
        try
        {
            Assert.False(MouseCamera.WheelActive);
            MouseCamera.Wheel(2);
            Assert.Equal(1f, MouseCamera.ZoomOf(ViewZoom.Kind.Chase));
        }
        finally
        {
            MouseCamera.WheelCycleWeapon = false;
            MouseCamera.WheelView = null;
        }
        MouseCamera.WheelView = ViewZoom.Kind.Chase;
        Assert.True(MouseCamera.WheelActive);
        MouseCamera.WheelView = null;
    }
}
