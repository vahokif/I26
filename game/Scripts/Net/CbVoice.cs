using Godot;
using I76.Formats.Input;
using I76.Net;

namespace I76.Game.Net;

/// <summary>
/// The CB radio in a network game (protocol 20, docs/multiplayer.md "CB voice"): hold the CB Transmit key (K) and the microphone goes out
/// to the players who may hear it; they hear it as over a CB, band-limited and squelched, on the game's Voice level. The wire, the codec,
/// the jitter buffer and the radio filter are I76.Net's (<see cref="VoiceSender"/>, <see cref="VoiceReceiver"/>, <see cref="RadioVoice"/>);
/// this node is the sound card and the key. No microphone is opened until the key is first pressed, and it is closed again after
/// <see cref="IdleCloseSeconds"/> of not talking. Your own voice is not played back (only the radio's click at the key).
/// </summary>
public partial class CbVoice : Node
{
    public required MeleeClient Client { get; init; }
    /// <summary>A player's name by slot, for the on-air list and the mute commands.</summary>
    public required Func<int, string> NameOf { get; init; }
    /// <summary>A player's team by slot (the HUD colours the name).</summary>
    public required Func<int, int> TeamOf { get; init; }
    /// <summary>The names to match a mute command against: every player in the game but this one.</summary>
    public required Func<IEnumerable<(int Slot, string Name)>> Players { get; init; }
    /// <summary>A line for the console (notices).</summary>
    public required Action<string> Say { get; init; }
    /// <summary>This game runs in this process (a listen host, or a successor after a migration): the host's commands work.</summary>
    public Func<bool>? IsHost { get; init; }
    /// <summary>/cbmute NAME and /cbunmute NAME on the server; returns why not, or null (the server then tells everyone).</summary>
    public Func<string, bool, string?>? HostMute { get; init; }
    /// <summary>The players the host has muted for everyone (the host's /cbmute alone lists them).</summary>
    public Func<IReadOnlyList<string>>? HostMutedList { get; init; }
    /// <summary>Whether the key may start a talk now (not over a menu or a picture).</summary>
    public Func<bool>? CanTalk { get; init; }

    public const double IdleCloseSeconds = 30, ReleaseTail = 0.12, MaxTalkSeconds = 28;
    /// <summary>The CB of the running network game (null outside one): Audio Control's CB Microphone reaches it here.</summary>
    public static CbVoice? Active { get; private set; }
    private const string MicBus = "CbMic";

    /// <summary>CB Voice in Play Options: off neither sends nor plays (also /cb on and /cb off for this game).</summary>
    public bool Enabled { get; set; } = I76.Game.Shell.ShellSettings.Load().CbVoice;
    private string _micDevice = I76.Game.Shell.ShellSettings.Load().CbMic;

    /// <summary>The key is down (or the pad's Misc1 button).</summary>
    private bool _held, _padHeld;
    /// <summary>This player is on the air.</summary>
    public bool Transmitting { get; private set; }
    /// <summary>The key has been used once (the HUD's reminder is gone).</summary>
    public bool Used { get; private set; }
    /// <summary>The host turned the CB off for the game: nothing is sent, played or opened.</summary>
    public bool Off => !Client.CbAllowed;
    private bool _deniedSaid;
    private double _ignoreAt;
    private bool _ignoreAll;
    private string _ignoreSent = "";

    private static readonly HashSet<string> Muted = new(StringComparer.OrdinalIgnoreCase);

