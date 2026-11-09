using Godot;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's fire dropper (the original draws the 4 x 4 m
/// MFIRESPL quad looping XFP1_201..203): where each drop lies, a pool of fire with flame tongues (the
/// wreck flame shader, one shared MultiMesh), embers rising (pooled emitters, a few at a time; no smoke), a charred scorch (a GroundMark: a Decal, or a mesh on the drawn ground under Compatibility) that outlives the flames and then fades, and a warm flickering light
/// (three, on the patches that matter most to the camera). A car's consecutive drops are joined: scorch
/// ribbon and tongues run along the gap, the oldest end burning down first, as each patch dies at the
/// sim's 20 s. Pools are fixed; nothing is allocated per frame. The sim is untouched and the 1997 look
/// keeps the original's quads.
/// </summary>
public partial class FireDrops : Node3D
{
    private const int MaxPatches = 40, Tongues = 9, Along = 14, PerPatch = Tongues + Along, Slots = 8, Lights = 3;
    private const float Reach = 25f, Within = 3f, BurnDown = 5f, ScorchHold = 5f, ScorchFade = 6f, Size = 4f;


    private sealed class Patch
    {
        public bool Used, Dead, Chosen; public Round? R; public int Gen, Born;
        public Vector3 Pos, From; public bool Joined; public int Variant;
        public GroundMark? Scorch; public int Slot = -1; public double DeathT; public float Level;
        public readonly Vector3[] Off = new Vector3[PerPatch]; public readonly float[] Sc = new float[PerPatch];
        public int Count;
    }

    private readonly Patch[] _patches = new Patch[MaxPatches];
    private readonly Dictionary<Round, Patch> _byRound = [];
    private readonly Dictionary<I76.Sim.Vehicles.Vehicle, Round> _last = [];
    private readonly List<Round> _gone = [];
    private MultiMesh _mm = null!;
    private static Texture2D? _ribbonS, _puddleS, _glowRibbonS, _glowPuddleS;   // generated once for the run (the same from the same seeds)
    private Texture2D _ribbon = null!, _puddle = null!, _glowRibbon = null!, _glowPuddle = null!;
    private readonly GpuParticles3D[] _embers = new GpuParticles3D[Slots];
    private readonly Patch?[] _slotOwner = new Patch?[Slots];
    private readonly OmniLight3D[] _lights = new OmniLight3D[Lights];
    private int _gen, _bornSeq;
    private double _t;

