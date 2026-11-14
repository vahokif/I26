namespace I76.Game;

/// <summary>The engine loop's playback rate. The original sets the voice's frequency from the engine's rpm each frame:
/// 8268.75 Hz at 1050 rpm, rising 8268.75 Hz per 4950 rpm (11025 Hz mono loops: x0.75 at idle, x1.5 at the 6000 rpm
/// free-rev cap), in absolute Hz whatever the file's own rate.</summary>
public static class EngineSoundPitch
{
    public const float IdleRpm = 1050f, IdleHz = 8268.75f, RpmSpan = 4950f;

    public static float Hz(float rpm) => IdleHz + IdleHz * (rpm - IdleRpm) / RpmSpan;

    /// <summary>The pitch scale for a loop recorded at <paramref name="sampleRate"/> Hz.</summary>
    public static float Scale(float rpm, float sampleRate) => Math.Max(Hz(rpm), 100f) / sampleRate;

    /// <summary>
    /// Whether a car's engine loop sounds in this state: running (flag 1) and not dead (flag 0x20). A gun emplacement has
    /// none. The original starts the loop at the ignition's end and stops it on engine off or death (sound events 1 and 2).
    /// </summary>
    public static bool LoopRuns(uint flags, bool emplacement) => !emplacement && (flags & 1) != 0 && (flags & 0x20) == 0;
}
