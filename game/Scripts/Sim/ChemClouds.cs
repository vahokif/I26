using Godot;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The Chemical Mortar's cloud (Nitro, ordnance kind 0x16): a landed round (<see cref="Round.Settled"/>) is the damage
/// smoke's emitter of kind 3 at the round. 1997: as the original, two xcp1 puffs a tick at the round with a ±0.5 jitter, rising, 20 ticks' life, its own
/// row of the frame table, the quad 1.7 times wider. Modern: a low, sickly green billowing cloud from the
/// same point (the damage smoke's modern shader, tinted). Purely visual; the cloud's harm is the sim's (the round's own hit test).
/// Its jitter has its own System.Random, never the sim's stream.
/// </summary>
public partial class ChemClouds : Node3D
{
    public required Func<string, Image?> Texture { get; init; }
    public float SceneLight { get; set; } = 1f;

    private const int Cap1997 = 512, CapModern = 768, Life1997 = 20;
    private static readonly int[] FrameTable = [0, 1, 2, 3, 4, 5, 6, 7, 4, 5, 6, 7, 4, 8, 6, 10, 8, 9, 10, 11];
    private static readonly System.Numerics.Vector3 Tint = new(0.62f, 0.95f, 0.28f);

    private struct Puff { public bool Live; public double X, Y, Z; public float Vx, Vy, Vz, Age, Life, End, Seed; }
    private readonly Puff[] _old = new Puff[Cap1997];
    private readonly Puff[] _new = new Puff[CapModern];
    private int _nextOld, _nextNew;
    private readonly Random _rng = new(1622);
    private MirrorDraw? _mirror;
    private MultiMesh _mmOld = null!, _mmNew = null!;
    private ShaderMaterial _matOld = null!, _matNew = null!;
    private bool _haveAtlas, _wasModern = true;
    private int _lastTick = SimClock.Now;
    private readonly Dictionary<Round, double> _carry = [];
    private readonly List<(float Depth, int I)> _order = [];
    private readonly float[] _buf = new float[CapModern * 16];

