using Godot;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// The garage bay the car stands in: a concrete floor with a hazard-striped turntable at its middle, block walls, a ribbed roller
/// door behind, a ceiling with strip lights, and a few props (tyre stacks, oil drums, a workbench). All of it is built here out of
/// primitives and small generated textures (no game data), drawn through the game's object shaders so the room follows the look
/// as the car does: Godot's lighting in the modern look, the 1997 vertex light through the palette's ramp in the 1997 look.
/// </summary>
public sealed class GarageRoom
{
    /// <summary>Where the boombox stands, on the bench (the radio's voice is here).</summary>
    public static readonly Vector3 Boombox = new(-HalfX + 0.55f, 1.01f, -4.2f);
    public const float HalfX = 8f, HalfZ = 9f, Height = 5.4f, TurntableRadius = 3.1f, TurntableHeight = 0.14f;

    private readonly List<(ShaderMaterial Mat, bool Lamp)> _materials = [];
    private static Shader? _modern, _faithful;
    public Node3D Root { get; } = new() { Name = "Room" };
    /// <summary>The disc the car stands on: it turns, the car with it. Its top is y = <see cref="TurntableHeight"/>.</summary>
    public Node3D Turntable { get; } = new() { Name = "Turntable" };
    public List<Light3D> Lamps { get; } = [];

    public GarageRoom()
    {
        _modern ??= GD.Load<Shader>("res://Shaders/object_back.gdshader");
        _faithful ??= GD.Load<Shader>("res://Shaders/object_back_1997.gdshader");
        Build();
    }

    /// <summary>The look: every material's shader.</summary>
    public void SetLook(bool mode1997)
    {
        foreach (var (m, _) in _materials) m.Shader = mode1997 ? _faithful : _modern;
    }

    // ---- textures: small, tileable, made from hashes ----

    private static float Hash(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }

