using System.Buffers.Binary;
using System.Text;

namespace I76.Formats.Input;

/// <summary>
/// One effect of a force\*.FRC file, as this port reads it. The files are RIFF "FORC" force-editor files: a trgt chunk, then a
/// LIST trak holding one LIST efct per effect (an id chunk and a data chunk). The data is a 104-byte name block (a size word, a
/// 64-byte name, ten words of header) and the record. Read off the shipped files, not from documentation, so the fields
/// are named for what they do across the fourteen: the waveform ids seen are 101 constant, 102 sine, 103 cosine, 104 square
/// low, 105 square high, 107 ramp down, 111 sawtooth down, and 201 a user-defined curve of percent samples.
/// The original never parsed them itself: I7_SFRCE.DLL hands the file name to the SideWinder driver's file effect
/// (the original's effect creation with the VFX effect GUID), so nothing in the game's code confirms or corrects this reading.
/// </summary>
public sealed record FrcEffect(
    string Name, int Waveform, int DirectionDegrees, int DurationMs, int GainPercent, int MagnitudePercent,
    int Cycles, int HighPercent, int LowPercent, int MaxLevel, int MinLevel, int[] Samples)
{
    public const int Constant = 101, Sine = 102, Cosine = 103, SquareLow = 104, SquareHigh = 105, RampUp = 106, RampDown = 107, SawtoothUp = 108, SawtoothDown = 111, UserDefined = 201;

    public bool Custom => Waveform == UserDefined;

    /// <summary>How fast it shakes, in Hz (a user-defined curve is a slow kick: its own samples span the duration).</summary>
    public double Hz => Custom || DurationMs <= 0 ? 0 : Cycles * 1000.0 / DurationMs;

    /// <summary>The waveform at a phase 0..1, in −1..+1.</summary>
    private double Wave(double phase)
    {
        double duty = HighPercent + LowPercent > 0 ? HighPercent / (double)(HighPercent + LowPercent) : 0.5;
        return Waveform switch
        {
            Sine => Math.Sin(2 * Math.PI * phase),
            Cosine => Math.Cos(2 * Math.PI * phase),
            SquareHigh => phase < duty ? 1 : -1,
            SquareLow => phase < duty ? -1 : 1,
            RampDown or SawtoothDown => 1 - 2 * phase,
            RampUp or SawtoothUp => 2 * phase - 1,
            _ => 1,
        };
    }

    /// <summary>The strength at a moment of the effect's duration, 0..1 (the size of the force, whichever way it pulls); 0 outside it.</summary>
    public double Level(double ms)
    {
        if (ms < 0 || ms >= DurationMs || DurationMs <= 0) return 0;
        double scale = Math.Clamp(MagnitudePercent, 0, 100) / 100.0 * Math.Clamp(GainPercent, 0, 100) / 100.0;
        if (Custom)
            return Samples.Length == 0 ? 0 : Math.Clamp(Math.Abs(Samples[Math.Min((int)(ms / DurationMs * Samples.Length), Samples.Length - 1)]) / 100.0, 0, 1) * scale;
        double cycles = Math.Max(Cycles, 1), t = ms / DurationMs * cycles;
        double Value(double phase) => Math.Abs(MinLevel + (MaxLevel - MinLevel) * (Wave(phase) + 1) / 2) / 100.0;
        // too fast for a motor to follow (and for a frame to sample): the cycle's average
        if (Hz > 25)
        {
            double sum = 0;
            for (int i = 0; i < 32; i++) sum += Value((i + 0.5) / 32);
            return Math.Clamp(sum / 32, 0, 1) * scale;
        }
        return Math.Clamp(Value(t - Math.Floor(t)), 0, 1) * scale;
    }
}

/// <summary>The FRC files' reader.</summary>
public static class FrcFile
{
    /// <summary>Every effect in the file, in order (empty for anything that is not a force file).</summary>
    public static List<FrcEffect> Parse(ReadOnlySpan<byte> d)
    {
        var list = new List<FrcEffect>();
        if (d.Length < 12 || !d[..4].SequenceEqual("RIFF"u8)) return list;
        Walk(d, 12, Math.Min(d.Length, 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(d[4..])), false, list);
        return list;
    }

    private static void Walk(ReadOnlySpan<byte> d, int at, int end, bool inEffect, List<FrcEffect> list)
    {
        while (at + 8 <= end)
        {
            var id = Encoding.ASCII.GetString(d.Slice(at, 4));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 4)..]);
            int body = at + 8, stop = Math.Min(end, body + size);
            if (size < 0 || body > end) return;
            if (id == "LIST" && stop - body >= 4) Walk(d, body + 4, stop, Encoding.ASCII.GetString(d.Slice(body, 4)) == "efct", list);
            else if (id == "data" && inEffect && Record(d[body..stop]) is { } e) list.Add(e);
            at = body + size + (size & 1);
        }
    }

    private static FrcEffect? Record(ReadOnlySpan<byte> r)
    {
        var rec = r.ToArray();
        return Record(rec);
    }

    private static FrcEffect? Record(byte[] r)
    {
        const int Name = 4, Head = 68, Fields = 22;       // the record's words after the 68-byte name block
        if (r.Length < Head + Fields * 4) return null;
        int W(int k) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(Head + k * 4));
        var nameBytes = r.AsSpan(Name, 64);
        int z = nameBytes.IndexOf((byte)0);
        var name = Encoding.ASCII.GetString(z < 0 ? nameBytes : nameBytes[..z]);
        int waveform = W(1);
        int count = W(18);
        int[] samples = [];
        if (waveform == FrcEffect.UserDefined)
        {
            int avail = (r.Length - Head) / 4 - 20;       // the curve starts after the 20 words of the record
            samples = new int[Math.Clamp(Math.Min(count, avail), 0, 4096)];
            for (int i = 0; i < samples.Length; i++) samples[i] = W(20 + i);
        }
        return new FrcEffect(name, waveform, W(3), W(5), W(6), W(16), count, W(12), W(13), waveform == FrcEffect.UserDefined ? 100 : W(20), waveform == FrcEffect.UserDefined ? -100 : W(21), samples);
    }
}
