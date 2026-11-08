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
        if (Header(vfs, name) is not { } d) return (50, 1);
        int pri = BitConverter.ToInt32(d, 4), max = BitConverter.ToInt32(d, 8);
        return (pri < 0 ? 50 : pri, max < 0 ? 1 : max);
    }

    /// <summary>
    /// Whether a sound started with no voice settings of its own loops: its GAS0 header's mode (-1: 0) is 1 or 2; mode 0 plays
    /// once. Of the cars' horn files only the police car's siren, aps2.wav, is mode 1: started by setSiren or by an AI's honk,
    /// it sounds until something stops it.
    /// </summary>
    public static bool Loops(AssetVfs vfs, string name) => Header(vfs, name) is { } d && BitConverter.ToInt32(d, 12) > 0;

    /// <summary>The sound file's bytes when they begin with a GAS0 header, else null.</summary>
    private static byte[]? Header(AssetVfs vfs, string name)
    {
        var d = vfs.TryRead(name);
        if (d is null)
        {
            int dot = name.LastIndexOf('.');
            var stem = dot < 0 ? name : name[..dot];
            d = vfs.TryRead(stem + ".gpw") ?? vfs.TryRead(stem + ".wav");
        }
        if (d is null && CompanionSounds.For(vfs, name) is { } other) return Header(other, name);
        return d is null || d.Length < 28 || d[0] != 'G' || d[1] != 'A' || d[2] != 'S' || d[3] != '0' ? null : d;
    }

    public static Pcm? Read(AssetVfs vfs, string name) => ReadRiff(vfs, name) is { } r ? Parse(r) : null;

    /// <summary>
    /// The volume a sound's GAS0 header asks for, in percent: the int32 at offset 16, -1 (or no header) meaning 100. The original
    /// starts the voice at 400·log2(v/100) millibels (<see cref="VolumeDb"/>). The Moth trucks', buses' and tractor's engine
    /// (eimarx) says 60; the tyre skid and turn sounds 50.
    /// </summary>
    public static int Volume(AssetVfs vfs, string name)
    {
        var d = vfs.TryRead(name);
        if (d is null)
        {
            int dot = name.LastIndexOf('.');
            var stem = dot < 0 ? name : name[..dot];
            d = vfs.TryRead(stem + ".gpw") ?? vfs.TryRead(stem + ".wav");
        }
        if (d is null && CompanionSounds.For(vfs, name) is { } other) return Volume(other, name);
        return d is null ? 100 : Volume(d);
    }

    /// <summary>The same, from the file's bytes.</summary>
    public static int Volume(ReadOnlySpan<byte> file)
    {
        if (file.Length < 28 || file[0] != 'G' || file[1] != 'A' || file[2] != 'S' || file[3] != '0') return 100;
        int v = BitConverter.ToInt32(file[16..]);
        return v < 0 ? 100 : v;
    }

    /// <summary>A header volume in percent as decibels: 4 dB per halving, 0 dB at 100 (60 is -2.9 dB, 50 is -4 dB).</summary>
    public static float VolumeDb(int percent) => percent <= 0 ? -80f : 4f * MathF.Log2(percent / 100f);

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
