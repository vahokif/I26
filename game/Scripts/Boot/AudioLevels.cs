namespace I76.Game.Boot;

/// <summary>The SFX and Voice sliders as decibels. The original's volume law is 4 dB per halving of level x voice volume, 0 dB
/// at level 10: level 8 is -1.3 dB, 5 is -4 dB and 2 is -9.3 dB. Here level 8 stays 0 dB (the mix is balanced round it), so the
/// law is shifted by +1.29 dB; level 10 is +1.29 dB. Level 0 and 1 are off (the bus is muted).</summary>
public static class AudioLevels
{
    public static float EffectDb(int level) => level < 1 ? -80f : 4f * MathF.Log2(level / 10f) - 4f * MathF.Log2(0.8f);
}
