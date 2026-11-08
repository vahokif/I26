using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

public class EnhancedRumbleTests
{
    private const double Dt = 0.0498;     // a 20 Hz tick

    private static RumbleEvents Idle(int gear = 1) => new() { EngineOn = true, Gear = gear, Rpm = 1050 };

    private sealed class Sink : IRumbleSink
    {
        public readonly List<(float W, float S)> Calls = [];
        public int Stops;
        public void Rumble(float weak, float strong) => Calls.Add((weak, strong));
        public void Stop() => Stops++;
    }

    [Fact]
    public void An_idle_car_is_silent()
    {
        var r = new EnhancedRumble();
        for (int i = 0; i < 5; i++) Assert.Equal((0f, 0f), r.Step(Idle(), Dt));
    }

    [Fact]
    public void Nitro_kicks_once_when_it_comes_on_and_fades()
    {
        var r = new EnhancedRumble();
        r.Step(Idle(), Dt);
        var e = Idle(); e.Nitro = true;
        var (_, strong) = r.Step(e, Dt);
        Assert.Equal(EnhancedRumble.NitroKick.Strong, strong);
        for (int i = 0; i < 12; i++) strong = r.Step(e, Dt).Strong;   // still held: no second kick
        Assert.Equal(0f, strong);
    }

    [Fact]
    public void A_car_already_on_nitro_at_the_first_sample_does_not_kick()
    {
        var r = new EnhancedRumble();
        var e = Idle(); e.Nitro = true; e.Handbrake = true; e.Cranking = true;
        Assert.Equal((0f, 0f), r.Step(e, Dt));
    }

    [Fact]
    public void The_handbrake_thuds_on_the_pull_and_again_after_a_release()
    {
        var r = new EnhancedRumble();
        r.Step(Idle(), Dt);
        var e = Idle(); e.Handbrake = true;
        Assert.Equal(EnhancedRumble.HandbrakeThud.Strong, r.Step(e, Dt).Strong);
        Assert.True(r.Step(e, Dt).Strong < EnhancedRumble.HandbrakeThud.Strong);   // 80 ms: fading in the second tick
        Assert.Equal(0f, r.Step(e, Dt).Strong);                       // gone in the third, and not renewed by the hold
        r.Step(Idle(), Dt);
        Assert.Equal(EnhancedRumble.HandbrakeThud.Strong, r.Step(e, Dt).Strong);
    }

    [Fact]
    public void A_gear_change_clicks_the_small_motor_only()
    {
        var r = new EnhancedRumble();
        r.Step(Idle(2), Dt);
        var (weak, strong) = r.Step(Idle(3), Dt);
        Assert.Equal(EnhancedRumble.GearClick.Weak, weak);
        Assert.Equal(0f, strong);
        r.Step(Idle(3), Dt);
        Assert.Equal(0f, r.Step(Idle(3), Dt).Weak);                   // 50 ms: over after two ticks
    }

    [Fact]
    public void The_starter_thumps_when_it_begins()
    {
        var r = new EnhancedRumble();
        r.Step(new RumbleEvents(), Dt);
        var e = new RumbleEvents { Cranking = true, Gear = 1 };
        Assert.Equal(EnhancedRumble.Crank.Strong, r.Step(e, Dt).Strong);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, true)]
    [InlineData(0u, false)]
    [InlineData(9u, false)]
    public void Shots_have_a_pulse_by_weapon_kind(uint cls, bool has) => Assert.Equal(has, EnhancedRumble.ShotPulse(cls) is not null);

    [Fact]
    public void Heavy_weapons_use_the_big_motor_and_machine_guns_the_small_one()
    {
        Assert.Equal(0f, EnhancedRumble.ShotPulse(1)!.Value.Strong);
        Assert.True(EnhancedRumble.ShotPulse(2)!.Value.Strong > 0.5f);
        Assert.True(EnhancedRumble.ShotPulse(3)!.Value.Strong > EnhancedRumble.ShotPulse(2)!.Value.Strong);
    }

    [Fact]
    public void A_shot_in_the_frame_pulses_and_it_ends()
    {
        var r = new EnhancedRumble();
        r.Step(Idle(), Dt);
        var e = Idle(); e.Shots.Add(new WeaponShot(0, 2, 1, 1));
        Assert.Equal(EnhancedRumble.ShotPulse(2)!.Value.Strong, r.Step(e, Dt).Strong);
        r.Step(Idle(), Dt);
        r.Step(Idle(), Dt);
        Assert.Equal(0f, r.Step(Idle(), Dt).Strong);                  // 120 ms: over after three ticks
    }

    [Fact]
    public void The_growl_follows_rpm_and_is_light()
    {
        Assert.Equal(0f, EnhancedRumble.Growl(1050));
        Assert.True(EnhancedRumble.Growl(3000) < EnhancedRumble.Growl(4000));
        Assert.Equal(0.2f, EnhancedRumble.Growl(9000), 4);
        var r = new EnhancedRumble();
        var e = Idle(); e.Rpm = 4500;
        Assert.Equal(0.2f, r.Step(e, Dt).Weak, 4);
        e.EngineOn = false;
        Assert.Equal(0f, r.Step(e, Dt).Weak);
    }

    [Fact]
    public void Strength_is_held_to_25_to_150_and_steps_round()
    {
        Assert.Equal(25, RumbleStrength.Clamp(0));
        Assert.Equal(150, RumbleStrength.Clamp(900));
        Assert.Equal(1.5f, RumbleStrength.Factor(150));
        Assert.Equal(125, RumbleStrength.Next(100));
        Assert.Equal(25, RumbleStrength.Next(150));
        Assert.Equal(50, RumbleStrength.Next(25));
    }

    [Fact]
    public void The_mix_scales_every_level_and_holds_the_sum_to_one()
    {
        var sink = new Sink();
        var mix = new RumbleMix(sink) { Strength = 0.5f };
        mix.Rumble(0.6f, 0.2f);
        Assert.Equal((0.3f, 0.1f), sink.Calls[^1]);
        mix.SetExtra(0.8f, 1f);
        Assert.Equal(0.7f, sink.Calls[^1].W, 4);
        Assert.Equal(0.6f, sink.Calls[^1].S, 4);
        mix.Strength = 1.5f;
        mix.SetExtra(0.8f, 1f);
        Assert.Equal((1f, 1f), sink.Calls[^1]);
    }

    [Fact]
    public void The_mix_stops_only_when_both_the_effects_and_the_pulses_are_quiet()
    {
        var sink = new Sink();
        var mix = new RumbleMix(sink);
        mix.Rumble(0.5f, 0.5f);
        mix.SetExtra(0.4f, 0f);
        mix.Stop();                                  // the effects end; the pulse still plays
        Assert.Equal(0, sink.Stops);
        Assert.Equal((0.4f, 0f), sink.Calls[^1]);
        mix.SetExtra(0f, 0f);
        Assert.Equal(1, sink.Stops);
        mix.SetExtra(0f, 0f);
        Assert.Equal(1, sink.Stops);
    }

    [Fact]
    public void The_mix_renews_a_held_pulse_each_tick_so_the_throttle_can_pass_it_on()
    {
        var sink = new Sink();
        var mix = new RumbleMix(sink);
        mix.SetExtra(0.2f, 0f);
        mix.SetExtra(0.2f, 0f);
        Assert.Equal(2, sink.Calls.Count);
    }
}
