using Godot;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission.Edit;
using I76.Editor.Objects;
using I76.Game.Sim;
using I76.Game.World;

namespace I76.Game.Editor.RoadTools;

/// <summary>
/// What the road tool shows over the ground: the road being drawn as the real road mesh (RoadNode, the loader's own quads and
/// textures, in both looks) with a tinted ribbon on it for the grade, the handles of the selected road, the snap marker.
/// All of it is parented to the world's root, so the floating origin moves it with the ground.
/// </summary>
public sealed partial class RoadVisuals : Node3D
{
    private readonly MissionBuilder _builder;
    private readonly Func<bool> _is1997;
    private RoadNode? _preview;
    private readonly MeshInstance3D _ribbon = new() { Name = "RoadGrade", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MeshInstance3D _selection = new() { Name = "RoadSelection", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MultiMeshInstance3D _handles = new() { Name = "RoadHandles", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MeshInstance3D _marker = new() { Name = "RoadSnap", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };

    public RoadVisuals(MissionBuilder builder, Func<bool> is1997)
    {
        _builder = builder; _is1997 = is1997;
        Name = "RoadTool";
        var m = Overlay(depth: false);
        m.AlbedoColor = new Color(1, 1, 1, 0.5f);
        _ribbon.MaterialOverride = m;
        _selection.MaterialOverride = Overlay(depth: false);
        AddChild(_ribbon); AddChild(_selection); AddChild(_handles); AddChild(_marker);
        _handles.Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 12, Rings = 6 } };
        _handles.MaterialOverride = Overlay(depth: false, alpha: 1f);
        _marker.Mesh = new TorusMesh { InnerRadius = 0.8f, OuterRadius = 1f, Rings = 24, RingSegments = 6 };
        _marker.MaterialOverride = Overlay(depth: false, alpha: 0.95f);
    }

    private static StandardMaterial3D Overlay(bool depth, float alpha = 0.55f) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = !depth,
        AlbedoColor = new Color(1, 1, 1, alpha),
        RenderPriority = 10,
    };

    public static Color Band(int band) => band switch { 0 => new Color(0.25f, 0.85f, 0.3f), 1 => new Color(1f, 0.82f, 0.2f), _ => new Color(0.95f, 0.25f, 0.2f) };

    // ---- the road mesh ----------------------------------------------------------------------------------------------------

    /// <summary>The road (its points at the given heights, edges flat across) as the loader's pieces would draw it.</summary>
    public static List<RoadPiece> Pieces(RoadEdit road, IReadOnlyList<float> heights)
    {
        var edges = new RoadEdge[road.Points.Count];
        for (int i = 0; i < edges.Length; i++)
        {
            var p = road.Points[i];
            edges[i] = new RoadEdge(new System.Numerics.Vector3(p.Left.X, heights[i], p.Left.Y), new System.Numerics.Vector3(p.Right.X, heights[i], p.Right.Y));
        }
        var o = new List<RoadPiece>();
        foreach (var (start, count) in I76.Formats.Assembly.Roads.Split(edges.Length)) o.Add(new RoadPiece(road.Kind, edges.AsSpan(start, count).ToArray()));
        return o;
    }

    /// <summary>Shows a road as the real road mesh, or nothing for null.</summary>
    public void ShowRoad(RoadEdit? road, IReadOnlyList<float>? heights)
    {
        _preview?.QueueFree();
        _preview = null;
        if (road is null || heights is null || _builder.Resolver is null || _builder.Textures is null || _builder.RoadNames is null) return;
        _preview = new RoadNode { Name = "RoadPreview", LightY = _builder.Light.Direction.Y * _builder.Light.Intensity, Ambient = _builder.Light.Ambient };
        _preview.Build(Pieces(road, heights), _builder.RoadNames, _builder.Resolver, _builder.Textures);
        _preview.SetMode1997(_is1997());
        AddChild(_preview);
    }

    public void SetMode1997(bool on) => _preview?.SetMode1997(on);

    // ---- ribbons -----------------------------------------------------------------------------------------------------------

