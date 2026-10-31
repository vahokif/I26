using Godot;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's fire on a burning scenery wreck (akwreck2: T15, T05, M14, S03, S05): the original draws it as xfr1
/// flame sprite planes inside the model, which the modern look leaves out (ScenerySpawner), so this burns in their place,
/// sized to the planes' extent. A few upright billboards spread over the footprint, each a procedural fire
/// (static_flame.gdshader: tongues of scrolling noise, tapering, a temperature ramp, soot rising off the tips of the
/// tallest so DamageSmoke's column grows out of it), in one mesh: one draw. Embers (WreckFire's) and, for the few
/// nearest the camera, a flickering light on the ground. The original's props burn forever, so it never dies down.
/// DamageSmoke owns and drives these: only the ones near the camera exist as visible nodes.
/// </summary>
public partial class StaticFire : Node3D
{
    private readonly GpuParticles3D _embers;
    private readonly OmniLight3D _light;
    private readonly float _phase = GD.Randf() * 100f, _k;

    /// <summary>Beyond this the fire is hidden (DamageSmoke); the shader fades it out over the last fifth.</summary>
    public const float Reach = 130f;

    public StaticFire(float height, float width)
    {
        WreckFire.Shared.Init();
        height = Mathf.Clamp(height, 1.5f, 9f);
        width = Mathf.Clamp(width, 1.5f, 9f);
        _k = height / 2.6f;
        AddChild(new MeshInstance3D
        {
            Mesh = FireMesh(height, width), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Flames",
            CustomAabb = new Aabb(new Vector3(-width, -1, -width), new Vector3(2 * width, 2.2f * height, 2 * width)),
        });
        float half = Mathf.Clamp(width * 0.3f, 0.4f, 3f);
        var m = (ParticleProcessMaterial)WreckFire.Shared.EmberProc.Duplicate();
        m.EmissionBoxExtents = new Vector3(half, 0.3f, half);
        m.EmissionShapeOffset = new Vector3(0, 0.6f, 0);
        m.InitialVelocityMin *= 0.8f * _k; m.InitialVelocityMax *= 0.8f * _k;
        AddChild(_embers = new GpuParticles3D
        {
            ProcessMaterial = m, DrawPass1 = WreckFire.Shared.EmberMesh, Amount = 24, Lifetime = 2.2, LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Embers",
            VisibilityAabb = new Aabb(new Vector3(-12, -3, -12), new Vector3(24, 8 + 8 * _k, 24)), Emitting = true,
        });
        // low over the wreck, so it pools on the ground around it
        _light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.5f, 0.18f), OmniRange = 9f + 3f * _k, OmniAttenuation = 1.3f, ShadowEnabled = false,
            Position = new Vector3(0, 0.9f * _k, 0), Name = "Glow", Visible = false,
        };
        AddChild(_light);
    }

    private static ShaderMaterial? _mat;

    /// <summary>The mission's light (DamageSmoke.SceneLight): the soot darker at night, the halo stronger.</summary>
    public static float SceneLight { set => _mat?.SetShaderParameter("scene_light", value); }
    private static readonly System.Collections.Generic.Dictionary<(float, float), Mesh> Meshes = [];

    /// <summary>
    /// The billboards, as one mesh: each quad's four corners carry its base centre (VERTEX; the shader turns the quad
    /// to the camera about world up), its corner (UV, y up), its width and height (UV2) and its seed and soot share
    /// (COLOR). A tall central body that carries the soot, two flanking ones and lower licks around the footprint,
    /// drawn in that order (the soot first, so the flames lay over its foot).
    /// </summary>
    private static Mesh FireMesh(float h, float w)
    {
        if (Meshes.TryGetValue((h, w), out var cached)) return cached;
        if (_mat is null)
        {
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/static_flame.gdshader") };
            _mat.SetShaderParameter("noise_tex", DamageSmoke.Noise);
            _mat.SetShaderParameter("reach", Reach);
        }
        float r = w * 0.5f;
        // x, z (of the half width), width (of the width), flame height (of the height), soot (the quad that much taller than its flame)
        (float X, float Z, float W, float H, float Soot)[] quads =
        [
            (0.00f, 0.05f, 0.80f, 1.00f, 0.75f),
            (-0.42f, -0.15f, 0.55f, 0.72f, 0f),
            (0.45f, 0.10f, 0.55f, 0.80f, 0f),
            (0.10f, -0.50f, 0.50f, 0.58f, 0f),
            (-0.15f, 0.50f, 0.50f, 0.62f, 0f),
            (0.60f, -0.45f, 0.35f, 0.40f, 0f),
            (-0.62f, 0.40f, 0.35f, 0.36f, 0f),
        ];
        var verts = new Vector3[quads.Length * 4];
        var uv = new Vector2[verts.Length];
        var uv2 = new Vector2[verts.Length];
        var col = new Color[verts.Length];
        var idx = new int[quads.Length * 6];
        for (int q = 0; q < quads.Length; q++)
        {
            var (x, z, qw, qh, soot) = quads[q];
            for (int c = 0; c < 4; c++)
            {
                int v = q * 4 + c;
                verts[v] = new Vector3(x * r, 0, z * r);
                uv[v] = new Vector2(c & 1, c >> 1);
                uv2[v] = new Vector2(qw * w, qh * h * (1f + soot));
                col[v] = new Color((q * 0.381966f + 0.13f) % 1f, soot / (1f + soot), 0, 1);
            }
            int b = q * 4, o = q * 6;
            idx[o] = b; idx[o + 1] = b + 1; idx[o + 2] = b + 2; idx[o + 3] = b + 2; idx[o + 4] = b + 1; idx[o + 5] = b + 3;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Color] = col;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _mat);
        return Meshes[(h, w)] = mesh;
    }

    /// <summary>
    /// Burning or not (out of reach, or the 1997 look); lit: the flickering light, for the few nearest the camera;
    /// sparks: the embers, only near enough to see (each system is a compute dispatch a frame).
    /// </summary>
    public void Set(bool on, bool lit, bool sparks = false)
    {
        Visible = on;
        _embers.Emitting = on && sparks;
        if (!on) return;
        float t = (float)Time.GetTicksMsec() / 1000f + _phase;
        float flick = 0.72f + 0.12f * Mathf.Sin(t * 13.1f) + 0.09f * Mathf.Sin(t * 7.3f + 1.3f) + 0.07f * Mathf.Sin(t * 23.7f + 4.1f);
        _light.LightEnergy = 3.4f * flick;
        _light.Visible = lit;
    }
}
