using Godot;

namespace I76.Game.Sim;

/// <summary>
/// A mark lying on the ground (an oil slick, a fire's scorch) in the modern look: a Decal on Forward+, which
/// conforms to whatever is under it. The Compatibility renderer draws no decals, so there the mark is a mesh
/// laid over the drawn ground (<see cref="Drape"/>), a grid whose vertices take the ground's height. The decal's
/// API, the part the marks use: a box (its Size) placed by Place, the textures, Modulate and the emission.
/// </summary>
public partial class GroundMark : Node3D
{
    /// <summary>The ground's height (Godot y) under a Godot-space point whose y is the mark's own level.</summary>
    public required Func<Vector3, float> GroundAt { get; init; }
    public bool Draped { get; init; } = I76.Game.Boot.RenderBackend.Compatibility;
    /// <summary>The decal box: 2 m over the mark's level as before (a deck overhead stays clean) and 4 m under it, so a slope or bank inside the mark is still reached.</summary>
    public const float BoxHeight = 6f, BoxLift = -1f;
    private const float Spacing = 0.5f, Lift = 0.05f;

    private Decal? _decal;
    private MeshInstance3D? _mesh;
    private StandardMaterial3D? _mat;
    private Vector3 _size = Vector3.One;

    public float NormalFade { get; init; }
    public float FadeBegin { get; init; } = 90f;
    public float FadeLength { get; init; } = 30f;

    public override void _Ready()
    {
        if (!Draped)
        {
            _decal = new Decal
            {
                AlbedoMix = 1f, NormalFade = NormalFade, UpperFade = 0f, LowerFade = 0.1f,
                DistanceFadeEnabled = true, DistanceFadeBegin = FadeBegin, DistanceFadeLength = FadeLength,
                CullMask = 0xFFFFF & ~CarNode.VehicleLayer & ~CarNode.CockpitLayer & ~SmokeLayer.QuadLayer,   // the ground, not the cars on it
                EmissionEnergy = 0f,
            };
            AddChild(_decal);
            return;
        }
        _mat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha, DistanceFadeMinDistance = FadeBegin + FadeLength, DistanceFadeMaxDistance = FadeBegin,
            RenderPriority = -1, Roughness = 1f, Metallic = 0f,
        };
        _mesh = new MeshInstance3D { MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_mesh);
    }

    public Texture2D? TextureAlbedo
    {
        set { if (_decal is not null) _decal.TextureAlbedo = value; else if (_mat is not null) _mat.AlbedoTexture = value; }
    }
    public Texture2D? TextureOrm
    {
        set
        {
            // the draped mesh takes the oil's ORM as constants (its gloss): a material without an ORM texture reads as
            // white there, fully metallic, and a scorch came out pale
            if (_decal is not null) _decal.TextureOrm = value;
            else if (_mat is not null) { _mat.Roughness = value is null ? 1f : 0.1f; _mat.Metallic = value is null ? 0f : 0.35f; }
        }
    }
    public Texture2D? TextureNormal
    {
        set
        {
            if (_decal is not null) _decal.TextureNormal = value;
            else if (_mat is not null) { _mat.NormalEnabled = value is not null; _mat.NormalTexture = value; }
        }
    }
    public Texture2D? TextureEmission
    {
        set
        {
            if (_decal is not null) _decal.TextureEmission = value;
            // the draped mesh glows a flat ember colour instead: Compatibility drew the glow texture white over the whole
            // scorch, hiding the flames in it
            else if (_mat is not null) { _mat.EmissionEnabled = value is not null; _mat.Emission = new Color(0.5f, 0.12f, 0.015f); }
        }
    }
    public float EmissionEnergy
    {
        set { if (_decal is not null) _decal.EmissionEnergy = value; else if (_mat is not null) _mat.EmissionEnergyMultiplier = value * 0.3f; }
    }
    public Color Modulate
    {
        set { if (_decal is not null) _decal.Modulate = value; else if (_mat is not null) _mat.AlbedoColor = value; }
    }

    /// <summary>Places the mark: <paramref name="basis"/>'s X across, Z along (unit, Y up), centred on <paramref name="at"/>, the mark's level.</summary>
    public void Place(Basis basis, Vector3 at, float width, float length)
    {
        _size = new Vector3(width, BoxHeight, length);
        if (_decal is not null)
        {
            _decal.Size = _size;
            _decal.GlobalTransform = new Transform3D(basis, at + Vector3.Up * BoxLift);
            return;
        }
        if (_mesh is null) return;
        _mesh.GlobalTransform = new Transform3D(Basis.Identity, at);
        _mesh.Mesh = Drape(basis.X, basis.Z, at, width, length, Spacing, Lift, GroundAt, null);
    }

    /// <summary>
    /// A width x length rectangle on the ground, centred on <paramref name="at"/> (Godot space), across
    /// <paramref name="across"/> and along <paramref name="along"/>: a grid about <paramref name="spacing"/> apart whose
    /// vertices stand <paramref name="lift"/> over <paramref name="groundAt"/>, local to <paramref name="at"/>. UVs run
    /// 0..1 across and along unless <paramref name="uv"/> maps a grid point's (s, t) in 0..1 to them.
    /// </summary>
    public static ArrayMesh Drape(Vector3 across, Vector3 along, Vector3 at, float width, float length, float spacing, float lift,
        Func<Vector3, float> groundAt, Func<float, float, Vector2>? uv)
    {
        across.Y = 0; along.Y = 0;
        across = across.Normalized(); along = along.Normalized();
        int nx = Math.Max(1, (int)MathF.Ceiling(width / spacing)), nz = Math.Max(1, (int)MathF.Ceiling(length / spacing));
        var verts = new Vector3[(nx + 1) * (nz + 1)];
        var uvs = new Vector2[verts.Length];
        var norms = new Vector3[verts.Length];
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                float s = (float)i / nx, t = (float)j / nz;
                var p = at + across * ((s - 0.5f) * width) + along * ((t - 0.5f) * length);
                p.Y = groundAt(p) + lift;
                int k = j * (nx + 1) + i;
                verts[k] = p - at;
                uvs[k] = uv?.Invoke(s, t) ?? new Vector2(s, t);
            }
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                var dx = verts[j * (nx + 1) + Math.Min(i + 1, nx)] - verts[j * (nx + 1) + Math.Max(i - 1, 0)];
                var dz = verts[Math.Min(j + 1, nz) * (nx + 1) + i] - verts[Math.Max(j - 1, 0) * (nx + 1) + i];
                var n = dz.Cross(dx).Normalized();
                norms[j * (nx + 1) + i] = n.Y < 0 ? -n : n;
            }
        var idx = new int[nx * nz * 6];
        int c = 0;
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, d = a + nx + 1, e = d + 1;
                idx[c++] = a; idx[c++] = b; idx[c++] = e;
                idx[c++] = a; idx[c++] = e; idx[c++] = d;
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
