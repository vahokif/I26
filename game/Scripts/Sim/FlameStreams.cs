using Godot;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The flame throwers' streams as the original draws them:
/// each pair of consecutive cross-sections (triangles of radius 0.15 growing
/// 11.5% a step, flat once the stream has met something) is joined by three
/// quads, each the whole texture with u running from 1 at the near end to 0
/// at the far end, drawn from both sides, and capped by a triangle in the
/// style's _401. The segment by the muzzle is in the _3xx frames, the far one
/// in _1xx, the rest in _2xx, each cycling its four frames a frame. Styles:
/// xfl1 (FlameThrower), xfm1 (Gas Launcher), xfh1 (Napalm Hose, Pyro-Tomic).
/// The chain is carried on the car, so it moves with it between ticks.
/// </summary>
public partial class FlameStreams : Node3D
{
    public required Combat Combat { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public required Func<string, Texture2D?> Texture { get; init; }
    public Func<I76.Sim.Vehicles.Vehicle, Node3D?>? CarOf { get; init; }
    /// <summary>The modern look draws the streams as NapalmModern's fire; the 1997 look keeps the original's frames below.</summary>
    public Func<bool>? Modern { get; init; }
    public float SceneLight { get; init; } = 1f;
    private NapalmModern _modern = null!;

    private static readonly string[] Styles = ["xfl1", "xfm1", "xfh1"];
    private static readonly (int A, int B)[] Sides = [(0, 1), (1, 2), (2, 0)];
    private readonly Dictionary<string, StandardMaterial3D?> _materials = [];
    private ImmediateMesh _im = null!;
    private double _t;

    public override void _Ready()
    {
        _im = new ImmediateMesh();
        AddChild(new MeshInstance3D { Mesh = _im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _modern = new NapalmModern { Name = "Modern", SceneLight = SceneLight };
        AddChild(_modern);
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

    public override void _Process(double delta)
    {
        _t += delta;
        _im.ClearSurfaces();
        bool modern = Modern?.Invoke() ?? true;
        if (!modern) _modern.Clear();
        if (Combat.Flames.Count == 0 && !modern) return;
        int frame = SimClock.Now & 3;   // the tube frames cycle once a frame: a sim tick
        var origin = Origin();
        // every stream's points where the car is drawn now
        var pts = new List<(Flame F, Vector3[] P)>();
        foreach (var f in Combat.Flames)
        {
            int n = f.Count;
            var p = new Vector3[n * 3];
            var car = CarOf?.Invoke(f.Owner);
            for (int i = 0; i < n * 3; i++)
                p[i] = car is not null && car.IsInsideTree()
                    ? car.GlobalTransform * Coords.ToGodot(f.OwnerRot.ToLocal(f.Cross[i] - f.OwnerPos))
                    : Coords.ToGodot(f.Cross[i] - origin);
            pts.Add((f, p));
        }
        if (modern) { _modern.Update(delta, pts); return; }
        foreach (int part in (int[])[1, 2, 3])
            foreach (var group in pts.GroupBy(x => x.F.Style))
            {
                if (Material($"{Styles[group.Key]}_{part}0{frame + 1}") is not { } mat) continue;
                bool begun = false;
                foreach (var (f, p) in group)
                {
                    int segs = f.Count - 1;
                    for (int s = 0; s < segs; s++)
                    {
                        int want = s == segs - 1 ? 1 : s == 0 ? 3 : 2;
                        if (want != part) continue;
                        if (!begun) { _im.SurfaceBegin(Mesh.PrimitiveType.Triangles, mat); begun = true; }
                        foreach (var (a, b) in Sides)
                        {
                            Vector3 na = p[s * 3 + a], nb = p[s * 3 + b], fa = p[(s + 1) * 3 + a], fb = p[(s + 1) * 3 + b];
                            Quad(na, new(1, 1), nb, new(1, 0), fb, new(0, 0), fa, new(0, 1));
                        }
                    }
                }
                if (begun) _im.SurfaceEnd();
            }
        // the end caps, one on the far cross-section of every segment
        foreach (var group in pts.GroupBy(x => x.F.Style))
        {
            if (Material($"{Styles[group.Key]}_401") is not { } cap) continue;
            bool begun = false;
            foreach (var (f, p) in group)
                for (int s = 1; s < f.Count; s++)
                {
                    if (!begun) { _im.SurfaceBegin(Mesh.PrimitiveType.Triangles, cap); begun = true; }
                    Tri(p[s * 3], new(0, 0), p[s * 3 + 1], new(1, 0), p[s * 3 + 2], new(1, 1));
                }
            if (begun) _im.SurfaceEnd();
        }
    }

    private void Tri(Vector3 a, Vector2 ua, Vector3 b, Vector2 ub, Vector3 c, Vector2 uc)
    {
        _im.SurfaceSetUV(ua); _im.SurfaceAddVertex(a);
        _im.SurfaceSetUV(ub); _im.SurfaceAddVertex(b);
        _im.SurfaceSetUV(uc); _im.SurfaceAddVertex(c);
    }

    private void Quad(Vector3 a, Vector2 ua, Vector3 b, Vector2 ub, Vector3 c, Vector2 uc, Vector3 d, Vector2 ud)
    {
        Tri(a, ua, b, ub, c, uc);
        Tri(a, ua, c, uc, d, ud);
    }
}
