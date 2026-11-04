using Godot;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using SN = System.Numerics;

namespace I76.Game.Sim;

/// <summary>
/// Tyre skid marks. The original lays a strip behind each rear tyre while the car's
/// sliding flag is set (per rear wheel, every sim step): a point whenever the
/// tyre has moved 0.2 m, 31 points to a strip, 16 strips in a ring, a flat near-black polygon at 0.75 opacity,
/// gone 60 s after it began. 1997 mode keeps all of that, including its one-sided 0.2 m ribbon.
///
/// The modern look follows the same trigger (the sim's sliding flag, so the original physics and the modern body
/// alike, for every car) but samples the drawn wheel every frame, so a curve is smooth; the ribbon is 0.26 m
/// wide and centred on the tyre, soft at the edges and broken by noise, thinner at its start and end, dark on
/// asphalt and a lighter dusty brown on dirt (by the surface's grip under each point), and fades out over its
/// last seconds. A pool of strips is reused, oldest ended one first; a strip is rebuilt only when it changes.
/// Purely visual: nothing here reaches the sim, and no sim random number is drawn.
/// </summary>
public partial class SkidMarks : Node3D
{
    public required Dictionary<Vehicle, CarNode> Cars { get; init; }
    public required Func<SN.Vector3> Origin { get; init; }
    public IGround? Ground { get; init; }
    /// <summary>The mission's meshes: the 1997 ribbon is laid on the ground that look draws (<see cref="Drawn1997"/>).</summary>
    public I76.Game.World.MissionBuilder? Builder { get; init; }
    private I76.Game.World.Drawn1997? _drawn;
    public float SceneLight { get; init; } = 1f;

    // the original: 16 strips, 31 points, 0.2 m apart at least and wide, 60 s, flat colour palette 0xfe (16,16,16) at 0.75
    private const int Cap1997 = 16, Points1997 = 31;
    private const float Step1997 = 0.2f, Life1997 = 60f, Lift1997 = 0.03f;
    private static readonly Color Col1997 = new(16 / 255f, 16 / 255f, 16 / 255f, 0.75f);
    // the modern look
    private const int CapModern = 48, PointsModern = 160;
    private const float StepModern = 0.14f, WidthModern = 0.30f, LifeModern = 100f, FadeModern = 25f, LiftModern = 0.04f, Start = 0.7f, End = 0.5f;
    private static readonly Color Asphalt = new(0.05f, 0.05f, 0.055f, 0.85f), Dirt = new(0.24f, 0.17f, 0.10f, 0.78f);

    private sealed class Strip
    {
        public (Vehicle, int)? Key;
        public int Seq;
        public readonly List<SN.Vector3> P = [];     // game space; the tyre's contact point
        public readonly List<Color> C = [];          // per point (modern)
        public float Born;
        public bool Ended, Dirty, NoStartTaper, NoEndTaper;
        public MeshInstance3D? Node;
        public ArrayMesh Mesh = new();
        public void Clear() { Key = null; P.Clear(); C.Clear(); Ended = false; NoStartTaper = false; NoEndTaper = false; Dirty = true; }
    }

    private readonly List<Strip> _strips = [];
    private readonly HashSet<(Vehicle, int)> _active = [];
    private ShaderMaterial _mat = null!;
    private bool _modern = true, _seen;
    private int _seq, _lastTick = SimClock.Now;
    private float _t;
    private SN.Vector3 _origin;

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/skid_mark.gdshader") };
        _mat.SetShaderParameter("scene_light", SceneLight);
        _origin = Origin();
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        bool modern = Cars.Values.FirstOrDefault()?.Modern ?? true;
        if (!_seen || modern != _modern)
        {
            bool first = !_seen;
            _seen = true; _modern = modern;
            if (!first) Restyle(modern);
            _active.Clear();
            _mat.SetShaderParameter("modern", modern);
        }
        var o = Origin();
        if (o != _origin) { _origin = o; foreach (var s in _strips) s.Dirty = true; }

