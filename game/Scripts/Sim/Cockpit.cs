using Godot;
using I76.Formats.Input;
using I76.Sim.Vehicles;

namespace I76.Game.Sim;

/// <summary>
/// The in-car view's live parts: the head (eye at
/// VLOC[0], 73.7° vertical field of view = the original's 90° horizontal on
/// 4:3, pitched −13°, the eight-state glance machine eased at 4·dt), the
/// panels composed into their textures (PRND21, compass, damage panel,
/// weapons and specials panels with their rolling counters), and the
/// animated strips (steering wheel, radar sweep, the arms). The panels are
/// kept up to date while the cockpit camera is current or the external
/// instruments are shown (hud || hud2d), the weapons panel
/// always; the head, wheel, arms, 3D needles and mirror only in
/// the car.
/// </summary>
public partial class Cockpit : Node
{
    public required Vehicle Car { get; init; }
    public required CarNode Node3D { get; init; }
    public required CockpitPanels Panels { get; init; }
    public required I76.Formats.Assembly.TextureResolver Resolver { get; init; }
    public required Assets.TextureFactory Textures { get; init; }
    public required Assets.MaterialLibrary Materials { get; init; }
    public I76.Sim.Weapons.Combat? Combat { get; init; }
    public Assets.AudioBank? Audio { get; init; }
    /// <summary>The art variant: the driver's (re-n3), 1 in the base game</summary>
    public int LightVariant { get; init; } = 1;
    /// <summary>The mirror's clear colour, palette index 0xea.</summary>
    public Color MirrorBackground { get; init; } = Colors.Black;
    /// <summary>The 1997 look: the mirror at the original's 256x64, nearest-sampled, and starting in objects-only mode.</summary>
    public bool Faithful
    {
        get => _faithful;
        set { _faithful = value; ApplyMirrorMode(); }
    }
    private bool _faithful;

    private bool Active => Node3D.CockpitCamera?.Current == true;

    /// <summary>
    /// Graphic Detail's Ext. Instrum. Disp. (or the `[` key): the 2D panels
    /// are drawn outside the car too, so
    /// they are updated there as well.
    /// </summary>
    public Func<bool>? Instruments { get; set; }

    /// <summary>The radar's current frame, as the 2D panel shows it (rows bottom-up), or null before the first sweep.</summary>
    public Texture2D? RadarTexture => _radarTex;

    /// <summary>Made by <see cref="ReadySteps"/>, a step at a time, by whoever added it (a mission loading under its briefing), not all in <see cref="_Ready"/>.</summary>
    public bool Staged { get; init; }

    public override void _Ready()
    {
        if (!Staged) foreach (var _ in ReadySteps()) { }
    }

    /// <summary>What <see cref="_Ready"/> makes (the animated panels' frames, the radar's, the mirror), a null between the costly parts.</summary>
    public IEnumerable<object?> ReadySteps()
    {
        _wheelFrames = Frames("zswls{0}{1:00}", 36);
        yield return null;
        _armR = Frames("zhr45{0}{1:00}", 6);
        _armL = Frames("zhl45{0}{1:00}", 6);
        _shotR = Frames("zsr45{0}{1:00}", 3);
        _shotL = Frames("zsl45{0}{1:00}", 3);
        yield return null;
        for (int i = 0; i <= 30; i++)
        {
            _radarF[i] = Image("zradf" + i.ToString("000"));
            _radarB[i] = Image("zradb" + i.ToString("000"));
            if (i % 10 == 9) yield return null;
        }
        _radarMember = Resolver.FindByStem("zradf000");
        _radarMask = Image("zradmask");
        if (Resolver.FindByStem("zradmask") is { } mm && Resolver.Load(mm) is { } mt && mt.Width == 256) _radarClear = I76.Sim.Weapons.RadarScope.WeaponCameraClear(mt.Indices);
        _rng = new System.Random();
        yield return null;
        SetupMirror();
    }

    private (string? Member, ImageTexture?[] Tex) Frames(string pattern, int n)
    {
        var tex = new ImageTexture?[n + 1];
        for (int i = 1; i <= n; i++)
            if (Image(string.Format(pattern, LightVariant, i)) is { } img) tex[i] = ImageTexture.CreateFromImage(img);
        return (Resolver.FindByStem(string.Format(pattern, 1, 1)), tex);
    }

    private Image? Image(string stem) =>
        Resolver.FindByStem(stem) is { } m && Textures.GetRgba(m) is { } t ? Godot.Image.CreateFromData(t.Width, t.Height, false, Godot.Image.Format.Rgba8, t.Rgba) : null;

    private void Sound(string name)
    {
        if (Audio?.Get(name) is { } s) Node3D.OneShot(s);
    }

    public override void _Process(double delta)
    {
        float dt = (float)System.Math.Clamp(delta, 0.001, 0.2);
        _steps = SimClock.Take(ref _lastTick);   // the ammo digits and the binocular glance step once a frame of the original: a sim tick
        bool pit = Active;
        Zoom(dt);
        if (!pit && Binoculars) SetBinoculars(false);   // any view but the binoculars: the ring is the ordinary one again (the camera view selection)
        // the draw returns at once unless hud || hud2d: in the car, or with the external instruments on
        bool shown = pit || Instruments?.Invoke() == true;
        if (!pit)
        {
            _wantArm[0] = _wantArm[1] = false;
            if (_mouseHead.HasOffset || _freeLooking)
            {
                _mouseHead.Reset();
                _freeLooking = false;
                _yaw = 0;
                _pitch = -13 * Mathf.Pi / 180;
                _roll = 0;
            }
            if (_mirrorView is not null) _mirrorView.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }
        if (shown && !_wasShown) ResetPanels();
        _wasShown = shown;
        if (pit) Head(dt);
        if (shown)
        {
            Gear();
            Compass();
            DamagePanel();
            SpecialsPanel();
            Radar();
            Gauges();
        }
        WeaponsPanel();
        if (pit)
        {
            Wheel(dt);
            Arms();
            Needles();
            Mirror();
        }
        Panels.Commit();
    }

    // ---- binoculars ----

    /// <summary>Camera mode 2: the cockpit eye at a fixed 8x zoom, a level head and its own glances; the hood's gunsight ring is the fine one (zretc_6), the low-resolution one (zretc_1) otherwise.</summary>
    public bool Binoculars { get; private set; }

    /// <summary>The camera's zoom (clamped to [0.5, 8]).</summary>
    public float CameraZoom => Mathf.Clamp(ZoomValue, 0.5f, 8f);

    /// <summary>The zoom_factor axis (8..10): PgDn/PgUp move it, End resets it; the camera clamps it back to 8, so it never shows.</summary>
    public float ZoomValue => (_zoomPos + 0x90000) / 65536f;
    private int _zoomPos = -0x10000, _zoomVel;
    private int _binoYaw;
    private float _binoAcc;
    private int _steps, _lastTick = SimClock.Now;

