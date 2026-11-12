using I76.Formats.Input;
using Xunit;

namespace I76.Game.Tests;

/// <summary>A script asking for the skip key takes every key only while it holds the camera (#175: Mondo Burger's race asks to its end).</summary>
public class CutsceneKeysTests
{
    private static readonly GameKey[] Others = [GameKey.PresetView1, GameKey.PresetView1 + 1, GameKey.PresetView1 + 10, GameKey.ToggleRearview, GameKey.SelfDestruct, GameKey.None];

    [Fact]
    public void A_cut_scene_takes_every_key_but_Esc_and_Pause()
    {
        foreach (var k in Others) Assert.True(CutsceneKeys.Swallowed(skipPolled: true, cameraHeld: true, k));
        Assert.False(CutsceneKeys.Swallowed(true, true, GameKey.MainMenu));
        Assert.False(CutsceneKeys.Swallowed(true, true, GameKey.PauseGame));
    }

    /// <summary>The camera given back while the script goes on asking for the skip key: F1..F11, the mirror and the rest act.</summary>
    [Fact]
    public void After_the_camera_is_given_back_no_key_is_taken()
    {
        foreach (var k in Others.Append(GameKey.MainMenu).Append(GameKey.PauseGame))
            Assert.False(CutsceneKeys.Swallowed(skipPolled: true, cameraHeld: false, k));
    }

    [Fact]
    public void Without_the_skip_test_nothing_is_taken()
    {
        foreach (var k in Others) Assert.False(CutsceneKeys.Swallowed(skipPolled: false, cameraHeld: true, k));
    }
}
