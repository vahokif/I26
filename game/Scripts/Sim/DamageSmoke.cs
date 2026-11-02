using Godot;
using I76.Formats.Assembly;
using I76.Sim.Damage;

namespace I76.Game.Sim;

/// <summary>
/// The original's damage smoke: a living car whose
/// health bar (the gauge, the health percentage, so a scratched engine, suspension or brake counts) is below 0.75 trails puffs from behind its body, white above 0.6, grey 0.4..0.6, black at 0.4 or less;
/// it stops at the car's death and never restarts (the burning wreck is WreckFire's, modern look only).
///
/// Purely visual: it reads the vehicle's health and motion and draws; nothing goes back into the sim, and its
/// jitter has its own System.Random so it never consumes the sim's CrtRand stream. It reads the Vehicle, so a
/// network client (whose mirror vehicles carry snapshot health, speed and velocity) sees it as a local game does.
///
/// 1997: 2 puffs per sim tick per car, 20 ticks' life, the frame table, a 0.8 m quad at 0.6 alpha, per tick
/// as the original (so its rate follows the frame rate). Modern: the same sprites at the 30 fps rate (60 puffs a
/// second a car), 0.67 s life whatever the frame rate, softened and lit by the scene. One MultiMesh for all cars.
/// </summary>
public partial class DamageSmoke : Node3D
{
    public required Dictionary<I76.Sim.Vehicles.Vehicle, CarNode> Cars { get; init; }
    public required World.MissionBuilder Builder { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public float SceneLight { get; set; } = 1f;

    /// <summary>I76_SMOKE=h holds every living car's health at h (0..1), for captures; I76_SMOKE=0 leaves the smoke out.</summary>
    private static readonly float? Held = float.TryParse(System.Environment.GetEnvironmentVariable("I76_SMOKE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h) ? h : null;

    private const int Cap1997 = 256, CapModern = 1024, Life1997 = 20;
    private const double ModernLife = 20.0 / 30, ModernRate = 60;
    private static readonly int[][] FrameTable =
    [
        [0, 1, 2, 3, 2, 2, 3, 3, 4, 5, 6, 7, 4, 5, 6, 7, 8, 9, 10, 11],
        [0, 0, 1, 1, 2, 2, 3, 3, 4, 5, 6, 7, 4, 5, 6, 7, 8, 9, 10, 11],
    ];
    private static readonly string[] Kinds = ["xwp1", "xsg1", "xbp1"];

    private struct Puff { public bool Live; public double X, Y, Z; public float Vx, Vy, Vz, Dx, Dy, Dz, S, Target, Age; public int Kind; }
    private readonly Puff[] _pool = new Puff[CapModern];
    private int _next;
    private readonly Random _rng = new(7676);   // not the sim's CrtRand
    private readonly float[] _noise = new float[256];
    private int _noiseAt;
    private readonly Dictionary<string, float> _minZ = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CarNode, double> _carry = [];
    private readonly Dictionary<CarNode, int> _kindOf = [];
    private MultiMesh _mm = null!;
    private ShaderMaterial _mat = null!;
    private bool _have1997, _ready, _modern = true;
    private int _lastTick = SimClock.Now;

    // modern look: procedural billowing smoke, the wreck's family (damage_smoke_modern.gdshader); its own pool and MultiMesh
    private static readonly float RateMul = 0.2f * (float.TryParse(System.Environment.GetEnvironmentVariable("I76_SMOKE_RATE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rs) ? rs : 1f);   // of the original rate; I76_SMOKE_RATE=k scales it (captures of a thick plume)
    private const float SizeBoost = 1.45f;
    private const int CapM = 3072;
    private const float StreamSpacing = 0.32f;   // metres driven between puffs (0.18 at black); widens as the pool fills
    private readonly Dictionary<CarNode, System.Numerics.Vector3> _lastAt = [];
    private int _liveM;
    // game axes (x, up, z), m/s: light and the same everywhere, the wind the plants sway in and the tumbleweeds roll before
    private static readonly Vector3 Wind = new(I76.Formats.Vegetation.Wind.Direction.X * I76.Formats.Vegetation.Wind.Breeze, 0f, I76.Formats.Vegetation.Wind.Direction.Y * I76.Formats.Vegetation.Wind.Breeze);
    private struct MPuff { public bool Live; public double X, Y, Z; public float Vx, Vy, Vz, Age, Life, End, Dens, Dark, Seed, Lift; }
    private readonly MPuff[] _mp = new MPuff[CapM];
    private int _mnext;
    private readonly Dictionary<CarNode, (float Level, double Carry)> _lvl = [];
    // The billows go out in depth slabs, one MultiMeshInstance3D each. Godot sorts transparent draws (the lamps' glare, the
    // tyre smoke) by instance, not by puff, and one instance for all the smoke sorted as a block: a flare behind the smoke was
    // drawn over it, one in front under it. Each slab's custom AABB is a small box at its mean puff, so it sorts at its own depth.
    private static readonly float[] SlabFar = [6f, 12f, 20f, 34f, 60f, float.MaxValue];   // metres from the camera: each slab's far edge, the nearest first
    private MirrorDraw? _mirror;
    private readonly float[] _layerBuf = new float[CapM * 16];
    private readonly MultiMesh[] _slabMm = new MultiMesh[SlabFar.Length];
    private readonly float[][] _slabBuf = new float[SlabFar.Length][];
    private ShaderMaterial _matM = null!;
    private DirectionalLight3D? _sun;
    private readonly float[] _keyM = new float[CapM];
    private readonly int[] _idxM = new int[CapM];
    private readonly List<(float Depth, int I)> _order = [];

    public override void _Ready()
    {
        Current = this;
        // 256 first differences of a periodic midpoint-displacement curve clamped to +-1, as steps of a few hundredths
        var c = new float[256];
        double amp = 1;
        for (int step = 128; step >= 1; step /= 2, amp *= 0.6)
            for (int i = 0; i < 256; i += step * 2)
                c[(i + step) % 256] = Math.Clamp((c[i] + c[(i + step * 2) % 256]) / 2 + (float)((_rng.NextDouble() - 0.5) * amp), -1f, 1f);
        float mx = 1e-6f;
        for (int i = 0; i < 256; i++) { _noise[i] = c[(i + 1) % 256] - c[i]; mx = MathF.Max(mx, MathF.Abs(_noise[i])); }
        for (int i = 0; i < 256; i++) _noise[i] *= 0.05f / mx;

        var atlas = BuildAtlas(out var cell);
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/damage_smoke.gdshader") };
        if (atlas is not null)
        {
            _mat.SetShaderParameter("atlas_near", atlas);
            _mat.SetShaderParameter("atlas_lin", atlas);
            _mat.SetShaderParameter("texel", new Vector2(cell.X, cell.Y));
            _have1997 = true;
        }
        _ready = Held != 0;
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _mat }, InstanceCount = CapModern, VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, Name = "Puffs" });
        _matM = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/damage_smoke_modern.gdshader") };
        _matM.SetShaderParameter("noise_tex", Noise);
        _matM.SetShaderParameter("a_cap", 0.3f);   // without the smoke layer (SmokeLayer.Thin divides the density down where puffs pile up)
        var billow = new QuadMesh { Size = Vector2.One, Material = _matM };
        for (int k = 0; k < SlabFar.Length; k++)
        {
            _slabBuf[k] = new float[CapM * 16];
            _slabMm[k] = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
                Mesh = billow, InstanceCount = CapM, VisibleInstanceCount = 0,
            };
            AddChild(new MultiMeshInstance3D { Multimesh = _slabMm[k], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, Name = $"Billows{k}" });
        }
    }

    /// <summary>A small tiling fbm texture (R: the shader's coarse 3-octave fbm, G: the same from other cells), baked once so the puffs (and StaticFire's flames) sample it instead of hashing per pixel.</summary>
    internal static ImageTexture Noise => _noiseTex ??= BuildNoise();
    private static ImageTexture? _noiseTex;
    private static ImageTexture BuildNoise()
    {
        const int N = 256, Cells = 8;
        var rng = new Random(1976);
        var lat = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            lat[c] = new float[3 * 32 * 32];   // lattices of 8, 16, 32 cells (period N)
            for (int i = 0; i < lat[c].Length; i++) lat[c][i] = (float)rng.NextDouble();
        }
        float Oct(int ch, int o, float u, float v)
        {
            int cells = Cells << o;
            float x = u * cells, y = v * cells;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float L(int xx, int yy) => lat[ch][o * 1024 + (yy % cells) * 32 + (xx % cells)];
            int x1 = x0 + 1, y1 = y0 + 1;
            return Mathf.Lerp(Mathf.Lerp(L(x0, y0), L(x1, y0), fx), Mathf.Lerp(L(x0, y1), L(x1, y1), fx), fy);
        }
        var data = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N, v = (y + 0.5f) / N;
                for (int c = 0; c < 2; c++)
                    data[(y * N + x) * 4 + c] = (byte)Math.Clamp((Oct(c, 0, u, v) * 0.5f + Oct(c, 1, u, v) * 0.3f + Oct(c, 2, u, v) * 0.2f) * 255f + 0.5f, 0f, 255f);
                data[(y * N + x) * 4 + 3] = 255;
            }
        return ImageTexture.CreateFromImage(Image.CreateFromData(N, N, false, Image.Format.Rgba8, data));
    }