    private void Zoom(float dt)
    {
        // held every frame in every view: a ramp of trunc(182 dt)*3 << 6 to +-0x5555, zeroed on release or reversal
        int dir = (Controls.Down("zoom_factor_plus") ? 1 : 0) - (Controls.Down("zoom_factor_minus") ? 1 : 0);
        if (dir == 0 || System.Math.Sign(_zoomVel) == -dir) _zoomVel = 0;
        if (dir != 0) _zoomVel = System.Math.Clamp(_zoomVel + dir * (((int)(182 * dt) * 3) << 6), -0x5555, 0x5555);
        _zoomPos = System.Math.Clamp(_zoomPos + _zoomVel, -0x10000, 0x10000);
        if (Controls.Down("zoom_factor_reset")) _zoomPos = -0x10000;
    }

    /// <summary>B from the cockpit, or B / F1 / V back: the head snaps (to 0, 0, 0 in the binoculars, to the forward state out of them) and the ring swaps.</summary>
    public void SetBinoculars(bool on)
    {
        if (Binoculars == on) return;
        Binoculars = on;
        _binoYaw = 0; _binoAcc = 0;
        _yaw = 0; _roll = 0; _pitch = on ? 0 : -13 * Mathf.Pi / 180;
        _state = 2;
        if (Node3D.Rtc1 is { } r1) r1.Visible = !on;
        if (Node3D.Rtc6 is { } r6) r6.Visible = on;
        if (!on && Node3D.CockpitCamera is { } cam) { cam.Basis = Basis.FromEuler(new Vector3(-_pitch, 0, 0), EulerOrder.Yxz); cam.Fov = I76.Sim.Views.ViewFov.Main; }
    }

    /// <summary>The glance in mode 2, per frame (a sim tick; 30 a second on the original's reference machine): yaw in integer degrees; pitch and roll stay 0.</summary>
    private int BinoGlance(float dt)
    {
        bool l = Glance("pilot_glance_left", FreeLook.Pose.Left), r = Glance("pilot_glance_right", FreeLook.Pose.Right), down = Glance("pilot_glance_down", FreeLook.Pose.Dash);
        _binoAcc += _steps;
        for (; _binoAcc >= 1; _binoAcc -= 1)
        {
            if (l && !r) _binoYaw = down ? -45 : _binoYaw > -88 ? _binoYaw - 29 : _binoYaw;
            else if (r && !l) _binoYaw = down ? 45 : _binoYaw < 93 ? _binoYaw + 31 : _binoYaw;
            else _binoYaw = 0;
        }
        return _binoYaw;
    }

    private bool _wasShown;
    private System.Random _rng = null!;

    /// <summary>Redraw everything the next update (the weapons panel, updated in every view, keeps its rows and housings).</summary>
    private void ResetPanels()
    {
        _gear = -1; _compass = float.NaN; _parts.Clear(); _specRows = null; _speedFrame = _tachFrame = -1;
    }

    // ---- head ----

    private static readonly (float Yaw, float Pitch, float Roll)[] HeadStates =
        [(-88, -3, -2), (-45, 0, -2), (0, -13, 0), (0, 6, 0), (93, -3, 2), (45, 0, 2), (160, -2, 2), (160, 0, 2)];
    // rows: from-state pair; columns: L, L+down, none, down, R, R+down, up
    private static readonly int[][] Transitions = [[0, 1, 2, 3, 2, 3, 2], [0, 1, 2, 3, 4, 5, 4], [2, 3, 2, 3, 4, 5, 6], [4, 5, 4, 5, 4, 5, 6]];
    private int _state = 2;
    private float _yaw, _pitch = -13 * Mathf.Pi / 180, _roll;

    /// <summary>Whether the head is turned (the 2D panels and the gun are off while it is).</summary>
    public bool LookingAway => _state != 2 || _freeLooking;

    // the port's free look on the pad's look stick (FreeLook) and mouse (MouseCockpitOffset)
    private readonly FreeLook _look = new();
    private readonly I76.Sim.Views.MouseCockpitOffset _mouseHead = new();
    private FreeLook.Pose _pose;
    private bool _freeLooking;

    /// <summary>A glance key held, or the look stick pushed hard into its pose.</summary>
    private bool Glance(string command, FreeLook.Pose pose) => Controls.Down(command) || _pose == pose;

    // ---- the 2D dash (dash3d == 0): what Hud draws over the driver's view ----

    /// <summary>The 3D dash is on; off, the interior model is hidden and the panels are drawn flat (ExtInstruments).</summary>
    public bool Dash3D => Node3D.Dash3D;

    private Texture2D? _reticle;

    /// <summary>The 32x32 gunsight reticle texture (<c>zretc_1</c>) from the game's archive.</summary>
    public Texture2D? ReticleTexture => _reticle ??= Image("zretc_1") is { } img ? ImageTexture.CreateFromImage(img) : null;

    /// <summary>
    /// The reticle zretc_1 (32x32) the 2D dash draws, with its top-left on the 640x480 frame: the side reticle at (300, 240) when
    /// an arm is raised or busy and the head has turned to its window (yaw beyond 88° right or -83° left), else the forward gunsight at
    /// (306, 300) with both arms idle and the head within 4° of straight ahead. Not in the binoculars. Those are the places for the
    /// original's view; with another Field of View the gun line projects elsewhere, so the sprite's centre moves with it.
    /// </summary>
    public (Texture2D Tex, float X, float Y)? Reticle()
    {
        if (Binoculars) return null;
        if (ReticleTexture is not { } tex) return null;
        bool Up(int ch) => Car.Arms[ch].Busy || Car.Arms.Ready(ch);
        (Texture2D, float, float) At(int x, int y)
        {
            var (cx, cy) = I76.Sim.Views.ViewFov.ScreenPoint(x + 16, y + 16, I76.Sim.Views.ViewFov.Main);
            return (tex, cx - 16, cy - 16);
        }
        if ((Up(1) && _yaw > 1.5359f) || (Up(2) && _yaw < -1.4486f)) return At(300, 240);
        if (Car.Arms.Idle(1) && Car.Arms.Idle(2) && MathF.Abs(_yaw) < 0.0698f) return At(306, 300);
        return null;
    }

    private Texture2D? _mirrorArt;
    /// <summary>The mirror as the 2D dash shows it (slot 7): its live view and its art, or null with the mirror off.</summary>
    public (Texture2D View, Texture2D Art)? MirrorFace => _mirrorView is not null && _mirrorMode != 0 && _mirrorArt is not null ? (_mirrorView.GetTexture(), _mirrorArt) : null;

    /// <summary>The pilot's glance as it stands (yaw, pitch down-positive, roll; radians): what the F6 view reads.</summary>
    public (float Yaw, float Pitch, float Roll) HeadAngles => (_yaw, _pitch, _roll);

