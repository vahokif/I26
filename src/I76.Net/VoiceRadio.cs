namespace I76.Net;

/// <summary>A second-order filter section (RBJ cookbook), direct form II transposed.</summary>
public sealed class Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Biquad LowPass(float rate, float hz, float q = 0.7071f) => Make(rate, hz, q, false);
    public static Biquad HighPass(float rate, float hz, float q = 0.7071f) => Make(rate, hz, q, true);

    private static Biquad Make(float rate, float hz, float q, bool high)
    {
        double w = 2 * Math.PI * Math.Min(hz, rate * 0.49f) / rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
        var b = new Biquad { _a1 = (float)(-2 * cs / a0), _a2 = (float)((1 - al) / a0) };
        if (high) { b._b0 = (float)((1 + cs) / 2 / a0); b._b1 = (float)(-(1 + cs) / a0); b._b2 = b._b0; }
        else { b._b0 = (float)((1 - cs) / 2 / a0); b._b1 = (float)((1 - cs) / a0); b._b2 = b._b0; }
        return b;
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0;
}

/// <summary>
/// The CB radio's voice: what a decoded 8 kHz talk becomes on the air. A 300 Hz to 3 kHz band (two sections each side), a compressor that
/// evens the talker's level, a soft clip for the carbon-microphone grit, and a steady hiss under the voice, then the band again so the
/// hiss and the clip's overtones stay inside it. <see cref="Squelch"/> makes the burst at the key going down and the click and tail at it
/// coming up. Deterministic (its own noise generator), mono, any sample rate.
/// </summary>
public sealed class RadioVoice
{
    public const float LowHz = 300, HighHz = 3000;
    private readonly float _rate;
    private readonly Biquad _hp1, _hp2, _lp1, _lp2, _hp3, _lp3;
    private float _env;
    private uint _rng = 0x9e3779b9;
    /// <summary>The hiss under the voice (linear, 1 = full scale); 0 for none. The default is about −36 dBFS.</summary>
    public float Hiss = 0.016f;
    /// <summary>The soft clip's drive (1 is nearly clean), the compressor's threshold (linear), ratio and make-up gain, and the output level.</summary>
    public float Drive = 2.2f, Threshold = 0.12f, Ratio = 3f, Makeup = 1.5f, Level = 0.7f;

    public RadioVoice(float rate = VoiceWire.SampleRate)
    {
        _rate = rate;
        _hp1 = Biquad.HighPass(rate, LowHz); _hp2 = Biquad.HighPass(rate, LowHz);
        _lp1 = Biquad.LowPass(rate, HighHz); _lp2 = Biquad.LowPass(rate, HighHz);
        _hp3 = Biquad.HighPass(rate, LowHz * 0.8f); _lp3 = Biquad.LowPass(rate, HighHz * 1.15f);
    }

    private float Noise()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng / (float)uint.MaxValue) * 2 - 1;
    }

    public void Process(Span<float> x)
    {
        float attack = 1 - MathF.Exp(-1 / (0.004f * _rate)), release = 1 - MathF.Exp(-1 / (0.12f * _rate));
        float norm = 1 / MathF.Tanh(Drive);
        for (int i = 0; i < x.Length; i++)
        {
            float v = _lp2.Process(_lp1.Process(_hp2.Process(_hp1.Process(x[i]))));
            // compressor: the level above the threshold is cut by the ratio; make-up gain brings the quiet talker up
            float a = MathF.Abs(v);
            _env += (a > _env ? attack : release) * (a - _env);
            float gain = _env > Threshold ? MathF.Pow(_env / Threshold, 1 / Ratio - 1) : 1;
            v *= gain * Makeup;
            v = MathF.Tanh(v * Drive) * norm * Level;
            v += Hiss * Noise();
            x[i] = Math.Clamp(_lp3.Process(_hp3.Process(v)), -1f, 1f);
        }
    }

    /// <summary>
    /// The squelch: the receiver opening (<paramref name="open"/>: a short burst of static as the carrier arrives) or closing (a click, then
    /// a tail of static that dies away). About 90 ms, at this sample rate, in the band.
    /// </summary>
    public float[] Squelch(bool open)
    {
        int n = (int)(_rate * (open ? 0.07f : 0.14f));
        var s = new float[n];
        var hp = Biquad.HighPass(_rate, 450); var lp = Biquad.LowPass(_rate, 3200); var lp2 = Biquad.LowPass(_rate, 3200);
        float amp = open ? 0.35f : 0.30f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;
            float env = open ? MathF.Pow(1 - t, 1.5f) : MathF.Exp(-t * 5.5f);
            float v = Noise() * env;
            // closing: a click first, a short burst of a low tone's edge
            if (!open && i < _rate * 0.006f) v += MathF.Sign(MathF.Sin(i * 2 * MathF.PI * 1400 / _rate)) * 0.55f * (1 - i / (_rate * 0.006f));
            s[i] = lp2.Process(lp.Process(hp.Process(v))) * amp * 1.7f;
        }
        return s;
    }
}

/// <summary>
/// A streaming sample-rate converter for the voice: capture at the sound card's rate down to 8 kHz (a low-pass first, below the new Nyquist),
/// playback up from it (Catmull-Rom between the samples). Linear in time, a little state between calls.
/// </summary>
public sealed class VoiceResampler
{
    private readonly double _step;     // input samples per output sample
    private readonly Biquad[] _pre;
    private readonly List<float> _buf = [0f, 0f, 0f];
    private double _pos = 1;            // index into _buf of the next output

    public VoiceResampler(int inRate, int outRate)
    {
        _step = (double)inRate / outRate;
        _pre = inRate > outRate
            ? [Biquad.LowPass(inRate, outRate * 0.44f), Biquad.LowPass(inRate, outRate * 0.44f), Biquad.LowPass(inRate, outRate * 0.44f, 1.1f)]
            : [];
    }

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        foreach (float s in input)
        {
            float v = s;
            foreach (var f in _pre) v = f.Process(v);
            _buf.Add(v);
        }
        // an output at position p needs the samples at floor(p)-1 .. floor(p)+2
        while ((int)_pos + 2 < _buf.Count)
        {
            int i = (int)_pos;
            float t = (float)(_pos - i), y0 = _buf[i - 1], y1 = _buf[i], y2 = _buf[i + 1], y3 = _buf[i + 2];
            output.Add(y1 + 0.5f * t * (y2 - y0 + t * (2 * y0 - 5 * y1 + 4 * y2 - y3 + t * (3 * (y1 - y2) + y3 - y0))));
            _pos += _step;
        }
        int drop = (int)_pos - 1;
        if (drop > 0) { _buf.RemoveRange(0, drop); _pos -= drop; }
    }
}