    public override void _Ready()
    {
        _matOld = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/damage_smoke.gdshader") };
        if (BuildAtlas() is { } atlas)
        {
            _matOld.SetShaderParameter("atlas_near", atlas.Tex);
            _matOld.SetShaderParameter("atlas_lin", atlas.Tex);
            _matOld.SetShaderParameter("texel", new Vector2(atlas.Cell.X, atlas.Cell.Y));
            _matOld.SetShaderParameter("grid", new Vector2(12, 1));
            _matOld.SetShaderParameter("stretch_x", 1.7f);
            _matOld.SetShaderParameter("modern", false);
            _haveAtlas = true;
        }
        _mmOld = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _matOld }, InstanceCount = Cap1997, VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _mmOld, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, Name = "Puffs" });
        _matNew = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/damage_smoke_modern.gdshader") };
        _matNew.SetShaderParameter("noise_tex", DamageSmoke.Noise);
        _matNew.SetShaderParameter("tint", new Vector3(Tint.X, Tint.Y, Tint.Z));
        _mmNew = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _matNew }, InstanceCount = CapModern, VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _mmNew, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, Name = "Billows" });
    }

    /// <summary>The twelve xcp1 frames (_101.._104, _201.._204, _301.._304) side by side.</summary>
    private (ImageTexture Tex, Vector2I Cell)? BuildAtlas()
    {
        Image? img = null; var cell = default(Vector2I);
        for (int i = 0; i < 12; i++)
        {
            string n = $"xcp1_{i / 4 + 1}0{i % 4 + 1}";
            if (Texture(n) is not { } t) { GD.Print($"chemical cloud: texture {n} not found"); return null; }
            if (img is null) { cell = new Vector2I(t.GetWidth(), t.GetHeight()); img = Image.CreateEmpty(cell.X * 12, cell.Y, false, Image.Format.Rgba8); }
            if (t.GetWidth() != cell.X || t.GetHeight() != cell.Y) return null;
            t.Convert(Image.Format.Rgba8);
            img.BlitRect(t, new Rect2I(0, 0, cell.X, cell.Y), new Vector2I(i * cell.X, 0));
        }
        return img is null ? null : (ImageTexture.CreateFromImage(img), cell);
    }

    /// <summary>A frame: the landed chemical rounds puff, the puffs move and age, and are drawn.</summary>
    public void Frame(IReadOnlyList<Round> rounds, bool modern, Func<System.Numerics.Vector3, Vector3> toGodot, double delta)
    {
        if (modern != _wasModern) { _wasModern = modern; Array.Clear(_old); Array.Clear(_new); _carry.Clear(); }
        int steps = SimClock.Take(ref _lastTick);
        if (modern)
        {
            _mmOld.VisibleInstanceCount = 0;
            Modern(rounds, toGodot, Math.Min(delta, 0.25));
            return;
        }
        _mmNew.VisibleInstanceCount = 0;
        SmokeLayer.Submit(this, _buf, 0, System.Numerics.Vector3.One);
        if (!_haveAtlas) { _mmOld.VisibleInstanceCount = 0; return; }
        float dt = SimClock.Tick;
        for (int s = 0; s < steps; s++)
        {
            foreach (var r in rounds)
            {
                if (r.Kind != 0x16 || !r.Settled) continue;
                for (int k = 0; k < 2; k++)
                {
                    ref var p = ref _old[_nextOld];
                    _nextOld = (_nextOld + 1) % Cap1997;
                    p = new Puff
                    {
                        Live = true, X = r.Pos.X + _rng.NextDouble() - 0.5, Y = r.Pos.Y + _rng.NextDouble() - 0.5, Z = r.Pos.Z + _rng.NextDouble() - 0.5,
                        Vy = 1.2f,
                    };
                }
            }
            for (int i = 0; i < Cap1997; i++)
            {
                ref var p = ref _old[i];
                if (!p.Live) continue;
                if (p.Age >= Life1997) { p.Live = false; continue; }
                p.Y += dt * p.Vy;
                p.Age += 1;
            }
        }
        DrawOld(toGodot);
    }

    private void DrawOld(Func<System.Numerics.Vector3, Vector3> toGodot)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam is null) { _mmOld.VisibleInstanceCount = 0; return; }
        var ct = cam.GlobalTransform;
        _order.Clear();
        for (int i = 0; i < Cap1997; i++)
        {
            ref var p = ref _old[i];
            if (!p.Live) continue;
            var gp = toGodot(new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z));
            float depth = -ct.Basis.Z.Dot(gp - ct.Origin);
            if (depth < cam.Near || depth > 60) continue;
            _order.Add((depth, i));
        }
        _order.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        for (int j = 0; j < _order.Count; j++)
        {
            ref var p = ref _old[_order[j].I];
            var gp = toGodot(new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z));
            int age = Math.Clamp((int)p.Age, 0, Life1997 - 1);
            _mmOld.SetInstanceTransform(j, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.8f), gp));
            _mmOld.SetInstanceCustomData(j, new Color(FrameTable[age], 0, age / (float)Life1997, 0));
        }
        _mmOld.VisibleInstanceCount = _order.Count;
    }

    private void Modern(IReadOnlyList<Round> rounds, Func<System.Numerics.Vector3, Vector3> toGodot, double dt)
    {
        _matNew.SetShaderParameter("scene_light", SceneLight);
        var live = new HashSet<Round>();
        foreach (var r in rounds)
        {
            if (r.Kind != 0x16 || !r.Settled) continue;
            live.Add(r);
            // thick at first, thinning towards the end of its life
            double c = _carry.GetValueOrDefault(r) + dt * 30;
            int n = (int)c;
            _carry[r] = c - n;
            for (int k = 0; k < n; k++)
            {
                float a = (float)(_rng.NextDouble() * Math.Tau), d = 3.2f * MathF.Sqrt((float)_rng.NextDouble());
                ref var p = ref _new[_nextNew];
                _nextNew = (_nextNew + 1) % CapModern;
                p = new Puff
                {
                    Live = true, X = r.Pos.X + MathF.Cos(a) * d, Y = r.Pos.Y + 0.6f + 1.2f * _rng.NextDouble(), Z = r.Pos.Z + MathF.Sin(a) * d,
                    Vx = MathF.Cos(a) * 0.5f, Vy = 0.5f + 0.6f * (float)_rng.NextDouble(), Vz = MathF.Sin(a) * 0.5f,
                    Life = 2.6f + 1.6f * (float)_rng.NextDouble(), End = 2.6f + 2.6f * (float)_rng.NextDouble(), Seed = (float)_rng.NextDouble(),
                };
            }
        }
        foreach (var gone in _carry.Keys.Where(k => !live.Contains(k)).ToList()) _carry.Remove(gone);
        for (int i = 0; i < CapModern; i++)
        {
            ref var p = ref _new[i];
            if (!p.Live) continue;
            if ((p.Age += (float)dt) >= p.Life) { p.Live = false; continue; }
            float k = Math.Min(1f, 0.8f * (float)dt);
            p.Vx -= p.Vx * k; p.Vz -= p.Vz * k; p.Vy -= p.Vy * 0.5f * k;
            p.X += dt * p.Vx; p.Y += dt * p.Vy; p.Z += dt * p.Vz;
        }
        var cam = GetViewport().GetCamera3D();
        if (cam is null) { _mmNew.VisibleInstanceCount = 0; SmokeLayer.Submit(this, _buf, 0, System.Numerics.Vector3.One); return; }
        var ct = cam.GlobalTransform;
        _order.Clear();
        for (int i = 0; i < CapModern; i++)
        {
            ref var p = ref _new[i];
            if (!p.Live) continue;
            var gp = toGodot(new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z));
            float depth = -ct.Basis.Z.Dot(gp - ct.Origin);
            if (depth < cam.Near || depth > 250) continue;
            _order.Add((depth, i));
        }
        _order.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        for (int j = 0; j < _order.Count; j++)
        {
            ref var p = ref _new[_order[j].I];
            var gp = toGodot(new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z));
            float lf = p.Age / p.Life, e = 1f - (1f - lf) * (1f - lf);
            float size = Mathf.Lerp(1.2f, p.End, e);
            int o = j * 16;
            _buf[o] = size; _buf[o + 1] = 0; _buf[o + 2] = 0; _buf[o + 3] = gp.X;
            _buf[o + 4] = 0; _buf[o + 5] = size; _buf[o + 6] = 0; _buf[o + 7] = gp.Y;
            _buf[o + 8] = 0; _buf[o + 9] = 0; _buf[o + 10] = size; _buf[o + 11] = gp.Z;
            _buf[o + 12] = lf; _buf[o + 13] = 0.6f; _buf[o + 14] = 0.25f; _buf[o + 15] = p.Seed;
        }
        if (SmokeLayer.Active)
        {
            // the smoke layer draws them, capped (SmokeLayer.cs)
            SmokeLayer.SceneLight = SceneLight;
            SmokeLayer.Submit(this, _buf, _order.Count, new System.Numerics.Vector3(Tint.X, Tint.Y, Tint.Z));
            _mirror ??= new MirrorDraw(this, CapModern, Tint);
            _mirror.Update(_buf, _order.Count, SceneLight);
            _mmNew.VisibleInstanceCount = 0;
            return;
        }
        SmokeLayer.Thin(_buf, _order.Count, cam);
        SmokeLayer.Lights(_matNew);
        if (_order.Count > 0) _mmNew.Buffer = _buf;
        _mmNew.VisibleInstanceCount = _order.Count;
    }

    public override void _ExitTree() => SmokeLayer.Release(this);
}
