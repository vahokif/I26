using Godot;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's rocket exhaust: a continuous stream rather than a puff every few metres. Puffs are laid by
/// distance flown along the drawn (interpolated) path, not once a sim tick or a time step, so a missile at 150 m/s
/// leaves no gaps whatever the frame rate; they start small and swell into each other (overlap), and are soft against
/// the ground (the damage smoke's shader, light grey). Presentation only: nothing here touches the sim or its random
/// stream. One MultiMesh and one pool for every trail, never a node per puff, no allocation per frame; when the pool
/// fills up the spacing widens (so a melee full of missiles thins the trails instead of dropping the newest) and the
/// oldest puffs are recycled.
/// </summary>
public partial class RocketSmoke : Node3D
{
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public float SceneLight { get; set; } = 1f;
    /// <summary>The smoke's colour (a multiplier on the grey): white for rockets, darker for the Ball O'Flame's sooty tail.</summary>
    public System.Numerics.Vector3 Tint { get; init; } = System.Numerics.Vector3.One;

    private const int Cap = 2400;
    private const float Spacing = 0.4f, Life = 1.9f, Start = 0.5f, End = 2.2f;
    private const int MaxPerFrame = 160;   // a stalled frame does not flood the pool

    private struct Puff { public bool Live; public double X, Y, Z; public float Vx, Vy, Vz, Age, Life, End, Dens, Seed; }
    private struct Emitter { public System.Numerics.Vector3 Last; public float Carry; public bool Has; }
    private readonly Puff[] _pool = new Puff[Cap];
    private readonly Dictionary<object, Emitter> _emitters = [];
    private readonly Random _rng = new(1976);   // not the sim's CrtRand
    private int _next, _live;
    private MirrorDraw? _mirror;
    private MultiMesh _mm = null!;
    private ShaderMaterial _mat = null!;
    private DirectionalLight3D? _sun;
    private readonly float[] _buf = new float[Cap * 16];
    private readonly float[] _key = new float[Cap];
    private readonly int[] _idx = new int[Cap];
    private static readonly System.Numerics.Vector3 Wind = new(I76.Formats.Vegetation.Wind.Direction.X * I76.Formats.Vegetation.Wind.Breeze, 0f, I76.Formats.Vegetation.Wind.Direction.Y * I76.Formats.Vegetation.Wind.Breeze);

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/damage_smoke_modern.gdshader") };
        _mat.SetShaderParameter("noise_tex", DamageSmoke.Noise);
        _mat.SetShaderParameter("tint", new Vector3(Tint.X, Tint.Y, Tint.Z));
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _mat }, InstanceCount = Cap, VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, Name = "Exhaust" });
    }

    /// <summary>The rocket <paramref name="key"/> is at <paramref name="at"/> (game coordinates, where it is drawn) this frame: lay puffs along the way from where it was (<paramref name="scale"/> smaller for a thinner, closer trail, <paramref name="dens"/> its thickness).</summary>
    public void Emit(object key, System.Numerics.Vector3 at, System.Numerics.Vector3 vel, float scale = 1f, float dens = 0.42f)
    {
        _emitters.TryGetValue(key, out var e);
        if (!e.Has) { e.Has = true; e.Last = at; _emitters[key] = e; return; }
        var d = at - e.Last;
        float len = d.Length();
        float load = _live / (float)Cap;
        float spacing = Spacing * scale * (1f + 3f * MathF.Max(0f, load - 0.45f));
        e.Carry += len;
        int n = Math.Min((int)(e.Carry / spacing), MaxPerFrame);
        if (len > 1e-5f)
            for (int i = 1; i <= n; i++)
            {
                // the i-th puff sits spacing further along from where the last one was, newest nearest the rocket
                float t = (i * spacing - (e.Carry - len)) / len;
                Spawn(e.Last + d * Math.Clamp(t, 0f, 1f), vel, scale, dens, (n - i) * spacing / MathF.Max(vel.Length(), 1f));
            }
        e.Carry -= (int)(e.Carry / spacing) * spacing;
        e.Last = at;
        _emitters[key] = e;
    }

    /// <summary>The rocket is gone: its puffs stay to drift away.</summary>
    public void Release(object key) => _emitters.Remove(key);

    /// <summary>The look switched: every puff goes.</summary>
    public void Clear()
    {
        Array.Clear(_pool);
        _emitters.Clear();
        _live = 0;
        if (_mm is not null) _mm.VisibleInstanceCount = 0;
        SmokeLayer.Submit(this, _buf, 0, Tint);
    }

    private void Spawn(System.Numerics.Vector3 g, System.Numerics.Vector3 vel, float scale, float dens, float age)
    {
        float r1 = (float)_rng.NextDouble(), r2 = (float)_rng.NextDouble(), r3 = (float)_rng.NextDouble(), r4 = (float)_rng.NextDouble();
        ref var p = ref _pool[_next];
        _next = (_next + 1) % Cap;
        p = new Puff
        {
            Live = true, X = g.X + (r1 - 0.5f) * 0.12f, Y = g.Y + (r2 - 0.5f) * 0.12f, Z = g.Z + (r3 - 0.5f) * 0.12f,
            // a little of the rocket's wake, then the breeze; it rises as it cools
            Vx = vel.X * 0.02f + (r4 - 0.5f) * 0.5f + Wind.X * 0.6f, Vy = 0.35f + 0.5f * r2, Vz = vel.Z * 0.02f + (r1 - 0.5f) * 0.5f + Wind.Z * 0.6f,
            Age = age, Life = Life * (0.85f + 0.3f * r3), End = End * scale * (0.8f + 0.4f * r4), Dens = dens, Seed = r1,
        };
    }

    public override void _Process(double delta)
    {
        float dt = (float)Math.Min(delta, 0.25);
        _mat.SetShaderParameter("scene_light", SceneLight);
        var cam = GetViewport().GetCamera3D();
        int live = 0;
        for (int i = 0; i < Cap; i++)
        {
            ref var p = ref _pool[i];
            if (!p.Live) continue;
            if ((p.Age += dt) >= p.Life) { p.Live = false; continue; }
            p.X += dt * p.Vx; p.Y += dt * p.Vy; p.Z += dt * p.Vz;
            live++;
        }
        _live = live;
        if (cam is null || live == 0) { _mm.VisibleInstanceCount = 0; SmokeLayer.Submit(this, _buf, 0, Tint); return; }
        var ct = cam.GlobalTransform;
        var org = Origin();
        _sun ??= GetTree().Root.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>().FirstOrDefault();
        var toSun = _sun is null ? new Vector3(0.3f, 0.8f, 0.5f) : _sun.GlobalTransform.Basis.Z;
        _mat.SetShaderParameter("sun_view", ct.Basis.Inverse() * toSun);
        int cnt = 0;
        for (int i = 0; i < Cap; i++)
        {
            ref var p = ref _pool[i];
            if (!p.Live) continue;
            var gp = new Vector3((float)(p.X - org.X), (float)(p.Y - org.Y), -(float)(p.Z - org.Z));
            float depth = -ct.Basis.Z.Dot(gp - ct.Origin);
            if (depth < cam.Near || depth > 150) continue;
            _key[cnt] = -depth; _idx[cnt++] = i;   // far to near
        }
        Array.Sort(_key, _idx, 0, cnt);
        var buf = _buf;
        for (int j = 0; j < cnt; j++)
        {
            ref var p = ref _pool[_idx[j]];
            float lf = p.Age / p.Life, e = 1f - (1f - lf) * (1f - lf);
            float size = Mathf.Lerp(Start, p.End, e);
            int o = j * 16;
            buf[o] = size; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = (float)(p.X - org.X);
            buf[o + 4] = 0; buf[o + 5] = size; buf[o + 6] = 0; buf[o + 7] = (float)(p.Y - org.Y);
            buf[o + 8] = 0; buf[o + 9] = 0; buf[o + 10] = size; buf[o + 11] = -(float)(p.Z - org.Z);
            buf[o + 12] = lf; buf[o + 13] = p.Dens; buf[o + 14] = 0.08f; buf[o + 15] = p.Seed;
        }
        if (SmokeLayer.Active)
        {
            // the smoke layer draws them, capped (SmokeLayer.cs)
            SmokeLayer.SceneLight = SceneLight;
            SmokeLayer.Submit(this, buf, cnt, Tint);
            _mirror ??= new MirrorDraw(this, Cap, Tint);
            _mirror.Update(buf, cnt, SceneLight);
            _mm.VisibleInstanceCount = 0;
            return;
        }
        SmokeLayer.Thin(buf, cnt, cam);
        SmokeLayer.Lights(_mat);
        _mm.Buffer = buf;
        _mm.VisibleInstanceCount = cnt;
    }

    public override void _ExitTree() => SmokeLayer.Release(this);
}
