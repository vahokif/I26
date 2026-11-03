using System.Collections.Concurrent;
using Godot;
using I76.Formats.Subtitles;
using I76.Formats.Video;

namespace I76.Game.Ui;

/// <summary>
/// Plays one of the install's Smacker movies over everything, as
/// StartSmackerPlayback does: the intro and opening credits at startup, a
/// mission's briefing when its script asks (opcode 51). A thread decodes a
/// little ahead; the picture follows the clock, dropping frames if the
/// screen falls behind, and the sound streams as it is decoded. Esc, Space,
/// Enter or a click skips it. It runs while the tree is paused.
/// </summary>
public partial class MoviePlayer : CanvasLayer
{
    public required string Path { get; init; }
    /// <summary>The 1997 look: the picture sampled nearest, as the original's 640x480 surface.</summary>
    public bool Faithful { get; init; }
    /// <summary>Esc, Space, Enter, a click or a pad button skips it. Off in a network co-op game for all but the host (their Esc is the menu, over the movie).</summary>
    public bool Skippable { get; init; } = true;
    public event Action? Finished;

    private Smacker _movie = null!;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _decoded;
    private readonly ConcurrentQueue<(int Index, byte[] Rgba, byte[]? Pal)> _frames = new();
    private readonly ConcurrentQueue<Vector2[]> _sound = new();
    private const int Ahead = 20;          // frames decoded ahead of the picture
    private TextureRect _view = null!;
    private ImageTexture? _tex, _palTex, _blkTex;
    /// <summary>Palette-constrained debanding and deblocking in shaders: off by default (smooth-scaled raw frames); I76_MOVIE_DEBAND=1 turns it on.</summary>
    private bool _deband;
    private int _hold = int.TryParse(System.Environment.GetEnvironmentVariable("I76_MOVIE_HOLD"), out int h) ? h : -1;
    private SubViewport? _pass;
    private ColorRect? _debandRect;
    private AudioStreamGeneratorPlayback? _out;
    private Vector2[]? _pending;
    private int _pendingAt;
    private double _clock;
    private bool _started, _done;
    private SubtitleTrack? _subtitles;
    private Label? _subtitleLabel;
    private bool _showSubtitles;
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
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(black);
        _deband = !Faithful && System.Environment.GetEnvironmentVariable("I76_MOVIE_DEBAND") == "1";   // off by default (the user's call); =1 tries it
        _view = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = Faithful ? CanvasItem.TextureFilterEnum.Nearest : CanvasItem.TextureFilterEnum.Linear,
        };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
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
        if (_movie.HasAudio(0))
        {
            var gen = new AudioStreamGenerator { MixRate = _movie.SampleRate(0), BufferLength = 0.5f };
            var player = new AudioStreamPlayer { Stream = gen, Name = "Sound", Bus = "Voice" };
            AddChild(player);
            player.Play();
            _out = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
        }
        _subtitleLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 10,
        };
        _subtitleLabel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _subtitleLabel.OffsetLeft = 60;
        _subtitleLabel.OffsetRight = -60;
        _subtitleLabel.OffsetBottom = -36;
        _subtitleLabel.OffsetTop = -140;
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 22);
        _subtitleLabel.AddThemeColorOverride("font_color", new Color(1, 0.95f, 0.82f));
        _subtitleLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _subtitleLabel.AddThemeConstantOverride("outline_size", 6);
        AddChild(_subtitleLabel);
        _showSubtitles = I76.Game.Shell.ShellSettings.Load().Subtitles;
        _subtitles = SubtitleLoader.Load(Path);
        GD.Print($"movie {Path}: subtitles {(_subtitles != null ? $"{_subtitles.Cues.Count} cues" : "none")}");
        _thread = new Thread(Decode) { IsBackground = true, Name = "smacker" };
        _thread.Start();
    }

    private void Decode()
    {
        var m = _movie;
        int track = m.HasAudio(0) ? 0 : -1;
        bool wide = track >= 0 && m.Is16Bit(0), stereo = track >= 0 && m.Stereo(0);
        try
        {
            while (!_stop)
            {
                if (_frames.Count >= Ahead) { Thread.Sleep(5); continue; }
                int index = m.Position;
                if (!m.NextFrame((t, pcm) => { if (t == track) _sound.Enqueue(Samples(pcm, wide, stereo)); })) break;
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
        if (_done || _movie is null) return;
        // Pre-buffer frames and audio before starting playback so the queue never starves or drops frames
        if (!_started)
        {
            PushSound();
            if (_frames.Count < Math.Min(8, _movie.FrameCount) && !_decoded)
            {
                if (Watch(0) == MovieStep.NoFirstFrame) { GiveUp("no first frame"); Finish(); }
                return;
            }
            _started = true;
        }
        else _clock += delta;
        // capture hook: I76_MOVIE_HOLD=N freezes the picture on frame N, so shots of different runs compare
        if (_hold >= 0 && _frames.IsEmpty && !_decoded) _clock -= delta;   // (a capture runs faster than real time: wait for the decoder)
        if (_hold >= 0 && _clock >= _hold * _movie.FrameDuration) _clock = _hold * _movie.FrameDuration;
        PushSound();
        int want = (int)(_clock / _movie.FrameDuration);
        (int Index, byte[] Rgba, byte[]? Pal)? show = null;
        while (_frames.TryPeek(out var f) && f.Index <= want) { _frames.TryDequeue(out f); show = f; }
        if (show is { } s)
        {
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
        if (_subtitleLabel is not null)
        {
            if (!_showSubtitles)
            {
                _subtitleLabel.Visible = false;
            }
            else
            {
                _subtitles ??= SubtitleLoader.Load(Path);
                if (_subtitles is not null)
                {
                    var cue = _subtitles.CueAt(_clock);
                    string? text = cue is null ? null : SubtitleTrack.Page(cue.Text, _clock - cue.Start, cue.End - cue.Start);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (_subtitleLabel.Text != text) _subtitleLabel.Text = text;
                        _subtitleLabel.Visible = true;
                    }
                    else
                    {
                        _subtitleLabel.Visible = false;
                    }
                }
                else
                {
                    _subtitleLabel.Visible = false;
                }
            }
        }
        switch (Watch(want))
        {
            case MovieStep.Over: Finish(); break;
            case MovieStep.Stalled: GiveUp("the decoder stopped making frames"); Finish(); break;   // stuck part way: it ends there
        }
    }

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
            if (_pendingAt == 0 && n == _pending.Length)
            {
                _out.PushBuffer(_pending);
            }
            else
            {
                _out.PushBuffer(_pending.AsSpan(_pendingAt, n).ToArray());
            }
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
        Finish();
    }

    private void GiveUp(string why) => GD.PrintErr($"movie {Path}: {why}, ending it");

    private void Finish()
    {
        if (_done) return;
        _done = true;
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
