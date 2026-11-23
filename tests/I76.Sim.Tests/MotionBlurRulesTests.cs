using System.Numerics;
using I76.Sim.Missions;
using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The modern look's motion blur: a fixed shutter whatever the frame rate, its clamp, and the frames that are cuts.</summary>
public class MotionBlurRulesTests
{
    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(144f)]
    [InlineData(240f)]
    public void AStreakIsAsLongAtAnyFrameRate(float fps)
    {
        // something crossing the screen at 600 px a second moves 600 / fps px a frame; scaled, it streaks the shutter's worth
        float perFrame = 600f / fps;
        float streak = perFrame * MotionBlurRules.Scale(1f / fps);
        Assert.Equal(600f * MotionBlurRules.DefaultShutter * MotionBlurRules.Reference, streak, 3);
    }

    [Fact]
    public void TheShutterIsASixtiethOfASecond()
    {
        Assert.Equal(1f, MotionBlurRules.Scale(1f / 60f), 4);
        Assert.Equal(0.5f, MotionBlurRules.Scale(1f / 30f), 4);
        Assert.Equal(144f / 60f, MotionBlurRules.Scale(1f / 144f), 4);   // a 144 fps frame moves 60/144 as far: scaled up to the same streak
    }

    [Fact]
    public void AHitchStreaksAsATenthOfASecondWould() =>
        Assert.Equal(MotionBlurRules.Scale(MotionBlurRules.MaxFrame), MotionBlurRules.Scale(2f));

    [Fact]
    public void APausedOrBrokenFrameTimeStreaksNoMore()
    {
        Assert.Equal(MotionBlurRules.Scale(MotionBlurRules.MinFrame), MotionBlurRules.Scale(0f));
        Assert.Equal(0f, MotionBlurRules.Scale(float.NaN));
    }

    [Theory]
    [InlineData(1080, 48.6f)]
    [InlineData(2160, 97.2f)]   // a 200 % Render Scale of 1080p: the same share of the picture
    [InlineData(720, 32.4f)]
    public void TheClampIsAShareOfTheHeight(int height, float px) => Assert.Equal(px, MotionBlurRules.MaxPixels(height), 3);

    private static readonly Vector3 Fwd = new(0, 0, -1), Up = Vector3.UnitY;

    [Fact]
    public void DrivingIsNotACut() =>
        Assert.False(MotionBlurRules.IsCut(Vector3.Zero, Fwd, Up, 1.3f, new Vector3(0, 0, -1.5f), Fwd, Up, 1.3f, 1f / 30f));   // 45 m/s

    [Fact]
    public void ARespawnIsACut() =>
        Assert.True(MotionBlurRules.IsCut(Vector3.Zero, Fwd, Up, 1.3f, new Vector3(12, 0, 0), Fwd, Up, 1.3f, 1f / 30f));

    [Fact]
    public void ATurnOfMoreThanFortyFiveDegreesInAFrameIsACut()
    {
        var turned = Vector3.Normalize(new Vector3(1, 0, -0.9f));
        var turnedLess = Vector3.Normalize(new Vector3(0.5f, 0, -1f));
        Assert.True(MotionBlurRules.IsCut(Vector3.Zero, Fwd, Up, 1.3f, Vector3.Zero, turned, Up, 1.3f, 1f / 30f));
        Assert.False(MotionBlurRules.IsCut(Vector3.Zero, Fwd, Up, 1.3f, Vector3.Zero, turnedLess, Up, 1.3f, 1f / 30f));
    }

    [Fact]
    public void TheBinocularsAreACut() =>
        Assert.True(MotionBlurRules.IsCut(Vector3.Zero, Fwd, Up, 1.3f, Vector3.Zero, Fwd, Up, 10.4f, 1f / 30f));

    [Fact]
    public void ANewCameraWithNoPastIsACut() =>
        Assert.True(MotionBlurRules.IsCut(Vector3.Zero, Fwd, Up, 0f, Vector3.Zero, Fwd, Up, 1.3f, 1f / 30f));

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]   // still for eight frames: the effect rests
    public void ItRestsAfterEightStillFrames(int quiet, bool idle) => Assert.Equal(idle, MotionBlurRules.Idle(quiet, 8));

    [Theory]
    [InlineData(null, true)]    // a file from before the key, or a new player: on
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TheSettingIsOnUnlessTurnedOff(bool? stored, bool on) => Assert.Equal(on, MotionBlurRules.From(stored));

    [Fact]
    public void TheScriptCameraCountsItsCutsNotItsTravel()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        var path = new List<Vector3> { new(0, 0, 0), new(0, 0, 100), new(0, 0, 200) };
        cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], path);
        cam.Update(0.036f);
        int first = cam.Cuts;
        for (int i = 0; i < 10; i++)
        {
            cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], path);   // the same shot, issued every frame
            cam.Update(0.036f);
        }
        Assert.Equal(first, cam.Cuts);
        cam.Set("camTransObj", [0, 300, 1550, 0], [null, null, null, null], [new(500, 0, 0), new(500, 0, 100)]);   // another shot
        cam.Update(0.036f);
        Assert.Equal(first + 1, cam.Cuts);
    }
}
