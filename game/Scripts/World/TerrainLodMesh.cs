using Godot;
using I76.Formats.Assembly;

namespace I76.Game.World;

/// <summary>
/// One chunk of the modern look's terrain at one level of detail, from
/// <see cref="TerrainSurface"/>: level 0 is 1.25 m between vertices, level 1
/// 2.5 m, level 2 5 m (the sample grid), level 3 10 m, level 4 20 m (the far ground, which at 1 km and
/// more has triangles of a few pixels at the finer grids and cost the Far draw distance most of its frame). Built off the main thread as plain
/// arrays; <see cref="ToMesh"/> makes the Godot mesh.
///
/// Every vertex of levels 0 to 3 carries where it would be on the next
/// coarser level (CUSTOM0.xyz, chunk-local) and that level's normal there
/// (CUSTOM1.xyz); terrain_modern.gdshader slides it there with distance from
/// the camera, so by the distance a chunk changes level it already has the
/// coarser level's shape and nothing pops (continuous LOD, as CDLOD does it).
/// CUSTOM0.w is the level + 1, 0 for a vertex that does not move (the coarsest level).
/// The coarser level's triangles are split along the same diagonal, (i+1, j)–(i, j+1),
/// so its corners, edge midpoints and the diagonal's midpoint are where the finer
/// vertices land.
/// </summary>
public sealed class TerrainLodMesh
{
    public const int Levels = 5;

    /// <summary>
    /// Level ranges, metres from the camera to a chunk's box: level l nearer than Range[l], else the coarsest. terrain_modern.gdshader
    /// finishes each level's morph <see cref="Settle"/> metres inside its range (<see cref="MorphEnd"/>), so a camera that close to
    /// the main one (the mirror's) sees no seam.
    /// </summary>
    public static readonly float[] Range = [100f, 450f, 900f, 2400f];
    /// <summary>Where each level's vertices start sliding to the next level's shape, metres from the camera (terrain_modern.gdshader's morph_start).</summary>
    public static readonly float[] MorphStart = [40f, 360f, 730f, 1800f];
    public const float Settle = 30f;
    public static float MorphEnd(int level) => Range[level] - Settle;
    /// <summary>
    /// The farthest a vertex on the edge between a level-l chunk and a level-(l+1) one can be, beyond Range[l]: the edge is part of the
    /// coarser chunk's box (at least Range[l] away), and runs along a finer chunk's box, which reaches 160 m in a line and, padded by the
    /// surface's freedom, 160 m x sqrt 2 + 28 m across. The level-l side is fully morphed there (MorphEnd[l] &lt; Range[l]); the coarser
    /// side must not have begun to morph (MorphStart[l+1] &gt;= Range[l] + EdgeReach), or its edge slides off the finer one's and the
    /// ground cracks open along the chunk boundary (the sky shows through on a steep face).
    /// </summary>
    public const float EdgeReach = 254f;
    public Vector3[] Vertices = [];
    public Vector3[] Normals = [];
    public Vector2[] Uv = [];
    public float[] Custom0 = [];
    public float[] Custom1 = [];
    /// <summary>The broad normal (TerrainSurface.BroadNormal), game-frame x and z: the shading of paved road.</summary>
    public float[] Custom2 = [];
    public int[] Indices = [];
    public Aabb Bounds;

    /// <summary>Quads along a chunk's edge at a level: 128, 64, 32 (the sample grid), 16, 8.</summary>
    public static int Quads(int level) => TerrainNode.Chunk * 4 >> level;