    /// <summary>Periodic value noise: the lattice repeats every <paramref name="period"/> cells.</summary>
    private static float Noise(float x, float y, int period, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        int P(int v) => ((v % period) + period) % period;
        float a = Hash(P(x0), P(y0), seed), b = Hash(P(x0 + 1), P(y0), seed), c = Hash(P(x0), P(y0 + 1), seed), d = Hash(P(x0 + 1), P(y0 + 1), seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static float Fbm(float u, float v, int cells, int seed) =>
        0.55f * Noise(u * cells, v * cells, cells, seed) + 0.3f * Noise(u * cells * 2, v * cells * 2, cells * 2, seed + 1) + 0.15f * Noise(u * cells * 4, v * cells * 4, cells * 4, seed + 2);

    private static Image Tex(int size, Func<float, float, int, int, Color> px)
    {
        var img = Image.CreateEmpty(size, size, true, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) img.SetPixel(x, y, px((x + 0.5f) / size, (y + 0.5f) / size, x, y));
        img.GenerateMipmaps();
        return img;
    }

    /// <summary>Every texture of the room by name: the same pixels each time, so they are made once a run.</summary>
    private static readonly (string Name, Func<Image> Make)[] Images =
    [
        ("concrete", Concrete), ("blockA", () => Blocks(new Color(0.62f, 0.64f, 0.60f))), ("blockB", () => Blocks(new Color(0.42f, 0.47f, 0.50f))),
        ("door", RollerDoor), ("hazard", Hazard), ("steel", () => Metal(new Color(0.34f, 0.35f, 0.37f), 0.6f)),
        ("darkSteel", () => Metal(new Color(0.12f, 0.12f, 0.13f), 0.4f)), ("ceiling", () => Plain(new Color(0.16f, 0.16f, 0.17f))),
        ("lamp", () => Plain(new Color(1f, 0.97f, 0.88f))), ("rubber", Rubber), ("wood", Wood),
        ("red", () => Metal(new Color(0.60f, 0.10f, 0.08f), 0.3f)), ("blue", () => Metal(new Color(0.10f, 0.22f, 0.50f), 0.3f)),
        ("yellow", () => Plain(new Color(0.85f, 0.65f, 0.08f))), ("cabinet", () => Metal(new Color(0.07f, 0.07f, 0.075f), 0.5f)),
        ("cone", () => Metal(new Color(0.20f, 0.20f, 0.21f), 0.8f)), ("chrome", () => Metal(new Color(0.65f, 0.66f, 0.68f), 0.3f)),
    ];

    private static System.Threading.Tasks.Task<Dictionary<string, Image>>? _images;
    private static readonly Dictionary<string, ImageTexture> _textures = [];

    /// <summary>Starts making the room's pixels on a worker thread (once a run): the first visit no longer waits half a second for them.</summary>
    public static void Prepare() => _images ??= System.Threading.Tasks.Task.Run(() => Images.ToDictionary(i => i.Name, i => i.Make()));

    private static ImageTexture T(string name)
    {
        if (_textures.TryGetValue(name, out var t)) return t;
        Prepare();
        return _textures[name] = ImageTexture.CreateFromImage(_images!.Result[name]);
    }

    private static Color Grey(float v, float r = 1f, float g = 1f, float b = 1f) => new(Mathf.Clamp(v * r, 0, 1), Mathf.Clamp(v * g, 0, 1), Mathf.Clamp(v * b, 0, 1));

    private static Image Concrete() => Tex(256, (u, v, x, y) =>
    {
        float n = Fbm(u, v, 8, 3), speck = Hash(x, y, 9) > 0.985f ? 0.12f : 0f;
        float joint = MathF.Min(MathF.Min(u, 1 - u), MathF.Min(v, 1 - v)) * 256f < 1.5f ? 0.55f : 1f;   // the slab's expansion joint along its edge
        float oil = Fbm(u, v, 3, 21) > 0.68f ? 0.72f : 1f;
        return Grey((0.30f + 0.16f * n + speck) * joint * oil, 1f, 0.99f, 0.96f);
    });

    private static Image Blocks(Color paint) => Tex(256, (u, v, x, y) =>
    {
        // painted cinder block: courses 32 px high, 64 wide, alternate courses offset, mortar lines, the paint chipped
        int course = y / 32, bx = (x + (course % 2) * 32) % 64, by = y % 32;
        bool mortar = by < 2 || bx < 2;
        float n = Fbm(u, v, 8, 5), chip = Fbm(u, v, 12, 33) > 0.72f ? 1f : 0f;
        var c = paint * (0.82f + 0.3f * n);
        if (chip > 0) c = c.Lerp(new Color(0.45f, 0.43f, 0.4f), 0.55f);
        if (mortar) c = c.Lerp(new Color(0.25f, 0.25f, 0.24f), 0.7f);
        return new Color(c.R, c.G, c.B);
    });

    private static Image RollerDoor() => Tex(256, (u, v, x, y) =>
    {
        // ribbed slats 16 px high: a lit upper edge, a shadowed lower one, a faint scrape, the paint a dull green-grey
        int s = y % 16;
        float rib = s < 2 ? 0.55f : s < 5 ? 1.12f : s > 13 ? 0.8f : 1f;
        float n = Fbm(u, v, 6, 8);
        float wear = Hash(x / 3, y, 4) > 0.97f ? 0.82f : 1f;
        return Grey((0.30f + 0.10f * n) * rib * wear, 0.82f, 0.95f, 0.88f);
    });

    private static Image Hazard() => Tex(128, (u, v, x, y) =>
    {
        bool yellow = ((x + y) / 16) % 2 == 0;
        float n = Fbm(u, v, 4, 11);
        return yellow ? new Color(0.82f * (0.75f + 0.25f * n), 0.62f * (0.75f + 0.25f * n), 0.06f) : new Color(0.07f, 0.07f, 0.07f) * (0.8f + 0.4f * n);
    });

    private static Image Metal(Color c, float grain) => Tex(128, (u, v, x, y) =>
    {
        float n = Fbm(u, v, 4, 14) * 0.6f + Hash(x, y, 2) * 0.4f * grain;
        return new Color(c.R * (0.8f + 0.4f * n), c.G * (0.8f + 0.4f * n), c.B * (0.8f + 0.4f * n));
    });

    private static Image Plain(Color c) => Tex(8, (_, _, _, _) => c);

    private static Image Rubber() => Tex(64, (u, v, x, y) => Grey(0.10f + 0.04f * Hash(x, y, 6) + (y % 16 < 3 ? 0.03f : 0f)));

    private static Image Wood() => Tex(128, (u, v, x, y) =>
    {
        float grain = 0.5f + 0.5f * MathF.Sin((v * 9f + Fbm(u, v, 4, 40) * 3f) * MathF.PI * 2f);
        return new Color(0.42f + 0.1f * grain, 0.28f + 0.07f * grain, 0.15f + 0.04f * grain);
    });

    // ---- the shaders' materials ----

    private ShaderMaterial Mat(Texture2D tex, float roughness = 0.9f, float metallic = 0f, float specular = 0.3f, bool lamp = false)
    {
        var m = new ShaderMaterial { Shader = _modern };
        m.SetShaderParameter("albedo_tex", tex);
        m.SetShaderParameter("use_texture", true);
        m.SetShaderParameter("base_roughness", roughness);
        m.SetShaderParameter("base_metallic", metallic);
        m.SetShaderParameter("base_specular", specular);
        if (lamp) { m.SetShaderParameter("prelit", true); m.SetShaderParameter("albedo_cockpit", tex); }
        _materials.Add((m, lamp));
        return m;
    }

    // ---- geometry ----

    /// <summary>A quad of the given size facing <paramref name="normal"/> (the visible side), its texture repeated <paramref name="repeat"/> times.</summary>
    private static ArrayMesh Quad(Vector2 size, Vector2 repeat)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float hx = size.X / 2, hy = size.Y / 2;
        // in the quad's own frame it faces +Z
        Vector3[] p = [new(-hx, -hy, 0), new(hx, -hy, 0), new(hx, hy, 0), new(-hx, hy, 0)];
        Vector2[] uv = [new(0, repeat.Y), new(repeat.X, repeat.Y), new(repeat.X, 0), new(0, 0)];
        foreach (int i in new[] { 0, 2, 1, 0, 3, 2 })      // Godot's front faces are clockwise
        {
            st.SetNormal(Vector3.Back);
            st.SetUV(uv[i]);
            st.AddVertex(p[i]);
        }
        return st.Commit();
    }

    private MeshInstance3D Wall(Node3D parent, Vector3 at, Vector3 facing, Vector2 size, Vector2 repeat, Material mat, string name, bool shadow = false)
    {
        var mi = new MeshInstance3D { Mesh = Quad(size, repeat), MaterialOverride = mat, Name = name, CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
        var up = MathF.Abs(facing.Y) > 0.99f ? Vector3.Forward * MathF.Sign(facing.Y) * -1 : Vector3.Up;
        var z = facing.Normalized();
        var x = up.Cross(z).Normalized();
        var y = z.Cross(x);
        mi.Transform = new Transform3D(new Basis(x, y, z), at);
        parent.AddChild(mi);
        return mi;
    }

    private MeshInstance3D Solid(Node3D parent, Mesh mesh, Vector3 at, Material mat, string name, bool shadow = true)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = at, Name = name,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(mi);
        return mi;
    }

    private void Build()
    {
        var concrete = Mat(T("concrete"), roughness: 0.42f, specular: 0.5f);
        var blockA = Mat(T("blockA"), roughness: 0.95f);
        var blockB = Mat(T("blockB"), roughness: 0.95f);
        var door = Mat(T("door"), roughness: 0.55f, metallic: 0.5f);
        var hazard = Mat(T("hazard"), roughness: 0.6f);
        var steel = Mat(T("steel"), roughness: 0.4f, metallic: 0.8f);
        var darkSteel = Mat(T("darkSteel"), roughness: 0.5f, metallic: 0.7f);
        var ceiling = Mat(T("ceiling"), roughness: 1f);
        var lampMat = Mat(T("lamp"), lamp: true);
        var rubber = Mat(T("rubber"), roughness: 0.95f);
        var wood = Mat(T("wood"), roughness: 0.8f);
        var red = Mat(T("red"), roughness: 0.45f, metallic: 0.3f);
        var blue = Mat(T("blue"), roughness: 0.45f, metallic: 0.3f);
        var yellow = Mat(T("yellow"), roughness: 0.6f);

        // shell: floor, ceiling, four walls (each facing in)
        Wall(Root, new Vector3(0, 0, 0), Vector3.Up, new Vector2(HalfX * 2, HalfZ * 2), new Vector2(HalfX * 2 / 4, HalfZ * 2 / 4), concrete, "Floor");
        Wall(Root, new Vector3(0, Height, 0), Vector3.Down, new Vector2(HalfX * 2, HalfZ * 2), Vector2.One, ceiling, "Ceiling");
        Wall(Root, new Vector3(0, Height / 2, -HalfZ), Vector3.Back, new Vector2(HalfX * 2, Height), new Vector2(HalfX * 2 / 4, Height / 4), blockA, "BackWall");
        Wall(Root, new Vector3(0, Height / 2, HalfZ), Vector3.Forward, new Vector2(HalfX * 2, Height), new Vector2(HalfX * 2 / 4, Height / 4), blockA, "FrontWall");
        Wall(Root, new Vector3(-HalfX, Height / 2, 0), Vector3.Right, new Vector2(HalfZ * 2, Height), new Vector2(HalfZ * 2 / 4, Height / 4), blockB, "LeftWall");
        Wall(Root, new Vector3(HalfX, Height / 2, 0), Vector3.Left, new Vector2(HalfZ * 2, Height), new Vector2(HalfZ * 2 / 4, Height / 4), blockB, "RightWall");
        // the roller door in the back wall, a frame round it and a hazard sill
        Wall(Root, new Vector3(0, 1.85f, -HalfZ + 0.04f), Vector3.Back, new Vector2(6.4f, 3.7f), new Vector2(2f, 3.7f / 3.2f), door, "Door");
        Solid(Root, new BoxMesh { Size = new Vector3(6.8f, 0.22f, 0.3f) }, new Vector3(0, 3.78f, -HalfZ + 0.15f), steel, "DoorBox");
        Solid(Root, new BoxMesh { Size = new Vector3(0.22f, 3.8f, 0.3f) }, new Vector3(-3.4f, 1.9f, -HalfZ + 0.15f), steel, "DoorJambL");
        Solid(Root, new BoxMesh { Size = new Vector3(0.22f, 3.8f, 0.3f) }, new Vector3(3.4f, 1.9f, -HalfZ + 0.15f), steel, "DoorJambR");
        Wall(Root, new Vector3(0, 0.01f, -HalfZ + 1.0f), Vector3.Up, new Vector2(7f, 0.4f), new Vector2(14f, 0.8f), hazard, "Sill");
        // a hazard ring round the turntable and a painted bay on the floor
        Solid(Root, new CylinderMesh { TopRadius = TurntableRadius + 0.85f, BottomRadius = TurntableRadius + 0.85f, Height = 0.012f, RadialSegments = 64 }, new Vector3(0, 0.006f, 0), hazard, "Ring", shadow: false);
        // wainscot and skirting: a dark band along the lower walls
        foreach (var (at, size, rot) in new (Vector3, Vector3, float)[] { (new(0, 0.5f, -HalfZ + 0.03f), new(HalfX * 2, 1f, 0.06f), 0), (new(0, 0.5f, HalfZ - 0.03f), new(HalfX * 2, 1f, 0.06f), 0),
                                                                         (new(-HalfX + 0.03f, 0.5f, 0), new(0.06f, 1f, HalfZ * 2), 0), (new(HalfX - 0.03f, 0.5f, 0), new(0.06f, 1f, HalfZ * 2), 0) })
            Solid(Root, new BoxMesh { Size = size }, at, darkSteel, "Wainscot", shadow: false);
        // steel ceiling beams and the strip lights between them
        for (int i = -2; i <= 2; i++)
            Solid(Root, new BoxMesh { Size = new Vector3(HalfX * 2, 0.3f, 0.18f) }, new Vector3(0, Height - 0.15f, i * 3.6f), steel, "Beam", shadow: false);
        foreach (float x in new[] { -4.5f, 0f, 4.5f })
            foreach (float z in new[] { -5.4f, -1.8f, 1.8f, 5.4f })
            {
                Solid(Root, new BoxMesh { Size = new Vector3(0.22f, 0.07f, 3.0f) }, new Vector3(x, Height - 0.4f, z), lampMat, "Strip", shadow: false);
                Solid(Root, new BoxMesh { Size = new Vector3(0.3f, 0.05f, 3.1f) }, new Vector3(x, Height - 0.34f, z), darkSteel, "StripHousing", shadow: false);
            }

        // the turntable: a dark steel disc with a yellow rim and a light-grey centre plate, standing a hand high off the floor
        Root.AddChild(Turntable);
        Solid(Turntable, new CylinderMesh { TopRadius = TurntableRadius, BottomRadius = TurntableRadius, Height = TurntableHeight, RadialSegments = 72 }, new Vector3(0, TurntableHeight / 2, 0), darkSteel, "Disc");
        Solid(Turntable, new CylinderMesh { TopRadius = TurntableRadius, BottomRadius = TurntableRadius, Height = 0.02f, RadialSegments = 72 }, new Vector3(0, TurntableHeight + 0.005f, 0), steel, "Plate", shadow: false);
        Solid(Turntable, new CylinderMesh { TopRadius = TurntableRadius + 0.02f, BottomRadius = TurntableRadius + 0.02f, Height = 0.05f, RadialSegments = 72 }, new Vector3(0, TurntableHeight - 0.02f, 0), yellow, "Rim", shadow: false);
        // tread plates: eight bars across the disc
        for (int i = 0; i < 8; i++)
        {
            var bar = Solid(Turntable, new BoxMesh { Size = new Vector3(0.12f, 0.012f, TurntableRadius * 1.7f) }, new Vector3(0, TurntableHeight + 0.018f, 0), darkSteel, "Bar", shadow: false);
            bar.RotationDegrees = new Vector3(0, i * 22.5f, 0);
        }

        // props: tyre stacks and drums against the right wall, a workbench on the left, a toolbox
        for (int s = 0; s < 3; s++)
        {
            float z = -5f + s * 1.25f;
            for (int k = 0; k < 4 + s % 2; k++)
            {
                var t = Solid(Root, new TorusMesh { InnerRadius = 0.2f, OuterRadius = 0.42f, Rings = 24, RingSegments = 12 }, new Vector3(HalfX - 0.75f, 0.14f + k * 0.26f, z), rubber, "Tyre");
                t.RotationDegrees = new Vector3(0, s * 20 + k * 11, 0);
            }
        }
        for (int d = 0; d < 4; d++)
        {
            var m = d % 2 == 0 ? red : blue;
            Solid(Root, new CylinderMesh { TopRadius = 0.29f, BottomRadius = 0.29f, Height = 0.9f, RadialSegments = 24 }, new Vector3(HalfX - 0.6f - (d / 2) * 0.62f, 0.45f, 1.5f + (d % 2) * 0.64f), m, "Drum");
        }
        Solid(Root, new BoxMesh { Size = new Vector3(0.8f, 0.95f, 3.2f) }, new Vector3(-HalfX + 0.5f, 0.475f, -3.4f), wood, "Bench");
        Solid(Root, new BoxMesh { Size = new Vector3(0.9f, 0.06f, 3.3f) }, new Vector3(-HalfX + 0.5f, 0.98f, -3.4f), steel, "BenchTop");
        Solid(Root, new BoxMesh { Size = new Vector3(0.36f, 0.22f, 0.7f) }, new Vector3(-HalfX + 0.5f, 1.12f, -3.0f), red, "Toolbox");
        Solid(Root, new BoxMesh { Size = new Vector3(0.5f, 1.5f, 1.1f) }, new Vector3(-HalfX + 0.4f, 0.75f, 0.6f), red, "ToolChest");
        // shelving over the bench
        for (int sh = 0; sh < 3; sh++)
            Solid(Root, new BoxMesh { Size = new Vector3(0.45f, 0.05f, 3.2f) }, new Vector3(-HalfX + 0.3f, 1.7f + sh * 0.7f, -3.4f), steel, "Shelf", shadow: false);
        for (int b = 0; b < 6; b++)
            Solid(Root, new BoxMesh { Size = new Vector3(0.3f, 0.32f, 0.45f) }, new Vector3(-HalfX + 0.32f, 1.9f + (b % 3) * 0.7f, -4.6f + b * 0.55f), b % 2 == 0 ? yellow : blue, "Crate", shadow: false);

        // the boombox on the bench: a black cabinet with two speaker cones, a handle, an antenna and a tuning dial
        var bb = new Node3D { Name = "Boombox", Position = Boombox };
        Root.AddChild(bb);
        var cabinet = Mat(T("cabinet"), roughness: 0.55f, metallic: 0.2f);
        var cone = Mat(T("cone"), roughness: 0.8f);
        var chrome = Mat(T("chrome"), roughness: 0.25f, metallic: 0.9f);
        Solid(bb, new BoxMesh { Size = new Vector3(0.22f, 0.26f, 0.62f) }, new Vector3(0, 0.13f, 0), cabinet, "Cabinet");
        foreach (float z in new[] { -0.17f, 0.17f })
        {
            var c = Solid(bb, new CylinderMesh { TopRadius = 0.085f, BottomRadius = 0.085f, Height = 0.02f, RadialSegments = 24 }, new Vector3(0.115f, 0.115f, z), cone, "Cone", shadow: false);
            c.RotationDegrees = new Vector3(0, 0, 90);
            var rim = Solid(bb, new TorusMesh { InnerRadius = 0.085f, OuterRadius = 0.1f, Rings = 24, RingSegments = 6 }, new Vector3(0.118f, 0.115f, z), chrome, "Rim", shadow: false);
            rim.RotationDegrees = new Vector3(0, 0, 90);
        }
        Solid(bb, new BoxMesh { Size = new Vector3(0.012f, 0.05f, 0.2f) }, new Vector3(0.115f, 0.225f, 0), chrome, "Dial", shadow: false);
        Solid(bb, new BoxMesh { Size = new Vector3(0.03f, 0.03f, 0.4f) }, new Vector3(0, 0.29f, 0), chrome, "Handle", shadow: false);
        var ant = Solid(bb, new CylinderMesh { TopRadius = 0.004f, BottomRadius = 0.006f, Height = 0.5f, RadialSegments = 6 }, new Vector3(-0.06f, 0.5f, 0.27f), chrome, "Antenna", shadow: false);
        ant.RotationDegrees = new Vector3(0, 0, -18);

        // the light: strip lamps' own spots (modern), the key from the front, a cool and a warm rim from behind
        foreach (float x in new[] { -4.5f, 0f, 4.5f })
            foreach (float z in new[] { -3.6f, 3.6f })
            {
                var spot = new SpotLight3D { Position = new Vector3(x, Height - 0.5f, z), RotationDegrees = new Vector3(-90, 0, 0), SpotAngle = 62, SpotRange = 9f, LightEnergy = 3.4f,
                    LightColor = new Color(1f, 0.95f, 0.86f), SpotAttenuation = 0.8f, ShadowEnabled = false };
                Root.AddChild(spot);
                Lamps.Add(spot);
            }
        var key = new SpotLight3D { Position = new Vector3(2.5f, 4.6f, 5.5f), SpotAngle = 38, SpotRange = 16, LightEnergy = 9f, LightColor = new Color(1f, 0.96f, 0.9f), ShadowEnabled = true, Name = "Key" };
        Root.AddChild(key);
        key.LookAtFromPosition(key.Position, new Vector3(0, 0.6f, 0));
        var cool = new OmniLight3D { Position = new Vector3(-5.5f, 1.8f, -6f), OmniRange = 12, LightEnergy = 2.4f, LightColor = new Color(0.45f, 0.62f, 1f), Name = "RimCool" };
        var warm = new OmniLight3D { Position = new Vector3(5.5f, 1.6f, -6f), OmniRange = 12, LightEnergy = 2.2f, LightColor = new Color(1f, 0.6f, 0.3f), Name = "RimWarm" };
        Root.AddChild(cool);
        Root.AddChild(warm);
        Lamps.AddRange([key, cool, warm]);
    }
}
