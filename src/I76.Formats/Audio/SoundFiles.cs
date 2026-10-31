using I76.Formats.Vfs;

namespace I76.Formats.Audio;

/// <summary>PCM sound data: interleaved samples, little-endian, as stored.</summary>
public sealed record Pcm(int Channels, int Rate, int Bits, byte[] Data)
{
    public float Seconds => Rate <= 0 || Channels <= 0 || Bits <= 0 ? 0 : Data.Length / (float)(Channels * Bits / 8) / Rate;
}

/// <summary>
/// The game's sounds: plain .wav, or .gpw (docs/formats/misc-data-files.md in
/// the original game): a 28-byte "GAS0" header, then a RIFF/WAVE stream. A ".wav"
/// name with no such file resolves to the .gpw of the same stem, which is
/// how engsnd.dat's names (aheli.wav, eishp.wav) reach their data.
/// </summary>
public static class SoundFiles
{
    public static byte[]? ReadRiff(AssetVfs vfs, string name)
    {
        var d = vfs.TryRead(name);
        if (d is null)
        {
            int dot = name.LastIndexOf('.');
            var stem = dot < 0 ? name : name[..dot];
            d = vfs.TryRead(stem + ".gpw") ?? vfs.TryRead(stem + ".wav");
        }
        if (d is null) return CompanionSounds.For(vfs, name) is { } other ? ReadRiff(other, name) : null;
        if (d.Length > 32 && d[0] == 'G' && d[1] == 'A' && d[2] == 'S' && d[3] == '0') d = d[28..];
        return d;
    }

    /// <summary>
    /// The voice limits a sound carries: its GAS0 header's priority (-1: 50) and how many voices
    /// of its name may play at once (-1: 1). A plain .wav has no header, so 50 and 1.
    /// </summary>
    public static (int Priority, int MaxVoices) Limits(AssetVfs vfs, string name)
    {
        var d = vfs.TryRead(name);
        if (d is null)
        {
            int dot = name.LastIndexOf('.');
            var stem = dot < 0 ? name : name[..dot];
            d = vfs.TryRead(stem + ".gpw") ?? vfs.TryRead(stem + ".wav");
        }
        if (d is null && CompanionSounds.For(vfs, name) is { } other) return Limits(other, name);
        if (d is null || d.Length < 28 || d[0] != 'G' || d[1] != 'A' || d[2] != 'S' || d[3] != '0') return (50, 1);
        int pri = BitConverter.ToInt32(d, 4), max = BitConverter.ToInt32(d, 8);
        return (pri < 0 ? 50 : pri, max < 0 ? 1 : max);
    }

    public static Pcm? Read(AssetVfs vfs, string name) => ReadRiff(vfs, name) is { } r ? Parse(r) : null;

    public static Pcm? Parse(byte[] riff)
    {
        if (riff.Length < 12 || Bin.Latin1(riff.AsSpan(0, 4)) != "RIFF" || Bin.Latin1(riff.AsSpan(8, 4)) != "WAVE") return null;
        int p = 12, channels = 1, rate = 11025, bits = 8;
        byte[]? data = null;
        while (p + 8 <= riff.Length)
        {
            string id = Bin.Latin1(riff.AsSpan(p, 4));
            long size = Bin.U32(riff, p + 4);
            int body = p + 8;
            // a size past the end (some files carry garbage or a too-large data size) takes what is there
            if (size > riff.Length - body) size = riff.Length - body;
            if (id == "fmt " && body + 16 <= riff.Length)
            {
                channels = Bin.U16(riff, body + 2);
                rate = (int)Bin.U32(riff, body + 4);
                bits = Bin.U16(riff, body + 14);
            }
            else if (id == "data")
                data = riff.AsSpan(body, (int)size).ToArray();
            p = body + (int)size + (int)(size & 1);
        }
        return data is null ? null : new Pcm(channels, rate, bits, data);
    }
}
