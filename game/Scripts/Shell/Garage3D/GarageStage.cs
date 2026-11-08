using Godot;
using Environment = Godot.Environment;
using I76.Sim.Trip;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// Where the garage's camera looks: a point on the car (in the car's own frame), and from where. <see cref="Relative"/> turns the camera
/// with the car on its turntable (a close-up of one part stays on it); otherwise the camera is the world's and the car turns in front of it.
/// Yaw is the camera's side: 0 behind the car, 90 on its right, 180 in front, -90 on its left (the car's own frame when relative).
/// </summary>
public readonly record struct FocusSpec(Vector3 Target, bool Relative, float Yaw, float Pitch, float Distance);

/// <summary>
/// The 3D garage's scene in a viewport of its own: the bay (<see cref="GarageRoom"/>), the car on its turntable, a camera that flies
/// to what the menu is on and can be turned and zoomed by hand, and a marker that pulses over the hardpoint being picked.
/// Drawn under the shell's frame, which is transparent where it draws nothing.
/// </summary>
public sealed partial class GarageStage : SubViewportContainer
{
    public required GarageAssets Assets { get; init; }
    /// <summary>The installs whose music the boombox plays, and the Music level it keeps to (null installs: no radio).</summary>
    public IReadOnlyList<I76.Formats.Vfs.GameInstall>? MusicInstalls { get; init; }
    public int MusicLevel { get; init; } = 8;
    public GarageRadio? Radio { get; private set; }
    public bool Mode1997 { get; private set; }
    /// <summary>Drawn though nothing shows it (the warm-up's stage: a see-through container skips drawing its viewport's picture, and a viewport drawn when visible would not draw).</summary>
    public bool DrawUnseen { get; init; }

    private SubViewport _vp = null!;
    private Camera3D _cam = null!;
    private WorldEnvironment _env = null!;
    private DirectionalLight3D _sun = null!;
    private GarageRoom _room = null!;
    private Node3D _holder = null!;
    private MeshInstance3D _marker = null!;
    private Environment _modernEnv = null!, _env1997 = null!;
    public GarageCar? Car { get; private set; }

    // the camera: where it is (eased) and where it wants to be
    private Vector3 _tgt = new(0, 0.9f, 0);
    private float _yaw = Mathf.DegToRad(-135), _pitch = Mathf.DegToRad(14), _dist = 9f;
    private FocusSpec _focus = new(new Vector3(0, 0.9f, 0), false, -135, 14, 9);
    private float _userYaw, _userPitch, _userZoom = 1f, _idle = 10f, _spin, _spinSpeed;
    private float _markerPhase;
    private Vector3? _markerAt;

