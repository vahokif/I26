using System.Numerics;
using I76.Formats;

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
        20f * DetMath.Log10(Gain(distance, min));

    /// <summary>
    /// A voice on the player's own car in the cockpit views (the hud flag: F1, the binoculars, the gun
    /// camera) is placed at the listener itself, with the listener's velocity -- no pan, no distance, no Doppler -- so the
    /// engine, horn, wind and tyres sit dead centre at full volume (GitHub #83: Godot placed them at the car's origin, to the
    /// right of the driver's seat). In every other view it is a 3D buffer at the car like any other.
    /// </summary>
    public static bool AtListener(bool onPlayersCar, bool cockpitView) => onPlayersCar && cockpitView;

    /// <summary>The listener's Doppler factor (0.8): the velocities count at 0.8 of their real size.</summary>
    public const float DopplerFactor = 0.8f;

    /// <summary>The speed of sound DirectSound3D works in, at distance factor 1 (one unit a metre): 343 m/s.</summary>
    public const float SpeedOfSound = 343f;

    /// <summary>
    /// The pitch a voice is heard at for its motion and the listener's, as DirectSound3D's software 3D
    /// works it out: only the relative velocity along the line between them counts (the source's minus
    /// the listener's, positive when they draw apart), scaled by the distance factor (1) and the Doppler
    /// factor (0.8), limited to half the speed of sound either way, and then
    /// <c>f' = f · c / (c + v)</c>. So a source closing at 50 m/s (40 m/s after the factor) plays 1.13
    /// times higher, one drawing away 0.90 times as high, and the shift never passes 2 (closing) or
    /// 2/3 (drawing apart). A voice with no motion relative to the listener, or at the listener
    /// itself, is not shifted; nor is one whose positions or velocities are not finite.
    /// </summary>
    public static float DopplerPitch(Vector3 source, Vector3 sourceVel, Vector3 listener, Vector3 listenerVel, float factor = DopplerFactor)
    {
        var d = source - listener;
        var rel = sourceVel - listenerVel;
        float len = d.Length();
        if (!(len > 1e-4f) || !float.IsFinite(len) || rel == Vector3.Zero) return 1f;
        float v = Vector3.Dot(rel, d) / len * factor;
        if (!float.IsFinite(v)) return 1f;
        v = System.Math.Clamp(v, -SpeedOfSound / 2, SpeedOfSound / 2);
        return SpeedOfSound / (SpeedOfSound + v);
    }

    /// <summary>Admission for a looping fire voice: under 600 m from the player's car.</summary>
    public static bool LoopStarts(float distanceFromPlayer) => distanceFromPlayer * distanceFromPlayer < StartCutoff * StartCutoff;
}