    public override void _Ready()
    {
        for (int i = 0; i < MaxPatches; i++) _patches[i] = new Patch();
        Prebake();
        (_ribbon, _puddle, _glowRibbon, _glowPuddle) = (_ribbonS!, _puddleS!, _glowRibbonS!, _glowPuddleS);

        var flame = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/fire_patch.gdshader") };
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(1.5f, 1.6f), CenterOffset = new Vector3(0, 0.7f, 0), Material = flame },
            InstanceCount = MaxPatches * PerPatch, VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-3000, -200, -3000), new Vector3(6000, 600, 6000)), Name = "Tongues" });

        var emberProc = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = Vector3.One,
            Direction = Vector3.Up, Spread = 30, InitialVelocityMin = 1.5f, InitialVelocityMax = 3.5f,
            Gravity = new Vector3(0, 1.2f, 0), DampingMin = 0.8f, DampingMax = 1.5f, LifetimeRandomness = 0.5f,
            ColorRamp = new GradientTexture1D { Gradient = new Gradient
            {
                Offsets = [0f, 0.4f, 1f],
                Colors = [new Color(1f, 0.75f, 0.35f, 1), new Color(1f, 0.4f, 0.08f, 1), new Color(0.6f, 0.1f, 0.02f, 0)],
            } },
        };
        var emberMesh = new QuadMesh
        {
            Size = new Vector2(0.06f, 0.06f),
            Material = new ShaderMaterial { Shader = new Shader { Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                varying float v_seed;
                void vertex() {
                    v_seed = float(INSTANCE_ID) * 2.4;
                    float sc = length(MODEL_MATRIX[0].xyz);
                    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
                }
                void fragment() {
                    float r = 1.0 - smoothstep(0.2, 1.0, length(UV * 2.0 - 1.0));
                    float f = 0.6 + 0.4 * sin(TIME * 23.0 + v_seed);
                    ALBEDO = COLOR.rgb * 6.0 * COLOR.a * r * f;
                }
                """ } },
        };
        var aabb = new Aabb(new Vector3(-12, -3, -12), new Vector3(24, 30, 24));
        for (int i = 0; i < Slots; i++)
        {
            _embers[i] = Emitter((ParticleProcessMaterial)emberProc.Duplicate(), emberMesh, 8, 1.4, aabb);
        }
        for (int i = 0; i < Lights; i++)
        {
            _lights[i] = new OmniLight3D { LightColor = new Color(1f, 0.52f, 0.2f), OmniRange = 9f, OmniAttenuation = 1.4f, ShadowEnabled = false, Visible = false };
            AddChild(_lights[i]);
        }
    }

    private GpuParticles3D Emitter(ParticleProcessMaterial proc, Mesh mesh, int amount, double life, Aabb aabb)
    {
        var p = new GpuParticles3D
        {
            ProcessMaterial = proc, DrawPass1 = mesh, Amount = amount, Lifetime = life, LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = aabb, Emitting = false,
        };
        AddChild(p);
        return p;
    }

    /// <summary>The run's textures, baked once (on any thread: a mission building under its briefing makes them ahead of the view).</summary>
    public static void Prebake()
    {
        if (_glowPuddleS is not null) return;
        var rnd = new Random(7611);
        _ribbonS = Bake(false, rnd.Next(), false); _puddleS = Bake(true, rnd.Next(), false);
        _glowRibbonS = Bake(false, rnd.Next(), true); _glowPuddleS = Bake(true, rnd.Next(), true);
    }

    private static CurveTexture Curve1(params (float X, float Y)[] pts)
    {
        var c = new Curve { MaxValue = pts.Max(p => p.Y) };
        foreach (var (x, y) in pts) c.AddPoint(new Vector2(x, y));
        return new CurveTexture { Curve = c };
    }

    private static float Hash(int x, int y, int s)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + s * 144665);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }
    private static float Noise(float x, float y, int s)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        return Mathf.Lerp(Mathf.Lerp(Hash(xi, yi, s), Hash(xi + 1, yi, s), fx), Mathf.Lerp(Hash(xi, yi + 1, s), Hash(xi + 1, yi + 1, s), fx), fy);
    }
    private static float Fbm(float x, float y, int s) => Noise(x, y, s) * 0.55f + Noise(x * 2.1f, y * 2.1f, s + 1) * 0.3f + Noise(x * 4.3f, y * 4.3f, s + 2) * 0.15f;

    /// <summary>The scorch mask (alpha, charred brown-black, ash toward the edge) or, glow: the hot mottle inside it.</summary>
    private static Texture2D Bake(bool puddle, int seed, bool glow)
    {
        int w = 128, h = puddle ? 128 : 256, s = seed & 0xffff;
        var img = Image.CreateEmpty(w, h, true, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2 - 1, t = (y + 0.5f) / h, d, endFade = 1f;
                if (puddle)
                {
                    float v = t * 2 - 1, ang = MathF.Atan2(v, u);
                    d = MathF.Sqrt(u * u + v * v) / (0.82f + 0.14f * (Fbm(MathF.Cos(ang) * 1.6f + 3, MathF.Sin(ang) * 1.6f + 3, s) - 0.5f) * 3.2f);
                    d += (Fbm(u * 5 + 9, v * 5 + 9, s + 5) - 0.5f) * 0.3f;
                }
                else
                {
                    float hw = 0.78f + 0.2f * (Fbm(t * 3f, 0.5f, s) - 0.5f) * 2f;
                    d = MathF.Abs(u) / hw + (Fbm(u * 2 + 4, t * 5, s + 3) - 0.5f) * 0.7f;
                    endFade = Mathf.SmoothStep(0.0f, 0.15f, MathF.Min(t, 1 - t) + (Fbm(u * 3, t * 9, s + 8) - 0.5f) * 0.1f);
                }
                float m = Mathf.SmoothStep(1.0f, 0.55f, d) * endFade;
                float mottle = Fbm(u * 6 + 30, t * (puddle ? 6 : 12) + 30, s + 9);
                if (glow)
                {
                    // embers glowing in the char: bright in the middle, broken up by the noise
                    float g = Mathf.SmoothStep(0.55f, 0.9f, mottle + (1 - d) * 0.55f - 0.25f) * Mathf.SmoothStep(0.9f, 0.3f, d);
                    img.SetPixel(x, y, new Color(g, g * 0.38f, g * 0.05f, m));
                }
                else
                {
                    float ash = Mathf.SmoothStep(0.5f, 0.95f, d) * 0.5f;
                    float c = 0.025f + 0.035f * mottle + ash * 0.09f;
                    img.SetPixel(x, y, new Color(c * 1.1f, c, c * 0.9f, m * (0.92f - 0.3f * ash)));
                }
            }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    private GroundMark NewDecal()
    {
        var d = new GroundMark { GroundAt = GroundAt, FadeBegin = 100, FadeLength = 30 };
        AddChild(d);
        return d;
    }

    /// <summary>The drawn ground's height under a Godot-space point at a drop's level (see GroundMark).</summary>
    public required Func<Vector3, float> GroundAt { get; init; }

    private static float Smooth(float a, float b, float x) { float t = Mathf.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    private Patch? Take()
    {
        Patch? oldest = null;
        foreach (var p in _patches)
        {
            if (!p.Used) return p;
            if (oldest is null || p.Born < oldest.Born) oldest = p;
        }
        if (oldest is not null) { Release(oldest); return oldest; }
        return null;
    }

    private void Release(Patch p)
    {
        if (p.R is not null) _byRound.Remove(p.R);
        if (p.Scorch is not null) p.Scorch.Visible = false;
        if (p.Slot >= 0) { _embers[p.Slot].Emitting = false; _slotOwner[p.Slot] = null; p.Slot = -1; }
        p.Used = false; p.R = null; p.Dead = false;
    }

    /// <summary>A free emitter, else the oldest patch's: fresh drops matter most, the old ones' embers in the air carry on.</summary>
    private int FreeSlot()
    {
        int old = -1;
        for (int i = 0; i < Slots; i++)
        {
            if (_slotOwner[i] is not { } o) return i;
            if (old < 0 || o.Born < _slotOwner[old]!.Born) old = i;
        }
        _slotOwner[old]!.Slot = -1; _slotOwner[old] = null;
        return old;
    }

    private void HideAll()
    {
        if (!Visible) return;
        Visible = false;
        foreach (var p in _patches) if (p.Used) Release(p);
        _byRound.Clear();
        _mm.VisibleInstanceCount = 0;
    }

    /// <summary>One frame: the fire drops among the rounds (kind 0x11), in the order they were dropped.</summary>
    public void Frame(IReadOnlyList<Round> rounds, bool modern, Func<System.Numerics.Vector3, Vector3> toGodot, Vector3 cam)
    {
        if (!modern) { HideAll(); return; }
        Visible = true;
        double ticks = Time.GetTicksMsec() * 0.001;
        _t = ticks;
        _gen++;
        _last.Clear();
        for (int i = 0; i < rounds.Count; i++)
        {
            var r = rounds[i];
            if (r.Kind != 0x11) continue;
            _last.TryGetValue(r.Shooter, out var prev);
            if (!_byRound.TryGetValue(r, out var p))
            {
                p = Take();
                if (p is null) continue;
                p.Used = true; p.R = r; p.Dead = false; p.Born = ++_bornSeq; p.Pos = toGodot(r.Pos); p.Joined = false;
                p.Variant = (int)((uint)(r.Pos.X * 7 + r.Pos.Z * 13) & 0x7fffffff);
                if (prev is not null && _byRound.ContainsKey(prev) && (r.Pos - prev.Pos).Length() <= Reach && prev.Age - r.Age <= Within
                    && toGodot(prev.Pos) is var f && (f - p.Pos).Length() > 0.05f)
                { p.Joined = true; p.From = f; }
                Layout(p);
                p.Scorch ??= NewDecal();
                var d = p.Scorch;
                bool jr = p.Joined;
                d.TextureAlbedo = jr ? _ribbon : _puddle; d.TextureEmission = jr ? _glowRibbon : _glowPuddle;
                if (jr)
                {
                    var seg = p.Pos - p.From; seg.Y = 0;
                    float dist = seg.Length(); var dir = seg / dist;
                    d.Place(new Basis(dir.Cross(Vector3.Up), Vector3.Up, dir), (p.From + p.Pos) / 2, Size * 0.95f, dist + Size);
                }
                else
                {
                    d.Place(Basis.FromEuler(new Vector3(0, Hash(p.Variant, 9, 4) * Mathf.Tau, 0)), p.Pos, Size, Size);
                }
                d.Visible = true;
                int slot = FreeSlot();
                if (slot >= 0)
                {
                    p.Slot = slot; _slotOwner[slot] = p;
                    var c = jr ? (p.From + p.Pos) / 2 : p.Pos;
                    float half = jr ? MathF.Min((p.Pos - p.From).Length() * 0.5f, 6f) : 0f;
                    var ext = new Vector3(1.4f + half, 0.1f, 1.4f + half);
                    ((ParticleProcessMaterial)_embers[slot].ProcessMaterial).EmissionBoxExtents = ext;
                    _embers[slot].GlobalPosition = c + Vector3.Up * 0.2f;
                    _embers[slot].Emitting = true;
                }
                _byRound[r] = p;
            }
            _last[r.Shooter] = r;
            p.Gen = _gen;
            p.Level = Smooth(0f, 0.6f, r.Age) * Smooth(0f, BurnDown, r.Life);
        }
        // drops that left the sim: burning is over, the scorch stays and fades
        foreach (var kv in _byRound) if (kv.Value.Gen != _gen && !kv.Value.Dead) _gone.Add(kv.Key);
        foreach (var r in _gone) { var p = _byRound[r]; p.Dead = true; p.DeathT = _t; p.Level = 0; _byRound.Remove(r); p.R = null; }
        _gone.Clear();

        int n = 0;
        foreach (var p in _patches)
        {
            if (!p.Used) continue;
            float k = 1f;
            if (p.Dead)
            {
                double since = _t - p.DeathT;
                if (since > ScorchHold + ScorchFade) { Release(p); continue; }
                k = 1f - Smooth(ScorchHold, ScorchHold + ScorchFade, (float)since);
                p.Level = 0;
            }
            else k = Smooth(0f, 0.5f, p.R!.Age);
            if (p.Scorch is { } d)
            {
                d.Modulate = new Color(1, 1, 1, k);
                d.EmissionEnergy = p.Level * 2.2f * (0.85f + 0.15f * MathF.Sin((float)_t * 9f + p.Variant));
            }
            if (p.Slot >= 0)
            {
                if ((p.Dead || p.R!.Life < BurnDown) && p.Level < 0.04f) { _embers[p.Slot].Emitting = false; _slotOwner[p.Slot] = null; p.Slot = -1; }
                else
                {
                    _embers[p.Slot].AmountRatio = p.Level;
                }
            }
            if (p.Level > 0.02f)
                for (int j = 0; j < p.Count; j++)
                {
                    float sc = p.Sc[j] * (0.35f + 0.65f * p.Level);
                    var basis = Basis.FromScale(new Vector3(sc, sc, sc));
                    _mm.SetInstanceTransform(n, new Transform3D(basis, p.Pos + p.Off[j]));
                    _mm.SetInstanceCustomData(n, new Color(0, 0.12f + 0.2f * Hash(j, p.Variant, 2), 0, Hash(p.Variant, j, 7)));
                    n++;
                }
        }
        _mm.VisibleInstanceCount = n;

        // the three lights: the patches nearest the camera that burn hardest
        foreach (var p in _patches) p.Chosen = false;
        for (int l = 0; l < Lights; l++)
        {
            Patch? best = null; float bs = 0.04f;
            foreach (var p in _patches)
            {
                if (!p.Used || p.Chosen || p.Level <= 0.04f) continue;
                float sc = p.Level / (4f + (p.Pos - cam).LengthSquared() * 0.02f);
                if (sc > bs) { bs = sc; best = p; }
            }
            var li = _lights[l];
            if (best is null) { li.Visible = false; continue; }
            best.Chosen = true;
            float t = (float)_t + best.Variant;
            float flick = 0.72f + 0.12f * MathF.Sin(t * 13.1f) + 0.09f * MathF.Sin(t * 7.3f + 1.3f) + 0.07f * MathF.Sin(t * 23.7f + 4.1f);
            li.GlobalPosition = (best.Joined ? (best.From + best.Pos) / 2 : best.Pos) + Vector3.Up * 1.2f;
            li.LightEnergy = best.Level * 2.6f * flick;
            li.OmniRange = 6f + (best.Joined ? 4f : 0f);
            li.Visible = true;
        }
    }

    /// <summary>The tongues of a patch, fixed at birth: a few over its pool, and (joined) a row along the gap to the previous drop.</summary>
    private static void Layout(Patch p)
    {
        int c = 0;
        for (int j = 0; j < Tongues; j++)
        {
            float a = Hash(p.Variant, j, 11) * Mathf.Tau, r = 0.2f + 1.3f * MathF.Sqrt(Hash(p.Variant, j, 12));
            p.Off[c] = new Vector3(MathF.Cos(a) * r, 0.02f, MathF.Sin(a) * r);
            p.Sc[c++] = 0.75f + 0.6f * Hash(p.Variant, j, 13);
        }
        if (p.Joined)
        {
            var seg = p.Pos - p.From;
            float dist = seg.Length();
            int m = Math.Min(Along, (int)(dist / 1.0f));
            for (int j = 0; j < m; j++)
            {
                float u = (j + 0.5f) / m;
                var side = new Vector3(-seg.Z, 0, seg.X).Normalized() * ((Hash(p.Variant, j, 14) - 0.5f) * 1.8f);
                p.Off[c] = (p.From - p.Pos) * (1 - u) + side;
                p.Sc[c++] = 0.7f + 0.5f * Hash(p.Variant, j, 15);
            }
        }
        p.Count = c;
    }
}