    private static Vector3 G(System.Numerics.Vector2 c, float h) => new(c.X, h, -c.Y);

    /// <summary>A strip over a centre line, one colour per step, lifted a little above the heights.</summary>
    public static ArrayMesh Ribbon(IReadOnlyList<System.Numerics.Vector2> centres, IReadOnlyList<float> heights, Func<int, Color> colour, float lift, float halfWidth)
    {
        var v = new List<Vector3>(); var c = new List<Color>();
        int n = centres.Count;
        for (int i = 0; i + 1 < n; i++)
        {
            System.Numerics.Vector2 T(int k)
            {
                var d = k == 0 ? centres[1] - centres[0] : k == n - 1 ? centres[k] - centres[k - 1] : centres[k + 1] - centres[k - 1];
                return d.LengthSquared() < 1e-9f ? new System.Numerics.Vector2(0, 1) : System.Numerics.Vector2.Normalize(d);
            }
            Vector3 L(int k) { var t = T(k); return G(centres[k] + new System.Numerics.Vector2(-t.Y, t.X) * halfWidth, heights[k] + lift); }
            Vector3 R(int k) { var t = T(k); return G(centres[k] - new System.Numerics.Vector2(-t.Y, t.X) * halfWidth, heights[k] + lift); }
            var col = colour(i);
            v.AddRange([R(i), L(i), L(i + 1), R(i), L(i + 1), R(i + 1)]);
            for (int k = 0; k < 6; k++) c.Add(col);
        }
        var mesh = new ArrayMesh();
        if (v.Count == 0) return mesh;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = c.ToArray();
        mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The grade ribbon: green, yellow, red along the drawn road for the ground's grade against the limit.</summary>
    public void ShowGrade(IReadOnlyList<System.Numerics.Vector2>? centres, IReadOnlyList<float>? heights, IReadOnlyList<float>? natural, float maxGrade)
    {
        if (centres is null || heights is null || natural is null || centres.Count < 2) { _ribbon.Mesh = null; return; }
        _ribbon.Mesh = Ribbon(centres, heights, i => Band(RoadGrade.Band(natural[Math.Min(i, natural.Count - 1)], maxGrade)), 0.25f, 5f);
    }

    /// <summary>A tinted outline of a road (selected, or about to be bulldozed or upgraded).</summary>
    public void ShowSelection(RoadEdit? road, Func<System.Numerics.Vector2, float> ground, Color colour)
    {
        if (road is null) { _selection.Mesh = null; return; }
        var c = RoadBuilder.Centres(road);
        var h = c.Select(ground).ToArray();
        colour.A = 1;
        _selection.Mesh = Ribbon(c, h, _ => colour, 0.3f, 5.4f);
        ((StandardMaterial3D)_selection.MaterialOverride!).AlbedoColor = new Color(1, 1, 1, 0.38f);
    }

    // ---- handles -----------------------------------------------------------------------------------------------------------

    public void ShowHandles(RoadEdit? road, Func<System.Numerics.Vector2, float> ground, int selected, int hover, float scale)
    {
        var mm = _handles.Multimesh;
        if (road is null) { mm.InstanceCount = 0; return; }
        int n = road.Points.Count;
        mm.InstanceCount = n;
        for (int i = 0; i < n; i++)
        {
            var c = road.Points[i].Centre;
            bool end = i == 0 || i == n - 1;
            float s = scale * (i == selected || i == hover ? 1.5f : end ? 1.25f : 1f);
            mm.SetInstanceTransform(i, new Transform3D(Basis.FromScale(new Vector3(s, s, s)), G(c, ground(c) + 0.5f)));
            mm.SetInstanceColor(i, i == selected ? new Color(1f, 0.6f, 0.1f) : i == hover ? new Color(1f, 1f, 0.5f) : end ? new Color(0.4f, 0.9f, 1f) : new Color(1f, 1f, 1f));
        }
    }

    // ---- snap marker ---------------------------------------------------------------------------------------------------------

    public void ShowMarker(System.Numerics.Vector2? at, float height, SnapKind kind, float scale)
    {
        if (at is not { } p || kind == SnapKind.None) { _marker.Visible = false; return; }
        _marker.Visible = true;
        var col = kind switch
        {
            SnapKind.Structure => new Color(0.3f, 0.85f, 1f),
            SnapKind.RoadEnd => new Color(0.4f, 1f, 0.4f),
            SnapKind.RoadMiddle => new Color(1f, 0.55f, 0.15f),
            _ => new Color(1f, 1f, 1f),
        };
        float s = scale * (kind is SnapKind.Angle or SnapKind.Grid ? 0.55f : 1.1f);
        _marker.Transform = new Transform3D(Basis.FromScale(new Vector3(s, s, s)), G(p, height + 0.6f));
        ((StandardMaterial3D)_marker.MaterialOverride!).AlbedoColor = col with { A = 0.95f };
    }

    // ---- bridge and tunnel stretches -----------------------------------------------------------------------------------------

    private readonly Node3D _stretch = new() { Name = "RoadStretch" };
    private StandardMaterial3D? _ghostMat;
    private bool _stretchAdded;

    public override void _ExitTree()
    {
        if (!_stretchAdded && IsInstanceValid(_stretch)) _stretch.Free();   // never added: it would stay a stray node
    }

    /// <summary>
    /// The pieces of a bridge or tunnel stretch as translucent copies of their models at the planned heights and pitch, a ribbon along the deck
    /// (green, amber when the plan has warnings), and a post at each joint down to the ground where the ground will be raised for it.
    /// </summary>
    public void ShowStretch(StretchPlan? plan, Func<CatalogueItem, Mesh?> meshOf, Func<System.Numerics.Vector2, float> ground)
    {
        if (!_stretchAdded) { AddChild(_stretch); _stretchAdded = true; }
        foreach (var c in _stretch.GetChildren()) { _stretch.RemoveChild(c); c.QueueFree(); }
        if (plan is null || !plan.Ok) return;
        bool warn = plan.Warnings.Count > 0;
        _ghostMat ??= new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, EmissionEnabled = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _ghostMat.AlbedoColor = warn ? new Color(1f, 0.82f, 0.3f, 0.6f) : new Color(0.4f, 1f, 0.45f, 0.6f);
        _ghostMat.Emission = warn ? new Color(0.4f, 0.28f, 0.04f) : new Color(0.1f, 0.35f, 0.12f);
        foreach (var p in plan.Pieces)
        {
            if (meshOf(p.Item) is not { } mesh) continue;
            var rot = I76.Editor.Roads.RoadStructures.Rotation(p.Yaw, p.Pitch);
            _stretch.AddChild(new MeshInstance3D
            {
                Mesh = mesh, MaterialOverride = _ghostMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = Coords.ToGodot(rot, new System.Numerics.Vector3(p.Origin.X, p.OriginY, p.Origin.Y)),
            });
        }
        var col = warn ? Band(1) : Band(0);
        _stretch.AddChild(new MeshInstance3D { Mesh = Ribbon(plan.Samples, plan.Deck, _ => col, 0.12f, 0.8f), MaterialOverride = _ribbon.MaterialOverride, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // the joints of a bridge: where the ground is brought up to the deck
        if (plan.Mode == BuildMode.Bridge)
            foreach (var p in plan.Pieces.Skip(1))
            {
                float g = ground(p.Origin);
                if (p.OriginY - g < 0.5f) continue;
                var mid = p.Origin + new System.Numerics.Vector2(MathF.Cos(p.Yaw), -MathF.Sin(p.Yaw)) * 5f;
                _stretch.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(1.2f, p.OriginY - g, 1.2f) }, MaterialOverride = _ribbon.MaterialOverride, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = G(mid, (p.OriginY + g) / 2),
                });
            }
    }

    public void Clear()
    {
        ShowStretch(null, _ => null, _ => 0);
        ShowRoad(null, null); ShowGrade(null, null, null, 0); ShowSelection(null, _ => 0, Colors.White); ShowHandles(null, _ => 0, -1, -1, 1); _marker.Visible = false;
    }
}
