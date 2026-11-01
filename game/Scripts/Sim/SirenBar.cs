using Godot;
using I76.Formats.Assembly;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's police light bar (the Sheriff's CP*1SIRN part; its faces are the animated CP11LBF1.TMT, three frames at
/// 4 a second: red bright, blue bright, both dim, drawn unlit and always running, whatever setSiren says, which only plays
/// the siren sound). While the car is in view the bar's lamps take emissive red and blue that follow those frames, a flare
/// stands at each lamp, and at night the bar throws a red and a blue light on the ground and the car round it. Only the
/// nearest few bars get real lights (<see cref="MaxLit"/>, no shadows); the rest keep the glow and the flares.
/// Presentation only: nothing here reads or writes the sim. The 1997 look draws the bar's own texture, as the original does.
/// </summary>
public sealed partial class SirenBar : Node3D
{
    /// <summary>How many bars cast light at once (the nearest to the camera); I76_SIREN_LIGHTS overrides it.</summary>
    public static readonly int MaxLit = int.TryParse(System.Environment.GetEnvironmentVariable("I76_SIREN_LIGHTS"), out int n) ? n : 4;
    private const float LightRange = 24f, LightFar = 180f, RedEnergy = 11f, BlueEnergy = 14f, GlowLevel = 2.6f;

    private const string GlowSource = """
        shader_type spatial;
        render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
        uniform sampler2D tex_red : source_color, filter_linear_mipmap;
        uniform sampler2D tex_blue : source_color, filter_linear_mipmap;
        uniform vec3 tint_red : source_color = vec3(1.0, 0.1, 0.04);
        uniform vec3 tint_blue : source_color = vec3(0.12, 0.3, 1.0);
        instance uniform float level_red = 0.0;
        instance uniform float level_blue = 0.0;
        void fragment() {
            vec4 r = texture(tex_red, UV);
            vec4 b = texture(tex_blue, UV);
            float mr = clamp((r.r - max(r.g, r.b)) * 2.5, 0.0, 1.0) * (0.45 + 0.55 * min(r.r * 1.3, 1.0));
            float mb = clamp((b.b - b.r) * 2.5, 0.0, 1.0) * (0.45 + 0.55 * min(b.b * 1.3, 1.0));
            ALBEDO = tint_red * mr * level_red + tint_blue * mb * level_blue;
        }
        """;
    private static Shader? _shader;
    private static readonly List<SirenBar> All = [];

    private readonly float[] _redLevel, _blueLevel;
    private readonly float _fps;
    private readonly MeshInstance3D _glow;
    private readonly OmniLight3D? _redLight, _blueLight;
    private readonly MeshInstance3D _redFlare, _blueFlare;
    private float _dist2 = float.MaxValue;

    private SirenBar(Mesh glowMesh, Texture2D red, Texture2D blue, float[] redLevel, float[] blueLevel, float fps, Vector3 redAt, Vector3 blueAt, bool night)
    {
        Name = "SirenBar";
        (_redLevel, _blueLevel, _fps) = (redLevel, blueLevel, fps);
        _shader ??= new Shader { Code = GlowSource };
        var mat = new ShaderMaterial { Shader = _shader, RenderPriority = 1 };
        mat.SetShaderParameter("tex_red", red);
        mat.SetShaderParameter("tex_blue", blue);
        var mesh = (ArrayMesh)glowMesh;
        for (int i = 0; i < mesh.GetSurfaceCount(); i++) mesh.SurfaceSetMaterial(i, mat);
        _glow = new MeshInstance3D { Mesh = mesh, Name = "SirenGlow", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_glow);
        _redFlare = Flare(redAt, new Color(1f, 0.12f, 0.05f));
        _blueFlare = Flare(blueAt, new Color(0.15f, 0.35f, 1f));
        if (night)
        {
            _redLight = new OmniLight3D { Position = redAt + new Vector3(0, 1.1f, 0), OmniRange = LightRange, LightEnergy = 0, LightColor = new Color(1f, 0.1f, 0.04f),
                ShadowEnabled = false, Visible = false, Name = "SirenRed", OmniAttenuation = 0.65f };
            _blueLight = new OmniLight3D { Position = blueAt + new Vector3(0, 1.1f, 0), OmniRange = LightRange, LightEnergy = 0, LightColor = new Color(0.1f, 0.25f, 1f),
                ShadowEnabled = false, Visible = false, Name = "SirenBlue", OmniAttenuation = 0.65f };
            AddChild(_redLight); AddChild(_blueLight);
        }
        All.Add(this);
    }