    private ImageTexture? BuildAtlas(out Vector2I cell)
    {
        cell = default;
        if (Builder.Resolver is not { } res || Builder.Textures is not { } tf) return null;
        Image? img = null;
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < 12; i++)
            {
                string n = $"{Kinds[k]}_{i / 4 + 1}0{i % 4 + 1}";
                if (res.FindByStem(n) is not { } m || tf.GetRgba(m) is not { } t) { GD.Print($"damage smoke: texture {n} not found"); return null; }
                if (img is null) { cell = new Vector2I(t.Width, t.Height); img = Image.CreateEmpty(cell.X * 12, cell.Y * 3, false, Image.Format.Rgba8); }
                if (t.Width != cell.X || t.Height != cell.Y) { GD.Print($"damage smoke: {n} is {t.Width}x{t.Height}, not {cell.X}x{cell.Y}"); return null; }
                img.BlitRect(Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba), new Rect2I(0, 0, t.Width, t.Height), new Vector2I(i * cell.X, k * cell.Y));
            }
        if (img is null) return null;
        GD.Print($"damage smoke: atlas {img.GetWidth()}x{img.GetHeight()}");
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The BDYB node's bounding box min z, the back end of the rear body section (the emitter's offset, raw in the car's frame).</summary>
    private float RearZ(CarNode car)
    {
        if (_minZ.TryGetValue(car.Definition, out var z)) return z;
        z = -2f;
        if (Builder.Assembler?.Assemble(car.Definition) is { } m && m.Parts.FirstOrDefault(p => p.NodeName.EndsWith("BDYB", StringComparison.OrdinalIgnoreCase)) is { } p && p.Mesh.Vertices.Length > 0)
            z = p.Mesh.Vertices.Min(v => v.Z);
        return _minZ[car.Definition] = z;
    }

    private readonly Dictionary<string, (float X, float Y, float Z)> _engine = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The modern look's emitter in the car's raw frame: the front body section's (BDYF) top at its z centre, else the whole body's centre.</summary>
    private (float X, float Y, float Z) EnginePoint(CarNode car)
    {
        if (_engine.TryGetValue(car.Definition, out var e)) return e;
        e = (0f, 1.1f, 0.8f);
        if (Builder.Assembler?.Assemble(car.Definition) is { } m)
        {
            var part = m.Parts.FirstOrDefault(p => p.NodeName.EndsWith("BDYF", StringComparison.OrdinalIgnoreCase) && p.Mesh.Vertices.Length > 0);
            var src = part is null ? m.Parts.Where(p => p.NodeName.Contains("BDY", StringComparison.OrdinalIgnoreCase) && p.Mesh.Vertices.Length > 0).ToArray() : [part];
            if (src.Length == 0) src = m.Parts.Where(p => p.Mesh.Vertices.Length > 0).ToArray();
            float zmin = float.MaxValue, zmax = float.MinValue, ymin = float.MaxValue, ymax = float.MinValue;
            foreach (var p in src) foreach (var v0 in p.Mesh.Vertices) { var v = p.ToModel.Apply(v0); zmin = Math.Min(zmin, v.Z); zmax = Math.Max(zmax, v.Z); ymin = Math.Min(ymin, v.Y); ymax = Math.Max(ymax, v.Y); }
            if (zmin < zmax)
            {
                if (part is null) e = (0f, (ymin + ymax) / 2, (zmin + zmax) / 2);
                else
                {
                    // the hood: the highest the section reaches over its front third (a cab's roof, if BDYF has one, is further back)
                    float zf = zmin + 0.65f * (zmax - zmin), hy = ymin;
                    foreach (var v0 in part.Mesh.Vertices) { var v = part.ToModel.Apply(v0); if (v.Z >= zf) hy = Math.Max(hy, v.Y); }
                    e = (0f, hy, (zmin + zmax) / 2 + 0.15f * (zmax - zmin));
                }
            }
            }
        return _engine[car.Definition] = e;
    }

    private static int KindFor(float h) => VehicleDamage.SmokeStage(h);

    public override void _Process(double delta)
    {
        using var _view = Hitch.Time(Hitch.Phase.Views);
        if (!_ready) return;
        bool modern = Cars.Values.FirstOrDefault()?.Modern ?? true;
        if (modern != _modern) { _modern = modern; Array.Clear(_pool); Array.Clear(_mp); _carry.Clear(); _lvl.Clear(); _lastAt.Clear(); }
        if (modern) { _mm.VisibleInstanceCount = 0; ProcessModern(delta); return; }
        SmokeLayer.Submit(this, _layerBuf, 0, System.Numerics.Vector3.One);
        foreach (var f in _fires) f?.Set(false, false);   // the 1997 look: the original's own flame planes
        HideBillows();
        if (!_have1997) return;
        _mat.SetShaderParameter("modern", modern);
        _mat.SetShaderParameter("scene_light", SceneLight);
        int cap = modern ? CapModern : Cap1997;
        // the original ran this once a frame, and its frame was the sim step: so once a sim tick, with the tick as dt
        int steps = SimClock.Take(ref _lastTick);
        double dt = SimClock.Tick;
        float life = modern ? (float)ModernLife : Life1997;
        for (int step = 0; step < steps; step++)
        {
            foreach (var (v, car) in Cars)
            {
                if (!IsInstanceValid(car) || !car.IsInsideTree() || !car.IsVisibleInTree()) continue;
                if (Combat_IsDead(v) || (Held ?? v.Health?.GaugeFraction() ?? 1f) is var h && h >= 0.75f) { _kindOf.Remove(car); continue; }
                int kind = KindFor(h);
                if (!_kindOf.TryGetValue(car, out int was) || was != kind) { _kindOf[car] = kind; GD.Print($"damage smoke: {car.Name} at {car.GamePosition} health {h:0.00}, kind {kind} ({Kinds[kind]})"); }
                int n;
                if (modern) { double c = _carry.GetValueOrDefault(car) + dt * ModernRate; n = (int)c; _carry[car] = c - n; }
                else n = 2;
                for (int i = 0; i < n; i++) Spawn(car, v, kind, cap);
            }

            // move and age every puff: per frame in 1997 (the original's per-frame dt), per second in the modern look
            float ageStep = modern ? (float)dt : 1f;
            for (int i = 0; i < cap; i++)
            {
                ref var p = ref _pool[i];
                if (!p.Live) continue;
                if (p.Age >= life) { p.Live = false; continue; }
                p.X += dt * p.Vx; p.Y += dt * p.Vy; p.Z += dt * p.Vz;
                float k = 2f * (float)dt, s = p.S;
                float s2 = k >= 0 && k <= 0.95f && MathF.Abs(p.Target - s) >= 0.005f ? s + k * (p.Target - s) : p.Target;
                float n = _noise[++_noiseAt & 0xff] * (modern ? (float)dt * 30f : 1f);
                p.Vx += p.Dx * (s2 - s) + n; p.Vy += p.Dy * (s2 - s) + n; p.Vz += p.Dz * (s2 - s) + n;
                p.S = s2;
                p.Age += ageStep;
            }
        }
        Draw(modern, cap, life);
    }

    private void ProcessModern(double delta)
    {
        double dt = Math.Min(delta, 0.25);
        _matM.SetShaderParameter("scene_light", SceneLight);
        foreach (var (v, car) in Cars)
        {
            if (!IsInstanceValid(car) || !car.IsInsideTree() || !car.IsVisibleInTree()) continue;
            if (Combat_IsDead(v) || (Held ?? v.Health?.GaugeFraction() ?? 1f) is var h && h >= 0.75f) { _kindOf.Remove(car); _lvl.Remove(car); _lastAt.Remove(car); continue; }
            int kind = KindFor(h);
            if (!_kindOf.TryGetValue(car, out int was) || was != kind) { _kindOf[car] = kind; GD.Print($"damage smoke: {car.Name} at {car.GamePosition} health {h:0.00}, kind {kind} ({Kinds[kind]})"); }
            var st = _lvl.TryGetValue(car, out var q) ? q : (kind, 0.0);
            st.Level = Mathf.MoveToward(st.Level, kind, (float)dt);   // a band to the next in about a second
            float L = st.Level;
            float rate = (L < 1 ? Mathf.Lerp(16f, 30f, L) : Mathf.Lerp(30f, 70f, L - 1)) * RateMul;   // half the puffs, 1.3x the size, denser: the same look for half the fill
            // the stream comes in clumps, not a band: the rate swells and ebbs, more deeply for black
            double tt = Time.GetTicksMsec() / 1000.0 + car.GetInstanceId() % 97;
            float clump = 1f + Mathf.Clamp(L - 0.5f, 0f, 1f) * 0.9f * (float)(Math.Sin(tt * 2.9) * Math.Sin(tt * 1.3 + 1));
            // a moving car lays its puffs by distance driven (the drawn path), so the trail is a stream at any speed and
            // frame rate; the time rate keeps a parked car smoking
            var here = Coords.ToGame(car.GlobalPosition) + Origin();
            var was0 = _lastAt.TryGetValue(car, out var w0) ? w0 : here;
            float driven = (here - was0).Length();
            _lastAt[car] = here;
            float spacing = Mathf.Lerp(StreamSpacing, StreamSpacing * 0.55f, Mathf.Clamp(L / 2f, 0f, 1f)) * (1f + 3f * Math.Max(0f, _liveM / (float)CapM - 0.4f));
            st.Carry += Math.Max(dt * rate * Math.Max(clump, 0.05f), Math.Min(driven, 60f) / spacing * Math.Max(clump, 0.7f));
            int n = Math.Min((int)st.Carry, 12); st.Carry -= (int)st.Carry;
            _lvl[car] = st;
            // the puffs are spread along the path drawn since the last frame, oldest furthest back
            for (int i = 0; i < n; i++)
            {
                float f = (i + 0.5f) / n;
                SpawnModern(car, v, L, driven > 0.01f ? 0f : (float)(dt * f), (was0 - here) * (1f - f));
            }
        }
        StaticSmokers(dt);
        int liveM = 0;
        for (int i = 0; i < CapM; i++)
        {
            ref var p = ref _mp[i];
            if (!p.Live) continue;
            if ((p.Age += (float)dt) >= p.Life) { p.Live = false; continue; }
            liveM++;
            float lf = p.Age / p.Life;
            // rises and slows, and the car's push bleeds off into the wind: the trail streams back
            float up = 2.0f * p.Lift * (1f - lf) * (1f - lf);
            p.Vy += (up - p.Vy) * Math.Min(1f, 1.2f * (float)dt);
            float k = Math.Min(1f, 0.9f * (float)dt);
            p.Vx += (Wind.X - p.Vx) * k; p.Vz += (Wind.Z - p.Vz) * k;
            p.X += dt * p.Vx; p.Y += dt * p.Vy; p.Z += dt * p.Vz;
        }
        _liveM = liveM;
        DrawModern();
    }

    private readonly Dictionary<int, double> _staticCarry = [];
    private StaticFire?[] _fires = [];
    private const float FireReach = StaticFire.Reach, SmokeReach = 250f;
    private const int FireLights = 3;
    private const float EmberReach = 60f;

    /// <summary>
    /// The burning scenery wrecks (akwreck2, T15 and others): the original draws their smoke as sprite planes in the model,
    /// which the modern look leaves out (ScenerySpawner); this is the black band of the damage smoke from the same point.
    /// Only those within reach of the camera spawn.
    /// </summary>
    private void StaticSmokers(double dt)
    {
        if (Builder.Scenery is not { } scn || scn.Smokers.Count == 0 || GetViewport().GetCamera3D() is not { } cam) return;
        var org = Origin();
        var camG = Coords.ToGame(cam.GlobalPosition) + org;
        float lift = Math.Max(1f, scn.SmokeExtent.Height / 6f);   // the ordinary puff column is about 6 m: reach the original's planes' height
        if (_fires.Length != scn.Smokers.Count) _fires = new StaticFire?[scn.Smokers.Count];
        // the fires: within reach of the camera, the nearest few lit
        var near = new List<(float D2, int I)>();
        for (int i = 0; i < scn.Smokers.Count; i++)
        {
            float d2 = System.Numerics.Vector3.DistanceSquared(scn.Smokers[i], camG);
            if (d2 <= FireReach * FireReach) near.Add((d2, i));
            else _fires[i]?.Set(false, false);
        }
        near.Sort((a, b) => a.D2.CompareTo(b.D2));
        StaticFire.SceneLight = SceneLight;
        for (int r = 0; r < near.Count && scn.FlameExtent.Height > 0 && WreckFire.Enabled; r++)
        {
            int i = near[r].I;
            if (_fires[i] is null) { _fires[i] = new StaticFire(scn.FlameExtent.Height, scn.FlameExtent.Width) { Name = $"StaticFire{i}" }; AddChild(_fires[i]); }
            _fires[i]!.Position = Coords.ToGodot(scn.Smokers[i] - org);
            _fires[i]!.Set(true, r < FireLights, near[r].D2 < EmberReach * EmberReach);
        }
        for (int i = 0; i < scn.Smokers.Count; i++)
        {
            var at = scn.Smokers[i];
            if (System.Numerics.Vector3.DistanceSquared(at, camG) > SmokeReach * SmokeReach) continue;
            double c = _staticCarry.GetValueOrDefault(i) + dt * 70f * RateMul * 2.5;
            int n = (int)c; _staticCarry[i] = c - n;
            for (int k = 0; k < n; k++) SpawnModern(at + new System.Numerics.Vector3(0, scn.FlameExtent.Height * 0.8f, 0), System.Numerics.Vector3.Zero, 2f, 0f, lift);   // from the flames' tips, out of the fire's own soot
        }
    }

    private void SpawnModern(CarNode car, I76.Sim.Vehicles.Vehicle v, float level, float back, System.Numerics.Vector3 shift = default)
    {
        float r1 = (float)_rng.NextDouble(), r2 = (float)_rng.NextDouble(), r3 = (float)_rng.NextDouble();
        var eng = EnginePoint(car);   // modern look: from the car's engine bay, just above the hood (the 1997 look keeps the original's rear emitter)
        float ox = eng.X + (r1 - 0.5f) * 0.6f, oz = eng.Z + (r2 - 0.5f) * 0.5f, oy = eng.Y + 0.1f + 0.3f * r3;
        EmitModern(Coords.ToGame(car.GlobalTransform * new Vector3(ox, oy, -oz)) + Origin() + shift, v.Vel, level, back, 1f);
    }

    /// <summary>The smoke being drawn in the current mission, for the burning wrecks (WreckFire) to feed.</summary>
    public static DamageSmoke? Current { get; private set; }
    private readonly Dictionary<object, double> _burnCarry = [];

    /// <summary>
    /// A burning wreck's smoke, when the smoke layer draws it capped (SmokeLayer.cs): black puffs from <paramref name="at"/>
    /// (Godot world), <paramref name="amount"/> (0..1) of the full rate, the same as a burning building's. Otherwise the
    /// wreck's own particles draw it and this does nothing.
    /// </summary>
    public bool Burn(object key, Vector3 at, float amount, double dt, float spread)
    {
        if (!SmokeLayer.Active || !_modern || !_ready) return false;
        double c = _burnCarry.GetValueOrDefault(key) + dt * 70f * RateMul * 2.5 * amount;
        int n = (int)c; _burnCarry[key] = c - n;
        var g = Coords.ToGame(at) + Origin();
        for (int k = 0; k < n; k++) SpawnModern(g + new System.Numerics.Vector3((float)(_rng.NextDouble() - 0.5) * spread, 0, (float)(_rng.NextDouble() - 0.5) * spread), System.Numerics.Vector3.Zero, 2f, 0f, 1.3f);
        return true;
    }

    /// <summary>The wreck is done: forget its carry.</summary>
    public void Extinguish(object key) => _burnCarry.Remove(key);

    /// <summary>Every puff gone at once: the rehearsal of a burning wreck behind the loading card leaves none of its smoke to rise from a whole car.</summary>
    public void Clear()
    {
        Array.Clear(_pool); Array.Clear(_mp); _burnCarry.Clear(); _liveM = 0;
        if (_ready) { SmokeLayer.Submit(this, _layerBuf, 0, System.Numerics.Vector3.One); HideBillows(); _mm.VisibleInstanceCount = 0; }   // and what was handed to the layer, which would show for one more frame
    }

    private void SpawnModern(System.Numerics.Vector3 at, System.Numerics.Vector3 vel, float level, float back, float lift)
    {
        var j = new System.Numerics.Vector3((float)_rng.NextDouble() - 0.5f, 0.3f * (float)_rng.NextDouble(), (float)_rng.NextDouble() - 0.5f);
        EmitModern(at + j, vel, level, back, lift);
    }

    private void EmitModern(System.Numerics.Vector3 g, System.Numerics.Vector3 vel, float level, float back, float lift)
    {
        float r1 = (float)_rng.NextDouble(), r2 = (float)_rng.NextDouble(), r3 = (float)_rng.NextDouble(), r4 = (float)_rng.NextDouble(), r5 = (float)_rng.NextDouble();
        float dens = (level < 1 ? Mathf.Lerp(0.3f, 0.6f, level) : Mathf.Lerp(0.6f, 0.5f, level - 1)) * 1.4f;
        float sizeMul = level < 1 ? Mathf.Lerp(0.7f, 1f, level) : Mathf.Lerp(1f, 1.3f, level - 1);
        sizeMul *= SizeBoost * (1f + 0.5f * (lift - 1f) / 1.6f);
        ref var p = ref _mp[_mnext];
        _mnext = (_mnext + 1) % CapM;
        // only a little of the car's velocity: the rest it leaves behind. Spawned along the frame's travel
        p = new MPuff
        {
            Live = true, X = g.X - vel.X * back, Y = g.Y, Z = g.Z - vel.Z * back,
            Vx = vel.X * 0.15f + (r4 - 0.5f) * 0.8f, Vy = (1.2f + r5) * lift, Vz = vel.Z * 0.15f + (r5 - 0.5f) * 0.8f,
            Life = 2f + 2f * r1 * r4 + 1.2f * r2 * (level / 2f), End = (1.6f + 2.6f * r2 * r2 + 0.6f * r4) * sizeMul, Dens = dens, Lift = lift,
            Dark = level < 1 ? level * 0.4f : Mathf.Lerp(0.4f, 1f, Mathf.Clamp((level - 1f), 0f, 1f)), Seed = r3,
        };
        p.Life = Math.Min(p.Life, 4f);
    }

    private void HideBillows() { foreach (var m in _slabMm) m.VisibleInstanceCount = 0; }

    public override void _ExitTree()
    {
        SmokeLayer.Release(this);
        if (Current == this) Current = null;
    }

    private void DrawModern()
    {
        var cam = GetViewport().GetCamera3D();
        if (cam is null) { HideBillows(); SmokeLayer.Submit(this, _layerBuf, 0, System.Numerics.Vector3.One); return; }
        var ct = cam.GlobalTransform;
        var org = Origin();
        _sun ??= GetTree().Root.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>().FirstOrDefault();
        var toSun = _sun is null ? new Vector3(0.3f, 0.8f, 0.5f) : _sun.GlobalTransform.Basis.Z;
        _matM.SetShaderParameter("sun_view", ct.Basis.Inverse() * toSun);
        int cnt = 0;
        for (int i = 0; i < CapM; i++)
        {
            ref var p = ref _mp[i];
            if (!p.Live) continue;
            var gp = new Vector3((float)(p.X - org.X), (float)(p.Y - org.Y), -(float)(p.Z - org.Z));
            float depth = -ct.Basis.Z.Dot(gp - ct.Origin);
            if (depth < cam.Near || depth > 150) continue;
            _keyM[cnt] = -depth; _idxM[cnt++] = i;   // ascending -depth: far to near
        }
        Array.Sort(_keyM, _idxM, 0, cnt);
        // MultiMesh buffer layout: 12 transform floats (3 rows of basis x,y,z,origin), then the custom data colour
        for (int j = 0; j < cnt; j++)
        {
            ref var p = ref _mp[_idxM[j]];
            float lf = p.Age / p.Life, e = 1f - (1f - lf) * (1f - lf);
            float size = Mathf.Lerp(0.8f, p.End, e);
            int o = j * 16;
            _layerBuf[o] = size; _layerBuf[o + 3] = (float)(p.X - org.X);
            _layerBuf[o + 7] = (float)(p.Y - org.Y); _layerBuf[o + 11] = -(float)(p.Z - org.Z);
            _layerBuf[o + 12] = lf; _layerBuf[o + 13] = p.Dens; _layerBuf[o + 14] = p.Dark; _layerBuf[o + 15] = p.Seed;
        }
        if (SmokeLayer.Active)
        {
            // the smoke layer draws them, capped (SmokeLayer.cs), and the mirror its own; the slabs are for when it cannot
            SmokeLayer.SceneLight = SceneLight;
            HideBillows();
            SmokeLayer.Submit(this, _layerBuf, cnt, System.Numerics.Vector3.One);
            _mirror ??= new MirrorDraw(this, CapM, System.Numerics.Vector3.One);
            _mirror.Update(_layerBuf, cnt, SceneLight);
            return;
        }
        SmokeLayer.Thin(_layerBuf, cnt, cam);
        SmokeLayer.Lights(_matM);
        int slab = SlabFar.Length - 1, n = 0;   // puffs come far to near, so the slabs from the last
        Vector3 sum = default;
        void Flush()
        {
            var mm = _slabMm[slab];
            if (n > 0)
            {
                mm.Buffer = _slabBuf[slab];
                var mean = sum / n;
                mm.CustomAabb = new Aabb(mean - Vector3.One * 0.5f, Vector3.One);
            }
            mm.VisibleInstanceCount = n;
            slab--; n = 0; sum = default;
        }
        for (int j = 0; j < cnt; j++)
        {
            ref var p = ref _mp[_idxM[j]];
            float px = (float)(p.X - org.X), py = (float)(p.Y - org.Y), pz = -(float)(p.Z - org.Z);
            while (slab > 0 && -_keyM[j] <= SlabFar[slab - 1]) Flush();
            float lf = p.Age / p.Life, e = 1f - (1f - lf) * (1f - lf);
            float size = Mathf.Lerp(0.8f, p.End, e);
            var buf = _slabBuf[slab];
            int o = n++ * 16;
            buf[o] = size; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = px;
            buf[o + 4] = 0; buf[o + 5] = size; buf[o + 6] = 0; buf[o + 7] = py;
            buf[o + 8] = 0; buf[o + 9] = 0; buf[o + 10] = size; buf[o + 11] = pz;
            buf[o + 12] = lf; buf[o + 13] = _layerBuf[j * 16 + 13]; buf[o + 14] = p.Dark; buf[o + 15] = p.Seed;
            sum += new Vector3(px, py, pz);
        }
        while (slab >= 0) Flush();
    }

    private static bool Combat_IsDead(I76.Sim.Vehicles.Vehicle v) => I76.Sim.Weapons.Combat.IsDead(v);

    private void Spawn(CarNode car, I76.Sim.Vehicles.Vehicle v, int kind, int cap)
    {
        float r1 = (float)_rng.NextDouble(), r2 = (float)_rng.NextDouble(), r3 = (float)_rng.NextDouble();
        float ox = r1 - 0.5f, oz = RearZ(car) - 1 + r2 - 0.5f, oy = 0.2f + 1 + 0.5f * r3;
        var t = car.GlobalTransform;
        var g = Coords.ToGame(t * new Vector3(ox, oy, -oz)) + Origin();
        var vel = v.Vel;
        float speed = v.Speed;
        ref var p = ref _pool[_next];
        _next = (_next + 1) % cap;   // the oldest is recycled when the pool is full
        p = new Puff { Live = true, X = g.X, Y = g.Y, Z = g.Z, Vx = vel.X, Vy = vel.Y + 1.2f, Vz = vel.Z, Kind = kind };
        if (speed > 0.1f)
        {
            var d = vel / MathF.Max(vel.Length(), 1e-4f);
            p.Dx = d.X; p.Dy = d.Y; p.Dz = d.Z; p.S = speed; p.Target = 0.75f * speed;
        }
    }

    private void Draw(bool modern, int cap, float life)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam is null) { _mm.VisibleInstanceCount = 0; return; }
        var ct = cam.GlobalTransform;
        var org = Origin();
        _order.Clear();
        for (int i = 0; i < cap; i++)
        {
            ref var p = ref _pool[i];
            if (!p.Live) continue;
            var gp = new Vector3((float)(p.X - org.X), (float)(p.Y - org.Y), -(float)(p.Z - org.Z));
            float depth = -ct.Basis.Z.Dot(gp - ct.Origin);   // view depth: drawn within the near plane and 60
            if (depth < cam.Near || depth > 60) continue;
            _order.Add((depth, i));
        }
        _order.Sort((a, b) => b.Depth.CompareTo(a.Depth));   // far to near
        for (int j = 0; j < _order.Count; j++)
        {
            ref var p = ref _pool[_order[j].I];
            var pos = new Vector3((float)(p.X - org.X), (float)(p.Y - org.Y), -(float)(p.Z - org.Z));
            float lf = Math.Clamp(p.Age / life, 0f, 0.9999f);
            int age = (int)(lf * Life1997);
            int frame = p.Kind == 2 || p.Kind == 1 ? FrameTable[1][age] : FrameTable[0][age];
            float size = modern ? 0.8f * (1f + 0.6f * lf) : 0.8f;
            _mm.SetInstanceTransform(j, new Transform3D(Basis.Identity.Scaled(Vector3.One * size), pos));
            _mm.SetInstanceCustomData(j, new Color(frame, p.Kind, lf, 0));
        }
        _mm.VisibleInstanceCount = _order.Count;
    }
}