    // ---- debug hooks (docs/dev-hooks.md): a WAV as the microphone, and the played-back audio written out ----
    private readonly string? _inputFile = System.Environment.GetEnvironmentVariable("I76_CB_INPUT") is { Length: > 0 } i ? i : null;
    private readonly string? _recordFile = System.Environment.GetEnvironmentVariable("I76_CB_RECORD") is { Length: > 0 } r ? r : null;
    private float[]? _fileSamples;
    private int _filePos;
    private double _fileStart = -1, _fileAcc;
    private readonly double _fileDelay = double.TryParse(System.Environment.GetEnvironmentVariable("I76_CB_AT"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 4;
    private readonly bool _quitWhenDone = System.Environment.GetEnvironmentVariable("I76_CB_QUIT") == "1";
    private double _quitAt = -1;
    private readonly List<float> _record = [];
    private int _recordedAt;

    // ---- sending ----
    private readonly VoiceSender _sender = new();
    private AudioStreamPlayer? _mic;
    private AudioEffectCapture? _capture;
    private VoiceResampler? _down;
    private readonly List<float> _pcm = [];
    private double _txStart, _lastTx = double.NegativeInfinity, _releaseAt = -1;
    private bool _capped;
    private bool _noMicSaid;

    // ---- receiving ----
    private readonly VoiceReceiver _rx = new();
    private sealed class Talker
    {
        public AudioStreamPlayer Player = null!;
        public AudioStreamGeneratorPlayback Playback = null!;
        public int Capacity;
        public VoiceResampler Up = null!;
        public RadioVoice Radio = null!;
        public readonly Queue<float> Pending = new();
        public double LastHeard = double.NegativeInfinity;
        public int Slot;
    }
    private readonly Dictionary<int, Talker> _talkers = [];
    private Talker? _local;
    private float _mixRate = 44100;
    private double _t;

    /// <summary>The players on the air now (heard in the last half second), for the HUD.</summary>
    public IEnumerable<(string Name, int Team)> Talking => _talkers.Values.Where(t => t.Slot >= 0 && _t - t.LastHeard < 0.5).Select(t => (NameOf(t.Slot), TeamOf(t.Slot)));

    public override void _Ready()
    {
        Active = this;
        _mixRate = AudioServer.GetMixRate();
        ProcessMode = ProcessModeEnum.Always;
        if (_inputFile is not null)
        {
            try
            {
                var (s, rate) = WavIo.Read(System.IO.File.ReadAllBytes(_inputFile));
                var o = new List<float>();
                new VoiceResampler(rate, VoiceWire.SampleRate).Process(s, o);
                _fileSamples = [.. o];
                GD.Print($"[cb] test input {_inputFile}: {_fileSamples.Length / (double)VoiceWire.SampleRate:0.0} s, sent {_fileDelay:0.#} s after joining");
            }
            catch (Exception ex) { GD.PrintErr($"[cb] test input {_inputFile}: {ex.Message}"); }
        }
    }

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
        StopMic();
        WriteRecord();
    }

    // ---- the key ----

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            // a pad: the Share / Mic button (Misc1), the one standard button the pad preset leaves unbound
            case InputEventJoypadButton { ButtonIndex: JoyButton.Misc1 } b when GodotPads.IsStandard(b.Device):
                _padHeld = b.Pressed && !Controls.Suspended;
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut) { _held = false; _padHeld = false; }
    }

    // ---- the frame ----

    public override void _Process(double delta)
    {
        _t += delta;
        bool joined = Client.State == ClientState.Joined;
        bool fileOn = _fileSamples is not null && joined && (Client.Scores?.Players.Count ?? 0) >= 2;
        if (fileOn && _fileStart < 0) _fileStart = _t;
        bool fileTalking = fileOn && _t - _fileStart >= _fileDelay && _filePos < _fileSamples!.Length;
        _held = Controls.Down("POETRY");      // the Poem key: nothing else uses it in a network game
        bool pressed = (_held || _padHeld) && CanTalk?.Invoke() != false;
        if (!pressed) _capped = false;
        // the host's switches: CB off for the game, or this player muted for everyone: the key does nothing, and says why once a press
        bool allowed = !Off && !Client.CbMuted;
        if (!pressed && !fileTalking) _deniedSaid = false;
        else if (!allowed && Enabled && joined && !_deniedSaid) { _deniedSaid = true; Say(Off ? "CB is off in this game" : "CB: you are muted by the host"); }
        bool want = Enabled && allowed && joined && !_capped && (pressed || fileTalking);
        if (want) _releaseAt = _t + ReleaseTail;
        else if (Transmitting && _t < _releaseAt && Enabled && allowed && joined) want = true;      // the key's tail, so the last word is not clipped
        if (pressed && allowed) Used = true;
        if (pressed && Enabled && allowed && joined && !fileOn && !_noMicSaid && !HasMic()) { _noMicSaid = true; Say("CB: no microphone found (check the system's input device, or CB Microphone in Audio Control)"); }
        if (want && !Transmitting) StartTalk(fileTalking);
        else if (!want && Transmitting) StopTalk();
        if (Transmitting)
        {
            if (fileTalking) FeedFile();
            else PumpMic();
            if (_t - _txStart > MaxTalkSeconds) { _capped = true; Say("CB: you have been on the air a long time; let go of the key and press it again to go on"); StopTalk(); }
        }
        else if (_capture is not null)
        {
            _capture.ClearBuffer();
            if (_t - _lastTx > IdleCloseSeconds) StopMic();
        }
        Receive();
        TellIgnored(joined);
        Playback();
        if (_quitAt >= 0 && _t > _quitAt) { _quitAt = -1; GetTree().Quit(); }
        // capture hook: I76_CB_SHOT=out.png saves the view 1.5 s into this player's transmission (the handset up, the HUD's CB rows)
        if (Transmitting && _shotAt < 0 && System.Environment.GetEnvironmentVariable("I76_CB_SHOT") is { Length: > 0 } shot && _t - _txStart > 1.5)
        {
            _shotAt = _t;
            GetViewport().GetTexture().GetImage().SavePng(shot);
            GD.Print($"[cb] saved {shot}");
        }
    }
    private double _shotAt = -1;

    /// <summary>
    /// Tells the server whom this player does not want to hear (everyone with CB Voice off, else the players /mute named), whenever that
    /// changes or a player comes and goes: the server then does not relay their voice here, which saves the bandwidth.
    /// </summary>
    private void TellIgnored(bool joined)
    {
        if (!joined || _t < _ignoreAt) return;
        _ignoreAt = _t + 0.5;
        bool all = !Enabled;
        var slots = all ? [] : Players().Where(p => Muted.Contains(p.Name)).Select(p => p.Slot).Order().ToList();
        string key = (all ? "*" : "") + string.Join(",", slots);
        if (key == _ignoreSent) return;
        _ignoreSent = key;
        Client.SendVoiceIgnore(all, slots);
    }

    private static bool HasMic()
    {
        try { return AudioServer.GetInputDeviceList().Length > 0; } catch { return false; }
    }

    // ---- sending ----

    private void StartTalk(bool fromFile)
    {
        Transmitting = true;
        _txStart = _t;
        _pcm.Clear();
        if (!fromFile) OpenMic();
        _capture?.ClearBuffer();
        _down = new VoiceResampler((int)_mixRate, VoiceWire.SampleRate);
        Local(true);
        GD.Print("[cb] transmitting");
    }

    private void StopTalk()
    {
        Transmitting = false;
        _lastTx = _t;
        _sender.End(p => Client.SendVoice(p));
        Local(false);
        GD.Print("[cb] stopped transmitting");
    }

    private void OpenMic()
    {
        if (_mic is not null) return;
        int bus = AudioServer.GetBusIndex(MicBus);
        if (bus < 0)
        {
            AudioServer.AddBus();
            bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, MicBus);
            AudioServer.SetBusMute(bus, true);          // the microphone is captured, never heard
            AudioServer.AddBusEffect(bus, new AudioEffectCapture { BufferLength = 0.5f });
        }
        _capture = AudioServer.GetBusEffect(bus, 0) as AudioEffectCapture;
        if (_micDevice.Length > 0) AudioServer.InputDevice = _micDevice;
        _mic = new AudioStreamPlayer { Name = "CbMic", Stream = new AudioStreamMicrophone(), Bus = MicBus, ProcessMode = ProcessModeEnum.Always };
        AddChild(_mic);
        _mic.Play();
    }

    /// <summary>
    /// CB Microphone changed in the in-game Audio Control: the next talk uses the new input device, and an open microphone (or a
    /// transmission in progress) moves to it at once, without leaving the game or the session.
    /// </summary>
    public void ApplyMic(string device)
    {
        if (device == _micDevice) return;
        _micDevice = device;
        try { AudioServer.InputDevice = device.Length > 0 ? device : "Default"; } catch { }
        if (_mic is null) return;
        _mic.Stop();            // the stream is opened again on the new device; what the old one had buffered is dropped
        _capture?.ClearBuffer();
        _mic.Play();
    }

    private void StopMic()
    {
        if (_mic is null) return;
        _mic.Stop();
        _mic.QueueFree();
        _mic = null;
        _capture?.ClearBuffer();
        _capture = null;
    }

    private void PumpMic()
    {
        if (_capture is null || _down is null) return;
        int n = _capture.GetFramesAvailable();
        if (n <= 0) return;
        var frames = _capture.GetBuffer(n);
        var mono = new float[frames.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = (frames[i].X + frames[i].Y) * 0.5f;
        _pcm.Clear();
        _down.Process(mono, _pcm);
        _sender.Feed(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pcm), p => Client.SendVoice(p));
    }

    private void FeedFile()
    {
        // real time, whatever the frame rate: 8000 samples a second of wall clock
        double now = Time.GetTicksMsec() / 1000.0;
        if (_fileAcc == 0) _fileAcc = now;
        int n = Math.Min((int)((now - _fileAcc) * VoiceWire.SampleRate), _fileSamples!.Length - _filePos);
        if (n <= 0) return;
        _fileAcc += n / (double)VoiceWire.SampleRate;
        _sender.Feed(_fileSamples.AsSpan(_filePos, n), p => Client.SendVoice(p));
        _filePos += n;
        if (_filePos >= _fileSamples.Length) { GD.Print("[cb] test input sent"); if (_quitWhenDone) _quitAt = _t + 8; }
    }

    // ---- receiving ----

    private void Receive()
    {
        while (Client.VoiceIn.Count > 0)
        {
            var p = Client.VoiceIn.Dequeue();
            if (!Enabled || Off || Muted.Contains(NameOf(p.Slot))) continue;
            _rx.Push(p);
            if (!_talkers.ContainsKey(p.Slot)) _talkers[p.Slot] = MakeTalker(p.Slot);
        }
    }

    private Talker MakeTalker(int slot)
    {
        var t = new Talker { Slot = slot, Up = new VoiceResampler(VoiceWire.SampleRate, (int)_mixRate), Radio = new RadioVoice() };
        // on the Voice bus: the Audio Control's Voice level is the volume
        var gen = new AudioStreamGenerator { MixRate = _mixRate, BufferLength = 0.4f };
        t.Player = new AudioStreamPlayer { Name = slot >= 0 ? $"CbTalker{slot}" : "CbLocal", Stream = gen, Bus = "Voice", ProcessMode = ProcessModeEnum.Always };
        AddChild(t.Player);
        t.Player.Play();
        t.Playback = (AudioStreamGeneratorPlayback)t.Player.GetStreamPlayback();
        t.Capacity = t.Playback.GetFramesAvailable();
        return t;
    }

    /// <summary>Samples at the voice rate onto a talker's line, resampled to the mix rate.</summary>
    private void Enqueue(Talker t, ReadOnlySpan<float> s8, bool record)
    {
        var o = new List<float>(s8.Length * 6);
        t.Up.Process(s8, o);
        foreach (float v in o) t.Pending.Enqueue(v);
        if (record && _recordFile is not null) _record.AddRange(o);
    }

    /// <summary>The squelch at this player's own key down and key up: the radio's click, so the key is felt; the voice itself is not played back.</summary>
    private void Local(bool open)
    {
        if (!Enabled) return;
        _local ??= MakeTalker(-1);
        Enqueue(_local, _local.Radio.Squelch(open), false);
    }

    private void Playback()
    {
        var frame = new float[VoiceWire.FrameSamples];
        int target = (int)(_mixRate * 0.09);
        foreach (var t in _talkers.Values.Append(_local).OfType<Talker>().ToList())
        {
            while (t.Capacity - t.Playback.GetFramesAvailable() < target)
            {
                if (t.Pending.Count == 0 && t.Slot >= 0 && Enabled)
                {
                    switch (_rx.Pull((byte)t.Slot, frame, out bool started))
                    {
                        case VoiceReceiver.PullKind.Frame:
                            t.LastHeard = _t;
                            if (started) Enqueue(t, t.Radio.Squelch(true), true);
                            t.Radio.Process(frame);
                            Enqueue(t, frame, true);
                            break;
                        case VoiceReceiver.PullKind.End:
                            Enqueue(t, t.Radio.Squelch(false), true);
                            WriteRecord();
                            break;
                    }
                }
                if (t.Pending.Count == 0) break;
                int n = Math.Min(t.Pending.Count, 512);
                var buf = new Vector2[n];
                for (int i = 0; i < n; i++) { float v = t.Pending.Dequeue(); v = Math.Clamp(v, -1f, 1f); buf[i] = new Vector2(v, v); }
                t.Playback.PushBuffer(buf);
            }
        }
    }

    private void WriteRecord()
    {
        if (_recordFile is null || _record.Count == _recordedAt) return;
        _recordedAt = _record.Count;
        try
        {
            System.IO.File.WriteAllBytes(_recordFile, WavIo.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_record), (int)_mixRate));
            GD.Print($"[cb] recorded {_record.Count / _mixRate:0.0} s to {_recordFile}");
            if (_quitWhenDone && _record.Count > _mixRate * 2 && _quitAt < 0) _quitAt = _t + 2;
        }
        catch (Exception ex) { GD.PrintErr($"[cb] record: {ex.Message}"); }
    }

    // ---- chat commands ----

    /// <summary>
    /// The chat line's CB commands: /mute NAME and /unmute NAME (for this player only), /cbmute NAME and /cbunmute NAME (for everyone: the host's),
    /// /cb on|off. Returns whether the line was one (it is then not sent).
    /// </summary>
    public bool Command(string text)
    {
        var w = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0 || !w[0].StartsWith('/')) return false;
        string arg = w.Length > 1 ? w[1].Trim() : "";
        switch (w[0].ToLowerInvariant())
        {
            case "/cb":
                if (arg.Equals("on", StringComparison.OrdinalIgnoreCase)) Enabled = true;
                else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) Enabled = false;
                else { Say($"CB voice is {(Enabled ? "on" : "off")} (/cb on, /cb off; /mute NAME, /unmute NAME; the host: /cbmute NAME, /cbunmute NAME)"); return true; }
                Say($"CB voice {(Enabled ? "on" : "off")} for this game");
                return true;
            case "/cbmute" or "/cbunmute":
            {
                bool mute = w[0].Equals("/cbmute", StringComparison.OrdinalIgnoreCase);
                if (IsHost?.Invoke() != true || HostMute is null) { Say("CB: only the host can mute a player for everyone (/mute NAME mutes them for you)"); return true; }
                if (arg.Length == 0 && mute)
                {
                    var list = HostMutedList?.Invoke() ?? [];
                    Say(list.Count == 0 ? "CB: nobody is muted for everyone (/cbmute NAME)" : "CB: muted for everyone: " + string.Join(", ", list));
                    return true;
                }
                if (HostMute(arg, mute) is { } why) Say(why);      // the server tells everyone when it is done
                return true;
            }
            case "/mute" or "/unmute":
            {
                bool mute = w[0].Equals("/mute", StringComparison.OrdinalIgnoreCase);
                if (arg.Length == 0) { Say(Muted.Count == 0 ? "Nobody is muted (/mute NAME)" : "Muted: " + string.Join(", ", Muted)); return true; }
                if (PickPlayer(arg) is not { } who) { Say($"CB: no one called \"{arg}\" (or more than one)"); return true; }
                if (mute) Muted.Add(who); else Muted.Remove(who);
                Say(mute ? $"CB: {who} is muted" : $"CB: {who} is not muted");
                return true;
            }
        }
        return false;
    }

    private string? PickPlayer(string typed) => VoiceNames.Match(Players().Select(p => p.Name), typed);

    public bool IsMuted(string name) => Muted.Contains(name);
}
