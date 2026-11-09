using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The guard against a pad whose rumble blocks the game: a load or a pause never switches rumble off; a pad that keeps stalling the game does.</summary>
public class RumbleStallGuardTests
{
    [Fact]
    public void A_mission_build_frame_does_not_switch_rumble_off()
    {
        var g = new RumbleStallGuard();
        // the build: six seconds in one frame, tick 0's engine rumble sent inside it, the loading card up (not in play)
        Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(now: 10, ms: 6164, explainedMs: 5609, vibrated: true, inPlay: false));
        // the same frame even if the game called it play: the build's own measured time explains it
        Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(now: 20, ms: 6164, explainedMs: 5609, vibrated: true, inPlay: true));
        Assert.False(g.Tripped);
    }

    [Fact]
    public void Long_frames_out_of_play_never_count()
    {
        // a pause, the briefing, a cut-scene's movie, another window in front: as long and as often as they come
        var g = new RumbleStallGuard();
        for (int i = 0; i < 20; i++) Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(i, 5000, 0, vibrated: true, inPlay: false));
        Assert.False(g.Tripped);
    }

    [Fact]
    public void One_stall_in_play_is_only_noted()
    {
        var g = new RumbleStallGuard();
        Assert.Equal(RumbleStallGuard.Verdict.Noted, g.Frame(100, 2000, 0, true, true));
        Assert.False(g.Tripped);
    }

    [Fact]
    public void A_pad_that_stalls_the_game_again_soon_switches_rumble_off()
    {
        var g = new RumbleStallGuard();
        Assert.Equal(RumbleStallGuard.Verdict.Noted, g.Frame(100, 2000, 1, true, true));
        Assert.Equal(RumbleStallGuard.Verdict.Tripped, g.Frame(104, 2000, 1, true, true));
        Assert.True(g.Tripped);
        Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(110, 9000, 0, true, true));   // once is enough
    }

    [Fact]
    public void Hitches_far_apart_do_not_switch_rumble_off()
    {
        var g = new RumbleStallGuard();
        Assert.Equal(RumbleStallGuard.Verdict.Noted, g.Frame(100, 2000, 0, true, true));
        Assert.Equal(RumbleStallGuard.Verdict.Noted, g.Frame(100 + RumbleStallGuard.RepeatSeconds + 5, 2000, 0, true, true));
        Assert.False(g.Tripped);
    }

    [Fact]
    public void A_stall_with_no_vibration_before_it_or_a_short_one_is_not_the_pad()
    {
        var g = new RumbleStallGuard();
        Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(100, 5000, 0, vibrated: false, inPlay: true));
        Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(101, RumbleStallGuard.StallMs - 1, 0, true, true));
        Assert.Equal(RumbleStallGuard.Verdict.None, g.Frame(102, 2500, 1200, true, true));   // 1.3 s unexplained
        Assert.False(g.Tripped);
    }

    [Fact]
    public void Changing_the_setting_switches_rumble_back_on()
    {
        var g = new RumbleStallGuard();
        g.Frame(100, 2000, 0, true, true);
        g.Frame(101, 2000, 0, true, true);
        Assert.True(g.Tripped);
        g.Reset();
        Assert.False(g.Tripped);
        Assert.Equal(RumbleStallGuard.Verdict.Noted, g.Frame(200, 2000, 0, true, true));   // starts over: a single stall is noted again
    }

    [Fact]
    public void Each_level_holds_the_motors_briefly_and_is_renewed_before_it_runs_out()
    {
        // a hang, a long load or a crash leaves the motors running this long at most
        Assert.True(ThrottledRumble.Hold <= 1);
        Assert.True(ThrottledRumble.Renew + ThrottledRumble.MinGap < ThrottledRumble.Hold);
    }
}
