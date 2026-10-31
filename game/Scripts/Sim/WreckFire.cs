using Godot;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's burning wreck (not in the original, whose dead car stops
/// smoking: its damage-smoke emitter is removed, and
/// none is started; docs/deviations.md). On the burnt hull: flames, the fire
/// glowing through cracks in the charred body (object.gdshaderinc's
/// <c>wreck</c>), black smoke, rising embers and a flickering warm light.
/// Over tens of seconds the flames die down and the body cools through dull
/// red to plain char, leaving a smouldering grey wisp that thins away after a
/// few minutes. Purely visual, nothing feeds back into the sim. In the 1997
/// look it is all hidden and the shaders ignore the char.
///
/// It follows the car rather than hanging off it, so the smoke rises straight
/// up from a wreck lying on its roof, and when the wreck leaves the world (a
/// melee clears it after 10 s) the smoke already in the air drifts away.
/// </summary>
public partial class WreckFire : Node3D
{
    /// <summary>I76_WRECK_FIRE=0 leaves it out (the wreck as before, to compare and to profile); =heat:H holds the heat at H (0..1), for captures.</summary>
    public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("I76_WRECK_FIRE") != "0";
    private static readonly float? HeldHeat = System.Environment.GetEnvironmentVariable("I76_WRECK_FIRE") is { } v && v.StartsWith("heat:")
        && float.TryParse(v[5..], System.Globalization.CultureInfo.InvariantCulture, out float h) ? h : null;

    public required CarNode Car { get; init; }
    /// <summary>The mission's light for the smoke: 1 by day, lower at night (as ModernFx's).</summary>
    public float SceneLight { get; init; } = 1f;

    // the burn-down, in seconds since the death
    public const float FlamesFull = 10, FlamesOut = 40, HeatOut = 75, SmokeThin = 45, SmokeGone = 240;

    /// <summary>The flames' strength at an age: full, then dying down to nothing.</summary>
    public static float Flames(double age) => 1f - Smooth(FlamesFull, FlamesOut, (float)age);
    /// <summary>The body's heat: white-hot cracks at first, cooling through orange and dull red to none.</summary>
    public static float Heat(double age) { float k = 1f - Smooth(4, HeatOut, (float)age); return k * k * (3 - 2 * k) * 0.35f + k * 0.65f; }
    /// <summary>The smoke: thick while it burns, a thin smoulder after, gone at the end.</summary>
    public static float Smoke(double age) => Mathf.Lerp(1f, 0.35f, Smooth(FlamesFull, SmokeThin, (float)age)) * (1f - Smooth(SmokeGone - 90, SmokeGone, (float)age));

