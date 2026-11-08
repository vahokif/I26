using Godot;
using I76.Formats.Audio;
using I76.Formats.Vfs;

namespace I76.Game.Assets;

/// <summary>
/// Game sounds as Godot streams, cached by name: the original's samples, or HiFi Audio's in their place
/// (<see cref="HiFiAudio"/>) while it is switched on. Everything else about a sound (its header's rules, how it is
/// pitched, placed and mixed) is the caller's, from the original file, either way.
/// </summary>
public sealed class AudioBank(AssetVfs vfs)
{
    public AssetVfs Vfs => vfs;

    private readonly Dictionary<string, AudioStream?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AudioStream? Get(string name, bool loop = false)
    {
        bool hifi = HiFiAudio.Enabled && HiFiAudio.Installed;
        var key = name + (loop ? "#loop" : "") + (hifi ? "#hifi" : "");
        if (_cache.TryGetValue(key, out var s)) return s;
        var pcm = SoundFiles.Read(vfs, name);
        // a pack replaces only sounds the game has: one the install lacks stays silent
        s = pcm is null ? null : (hifi ? HiFiAudio.Load(name, vfs.Install.IsNitro, loop, pcm.Seconds) : null) ?? ToStream(pcm, loop);
        return _cache[key] = s;
    }

    /// <summary>Whether the stream repeats by itself (a sound fetched with loop: true).</summary>
    public static bool Loops(AudioStream? s) => s switch
    {
        AudioStreamWav w => w.LoopMode != AudioStreamWav.LoopModeEnum.Disabled,
        AudioStreamOggVorbis o => o.Loop,
        _ => false,
    };

    private readonly Dictionary<string, (int Priority, int MaxVoices)> _limits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sound's priority and how many voices of it may play at once (<see cref="SoundFiles.Limits"/>).</summary>
    public (int Priority, int MaxVoices) Limits(string name) =>
        _limits.TryGetValue(name, out var l) ? l : _limits[name] = SoundFiles.Limits(vfs, name);

    /// <summary>The sound's GAS0 volume in percent (<see cref="SoundFiles.Volume(AssetVfs, string)"/>).</summary>
    public int Volume(string name) => SoundFiles.Volume(vfs, name);

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
