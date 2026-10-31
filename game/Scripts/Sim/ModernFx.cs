using Godot;

namespace I76.Game.Sim;

/// <summary>
/// The modern look of combat (not in the original; 1997 mode leaves it out):
/// glowing tracer streaks, spark bursts and dust where rounds land, smoke
/// trails behind rockets, and brief lights at muzzles and impacts. Purely
/// visual: nothing here feeds back into the simulation.
/// </summary>
public partial class ModernFx : Node3D
{
    private ShaderMaterial _tracer = null!;
    private ArrayMesh _strip = null!;
    private ParticleProcessMaterial _sparkProc = null!, _dustProc = null!, _smokeProc = null!;
    private Mesh _sparkMesh = null!, _puffMesh = null!;
    private readonly List<(Node Node, double Until)> _expiring = [];
    private readonly List<(OmniLight3D Light, double Start, double Life, float Energy)> _flashes = [];
    private QuadMesh _quad = null!;
    private Flipbook? _explosionHd, _explosion, _fireball, _smoke;
    private readonly List<Sprite> _sprites = [];

    /// <summary>The mission's light for smoke sprites (fire keeps its own brightness): 1 by day, lower at night.</summary>
    public float SceneLight
    {
        get => _sceneLight;
        set
        {
            _sceneLight = value;
            foreach (var b in new[] { _explosionHd, _explosion, _fireball, _smoke }) b?.Material.SetShaderParameter("scene_light", value);
        }
    }
    private float _sceneLight = 1f;

    /// <summary>A flipbook atlas and its material (flipbook.gdshader).</summary>
    private sealed record Flipbook(ShaderMaterial Material, int Frames);

    /// <summary>One flipbook sprite in flight: its frames over its life, its growth, drift and fade.</summary>
    private sealed class Sprite
    {
        public required MeshInstance3D Node;
        public required Flipbook Book;
        public double Start, Life;
        public bool Loop;
        public float FrameRate;           // looping books: frames per second
        public Vector3 At, Velocity;
        public float Size0, Size1, FadeIn, FadeOut, Opacity = 1f;
    }
    private double _t;

    private static readonly Aabb Everywhere = new(new Vector3(-1e5f, -1e5f, -1e5f), new Vector3(2e5f, 2e5f, 2e5f));