    private void Head(float dt)
    {
        var (sx, sy) = Controls.LookStick;
        _pose = _look.Update(sx, sy);
        _freeLooking = false;
        if (Binoculars) { BinoHead(dt); return; }
        bool l = Glance("pilot_glance_left", FreeLook.Pose.Left);
        bool r = Glance("pilot_glance_right", FreeLook.Pose.Right);
        bool up = Controls.Down("pilot_glance_up");       // towards the back (the stick's rear look is aimed below, over the shoulder on its side)
        bool down = Glance("pilot_glance_down", FreeLook.Pose.Dash);   // forward, tipped 6° down (the dash and the wheel)
        int col = (l, r, up, down) switch
        {
            (true, false, false, false) => 0,
            (true, false, false, true) => 1,
            (false, false, false, false) => 2,
            (false, false, false, true) => 3,
            (false, true, false, false) => 4,
            (false, true, false, true) => 5,
            (false, false, true, false) => 6,
            _ => -1,
        };
        if (col >= 0) _state = Transitions[_state / 2][col];
        // capture hook: I76_HEAD_STATE=N holds the glance state (4 looks out of the right window, 0 the left)
        if (int.TryParse(System.Environment.GetEnvironmentVariable("I76_HEAD_STATE"), out int hs) && hs is >= 0 and <= 7) _state = hs;
        var (ty, tp, tr) = HeadStates[_state];
        float yawT = ty * Mathf.Pi / 180, pitchT = tp * Mathf.Pi / 180, rollT = tr * Mathf.Pi / 180;
        // the free look: facing forward with no glance key held, the stick's or mouse's own aim (a little roll into the turn, as the glances lean)
        bool freeAllowed = _state == 2 && col == 2 && I76.Formats.Input.DashToggle.FreeLookAllowed(Node3D.Dash3D);
        var fh = freeAllowed ? FreeLook.Head(sx, sy) : null;
        if (MouseCamera.CockpitActive && freeAllowed)
        {
            var (my, mp) = MouseCamera.Take();
            _mouseHead.Step(dt, MouseCamera.ReturnBehind, fh is not null || Controls.Down("pilot_glance_target"), my, mp);
        }
        else
        {
            if (MouseCamera.CockpitActive) MouseCamera.Take();
            _mouseHead.Reset();
        }
        if (fh is { } stickHead)
        {
            yawT = stickHead.Yaw * Mathf.Pi / 180;
            pitchT = stickHead.Pitch * Mathf.Pi / 180;
            rollT = System.Math.Clamp(stickHead.Yaw / 90f, -1f, 1f) * 2 * Mathf.Pi / 180;
            _freeLooking = MathF.Abs(stickHead.Yaw) > 15 || MathF.Abs(stickHead.Pitch - FreeLook.Rest) > 10;
        }
        else if (_mouseHead.HasOffset)
        {
            yawT += _mouseHead.Yaw;
            pitchT += _mouseHead.Pitch;
            rollT = System.Math.Clamp(yawT / (Mathf.Pi / 2f), -1f, 1f) * 2 * Mathf.Pi / 180;
            _freeLooking = MathF.Abs(_mouseHead.Yaw) > 15 * Mathf.Pi / 180 || MathF.Abs(_mouseHead.Pitch) > 10 * Mathf.Pi / 180;
        }
        // capture hook: I76_HEAD=yaw,pitch in the game's degrees
        if (System.Environment.GetEnvironmentVariable("I76_HEAD") is { } hv && hv.Split(',') is [var hy, var hp]
            && float.TryParse(hy, System.Globalization.CultureInfo.InvariantCulture, out float fy) && float.TryParse(hp, System.Globalization.CultureInfo.InvariantCulture, out float fp))
            (yawT, pitchT) = (fy * Mathf.Pi / 180, fp * Mathf.Pi / 180);
        // the stick pushed all the way round: the look rear straight over that shoulder (the original's glance machine would swing a left
        // look forward and across to its one rear pose over the right shoulder, a step a tick)
        if (_pose == FreeLook.Pose.Rear)
        {
            float side = _look.RearLeft ? -1 : 1;
            yawT = side * 160 * Mathf.Pi / 180;
            pitchT = -2 * Mathf.Pi / 180;
            rollT = side * 2 * Mathf.Pi / 180;
            _freeLooking = true;
        }
        // glance at the target while pilot_glance_target is held (bearing within −130..160°, elevation ±16°)
        if (Controls.Down("pilot_glance_target") && Combat?.RadarTarget.GetValueOrDefault(Car) is { } t)
        {
            var d = t.Pos.ToVector3() - Car.Pos.ToVector3();
            var f = Car.Rot.Forward;
            float b = Mathf.Wrap(Mathf.Atan2(d.X, d.Z) - Mathf.Atan2(f.X, f.Z), -Mathf.Pi, Mathf.Pi);
            if (b > -130 * Mathf.Pi / 180 && b < 160 * Mathf.Pi / 180) yawT = b;
            float e = Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
            pitchT = -Mathf.Clamp(e, -16 * Mathf.Pi / 180, 16 * Mathf.Pi / 180);   // game pitch is positive downwards
        }
        float k = (_mouseHead.HasOffset ? 18 : 4) * dt;
        float dy = Mathf.Wrap(yawT - _yaw, -Mathf.Pi, Mathf.Pi);
        if (MathF.Abs(dy) >= 0.005f) _yaw += MathF.Min(1f, k) * dy;
        _pitch = k <= 0.95f && MathF.Abs(pitchT - _pitch) >= 0.005f ? _pitch + k * (pitchT - _pitch) : pitchT;
        _roll = k <= 0.95f && MathF.Abs(rollT - _roll) >= 0.005f ? _roll + k * (rollT - _roll) : rollT;
        if (Node3D.CockpitCamera is { } cam)
        {
            // game yaw is to the right and Godot's Z is mirrored: a right turn is a negative Y rotation here
            cam.Basis = Sway(dt) * Basis.FromEuler(new Vector3(-_pitch, -_yaw, _roll), EulerOrder.Yxz);   // the Z mirror negates pitch and yaw, not roll
            cam.Fov = I76.Sim.Views.ViewFov.Main;
        }
        // arms rise past ±58° / 53° to the side windows (the handguns): the sim moves them with the next tick
        _wantArm[0] = (_state == 4 || _mouseHead.HasOffset) && _yaw > 1.0123f;
        _wantArm[1] = (_state == 0 || _mouseHead.HasOffset) && _yaw < -0.9250f;
    }

    // ---- head sway: the eye tilts with the car's acceleration; view only ----

    private readonly I76.Sim.Views.HeadSway _sway = new();

