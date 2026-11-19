using Godot;
using I76.Editor.Intersections;

namespace I76.Game.Editor.Intersections;

/// <summary>The pieces the road tool is about to place, drawn over the ground while a road is being drawn: the piece's top faces, tinted, with their outlines.</summary>
public sealed partial class PieceView : Node3D
{
    private readonly MeshInstance3D _fill = new() { Name = "PieceFill", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MeshInstance3D _edge = new() { Name = "PieceEdge", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

    public PieceView()
    {
        Name = "PiecePreview";
        _fill.MaterialOverride = Mat(0.5f);
        _edge.MaterialOverride = Mat(1f);
        AddChild(_fill); AddChild(_edge);
    }

    private static StandardMaterial3D Mat(float alpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = true,
        AlbedoColor = new Color(1, 1, 1, alpha),
        RenderPriority = 11,
    };

    private static Vector3 G(System.Numerics.Vector2 c, float h) => new(c.X, h, -c.Y);

    /// <summary>Road-end pieces (the road tool's end choice) as a preview, drawn as a proposal's would be.</summary>
    public void ShowPieces(IReadOnlyList<ExtraPiece> pieces, Func<System.Numerics.Vector2, float> ground) =>
        Show(pieces.Count == 0 ? [] : [new Proposal(pieces[0].Def, pieces[0].Pose, default, []) { Extras = [.. pieces.Skip(1)] }], ground);

    public void Show(IReadOnlyList<Proposal> proposals, Func<System.Numerics.Vector2, float> ground)
    {
        if (proposals.Count == 0) { _fill.Mesh = null; _edge.Mesh = null; return; }
        var fv = new List<Vector3>(); var fc = new List<Color>(); var ev = new List<Vector3>(); var ec = new List<Color>();
        var tint = new Color(0.3f, 0.85f, 1f);
        foreach (var (def, pose) in proposals.SelectMany(pr => pr.Extras.Select(x => (x.Def, x.Pose)).Prepend((pr.Def, pr.Pose))))
            foreach (var poly in def.Polygons)
            {
                var w = poly.Select(pose.ToWorld).ToArray();
                // each corner at its own ground height, lifted a little: the piece is a plane in the game, the view is of where it lands
                var v = w.Select(p => G(p, ground(p) + 0.35f)).ToArray();
                for (int k = 1; k + 1 < v.Length; k++) { fv.AddRange([v[0], v[k], v[k + 1]]); for (int c = 0; c < 3; c++) fc.Add(tint); }
                for (int k = 0; k < v.Length; k++) { ev.AddRange([v[k], v[(k + 1) % v.Length]]); ec.Add(Colors.White); ec.Add(Colors.White); }
            }
        _fill.Mesh = Build(Mesh.PrimitiveType.Triangles, fv, fc);
        _edge.Mesh = Build(Mesh.PrimitiveType.Lines, ev, ec);
    }

    private static ArrayMesh Build(Mesh.PrimitiveType type, List<Vector3> v, List<Color> c)
    {
        var mesh = new ArrayMesh();
        if (v.Count == 0) return mesh;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = c.ToArray();
        mesh.AddSurfaceArrays(type, arrays);
        return mesh;
    }
}