    public override void _ExitTree() => All.Remove(this);

    private static ShaderMaterial? _redFlareMat, _blueFlareMat;
    private static QuadMesh? _quad;

    private MeshInstance3D Flare(Vector3 at, Color tint)
    {
        ref var cached = ref (tint.R > tint.B ? ref _redFlareMat : ref _blueFlareMat);
        if (cached is null)
        {
            cached = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/headlight_glare.gdshader") };
            cached.SetShaderParameter("tint", tint);
            cached.SetShaderParameter("strength", 1.5f);
            cached.SetShaderParameter("sharpness", 1.0f);
            cached.SetShaderParameter("wide", 1.0f);     // a bar shines every way
            cached.SetShaderParameter("size_near", 0.32f);
            cached.SetShaderParameter("size_far", 0.5f);
            cached.SetShaderParameter("streak_amount", 0.35f);
            cached.SetShaderParameter("halo_amount", 0.5f);
            cached.SetShaderParameter("day_floor", 0.55f);
        }
        _quad ??= new QuadMesh { Size = new Vector2(2, 2) };
        var m = new MeshInstance3D { Mesh = _quad, MaterialOverride = cached, Position = at + new Vector3(0, 0.12f, 0), Name = "SirenFlare",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 8, Visible = false };
        AddChild(m);
        return m;
    }

    /// <summary>
    /// The bar of an assembled car (null for a car without one): its lit faces re-drawn under the emissive shader, and the two
    /// lamps' places found from where each face's texture is red or blue.
    /// </summary>
    public static SirenBar? Build(Assets.MeshBuilder meshes, AssembledModel model, bool night)
    {
        // the texture scan and the mesh are the same for every bar of a car, so they are kept
        var cache = Made.GetValue(meshes, _ => new Dictionary<string, Analysis?>(StringComparer.OrdinalIgnoreCase));    // per mission: its materials and palette
        if (!cache.TryGetValue(model.Definition, out var made)) cache[model.Definition] = made = Analyse(meshes, model);
        return made is null ? null : new SirenBar(made.Mesh, made.Red, made.Blue, made.RedLevel, made.BlueLevel, made.Fps, made.RedAt, made.BlueAt, night);
    }

    private sealed record Analysis(Mesh Mesh, Texture2D Red, Texture2D Blue, float[] RedLevel, float[] BlueLevel, float Fps, Vector3 RedAt, Vector3 BlueAt);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assets.MeshBuilder, Dictionary<string, Analysis?>> Made = new();

