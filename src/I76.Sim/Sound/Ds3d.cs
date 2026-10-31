namespace I76.Sim.Sound;

/// <summary>
/// How loud a positional voice is, as the original's DirectSound3D set-up
/// makes it. The listener (every frame) sits at the camera
/// with distance factor 1, rolloff factor 1 and Doppler factor 0.8.
/// Each voice with an object is a 3D buffer at that object
/// (every pump): min distance 10, or 100 for a voice
/// flagged 0x200 (the weapon impacts, sound event 7's
/// vexplode.wav), max distance 400. So the inverse-distance law of
/// DirectSound: full volume inside the min distance, min/d beyond it, and
/// no quieter past the max distance -- a bump 2 km away is still at
/// 10/400, −32 dB.
///
/// Does not start a voice whose object is 600 m or more from
/// the player's car, but only when the caller passes a descriptor with mode
/// ≠ 1 -- of the in-game callers, the looping fire (mode 2).
/// Every voice with an object then meets the admission test:
/// 600 m or more from the player's car it is parked
/// unplayed (state 3), so a one-shot that far is never heard (Nitro Pack,
/// measured: P19's helicopter destroyed 1.8 km away loads vexplode.wav and
/// never plays it). The original stops voices that move out of range.
/// </summary>
public static class Ds3d
{
    /// <summary>The usual min distance (10.0).</summary>
    public const float MinDistance = 10f;

    /// <summary>The min distance of a voice flagged 0x200 (100.0).</summary>
    public const float LoudMinDistance = 100f;

    /// <summary>The max distance (400.0).</summary>
    public const float MaxDistance = 400f;

    /// <summary>Start cut-off for a mode-2 voice, from the player's car (360000 = 600²).</summary>
    public const float StartCutoff = 600f;

    /// <summary>The gain of a voice at <paramref name="distance"/> from the listener (1 at or inside the min distance).</summary>
    public static float Gain(float distance, float min = MinDistance) =>
        min / System.Math.Clamp(distance, min, System.Math.Max(min, MaxDistance));

    /// <summary><see cref="Gain"/> in decibels: 0 near, −6 dB per doubling, held from the max distance on.</summary>
    public static float AttenuationDb(float distance, float min = MinDistance) =>
        20f * MathF.Log10(Gain(distance, min));

    /// <summary>
    /// A voice on the player's own car in the cockpit views (the hud flag: F1, the binoculars, the gun
    /// camera) is placed at the listener itself, with the listener's velocity -- no pan, no distance, no Doppler -- so the
    /// engine, horn, wind and tyres sit dead centre at full volume (GitHub #83: Godot placed them at the car's origin, to the
    /// right of the driver's seat). In every other view it is a 3D buffer at the car like any other.
    /// </summary>
    public static bool AtListener(bool onPlayersCar, bool cockpitView) => onPlayersCar && cockpitView;

    /// <summary>Admission for a looping fire voice: under 600 m from the player's car.</summary>
    public static bool LoopStarts(float distanceFromPlayer) => distanceFromPlayer * distanceFromPlayer < StartCutoff * StartCutoff;
}