    private static float Smooth(float a, float b, float x) { float t = Mathf.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    private GpuParticles3D _flames = null!, _smoke = null!, _embers = null!;
    private OmniLight3D _light = null!;
    private double _age, _gone = -1;
    private float _phase;
    private Vector3 _centre;
    private float _width, _length;
    private bool _wasModern = true;

    /// <summary>The car is renewed (DamageView.Restore): it is no longer touched, even by a frame still to run before the free.</summary>
    public void Detach()
    {
        _detached = true;
        Visible = false;
        SetProcess(false);
    }

    private bool _detached;

    /// <summary>Done: the car went and the smoke with it, or the smoke has thinned away; free to go.</summary>
    public bool Finished { get; private set; }

    public override void _Ready()
    {
        Shared.Init();
        _phase = GD.Randf() * 100f;
        var b = Car.BodyBounds;
        _centre = b.GetCenter();
        _width = Mathf.Clamp(b.Size.X, 1f, 4f);
        _length = Mathf.Clamp(b.Size.Z, 2f, 12f);
        var box = new Vector3(_width * 0.32f, 0.12f, _length * 0.32f);
        float topY = b.Size.Y * 0.15f;
        _flames = Emitter(Shared.FlameProc, Shared.FlameMesh, 30, 0.85, box, topY);
        _smoke = Emitter(Shared.SmokeProc, Shared.SmokeMesh, 36, 8.0, box * 0.8f, topY + 0.5f);
        _embers = Emitter(Shared.EmberProc, Shared.EmberMesh, 14, 1.8, box, topY + 0.2f);
        _smoke.SetInstanceShaderParameter("scene_light", SceneLight);
        _light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.52f, 0.2f), OmniRange = 9f, OmniAttenuation = 1.4f, ShadowEnabled = false,
            Position = new Vector3(0, b.Size.Y * 0.5f + 0.6f, 0), Name = "Glow",
        };
        AddChild(_light);
        Follow();
        Apply();
    }

    private GpuParticles3D Emitter(ParticleProcessMaterial proc, Mesh mesh, int amount, double life, Vector3 box, float y)
    {
        var p = new GpuParticles3D
        {
            ProcessMaterial = proc, DrawPass1 = mesh, Amount = amount, Lifetime = life, LocalCoords = false,
            Position = new Vector3(0, y, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-12, -3, -12), new Vector3(24, 34, 24)), Emitting = true,
        };
        // each emitter its own box: a shared process material with the car's size set per instance would need one each
        p.ProcessMaterial = (ParticleProcessMaterial)proc.Duplicate();
        ((ParticleProcessMaterial)p.ProcessMaterial).EmissionBoxExtents = box;
        AddChild(p);
        return p;
    }

    /// <summary>On the wreck's middle, turned with its heading only: the fire burns up whichever way up the car lies.</summary>
    private void Follow()
    {
        if (!IsInstanceValid(Car) || !Car.IsInsideTree()) return;
        var t = Car.GlobalTransform;
        var fwd = t.Basis.Z with { Y = 0 };
        float yaw = fwd.LengthSquared() > 1e-4f ? Mathf.Atan2(fwd.X, fwd.Z) : 0f;
        GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), t * _centre);
    }

    public override void _Process(double delta)
    {
        using var _view = Hitch.Time(Hitch.Phase.Views);
        _age += delta;
        bool carGone = !IsInstanceValid(Car) || !Car.IsInsideTree();
        if (carGone && _gone < 0) _gone = _age;
        if (!carGone) Follow();
        Apply(delta);
    }

    private void Apply(double delta = 0)
    {
        bool modern = !IsInstanceValid(Car) || Car.Modern;
        bool carGone = _gone >= 0;
        float flames = carGone ? 0 : Flames(_age), heat = carGone ? 0 : HeldHeat ?? Heat(_age), smoke = carGone ? 0 : Smoke(_age);
        if (_detached) return;
        if (!carGone) Car.SetBurn(1f, heat);
        if (modern != _wasModern)
        {
            _wasModern = modern;
            Visible = modern;
        }
        _flames.Emitting = modern && flames > 0.02f;
        _flames.AmountRatio = Mathf.Max(flames, 0.2f);
        _flames.SetInstanceShaderParameter("level", flames);
        _embers.Emitting = modern && flames > 0.15f;
        _embers.AmountRatio = flames;
        // the smoke layer draws the smoke capped (DamageSmoke.Burn, black puffs of its own look) when it can; the particles when not
        bool layered = modern && smoke > 0.02f && DamageSmoke.Current?.Burn(this, GlobalPosition + Vector3.Up * (Car.BodyBounds.Size.Y * 0.15f + 0.5f), Mathf.Clamp(smoke, 0.25f, 1f), delta, MathF.Min(_width, _length) * 0.5f) == true;
        if (!layered) DamageSmoke.Current?.Extinguish(this);
        _smoke.Emitting = modern && smoke > 0.02f && !layered;
        _smoke.AmountRatio = Mathf.Clamp(smoke, 0.25f, 1f);
        _smoke.SetInstanceShaderParameter("thick", Mathf.Clamp((smoke - 0.35f) / 0.65f, 0, 1));
        _smoke.SetInstanceShaderParameter("fire", flames);
        // a fire's flicker: three unrelated sines, never quite repeating; dimmer, steadier embers after the flames
        float t = (float)_age * 1f + _phase;
        float flick = 0.72f + 0.12f * Mathf.Sin(t * 13.1f) + 0.09f * Mathf.Sin(t * 7.3f + 1.3f) + 0.07f * Mathf.Sin(t * 23.7f + 4.1f);
        _light.LightEnergy = flames * 3.2f * flick + heat * 0.7f * (0.9f + 0.1f * Mathf.Sin(t * 2.3f));
        _light.OmniRange = 6f + 5f * flames;
        _light.Visible = modern && _light.LightEnergy > 0.02f;
        // the smoke in the air has drifted off: done
        if (carGone && _age - _gone > _smoke.Lifetime + 0.5 || !carGone && _age > SmokeGone + _smoke.Lifetime && heat <= 0) Finished = true;
    }

    /// <summary>The process materials and meshes every wreck shares (the shaders compile once).</summary>
    internal static class Shared
    {
        public static ParticleProcessMaterial FlameProc = null!, SmokeProc = null!, EmberProc = null!;
        public static Mesh FlameMesh = null!, SmokeMesh = null!, EmberMesh = null!;
        private static bool _done;

        public static void Init()
        {
            if (_done) return;
            _done = true;
            // flames: tongues licking up off the hull, buoyant, quick
            FlameProc = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = Vector3.One,
                Direction = Vector3.Up, Spread = 10, InitialVelocityMin = 0.6f, InitialVelocityMax = 1.6f,
                Gravity = new Vector3(0, 2.2f, 0), DampingMin = 0.5f, DampingMax = 1f,
                ScaleMin = 0.8f, ScaleMax = 1.5f, ScaleCurve = Curve1((0, 0.55f), (0.35f, 1f), (1, 0.6f)), LifetimeRandomness = 0.35f,
            };
            var flame = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/wreck_flame.gdshader") };
            FlameMesh = new QuadMesh { Size = new Vector2(1.0f, 1.7f), CenterOffset = new Vector3(0, 0.75f, 0), Material = flame };

            // smoke: rising, spreading, leaning a little downwind
            SmokeProc = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = Vector3.One,
                Direction = Vector3.Up, Spread = 12, InitialVelocityMin = 1.6f, InitialVelocityMax = 2.6f,
                Gravity = new Vector3(0.35f, 0.25f, 0.15f), DampingMin = 0.15f, DampingMax = 0.35f,
                AngleMin = -180, AngleMax = 180, AngularVelocityMin = -14, AngularVelocityMax = 14,
                ScaleMin = 1.2f, ScaleMax = 1.9f, ScaleCurve = Curve1((0, 0.55f), (0.4f, 2.1f), (1, 4.2f)), LifetimeRandomness = 0.25f,
            };
            var smoke = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/wreck_smoke.gdshader") };
            if (ModernFx.LoadVfx("WispySmoke01_8x8.tga") is { } wisp)
            {
                smoke.SetShaderParameter("atlas", wisp);
                smoke.SetShaderParameter("has_atlas", true);
            }
            SmokeMesh = new QuadMesh { Size = new Vector2(1.4f, 1.4f), Material = smoke };

            // embers: sparks drifting up out of the fire, swirling, burning out
            EmberProc = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = Vector3.One,
                Direction = Vector3.Up, Spread = 30, InitialVelocityMin = 1.5f, InitialVelocityMax = 3.5f,
                Gravity = new Vector3(0, 1.2f, 0), DampingMin = 0.8f, DampingMax = 1.5f,
                TurbulenceEnabled = false, LifetimeRandomness = 0.5f,
                ColorRamp = new GradientTexture1D { Gradient = new Gradient
                {
                    Offsets = [0f, 0.4f, 1f],
                    Colors = [new Color(1f, 0.75f, 0.35f, 1), new Color(1f, 0.4f, 0.08f, 1), new Color(0.6f, 0.1f, 0.02f, 0)],
                } },
            };
            EmberMesh = new QuadMesh
            {
                Size = new Vector2(0.06f, 0.06f),
                Material = new ShaderMaterial
                {
                    Shader = new Shader
                    {
                        Code = """
                            shader_type spatial;
                            #include "res://Shaders/cab_clip.gdshaderinc"
                            render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
                            varying float v_seed;
                            void vertex() {
                                v_seed = float(INSTANCE_ID) * 2.4;
                                float sc = length(MODEL_MATRIX[0].xyz);
                                MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
                            }
                            void fragment() {
                                CAB_CLIP
                                float r = 1.0 - smoothstep(0.2, 1.0, length(UV * 2.0 - 1.0));
                                float f = 0.6 + 0.4 * sin(TIME * 23.0 + v_seed);
                                ALBEDO = COLOR.rgb * 6.0 * COLOR.a * r * f;
                            }
                            """,
                    },
                },
            };
        }

        private static CurveTexture Curve1(params (float X, float Y)[] pts)
        {
            var c = new Curve { MaxValue = pts.Max(p => p.Y) };
            foreach (var (x, y) in pts) c.AddPoint(new Vector2(x, y));
            return new CurveTexture { Curve = c };
        }
    }
}
