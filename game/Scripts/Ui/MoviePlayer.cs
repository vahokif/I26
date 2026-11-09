using System.Collections.Concurrent;
using Godot;
using I76.Formats.Video;

namespace I76.Game.Ui;

/// <summary>
/// Plays one of the install's Smacker movies over everything, as
/// StartSmackerPlayback does: the intro and opening credits at startup, a
/// mission's briefing when its script asks (opcode 51). A thread decodes a
/// little ahead; the picture follows the clock, dropping frames if the
/// screen falls behind, and the sound streams as it is decoded. Esc, Space,
/// Enter or a click skips it. It runs while the tree is paused.
/// With HiFi Audio on and a track for the movie in the pack, that track plays
/// in place of the movie's own sound (track 0, the only one the games' movies
/// have): it starts with the first frame and the picture follows it as it is
/// heard (<see cref="MovieClock"/>); the end logs how the two met.
/// </summary>
public partial class MoviePlayer : CanvasLayer
{
    public required string Path { get; init; }
    /// <summary>The 1997 look: the picture sampled nearest, as the original's 640x480 surface.</summary>
    public bool Faithful { get; init; }
    /// <summary>Esc, Space, Enter, a click or a pad button skips it. Off in a network co-op game for all but the host (their Esc is the menu, over the movie).</summary>
    public bool Skippable { get; init; } = true;
    /// <summary>The Nitro Pack's movie (HiFi Audio's movies/nitro/ wins then; both games have a CREDF01 and an INTROF01).</summary>
    public bool Nitro { get; init; }
    /// <summary>
    /// Played while a mission loads on the main thread under it (a briefing over its build, Main.LoadUnderBriefing), whose steps
    /// hold the picture for up to a second or two: the movie's own sound is decoded and queued further ahead, so it plays on through
    /// them (the picture catches up after, by the clock).
    /// </summary>
    public bool OverLoad { get; init; }
    /// <summary>
    /// While true (a briefing played while its mission loads, until the mission can start): <see cref="WaitingTag"/> over the
    /// picture, the skip keys do nothing (not kept for later), and a movie that runs out holds its last frame until it turns
    /// false, then ends.
    /// </summary>
    public Func<bool>? Waiting { get; init; }
    /// <summary>Shown over the picture while <see cref="Waiting"/> (the boot screen's "please stand by", small, in a corner: StandByTag).</summary>
    public StandByTag? WaitingTag { get; init; }
    private bool IsWaiting => Waiting?.Invoke() == true;
    private StandByTag? _tag;
    /// <summary>The movie ran out while <see cref="Waiting"/>: its last frame stays up until the mission can start.</summary>
    private bool _held;
    /// <summary>Capture hook: I76_MOVIE_END=N runs the movie out N drawn frames after its first, as its end would (held while waiting).</summary>
    private int _endAfter = int.TryParse(System.Environment.GetEnvironmentVariable("I76_MOVIE_END"), out int ea) ? ea : -1;
    public event Action? Finished;

    private Smacker _movie = null!;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _decoded;
    private readonly ConcurrentQueue<(int Index, byte[] Rgba, byte[]? Pal)> _frames = new();
    private readonly ConcurrentQueue<Vector2[]> _sound = new();
    private int Ahead => OverLoad ? 60 : 20;   // frames decoded ahead of the picture (4 s over a load, its sound with them)
    private TextureRect _view = null!;
    private ImageTexture? _tex, _palTex, _blkTex;
    /// <summary>Palette-constrained debanding and deblocking in shaders: off by default (smooth-scaled raw frames); I76_MOVIE_DEBAND=1 turns it on.</summary>
    private bool _deband;
    private int _hold = int.TryParse(System.Environment.GetEnvironmentVariable("I76_MOVIE_HOLD"), out int h) ? h : -1;
    /// <summary>Capture hook: I76_MOVIE_SKIP=N skips the movie N drawn frames after its first, as a key would (a briefing ended early).</summary>
    private int _skipAfter = int.TryParse(System.Environment.GetEnvironmentVariable("I76_MOVIE_SKIP"), out int sk) ? sk : -1;
    private SubViewport? _pass;
    private ColorRect? _debandRect;
    private AudioStreamGeneratorPlayback? _out;
    private Vector2[]? _pending;
    private int _pendingAt;
    private MovieClock _clock = null!;
    private bool _started, _done;
    /// <summary>HiFi Audio's track in place of the movie's own sound, or null.</summary>
    private AudioStreamPlayer? _hifi;
    private (string Entry, double Seconds, double Own) _hifiInfo;
    private long _hifiStart;
    private bool _hifiTold;
    /// <summary>Frames the decoder has made (its thread counts them), for the watch on a decoder that stops.</summary>
    private volatile int _produced;
    private readonly MovieWatch _watch = new();

