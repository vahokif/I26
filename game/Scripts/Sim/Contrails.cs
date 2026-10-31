using Godot;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The original's contrails behind rockets and mortar rounds, for the
/// 1997 look. A trail is a ring of up to 23 small triangles across the line of
/// flight (points (∓0.173, −0.1), (0, 0.2)), one stamped every
/// frame, a gap of 2 m or more cut into a power-of-two number of pieces of at
/// most 2 m. The rockets' styles (0–2) roll each new triangle further, 0°, 1°,
/// … 11° and 11° from then on, from a random start. Consecutive triangles are
/// joined by three quads, each the whole texture (u 0 at the older end): the
/// newest segment in the style's _101 texture, the oldest in _301, the rest in
/// _201. Once the round is gone the trail loses four segments a frame.
/// </summary>
public partial class Contrails : Node3D
{
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public required Func<string, Texture2D?> Texture { get; init; }

    private const int Ring = 23;
    // the styles' textures: FireRite, heat seeker, radar missile, HE mortar, WP mortar, cluster, and one more
    private static readonly string[] Styles = ["xdu1", "xsh1", "xsr1", "xme1", "xmw1", "xmc1", "xxt1"];
    private static readonly System.Numerics.Vector2[] Shape = [new(-0.173f, -0.1f), new(0.173f, -0.1f), new(0f, 0.2f)];
    private static readonly (int A, int B)[] Sides = [(2, 1), (1, 0), (0, 2)];

    private sealed class Trail
    {
        public required int Style;
        public readonly List<System.Numerics.Vector3[]> Tris = [];
        /// <summary>Where each section was stamped (the modern look's ribbon follows these).</summary>
        public readonly List<System.Numerics.Vector3> Pts = [];
        public OmniLight3D? Light;
        public float Roll;
        public int Step;
        public System.Numerics.Vector3 Last;
        public bool Gone;
    }

    private readonly Dictionary<Round, Trail> _trails = [];
    private readonly Dictionary<string, StandardMaterial3D?> _materials = [];
    private MeshInstance3D _mesh = null!;
    private ImmediateMesh _im = null!;
    private readonly Random _rand = new();

    /// <summary>The trail style a round's kind has (ord+0x60), or −1 for none: kinds 2, 3, 4, 5, 7, 8, 0x14 and 0x16 trail.</summary>
    private const int ClusterStyle = 5;
    /// <summary>The scene's light (1 by day, lower at night), for the modern trail's smoke.</summary>
    public float SceneLight { get; init; } = 1f;
    private ShaderMaterial? _fire;
    private float _t;
    private int _lastTick = SimClock.Now;

    public static int StyleOf(uint kind) => kind switch { 2 => 0, 3 => 1, 8 => 2, 4 => 3, 7 => 4, 5 => 5, 0x14 or 0x16 => 6, _ => -1 };

    public override void _Ready()
    {
        _im = new ImmediateMesh();
        _mesh = new MeshInstance3D { Mesh = _im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_mesh);
    }

    private StandardMaterial3D? Material(string name)
    {
        if (_materials.TryGetValue(name, out var m)) return m;
        m = Texture(name) is { } t
            ? new StandardMaterial3D
            {
                AlbedoTexture = t, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            }
            : null;
        return _materials[name] = m;
    }

    /// <summary>One frame: each trailing round stamps where it is now (its interpolated pose), trails of gone rounds shrink, then all are drawn.</summary>
    public void Frame(IReadOnlyList<Round> rounds, float alpha, bool modern = false, int predictedFrom = int.MaxValue, float predictedAlpha = 1f)
    {
        // the original's per-frame rules (a stamp, four segments gone) run once a sim tick, not once a render frame
        int steps = SimClock.Take(ref _lastTick);
        var live = new HashSet<Round>();
        for (int ri = 0; ri < rounds.Count; ri++)
        {
            var r = rounds[ri];
            int style = StyleOf(r.Kind);
            if (style < 0 || (modern && style != ClusterStyle) || (r.Kind == 0x16 && r.Settled)) continue;   // a landed chemical round trails no more
            live.Add(r);
            var at = r.PrevPos + (r.Pos - r.PrevPos) * (ri < predictedFrom ? alpha : predictedAlpha);
            if (!_trails.TryGetValue(r, out var t))
            {
                _trails[r] = t = new Trail { Style = style, Roll = (float)(_rand.NextDouble() * Math.Tau), Last = at };
                Stamp(t, r, at);
                continue;
            }
            if (steps == 0) continue;
            float d = (at - t.Last).Length();
            if (d >= 2f)
            {
                int n = 2;
                for (float piece = d * 0.5f; piece > 2f; piece *= 0.5f) n <<= 1;
                for (int k = 1; k <= n; k++) Stamp(t, r, System.Numerics.Vector3.Lerp(t.Last, at, k / (float)n));
            }
            else Stamp(t, r, at);
            t.Last = at;
        }
        foreach (var (r, t) in _trails.ToList())
        {
            if (live.Contains(r)) continue;
            t.Gone = true;
            int drop = Math.Min(4 * steps, t.Tris.Count);
            t.Tris.RemoveRange(0, drop);
            t.Pts.RemoveRange(0, drop);
            if (steps > 0 && t.Tris.Count < 2) { t.Light?.QueueFree(); _trails.Remove(r); }
        }
        if (modern) DrawFire();
        else
        {
            foreach (var t in _trails.Values) if (t.Light is not null) { t.Light.QueueFree(); t.Light = null; }
            Draw();
        }
    }

