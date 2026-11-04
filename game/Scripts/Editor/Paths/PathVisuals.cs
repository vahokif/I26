using Godot;
using I76.Editor.Paths;
using I76.Game.Editor.Objects;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.PathTools;

/// <summary>
/// The path layer over the ground: each path a line draped on the ground with an arrow on every leg (the way it is driven), a ball at
/// each point (the first larger), and its label by the first point; the selected path brighter, its points with handles; the path
/// being drawn and the line to the cursor. Unshaded and drawn over everything, so it reads the same in both looks. Parented to the
/// world's root, so the floating origin moves it with the ground.
/// </summary>
public sealed partial class PathVisuals : Node3D
{
    private readonly MeshInstance3D _lines = new() { Name = "PathLines", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MeshInstance3D _draft = new() { Name = "PathDraft", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MultiMeshInstance3D _points = new() { Name = "PathPoints", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MeshInstance3D _marker = new() { Name = "PathCursor", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
    private readonly Node3D _labels = new() { Name = "PathLabels" };
    private readonly Func<double, double, float> _ground;

    public PathVisuals(Func<double, double, float> ground)
    {
        _ground = ground;
        Name = "PathLayer";
        _lines.MaterialOverride = Overlay(0.9f);
        _draft.MaterialOverride = Overlay(0.95f);
        AddChild(_lines); AddChild(_draft); AddChild(_points); AddChild(_marker); AddChild(_labels);
        _points.Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 12, Rings = 6 } };
        _points.MaterialOverride = Overlay(1f);
        _marker.Mesh = new TorusMesh { InnerRadius = 0.8f, OuterRadius = 1f, Rings = 24, RingSegments = 6 };
        _marker.MaterialOverride = Overlay(0.95f);
    }

    private static StandardMaterial3D Overlay(float alpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = true,
        AlbedoColor = new Color(1, 1, 1, alpha),
        RenderPriority = 12,
    };

    /// <summary>A path's colour: a hue of its own (by its first entry), the selected one amber.</summary>
    public static Color Colour(int first, bool selected) =>
        selected ? new Color(1f, 0.78f, 0.2f) : Color.FromHsv(first * 0.618034f % 1f, 0.55f, 1f);

    private Vector3 G(V2 c, float lift) => new(c.X, _ground(c.X, c.Y) + lift, -c.Y);

    /// <summary>
    /// Every path's line and points. <paramref name="scale"/> grows with the camera's distance (lines, balls and arrows stay
    /// readable from far out); <paramref name="hoverPoint"/> and <paramref name="selPoint"/> are points of the selected path.
    /// </summary>
    public void Show(IReadOnlyList<PathGroup> paths, int selected, int selPoint, int hoverPoint, int hoverPath, float scale, bool labels)
    {
        var v = new List<Vector3>(); var c = new List<Color>();
        foreach (var g in paths)
        {
            bool sel = g.First == selected;
            var col = Colour(g.First, sel);
            if (!sel && g.First == hoverPath) col = col.Lightened(0.4f);
            col.A = sel ? 1f : 0.8f;
            AddLine(v, c, g.Points.Select(PathEdits.XZ).ToList(), col, scale * (sel ? 0.42f : 0.3f), scale);
        }
        _lines.Mesh = Mesh(v, c);

        var mm = _points.Multimesh;
        int n = paths.Sum(g => g.Points.Count);
        mm.InstanceCount = n;
        int i = 0;
        foreach (var g in paths)
        {
            bool sel = g.First == selected;
            for (int k = 0; k < g.Points.Count; k++, i++)
            {
                var p = PathEdits.XZ(g.Points[k]);
                float s = scale * (sel ? 1.25f : 0.9f) * (k == 0 ? 1.45f : 1f) * (sel && (k == selPoint || k == hoverPoint) ? 1.5f : 1f);
                mm.SetInstanceTransform(i, new Transform3D(Basis.FromScale(new Vector3(s, s, s)), G(p, 0.4f)));
                var col = sel && k == selPoint ? new Color(1f, 0.45f, 0.1f) : sel && k == hoverPoint ? new Color(1f, 1f, 0.55f) : k == 0 ? new Color(1f, 1f, 1f) : Colour(g.First, sel);
                mm.SetInstanceColor(i, col);
            }
        }

        foreach (var l in _labels.GetChildren()) { _labels.RemoveChild(l); l.QueueFree(); }
        if (!labels) return;
        foreach (var g in paths)
        {
            if (g.Points.Count == 0) continue;
            bool sel = g.First == selected;
            var l = MarkerView.Label(g.Label, sel ? new Color(1f, 0.85f, 0.35f) : Colour(g.First, false).Lightened(0.35f), 0, sel ? 0.0006f : 0.00045f);
            l.Position = G(PathEdits.XZ(g.Points[0]), 2.2f * scale + 1f);
            _labels.AddChild(l);
        }
    }

    /// <summary>The path being drawn: its points so far, then a lighter leg to the cursor.</summary>
    public void ShowDraft(IReadOnlyList<V2>? points, V2? cursor, float scale)
    {
        if (points is null || points.Count == 0) { _draft.Mesh = null; return; }
        var v = new List<Vector3>(); var c = new List<Color>();
        var col = new Color(0.45f, 1f, 0.5f);
        AddLine(v, c, points, col, scale * 0.42f, scale);
        if (cursor is { } cur) AddLine(v, c, [points[^1], cur], col with { A = 0.45f }, scale * 0.3f, scale);
        foreach (var p in points) AddBall(v, c, p, scale * 0.9f, col);
        _draft.Mesh = Mesh(v, c);
    }

    /// <summary>A ring where a click would put a point (a new point, or one inserted on a line).</summary>
    public void ShowMarker(V2? at, Color colour, float scale)
    {
        if (at is not { } p) { _marker.Visible = false; return; }
        _marker.Visible = true;
        _marker.Transform = new Transform3D(Basis.FromScale(new Vector3(scale * 1.1f, scale * 1.1f, scale * 1.1f)), G(p, 0.6f));
        ((StandardMaterial3D)_marker.MaterialOverride!).AlbedoColor = colour with { A = 0.95f };
    }

    public void Clear()
    {
        _lines.Mesh = null; _draft.Mesh = null; _points.Multimesh.InstanceCount = 0; _marker.Visible = false;
        foreach (var l in _labels.GetChildren()) { _labels.RemoveChild(l); l.QueueFree(); }
    }

    // ---- meshes -----------------------------------------------------------------------------------------------------------

    /// <summary>A strip along the legs, draped on the ground (a sample every few metres), and an arrowhead in the middle of each leg.</summary>
    private void AddLine(List<Vector3> v, List<Color> c, IReadOnlyList<V2> pts, Color col, float half, float scale)
    {
        float lift = 0.3f;
        for (int k = 0; k + 1 < pts.Count; k++)
        {
            V2 a = pts[k], b = pts[k + 1], d = b - a;
            float len = d.Length();
            if (len < 1e-3f) continue;
            var t = d / len;
            var nrm = new V2(-t.Y, t.X) * half;
            int steps = Math.Clamp((int)(len / 6f), 1, 400);
            for (int s = 0; s < steps; s++)
            {
                V2 p0 = a + d * ((float)s / steps), p1 = a + d * ((float)(s + 1) / steps);
                Vector3 l0 = G(p0 + nrm, lift), r0 = G(p0 - nrm, lift), l1 = G(p1 + nrm, lift), r1 = G(p1 - nrm, lift);
                v.AddRange([r0, l0, l1, r0, l1, r1]);
                for (int q = 0; q < 6; q++) c.Add(col);
            }
            // the arrowhead: which way the path runs
            float head = MathF.Min(scale * 2.2f, len * 0.35f);
            if (head > 0.5f)
            {
                V2 tip = a + d * 0.5f + t * head * 0.5f, back = tip - t * head;
                var w = new V2(-t.Y, t.X) * head * 0.6f;
                v.AddRange([G(tip, lift + 0.05f), G(back + w, lift + 0.05f), G(back - w, lift + 0.05f)]);
                for (int q = 0; q < 3; q++) c.Add(col with { A = 1f });
            }
        }
    }

    /// <summary>A flat diamond on the ground (a point of the path being drawn).</summary>
    private void AddBall(List<Vector3> v, List<Color> c, V2 p, float r, Color col)
    {
        V2[] q = [p + new V2(r, 0), p + new V2(0, r), p - new V2(r, 0), p - new V2(0, r)];
        v.AddRange([G(q[0], 0.4f), G(q[1], 0.4f), G(q[2], 0.4f), G(q[0], 0.4f), G(q[2], 0.4f), G(q[3], 0.4f)]);
        for (int k = 0; k < 6; k++) c.Add(col with { A = 1f });
    }

    private static ArrayMesh? Mesh(List<Vector3> v, List<Color> c)
    {
        if (v.Count == 0) return null;
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Color] = c.ToArray();
        mesh.AddSurfaceArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
