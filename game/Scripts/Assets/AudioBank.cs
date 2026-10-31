using Godot;
using I76.Formats.Audio;
using I76.Formats.Vfs;

namespace I76.Game.Assets;

/// <summary>Game sounds as Godot streams, cached by name.</summary>
public sealed class AudioBank(AssetVfs vfs)
{
    public AssetVfs Vfs => vfs;

    private readonly Dictionary<string, AudioStreamWav?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AudioStreamWav? Get(string name, bool loop = false)
    {
        var key = name + (loop ? "#loop" : "");
        if (_cache.TryGetValue(key, out var s)) return s;
        s = null;
        if (SoundFiles.Read(vfs, name) is { } pcm) s = ToStream(pcm, loop);
        return _cache[key] = s;
    }

    private readonly Dictionary<string, (int Priority, int MaxVoices)> _limits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sound's priority and how many voices of it may play at once (<see cref="SoundFiles.Limits"/>).</summary>
    public (int Priority, int MaxVoices) Limits(string name) =>
        _limits.TryGetValue(name, out var l) ? l : _limits[name] = SoundFiles.Limits(vfs, name);

    /// <summary>PCM as a Godot stream (8- or 16-bit; null otherwise), looped whole if asked.</summary>
    public static AudioStreamWav? ToStream(Pcm pcm, bool loop = false)
    {
        if (pcm.Bits != 8 && pcm.Bits != 16) return null;
        var data = pcm.Data;
        if (pcm.Bits == 8)
        {
            data = (byte[])data.Clone();
            for (int i = 0; i < data.Length; i++) data[i] ^= 0x80;   // WAV 8-bit is unsigned, Godot's is signed
        }
        var s = new AudioStreamWav
        {
            Data = data,
            Format = pcm.Bits == 8 ? AudioStreamWav.FormatEnum.Format8Bits : AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = pcm.Rate,
            Stereo = pcm.Channels == 2,
        };
        if (loop)
        {
            s.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            s.LoopBegin = 0;
            s.LoopEnd = data.Length / (pcm.Bits / 8) / pcm.Channels;
        }
        return s;
    }
}
