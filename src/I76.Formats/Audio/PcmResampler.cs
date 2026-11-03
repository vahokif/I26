using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace I76.Formats.Audio;

/// <summary>
/// Converts and resamples raw PCM audio (any bit depth, channels, or sample rate)
/// to standard 16 kHz 16-bit mono PCM formatted as a RIFF/WAVE stream for Whisper speech recognition.
/// </summary>
public static class PcmResampler
{
    public static MemoryStream? FromRiff(byte[] riffBytes)
    {
        var parsed = SoundFiles.Parse(riffBytes);
        if (parsed != null && parsed.Data.Length > 0)
        {
            return Resample(parsed.Data, parsed.Rate, parsed.Bits, parsed.Channels);
        }
        return null;
    }

    public static MemoryStream Resample(byte[] pcmData, int srcRate, int bits, int channels, int targetRate = 16000)
    {
        if (srcRate <= 0) srcRate = 11025;
        if (bits <= 0) bits = 8;
        if (channels <= 0) channels = 1;

        int bytesPerSample = (bits / 8) * channels;
        int sampleCount = pcmData.Length / bytesPerSample;
        float[] srcFloats = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            if (bits == 8 && channels == 1)
            {
                srcFloats[i] = (pcmData[i] - 128) / 128.0f;
            }
            else if (bits == 16 && channels == 1)
            {
                short s = BinaryPrimitives.ReadInt16LittleEndian(pcmData.AsSpan(i * 2, 2));
                srcFloats[i] = s / 32768.0f;
            }
            else if (bits == 8 && channels == 2)
            {
                float left = (pcmData[i * 2] - 128) / 128.0f;
                float right = (pcmData[i * 2 + 1] - 128) / 128.0f;
                srcFloats[i] = (left + right) * 0.5f;
            }
            else // 16-bit stereo
            {
                short left = BinaryPrimitives.ReadInt16LittleEndian(pcmData.AsSpan(i * 4, 2));
                short right = BinaryPrimitives.ReadInt16LittleEndian(pcmData.AsSpan(i * 4 + 2, 2));
                srcFloats[i] = (left + right) / 65536.0f;
            }
        }

        int targetCount = (int)Math.Round((double)srcFloats.Length * targetRate / srcRate);
        float[] resampledFloats = new float[targetCount];
        for (int i = 0; i < targetCount; i++)
        {
            double srcPos = (double)i * srcRate / targetRate;
            int idx0 = (int)Math.Floor(srcPos);
            int idx1 = Math.Min(idx0 + 1, srcFloats.Length - 1);
            float frac = (float)(srcPos - idx0);
            resampledFloats[i] = srcFloats[idx0] * (1.0f - frac) + srcFloats[idx1] * frac;
        }

        var wavStream = new MemoryStream();
        using var bw = new BinaryWriter(wavStream, Encoding.UTF8, leaveOpen: true);
        int dataBytes = targetCount * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(dataBytes + 36);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(targetRate);
        bw.Write(targetRate * 2); // Byte rate
        bw.Write((short)2); // Block align
        bw.Write((short)16);// Bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        foreach (var sample in resampledFloats)
        {
            short val = (short)Math.Clamp((int)Math.Round(sample * 32767.0f), -32768, 32767);
            bw.Write(val);
        }

        wavStream.Position = 0;
        return wavStream;
    }
}