    public override void _Ready()
    {
        Layer = Skippable ? 100 : 30;   // an unskippable movie is under the menu a player's Esc opens
        ProcessMode = ProcessModeEnum.Always;
        try { _movie = Smacker.Open(Path); }
        catch (System.Exception ex)
        {
            GD.PrintErr($"movie {Path}: {ex.Message}");
            CallDeferred(MethodName.Finish);
            return;
        }
        _clock = new MovieClock(_movie.FrameDuration);
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        _deband = !Faithful && System.Environment.GetEnvironmentVariable("I76_MOVIE_DEBAND") == "1";   // off by default (the user's call); =1 tries it
        _view = new TextureRect
        {
            AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = Faithful ? CanvasItem.TextureFilterEnum.Nearest : CanvasItem.TextureFilterEnum.Linear,
        };
        AddChild(_view);
        if (_deband)
        {
            // pass 1 at the movie's own size into a half-float target (so the smoothed colours keep their precision), pass 2 scales it up
            _pass = new SubViewport
            {
                Size = new Vector2I(_movie.Width, _movie.Height), UseHdr2D = true, Disable3D = true, TransparentBg = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always, GuiDisableInput = true,
            };
            _debandRect = new ColorRect
            {
                Size = new Vector2(_movie.Width, _movie.Height), MouseFilter = Control.MouseFilterEnum.Ignore,
                Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/movie_deband.gdshader") },
            };
            _pass.AddChild(_debandRect);
            AddChild(_pass);
            _view.Texture = _pass.GetTexture();
            _view.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/movie_deband_view.gdshader") };
        }
        // HiFi Audio: the pack's track for this movie, which starts with its first frame (_Process); none, and the movie's own sound streams
        if (_movie.HasAudio(0) && _hold < 0)
        {
            long own = _movie.AudioSamples(0);
            double ownSeconds = own / (double)_movie.SampleRate(0);
            if (Assets.HiFiAudio.LoadMovie(System.IO.Path.GetFileName(Path), Nitro, ownSeconds) is { } found)
            {
                var (stream, ogg, entry) = found;
                _hifi = new AudioStreamPlayer { Stream = stream, Name = "HiFiSound" };
                AddChild(_hifi);
                _hifiInfo = (entry, ogg.Seconds, ownSeconds);
                GD.Print($"movie {System.IO.Path.GetFileName(Path)}: HiFi Audio {entry} ({ogg.Channels} ch, {ogg.Rate} Hz, {ogg.Samples} samples, {ogg.Seconds:0.000} s) "
                    + $"in place of its own track ({_movie.SampleRate(0)} Hz, {(_movie.Is16Bit(0) ? 16 : 8)}-bit {(_movie.Stereo(0) ? "stereo" : "mono")}, {own} samples, {ownSeconds:0.000} s; "
                    + $"the picture {_movie.FrameCount} frames, {_movie.FrameCount * _movie.FrameDuration:0.000} s)");
            }
        }
        if (_movie.HasAudio(0) && _hifi is null)
        {
            var gen = new AudioStreamGenerator { MixRate = _movie.SampleRate(0), BufferLength = OverLoad ? 4.5f : 0.5f };
            var player = new AudioStreamPlayer { Stream = gen, Name = "Sound" };
            AddChild(player);
            player.Play();
            _out = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
        }
        if (WaitingTag is { } tag)
        {
            tag.Picture = new Vector2(_movie.Width, _movie.Height);
            AddChild(_tag = tag);
        }
        _thread = new Thread(Decode) { IsBackground = true, Name = "smacker" };
        _thread.Start();
    }

