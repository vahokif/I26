using System.Numerics;
using I76.Formats.Assembly;

namespace I76.Formats.Vegetation;

/// <summary>
/// Where no plant may stand, in the game frame's XZ: the roads (their quads), the footprints of the mission's
/// objects (oriented boxes of the parts that stand on the ground), the AI's driving lines (polylines) and points
/// such as where cars start (discs). Each test takes the clearance the plant needs. Built once, then read from any
/// thread.
/// </summary>
public sealed class VegMask
{
    public const float Cell = 16f;

    private readonly record struct Quad(Vector2 A, Vector2 B, Vector2 C, Vector2 D);
    private readonly record struct Box(Vector2 Centre, Vector2 U, float HalfU, float HalfV, float Margin);
    private readonly record struct Seg(Vector2 A, Vector2 B, float Radius);
    private readonly record struct Disc(Vector2 Centre, float Radius);

    private readonly List<Quad> _quads = [];
    private readonly List<Box> _boxes = [];
    private readonly List<Seg> _segs = [];
    private readonly List<Disc> _discs = [];
    private readonly Dictionary<(int, int), List<int>> _quadGrid = [], _boxGrid = [], _segGrid = [], _discGrid = [];
    /// <summary>The widest clearance any test may ask for: every shape is entered in the cells it reaches by this much.</summary>
    public float MaxClearance { get; init; } = 8f;

    public int Roads => _quads.Count;
    public int Footprints => _boxes.Count;
    public int Lines => _segs.Count;

    private static (int, int) Key(float x, float z) => ((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell));