    private static Analysis? Analyse(Assets.MeshBuilder meshes, AssembledModel model)
    {
        if (MaxLit < 0) return null;   // I76_SIREN_LIGHTS=-1: no modern bar at all (for A/B timing)
        bool Bar(ModelPart p) => p.NodeName.Length >= 8 && p.NodeName[4..8].Equals("SIRN", StringComparison.OrdinalIgnoreCase);
        bool Lit(I76.Formats.Geo.GeoFace f) => f.Texture.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase);
        var faces = model.Parts.Where(Bar).SelectMany(p => p.Mesh.Faces.Where(Lit).Select(f => (p, f))).ToList();
        if (faces.Count == 0) return null;
        if (meshes.AnimatedTextures(faces[0].f.Texture) is not { } anim || anim.Frames.Length < 2) return null;
        // which frame lights which lamp: the red (blue) the frame's texels hold, against the dimmest frame's
        var imgs = anim.Frames.Select(t => t.GetImage()).ToArray();
        if (imgs.Any(i => i is null)) return null;
        for (int i = 0; i < imgs.Length; i++) if (imgs[i].IsCompressed()) imgs[i].Decompress();
        float Score(Image im, bool red)
        {
            float sum = 0;
            for (int y = 0; y < im.GetHeight(); y++)
                for (int x = 0; x < im.GetWidth(); x++)
                {
                    var c = im.GetPixel(x, y);
                    sum += red ? Mathf.Max(0, c.R - Mathf.Max(c.G, c.B)) : Mathf.Max(0, c.B - c.R);
                }
            return sum;
        }
        float[] rs = imgs.Select(i => Score(i, true)).ToArray(), bs = imgs.Select(i => Score(i, false)).ToArray();
        float[] Norm(float[] a) { float lo = a.Min(), hi = a.Max(); return a.Select(v => hi - lo < 1e-3f ? 0f : (v - lo) / (hi - lo)).ToArray(); }
        float[] rl = Norm(rs), bl = Norm(bs);
        int rf = Array.IndexOf(rs, rs.Max()), bf = Array.IndexOf(bs, bs.Max());
        Vector3 redAt = default, blueAt = default; int nr = 0, nb = 0;
        foreach (var (p, f) in faces)
        {
            if (f.Vertices.Length < 3) continue;
            var c = Vector3.Zero; float u = 0, v = 0;
            foreach (var fv in f.Vertices)
            {
                if ((uint)fv.Index >= p.Mesh.Vertices.Length) continue;
                c += Coords.ToGodot(p.ToModel.Apply(p.Mesh.Vertices[fv.Index]));
                u += fv.U; v += fv.V;
            }
            c /= f.Vertices.Length; u /= f.Vertices.Length; v /= f.Vertices.Length;
            int px = Mathf.Clamp((int)(u * imgs[rf].GetWidth()), 0, imgs[rf].GetWidth() - 1), py = Mathf.Clamp((int)(v * imgs[rf].GetHeight()), 0, imgs[rf].GetHeight() - 1);
            var cr = imgs[rf].GetPixel(px, py); var cb = imgs[bf].GetPixel(Mathf.Clamp(px, 0, imgs[bf].GetWidth() - 1), Mathf.Clamp(py, 0, imgs[bf].GetHeight() - 1));
            if (cr.R - Mathf.Max(cr.G, cr.B) > 0.1f) { redAt += c; nr++; }
            else if (cb.B - cb.R > 0.1f) { blueAt += c; nb++; }
        }
        if (nr == 0 || nb == 0) return null;
        redAt /= nr; blueAt /= nb;
        var mesh = meshes.Build(model, wheels: false, include: Bar, skipFace: (p, f) => !Lit(f));
        return mesh is null ? null : new Analysis(mesh, anim.Frames[rf], anim.Frames[bf], rl, bl, anim.Fps, redAt, blueAt);
    }

    /// <summary>Once a frame, from the car's Place: <paramref name="shown"/> is the body being drawn, <paramref name="alive"/> the car not a wreck, <paramref name="modern"/> the look.</summary>
    public void Update(bool shown, bool alive, bool modern)
    {
        bool on = alive && modern && IsVisibleInTree();   // the lights (the player's own car lights the road from the cockpit too)
        _glow.Visible = _redFlare.Visible = _blueFlare.Visible = on && shown;
        // the frame the textures show now (the same clock): the lamps and the lights follow it
        int f = (int)(Assets.MaterialLibrary.Clock * _fps) % _redLevel.Length;
        float lr = _redLevel[f], lb = _blueLevel[f];
        _glow.SetInstanceShaderParameter("level_red", lr * GlowLevel);
        _glow.SetInstanceShaderParameter("level_blue", lb * GlowLevel);
        _redFlare.SetInstanceShaderParameter("level", lr);
        _blueFlare.SetInstanceShaderParameter("level", lb);
        if (_redLight is null || _blueLight is null) return;
        var cam = GetViewport()?.GetCamera3D();
        _dist2 = on && cam is not null ? cam.GlobalPosition.DistanceSquaredTo(GlobalPosition) : float.MaxValue;
        // the lights: only for the nearest few bars
        bool lit = on && _dist2 < LightFar * LightFar && Rank() < MaxLit;
        _redLight.Visible = lit && lr > 0.02f;
        _blueLight.Visible = lit && lb > 0.02f;
        _redLight.LightEnergy = RedEnergy * lr;
        _blueLight.LightEnergy = BlueEnergy * lb;
    }

    /// <summary>How many bars are nearer the camera than this one (distances as of their last update).</summary>
    private int Rank()
    {
        int r = 0;
        foreach (var b in All) if (b != this && b._dist2 < _dist2) r++;
        return r;
    }
}