    public override void _Ready()
    {
        _tracer = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/tracer.gdshader") };
        _quad = new QuadMesh { Size = Vector2.One };
        var shader = GD.Load<Shader>("res://Shaders/flipbook.gdshader");
        _explosionHd = LoadBook(shader, "Explosion02HD_5x5.tga", 5, 5, false);
        _explosion = LoadBook(shader, "Explosion01_5x5.tga", 5, 5, false);
        _fireball = LoadBook(shader, "FireBall02_8x8.tga", 8, 8, true);
        _smoke = LoadBook(shader, "WispySmoke01_8x8.tga", 8, 8, false);
        SceneLight = _sceneLight;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 0f), (1f, 1f), (0f, 1f) })
        {
            st.SetUV(new Vector2(u, v));
            st.AddVertex(new Vector3(u, v, 0));
        }
        _strip = st.Commit();

        // sparks: hot streaks along their velocity, falling and fading
        var sparkShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                void fragment() { ALBEDO = COLOR.rgb * 7.0 * COLOR.a; }
                """,
        };
        _sparkMesh = new BoxMesh { Size = new Vector3(0.025f, 0.35f, 0.025f), Material = new ShaderMaterial { Shader = sparkShader } };
        _sparkProc = new ParticleProcessMaterial
        {
            Direction = Vector3.Up, Spread = 55, InitialVelocityMin = 5, InitialVelocityMax = 15,
            Gravity = new Vector3(0, -20, 0), DampingMin = 1, DampingMax = 3,
            ColorRamp = Ramp((0, new Color(1f, 0.95f, 0.75f, 1)), (0.35f, new Color(1f, 0.6f, 0.2f, 1)), (1, new Color(0.8f, 0.2f, 0.05f, 0))),
            ScaleCurve = Curve1((0, 1), (1, 0.3f)),
        };
        _sparkProc.SetParticleFlag(ParticleProcessMaterial.ParticleFlags.AlignYToVelocity, true);

        // dust and smoke: soft lit puffs
        var soft = new GradientTexture2D
        {
            Gradient = new Gradient { Offsets = [0f, 0.55f, 1f], Colors = [new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.45f), new Color(1, 1, 1, 0)] },
            Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64,
        };
        var puffMat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true, AlbedoTexture = soft, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ProximityFadeEnabled = true, ProximityFadeDistance = 0.6f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _puffMesh = new QuadMesh { Size = Vector2.One, Material = puffMat };
        _dustProc = new ParticleProcessMaterial
        {
            Direction = Vector3.Up, Spread = 35, InitialVelocityMin = 1.5f, InitialVelocityMax = 4,
            Gravity = new Vector3(0, -1.5f, 0), DampingMin = 2, DampingMax = 3, ScaleMin = 0.5f, ScaleMax = 0.9f,
            ColorRamp = Ramp((0, new Color(0.58f, 0.42f, 0.3f, 0.75f)), (1, new Color(0.6f, 0.47f, 0.36f, 0))),
            ScaleCurve = Curve1((0, 0.6f), (1, 2.2f)),
        };
        _smokeProc = new ParticleProcessMaterial
        {
            Direction = Vector3.Up, Spread = 180, InitialVelocityMin = 0.2f, InitialVelocityMax = 0.8f,
            Gravity = new Vector3(0, 0.6f, 0), DampingMin = 0.5f, DampingMax = 1, ScaleMin = 0.35f, ScaleMax = 0.55f,
            ColorRamp = Ramp((0, new Color(0.9f, 0.85f, 0.78f, 0.55f)), (0.3f, new Color(0.62f, 0.6f, 0.57f, 0.4f)), (1, new Color(0.55f, 0.53f, 0.5f, 0))),
            ScaleCurve = Curve1((0, 0.5f), (1, 3f)),
        };
    }

    private static GradientTexture1D Ramp(params (float At, Color C)[] stops) =>
        new() { Gradient = new Gradient { Offsets = stops.Select(s => s.At).ToArray(), Colors = stops.Select(s => s.C).ToArray() } };

    private static CurveTexture Curve1(params (float X, float Y)[] pts)
    {
        var c = new Curve { MaxValue = pts.Max(p => p.Y) };
        foreach (var (x, y) in pts) c.AddPoint(new Vector2(x, y));
        return new CurveTexture { Curve = c };
    }

    private int _clearGen;

    /// <summary>The look switched to 1997: every effect in flight goes at once (sprites, lights, bursts, bullet trails, pending fireworks). The tracers and muzzle flashes are the caller's, hidden by it.</summary>
    public void Clear()
    {
        _clearGen++;
        foreach (var sp in _sprites) if (IsInstanceValid(sp.Node)) sp.Node.QueueFree();
        _sprites.Clear();
        foreach (var f in _flashes) if (IsInstanceValid(f.Light)) f.Light.QueueFree();
        _flashes.Clear();
        foreach (var e in _expiring) if (IsInstanceValid(e.Node)) e.Node.QueueFree();
        _expiring.Clear();
        foreach (var b in _balls.Values) b.Free();
        _balls.Clear();
        foreach (var f in _fadingTails) if (IsInstanceValid(f.Tail.Node)) f.Tail.Node.QueueFree();
        _fadingTails.Clear();
    }

    // ---- tracers ----

    public MeshInstance3D NewTracer()
    {
        var m = new MeshInstance3D { Mesh = _strip, MaterialOverride = _tracer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = Everywhere };
        AddChild(m);
        return m;
    }

    public static void SetTracer(MeshInstance3D m, Vector3 tail, Vector3 head, Color tint, float width, float energy)
    {
        m.SetInstanceShaderParameter("tail", tail);
        m.SetInstanceShaderParameter("head", head);
        m.SetInstanceShaderParameter("tint", tint);
        m.SetInstanceShaderParameter("width", width);
        m.SetInstanceShaderParameter("energy", energy);
    }

    // ---- bursts ----

    private void Burst(ParticleProcessMaterial proc, Mesh mesh, int amount, double life, Vector3 at, Vector3 up)
    {
        var p = new GpuParticles3D
        {
            ProcessMaterial = proc, DrawPass1 = mesh, Amount = amount, Lifetime = life, OneShot = true, Explosiveness = 1,
            LocalCoords = false, VisibilityAabb = new Aabb(new Vector3(-10, -10, -10), new Vector3(20, 20, 20)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(p);
        p.GlobalTransform = new Transform3D(Facing(up), at);
        p.Emitting = true;
        _expiring.Add((p, _t + life + 0.2));
    }

    /// <summary>A basis whose +Y is the given direction (the process materials spray along +Y).</summary>
    private static Basis Facing(Vector3 up)
    {
        var y = up.LengthSquared() > 1e-6f ? up.Normalized() : Vector3.Up;
        var x = Mathf.Abs(y.Dot(Vector3.Forward)) < 0.9f ? y.Cross(Vector3.Forward).Normalized() : y.Cross(Vector3.Right).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    // ---- fireworks ----

    private static readonly Color[] ShellColours =
    [
        new(1f, 0.25f, 0.2f), new(0.3f, 0.6f, 1f), new(0.35f, 1f, 0.35f), new(1f, 0.85f, 0.25f), new(1f, 0.4f, 1f), new(0.95f, 0.95f, 1f),
    ];

    private ParticleProcessMaterial ShellProc(Color c)
    {
        var m = new ParticleProcessMaterial
        {
        Direction = Vector3.Up, Spread = 180, InitialVelocityMin = 9, InitialVelocityMax = 15,
        Gravity = new Vector3(0, -5, 0), DampingMin = 2.5f, DampingMax = 3.5f,
        ColorRamp = Ramp((0, new Color(1, 1, 0.9f, 1)), (0.08f, new Color(c, 1)), (0.7f, new Color(c, 0.9f)), (1, new Color(c * 0.7f, 0))),
        ScaleCurve = Curve1((0, 1.4f), (1, 0.6f)),
        };
        m.SetParticleFlag(ParticleProcessMaterial.ParticleFlags.AlignYToVelocity, true);
        return m;
    }

    private Mesh? _starMesh;

    /// <summary>A firework star: a longer, thicker streak than a spark, its colour kept rather than burnt to white.</summary>
    private Mesh StarMesh() => _starMesh ??= new BoxMesh
    {
        Size = new Vector3(0.1f, 0.7f, 0.1f),
        Material = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = """
                    shader_type spatial;
                    render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                    void fragment() { ALBEDO = COLOR.rgb * 1.3; ALPHA = COLOR.a; }
                    """,
            },
        },
    };

    /// <summary>
    /// The fireworks stand's own explosion (xbldx2.xdf, its burst centred 12 m
    /// up where a building's is 4.7): in the modern look, coloured shells
    /// bursting over the blast for a few seconds, each with a flash of its light.
    /// </summary>
    public void Fireworks(Vector3 at)
    {
        int gen = _clearGen;
        var rng = new RandomNumberGenerator();
        for (int i = 0; i < 9; i++)
        {
            double delay = i == 0 ? 0.05 : rng.RandfRange(0.1f, 2.6f);
            var c = ShellColours[rng.RandiRange(0, ShellColours.Length - 1)];
            var p = at + new Vector3(rng.RandfRange(-7, 7), rng.RandfRange(6, 13), rng.RandfRange(-7, 7));
            var proc = ShellProc(c);
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                if (!IsInsideTree() || gen != _clearGen) return;
                Burst(proc, StarMesh(), 70, 1.8, p, Vector3.Up);
                Light(p, 6f, 25, 0.35, c);
            };
        }
    }

    /// <summary>A round strikes: sparks sprayed along <paramref name="spray"/>, dust as well off the ground, and a flash of light.</summary>
    public void Strike(Vector3 at, Vector3 spray, bool ground, bool heavy)
    {
        Burst(_sparkProc, _sparkMesh, heavy ? 22 : 12, 0.45, at, spray);
        if (ground) Burst(_dustProc, _puffMesh, heavy ? 10 : 6, 0.9, at, Vector3.Up);
        Light(at + spray.Normalized() * 0.3f, heavy ? 3.5f : 2f, heavy ? 6 : 4, 0.06, new Color(1f, 0.7f, 0.35f));
    }

    public void Light(Vector3 at, float energy, float range, double life, Color color)
    {
        var l = new OmniLight3D { LightColor = color, LightEnergy = energy, OmniRange = range, ShadowEnabled = false };
        AddChild(l);
        l.GlobalPosition = at;
        _expiring.Add((l, _t + life));
    }

    // ---- muzzle flashes ----

    private ArrayMesh? _muzzleMesh;
    private ShaderMaterial? _muzzleMat;
    private ParticleProcessMaterial? _gunSmokeProc;

    /// <summary>
    /// A muzzle flash (muzzle_flash.gdshader), hidden until fired: three
    /// unit planes crossed along -Z from the origin (the tongue, one unit
    /// long and wide) and one across it near the muzzle (the star, 1.5 wide),
    /// scaled by the caller to the gun.
    /// </summary>
    public MeshInstance3D NewMuzzleFlash()
    {
        if (_muzzleMesh is null)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            void Quad(Vector3[] v, Vector2[] uv, Vector3 n, Color c)
            {
                foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    st.SetColor(c); st.SetNormal(n); st.SetUV(uv[i]); st.AddVertex(v[i]);
                }
            }
            Vector2[] tongueUv = [new(0, 0), new(0, 1), new(1, 1), new(1, 0)];
            for (int k = 0; k < 3; k++)
            {
                float a = k * Mathf.Pi / 3;
                var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * 0.5f;
                Quad([-d, d, d + Vector3.Forward, -d + Vector3.Forward], tongueUv, d.Cross(Vector3.Forward).Normalized(), Colors.Black);
            }
            const float h = 0.75f, z = -0.04f;
            Quad([new(-h, -h, z), new(h, -h, z), new(h, h, z), new(-h, h, z)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], Vector3.Back, Colors.Red);
            _muzzleMesh = st.Commit();
            _muzzleMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/muzzle_flash.gdshader") };
        }
        return new MeshInstance3D { Mesh = _muzzleMesh, MaterialOverride = _muzzleMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
    }

    public static void SetMuzzleFlash(MeshInstance3D m, float age, float seed, float energy)
    {
        m.SetInstanceShaderParameter("age", age);
        m.SetInstanceShaderParameter("seed", seed);
        m.SetInstanceShaderParameter("energy", energy);
    }

    /// <summary>A whiff of gun smoke left in the air at the muzzle, blown forward and spreading.</summary>
    public void GunSmoke(Vector3 at, Vector3 forward, float size)
    {
        if (_gunSmokeProc is null)
        {
            float l = Mathf.Clamp(_sceneLight, 0.25f, 1f);
            _gunSmokeProc = new ParticleProcessMaterial
            {
                Direction = Vector3.Up, Spread = 15, InitialVelocityMin = 3f, InitialVelocityMax = 6f,
                Gravity = new Vector3(0, 0.4f, 0), DampingMin = 5, DampingMax = 8, ScaleMin = 0.18f, ScaleMax = 0.3f,
                ColorRamp = Ramp((0, new Color(0.7f * l, 0.68f * l, 0.65f * l, 0.12f)), (0.3f, new Color(0.62f * l, 0.61f * l, 0.59f * l, 0.08f)), (1, new Color(0.55f * l, 0.55f * l, 0.53f * l, 0))),
                ScaleCurve = Curve1((0, 0.5f), (1, 3f)),
            };
        }
        Burst(_gunSmokeProc, _puffMesh, size > 1.2f ? 2 : 1, 1.1, at, forward);
    }

    // ---- explosions ----

    /// <summary>Whether the flipbooks were fetched (tools/fetch-vfx.sh); without them explosions use the XDFC sprites.</summary>
    public bool HasExplosions => _explosionHd is not null || _explosion is not null;

    /// <summary>
    /// A texture from game/vfx (tools/fetch-vfx.sh): the imported one in an export, else the fetched file read
    /// directly (a development run needs no import step); null when it was not fetched. Loaded once.
    /// </summary>
    public static Texture2D? LoadVfx(string file)
    {
        if (VfxCache.TryGetValue(file, out var cached)) return cached;
        var res = "res://vfx/" + file;
        Texture2D? tex = null;
        if (ResourceLoader.Exists(res)) tex = GD.Load<Texture2D>(res);   // an imported texture whose cache (.godot/imported) is missing exists but does not load: read the file then
        if (tex is null && System.IO.File.Exists(ProjectSettings.GlobalizePath(res)) && Image.LoadFromFile(ProjectSettings.GlobalizePath(res)) is { } img && !img.IsEmpty())
        {
            img.GenerateMipmaps();
            tex = ImageTexture.CreateFromImage(img);
        }
        return VfxCache[file] = tex;
    }

    private static readonly Dictionary<string, Texture2D?> VfxCache = [];

    /// <summary>A flipbook from game/vfx (<see cref="LoadVfx"/>) and its material.</summary>
    private static Flipbook? LoadBook(Shader shader, string file, int cols, int rows, bool additive)
    {
        if (LoadVfx(file) is not { } tex) return null;
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("atlas", tex);
        m.SetShaderParameter("grid", new Vector2(cols, rows));
        m.SetShaderParameter("additive", additive);
        m.SetShaderParameter("clip_cab", file.StartsWith("WispySmoke"));
        return new Flipbook(m, cols * rows);
    }

    private Sprite AddSprite(Flipbook book, Vector3 at, float size0, float size1, double delay, double life, float spin)
    {
        var node = new MeshInstance3D { Mesh = _quad, MaterialOverride = book.Material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, CustomAabb = new Aabb(-Vector3.One, Vector3.One * 2) };
        AddChild(node);
        node.SetInstanceShaderParameter("spin", spin);
        var sp = new Sprite { Node = node, Book = book, Start = _t + delay, Life = life, At = at, Size0 = size0, Size1 = size1 };
        _sprites.Add(sp);
        return sp;
    }

    /// <summary>
    /// An explosion of radius <paramref name="size"/> m from the Unity Labs
    /// flipbooks, layered as C&amp;C Generals did: two or three explosion sprites
    /// a beat apart (the HD sequence for big ones), an additive fireball flash
    /// at the core, smoke puffs that rise and spread after it, plus a flash of
    /// light and sparks. <paramref name="smoke"/> tints the smoke (white
    /// phosphorus: white). False when the flipbooks are missing.
    /// </summary>
    public bool Explode(Vector3 at, float size, double life, Color smoke, float fire = 1f)
    {
        var main = size >= 4 ? _explosionHd ?? _explosion : _explosion ?? _explosionHd;
        if (main is null) return false;
        int layers = size >= 6 ? 3 : size >= 4 ? 2 : 1;
        for (int k = 0; k < layers; k++)
        {
            var off = new Vector3(GD.Randf() - 0.5f, 0, GD.Randf() - 0.5f) * size * 0.35f * k;
            float s = size * (2.1f - 0.3f * k);
            var sp = AddSprite(main, at + off + Vector3.Up * size * (0.55f + 0.15f * k), s * 0.9f, s * 1.1f, k * 0.09, life * (0.9f - 0.08f * k), GD.Randf() * Mathf.Tau);
            sp.Velocity = Vector3.Up * size * 0.12f;
            sp.FadeOut = 0.25f;
            if (fire < 1f) sp.Node.SetInstanceShaderParameter("tint", new Color(1.15f, 1.15f, 1.15f));
        }
        if (_fireball is not null && fire > 0.5f)
        {
            var fb = AddSprite(_fireball, at + Vector3.Up * size * 0.5f, size * 1.4f, size * 2.4f, 0, 0.55, GD.Randf() * Mathf.Tau);
            fb.Loop = true; fb.FrameRate = 40; fb.FadeOut = 0.7f;
        }
        if (_smoke is not null)
            for (int k = 0; k < layers * 2; k++)
            {
                var off = new Vector3(GD.Randf() - 0.5f, 0, GD.Randf() - 0.5f) * size * 0.8f;
                var sm = AddSprite(_smoke, at + off + Vector3.Up * size * 0.8f, size * 1.3f, size * 3.2f, life * (0.35 + 0.08 * k), 3.5 + GD.Randf(), GD.Randf() * Mathf.Tau);
                sm.Loop = true; sm.FrameRate = 14; sm.FadeIn = 0.2f; sm.FadeOut = 0.6f; sm.Opacity = 0.6f;
                sm.Velocity = new Vector3(GD.Randf() - 0.5f, 2.2f + GD.Randf() * 1.5f, GD.Randf() - 0.5f);
                sm.Node.SetInstanceShaderParameter("tint", smoke.Lerp(Colors.White, 0.2f) * 1.6f);
            }
        var l = new OmniLight3D { LightColor = new Color(1f, 0.65f, 0.3f), LightEnergy = 6f * fire, OmniRange = size * 5f, ShadowEnabled = false };
        AddChild(l);
        l.GlobalPosition = at + Vector3.Up * size * 0.5f;
        _flashes.Add((l, _t, 0.45 + size * 0.04, l.LightEnergy));
        Burst(_sparkProc, _sparkMesh, (int)Mathf.Clamp(size * 6, 12, 48), 0.7, at + Vector3.Up * 0.3f, Vector3.Up);
        return true;
    }

    /// <summary>
    /// A rehearsal of the effects a mission shows for the first time behind the loading card: the explosion's flipbooks, its light and
    /// sparks, the fireball burst and a puff of gun smoke, all alive at once at <paramref name="at"/> (Godot coordinates), so that the
    /// renderer compiles their pipelines now and not in the frame of the first blast. <see cref="Clear"/> takes them away again.
    /// </summary>
    public void Warm(Vector3 at)
    {
        Explode(at, 6f, 1.0, new Color(0.3f, 0.3f, 0.3f), 1f);
        FireBurst(at);
        GunSmoke(at, Vector3.Up, 2f);
        foreach (var sp in _sprites) { sp.Start = _t; sp.Life = 10; }   // the delayed ones too: every one is drawn
    }

    // ---- the Ball O'Flame ----

    /// <summary>One flying fireball: its layered flame cards, its light, the embers it sheds, and where it last dropped a trail flame.</summary>
    private sealed class Ball
    {
        public required MeshInstance3D[] Cards;
        public OmniLight3D? Light;
        public required GpuParticles3D Embers;
        public required Tail Flames;
        public Vector3 Last;
        public float Seed;
        public double Born;

        public void Free()
        {
            foreach (var c in Cards) if (IsInstanceValid(c)) c.QueueFree();
            if (Light is not null && IsInstanceValid(Light)) Light.QueueFree();
            if (IsInstanceValid(Embers)) Embers.QueueFree();
            if (IsInstanceValid(Flames.Node)) Flames.Node.QueueFree();
        }
    }

    private readonly Dictionary<object, Ball> _balls = [];
    private ParticleProcessMaterial? _emberProc;
    private Mesh? _emberMesh;
    private const int MaxBallLights = 3;   // the light budget: the nearest few balls light the scene, the rest only glow

    // layers of the flame: size (m), frame rate, frame offset, spin (rad/s), tint (above 1: hotter than the atlas's orange)
    private static readonly (float Size, float Rate, float Offset, float Spin, Color Tint)[] BallLayers =
    [
        (1.7f, 20f, 0f, 0.7f, new Color(1f, 0.62f, 0.34f)),     // the cooler, wider body
        (1.15f, 26f, 29f, -1.1f, new Color(1.35f, 1.0f, 0.62f)), // the hotter inner flame, turning the other way
        (0.5f, 31f, 13f, 1.9f, new Color(1.9f, 1.55f, 1.05f)),    // the white-hot core
    ];

    /// <summary>
    /// The modern Ball O'Flame (nitro weapon gsball, kind 0x15), drawn this frame at <paramref name="at"/>
    /// (Godot coordinates, where the shot is drawn): a roiling ball of flame (the looping fireball flipbook
    /// in three layers, hot core to cooler edge, additive), a tail of flames that shrink and rise behind it as it
    /// flies, embers, and a warm flickering light. <paramref name="key"/> is the round. Presentation only.
    /// </summary>
    public void Fireball(object key, Vector3 at)
    {
        if (_fireball is null) return;
        if (!_balls.TryGetValue(key, out var b))
        {
            _emberMesh ??= EmberMesh();
            _emberProc ??= new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.3f,
                Direction = Vector3.Up, Spread = 180, InitialVelocityMin = 0.6f, InitialVelocityMax = 2.4f,
                Gravity = new Vector3(0, 1.6f, 0), DampingMin = 0.8f, DampingMax = 1.8f, ScaleMin = 0.5f, ScaleMax = 1f,
                ColorRamp = Ramp((0, new Color(1f, 0.9f, 0.55f, 1)), (0.4f, new Color(1f, 0.45f, 0.1f, 0.9f)), (1, new Color(0.7f, 0.1f, 0.02f, 0))),
                ScaleCurve = Curve1((0, 1), (1, 0.2f)),
            };
            var embers = new GpuParticles3D
            {
                ProcessMaterial = _emberProc, DrawPass1 = _emberMesh, Amount = 26, Lifetime = 0.9, LocalCoords = false,
                VisibilityAabb = new Aabb(new Vector3(-20, -20, -20), new Vector3(40, 40, 40)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Emitting = true,
            };
            AddChild(embers);
            var cards = new MeshInstance3D[BallLayers.Length];
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i] = new MeshInstance3D { Mesh = _quad, MaterialOverride = _fireball.Material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(-Vector3.One, Vector3.One * 2) };
                AddChild(cards[i]);
            }
            OmniLight3D? light = null;
            if (_balls.Values.Count(o => o.Light is not null) < MaxBallLights)
            {
                light = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.22f), OmniRange = 11f, OmniAttenuation = 1.4f, ShadowEnabled = false };
                AddChild(light);
            }
            b = new Ball { Cards = cards, Light = light, Embers = embers, Flames = NewTail(), Last = at, Seed = GD.Randf() * 100f, Born = _t };
            _balls[key] = b;
        }
        double age = _t - b.Born;
        float flick = 0.5f + 0.25f * Mathf.Sin((float)age * 23f + b.Seed) + 0.25f * Mathf.Sin((float)age * 37.1f + b.Seed * 2.3f);   // 0..1, quick and uneven
        for (int i = 0; i < b.Cards.Length; i++)
        {
            var (size, rate, offset, spin, tint) = BallLayers[i];
            var card = b.Cards[i];
            size *= 1f + 0.12f * (flick - 0.5f) * (i + 1);
            card.SetInstanceShaderParameter("frame", (float)((age * rate + offset + b.Seed) % _fireball.Frames));
            card.SetInstanceShaderParameter("spin", b.Seed + (float)age * spin);
            card.SetInstanceShaderParameter("tint", tint);
            card.SetInstanceShaderParameter("opacity", i == 0 ? 0.8f : 1f);
            card.GlobalTransform = new Transform3D(Basis.Identity.Scaled(Vector3.One * size), at);
        }
        b.Embers.GlobalPosition = at;
        b.Last = at;
        b.Flames.Update(at, (float)GetProcessDeltaTime());
        if (b.Light is { } l)
        {
            l.GlobalPosition = at;
            l.LightEnergy = 3.2f + 2.2f * flick;
        }
    }

    /// <summary>
    /// The fireball bursts (it struck: the missile blast's explosion is drawn too): a flare of the same flame flipbook that
    /// swells and fades, a spray of embers and a flash of warm light, so the hit stays a ball of fire and not just a bang.
    /// </summary>
    public void FireBurst(Vector3 at)
    {
        if (_fireball is null) return;
        for (int k = 0; k < 2; k++)
        {
            var sp = AddSprite(_fireball, at + Vector3.Up * (0.7f + 0.5f * k) + new Vector3(GD.Randf() - 0.5f, 0, GD.Randf() - 0.5f) * 0.8f, 1.6f + k * 0.5f, 3.4f - k * 0.4f, k * 0.05, 0.75 + 0.15 * k, GD.Randf() * Mathf.Tau);
            sp.Loop = true; sp.FrameRate = 34; sp.FadeOut = 0.75f; sp.Velocity = Vector3.Up * 1.2f;
            sp.Node.SetInstanceShaderParameter("tint", new Color(1.1f, 0.75f, 0.45f));
        }
        Burst(_sparkProc, _sparkMesh, 20, 0.7, at + Vector3.Up * 0.4f, Vector3.Up);
        var l = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.22f), LightEnergy = 5f, OmniRange = 12f, ShadowEnabled = false };
        AddChild(l);
        l.GlobalPosition = at + Vector3.Up;
        _flashes.Add((l, _t, 0.6, l.LightEnergy));
    }

    /// <summary>The fireball is gone (it struck, or ran out): its card and light go at once, its embers fade out where they are.</summary>
    public void ReleaseFireball(object key)
    {
        if (!_balls.Remove(key, out var b)) return;
        foreach (var c in b.Cards) c.QueueFree();
        b.Light?.QueueFree();
        _fadingTails.Add((b.Flames, b.Last, _t + Tail.Life + 0.1));
        b.Embers.Emitting = false;
        _expiring.Add((b.Embers, _t + 1.1));
    }

    /// <summary>
    /// The fireball's tail: flame cards from the same flipbook left along the drawn path (a card every 0.22 m, so even at 80 m/s
    /// they run together into one streak of fire), each playing its frames as it shrinks, rises and cools to red. One
    /// MultiMesh a ball, a ring of cards the oldest of which is reused; positions are kept here (Godot coordinates).
    /// </summary>
    private sealed class Tail
    {
        public const int Cap = 72;
        public const float Spacing = 0.22f, Life = 0.42f;
        public readonly MultiMeshInstance3D Node;
        private readonly MultiMesh _mm;
        private readonly Vector3[] _at = new Vector3[Cap];
        private readonly float[] _age = new float[Cap], _seed = new float[Cap], _size = new float[Cap], _rise = new float[Cap];
        private readonly float[] _buf = new float[Cap * 16];
        private int _next;
        private float _carry;
        private Vector3 _last;
        private bool _has;

        public Tail(ShaderMaterial mat)
        {
            _mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
                Mesh = new QuadMesh { Size = Vector2.One, Material = mat }, InstanceCount = Cap, VisibleInstanceCount = 0,
            };
            for (int i = 0; i < Cap; i++) _age[i] = float.MaxValue;
            Node = new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, TopLevel = true };
        }

        /// <summary>The ball is at <paramref name="at"/> this frame (<paramref name="dt"/> since the last): lay cards from where it was, age the rest.</summary>
        public void Update(Vector3 at, float dt)
        {
            if (!_has) { _last = at; _has = true; }
            var d = at - _last;
            float len = d.Length();
            _carry += len;
            int n = Mathf.Min((int)(_carry / Spacing), 24);
            for (int i = 1; i <= n && len > 1e-4f; i++)
            {
                float t = Mathf.Clamp((i * Spacing - (_carry - len)) / len, 0f, 1f);
                int k = _next;
                _next = (_next + 1) % Cap;
                _at[k] = _last + d * t + new Vector3(GD.Randf() - 0.5f, GD.Randf() - 0.4f, GD.Randf() - 0.5f) * 0.25f;
                _age[k] = (n - i) * Spacing / Mathf.Max(len / Mathf.Max(dt, 1e-3f), 1f);   // the ones laid first this frame are older by the time it took to reach them
                _seed[k] = GD.Randf();
                _size[k] = 0.8f + GD.Randf() * 0.5f;
                _rise[k] = 0.5f + GD.Randf() * 0.9f;
            }
            _carry -= n * Spacing;
            if (_carry > Spacing) _carry = 0f;
            _last = at;
            int cnt = 0;
            for (int i = 0; i < Cap; i++)
            {
                if ((_age[i] += dt) >= Life) { _age[i] = float.MaxValue; continue; }
                float l = _age[i] / Life;
                float size = _size[i] * Mathf.Lerp(1f, 0.3f, l);
                var p = _at[i] + Vector3.Up * (_rise[i] * _age[i]);
                int o = cnt++ * 16;
                _buf[o] = size; _buf[o + 1] = 0; _buf[o + 2] = 0; _buf[o + 3] = p.X;
                _buf[o + 4] = 0; _buf[o + 5] = size; _buf[o + 6] = 0; _buf[o + 7] = p.Y;
                _buf[o + 8] = 0; _buf[o + 9] = 0; _buf[o + 10] = size; _buf[o + 11] = p.Z;
                _buf[o + 12] = l; _buf[o + 13] = (_seed[i] * 63f + _age[i] * 24f) % 63f; _buf[o + 14] = _seed[i] * Mathf.Tau; _buf[o + 15] = 0;
            }
            _mm.VisibleInstanceCount = cnt;
            _mm.Buffer = _buf;
        }
    }

    private readonly List<(Tail Tail, Vector3 At, double Until)> _fadingTails = [];
    private ShaderMaterial? _tailMat;

    private Tail NewTail()
    {
        if (_tailMat is null)
        {
            _tailMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/fireball_tail.gdshader") };
            _tailMat.SetShaderParameter("atlas", LoadVfx("FireBall02_8x8.tga"));
        }
        var t = new Tail(_tailMat);
        AddChild(t.Node);
        return t;
    }

    /// <summary>A tiny additive billboard for embers (hot, then red, by the particle's colour).</summary>
    private static Mesh EmberMesh() => new QuadMesh
    {
        Size = new Vector2(0.12f, 0.12f),
        Material = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = """
                    shader_type spatial;
                    render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                    void vertex() {
                        float sc = length(MODEL_MATRIX[0].xyz);
                        MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
                    }
                    void fragment() {
                        float r = length(UV - vec2(0.5)) * 2.0;
                        ALBEDO = COLOR.rgb * 6.0 * COLOR.a * clamp(1.0 - r, 0.0, 1.0);
                    }
                    """,
            },
        },
    };

    // ---- smoke trails ----

    public GpuParticles3D Trail()
    {
        var p = new GpuParticles3D
        {
            ProcessMaterial = _smokeProc, DrawPass1 = _puffMesh, Amount = 90, Lifetime = 1.6, LocalCoords = false,
            VisibilityAabb = Everywhere, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Emitting = true,
        };
        AddChild(p);
        return p;
    }

    /// <summary>The rocket is gone: its trail stops emitting and drifts away before it is freed.</summary>
    public void EndTrail(GpuParticles3D p)
    {
        p.Emitting = false;
        _expiring.Add((p, _t + p.Lifetime + 0.1));
    }

    public override void _Process(double delta)
    {
        _t += delta;

        for (int i = _sprites.Count - 1; i >= 0; i--)
        {
            var sp = _sprites[i];
            double age = _t - sp.Start;
            if (age < 0) continue;
            float k = (float)(age / sp.Life);
            if (k >= 1f) { sp.Node.QueueFree(); _sprites.RemoveAt(i); continue; }
            sp.Node.Visible = true;
            float frame = sp.Loop ? (float)(age * sp.FrameRate % sp.Book.Frames) : k * (sp.Book.Frames - 1);
            float fade = (sp.FadeIn > 0 ? Mathf.Clamp(k / sp.FadeIn, 0, 1) : 1) * (sp.FadeOut > 0 ? Mathf.Clamp((1 - k) / sp.FadeOut, 0, 1) : 1);
            sp.Node.SetInstanceShaderParameter("frame", frame);
            sp.Node.SetInstanceShaderParameter("opacity", fade * sp.Opacity);
            float size = Mathf.Lerp(sp.Size0, sp.Size1, 1 - (1 - k) * (1 - k));
            sp.Node.GlobalTransform = new Transform3D(Basis.Identity.Scaled(Vector3.One * size), sp.At + sp.Velocity * (float)age);
        }
        for (int i = _fadingTails.Count - 1; i >= 0; i--)
        {
            var (tail, at, until) = _fadingTails[i];
            if (_t >= until) { tail.Node.QueueFree(); _fadingTails.RemoveAt(i); continue; }
            tail.Update(at, (float)delta);
        }
        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var (l, start, life, e) = _flashes[i];
            float k = (float)((_t - start) / life);
            if (k >= 1f) { l.QueueFree(); _flashes.RemoveAt(i); continue; }
            l.LightEnergy = e * (1 - k) * (1 - k);
        }

        for (int i = _expiring.Count - 1; i >= 0; i--)
            if (_t >= _expiring[i].Until)
            {
                if (IsInstanceValid(_expiring[i].Node)) _expiring[i].Node.QueueFree();
                _expiring.RemoveAt(i);
            }
    }
}