    private void Insert(Dictionary<(int, int), List<int>> grid, Vector2 min, Vector2 max, float reach, int index)
    {
        var (x0, z0) = Key(min.X - reach, min.Y - reach);
        var (x1, z1) = Key(max.X + reach, max.Y + reach);
        if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 1 << 16) return;   // a degenerate giant: ignore
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = [];
                l.Add(index);
            }
    }

    /// <summary>A road quad (the four corners in order round it).</summary>
    public void AddRoadQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        _quads.Add(new Quad(a, b, c, d));
        Insert(_quadGrid, Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)), MaxClearance, _quads.Count - 1);
    }

    /// <summary>An oriented box: centre, its first axis (unit), half extents along it and across, and a margin of its own added to any clearance.</summary>
    public void AddFootprint(Vector2 centre, Vector2 axisU, float halfU, float halfV, float margin = 0f)
    {
        axisU = axisU.LengthSquared() < 1e-8f ? Vector2.UnitX : Vector2.Normalize(axisU);
        _boxes.Add(new Box(centre, axisU, halfU, halfV, margin));
        float r = MathF.Sqrt(halfU * halfU + halfV * halfV) + margin;
        Insert(_boxGrid, centre - new Vector2(r), centre + new Vector2(r), MaxClearance, _boxes.Count - 1);
    }

    public void AddLine(IReadOnlyList<Vector2> points, float radius)
    {
        for (int i = 0; i + 1 < points.Count; i++)
        {
            _segs.Add(new Seg(points[i], points[i + 1], radius));
            Insert(_segGrid, Vector2.Min(points[i], points[i + 1]), Vector2.Max(points[i], points[i + 1]), radius + MaxClearance, _segs.Count - 1);
        }
    }

    public void AddDisc(Vector2 centre, float radius)
    {
        _discs.Add(new Disc(centre, radius));
        Insert(_discGrid, centre - new Vector2(radius), centre + new Vector2(radius), MaxClearance, _discs.Count - 1);
    }

    /// <summary>The distance from p to the nearest road (0 on one), up to <paramref name="cap"/>.</summary>
    public float RoadDistance(Vector2 p, float cap)
    {
        float best = cap;
        if (!_quadGrid.TryGetValue(Key(p.X, p.Y), out var l)) return best;
        foreach (int i in l)
        {
            float d = QuadDistance(_quads[i], p);
            if (d < best) { best = d; if (d <= 0) return 0; }
        }
        return best;
    }

    /// <summary>
    /// Whether a plant at p needing these clearances is kept out: within <paramref name="road"/> of a road, within
    /// <paramref name="footprint"/> of an object's footprint, inside a driving line's radius plus <paramref name="line"/>,
    /// or inside a disc (unless <paramref name="discs"/> is false: the sward grows where cars start).
    /// </summary>
    public bool Blocked(Vector2 p, float road, float footprint, float line, bool discs = true)
    {
        var k = Key(p.X, p.Y);
        if (road > 0 && RoadDistance(p, road) < road) return true;
        if (_boxGrid.TryGetValue(k, out var bl))
            foreach (int i in bl)
            {
                var b = _boxes[i];
                var d = p - b.Centre;
                float u = MathF.Abs(Vector2.Dot(d, b.U)), v = MathF.Abs(d.X * -b.U.Y + d.Y * b.U.X);
                float m = footprint + b.Margin;
                if (u < b.HalfU + m && v < b.HalfV + m) return true;
            }
        if (_segGrid.TryGetValue(k, out var sl))
            foreach (int i in sl)
            {
                var s = _segs[i];
                if (SegDistance(s.A, s.B, p) < s.Radius + line) return true;
            }
        if (discs && _discGrid.TryGetValue(k, out var dl))
            foreach (int i in dl)
                if (Vector2.Distance(_discs[i].Centre, p) < _discs[i].Radius + footprint) return true;
        return false;
    }

    public static float SegDistance(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>0 inside the quad (convex or not: two triangles), else the distance to its nearest edge.</summary>
    private static float QuadDistance(Quad q, Vector2 p)
    {
        if (InTri(q.A, q.B, q.C, p) || InTri(q.A, q.C, q.D, p)) return 0;
        return MathF.Min(MathF.Min(SegDistance(q.A, q.B, p), SegDistance(q.B, q.C, p)), MathF.Min(SegDistance(q.C, q.D, p), SegDistance(q.D, q.A, p)));
    }

    private static bool InTri(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>
    /// The roads (every road quad), the ground-standing parts of every placed object (oriented boxes of each part's
    /// bounds; a bridge's deck high over a riverbed is not one), where cars start, and the script's driving lines.
    /// </summary>
    public static VegMask ForMission(MissionWorld world, ModelAssembler assembler)
    {
        var mask = new VegMask { MaxClearance = 8f };
        static System.Numerics.Vector2 XZ(System.Numerics.Vector3 v) => new(v.X, v.Z);
        foreach (var piece in I76.Formats.Assembly.Roads.Build(world.Mission, world.BilinearHeight))
            for (int j = 0; j + 1 < piece.Points.Length; j++)
                mask.AddRoadQuad(XZ(piece.Points[j].Right), XZ(piece.Points[j].Left), XZ(piece.Points[j + 1].Left), XZ(piece.Points[j + 1].Right));
        var models = new Dictionary<string, AssembledModel?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in world.Placements.Concat(world.LinePlacements))
        {
            var pos = world.GroundedPosition(p);
            if (p.Object.ObjectType == 1 || p.Object.IsKeyword || p.Definition is null)
            {
                // cars (where they start), spawn and regen points: a clear disc
                mask.AddDisc(XZ(pos), p.Object.ObjectType == 1 ? 6f : 4f);
                if (p.Definition is null || p.Object.ObjectType == 1) continue;
            }
            if (!models.TryGetValue(p.Definition, out var model)) models[p.Definition] = model = assembler.Assemble(p.Definition);
            if (model is null) { mask.AddDisc(XZ(pos), 4f); continue; }
            var place = Affine.FromEngine(p.Object.Rotation, pos);
            var u = place.Rotate(System.Numerics.Vector3.UnitX); var w = place.Rotate(System.Numerics.Vector3.UnitZ);
            foreach (var part in model.Parts)
            {
                if (part.Mesh.Vertices.Length == 0) continue;
                var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
                foreach (var v in part.Mesh.Vertices) { var q = part.ToModel.Apply(v); mn = System.Numerics.Vector3.Min(mn, q); mx = System.Numerics.Vector3.Max(mx, q); }
                var centre = place.Apply((mn + mx) * 0.5f);
                // the part's lowest corner in the world, against the ground under its middle
                float lowest = float.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    var corner = new System.Numerics.Vector3((k & 1) == 0 ? mn.X : mx.X, (k & 2) == 0 ? mn.Y : mx.Y, (k & 4) == 0 ? mn.Z : mx.Z);
                    lowest = MathF.Min(lowest, place.Apply(corner).Y);
                }
                if (lowest > world.BilinearHeight(centre.X, centre.Z) + 2.5) continue;
                float lu = new System.Numerics.Vector2(u.X, u.Z).Length(), lw = new System.Numerics.Vector2(w.X, w.Z).Length();
                mask.AddFootprint(XZ(centre), new System.Numerics.Vector2(u.X, u.Z), (mx.X - mn.X) * 0.5f * MathF.Max(lu, 0.3f), (mx.Z - mn.Z) * 0.5f * MathF.Max(lw, 0.3f));
            }
        }
        if (world.Mission.GetFsmTables()?.Paths is { } paths)
            foreach (var path in paths)
                mask.AddLine([.. path.Points.Select(q => new System.Numerics.Vector2(q.X, q.Z))], 2.5f);
        return mask;
    }
}