    /// <summary>The sway stepped one frame, as a rotation in the eye's frame applied after the head turn (Euler · Sway · Eye).</summary>
    private Basis Sway(float dt)
    {
        var rot = Car.Rot;
        _sway.Step(Car.Accel, rot.Right, rot.Forward, dt);
        var (r, u, f) = _sway.Basis(Car.P.Track, Car.P.Wheelbase);
        // the game's rows as columns, conjugated by the Z mirror: x and y keep their axes' z negated, z is negated whole
        static Vector3 M(System.Numerics.Vector3 v) => new(v.X, v.Y, -v.Z);
        return new Basis(M(r), M(u), -M(f));
    }

    private void BinoHead(float dt)
    {
        float yawT = BinoGlance(dt) * Mathf.Pi / 180, pitchT = 0;
        if (Controls.Down("pilot_glance_target") && Combat?.RadarTarget.GetValueOrDefault(Car) is { } t)
        {
            var d = t.Pos.ToVector3() - Car.Pos.ToVector3();
            var f = Car.Rot.Forward;
            float b = Mathf.Wrap(Mathf.Atan2(d.X, d.Z) - Mathf.Atan2(f.X, f.Z), -Mathf.Pi, Mathf.Pi);
            if (b > -130 * Mathf.Pi / 180 && b < 160 * Mathf.Pi / 180) yawT = b;
            float e = Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
            pitchT = -Mathf.Clamp(e, -16 * Mathf.Pi / 180, 16 * Mathf.Pi / 180);
        }
        float k = 2 * dt;   // the cockpit eases at 4 dt
        float dy = Mathf.Wrap(yawT - _yaw, -Mathf.Pi, Mathf.Pi);
        if (MathF.Abs(dy) >= 0.005f) _yaw += k * dy;
        _pitch = k <= 0.95f && MathF.Abs(pitchT - _pitch) >= 0.005f ? _pitch + k * (pitchT - _pitch) : pitchT;
        _roll = 0;
        _wantArm[0] = _wantArm[1] = false;
        if (Node3D.CockpitCamera is { } cam)
        {
            cam.Basis = Sway(dt) * Basis.FromEuler(new Vector3(-_pitch, -_yaw, 0), EulerOrder.Yxz);
            // 8x: tan(h/2) = 1/8 across, so 0.75/8 down on 4:3 (10.71 degrees)
            cam.Fov = Mathf.RadToDeg(2 * MathF.Atan(0.75f / CameraZoom));
        }
    }

    // ---- PRND21 ----

    private int _gear = -1;
    private static readonly string[] GearAnchors = ["park", "reverse", "neutral", "drive", "second", "first"];

    private void Gear()
    {
        int g = Car.DriveState;
        if (g == _gear || g < 0 || g >= GearAnchors.Length) return;
        if (_gear >= 0) Sound("vshif1a.wav");
        _gear = g;
        Panels.Clear("zgear101.map");
        Panels.StampAt("prndback", "prnd");
        Panels.StampAt("arrow", GearAnchors[g]);
    }

    // ---- compass ----

    private float _compass = float.NaN;

