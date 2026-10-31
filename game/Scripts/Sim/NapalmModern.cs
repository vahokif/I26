using Godot;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's flamer streams (the 1997 look keeps FlameStreams' xfl1/xfm1/xfh1 tubes and xflht1 bursts).
/// Purely visual, nothing feeds back into the sim. A camera-facing jet ribbon through the stream's points with a
/// procedural fire shader (napalm_stream.gdshader, the wreck fire's palette), smoke and embers curling off it, flames
/// licking up from where it lies on a target, and a flickering warm light at the nozzle and the impact. The drawn
/// length chases the stream's, so a stub and a 19 m stream both grow out of the nozzle and run back into it as the
/// points come and go. Smoke already in the air drifts on after the stream is gone.
/// </summary>
public partial class NapalmModern : Node3D
{
    public float SceneLight { get; set; } = 1f;

    private sealed class Stream
    {
        public Vector3[] C = new Vector3[19];   // centres, as of the last frame it was alive
        public int Count, FirstHit = -1, Style;
        public float Reach, Glow;                    // drawn length in segments
        public bool Alive; public Vector3 HitAt; 
        public double AccSmoke, AccLick, AccEmber;
        public OmniLight3D? Nozzle, Impact;
    }

    private struct Puff { public Vector3 P, V; public float Age, Life, Size, Grow, Seed; public byte Kind; public float Yaw; }  // 0 lick, 1 ember, 2 smoke

    private const int MaxPuffs = 260;
    private const float GrowRate = 45f, RetractRate = 70f;
    private static readonly float[] StyleSize = [0.6f, 0.8f, 1f];

    private readonly Dictionary<Flame, Stream> _streams = [];
    private readonly List<Puff> _puffs = [];
    private ImmediateMesh _mesh = null!;
    private ShaderMaterial _jet = null!, _fire = null!, _smoke = null!;
    private readonly RandomNumberGenerator _rng = new();
    private double _t;

    public override void _Ready()
    {
        var shader = GD.Load<Shader>("res://Shaders/napalm_stream.gdshader");
        ShaderMaterial Mat(int mode) { var m = new ShaderMaterial { Shader = shader }; m.SetShaderParameter("mode", mode); return m; }
        _jet = Mat(0); _fire = Mat(1); _smoke = Mat(2);
        _mesh = new ImmediateMesh();
        AddChild(new MeshInstance3D { Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 100f });
        _rng.Seed = 76;
    }

    public void Clear()
    {
        _mesh.ClearSurfaces();
        foreach (var s in _streams.Values) { s.Nozzle?.QueueFree(); s.Impact?.QueueFree(); }
        _streams.Clear();
        _puffs.Clear();
    }

