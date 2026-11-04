using Godot;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's caltrops (kind 0xe): each drop is a scatter of small
/// four-spiked jacks (a tetrahedron's four points: one spike always up, the other three on the ground) over the
/// 4 x 4 m the original's MCALTROP quad covered, standing on the drawn ground (the oil's and fire's DrawnY). All the
/// pieces of all the patches are one MultiMesh of a fixed size, a drop owning a slice of it while it lives; they
/// are placed once and kept in the anchor's frame, so a shift of the world origin moves only the node. The 1997 look draws
/// the original's textured quad. What blows tyres is the sim's ray test, untouched.
/// </summary>
public partial class CaltropPatches : Node3D
{
    public const int MaxPatches = 400, PerPatch = 32;
    /// <summary>A spike's length (m): well over a real jack's (#104: 0.14 m was lost on dirt from the chase camera), so it reads from afar.</summary>
    private const float Spike = 0.2f;

    /// <summary>The drawn ground's height under a Godot-space point at a drop's level (see GroundMark).</summary>
    public required Func<Vector3, float> GroundAt { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }

    private MultiMeshInstance3D? _mmi;
    private MultiMesh? _mm;
    private System.Numerics.Vector3 _anchor;
    private bool _anchored;
    private readonly Dictionary<Round, int> _slots = [];
    private readonly Stack<int> _free = [];
    private readonly List<Round> _gone = [];
    private int _made;
    private static readonly Transform3D Hidden = new(Basis.FromScale(Vector3.Zero), Vector3.Zero);

    public override void _Ready()
    {
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = Jack(), InstanceCount = MaxPatches * PerPatch };
        for (int i = 0; i < _mm.InstanceCount; i++) _mm.SetInstanceTransform(i, Hidden);
        _mmi = new MultiMeshInstance3D
        {
            Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.On, ExtraCullMargin = 4096f,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.88f, 0.92f), Metallic = 0.35f, Roughness = 0.4f, EmissionEnabled = true, Emission = new Color(0.55f, 0.57f, 0.6f), EmissionEnergyMultiplier = 0.25f },
        };
        AddChild(_mmi);
    }

    /// <summary>A jack: four thin three-sided spikes from the centre to a tetrahedron's corners, one pointing up, flat shaded; its lowest tips are at y = -Spike / 3.</summary>
    private static ArrayMesh Jack()
    {
        var dirs = new Vector3[4];
        dirs[0] = Vector3.Up;
        for (int k = 0; k < 3; k++)
        {
            float a = k * Mathf.Tau / 3f;
            dirs[k + 1] = new Vector3(MathF.Sqrt(8f / 9f) * MathF.Cos(a), -1f / 3f, MathF.Sqrt(8f / 9f) * MathF.Sin(a));
        }
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        foreach (var d in dirs)
        {
            var side = d.Cross(MathF.Abs(d.Y) > 0.9f ? Vector3.Right : Vector3.Up).Normalized();
            var fwd = d.Cross(side);
            var tip = d * Spike;
            var b = new Vector3[3];
            for (int k = 0; k < 3; k++)
                b[k] = (side * MathF.Cos(k * Mathf.Tau / 3f) + fwd * MathF.Sin(k * Mathf.Tau / 3f)) * Spike * 0.11f;
            for (int k = 0; k < 3; k++)
            {
                var p0 = b[k]; var p1 = b[(k + 1) % 3];
                var n = (p1 - p0).Cross(tip - p0).Normalized();
                if (n.Dot(p0 + p1 + tip) < 0) { (p0, p1) = (p1, p0); n = -n; }
                verts.Add(p0); verts.Add(p1); verts.Add(tip);
                for (int j = 0; j < 3; j++) norms.Add(n);
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static float Hash(int x, int y, int s)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + s * 144665);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }

    /// <summary>One frame: the caltrops among the rounds (kind 0xe). <paramref name="patch"/> is a drop's quad in its own plane (the ordnance mesh's bounds).</summary>
    public void Frame(IReadOnlyList<Round> rounds, bool modern, Func<System.Numerics.Vector3, Vector3> toGodot, Func<Round, Aabb?> patch)
    {
        if (_mmi is null || _mm is null) return;
        if (!modern) { Release(null); _mmi.Visible = false; return; }
        _mmi.Visible = true;
        if (!_anchored) { _anchor = Origin(); _anchored = true; }
        Position = toGodot(_anchor);
        var live = new HashSet<Round>();
        foreach (var r in rounds)
        {
            if (r.Kind != 0xe) continue;
            live.Add(r);
            if (_slots.ContainsKey(r) || patch(r) is not { } box) continue;
            int slot;
            if (_free.Count > 0) slot = _free.Pop();
            else if (_made < MaxPatches) slot = _made++;
            else continue;
            _slots[r] = slot;
            Scatter(r, slot, box, toGodot);
        }
        Release(live);
    }

    private void Release(HashSet<Round>? live)
    {
        if (_slots.Count == 0) return;
        _gone.Clear();
        foreach (var r in _slots.Keys) if (live is null || !live.Contains(r)) _gone.Add(r);
        foreach (var r in _gone)
        {
            int slot = _slots[r];
            for (int k = 0; k < PerPatch; k++) _mm!.SetInstanceTransform(slot * PerPatch + k, Hidden);
            _free.Push(slot);
            _slots.Remove(r);
        }
    }

    /// <summary>The drop's jacks: scattered over its quad (its density the same wherever it lies), each turned about the vertical, on the drawn ground.</summary>
    private void Scatter(Round r, int slot, Aabb box, Func<System.Numerics.Vector3, Vector3> toGodot)
    {
        var basis = Coords.ToGodot(r.Rot.ToEngine());
        var at = toGodot(r.Pos);
        var node = toGodot(_anchor);
        int seed = (int)((uint)(r.Pos.X * 7 + r.Pos.Z * 13) & 0x7fffffff) + r.NetId;
        for (int k = 0; k < PerPatch; k++)
        {
            float u = Hash(k, seed, 1) - 0.5f, v = Hash(k, seed, 2) - 0.5f;
            var local = new Vector3(box.Position.X + (u + 0.5f) * box.Size.X, 0, box.Position.Z + (v + 0.5f) * box.Size.Z);
            var p = at + basis * local;
            float scale = 0.85f + 0.3f * Hash(k, seed, 3);
            p.Y = GroundAt(p) + Spike * scale / 3f;
            var rot = new Basis(Vector3.Up, Hash(k, seed, 4) * Mathf.Tau);
            _mm!.SetInstanceTransform(slot * PerPatch + k, new Transform3D(rot.Scaled(Vector3.One * scale), p - node));
        }
    }
}