    private void Compass()
    {
        var f = Car.Rot.Forward;
        float deg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(f.X, f.Z)), 360f);
        if (MathF.Abs(deg - _compass) < 0.5f) return;
        _compass = deg;
        Panels.Window("zcm_.map", "compass_window", "left", "right", deg / 360f);
    }

    // ---- damage panel ----

    private readonly Dictionary<string, int> _parts = [];

    /// <summary>The panel's five bands: 100 exactly is the unlit plate, then green, yellow, red, and dark at 0.</summary>
    private static int Band(int pct) => pct == 100 ? 0 : pct >= 67 ? 1 : pct >= 34 ? 2 : pct >= 1 ? 3 : pct == 0 ? 4 : -1;
    private static readonly string[] PartSuffix = ["_off", "_grn", "_ylw", "_red", "_drk"];

    private void DamagePanel()
    {
        if (Car.Health is not { } h) return;
        static int Pct(int cur, int max) => max <= 0 ? 0 : (int)(cur * (100f / max) + 0.5f);
        var items = new List<(string Anchor, string Sprite, int Pct)>
        {
            ("engine", "engine", h.Engine is { } e ? Pct(e.Hp, e.Max) : 100),
            ("suspen", "suspen", h.Suspension is { } s ? Pct(s.Hp, s.Max) : 100),
            ("brakes", "brakes", h.Brake is { } b ? Pct(b.Hp, b.Max) : 100),
        };
        foreach (var (anchor, slot) in new[] { ("rrtire", 4), ("rltire", 5), ("frtire", 0), ("fltire", 1) })
            items.Add((anchor, "tire", h.Slots[slot] is { } w ? Pct(w.Hp, w.Max) : 100));
        string[] arm = ["farm", "larm", "rarm", "barm"], chas = ["fchas", "lchas", "rchas", "bchas"];
        for (int z = 0; z < 4; z++)
        {
            // a helicopter's hits take its one pool (the helicopter's damage), never the zones, so the original's plates stay unlit
            // however hurt it is; ours show the pool on every armour and chassis plate (docs/deviations.md, #38)
            if (Car.Heli is { } heli)
            {
                int pool = Pct(heli.Hp, heli.MaxHp);
                items.Add((arm[z], arm[z], pool));
                items.Add((chas[z], chas[z], pool));
                continue;
            }
            items.Add((arm[z], arm[z], Pct(h.Armour[z], h.ArmourMax[z])));
            items.Add((chas[z], chas[z], Pct(h.Chassis[z], h.ChassisMax[z])));
        }
        bool changed = false;
        foreach (var it in items)
        {
            int band = Band(it.Pct);
            if (band < 0 || _parts.TryGetValue(it.Anchor, out int was) && was == band) continue;
            if (_parts.ContainsKey(it.Anchor) && band == 4) Sound("cwstat.wav");
            _parts[it.Anchor] = band;
            changed = true;
        }
        if (!changed) return;
        Panels.Clear("zsy_.map");
        foreach (var it in items)
            if (_parts.TryGetValue(it.Anchor, out int band)) Panels.StampAt(it.Sprite + PartSuffix[band], it.Anchor);
    }

    // ---- weapons panel ----

    private sealed class Row
    {
        public int Housing, Diode = -1, Value = -1;
        public bool On;
        public int[] Digits = [10, 10, 10, 10];
    }
    private Row[]? _rows;
    private int _separator = -1;
    private static readonly string[] Places = ["ones", "tens", "hunds", "thous"];
    private static readonly string[] DiodeSprite = ["off", "green", "yellow", "red", "drk"];

    private static string Res(string name) => name.StartsWith('3') ? name[1..] : name;   // full-size art drops the '3'

    private void WeaponsPanel()
    {
        if (Car.Armory is not { } a) return;
        var ws = a.Weapons.Take(5).ToList();
        bool dirty = false, force = false;
        if (_rows is null || _rows.Length != ws.Count)
        {
            // one of five housings per row, chosen at random once
            _rows = ws.Select(_ => new Row { Housing = 1 + _rng.Next(5) }).ToArray();
            int lastFront = -1;
            for (int i = 0; i < ws.Count; i++) if (ws[i].MountKind is 1 or 4) lastFront = i;
            _separator = lastFront + 1;
            dirty = force = true;
        }
        for (int i = 0; i < ws.Count; i++)
        {
            var w = ws[i]; var row = _rows[i];
            bool on = i == a.Selected || a.Group.Contains(i);
            int diode = Band(w.MaxHp <= 0 ? 0 : (int)((float)w.Hp / w.MaxHp * 100));
            if (on != row.On || diode != row.Diode) { row.On = on; row.Diode = diode; dirty = true; }
            if (Roll(row.Digits, w.Ammo, force)) dirty = true;
        }
        if (!dirty) return;
        Panels.Clear("zbk_.map");
        for (int i = 0; i < ws.Count; i++)
        {
            var w = ws[i]; var row = _rows[i]; int n = i + 1;
            Panels.StampAt("housing" + row.Housing, $"bracket_{n}");
            if (row.Diode >= 0) Panels.StampAt(DiodeSprite[row.Diode], $"diode_{n}");
            Panels.StampAt(Res(row.On ? w.Def.PlateOn : w.Def.PlateOff), $"dymo_{n}");
            for (int p = 0; p < 4; p++) Panels.VWindow("zbk_.map", $"num_{Places[p]}_{n}", "top", "bottom", row.Digits[p] * 0.1f);
        }
        if (_separator >= 0 && _separator < 5) Panels.StampAt("sepbar", $"separator_{_separator + 1}");
    }

    /// <summary>
    /// The rolling counter: each digit that differs from the value's steps down
    /// by one per update (an increase rolls down through 9). True if
    /// any digit moved.
    /// </summary>
    private bool Roll(int[] shown, int value, bool force)
    {
        bool moved = false;
        value = System.Math.Clamp(value, 0, 9999);
        for (int step = 0; step < System.Math.Max(_steps, force ? 1 : 0); step++)
            for (int p = 0; p < 4; p++)
            {
                int d = value / (int)System.Math.Pow(10, p) % 10;
                if (shown[p] == d && !force) continue;
                if (force) shown[p] = d;
                else { shown[p] -= 1; if (shown[p] < 0) shown[p] = 9; }
                moved = true;
            }
        return moved;
    }

    // ---- specials panel ----

    private static readonly string[] SpecialNames = ["sp_empty", "radar", "nitrous", "blower", "xaust", "structo", "curb", "mud", "heated", "cuphldr", "dash_mary", "tire_covr"];
    private Row[]? _specRows;

    private void SpecialsPanel()
    {
        if (Car.Health is not { } h) return;
        var specs = Enumerable.Range(0, 3).Select(i => h.Slots[17 + i] as I76.Sim.Damage.SpecialPart).ToArray();
        bool dirty = false, force = false;
        // one of the weapons panel's five housings per row, chosen at random on the first draw: the olive plate
        // the dymo label and the counters sit on
        if (_specRows is null) { _specRows = specs.Select(_ => new Row { Housing = 1 + _rng.Next(5) }).ToArray(); dirty = force = true; }
        for (int i = 0; i < 3; i++)
        {
            var s = specs[i]; var row = _specRows[i];
            bool on = s is not null && s.Charges >= 1 && s.Unk_0x10 != 0;
            int diode = s is null ? -1 : Band(s.Max <= 0 ? 0 : (int)((float)s.Hp / s.Max * 100));
            if (on != row.On || diode != row.Diode) { row.On = on; row.Diode = diode; dirty = true; }
            if (Roll(row.Digits, s?.Charges ?? 0, force)) dirty = true;
        }
        if (!dirty) return;
        Panels.Clear("zbks_.map");
        for (int i = 0; i < 3; i++)
        {
            var s = specs[i]; var row = _specRows[i]; int n = i + 1;
            Panels.StampAt("housing" + row.Housing, $"sp_bracket_{n}");
            // an empty slot (type 0) keeps the housing's own EMPTY plate and 0000: no plate, diode or counter is drawn
            if (s is null || s.Type == 0) continue;
            int type = s.Type < 0 || s.Type >= SpecialNames.Length ? 0 : s.Type;
            Panels.StampAt(SpecialNames[type] + (row.On ? "_on" : "_off"), $"sp_dymo_{n}");
            if (row.Diode >= 0) Panels.StampAt(DiodeSprite[row.Diode], $"sp_diode_{n}");
            for (int p = 0; p < 4; p++) Panels.VWindow("zbks_.map", $"sp_num_{Places[p]}_{n}", "top", "bottom", row.Digits[p] * 0.1f);
        }
    }

    // ---- steering wheel and arms ----

    private (string? Member, ImageTexture?[] Tex) _wheelFrames, _armR, _armL;
    private float _wheel = 9;
    private int _wheelShown = -1;

    private void Wheel(float dt)
    {
        float s = Mathf.Clamp(Car.Steer, -0.9999f, 0.9999f);
        int tgt = s < 0 ? 9 + (int)(27 * -s) : 9 + (int)(9 * -s);
        float k = 2 * dt;
        _wheel = k > 0.95f || MathF.Abs(tgt - _wheel) < 0.005f ? tgt : _wheel + k * (tgt - _wheel);
        int frame = System.Math.Clamp((int)MathF.Round(_wheel), 1, 36);
        if (frame == _wheelShown || _wheelFrames.Member is null || _wheelFrames.Tex[frame] is not { } t) return;
        _wheelShown = frame;
        Materials.OverrideTexture(_wheelFrames.Member, t);
    }

    private (string? Member, ImageTexture?[] Tex) _shotR, _shotL;
    private readonly bool[] _wantArm = new bool[2];
    private readonly (int Axis, int Frame)[] _armShown = [(-1, -1), (-1, -1)];

    /// <summary>Whether the glance wants an arm raised to its window (crew channel 1 right, 2 left): the driver's input for the arms (<see cref="Vehicle.Arms"/>).</summary>
    public bool WantArm(int ch) => _wantArm[ch - 1];

    /// <summary>The arms as the sim has them (CrewArms, stepped once a tick): the raise strip's frame, or the shot's.</summary>
    private void Arms()
    {
        for (int i = 0; i < 2; i++)
        {
            var a = Car.Arms[i + 1];
            var (raise, shot) = i == 0 ? (_armR, _shotR) : (_armL, _shotL);
            int frame = System.Math.Clamp(a.Cur, 0, a.Axis == 0 ? 5 : 2) + 1;
            if ((frame, a.Axis) == (_armShown[i].Frame, _armShown[i].Axis) || raise.Member is null) continue;
            var tex = a.Axis == 1 ? shot.Tex[frame] : raise.Tex[frame];
            if (tex is null) continue;
            _armShown[i] = (a.Axis, frame);
            Materials.OverrideTexture(raise.Member, tex);
        }
    }

    // ---- radar ----

    private readonly Image?[] _radarF = new Image?[31], _radarB = new Image?[31];
    private string? _radarMember;
    private int _radarCur, _radarPrev;
    private double _radarNext, _t;
    private readonly Dictionary<Vehicle, double> _sweptAt = [];
    /// <summary>The 256-colour palette the panels' bitmaps use (the radar's blip colours are indices into it).</summary>
    public I76.Formats.Imaging.Palette? Palette { get; init; }
    private bool _ledAny;
    /// <summary>Extra radar blips with their own colour: CTF's flags (class 0x5b, in the team's colour), each as a sim position and a colour. The vehicles' rule of range applies.</summary>
    public Func<IEnumerable<(System.Numerics.Vector3 Pos, Color Colour)>>? Blips { get; set; }
    public int RadarRange => Combat?.RadarRange.GetValueOrDefault(Car, 1) ?? 1;   // 0: 100 m, 1: 600 m: the sim's radar (R flips it)
    public bool RadarOn { get; set; } = true;  // radar +0xc bit 0

    private void Radar()
    {
        _t += GetProcessDeltaTime();
        if (_radarMember is null || Combat is null) return;
        // the radar object (class 0x22) is built switched on at range 1 with the cockpit's radar part, special or not
        bool has = RadarOn;
        Image? frame;
        if (WeaponCamera() is { } cam) { UploadRadar(cam); return; }
        var locked = has ? Combat.RadarTarget.GetValueOrDefault(Car) : null;
        if (!has) frame = _radarF[30];
        else if (locked is not null)
        {
            // flag 0x10 (a target selected): the sweep stops and the locked frame ZRADBnnn follows the target's bearing
            // the frame is written into the sweep's current step, the step it left stays where the sweep stopped: the ping
            // window below runs from there to the target's bearing, and the sweep resumes from the target's frame
            var d = Car.Rot.ToLocal(locked.Pos.ToVector3() - Car.Pos.ToVector3());
            _radarCur = I76.Sim.Weapons.RadarScope.LockedFrame(d.X, d.Z);
            frame = _radarB[_radarCur] ?? _radarF[30];
        }
        else
        {
            if (_t < _radarNext) return;
            _radarPrev = _radarCur;
            while (_radarNext < _t) { _radarNext += 0.2; _radarCur = I76.Sim.Weapons.RadarScope.NextSweep(_radarCur); }
            frame = _radarF[_radarCur];
        }
        if (frame is null) return;
        var img = (Image)frame.Duplicate();
        // a newly picked target's byte is 0 (the target pick and the GOG build's E/Q: the one it replaces goes back to 0x69)
        if (locked != _radarPicked)
        {
            if (_radarPicked is not null) _sweptAt.Remove(_radarPicked);
            if (locked is not null) _sweptAt[locked] = _t;
            _radarPicked = locked;
        }
        if (has)
        {
            Panels.StampInto(img, "zrad.map", RadarRange == 0 ? "range_1" : "range_3", "range_pos");
            float R = RadarRange == 0 ? 100 : 600;
            bool any = false, pinged = false;
            foreach (var v in Combat.World.Vehicles)
            {
                if (v.Wreck || !Combat.IsContact(Car, v)) continue;   // the contact list: 600 m on the ground, no dead or engine-off car
                any = true;   // the LED and the growl: any listed car, on the scope or past its range circle
                if (I76.Sim.Weapons.RadarScope.Project(Car.Rot, Car.Pos.ToVector3(), v.Pos.ToVector3(), R) is not var (nx, nz)) continue;
                // the sweep pings at its steps; a locked radar every tick, for every car but the target in the window from the
                // step the sweep stopped on to the target's bearing: the original's beeping while a target is held
                bool inWedge = v != locked && (locked is null || _steps > 0) && I76.Sim.Weapons.RadarScope.InPingWedge(_radarPrev, _radarCur, nx, nz);
                if (inWedge) { if (!pinged) { RadarPing(); pinged = true; } _sweptAt[v] = _t; }   // every pass pings; blips in one step share the sound
                int px = System.Math.Clamp((int)(99 + 51 * nx), 0, img.GetWidth() - 2);
                int py = System.Math.Clamp((int)(65 + 51 * nz), 0, img.GetHeight() - 2);   // a raw bitmap row: bottom-up, as the image stores it
                // no team colours: palette index 0x69 when listed, 0 as the sweep passes or it is picked, +3 a second back to
                // 0x69; the target 0xff, which the 1997 CD build never writes (its target fades as the others do)
                bool soft = I76.Game.World.SoftwareLook.Active;
                int ci = !soft && Combat.RadarTarget.GetValueOrDefault(Car) == v ? 0xff
                    : _sweptAt.TryGetValue(v, out var st) ? System.Math.Min(0x69, (int)(3 * (_t - st))) : 0x69;
                img.FillRect(new Rect2I(px, py, 2, 2), soft ? SoftwareBlip(ci) : BlipColour(ci));
            }
            if (Blips is { } blips)
                foreach (var (fp, fc) in blips())
                {
                    if (I76.Sim.Weapons.RadarScope.Project(Car.Rot, Car.Pos.ToVector3(), fp, R) is not var (nx, nz)) continue;
                    int px = System.Math.Clamp((int)(99 + 51 * nx), 0, img.GetWidth() - 3);
                    int py = System.Math.Clamp((int)(65 + 51 * nz), 0, img.GetHeight() - 3);
                    img.FillRect(new Rect2I(px, py, 3, 3), fc);
                }
            if (any && !_ledAny) Sound("cgrowl.wav");
            _ledAny = any;
            Panels.StampInto(img, "zrad.map", any ? "radled_1" : "radled_2", "led_pos");
        }
        UploadRadar(img);
    }

    private Vehicle? _radarPicked;

    /// <summary>CRADAR.WAV: its header's one voice, so a ping asked for while one sounds is dropped (a held target beeps at the sample's length).</summary>
    private void RadarPing()
    {
        if (Audio?.Get("cradar.wav") is { } s) Node3D.OneShotSingle("cradar.wav", s);
    }
    private readonly Color?[] _softBlips = new Color?[256];

    /// <summary>
    /// A blip's palette index as the GOG build draws it. The mission palette's slot 255 is black (1,1,1), invisible on the
    /// green screen: the target's blip is drawn white, the colour a Windows 8-bit palette gives its last static slot.
    /// </summary>
    private Color BlipColour(int ci) => ci == 0xff ? Colors.White
        : Palette is { } pal ? Color.Color8(pal[ci].R, pal[ci].G, pal[ci].B) : new Color(ci / 255f, ci / 255f, ci / 255f);

    /// <summary>The Software renderer's blip: the 1997 CD build's green for the byte (SoftwarePalette.RadarGreen), as its palette entry.</summary>
    private Color SoftwareBlip(int ci)
    {
        if (_softBlips[ci] is { } c) return c;
        var (r, g, b) = I76.Formats.Imaging.SoftwarePalette.RadarGreen(ci);
        c = Palette is { } pal && I76.Formats.Imaging.SoftwarePalette.RadarBlip(pal, ci) is var i ? Color.Color8(pal[i].R, pal[i].G, pal[i].B) : new Color(r, g, b);
        return (_softBlips[ci] = c).Value;
    }

    private void UploadRadar(Image img)
    {
        _radarTex ??= ImageTexture.CreateFromImage(img);
        _radarTex.Update(img);
        if (System.Environment.GetEnvironmentVariable("I76_PIT_DUMP") is { } dir && Godot.Engine.GetProcessFrames() >= 15 && !_radarDumped) { img.SavePng(System.IO.Path.Combine(dir, "radar.png")); _radarDumped = true; }
        if (!_radarBound) { Materials.OverrideTexture(_radarMember!, _radarTex); _radarBound = true; }
    }

    // ---- Toggle Weapon Cam: the turret's view in the radar screen ----

    private Image? _radarMask;
    private bool[]? _radarClear;   // the mask's corners outside the box, left transparent as the radar frames' are
    private SubViewport? _wcamView;
    private Camera3D? _wcamCam;
    /// <summary>The backdrop key: the camera's clear colour, replaced by the two greys; no object is this colour.</summary>
    private static readonly Color WcamKey = new(1, 0, 1);

    /// <summary>
    /// The radar picture while the weapon camera shows (the flag set, a target held, a turret, the radar on and
    /// zradmask.map found), else null: the turret's view read back from its 256x128 viewport,
    /// the two greys behind it, remapped to the green ramp by luminance, the mask over it. Raw rows, bottom-up.
    /// </summary>
    private Image? WeaponCamera()
    {
        var turret = Combat is null || !RadarOn || _radarMask is null || !Combat.RadarCamera.Contains(Car) || Combat.RadarTarget.GetValueOrDefault(Car) is null
            ? null : Car.Armory?.Weapons.LastOrDefault(w => w.Style == 3);
        if (turret is null)
        {
            if (_wcamView is not null) _wcamView.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return null;
        }
        if (_wcamView is null)
        {
            _wcamView = new SubViewport { Size = new Vector2I(256, 128), Name = "WeaponCam", RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            // 2 * atan(64 / 424.2): the engine's 135 degrees clamped, zoom 8 (focal length 424.2 px), over 128 rows
            _wcamCam = new Camera3D { Fov = 17.16f, KeepAspect = Camera3D.KeepAspectEnum.Height, Near = 0.05f, Far = 4000, Current = true,
                // no terrain, no sky, and not the interior
                CullMask = 0xFFFFFu & ~World.TerrainNode.TerrainLayer & ~World.TerrainNode.SkyFogLayer & ~CarNode.CockpitLayer & ~SmokeLayer.QuadLayer,
                Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = WcamKey,
                    AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.35f, 0.35f, 0.35f) } };
            _wcamView.AddChild(_wcamCam);
            AddChild(_wcamView);
        }
        _wcamView.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        // the pitch node: the car's pose, the mount's base, the yaw and the pitch (Armory.MuzzleRot), at the turret's pivot, looking down +Z
        var mount = Node3D.GlobalTransform * new Transform3D(Coords.ToGodot(turret.MuzzleRot.ToEngine()), Coords.ToGodot(turret.TurretPivot ?? turret.MuzzlePos));
        // the view only, not the aim: a helicopter's turret hangs under the body, so its frame is turned over (or on its side) and the horizon
        // with it. The camera keeps the gun's line and rolls to the world's up; aimed straight up or down it has no horizon, and keeps the mount's roll.
        var gun = -mount.Basis.Z;
        if (new Vector3(gun.X, 0, gun.Z).LengthSquared() > 1e-4f) mount.Basis = Basis.LookingAt(gun, Vector3.Up);
        _wcamCam!.GlobalTransform = mount;
        var shot = _wcamView.GetTexture().GetImage();   // the last frame's render: one frame of lag, as the 3D dash had
        if (shot is null || shot.GetWidth() != 256) return _radarF[30] is { } blank ? (Image)blank.Duplicate() : null;
        shot.Convert(Godot.Image.Format.Rgba8);
        var px = shot.GetData();
        var outp = new byte[256 * 128 * 4];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 256; x++)
            {
                int i = (y * 256 + x) * 4;
                float r = px[i] / 255f, g = px[i + 1] / 255f, b = px[i + 2] / 255f;
                // the backdrop: rows 0..47 of the picture grey 0.7 (sky), the rest 0.5 (ground)
                if (px[i] == 255 && px[i + 1] == 0 && px[i + 2] == 255) r = g = b = y < 48 ? 0.7f : 0.5f;
                float t = Mathf.Clamp((0.3f * r + 0.59f * g + 0.11f * b - 0.133f) / (0.617f - 0.133f), 0, 1);
                int o = ((127 - y) * 256 + x) * 4;   // raw rows are bottom-up
                outp[o] = (byte)Mathf.RoundToInt(255 * 0.388f * t);
                outp[o + 1] = (byte)Mathf.RoundToInt(255 * (0.225f + (0.776f - 0.225f) * t));
                outp[o + 2] = outp[o];
                outp[o + 3] = 255;
            }
        var img = Godot.Image.CreateFromData(256, 128, false, Godot.Image.Format.Rgba8, outp);
        img.BlendRect(_radarMask!, new Rect2I(0, 0, System.Math.Min(256, _radarMask!.GetWidth()), System.Math.Min(128, _radarMask.GetHeight())), Vector2I.Zero);
        if (_radarClear is { } clear)
        {
            // raw rows, bottom-up: the mask's index order, one row for one
            for (int i = 0; i < clear.Length && i < 256 * 128; i++)
                if (clear[i]) img.SetPixel(i % 256, i / 256, new Color(0, 0, 0, 0));
        }
        return img;
    }

    private ImageTexture? _radarTex;
    private bool _radarBound, _radarDumped;

    // ---- gauge needles ----

    /// <summary>
    /// The speedometer's mph: the engine's. A helicopter (class 9: the Huey, Cobra, balloon,
    /// UFO, Phantom) never runs the car update that keeps it, so in the original its speedometer stays at 0 however fast
    /// it flies (measured in the original game: mph 0 and both needles at rest at 107 u/s); here it reads the airspeed, |v| in mph as the
    /// engine scales it (docs/deviations.md).
    /// </summary>
    private float Mph => Car.Heli is null ? Car.Engine.Speedo : Car.Speed * 2.2369363f;

    private void Needles()
    {
        // speedo: 0..150 mph over 278°; tacho: 0..8400 rpm over 236.25°; past 180° wraps negative
        float Wrap(float deg) => deg > 180 ? deg - 360 : deg;
        float speedo = Wrap(System.Math.Clamp(Mph, 0, 150) * 1.8533334f);
        float tacho = Wrap(System.Math.Min(Car.Engine.Rpm, 8400) * 0.028125f);
        Turn(Node3D.Needles[0], speedo);
        Turn(Node3D.Needles[1], tacho);
        // RotY (the engine's [c 0 −s; 0 1 0; s 0 c]) ahead of the needle's VLOC; the Z mirror turns it the other way here
        static void Turn((Node3D Node, Transform3D Pose)? n, float deg)
        {
            if (n is { } x) x.Node.Transform = x.Pose * new Transform3D(new Basis(Vector3.Up, -Mathf.DegToRad(deg)), Vector3.Zero);
        }
    }

    // ---- the 2D gauges ----

    private int _speedFrame = -1, _tachFrame = -1;

    /// <summary>
    /// The external instruments' speedometer and tachometer: the dial
    /// (numerals only, no face) with one of zneedle6.map's 79x79 needle frames
    /// stamped at its anchor. Speed 0..150 mph over frames 9..38, rpm over
    /// 1..25 (8000 rpm). The original composes them only under the 2D dash
    /// (dash3d == 0), which leaves the default 3D dash's chase view without
    /// gauges; here they always work (docs/deviations.md).
    /// </summary>
    private void Gauges()
    {
        float s = System.Math.Clamp(Mph, 0, 150);
        Needle("zspeedo6.map", "speedneedleloc", System.Math.Min(9 - (int)(s * -0.19333334f), 38), ref _speedFrame);
        float r = System.Math.Clamp(Car.Engine.Rpm, 0, 8400);
        Needle("ztach6.map", "tachneedleloc", System.Math.Min((int)(r * 0.003f + 1f), 25), ref _tachFrame);

        // the anchor by its dial: the first speedneedleloc in the file is zspeedo3.map's
        void Needle(string dial, string anchor, int frame, ref int shown)
        {
            if (frame == shown || Panels.Layout.FindIn(dial, anchor) is not { } a) return;
            shown = frame;
            Panels.Clear(dial);
            Panels.Stamp(dial, $"needle_{frame}", a.X, a.Y);
        }
    }

    // ---- rear-view mirror ----

    private SubViewport? _mirrorView;
    private Camera3D? _mirrorCam;
    private ShaderMaterial? _mirrorMat;
    private Godot.Environment? _mirrorFlat;
    private int _mirrorMode = 2;
    private bool _mirrorTick;

    private void SetupMirror()
    {
        if (Resolver.FindByStem("zmiri101") is not { } member || Node3D.CockpitMesh is not { Mesh: { } mesh } mi) return;
        var made = Materials.MadeFor(member).ToHashSet();
        var surfaces = Enumerable.Range(0, mesh.GetSurfaceCount()).Where(i => mesh.SurfaceGetMaterial(i) is { } m && made.Contains(m)).ToList();
        if (surfaces.Count == 0) return;
        _mirrorView = new SubViewport { Size = new Vector2I(1024, 256), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Name = "Mirror" };
        _mirrorCam = new Camera3D { Fov = 30, KeepAspect = Camera3D.KeepAspectEnum.Width, Near = 0.1f, Far = 20000, Current = true };
        _mirrorView.AddChild(_mirrorCam);
        SmokeLayer.MirrorCamera = _mirrorCam;
        AddChild(_mirrorView);
        _mirrorFlat = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = MirrorBackground,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.35f, 0.35f, 0.35f) };
        _mirrorMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/cockpit_mirror.gdshader") };
        if (Textures.Get(member)?.Texture is { } art) { _mirrorMat.SetShaderParameter("art", art); _mirrorArt = art; }
        if (System.Environment.GetEnvironmentVariable("I76_PIT_DUMP") is { } dir) foreach (var n in new[] { "zmiri101", "zdash101", "zdash102" }) Image(n)?.SavePng(System.IO.Path.Combine(dir, n + ".png"));
        var vt = _mirrorView.GetTexture();
        _mirrorMat.SetShaderParameter("view", vt);
        _mirrorMat.SetShaderParameter("view_nearest", vt);
        foreach (int i in surfaces) mi.SetSurfaceOverrideMaterial(i, _mirrorMat);
        _mirrorMode = _mirrorSetting;
        ApplyMirrorMode();
    }

    /// <summary>Puts a 2D overlay into the mirror's own picture (the target brackets, inMirror = 1); false with no mirror.</summary>
    public bool AddMirrorOverlay(Control c)
    {
        if (_mirrorView is null) return false;
        _mirrorView.AddChild(c);
        return true;
    }

    /// <summary>Graphic Detail's Rear Mirror (I76PLYR.DEF +0x4d): Off, Low (objects only), High (full) — the mode the mirror starts in.</summary>
    public int MirrorSetting
    {
        get => _mirrorSetting;
        set { _mirrorSetting = System.Math.Clamp(value, 0, 2); _mirrorMode = _mirrorSetting; ApplyMirrorMode(); }
    }
    private int _mirrorSetting = 2;

    /// <summary>` (TOGGLE_REARVIEW) cycles off, objects only (no sky or terrain), full; the state is kept in any view.</summary>
    public void CycleMirror()
    {
        if (_mirrorMat is null) return;
        _mirrorMode = (_mirrorMode + 1) % 3;
        ApplyMirrorMode();
    }

    private void ApplyMirrorMode()
    {
        if (_mirrorView is null || _mirrorCam is null || _mirrorMat is null) return;
        _mirrorView.Size = _faithful ? new Vector2I(256, 64) : new Vector2I(1024, 256);
        _mirrorMat.SetShaderParameter("faithful", _faithful);
        _mirrorMat.SetShaderParameter("hidden", _mirrorMode == 0);
        _mirrorCam.CullMask = (_mirrorMode == 1 ? ~World.TerrainNode.TerrainLayer : 0xFFFFFu) & ~CarNode.CockpitLayer & ~SmokeLayer.QuadLayer & 0xFFFFF;
        _mirrorCam.Environment = _mirrorMode == 1 ? _mirrorFlat : null;
    }

    private void Mirror()
    {
        if (_mirrorView is null || _mirrorCam is null) return;
        if (_mirrorMode == 0) { _mirrorView.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; return; }
        // the eye, not head-turned, its right and forward negated: looking backwards
        _mirrorCam.GlobalTransform = Node3D.GlobalTransform * new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Node3D.Eye);
        // the original redraws it every second frame; the modern look every frame
        _mirrorTick = !_mirrorTick;
        _mirrorView.RenderTargetUpdateMode = !_faithful ? SubViewport.UpdateMode.Always : _mirrorTick ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled;
    }
}
