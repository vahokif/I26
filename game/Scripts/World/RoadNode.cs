using Godot;
using I76.Formats.Assembly;
using I76.Game.Assets;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The mission's roads as the engine lays them out (see I76.Formats.Assembly.Roads):
/// one textured quad per pair of points, edges on the bilinear terrain height.
/// One mesh per road piece, a surface per texture, so each piece's floats stay local.
/// Drawn in 1997 mode; modern mode paints the roads into the terrain instead.
///
/// The engine swaps to a far texture set beyond view depth 60 and stops
/// drawing at 450; here the near set is used everywhere and mipmapping does
/// the far work (the far set is a lower-detail version, as the ground's
/// levels are).
/// </summary>
public partial class RoadNode : Node3D
{
    private readonly List<ShaderMaterial> _materials = [];
    public int Quads { get; private set; }
    /// <summary>The road light: Ly times the preset's intensity, plus ambient.</summary>
    public float LightY { get; set; } = 0.2588f;
    public float Ambient { get; set; }
    public List<string> MissingTextures { get; } = [];

    /// <param name="only">Which of a piece's quads (piece, index of the quad's first point) to make; all if null.</param>
    public void Build(IReadOnlyList<RoadPiece> pieces, string[] names, TextureResolver resolver, TextureFactory textures, Func<RoadPiece, int, bool>? only = null)
    {
        var shader = GD.Load<Shader>("res://Shaders/road.gdshader");
        var mats = new Dictionary<int, ShaderMaterial?>();
        ShaderMaterial? Mat(int idx)
        {
            if (mats.TryGetValue(idx, out var m)) return m;
            m = null;
            if (resolver.FindByStem(names[idx]) is { } member && textures.Get(member) is { } t)
            {
                m = new ShaderMaterial { Shader = shader };
                m.SetShaderParameter("light_y", LightY);
                m.SetShaderParameter("ambient", Ambient);
                m.SetShaderParameter("road_tex", t.Texture);
                _materials.Add(m);
            }
            else MissingTextures.Add(names[idx]);
            return mats[idx] = m;
        }

        foreach (var piece in pieces)
        {
            var origin = Coords.ToGodot(piece.Points[0].Left);
            var bySurface = new Dictionary<ShaderMaterial, (List<Vector3> V, List<Vector2> UV, List<Vector3> N)>();
            for (int j = 0; j + 1 < piece.Points.Length; j++)
            {
                if (only is not null && !only(piece, j)) continue;
                var mat = Mat(Roads.TextureIndex(piece.Kind, j, far: false));
                if (mat is null) continue;
                if (!bySurface.TryGetValue(mat, out var s)) bySurface[mat] = s = ([], [], []);
                var a = Coords.ToGodot(piece.Points[j].Right) - origin;      // u0 v0
                var b = Coords.ToGodot(piece.Points[j].Left) - origin;       // u1 v0
                var c = Coords.ToGodot(piece.Points[j + 1].Left) - origin;   // u1 v1
                var d = Coords.ToGodot(piece.Points[j + 1].Right) - origin;  // u0 v1
                var n = (c - a).Cross(b - a).Normalized();
                if (!(n.LengthSquared() > 0.5f)) n = Vector3.Up;   // a degenerate quad: no NaN lighting
                if (n.Y < 0) n = -n;
                foreach (var (p, uv) in new[] { (a, new Vector2(0, 0)), (b, new Vector2(1, 0)), (c, new Vector2(1, 1)),
                                                (a, new Vector2(0, 0)), (c, new Vector2(1, 1)), (d, new Vector2(0, 1)) })
                {
                    s.V.Add(p); s.UV.Add(uv); s.N.Add(n);
                }
                Quads++;
            }
            if (bySurface.Count == 0) continue;
            var mesh = new ArrayMesh();
            foreach (var (mat, s) in bySurface)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = s.V.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = s.UV.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = s.N.ToArray();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
            }
            AddChild(new MeshInstance3D { Mesh = mesh, Position = origin, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
    }

    public void SetMode1997(bool on)
    {
        foreach (var m in _materials) m.SetShaderParameter("mode_1997", on);
    }
}