    private void Stamp(Trail t, Round r, System.Numerics.Vector3 at)
    {
        if (t.Style < 3)
        {
            t.Roll += t.Step * (MathF.PI / 180f);
            t.Step = Math.Min(t.Step + 1, 11);
        }
        float c = MathF.Cos(t.Roll), s = MathF.Sin(t.Roll);
        var rot = r.Rot;
        var tri = new System.Numerics.Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            var p = Shape[i];
            float x = p.X * c - p.Y * s, y = p.X * s + p.Y * c;
            tri[i] = at + rot.Right * x + rot.Up * y;
        }
        t.Tris.Add(tri);
        t.Pts.Add(at);
        if (t.Tris.Count > Ring) { t.Tris.RemoveAt(0); t.Pts.RemoveAt(0); }
    }

    private void Draw()
    {
        _im.ClearSurfaces();
        var origin = Origin();
        Vector3 G(System.Numerics.Vector3 p) => Coords.ToGodot(p - origin);
        foreach (int part in (int[])[1, 2, 3])
            foreach (var group in _trails.Values.GroupBy(t => t.Style))
            {
                var mat = Material($"{Styles[group.Key]}_{part}01");
                if (mat is null) continue;
                bool begun = false;
                foreach (var t in group)
                {
                    int n = t.Tris.Count;
                    for (int i = 0; i + 1 < n; i++)
                    {
                        // the newest segment the "start" frame (_101), the oldest the end (_301), the rest _201
                        int which = i == n - 2 ? 1 : i == 0 ? 3 : 2;
                        if (which != part) continue;
                        if (!begun) { _im.SurfaceBegin(Mesh.PrimitiveType.Triangles, mat); begun = true; }
                        var older = t.Tris[i];
                        var newer = t.Tris[i + 1];
                        foreach (var (a, b) in Sides)
                        {
                            Vector3 v1 = G(older[a]), v2 = G(older[b]), v3 = G(newer[b]), v4 = G(newer[a]);
                            Quad(v1, new Vector2(0, 1), v2, new Vector2(0, 0), v3, new Vector2(1, 0), v4, new Vector2(1, 1));
                        }
                    }
                }
                if (begun) _im.SurfaceEnd();
            }
    }

    private void Quad(Vector3 a, Vector2 ua, Vector3 b, Vector2 ub, Vector3 c, Vector2 uc, Vector3 d, Vector2 ud)
    {
        foreach (var (p, uv) in new[] { (a, ua), (b, ub), (c, uc), (a, ua), (c, uc), (d, ud) })
        {
            _im.SurfaceSetUV(uv);
            _im.SurfaceAddVertex(p);
        }
    }

    /// <summary>The modern look draws its own smoke for the rest: the trails go.</summary>
    public void Clear()
    {
        foreach (var t in _trails.Values) t.Light?.QueueFree();
        _trails.Clear();
        _im.ClearSurfaces();
    }

    /// <summary>
    /// The modern look's cluster bomb trail: one camera-facing ribbon through each trail's stamped points
    /// (one draw call for all of them), and the bomb's radius-25 light, flickering warm.
    /// </summary>
    private void DrawFire()
    {
        _im.ClearSurfaces();
        _t = Time.GetTicksMsec() / 1000f;
        _fire ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/cluster_fire.gdshader") };
        _fire.SetShaderParameter("daylight", SceneLight);
        var origin = Origin();
        var cam = GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        bool begun = false;
        int seed = 0;
        foreach (var t in _trails.Values)
        {
            seed++;
            int n = t.Pts.Count;
            if (n < 2) continue;
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++) pts[i] = Coords.ToGodot(t.Pts[n - 1 - i] - origin); // 0 = the bomb
            var along = new float[n];
            for (int i = 1; i < n; i++) along[i] = along[i - 1] + (pts[i] - pts[i - 1]).Length();
            float total = MathF.Max(along[n - 1], 0.01f);
            // a full ring is about 40 m: the ribbon is 1.4 m across at its widest
            const float Half = 1.0f;
            if (!begun) { _im.SurfaceBegin(Mesh.PrimitiveType.Triangles, _fire); begun = true; }
            var col = new Color((seed * 0.6180339f) % 1f, 0, 0);
            var side = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var tan = pts[Math.Max(i - 1, 0)] - pts[Math.Min(i + 1, n - 1)];
                var sd = tan.Cross(cam - pts[i]);
                side[i] = sd.LengthSquared() > 1e-8f ? sd.Normalized() * Half : (i > 0 ? side[i - 1] : Vector3.Right * Half);
            }
            void V(int i, int k)
            {
                _im.SurfaceSetColor(col);
                _im.SurfaceSetUV(new Vector2(along[i] / total, k));
                _im.SurfaceSetUV2(new Vector2(along[i], 0));
                _im.SurfaceAddVertex(pts[i] + side[i] * (k == 0 ? -1f : 1f));
            }
            for (int i = 0; i + 1 < n; i++)
            {
                V(i, 0); V(i, 1); V(i + 1, 0);
                V(i, 1); V(i + 1, 1); V(i + 1, 0);
            }
        }
        if (begun) _im.SurfaceEnd();
        foreach (var t in _trails.Values)
        {
            if (t.Pts.Count == 0) continue;
            if (t.Light is null) { t.Light = new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.25f), OmniRange = 25f, ShadowEnabled = false }; AddChild(t.Light); }
            float fl = 0.8f + 0.2f * MathF.Sin(_t * 47f + t.Roll * 5f) + 0.12f * MathF.Sin(_t * 113f + t.Roll);
            t.Light.LightEnergy = (t.Gone ? 0.3f : 1.6f) * fl;
            t.Light.Position = Coords.ToGodot(t.Pts[^1] - origin);
        }
    }
}