        if (modern) FrameModern();
        else if (SimClock.Take(ref _lastTick) > 0) TickOriginal();

        float life = modern ? LifeModern : Life1997;
        foreach (var s in _strips)
        {
            if (s.Key is null && s.P.Count == 0) { if (s.Dirty) Rebuild(s); continue; }
            float age = _t - s.Born;
            if (age > life) { s.Clear(); Rebuild(s); continue; }
            if (modern) s.Node?.SetInstanceShaderParameter("fade", Mathf.Clamp((life - age) / FadeModern, 0f, 1f));
            if (s.Dirty) Rebuild(s);
        }
    }

    /// <summary>
    /// F12: the marks already laid stay, drawn in the new look. The same contact points: to 1997 the newest sixteen strips,
    /// thinned to its 0.2 m step and cut to its 31 points; to modern all of them, finished (tapered at the end), in asphalt.
    /// </summary>
    private void Restyle(bool modern)
    {
        var keep = _strips.Where(s => s.P.Count >= 2).OrderByDescending(s => s.Born).ToList();
        if (!modern) keep = keep.Take(Cap1997).ToList();
        foreach (var s in _strips)
            if (!keep.Contains(s)) s.Node?.QueueFree();
        _strips.Clear();
        _strips.AddRange(keep);
        _ring = 0;
        foreach (var s in keep)
        {
            s.Key = (null!, -1);   // nobody's: no tyre extends it, and a new mark may take the oldest finished strip
            if (modern)
            {
                s.C.Clear();
                foreach (var _ in s.P) s.C.Add(SurfaceColour(2.3f));
                s.Ended = true; s.NoEndTaper = false; s.NoStartTaper = false;
            }
            else
            {
                var thin = new List<SN.Vector3> { s.P[0] };
                foreach (var p in s.P.Skip(1))
                {
                    var l = thin[^1];
                    if (MathF.Sqrt((p.X - l.X) * (p.X - l.X) + (p.Z - l.Z) * (p.Z - l.Z)) >= Step1997) thin.Add(p);
                }
                s.P.Clear(); s.P.AddRange(thin.Take(Points1997));
                s.C.Clear();
                foreach (var _ in s.P) s.C.Add(default);
            }
            s.Dirty = true;
        }
    }

    // ---- the triggers, as the original's: the sliding flag, on the two rear tyres

    private static IEnumerable<(int Idx, Wheel W)> Rears(Vehicle v) { yield return (0, v.RearLeft); yield return (1, v.RearRight); }

    private static bool Marks(Vehicle v) => !v.IsHeli && !v.IsEmplacement && (v.RearLeft.Offset.Z != 0 || v.RearRight.Offset.Z != 0 || v.RearLeft.Offset.X != 0);

    /// <summary>Once a sim step: the original's rules and numbers.</summary>
    private void TickOriginal()
    {
        foreach (var (v, car) in Cars)
        {
            if (!GodotObject.IsInstanceValid(car) || !car.IsInsideTree() || !Marks(v)) continue;
            bool sliding = (v.Flags & VehicleFlags.Sliding) != 0;
            foreach (var (idx, w) in Rears(v))
            {
                var key = (v, idx);
                if (!sliding || w.AtLimit) { _active.Remove(key); continue; }   // a tyre hanging at the end of its travel leaves none
                var rot = v.Rot;
                var p = v.Pos.ToVector3() + rot.Right * w.Offset.X + rot.Forward * w.Offset.Z + rot.Up * w.Mount.Y;
                p.Y = w.Ground != 0 || Ground is null ? w.Ground : Ground.Query(p.X, p.Y, p.Z).Height;
                if (_active.Add(key)) { Begin(key, p, default); continue; }
                var s = Newest(key);
                if (s is null || s.P.Count >= Points1997) continue;
                var last = s.P[^1];
                if (MathF.Sqrt((p.X - last.X) * (p.X - last.X) + (p.Z - last.Z) * (p.Z - last.Z)) >= Step1997) { s.P.Add(p); s.Dirty = true; }
            }
        }
    }

    /// <summary>The modern look: every frame, from the drawn tyre, so a curve is smooth.</summary>
    private void FrameModern()
    {
        foreach (var (v, car) in Cars)
        {
            if (!GodotObject.IsInstanceValid(car) || !car.IsInsideTree() || !Marks(v)) continue;
            bool sliding = (v.Flags & VehicleFlags.Sliding) != 0 && (v.Flags & VehicleFlags.Airborne) == 0;
            foreach (var (idx, w) in Rears(v))
            {
                var key = (v, idx);
                if (!sliding)
                {
                    if (_active.Remove(key) && Newest(key) is { } done) { done.Ended = true; done.Dirty = true; }
                    continue;
                }
                var g = Coords.ToGame(car.GlobalTransform * new Vector3(w.Offset.X, w.Mount.Y, -w.Offset.Z)) + _origin;
                var grip = 2f;
                if (Ground is not null)
                {
                    g.Y = Ground.Query(g.X, g.Y, g.Z).Height;
                    grip = Ground.Surface(Ground.SurfaceId(g.X, g.Z)).Grip;
                }
                var col = SurfaceColour(grip);
                var s = _active.Contains(key) ? Newest(key) : null;
                if (s is null)
                {
                    _active.Add(key);
                    Begin(key, g, col);
                    continue;
                }
                var last = s.P[^1];
                if ((g.X - last.X) * (g.X - last.X) + (g.Z - last.Z) * (g.Z - last.Z) < StepModern * StepModern) continue;
                if ((g - last).LengthSquared() > 25f * 25f) { s.Ended = true; s.Dirty = true; Begin(key, g, col); continue; }   // a jump: a teleport, not a skid
                s.P.Add(g); s.C.Add(col); s.Dirty = true;
                if (s.P.Count >= PointsModern) { s.Ended = true; s.NoEndTaper = true; Begin(key, g, col, taper: false); }   // a long slide goes on in the next strip
            }
        }
    }

    // asphalt (grip ~2.3) to dirt (~1.5): the colour and the opacity blend by the surface's grip
    private static Color SurfaceColour(float grip)
    {
        float t = Mathf.Clamp((grip - 1.55f) / 0.6f, 0f, 1f);
        return Dirt.Lerp(Asphalt, t);
    }

    private Strip? Newest(( Vehicle, int) key)
    {
        Strip? best = null;
        foreach (var s in _strips) if (s.Key == key && (best is null || s.Seq > best.Seq)) best = s;
        return best;
    }

    /// <summary>Takes a strip for a new mark: the next in the ring (1997, whatever it holds), else the oldest finished one, else the oldest.</summary>
    private void Begin((Vehicle, int) key, SN.Vector3 at, Color col, bool taper = true)
    {
        int cap = _modern ? CapModern : Cap1997;
        Strip s;
        if (_strips.Count < cap)
        {
            s = new Strip();
            s.Node = new MeshInstance3D { Mesh = s.Mesh, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4, Visible = false };
            AddChild(s.Node);
            _strips.Add(s);
        }
        else if (_modern)
        {
            s = _strips.OrderBy(x => x.Key is null ? -1 : x.Ended ? 0 : 1).ThenBy(x => x.Seq).First();
        }
        else s = _strips[_ring++ % cap];
        s.Clear();
        s.Key = key; s.Seq = ++_seq; s.Born = _t;
        s.P.Add(at); s.C.Add(col);
        s.Dirty = true;
        s.Node!.SetInstanceShaderParameter("fade", 1f);
        s.NoStartTaper = !taper;
    }
    private int _ring;

    // ---- the geometry

    private Vector3 G(SN.Vector3 p, float lift) { var q = p - _origin; return new Vector3(q.X, q.Y + lift, -q.Z); }

    /// <summary>
    /// 1997: a ribbon vertex's height. The sim's ground is the bilinear heightfield; the look draws a triangulated mesh
    /// (and road quads over it), which differ inside a cell whose corners are not on one plane. A point on a structure's
    /// surface (a bridge, a ramp: the sim's height is off the bare terrain) keeps the sim's height.
    /// </summary>
    private SN.Vector3 OnDrawn(SN.Vector3 p)
    {
        if (Builder?.World is not { } w) return p;
        _drawn ??= new I76.Game.World.Drawn1997(w, Builder.RoadPieces);
        if (Ground is not null && MathF.Abs(Ground.Query(p.X, p.Y, p.Z).Height - Ground.BareHeight(p.X, p.Z)) > 0.05f) return p;
        return p with { Y = _drawn.Height(p.X, p.Z) };
    }

    private void Rebuild(Strip s)
    {
        s.Dirty = false;
        s.Mesh.ClearSurfaces();
        int n = s.P.Count;
        if (s.Node is null) return;
        if (n < 2) { s.Node.Visible = false; return; }
        var vs = new List<Vector3>(n * 2);
        var cs = new List<Color>(n * 2);
        var uv = new List<Vector2>(n * 2);
        var idx = new List<int>((n - 1) * 6);
        if (!_modern)
        {
            // the original's ribbon: each point and a second 0.2 m to the right of its direction of travel (the first
            // point takes the first segment's); the quad of two neighbours is (side k, centre k, centre k+1, side k+1)
            for (int k = 0; k < n; k++)
            {
                var c = s.P[k];
                var from = k == 0 ? s.P[0] : s.P[k - 1];
                var to = k == 0 ? s.P[1] : c;
                float dx = to.X - from.X, dz = to.Z - from.Z, len = MathF.Sqrt(dx * dx + dz * dz);
                float f = len > 1e-6f ? Step1997 / len : 0f;
                vs.Add(G(OnDrawn(c), Lift1997)); vs.Add(G(OnDrawn(new SN.Vector3(c.X + dz * f, c.Y, c.Z - dx * f)), Lift1997));
                cs.Add(Col1997); cs.Add(Col1997);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0));
            }
        }
        else
        {
            float along = 0;
            for (int k = 0; k < n; k++)
            {
                var a = s.P[Math.Max(k - 1, 0)]; var b = s.P[Math.Min(k + 1, n - 1)];
                float tx = b.X - a.X, tz = b.Z - a.Z, tl = MathF.Sqrt(tx * tx + tz * tz);
                if (tl < 1e-6f) { tx = 0; tz = 1; tl = 1; }
                float rx = tz / tl * WidthModern * 0.5f, rz = -tx / tl * WidthModern * 0.5f;
                if (k > 0) { var d = s.P[k] - s.P[k - 1]; along += MathF.Sqrt(d.X * d.X + d.Z * d.Z); }
                var c = s.P[k];
                var col = s.C[k];
                float toEnd = 0;
                for (int j = k; j < n - 1; j++) { var d = s.P[j + 1] - s.P[j]; toEnd += MathF.Sqrt(d.X * d.X + d.Z * d.Z); }
                float ta = s.NoStartTaper ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp(along / Start, 0f, 1f));
                if (s.Ended && !s.NoEndTaper) ta *= Mathf.SmoothStep(0f, 1f, Mathf.Clamp(toEnd / End, 0f, 1f));
                col.A *= ta;
                vs.Add(G(new SN.Vector3(c.X - rx, c.Y, c.Z - rz), LiftModern)); vs.Add(G(new SN.Vector3(c.X + rx, c.Y, c.Z + rz), LiftModern));
                cs.Add(col); cs.Add(col);
                uv.Add(new Vector2(0, along)); uv.Add(new Vector2(1, along));
            }
        }
        for (int k = 0; k < n - 1; k++)
        {
            int i = k * 2;
            idx.Add(i); idx.Add(i + 1); idx.Add(i + 2);
            idx.Add(i + 1); idx.Add(i + 3); idx.Add(i + 2);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vs.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = cs.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        s.Mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
        s.Node.Visible = true;
    }
}
