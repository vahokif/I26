using Godot;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The 1997 look's shadows, as the original draws them (it has no shadow map): every car and
/// scenery object whose centre lies within 50 units of the camera (view
/// depth) casts its light-facing faces along the light onto a plane laid on
/// the ground under it — a ray from its centre along the light, up to 128
/// units, to the ground; the plane through three heights 2 units around
/// the hit, and the projection is built along the
/// light. The 3D card filled the faces in palette colour 0xfe, flat and
/// opaque (T01: 27,27,27 over ground of every shade); the software renderer
/// darkened what lay under them through its shadow table, and both renderers
/// of the 1997 look do that (drop_shadow_1997.gdshader, SoftwarePalette.Shadow).
/// The original works per scene node; this casts each mesh as one.
/// </summary>
public partial class DropShadows : Node3D
{
    /// <summary>The ground height at a game-space (x, z).</summary>
    public required Func<float, float, float> Ground { get; init; }
    /// <summary>What a caster stands on: the height at a game-space (x, z) of the surface under a thing at height y (a bridge deck, else the terrain); the terrain's Ground without it.</summary>
    public Func<float, float, float, float>? Surface { get; init; }
    /// <summary>The floating origin: Godot space is game space minus it (Z mirrored).</summary>
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    /// <summary>The meshes that cast now: the cars' parts and the scenery's (Static: it stays where it is).</summary>
    public required Func<IEnumerable<(Mesh Mesh, Transform3D Xf, bool Static)>> Casters { get; init; }
    /// <summary>Toward the light, in Godot space (i76_light_dir).</summary>
    public required Func<Vector3> Light { get; init; }
    /// <summary>The 1997 look at night: the light a caster centred at a global point takes its shadow from, null for the moon (NightLights).</summary>
    public Func<Vector3, Vector3?>? LightAt { get; init; }
    /// <summary>The modern look's feathered shadow: how dark where all layers meet (0..1).</summary>
    public float Strength { get; set; } = 0.55f;

    private const int Layers = 10;
    private const float Spread = 0.09f;
    private bool _soft;
    private ShaderMaterial? _soft0;
    private readonly ShaderMaterial[] _softMats = new ShaderMaterial[Layers];

    /// <summary>The modern look's soft layers instead of the 1997's one flat polygon.</summary>
    public bool Soft
    {
        get => _soft;
        set { _soft = value; foreach (var mi in _pool) mi.MaterialOverride = _soft ? _soft0 : _mat; }
    }

    private void SetStrength(float s)
    {
        float a = 1f - Mathf.Pow(1f - Mathf.Clamp(s, 0f, 0.95f), 1f / Layers);
        foreach (var m in _softMats) m.SetShaderParameter("layer_alpha", a);
        _far.SetShaderParameter("layer_alpha", Mathf.Clamp(s, 0f, 0.95f));
    }

    private const float Reach = 50f;
    /// <summary>How far the modern look casts (m): Shadow Detail Low a moderate distance, High far; never past the camera's far plane. Shadows beyond <see cref="FeatherReach"/> are one flat layer and small casters drop out (<see cref="MinAngle"/>).</summary>
    public float SoftReach { get; set; } = 130f;
    private const float FeatherReach = 120f, MinAngle = 0.012f;
    private float _fadeAt = -1;
    private ShaderMaterial _far = null!;
    private readonly List<MeshInstance3D> _pool = [];
    private ShaderMaterial _mat = null!;

    private static Shader[]? _layerShaders;
    private static Shader? _farShader;

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/drop_shadow_1997.gdshader") };
        var code = GD.Load<Shader>("res://Shaders/drop_shadow_soft.gdshader").Code;
        // the shaders (one per layer, as the stencil reference is in the code) are made once for the run: a new Shader is compiled again, tens of ms for the set
        _layerShaders ??= new Shader[Layers];
        _farShader ??= new Shader { Code = code };
        for (int i = 0; i < Layers; i++)
        {
            // a Vogel disc: layer i at radius sqrt((i + .5) / n), turned by the golden angle
            float r = Mathf.Sqrt((i + 0.5f) / Layers), a = i * 2.39996f;
            // the stencil reference is a literal in the shader, so one shader per layer
            var m = new ShaderMaterial { Shader = _layerShaders[i] ??= new Shader { Code = code.Replace("compare_not_equal, 1;", $"compare_not_equal, {i + 1};") } };
            m.SetShaderParameter("jitter", new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            m.SetShaderParameter("spread", Spread);
            // the layer's own stencil reference: one pixel is covered once per layer
            // layer-major draw order across all casters (priority first), so one layer's stencil value covers a pixel once for every part
            m.RenderPriority = -100 + i;
            if (i > 0) _softMats[i - 1].NextPass = m;
            _softMats[i] = m;
        }
        _far = new ShaderMaterial { Shader = _farShader };   // one layer, with the first one's stencil reference
        _far.SetShaderParameter("jitter", Vector2.Zero);
        _far.SetShaderParameter("spread", Spread);
        _far.RenderPriority = -100;
        _soft0 = _softMats[0];
        SetStrength(Strength);
    }

