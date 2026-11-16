namespace I76.Sim.Session;

/// <summary>
/// The port's auto-reverse option, at the input layer (the sim and the wire stay the original's): the
/// brake key held on a stopped car shifts into reverse and backs up, the accelerate key on a reversing car
/// brakes, then shifts to drive and goes forward (a pad's throttle axis alike, its sign flipped). It rewrites the keys and adds the original's own
/// Reverse command, so the gear flips as with the manual key. Off, nothing passes through here.
/// </summary>
public sealed class AutoReverse
{
    /// <summary>Below this speed along the heading (m/s, about 1.5 mph) the car counts as stopped.</summary>
    public const float StopSpeed = 0.7f;
    /// <summary>The analog throttle's travel (of ±32767) under which it is neither accelerate nor brake.</summary>
    public const short AnalogDead = 3277;
    /// <summary>Seconds after a shift in which another is not made (the gear takes a tick or a round trip to show): 8 ticks of the 28 Hz tick.</summary>
    public const float HoldSeconds = 0.288f;
    /// <summary>That, in ticks of the session's rate.</summary>
    public int HoldTicks { get; }
    private int _hold;
    private bool _lifted;

    public AutoReverse(SimRate? rate = null) => HoldTicks = System.Math.Max(1, (int)MathF.Round(HoldSeconds / (rate ?? SimRate.Default).TickSeconds));

    /// <param name="input">The keys as pressed.</param>
    /// <param name="reversing">The car is in reverse gear.</param>
    /// <param name="forwardSpeed">Velocity along the car's heading, negative when rolling backwards.</param>
    /// <param name="throttle">The car's throttle as the last tick left it (−1 full brake … 1 full throttle).</param>
    public CarInput Apply(CarInput input, bool reversing, float forwardSpeed, float throttle = 0f)
    {
        if (_hold > 0) _hold--;
        bool lifted = _lifted;
        _lifted = false;
        // a pad's throttle axis counts as the keys past a tenth of its travel (the triggers' accelerate and brake), and a swap flips its sign
        bool analog = input.Has(CarButtons.Analog);
        bool up = analog ? input.Throttle > AnalogDead : input.Has(CarButtons.ThrottleUp);
        bool down = analog ? input.Throttle < -AnalogDead : input.Has(CarButtons.ThrottleDown);
        if (up == down) return input;
        bool shift = false, swap = false;
        if (!reversing)
        {
            // brake key on a stopped car: into reverse, and the key drives
            if (down && forwardSpeed < StopSpeed && _hold == 0) shift = swap = true;
        }
        else if (down)
        {
            // the brake key in reverse backs up, unless the car is still rolling forward: then it brakes
            if (forwardSpeed <= StopSpeed) swap = true;
        }
        else
        {
            // accelerate in reverse: brake while backing up, then into drive and go
            if (forwardSpeed < -StopSpeed || _hold > 0) swap = true;
            else shift = true;
        }
        if (swap && analog) input.Throttle = (short)System.Math.Clamp(-input.Throttle, -32767, 32767);
        else if (!analog && (swap || shift))
        {
            // the keys' throttle ramps a tick at a time and only a tick with neither key sets it to 0 (KeyRamp): turned
            // round from a held brake (−1) the other key takes two ticks to bring it back past 0, so the tick that turns it
            // holds neither, as a player lifting one key before pressing the other
            bool drive = swap ? down : up;
            var keys = drive ? CarButtons.ThrottleUp : CarButtons.ThrottleDown;
            // (once: a network client's car may not show the lifted tick yet)
            if (!lifted && (drive ? throttle + KeyRamp.UpPerTick <= 0 : throttle - KeyRamp.DownPerTick >= 0)) { keys = CarButtons.None; _lifted = true; }
            input.Buttons = (input.Buttons & ~(CarButtons.ThrottleUp | CarButtons.ThrottleDown)) | keys;
        }
        if (shift) { input.Commands |= CarCommands.Reverse; _hold = HoldTicks; }
        return input;
    }
}
