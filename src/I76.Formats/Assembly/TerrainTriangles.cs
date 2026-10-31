namespace I76.Formats.Assembly;

/// <summary>
/// How the 1997 look cuts each sample cell of its terrain into two triangles (rendering only; the simulation keeps the
/// bilinear heightfield).
///
/// One fixed diagonal everywhere turns every cliff rim that runs across it into a comb: where a rim steps diagonally from
/// sample to sample, each cell with one corner off the rim folds along the diagonal through that corner, a notch one
/// sample wide between two teeth, and the engine's per-vertex light (unnormalised gradient) shades the
/// teeth's faces black and white by turns. The maps' tall boundary walls (M14's 100 m plateau, T14's 130 m rims) are
/// edged with such steps and showed as striped saw-toothed cliffs on the skyline. The original never drew that comb:
/// its quadtree (the terrain quad subdivision) tests flatness only at tile centres and quarter points, and its leaves
/// go out as whole quads or fans (the original's terrain leaf faces), so its rims stand as solid walls. Here a cell folds
/// along the diagonal whose ends are nearer in height instead, so a stepped rim is chamfered rather than notched; level
/// cells, slopes and straight walls (both diagonals alike) keep the old diagonal, b–c.
/// </summary>
public static class TerrainTriangles
{
    /// <summary>
    /// The index list for an <paramref name="n"/>×<paramref name="n"/> grid of vertices, row-major (vertex j·n + i at
    /// column i, row j; row j+1 lies toward −Z in Godot, so the triangles are clockwise seen from above, Godot's front
    /// face), from the vertices' heights.
    /// </summary>
    public static int[] Indices(ReadOnlySpan<float> heights, int n)
    {
        int cells = n - 1;
        var idx = new int[cells * cells * 6];
        int q = 0;
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                if (MathF.Abs(heights[a] - heights[d]) < MathF.Abs(heights[b] - heights[c]))
                {
                    idx[q++] = a; idx[q++] = c; idx[q++] = d;
                    idx[q++] = a; idx[q++] = d; idx[q++] = b;
                }
                else
                {
                    idx[q++] = a; idx[q++] = c; idx[q++] = b;
                    idx[q++] = b; idx[q++] = c; idx[q++] = d;
                }
            }
        return idx;
    }

    /// <summary>
    /// The height the 1997 look draws at a sample (the mesh's own vertex height before the 0.1 scale): a lone sample more
    /// than 15 m above (or below) all eight neighbours is drawn level with them (a01 has four, up to 320 m tall; the
    /// original's quadtree never makes such a needle a vertex). Collision keeps the raw samples.
    /// </summary>
    public static int Despiked(Func<int, int, int> sample, int sx, int sz)
    {
        int h = sample(sx, sz), mx = int.MinValue, mn = int.MaxValue;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dz != 0) { int q = sample(sx + dx, sz + dz); mx = Math.Max(mx, q); mn = Math.Min(mn, q); }
        return h - mx > 150 ? mx : mn - h > 150 ? mn : h;
    }

    /// <summary>
    /// The height of the surface <see cref="Indices"/> draws at sample coordinates (fx, fz): the cell's two triangles, cut
    /// along the diagonal Indices picks (<paramref name="vertexHeight"/> gives the drawn height, in metres, of a sample).
    /// Rendering only; the simulation's ground is the bilinear heightfield.
    /// </summary>
    public static float DrawnHeight(Func<int, int, float> vertexHeight, double fx, double fz)
    {
        int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
        float tx = (float)(fx - i), tz = (float)(fz - j);
        float a = vertexHeight(i, j), b = vertexHeight(i + 1, j), c = vertexHeight(i, j + 1), d = vertexHeight(i + 1, j + 1);
        if (MathF.Abs(a - d) < MathF.Abs(b - c))
            return tz >= tx ? (1 - tz) * a + (tz - tx) * c + tx * d : (1 - tx) * a + (tx - tz) * b + tz * d;
        return tx + tz <= 1 ? a + tx * (b - a) + tz * (c - a) : d + (1 - tx) * (c - d) + (1 - tz) * (b - d);
    }
}
