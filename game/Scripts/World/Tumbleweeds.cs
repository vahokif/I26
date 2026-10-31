using Godot;
using I76.Formats.Vegetation;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The modern look's rolling tumbleweeds (docs/modern-vegetation.md, Tumbleweeds; a deviation): a few Russian thistles
/// blowing round the camera where the biome has them (<see cref="Biome.Tumbleweeds"/>), moved by
/// <see cref="TumbleweedField"/> and drawn with the plants' material (one MultiMesh a mesh variant, the burst pieces in
/// them too) and a soft shade under each. Visual only: nothing the simulation or a network game reads. A child of the
/// <see cref="VegetationNode"/>, so Vegetation Off and the 1997 look hide it with the plants.
/// </summary>
public partial class TumbleweedNode : Node3D
{
    private const int Variants = 3;
    private const int Capacity = 16 + TumbleweedField.MaxFragments;

    private TumbleweedField _field = null!;
    private Ground _ground = null!;
    private float _rate;
    private readonly MultiMeshInstance3D[] _nodes = new MultiMeshInstance3D[Variants];
    private readonly Vector3[] _centre = new Vector3[Variants];
    private readonly float[][] _buf = new float[Variants][];
    private MultiMeshInstance3D _shade = null!;
    private readonly float[] _shadeBuf = new float[Capacity * 16];
    private ShaderMaterial _material = null!, _shadeMaterial = null!;
    // the cars' last positions (game frame), by node id, for their velocity
    private readonly Dictionary<ulong, System.Numerics.Vector3> _carsBefore = [];
    private readonly Dictionary<ulong, System.Numerics.Vector3> _carsNow = [];
    private readonly TumbleCar[] _cars = new TumbleCar[CabClip.Max];
    private int _carCount;

    /// <summary>I76_TUMBLEWEEDS=N: this many whatever the biome and the setting say (0 none); unset: the biome's rate at the setting.</summary>
    private static readonly int? Override = int.TryParse(System.Environment.GetEnvironmentVariable("I76_TUMBLEWEEDS"), out int n) ? Math.Max(0, n) : null;

