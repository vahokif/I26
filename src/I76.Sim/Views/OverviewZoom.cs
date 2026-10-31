namespace I76.Sim.Views;

/// <summary>
/// The F10 view's height control and the binding layer's key ramp).
/// While overview_zoom_plus (grey PgDn) is held, a ramp grows by 3·ftol(182·dt)·16 a frame up to 0x5555 and sets the
/// axis position; overview_zoom_delta = (pos·32) >> 16 (a whole number, -32..32); the handler then scales the height by
/// 1 + 0.01·delta each frame (overview_zoom_minus, grey PgUp, runs the ramp the other way). Releasing a key zeroes
/// the ramp. The original does this once per displayed frame; here it runs at its 60 Hz frame (dt = 1/60, so
/// ftol(182·dt) = 3), whatever the render rate.
/// </summary>
public sealed class OverviewZoom
{
    public const float Step = 1f / 60f;
    private int _ramp;

    public float Height = 100f;

    public void Reset() { Height = 100f; _ramp = 0; }

    /// <summary>One 1/60 s frame with the keys' state: the delta the original's axis reads (for tests), and the new height.</summary>
    public int Frame(bool plus, bool minus, float radius, float far)
    {
        int d = (int)(182f * Step);
        int pos = 0;
        if (plus)
        {
            if (_ramp < 0) _ramp = 0;
            _ramp = System.Math.Min(_ramp + (d * 3 << 4), 0x5555);
            pos += _ramp;
        }
        if (minus)
        {
            if (_ramp > 0) _ramp = 0;
            _ramp = System.Math.Max(_ramp - (d * 3 << 4), -0x5555);
            pos += _ramp;
        }
        if (!plus && !minus) _ramp = 0;
        pos = System.Math.Clamp(pos, -0x10000, 0x10000);
        int delta = (int)(((long)pos * 32) >> 16);
        Height *= 1f - delta * -0.01f;
        Height = System.Math.Max(System.Math.Min(Height, far * 0.85f), radius * 4f);
        return delta;
    }

    /// <summary>The height the camera stands at: the original's <paramref name="height"/> times the port's wheel factor, held within the same bounds (4 radii, 0.85 of the far plane).</summary>
    public static float Scaled(float height, float wheel, float radius, float far) =>
        System.Math.Max(System.Math.Min(height * wheel, far * 0.85f), radius * 4f);
}