    /// <summary>Draw this frame. <paramref name="live"/> pairs each flame with its points where the car is drawn (3 per cross-section).</summary>
    public void Update(double dt, List<(Flame F, Vector3[] P)> live)
    {
        _t += dt;
        float fdt = (float)dt;
        foreach (var s in _streams.Values) s.Alive = false;
        foreach (var (f, p) in live)
        {
            if (!_streams.TryGetValue(f, out var s)) _streams[f] = s = new Stream { Style = Math.Clamp(f.Style, 0, 2) };
            s.Alive = true; s.Count = f.Count; s.FirstHit = f.FirstHit;
            for (int i = 0; i < f.Count; i++) s.C[i] = (p[i * 3] + p[i * 3 + 1] + p[i * 3 + 2]) / 3f;
        }
        var cam = GetViewport()?.GetCamera3D();
        Vector3 camPos = cam is null ? Vector3.Zero : ToLocal(cam.GlobalPosition);
        Basis cb = cam is null ? Basis.Identity : cam.GlobalBasis;

        _mesh.ClearSurfaces();
        bool begun = false;
        foreach (var (key, s) in _streams.ToArray())
        {
            float target = s.Alive ? s.Count - 1 : 0;
            s.Reach = target > s.Reach ? Math.Min(target, s.Reach + GrowRate * fdt) : Math.Max(target, s.Reach - RetractRate * fdt);
            bool hit0 = s.Alive && s.FirstHit >= 0 && s.FirstHit < s.Count - 1;
            s.Glow = hit0 ? 1f : Math.Max(0f, s.Glow - fdt / 0.8f);
            if (!s.Alive && s.Reach <= 0.01f && s.Glow <= 0f)
            {
                s.Nozzle?.QueueFree(); s.Impact?.QueueFree();
                _streams.Remove(key);
                continue;
            }
            if (s.Reach > 0.02f)
            {
                if (!begun) { _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _jet); begun = true; }
                Ribbon(s, camPos);
            }
            Emit(s, fdt);
            Lights(s);
        }
        if (begun) _mesh.SurfaceEnd();
        Puffs(fdt, cb);
    }

    private Vector3 At(Stream s, float u)
    {
        int i = Math.Min((int)u, s.Count - 2);
        return s.C[i].Lerp(s.C[i + 1], Math.Min(u - i, 1f));
    }

    private void Ribbon(Stream s, Vector3 camPos)
    {
        float sc = StyleSize[s.Style];
        float len = s.Reach;
        int steps = Math.Max(2, (int)Math.Ceiling(len * 2f));
        float ms = 0;
        Vector3 prev = At(s, 0), pl = default, pr = default;
        Color pc = default;
        for (int k = 0; k <= steps; k++)
        {
            float u = len * k / steps;
            var p = At(s, u);
            if (k > 0) ms += prev.DistanceTo(p);
            var tan = (At(s, Math.Min(u + 0.25f, s.Count - 1.001f)) - At(s, Math.Max(u - 0.25f, 0))).Normalized();
            var side = tan.Cross((camPos - p).Normalized());
            side = side.LengthSquared() < 1e-6f ? Vector3.Up : side.Normalized();
            float t = u / Math.Max(s.Count - 1, 1);
            bool flat = s.FirstHit >= 0 && u >= s.FirstHit;
            float contact = flat ? 1f : 0f;
            float w = (0.17f + 0.38f * t) * sc * (flat ? 1.5f : 1f);
            // the tip fades as the stream is still growing out or running back
            float tip = Mathf.Clamp((len - u) * 2f + 0.15f, 0f, 1f) * Mathf.Clamp(len * 3f, 0f, 1f);
            var col = new Color(t, contact, tip, sc);
            var a = p - side * w; var b = p + side * w;
            if (k > 0)
            {
                float v0 = ms - prev.DistanceTo(p);
                V(pl, 0, v0, pc); V(pr, 1, v0, pc); V(b, 1, ms, col);
                V(pl, 0, v0, pc); V(b, 1, ms, col); V(a, 0, ms, col);
            }
            pl = a; pr = b; prev = p; pc = col;
        }
    }

    private void V(Vector3 p, float u, float v, Color c) { _mesh.SurfaceSetColor(c); _mesh.SurfaceSetUV(new Vector2(u, v)); _mesh.SurfaceAddVertex(p); }

    private void Emit(Stream s, float dt)
    {
        float sc = StyleSize[s.Style];
        if (s.Alive && s.Reach >= 0.5f && s.FirstHit >= 0 && s.FirstHit < s.Count - 1) s.HitAt = At(s, Math.Min(s.Reach, s.Count - 1));
        if (!s.Alive && s.Glow > 0f)
        {
            // contact has stopped: the fire on the target dies down over the glow's span
            s.AccLick += dt * 12 * s.Glow * sc; s.AccSmoke += dt * 8 * sc;
            while (s.AccLick >= 1) { s.AccLick--; Spawn(0, s.HitAt + new Vector3(_rng.Randf() - 0.5f, 0f, _rng.Randf() - 0.5f) * 0.9f * sc, sc); }
            while (s.AccSmoke >= 1) { s.AccSmoke--; Spawn(3, s.HitAt + Vector3.Up * 0.4f, sc); }
            return;
        }
        if (!s.Alive || s.Reach < 0.5f) return;
        s.AccSmoke += dt * (14 + 2.2 * s.Reach) * sc; s.AccEmber += dt * 9 * sc;
        bool hit = s.FirstHit >= 0 && s.FirstHit < s.Count - 1;
        if (hit) s.AccLick += dt * (22 + 2 * (s.Count - s.FirstHit)) * sc;
        while (s.AccSmoke >= 1) { s.AccSmoke--; Spawn(3, At(s, _rng.Randf() * s.Reach), sc); }
        while (s.AccEmber >= 1) { s.AccEmber--; Spawn(2, At(s, _rng.Randf() * s.Reach), sc); }
        while (s.AccLick >= 1)
        {
            s.AccLick--;
            float u = Mathf.Lerp(s.FirstHit, Math.Min(s.Reach, s.Count - 1), _rng.Randf());
            var p = At(s, u) + new Vector3(_rng.Randf() - 0.5f, _rng.Randf() * 0.5f - 0.1f, _rng.Randf() - 0.5f) * 0.9f * sc;
            Spawn(0, p, sc);
            if (_rng.Randf() < 0.2f) Spawn(3, p + Vector3.Up * 0.3f, sc);
            if (_rng.Randf() < 0.2f) Spawn(1, p, sc);
        }
    }

    // kind 0 lick, 1 ember (from a lick), 2 ember (from the stream), 3 smoke
    private void Spawn(int what, Vector3 p, float sc)
    {
        if (_puffs.Count >= MaxPuffs) return;
        float r() => _rng.Randf() - 0.5f;
        var pf = new Puff { P = p, Seed = _rng.Randf(), Yaw = _rng.Randf() * Mathf.Pi };
        switch (what)
        {
            case 0: pf.Kind = 0; pf.Life = 0.5f + 0.4f * _rng.Randf(); pf.Size = (0.9f + 0.8f * _rng.Randf()) * sc; pf.V = new Vector3(r() * 0.5f, 1.4f + _rng.Randf(), r() * 0.5f); break;
            case 3: pf.Kind = 2; pf.Life = 1.6f + 1.0f * _rng.Randf(); pf.Size = 0.3f * sc; pf.Grow = 0.8f * sc; pf.V = new Vector3(r() * 0.8f, 1.0f + _rng.Randf(), r() * 0.8f); break;
            default: pf.Kind = 1; pf.Life = 0.5f + 0.5f * _rng.Randf(); pf.Size = 0.05f; pf.V = new Vector3(r() * 2f, 1.5f + 2f * _rng.Randf(), r() * 2f); break;
        }
        _puffs.Add(pf);
    }

    private void Puffs(float dt, Basis cb)
    {
        if (_puffs.Count == 0) return;
        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            var p = _puffs[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _puffs[i] = _puffs[^1]; _puffs.RemoveAt(_puffs.Count - 1); continue; }
            float k = p.Age;
            p.P += p.V * dt;
            if (p.Kind == 2) { p.V.X += Mathf.Sin(k * 3f + p.Seed * 20f) * dt * 1.2f; p.V *= 1f - 0.6f * dt; p.Size += p.Grow * dt; }
            else if (p.Kind == 1) p.V.Y -= 6f * dt;
            _puffs[i] = p;
        }
        var right = cb.X; var up = cb.Y;
        var side = Vector3.Up.Cross(cb.Z).Normalized();
        // fire (licks, embers) glows; smoke goes over it
        for (int pass = 0; pass < 2; pass++)
        {
            bool any = false;
            foreach (var p in _puffs)
            {
                if ((p.Kind == 2) != (pass == 1)) continue;
                if (!any) { _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, pass == 0 ? _fire : _smoke); any = true; }
                var c = new Color(p.Age / p.Life, p.Seed, p.Kind == 1 ? 1f : 0f, 1f);
                if (p.Kind == 0)
                {
                    // three planes crossed about the vertical, so the tongue has body from any side and still rises in world space
                    float h = p.Size * (0.45f + 0.65f * Mathf.Min(p.Age / p.Life * 3f, 1f));
                    var u = Vector3.Up * h;
                    for (int k = 0; k < 3; k++)
                    {
                        float a = p.Yaw + k * Mathf.Pi / 3f;
                        var s = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * p.Size * 0.42f;
                        Q(p.P - s, p.P + s, p.P + s + u, p.P - s + u, new Color(c.R, c.G, c.B, 0.6f));
                    }
                }
                else
                {
                    var s = right * p.Size; var u = up * p.Size;
                    Q(p.P - s - u, p.P + s - u, p.P + s + u, p.P - s + u, c);
                }
            }
            if (any) _mesh.SurfaceEnd();
        }
    }

    // a, b the bottom corners, c, d the top ones; the lick's UV.y is 1 at its base
    private void Q(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        const float v0 = 1, v1 = 0;
        V(a, 0, v0, col); V(b, 1, v0, col); V(c, 1, v1, col);
        V(a, 0, v0, col); V(c, 1, v1, col); V(d, 0, v1, col);
    }

    private void Lights(Stream s)
    {
        float flick = 0.8f + 0.2f * Mathf.Sin((float)_t * 41f + s.Style) * Mathf.Sin((float)_t * 17f) + 0.1f * _rng.Randf();
        float sc = StyleSize[s.Style];
        bool on = s.Alive && s.Reach > 0.5f;
        if (on)
        {
            s.Nozzle ??= NewLight(); s.Impact ??= NewLight();
            s.Nozzle.Position = s.C[0] + Vector3.Up * 0.3f;
            s.Nozzle.LightEnergy = 1.4f * flick * sc; s.Nozzle.OmniRange = 5f * sc + 2f; s.Nozzle.Visible = true;
            bool hit = s.FirstHit >= 0 && s.FirstHit < s.Count - 1;
            s.Impact.Visible = hit || s.Glow > 0f;
            if (hit) { s.Impact.Position = s.HitAt + Vector3.Up * 0.8f; s.Impact.LightEnergy = 2.6f * flick * sc; s.Impact.OmniRange = 7f * sc + 2f; }
        }
        else
        {
            if (s.Nozzle is not null) s.Nozzle.Visible = false;
            if (s.Impact is not null)
            {
                s.Impact.Visible = s.Glow > 0f;
                s.Impact.Position = s.HitAt + Vector3.Up * 0.8f; s.Impact.LightEnergy = 2.6f * flick * sc * s.Glow;
            }
        }
    }

    private OmniLight3D NewLight()
    {
        var l = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.18f), ShadowEnabled = false, OmniAttenuation = 1.5f };
        AddChild(l);
        return l;
    }

    public override void _Process(double delta) => _smoke.SetShaderParameter("scene_light", SceneLight);
}