    /// <summary>I76_TUMBLEWEED_AHEAD=D[,S]: the first one put D metres ahead and S to the right of the player's car as it drives off (of the camera, with no car moving), a second or more in (a capture hook: one to drive into, or to watch).</summary>
    private static readonly float[]? Ahead = System.Environment.GetEnvironmentVariable("I76_TUMBLEWEED_AHEAD") is { Length: > 0 } a
        ? [.. a.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)), 0f] : null;
    private bool _placed;

    public string Stats => $"tumbleweeds: {_field.Count} (rate {_rate:0.##}), {_field.Bursts} burst, {_field.Snags} caught on shrubs, {_field.Respawns} started again, {_drawn} drawn, {_cost * 1000:0} us a frame, nearest {_nearest:0} m";
    private int _drawn;
    private float _nearest;

    /// <summary>
    /// The tumbleweeds of a mission: their meshes (<see cref="SpeciesCatalog.RollingTumbleweed"/>, three variants), the plants'
    /// material made to roll (<paramref name="material"/>, already carrying the atlas and the ground's tint), the land.
    /// </summary>
    public static TumbleweedNode Create(VegRegion region, SurfaceTerrain terrain, VegMask mask, Func<VegetationPlacer?> placer, float lowLevel, ulong seed, ShaderMaterial material, Func<PlantMesh, ArrayMesh> toMesh)
    {
        var node = new TumbleweedNode { Name = "Tumbleweeds", ProcessPriority = 1001 };   // after CabClip has the cars
        node._rate = region.Biome.Tumbleweeds * region.Density;
        var radius = new float[Variants];
        var meshes = new PlantMesh[Variants];
        Parallel.For(0, Variants, v => meshes[v] = SpeciesCatalog.RollingTumbleweed(VegHash.Mix(VegHash.Of("tumbleweed-rolling"), (ulong)v)));
        node._material = material;
        material.SetShaderParameter("rolling", true);
        material.SetShaderParameter("tier_in", Vector2.Zero);
        material.SetShaderParameter("tier_out", Vector2.Zero);
        for (int v = 0; v < Variants; v++)
        {
            var (mn, mx) = meshes[v].Bounds();
            var c = (mn + mx) * 0.5f;
            node._centre[v] = new Vector3(c.X, c.Y, c.Z);
            // the ball's radius: the tangle painted on the cards fills a circle a little inside them (the atlas cell's 118 of 128)
            radius[v] = 0.8f;
            node._buf[v] = new float[Capacity * 16];
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = toMesh(meshes[v]), InstanceCount = Capacity, VisibleInstanceCount = 0 };
            node._nodes[v] = new MultiMeshInstance3D
            {
                Multimesh = mm, MaterialOverride = material, Layers = TerrainNode.TerrainLayer, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = $"Tumbleweed{v}",
            };
            node.AddChild(node._nodes[v]);
        }
        node._shadeMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/vegetation_shade.gdshader") };
        var quad = new PlaneMesh { Size = new Vector2(2, 2) };
        node._shade = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = quad, InstanceCount = Capacity, VisibleInstanceCount = 0 },
            MaterialOverride = node._shadeMaterial, Layers = TerrainNode.TerrainLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled, Name = "TumbleweedShade",
        };
        node.AddChild(node._shade);
        node._ground = new Ground(terrain.Cached(), mask, placer, region, lowLevel);
        node._field = new TumbleweedField(node._ground, seed, radius);
        return node;
    }

    /// <summary>Vegetation's level: Off none, Low and High their count (or I76_TUMBLEWEEDS), range and fade; High's cast sun shadows.</summary>
    public void SetLevel(int level)
    {
        int count = level <= 0 ? 0 : Override ?? TumbleweedField.CountFor(_rate, level);
        _field.Count = Math.Min(count, 16);
        _field.Range = TumbleweedField.RangeFor(level);
        _material.SetShaderParameter("fade_start", _field.Range - 45f);
        _material.SetShaderParameter("fade_end", _field.Range - 12f);
        _shadeMaterial.SetShaderParameter("fade_start", 60f);
        _shadeMaterial.SetShaderParameter("fade_end", 90f);
        foreach (var n in _nodes) n.CastShadow = level >= 2 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        _ground.Reset();
        Visible = _field.Count > 0;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || _field.Count == 0 || GetViewport()?.GetCamera3D() is not { } cam || GetParent<Node3D>() is not { } veg) return;
        // the game frame: the vegetation node sits in the world root, the game frame mirrored and shifted
        var l = veg.ToLocal(cam.GlobalPosition);
        var eye = new System.Numerics.Vector3(l.X, l.Y, -l.Z);
        // the gust front in the plants' shader runs on Godot's global coordinates: line the tumbleweeds' up with it
        var o = veg.GlobalPosition;
        _field.FrontOffset = o.X * Wind.Direction.X - o.Z * Wind.Direction.Y;
        float dt = (float)delta;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _field.Step(dt, eye, Cars(veg, dt));
        if (Ahead is { } ahead && !_placed && _field.Time >= 1f)
        {
            // the capture hook: the first one ahead of the player's car along its motion (of the camera, with no car moving), on the ground
            var f = veg.ToLocal(cam.GlobalPosition - cam.GlobalTransform.Basis.Z) - l;
            var from = new System.Numerics.Vector2(eye.X, eye.Z);
            var dir = new System.Numerics.Vector2(f.X, -f.Z);
            if (_carCount > 0 && new System.Numerics.Vector2(_cars[0].Velocity.X, _cars[0].Velocity.Z) is { } cv && cv.Length() > 2f)
            {
                from = new System.Numerics.Vector2(_cars[0].Position.X, _cars[0].Position.Z);
                dir = cv;
            }
            if (dir.LengthSquared() > 1e-6f && (_carCount == 0 || dir != new System.Numerics.Vector2(f.X, -f.Z) || _field.Time >= 3f))
            {
                _placed = true;
                var fw = System.Numerics.Vector2.Normalize(dir);
                // the right in the game frame (mirrored): forward turned the other way
                var right = new System.Numerics.Vector2(fw.Y, -fw.X);
                _field.Place(0, from + fw * ahead[0] + right * ahead[1]);
            }
        }
        Draw(eye);
        _cost = _cost * 0.98 + System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 0.02;
    }

    private double _cost;

    /// <summary>The cars of this frame in the game frame, their velocity from where they were last frame.</summary>
    private ReadOnlySpan<TumbleCar> Cars(Node3D veg, float dt)
    {
        _carsNow.Clear();
        int n = 0;
        for (int i = 0; i < CabClip.Count; i++)
        {
            var l = veg.ToLocal(CabClip.Positions[i]);
            var g = new System.Numerics.Vector3(l.X, l.Y, -l.Z);
            var vel = System.Numerics.Vector3.Zero;
            if (dt > 0 && _carsBefore.TryGetValue(CabClip.Ids[i], out var before) && System.Numerics.Vector3.Distance(before, g) < 30f) vel = (g - before) / dt;
            _carsNow[CabClip.Ids[i]] = g;
            _cars[n++] = new TumbleCar(g, vel);
        }
        _carsBefore.Clear();
        foreach (var (k, v) in _carsNow) _carsBefore[k] = v;
        _carCount = n;
        return _cars.AsSpan(0, n);
    }

    /// <summary>Every tumbleweed and piece into its variant's MultiMesh, and a shade under each, relative to an anchor near the eye.</summary>
    private void Draw(System.Numerics.Vector3 eye)
    {
        // the anchor moves in 256 m steps, so the instances' offsets stay small (float precision far out on the map)
        var anchor = new Vector3(MathF.Round(eye.X / 256f) * 256f, 0, MathF.Round(eye.Z / 256f) * 256f);
        var at = Coords.ToGodot(anchor.X, 0, anchor.Z);
        var counts = new int[Variants];
        int shades = 0;
        void Add(in Tumble t)
        {
            if (!t.Active || t.Fade <= 0.001f) return;
            int v = t.Variant;
            var buf = _buf[v];
            int k = counts[v]++ * 16;
            var q = t.Orientation;
            float s = t.Scale;
            // the rotation's matrix (drawn frame, column vectors), scaled
            float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z, xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z, wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;
            float r00 = 1 - 2 * (yy + zz), r01 = 2 * (xy - wz), r02 = 2 * (xz + wy);
            float r10 = 2 * (xy + wz), r11 = 1 - 2 * (xx + zz), r12 = 2 * (yz - wx);
            float r20 = 2 * (xz - wy), r21 = 2 * (yz + wx), r22 = 1 - 2 * (xx + yy);
            // a burst piece is a torn-off clump, not a small ball: squashed along its own axes
            float sx = s, sy = s, sz = s;
            if (t.Life > 0) { sy *= 0.4f + 0.35f * (t.Phase * 7.3f % 1f); sz *= 0.7f; }
            // the mesh's centre on the ball's: origin = p - R S c
            var c = _centre[v];
            float px = t.Position.X - anchor.X, py = t.Position.Y, pz = -(t.Position.Z - anchor.Z);
            float ox = px - (r00 * sx * c.X + r01 * sy * c.Y + r02 * sz * c.Z);
            float oy = py - (r10 * sx * c.X + r11 * sy * c.Y + r12 * sz * c.Z);
            float oz = pz - (r20 * sx * c.X + r21 * sy * c.Y + r22 * sz * c.Z);
            buf[k] = r00 * sx; buf[k + 1] = r01 * sy; buf[k + 2] = r02 * sz; buf[k + 3] = ox;
            buf[k + 4] = r10 * sx; buf[k + 5] = r11 * sy; buf[k + 6] = r12 * sz; buf[k + 7] = oy;
            buf[k + 8] = r20 * sx; buf[k + 9] = r21 * sy; buf[k + 10] = r22 * sz; buf[k + 11] = oz;
            buf[k + 12] = t.Fade; buf[k + 13] = t.Phase; buf[k + 14] = t.Radius * 2; buf[k + 15] = t.Phase * 2 - 1;
            // its shade: darker and tighter on the ground, fainter and wider as it hops
            float above = MathF.Max(0, t.Position.Y - t.Radius - t.GroundY);
            float shade = 0.5f * t.Fade * MathF.Exp(-2.2f * above);
            if (shade < 0.02f) return;
            float r = t.Radius * (1.25f + 0.35f * above);
            int j = shades++ * 16;
            float gx = t.Slope.X, gz = t.Slope.Y;
            _shadeBuf[j] = r; _shadeBuf[j + 1] = 0; _shadeBuf[j + 2] = 0; _shadeBuf[j + 3] = px;
            _shadeBuf[j + 4] = gx * r; _shadeBuf[j + 5] = 1; _shadeBuf[j + 6] = -gz * r; _shadeBuf[j + 7] = t.GroundY + 0.03f;
            _shadeBuf[j + 8] = 0; _shadeBuf[j + 9] = 0; _shadeBuf[j + 10] = r; _shadeBuf[j + 11] = pz;
            _shadeBuf[j + 12] = shade; _shadeBuf[j + 13] = 0; _shadeBuf[j + 14] = 0; _shadeBuf[j + 15] = 0;
        }
        _nearest = float.MaxValue;
        foreach (ref readonly var t in _field.Weeds)
        {
            Add(t);
            if (t.Active) _nearest = MathF.Min(_nearest, new System.Numerics.Vector2(t.Position.X - eye.X, t.Position.Z - eye.Z).Length());
        }
        foreach (ref readonly var t in _field.Fragments) Add(t);
        var bounds = new Aabb(new Vector3(-_field.Range - 300, eye.Y - 400, -_field.Range - 300), new Vector3(2 * _field.Range + 600, 800, 2 * _field.Range + 600));
        for (int v = 0; v < Variants; v++)
        {
            var node = _nodes[v];
            node.Position = at;
            var mm = node.Multimesh;
            if (counts[v] > 0) mm.Buffer = _buf[v];
            mm.VisibleInstanceCount = counts[v];
            mm.CustomAabb = bounds;
        }
        _drawn = counts.Sum();
        _shade.Position = at;
        if (shades > 0) _shade.Multimesh.Buffer = _shadeBuf;
        _shade.Multimesh.VisibleInstanceCount = shades;
        _shade.Multimesh.CustomAabb = bounds;
    }

    /// <summary>The land as the tumbleweeds see it: the drawn ground, the objects' footprints, the shrubs (the placer's, cell by cell, kept), the river bottom kept clear.</summary>
    private sealed class Ground(SurfaceTerrain terrain, VegMask mask, Func<VegetationPlacer?> placer, VegRegion region, float lowLevel) : ITumbleGround
    {
        private readonly Dictionary<(int, int), (System.Numerics.Vector2 Centre, float Radius)?> _shrubs = [];

        public void Reset() => _shrubs.Clear();

        public bool Has(float x, float z) => terrain.Has(x, z);
        public float Height(float x, float z) => terrain.Height(x, z);
        public float Rough(float x, float z) => terrain.Ground(x, z);
        public bool Blocked(System.Numerics.Vector2 p, float radius) => mask.Blocked(p, 0, radius, -1e6f, discs: false);

        public bool Suitable(float x, float z) => !region.LowGround || terrain.Height(x, z) - lowLevel > region.Biome.RiparianBand;

        public (System.Numerics.Vector2 Centre, float Radius)? Shrub(System.Numerics.Vector2 p)
        {
            if (placer() is not { } pl || pl.Biome.Shrubs.Length == 0) return null;
            float cell = pl.CellSize(VegLayer.Shrub);
            int ci = (int)MathF.Floor(p.X / cell), cj = (int)MathF.Floor(p.Y / cell);
            (System.Numerics.Vector2, float)? best = null;
            float bestD = float.MaxValue;
            if (_shrubs.Count > 4096) _shrubs.Clear();
            for (int j = cj - 1; j <= cj + 1; j++)
                for (int i = ci - 1; i <= ci + 1; i++)
                {
                    if (!_shrubs.TryGetValue((i, j), out var s))
                    {
                        s = pl.PlaceCell(VegLayer.Shrub, i, j) is { } plant
                            ? (new System.Numerics.Vector2(plant.Position.X, plant.Position.Z), pl.Species[plant.Species].Radius * plant.Scale)
                            : null;
                        _shrubs[(i, j)] = s;
                    }
                    if (s is not { } sh) continue;
                    float d = System.Numerics.Vector2.Distance(sh.Centre, p) - sh.Radius;
                    if (d < bestD) { bestD = d; best = sh; }
                }
            return best;
        }
    }
}