    private float GroundY(Vector3 godot)
    {
        var o = Origin();
        var g = Coords.ToGame(godot) + o;
        return Ground(g.X, g.Z) - o.Y;
    }

    public override void _Process(double delta)
    {
        int used = 0;
        var origin = Origin();
        var light = Light().Normalized();
        if (origin != _drapedOrigin || light.DistanceTo(_drapedLight) > 0.002f) { _draped.Clear(); _drapedOrigin = origin; _drapedLight = light; }   // the floating origin or the light moved
        if (_soft) SetStrength(Strength);
        if (Visible && GetViewport().GetCamera3D() is { } cam)
        {
            var eye = cam.GlobalPosition;
            var fwd = -cam.GlobalBasis.Z;
            var l = Light().Normalized();
            float reach = _soft ? Mathf.Min(SoftReach, cam.Far) : Reach;
            if (_soft && reach != _fadeAt) SetFade(_fadeAt = reach);
            _builds = 0;
            foreach (var (mesh, xf, isStatic) in Casters())
            {
                bool far = false;
                var centre = xf * mesh.GetAabb().GetCenter();
                if (_soft)
                {
                    // the modern look: how near the caster's bounds come to the eye, whichever way it looks. The original's view depth of the centre
                    // (below) dropped a bridge's or a building's shadow whenever the camera turned and the centre fell behind it, though the shadow lay in view (#91)
                    var world = xf * mesh.GetAabb();
                    var closest = new Vector3(Mathf.Clamp(eye.X, world.Position.X, world.End.X), Mathf.Clamp(eye.Y, world.Position.Y, world.End.Y), Mathf.Clamp(eye.Z, world.Position.Z, world.End.Z));
                    float dist = closest.DistanceTo(eye);
                    if (dist >= reach) continue;
                    far = dist > FeatherReach;
                    if (far && world.Size.Length() < dist * MinAngle) continue;   // too small on the screen to read from here
                }
                else
                {
                    float depth = (centre - eye).Dot(fwd);
                    if (depth <= 0 || depth >= reach) continue;
                }
                if (_soft && isStatic && _builds >= MaxBuilds && !DrapedReady(mesh, xf)) continue;   // built a few a frame, so the first sight of a town does not stall
                var cl = !_soft && LightAt?.Invoke(centre) is { } near ? near : l;
                if (Plane(centre, cl, !isStatic) is not { } plane) continue;
                if (used == _pool.Count)
                {
                    var mi = new MeshInstance3D { MaterialOverride = _soft ? _soft0 : _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    AddChild(mi);
                    _pool.Add(mi);
                }
                var s = _pool[used++];
                var draped = isStatic && cl == l ? Draped(mesh, xf, l) : null;   // under a car's own light at night the sun's drape would be wrong: the plane
                var want = !_soft ? _mat : far ? _far : _soft0;
                if (s.MaterialOverride != want) s.MaterialOverride = want;
                s.Mesh = draped ?? mesh;
                s.SetInstanceShaderParameter("use_ground", draped is null ? 0f : 1f);
                s.SetInstanceShaderParameter("all_faces", isStatic ? 0f : 1f);
                s.GlobalTransform = xf;
                s.SetInstanceShaderParameter("plane", plane);
                if (!_soft) s.SetInstanceShaderParameter("caster_light", new Vector4(cl.X, cl.Y, cl.Z, 1f));
                s.Visible = true;
            }
        }
        for (int i = used; i < _pool.Count; i++) _pool[i].Visible = false;
    }

    private const int MaxBuilds = 3;
    private int _builds;

    private void SetFade(float reach)
    {
        foreach (var m in _softMats) { m.SetShaderParameter("fade_from", reach * 0.7f); m.SetShaderParameter("fade_to", reach); }
        _far.SetShaderParameter("fade_from", reach * 0.7f); _far.SetShaderParameter("fade_to", reach);
    }

    private bool DrapedReady(Mesh mesh, Transform3D xf)
    {
        var aabb = mesh.GetAabb();
        return Mathf.Max(aabb.Size.X, aabb.Size.Z) < 8f || _draped.ContainsKey((mesh, xf.Origin));
    }

    private readonly Dictionary<(Mesh, Vector3), ArrayMesh?> _draped = [];
    private System.Numerics.Vector3 _drapedOrigin;
    private Vector3 _drapedLight;

    /// <summary>
    /// A static caster of any size (a ramp, a bridge, a building on a slope) as a copy of its mesh whose each vertex
    /// carries where its own ray along the light meets the terrain, and its height above the terrain beneath it
    /// (CUSTOM0 xyz, w): the plane through one point is a poor stand-in over a whole ramp or bridge, where the
    /// ground falls away or rises beside it (the shadow floated in the air, or stood up the bank), and what lies
    /// under the ground casts nothing. Both looks: the 1997 look's one plane through the centre of a bridge over a canyon stood
    /// up the canyon wall as a black slab (Seminole). Cached per mesh and place; rebuilt when the light moves.
    /// </summary>
    private ArrayMesh? Draped(Mesh mesh, Transform3D xf, Vector3 l)
    {
        var aabb = mesh.GetAabb();
        if (Mathf.Max(aabb.Size.X, aabb.Size.Z) < 8f) return null;
        var key = (mesh, xf.Origin);
        if (_draped.TryGetValue(key, out var hit)) return hit;
        if (_draped.Count > 2048) _draped.Clear();
        _builds++;
        var result = new ArrayMesh();
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(i) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = mesh.SurfaceGetArrays(i);
            if (arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is not { Length: > 0 } verts) continue;
            var norms = arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]>();
            var index = arrays[(int)Mesh.ArrayType.Index].As<int[]>();
            int tris = (index is { Length: > 0 } ? index.Length : verts.Length) / 3;
            // each triangle cut small, and each corner given its own point on the ground; a triangle whose corners fall either side of a cliff's rim
            // would hang across the gap as a sheet (the shadow floating under Seminole's bridge), so one whose sides do not follow the ground is left out
            var pv = new List<Vector3>(); var pn = new List<Vector3>(); var pc = new List<float>();
            for (int t = 0; t < tris; t++)
            {
                int i0 = index is { Length: > 0 } ? index[t * 3] : t * 3, i1 = index is { Length: > 0 } ? index[t * 3 + 1] : t * 3 + 1, i2 = index is { Length: > 0 } ? index[t * 3 + 2] : t * 3 + 2;
                Vector3 Nrm(int k) => norms is { Length: > 0 } ? norms[k] : Vector3.Up;
                foreach (var tri in ShadowDrape.Split((N(verts[i0]), N(Nrm(i0))), (N(verts[i1]), N(Nrm(i1))), (N(verts[i2]), N(Nrm(i2))), ShadowDrape.MaxEdge))
                {
                    var hits = new (System.Numerics.Vector3 P, float H)[3];
                    for (int k = 0; k < 3; k++) hits[k] = ShadowDrape.Of(N(xf * V(tri[k].P)), N(l), GroundN);
                    bool ok = true;
                    for (int k = 0; k < 3 && ok; k++)
                    {
                        var (a0, b0) = (hits[k], hits[(k + 1) % 3]);
                        if (a0.H > 0 && b0.H > 0 && System.Numerics.Vector3.Distance(a0.P, b0.P) > 1f) ok = ShadowDrape.Connected(a0.P, b0.P, GroundN);
                    }
                    if (!ok) continue;
                    for (int k = 0; k < 3; k++) { pv.Add(V(tri[k].P)); pn.Add(V(tri[k].N)); pc.Add(hits[k].P.X); pc.Add(hits[k].P.Y); pc.Add(hits[k].P.Z); pc.Add(hits[k].H); }
                }
            }
            if (pv.Count == 0) continue;
            var cut = new Godot.Collections.Array();
            cut.Resize((int)Mesh.ArrayType.Max);
            cut[(int)Mesh.ArrayType.Vertex] = pv.ToArray();
            cut[(int)Mesh.ArrayType.Normal] = pn.ToArray();
            cut[(int)Mesh.ArrayType.Custom0] = pc.ToArray();
            result.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, cut, (Mesh.ArrayFormat)((uint)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        }
        var r = result.GetSurfaceCount() > 0 ? result : null;
        _draped[key] = r;
        return r;
    }

    private float SurfaceN(float x, float y, float z)
    {
        var o = Origin();
        var g = Coords.ToGame(new Vector3(x, y, z)) + o;
        return (Surface?.Invoke(g.X, g.Y, g.Z) ?? Ground(g.X, g.Z)) - o.Y;
    }

    private static System.Numerics.Vector3 N(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    private float GroundN(float x, float z) => GroundY(new Vector3(x, 0, z));

    /// <summary>ShadowDrape.Plane in Godot space; a car's plane may be a bridge deck's (the structure query is too dear for every prop in reach, which stand on the terrain).</summary>
    private Vector4? Plane(Vector3 centre, Vector3 l, bool onStructures) =>
        ShadowDrape.Plane(N(centre), N(l), onStructures ? SurfaceN : (x, _, z) => GroundN(x, z)) is { } p ? new Vector4(p.X, p.Y, p.Z, p.W) : null;
}
