using System.Numerics;

namespace I76.Sim.Views;

/// <summary>
/// The modern look's motion blur (Graphic Detail's Motion Blur; the original had none): how long the shutter is open, how
/// far a streak may reach, and which frames are cuts that get no blur. The drawing is game/Scripts/Sim/MotionBlur.cs.
///
/// The shutter is a fixed time, not a fraction of a frame: a frame's motion vectors are scaled by <see cref="Scale"/>
/// (shutter seconds over the frame's seconds), so a car at 100 km/h streaks as far at 144 fps as at 30.
/// </summary>
public static class MotionBlurRules
{
    public static readonly string[] Names = ["Off", "On"];

    /// <summary>The reference interval the shutter is a fraction of: one frame at 60 fps.</summary>
    public const float Reference = 1f / 60f;
    /// <summary>The shutter, a fraction of <see cref="Reference"/> (1: open the whole of a 60 fps frame, 1/60 s).</summary>
    public const float DefaultShutter = 1f;
    /// <summary>The longest streak, a fraction of the view's height (4.5 %: 49 px at 1080p), whatever the resolution or Render Scale.</summary>
    public const float DefaultMaxLength = 0.045f;

    /// <summary>The frame times the scale is worked out over: a hitch longer than this streaks as a frame of this length would.</summary>
    public const float MinFrame = 1f / 1000f, MaxFrame = 1f / 10f;

    /// <summary>What one frame's motion is multiplied by for a shutter of <paramref name="shutter"/> reference intervals, at a frame of <paramref name="dt"/> seconds.</summary>
    public static float Scale(float dt, float shutter = DefaultShutter) =>
        float.IsFinite(dt) ? shutter * Reference / System.Math.Clamp(dt, MinFrame, MaxFrame) : 0f;

    /// <summary>The longest streak in pixels for a view <paramref name="height"/> pixels high (the 3D view's, at its Render Scale).</summary>
    public static float MaxPixels(int height, float maxLength = DefaultMaxLength) => System.Math.Max(0, height) * maxLength;

    /// <summary>A camera faster than this between two frames has jumped (a respawn, a teleport): 540 km/h, past any car or helicopter.</summary>
    public const float CutSpeed = 150f;
    /// <summary>A camera that turns more than this between two frames (radians, 45 degrees) has cut.</summary>
    public const float CutTurn = MathF.PI / 4;
    /// <summary>A view whose zoom changes by more than this fraction between two frames has cut (the binoculars).</summary>
    public const float CutZoom = 0.1f;

    /// <summary>
    /// Whether the camera's move from one frame to the next is a cut rather than motion: it moved faster than <see cref="CutSpeed"/>
    /// over the frame's <paramref name="dt"/>, turned (or rolled) by more than <see cref="CutTurn"/>, or zoomed by more than
    /// <see cref="CutZoom"/>. Forward and up are unit vectors; zoom is the projection's vertical scale (1 / tan(fov / 2)).
    /// </summary>
    public static bool IsCut(Vector3 prevEye, Vector3 prevForward, Vector3 prevUp, float prevZoom, Vector3 eye, Vector3 forward, Vector3 up, float zoom, float dt)
    {
        if (!float.IsFinite(eye.X + eye.Y + eye.Z + prevEye.X + prevEye.Y + prevEye.Z)) return true;
        float frame = System.Math.Clamp(float.IsFinite(dt) ? dt : MaxFrame, 1f / 240f, MaxFrame);
        if (Vector3.Distance(prevEye, eye) > CutSpeed * frame) return true;
        float cos = MathF.Cos(CutTurn);
        if (Vector3.Dot(prevForward, forward) < cos || Vector3.Dot(prevUp, up) < cos) return true;
        if (!(prevZoom > 0f) || !(zoom > 0f) || MathF.Abs(zoom / prevZoom - 1f) > CutZoom) return true;
        return false;
    }

    /// <summary>
    /// The effect rests (draws nothing, asks the engine for no motion vectors) once the camera has been still and the longest
    /// streak under a pixel for <paramref name="after"/> frames running (<paramref name="quiet"/>): standing, or crawling.
    /// </summary>
    public static bool Idle(int quiet, int after) => quiet >= after;

    /// <summary>The cfg value: absent is off (the default).</summary>
    public static bool From(bool? stored) => stored ?? false;
}