    public static TerrainLodMesh Build(ITerrainSurface surf, int x0, int z0, int level)
    {
        int n = Quads(level) + 1;
        double stride = (double)TerrainNode.Chunk / Quads(level);   // samples between vertices
        const int pad = 2;
        int m = n + 2 * pad;
        // the drawn points with a border of two, for this level's normals and the next one's
        var p = new System.Numerics.Vector3[m * m];
        var q = new System.Numerics.Vector3[m * m];   // the points the shading normal is taken from
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                double fx = x0 + (i - pad) * stride, fz = z0 + (j - pad) * stride;
                var v = surf.Point(fx, fz, out double shade);
                p[j * m + i] = v;
                q[j * m + i] = new System.Numerics.Vector3(v.X, (float)shade, v.Z);
            }
        System.Numerics.Vector3 P(int i, int j) => p[(j + pad) * m + i + pad];
        // the normal at grid point (i, j) from the neighbours `st` points away (game frame)
        System.Numerics.Vector3 N(int i, int j, int st)
        {
            var tx = q[(j + pad) * m + i + st + pad] - q[(j + pad) * m + i - st + pad];
            var tz = q[(j + st + pad) * m + i + pad] - q[(j - st + pad) * m + i + pad];
            var c = System.Numerics.Vector3.Cross(tz, tx);
            return System.Numerics.Vector3.Normalize(c);
        }
        var mesh = new TerrainLodMesh
        {
            Vertices = new Vector3[n * n], Normals = new Vector3[n * n], Uv = new Vector2[n * n],
            Custom0 = new float[n * n * 4], Custom1 = new float[n * n * 4], Custom2 = new float[n * n * 2],
        };
        float ox = x0 * (float)TerrainSurface.Spacing, oz = z0 * (float)TerrainSurface.Spacing;
        Vector3 Local(System.Numerics.Vector3 g) => new(g.X - ox, g.Y, -(g.Z - oz));
        bool morph = level < Levels - 1;
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                var g = P(i, j);
                var loc = Local(g);
                mesh.Vertices[k] = loc;
                min = min.Min(loc); max = max.Max(loc);
                var nn = N(i, j, 1);
                mesh.Normals[k] = new Vector3(nn.X, nn.Y, -nn.Z);
                mesh.Uv[k] = new Vector2(g.X / (float)TerrainSurface.Spacing, g.Z / (float)TerrainSurface.Spacing);
                var bn = surf.BroadNormal(x0 + i * stride, z0 + j * stride);
                mesh.Custom2[k * 2] = bn.X; mesh.Custom2[k * 2 + 1] = bn.Z;
                if (!morph) continue;
                // where the next level has this point: its own vertex, an edge's midpoint, or the diagonal's
                System.Numerics.Vector3 tp, tn;
                bool ox1 = (i & 1) == 1, oz1 = (j & 1) == 1;
                if (!ox1 && !oz1) { tp = g; tn = N(i, j, 2); }
                else if (ox1 && !oz1) { tp = (P(i - 1, j) + P(i + 1, j)) * 0.5f; tn = N(i - 1, j, 2) + N(i + 1, j, 2); }
                else if (!ox1) { tp = (P(i, j - 1) + P(i, j + 1)) * 0.5f; tn = N(i, j - 1, 2) + N(i, j + 1, 2); }
                else { tp = (P(i + 1, j - 1) + P(i - 1, j + 1)) * 0.5f; tn = N(i + 1, j - 1, 2) + N(i - 1, j + 1, 2); }
                tn = System.Numerics.Vector3.Normalize(tn);
                var tl = Local(tp);
                min = min.Min(tl); max = max.Max(tl);
                mesh.Custom0[k * 4] = tl.X; mesh.Custom0[k * 4 + 1] = tl.Y; mesh.Custom0[k * 4 + 2] = tl.Z;
                mesh.Custom0[k * 4 + 3] = level + 1;
                mesh.Custom1[k * 4] = tn.X; mesh.Custom1[k * 4 + 1] = tn.Y; mesh.Custom1[k * 4 + 2] = -tn.Z;
            }
        mesh.Bounds = new Aabb(min, max - min);
        int c = n - 1;
        var idx = new int[c * c * 6];
        int w = 0;
        for (int j = 0; j < c; j++)
            for (int i = 0; i < c; i++)
            {
                int a = j * n + i, b = a + 1, cc = a + n, d = cc + 1;
                // clockwise seen from above (Godot's front face), split (i+1, j)–(i, j+1) as the old mesh was
                idx[w++] = a; idx[w++] = cc; idx[w++] = b;
                idx[w++] = b; idx[w++] = cc; idx[w++] = d;
            }
        mesh.Indices = idx;
        return mesh;
    }

    public ArrayMesh ToMesh()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Normals;
        arrays[(int)Mesh.ArrayType.TexUV] = Uv;
        arrays[(int)Mesh.ArrayType.Custom0] = Custom0;
        arrays[(int)Mesh.ArrayType.Custom1] = Custom1;
        arrays[(int)Mesh.ArrayType.Custom2] = Custom2;
        arrays[(int)Mesh.ArrayType.Index] = Indices;
        var flags = (Mesh.ArrayFormat)(((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                                     | ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
                                     | ((long)Mesh.ArrayCustomFormat.RgFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift));
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: flags);
        return mesh;
    }
}
