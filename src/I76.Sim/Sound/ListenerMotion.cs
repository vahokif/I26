using System.Numerics;
using I76.Formats;

namespace I76.Sim.Sound;

/// <summary>
/// The listener's velocity for the Doppler shift (<see cref="Ds3d.DopplerPitch"/>), from where the camera is
/// each frame. The camera has no velocity of its own to read, so it is the change of its position over the
/// frame time, eased over a tenth of a second (the same at any frame rate) so a frame's uneven step does not
/// wobble the pitch. A move no camera makes is not motion: a cut to another camera, a cut the caller reports
/// (<see cref="Cut"/>: a teleport, a restore), a step faster than <see cref="TeleportSpeed"/> (a respawn, a
/// reset, the floating origin moving) all leave the listener standing (velocity 0) for that frame, and the
/// next frame's step starts it afresh. A step whose speed changes faster than any car can
/// (<see cref="MaxAcceleration"/>: a view swung to another place on the same camera) is not taken
/// for that one frame, the velocity held; a second such step in a row is believed (a crash, a landing).
/// </summary>
public sealed class ListenerMotion
{
    /// <summary>A step faster than this is a jump, not motion (m/s).</summary>
    public const float TeleportSpeed = 1000f;

    /// <summary>A change of velocity faster than this in one frame is a jump (m/s²; about 40 g).</summary>
    public const float MaxAcceleration = 400f;

    /// <summary>How long the velocity takes to ease most of the way (1 − 1/e) to a new one (s).</summary>
    public const float Smoothing = 0.1f;

    private Vector3 _last;
    private object? _camera;
    private bool _havePos, _haveVel, _held;

    /// <summary>The listener's velocity now (0 after a cut).</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>The next frame starts afresh: the listener stands still until a step from there is seen.</summary>
    public void Cut()
    {
        _havePos = _haveVel = _held = false;
        Velocity = Vector3.Zero;
    }

    /// <summary>The camera at <paramref name="position"/> (any fixed frame) after a frame of <paramref name="dt"/> seconds; <paramref name="camera"/> is which camera it is.</summary>
    public Vector3 Update(Vector3 position, object? camera, float dt)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) { Cut(); return Velocity = Vector3.Zero; }
        if (!ReferenceEquals(camera, _camera)) { _camera = camera; Cut(); }
        if (!_havePos) { _last = position; _havePos = true; _haveVel = false; return Velocity = Vector3.Zero; }
        if (!(dt > 0f)) return Velocity;   // no time passed (a paused frame): as it was
        var raw = (position - _last) / dt;
        _last = position;
        if (!(raw.Length() <= TeleportSpeed)) { _haveVel = _held = false; return Velocity = Vector3.Zero; }
        if (!_haveVel) { _haveVel = true; _held = false; return Velocity = raw; }
        if ((raw - Velocity).Length() > MaxAcceleration * dt && !_held) { _held = true; return Velocity; }
        _held = false;
        return Velocity += (raw - Velocity) * (1f - DetMath.Exp(-dt / Smoothing));
    }
}
