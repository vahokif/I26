using System.Buffers.Binary;

namespace I76.Net;

/// <summary>A minimal WAV reader and writer for the CB voice's test hooks (a file fed in as the microphone, the played-back audio written out): PCM 8/16-bit in, 16-bit mono out.</summary>
public static class WavIo
{
    /// <summary>A PCM WAV as mono samples (−1..1; channels averaged) and its rate.</summary>
    public static (float[] Samples, int Rate) Read(byte[] file)
    {
        if (file.Length < 44 || System.Text.Encoding.ASCII.GetString(file, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(file, 8, 4) != "WAVE") throw new FormatException("not a WAV file");
        int pos = 12, channels = 1, rate = 8000, bits = 16;
        while (pos + 8 <= file.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(file, pos, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body)) != 1) throw new FormatException("the WAV is not PCM");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 14));
            }
            else if (id == "data")
            {
                size = Math.Min(size, file.Length - body);
                int bytes = bits / 8, frames = size / (bytes * channels);
                var o = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int at = body + (i * channels + c) * bytes;
                        sum += bits == 8 ? (file[at] - 128) / 128f : BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at)) / 32768f;
                    }
                    o[i] = sum / channels;
                }
                return (o, rate);
            }
            pos = body + size + (size & 1);
        }
        throw new FormatException("the WAV has no data");
    }

    /// <summary>16-bit mono PCM.</summary>
    public static byte[] Write(ReadOnlySpan<float> samples, int rate)
    {
        var b = new byte[44 + samples.Length * 2];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 36 + samples.Length * 2);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), 16);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(b, 36);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(44 + i * 2), (short)Math.Clamp(MathF.Round(samples[i] * 32767f), -32768f, 32767f));
        return b;
    }
}