    private void Decode()
    {
        var m = _movie;
        int track = m.HasAudio(0) && _hifi is null ? 0 : -1;   // (HiFi Audio's track in its place: the movie's own is not unpacked)
        bool wide = track >= 0 && m.Is16Bit(0), stereo = track >= 0 && m.Stereo(0);
        try
        {
            while (!_stop)
            {
                if (_frames.Count >= Ahead) { Thread.Sleep(5); continue; }
                int index = m.Position;
                if (!m.NextFrame(track < 0 ? null : (t, pcm) => { if (t == track) _sound.Enqueue(Samples(pcm, wide, stereo)); })) break;
                var px = m.Pixels; var pal = m.Palette;
                if (_deband)
                {
                    var rgbaPal = new byte[1024 + _movie.BlockTypes.Length];   // the palette, then the block types
                    for (int i = 0; i < 256; i++) { rgbaPal[i * 4] = pal[i * 3]; rgbaPal[i * 4 + 1] = pal[i * 3 + 1]; rgbaPal[i * 4 + 2] = pal[i * 3 + 2]; rgbaPal[i * 4 + 3] = 255; }
                    _movie.BlockTypes.CopyTo(rgbaPal, 1024);
                    _frames.Enqueue((index, (byte[])px.Clone(), rgbaPal));
                    _produced++;
                    continue;
                }
                var rgba = new byte[m.Width * m.Height * 4];
                for (int i = 0, o = 0; i < px.Length; i++, o += 4)
                {
                    int c = px[i] * 3;
                    rgba[o] = pal[c]; rgba[o + 1] = pal[c + 1]; rgba[o + 2] = pal[c + 2]; rgba[o + 3] = 255;
                }
                _frames.Enqueue((index, rgba, null));
                _produced++;
            }
        }
        catch (System.Exception ex) { GD.PrintErr($"movie {Path}: {ex.Message}"); }
        _decoded = true;
    }

    /// <summary>PCM as the generator's stereo float frames: unsigned 8-bit or signed 16-bit, mono or interleaved.</summary>
    private static Vector2[] Samples(byte[] pcm, bool wide, bool stereo)
    {
        int bytes = wide ? 2 : 1, ch = stereo ? 2 : 1, n = pcm.Length / (bytes * ch);
        var o = new Vector2[n];
        float S(int i) => wide ? (short)(pcm[i] | (pcm[i + 1] << 8)) / 32768f : (pcm[i] - 128) / 128f;
        for (int i = 0; i < n; i++)
        {
            float l = S(i * bytes * ch), r = stereo ? S((i * ch + 1) * bytes) : l;
            o[i] = new Vector2(l, r);
        }
        return o;
    }