    /// <summary>The turntable's turn while nothing is being looked at closely, in degrees a second.</summary>
    public const float SpinRate = 11f;
    /// <summary>How far right of the window's middle the car stands, in halves of the window's width (the notepad is on the left).</summary>
    public const float ViewShift = 0.34f;

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _vp = new SubViewport { OwnWorld3D = true, AudioListenerEnable3D = true, HandleInputLocally = false, GuiDisableInput = true, RenderTargetUpdateMode = DrawUnseen ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.WhenVisible, Name = "Viewport" };
        AddChild(_vp);
        _room = new GarageRoom();
        _vp.AddChild(_room.Root);
        _holder = new Node3D { Name = "Holder", Position = new Vector3(0, GarageRoom.TurntableHeight + 0.022f, 0) };
        _room.Turntable.AddChild(_holder);
        _sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 0.55f, ShadowEnabled = true, DirectionalShadowMaxDistance = 24, LightColor = new Color(1f, 0.96f, 0.9f),
            RotationDegrees = new Vector3(-62, 28, 0) };
        _vp.AddChild(_sun);
        _modernEnv = new Environment
        {
            BackgroundMode = Environment.BGMode.Color, BackgroundColor = new Color(0.04f, 0.045f, 0.05f),
            AmbientLightSource = Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.64f, 0.7f), AmbientLightEnergy = 0.5f,
            ReflectedLightSource = Environment.ReflectionSource.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.50f, 0.52f, 0.56f), SkyHorizonColor = new Color(0.78f, 0.76f, 0.70f),
                GroundHorizonColor = new Color(0.55f, 0.53f, 0.50f), GroundBottomColor = new Color(0.22f, 0.22f, 0.23f), SkyEnergyMultiplier = 1.2f,
            } },
            TonemapMode = Environment.ToneMapper.Filmic, SsaoEnabled = !I76.Game.Boot.RenderBackend.Compatibility, GlowEnabled = true, GlowIntensity = 0.5f, GlowBloom = 0.05f,
        };
        _env1997 = new Environment
        {
            BackgroundMode = Environment.BGMode.Color, BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
            AmbientLightSource = Environment.AmbientSource.Disabled, ReflectedLightSource = Environment.ReflectionSource.Disabled,
            TonemapMode = Environment.ToneMapper.Linear,
        };
        _env = new WorldEnvironment { Environment = _modernEnv };
        _vp.AddChild(_env);
        _cam = new Camera3D { Name = "Camera", Near = 0.1f, Far = 60f, Fov = 42f, Current = true };
        _vp.AddChild(_cam);
        _marker = new MeshInstance3D
        {
            Name = "Marker", Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f, Rings = 32, RingSegments = 8 },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.78f, 0.12f, 0.9f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true, RenderPriority = 5 },
        };
        _vp.AddChild(_marker);
        if (MusicInstalls is { Count: > 0 } installs)
        {
            Radio = new GarageRadio { Name = "Radio", Installs = installs, Level = MusicLevel, Position = GarageRoom.Boombox + new Vector3(0, 0.15f, 0) };
            _vp.AddChild(Radio);
        }
        SetLook(Mode1997);
        Place(snap: true);
    }

    /// <summary>The look: the 1997 shaders and a flat card-like frame, or the modern ones with Godot's light, shadows and reflections.</summary>
    public void SetLook(bool mode1997)
    {
        Mode1997 = mode1997;
        Assets.SetLook(mode1997);
        Assets.SetGlobals(mode1997);
        if (_vp is null) return;
        _room.SetLook(mode1997);
        _env.Environment = mode1997 ? _env1997 : _modernEnv;
        _sun.ShadowEnabled = !mode1997;
        _sun.LightEnergy = mode1997 ? 0f : 0.55f;
        foreach (var l in _room.Lamps) l.Visible = !mode1997;
        _vp.Msaa3D = mode1997 ? Viewport.Msaa.Disabled : Viewport.Msaa.Msaa4X;
        _cam.Fov = mode1997 ? 60f : 42f;
        TextureFilter = mode1997 ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear;
        if (mode1997) _vp.Scaling3DScale = 1f;
        if (Car is { } c) c.Node.Modern = !mode1997;
    }

    // ---- the car ----

    /// <summary>Shows this car (its file is built through the game's car assembly), replacing the one on the turntable.</summary>
    public void SetCar(TripCar car, I76.Formats.Imaging.CarPaint? paint = null)
    {
        var fresh = GarageCar.Build(Assets, car, Mode1997, paint);
        var old = Car;
        Car = fresh;
        _holder.AddChild(fresh);
        _holder.Scale = Vector3.One * fresh.Fit;
        if (old is not null) { _holder.RemoveChild(old); old.QueueFree(); }
        Assets.SetLook(Mode1997);
    }

    /// <summary>The car's own frame to the world.</summary>
    public Vector3 ToWorld(Vector3 local) => _holder.ToGlobal(local);

    // ---- the camera ----

    /// <summary>Flies the camera to look here; a change of place gives the hand-turned view back.</summary>
    public void Focus(FocusSpec spec)
    {
        if (spec == _focus) return;
        _focus = spec;
        _userYaw = _userPitch = 0;
        _userZoom = 1f;
    }

    /// <summary>A hand's turn of the camera (mouse pixels): it keeps the turntable still for a few seconds.</summary>
    public void Orbit(float dx, float dy)
    {
        _userYaw -= Mathf.DegToRad(dx * 0.35f);
        _userPitch = Mathf.Clamp(_userPitch + Mathf.DegToRad(dy * 0.25f), Mathf.DegToRad(-40), Mathf.DegToRad(50));
        _idle = 0;
    }

    public void Zoom(int notches)
    {
        _userZoom = Mathf.Clamp(_userZoom * MathF.Pow(0.9f, notches), 0.45f, 1.7f);
        _idle = 0;
    }

    /// <summary>The marker pulsing at a point of the car (its frame), or none.</summary>
    public void Mark(Vector3? local, float size = 0.35f)
    {
        _markerAt = local;
        _marker.Visible = local is not null;
        _marker.Scale = Vector3.One * size;
    }

    /// <summary>I76_GARAGE_CAM=x,y,z,tx,ty,tz: the camera held there, looking at that point (world axes), for a capture of the bay.</summary>
    private static readonly (Vector3 From, Vector3 At)? DebugCam = System.Environment.GetEnvironmentVariable("I76_GARAGE_CAM")?.Split(',') is { Length: 6 } c
        && c.Select(v => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : float.NaN).ToArray() is var f && f.All(x => !float.IsNaN(x))
        ? (new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5])) : null;
    /// <summary>Capture hook (the tour): a camera held at this place.</summary>
    public (Vector3 From, Vector3 At)? Hold { get; set; }
    private bool _frozen;
    private int _dbg;
    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // a child of the shell's canvas layer is not laid out by a parent: the window's size is its own
        if (GetViewport()?.GetVisibleRect().Size is { } win && Size != win) { Position = Vector2.Zero; Size = win; }
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_DEBUG") == "1" && ++_dbg % 20 == 1) GD.Print($"garage3d: container {Size} viewport {_vp.Size} cam {_cam.Position} looking at {_tgt} car {Car?.Bounds} spin {_spin}");
        _idle += dt;
        // the turntable: turns in the overview, stops for a close-up (or a hand on the camera) and eases in and out
        bool spinning = !_focus.Relative && _idle > 3f && !_frozen;
        _spinSpeed = Mathf.Lerp(_spinSpeed, spinning ? SpinRate : 0f, 1f - MathF.Exp(-dt * 2.5f));
        _spin += _spinSpeed * dt;
        _room.Turntable.RotationDegrees = new Vector3(0, _spin, 0);
        Place(snap: false, dt);
        if (_markerAt is { } m)
        {
            _markerPhase += dt * 4.5f;
            _marker.GlobalPosition = ToWorld(m);
            float s = 1f + 0.18f * MathF.Sin(_markerPhase);
            _marker.Scale = Vector3.One * 0.3f * s;
            _marker.LookAt(_cam.GlobalPosition, Vector3.Up);
            _marker.RotateObjectLocal(Vector3.Right, -Mathf.Pi / 2);
        }
    }

    /// <summary>Capture hook: the camera at its goal at once.</summary>
    public void Snap(bool wide = false) { _frozen = true; _spin = 0; _spinSpeed = 0; _cam.Fov = wide ? 72f : Mode1997 ? 60f : 42f; Place(snap: true); }

    /// <summary>Capture hook: the 3D view as drawn.</summary>
    public void ShowRoom(bool on) { foreach (var c in _room.Root.GetChildren().OfType<Node3D>()) if (c != _room.Turntable) c.Visible = on; }

    public Image Grab() => _vp.GetTexture().GetImage();

    /// <summary>Capture hook (car segmentation views): an orthographic camera at <paramref name="from"/> looking at <paramref name="at"/>, <paramref name="height"/> world units tall.</summary>
    public void OrthoCamera(Vector3 from, Vector3 at, Vector3 up, float height)
    {
        _frozen = true; _spin = 0; _spinSpeed = 0; _room.Turntable.RotationDegrees = Vector3.Zero;
        Hold = (from, at);
        _cam.Projection = Camera3D.ProjectionType.Orthogonal; _cam.Size = height; _cam.Near = 0.05f; _cam.Far = 100f;
        _cam.Position = from; _cam.LookAt(at, up);
    }

    /// <summary>Capture hook: the turntable (the car's floor) shown or not.</summary>
    public void ShowTurntable(bool on) { foreach (var c in _room.Turntable.GetChildren().OfType<Node3D>()) if (c != _holder) c.Visible = on; }

    /// <summary>Capture hook: the background colour (the segmentation views' neutral grey; the wheel mask's black).</summary>
    public void Background(Color c) { _modernEnv.BackgroundColor = c; _modernEnv.BackgroundMode = Environment.BGMode.Color; }

    private void Place(bool snap, float dt = 0.016f)
    {
        if (_cam.Projection == Camera3D.ProjectionType.Orthogonal) return;   // the segmentation views' camera, set by OrthoCamera
        float turn = Mathf.DegToRad(_spin);
        var target = ToWorld(_focus.Target);
        float goalYaw = Mathf.DegToRad(_focus.Yaw) + (_focus.Relative ? turn : 0) + _userYaw;
        float goalPitch = Mathf.DegToRad(_focus.Pitch) + _userPitch, goalDist = _focus.Distance * _userZoom;
        float k = snap ? 1f : 1f - MathF.Exp(-dt * 3.2f);
        _tgt = _tgt.Lerp(target, snap ? 1f : 1f - MathF.Exp(-dt * 5f));
        _yaw += (float)Math.IEEERemainder(goalYaw - _yaw, Math.Tau) * k;
        _pitch = Mathf.Lerp(_pitch, goalPitch, k);
        _dist = Mathf.Lerp(_dist, goalDist, k);
        var dir = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));
        var pos = _tgt + dir * _dist;
        // inside the bay: the walls and the ceiling stop the camera
        pos = new Vector3(Mathf.Clamp(pos.X, -GarageRoom.HalfX + 0.5f, GarageRoom.HalfX - 0.5f), Mathf.Clamp(pos.Y, 0.25f, GarageRoom.Height - 0.7f), Mathf.Clamp(pos.Z, -GarageRoom.HalfZ + 0.5f, GarageRoom.HalfZ - 0.5f));
        _cam.Position = pos;
        _cam.LookAt(_tgt, Vector3.Up);
        if ((Hold ?? DebugCam) is { } dc) { _cam.Position = dc.From; _cam.LookAt(dc.At, Vector3.Up); return; }
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_TOUR") is not null) return;
        // the notepad takes the left of the window: the view's middle is moved to the right of it, so the car stands in the clear
        var vs = _vp.Size;
        float aspect = vs.Y > 0 ? (float)vs.X / vs.Y : 1.78f;
        float shift = ViewShift * _dist * MathF.Tan(Mathf.DegToRad(_cam.Fov) / 2) * aspect;
        _cam.LookAt(_tgt - _cam.GlobalBasis.X * shift, Vector3.Up);
    }

    public override void _ExitTree() { Assets.RestoreGlobals(); _vp?.QueueFree(); }
}
