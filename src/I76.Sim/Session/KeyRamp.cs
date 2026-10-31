namespace I76.Sim.Session;

/// <summary>
/// The keyboard's throttle and steer ramps, and the analog axes' pass-through.
/// The original adds to the throttle once per frame, so it builds faster at a
/// higher simulation rate; steering is a function of the time held. Modern
/// Physics steps its own clock and wants the throttle by time as well: the
/// original's per-tick ramp at the session's rate, as a rate per second
/// (docs/modern-physics.md "Rate").
/// </summary>
public static class KeyRamp
{
    /// <summary>Throttle added per call while up is held, before the (1 − hold/3) taper.</summary>
    public const float UpPerTick = 0.4f;
    /// <summary>Throttle taken away per call while down is held, before the taper.</summary>
    public const float DownPerTick = 0.5f;

    /// <summary>The original's throttle-up ramp at this rate, per second: 0.4 a tick.</summary>
    public static float UpPerSecond(SimRate rate) => UpPerTick / rate.TickSeconds;
    /// <summary>The original's throttle-down ramp at this rate, per second: 0.5 a tick.</summary>
    public static float DownPerSecond(SimRate rate) => DownPerTick / rate.TickSeconds;

    /// <summary>
    /// One step of dt seconds. Per-tick (<paramref name="timeBasedTick"/> null, the original): throttle up adds
    /// 0.4·(1 − t/3) and down takes away 0.5·(1 − t/3) a call. Time-based: the same per second as the original's at
    /// a tick of <paramref name="timeBasedTick"/> seconds, scaled by dt. Steering is √(t/3) of full lock by the hold
    /// time t either way, as the original's. Both clamp to ±0.9999 and go to 0 on release. Down only brakes;
    /// reverse is its own command.
    /// </summary>
    public static void Apply(ref DriverState s, CarInput i, float dt, float? timeBasedTick, ref float throttle, ref float steer)
    {
        if (i.Has(CarButtons.Analog))
        {
            s.HoldUp = s.HoldDown = s.HoldRight = s.HoldLeft = 0;
            throttle = System.Math.Clamp(i.Throttle / 32767f, -0.9999f, 0.9999f);
            steer = System.Math.Clamp(i.Steer / 32767f, -0.9999f, 0.9999f);
            return;
        }
        float scale = timeBasedTick is { } tick ? dt / tick : 1f;
        bool up = i.Has(CarButtons.ThrottleUp), down = i.Has(CarButtons.ThrottleDown);
        bool left = i.Has(CarButtons.SteerLeft), right = i.Has(CarButtons.SteerRight);
        float e4 = throttle, e0 = steer;
        if (up) { s.HoldUp = MathF.Min(s.HoldUp + dt, 3f); e4 += UpPerTick * scale * (1 - s.HoldUp / 3f); } else s.HoldUp = 0;
        if (down) { s.HoldDown = MathF.Min(s.HoldDown + dt, 3f); e4 -= DownPerTick * scale * (1 - s.HoldDown / 3f); } else s.HoldDown = 0;
        if (right) { s.HoldRight = MathF.Min(s.HoldRight + dt, 3f); e0 = -MathF.Sqrt(s.HoldRight / 3f); } else s.HoldRight = 0;
        if (left) { s.HoldLeft = MathF.Min(s.HoldLeft + dt, 3f); e0 = MathF.Sqrt(s.HoldLeft / 3f); } else s.HoldLeft = 0;
        if (!up && !down) e4 = 0;
        if (!left && !right) e0 = 0;
        throttle = System.Math.Clamp(e4, -0.9999f, 0.9999f);
        steer = System.Math.Clamp(e0, -0.9999f, 0.9999f);
    }
}