    public override void _Process(double delta)
    {
        if (_tag is not null && !IsWaiting) _tag.FadeOut();   // (and from then on a skip ends the movie)
        if (_held) { if (!IsWaiting) Finish(); return; }
        if (_done || _movie is null) return;
        // the clock starts with the first frame, so a slow first decode does not eat into the picture
        // (a decoder that died or never delivers ends the movie instead: nothing else would ever start the clock, and the
        // black under the picture would stay over the paused game, MovieWatch)
        if (!_started)
        {
            if (_frames.IsEmpty)
            {
                if (Watch(0) == MovieStep.NoFirstFrame) { GiveUp("no first frame"); RunOut(); }
                return;
            }
            _started = true;
            I76.Game.World.MissionBuilder.Trace($"movie: first frame of {System.IO.Path.GetFileName(Path)}");
            if (_hifi is not null) { _hifi.Play(); _hifiStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }
        else _clock.Step(delta, Heard());
        if (_skipAfter >= 0 && _skipAfter-- == 0) { GD.Print($"movie {System.IO.Path.GetFileName(Path)}: I76_MOVIE_SKIP skips it{(IsWaiting ? " (ignored: the mission is not ready)" : "")}"); if (!IsWaiting) { Finish(); return; } }
        if (_endAfter >= 0 && _endAfter-- == 0) { GD.Print($"movie {System.IO.Path.GetFileName(Path)}: I76_MOVIE_END runs it out"); RunOut(); return; }
        // capture hook: I76_MOVIE_HOLD=N freezes the picture on frame N, so shots of different runs compare
        if (_hold >= 0 && _frames.IsEmpty && !_decoded) _clock.Set(_clock.Seconds - delta);   // (a capture runs faster than real time: wait for the decoder)
        if (_hold >= 0 && _clock.Seconds >= _hold * _movie.FrameDuration) _clock.Set(_hold * _movie.FrameDuration);
        PushSound();
        int want = _clock.Frame;
        (int Index, byte[] Rgba, byte[]? Pal)? show = null;
        while (_frames.TryPeek(out var f) && f.Index <= want) { _frames.TryDequeue(out f); show = f; }
        if (show is { } s)
        {
            if (s.Index == _movie.FrameCount - 1) TellSync();
            if (s.Pal is not null)
            {
                var idx = Image.CreateFromData(_movie.Width, _movie.Height, false, Image.Format.R8, s.Rgba);
                var pal = Image.CreateFromData(256, 1, false, Image.Format.Rgba8, s.Pal[..1024]);
                var blk = Image.CreateFromData(_movie.Width / 4, _movie.Height / 4, false, Image.Format.R8, s.Pal[1024..]);
                if (_tex is null)
                {
                    _tex = ImageTexture.CreateFromImage(idx); _palTex = ImageTexture.CreateFromImage(pal); _blkTex = ImageTexture.CreateFromImage(blk);
                    var mat = (ShaderMaterial)_debandRect!.Material;
                    if (System.Environment.GetEnvironmentVariable("I76_MOVIE_DEBLOCK") == "0") mat.SetShaderParameter("deblock_strength", 0f);
                    mat.SetShaderParameter("idx", _tex); mat.SetShaderParameter("pal", _palTex); mat.SetShaderParameter("blk", _blkTex);
                }
                else { _tex.Update(idx); _palTex!.Update(pal); _blkTex!.Update(blk); }
            }
            else
            {
                var img = Image.CreateFromData(_movie.Width, _movie.Height, false, Image.Format.Rgba8, s.Rgba);
                if (_tex is null) { _tex = ImageTexture.CreateFromImage(img); _view.Texture = _tex; }
                else _tex.Update(img);
            }
        }
        switch (Watch(want))
        {
            case MovieStep.Over: RunOut(); break;
            case MovieStep.Stalled: GiveUp("the decoder stopped making frames"); RunOut(); break;   // stuck part way: it ends there
        }
    }

    /// <summary>HiFi Audio's track position as heard now (seconds from its first sample), or null when none plays.</summary>
    private double? Heard() =>
        _hifi is { } p && p.Playing ? p.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - AudioServer.GetOutputLatency() : null;

    /// <summary>
    /// The drift check, as the last frame goes up: where HiFi Audio's track is against that frame's time (the picture follows the
    /// track, so this is under a display frame), the wall clock's view of the same, and the track's end against the picture's.
    /// </summary>
    private void TellSync()
    {
        if (_hifi is null || _hifiTold) return;
        _hifiTold = true;
        double frame = (_movie.FrameCount - 1) * _movie.FrameDuration, end = _movie.FrameCount * _movie.FrameDuration;
        double wall = System.Diagnostics.Stopwatch.GetElapsedTime(_hifiStart).TotalSeconds;
        string heard = Heard() is { } h ? $"{h:0.000} s ({Signed((h - frame) * 1000, "0.0")} ms)" : "over";
        GD.Print($"movie {System.IO.Path.GetFileName(Path)}: HiFi Audio sync: last frame {_movie.FrameCount - 1} (due {frame:0.000} s) up at clock {_clock.Seconds:0.000} s, "
            + $"the track heard at {heard}, wall {wall:0.000} s; the track ends at {_hifiInfo.Seconds:0.000} s, the picture at {end:0.000} s "
            + $"({Signed((_hifiInfo.Seconds - end) * 1000, "0.0")} ms, {Signed((_hifiInfo.Seconds - end) / _movie.FrameDuration, "0.00")} frames), its own track at {_hifiInfo.Own:0.000} s");
    }

    private static string Signed(double v, string format) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    private MovieStep Watch(int want) =>
        _watch.Step(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency, _produced, _decoded, !_frames.IsEmpty, _started, want, _movie.FrameCount);

    private void PushSound()
    {
        if (_out is null) { _sound.Clear(); return; }
        int room = _out.GetFramesAvailable();
        while (room > 0)
        {
            if (_pending is null || _pendingAt >= _pending.Length)
            {
                if (!_sound.TryDequeue(out _pending)) return;
                _pendingAt = 0;
            }
            int n = Math.Min(room, _pending.Length - _pendingAt);
            _out.PushBuffer(_pending.AsSpan(_pendingAt, n).ToArray());
            _pendingAt += n;
            room -= n;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_done || !Skippable) return;
        bool skip = e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape or Key.Space or Key.Enter or Key.KpEnter }
            || e is InputEventMouseButton { Pressed: true }
            || e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.A or JoyButton.B or JoyButton.Start };   // any pad: A, B, Start (not only one the pad list knows yet: the intro plays before it has looked)
        if (!skip) return;
        GetViewport().SetInputAsHandled();
        if (IsWaiting) return;   // the mission cannot start yet: the press does nothing (and nothing else sees it)
        Finish();
    }

    /// <summary>The movie is over by itself: it ends, or, while <see cref="Waiting"/>, its last frame stays up under the stand-by line until the mission can start.</summary>
    private void RunOut()
    {
        if (!IsWaiting) { Finish(); return; }
        if (_held) return;
        _held = true;
        _stop = true;   // (the decoder is done with: only the last frame stays)
        _hifi?.Stop();
        GD.Print($"movie {System.IO.Path.GetFileName(Path)}: over, its last frame held until the mission can start");
    }

    private void GiveUp(string why) => GD.PrintErr($"movie {Path}: {why}, ending it");

    /// <summary>Ends the movie at once, as a skip does (what follows it runs): a mission whose build under its briefing failed.</summary>
    public void End() => Finish();

    private void Finish()
    {
        if (_done) return;
        _done = true;
        if (_hifi is not null && !_hifiTold && _started)
            GD.Print($"movie {System.IO.Path.GetFileName(Path)}: HiFi Audio: ended at clock {_clock.Seconds:0.000} s (frame {_clock.Frame} of {_movie.FrameCount}), the track heard at {(Heard() is { } h ? $"{h:0.000} s" : "its end")}");
        _stop = true;
        // a decoder stuck inside a frame is left to itself (a background thread) rather than holding the game
        if (_thread is null || _thread.Join(1000)) _movie?.Dispose();
        // gone first, whatever follows: a failure in what comes next must not leave the movie's black over the game
        QueueFree();
        try { Finished?.Invoke(); }
        catch (System.Exception ex) { GD.PrintErr($"movie {Path}: what follows it failed: {ex}"); }
    }

    public override void _ExitTree()
    {
        _stop = true;
        _thread?.Join(1000);
    }

    /// <summary>The install's smk/ file of that name (any case), or null.</summary>
    public static string? Find(I76.Formats.Vfs.GameInstall install, string name)
    {
        if (install.FindDir("smk") is not { } dir) return null;
        return System.IO.Directory.EnumerateFiles(dir).FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
