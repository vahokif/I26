using I76.Formats.Assembly;
using I76.Formats.Mission;
using SN = System.Numerics;

namespace I76.Game.World;

/// <summary>
/// Where the 1997 look's ground is drawn: the height of the terrain mesh TerrainNode builds (needles levelled, each cell
/// cut along TerrainTriangles' diagonal) or, inside a road quad, of the road mesh RoadNode draws on top of it. The
/// simulation's ground is the bilinear heightfield, which differs from both inside a cell whose corners are not on
/// one plane; things drawn onto the ground (the skid marks) take their height from here. Rendering only.
/// </summary>
public sealed class Drawn1997
{
    private const int CellShift = 4;    // road quads are binned in 16 m cells
    private readonly MissionWorld _world;
    private readonly Dictionary<(int, int), List<(SN.Vector3 A, SN.Vector3 B, SN.Vector3 C, SN.Vector3 D)>> _quads = [];

    public Drawn1997(MissionWorld world, IReadOnlyList<RoadPiece>? pieces)
    {
        _world = world;
        if (pieces is null) return;
        foreach (var piece in pieces)
            for (int j = 0; j + 1 < piece.Points.Length; j++)
            {
                // RoadNode's corners: a = Right j, b = Left j, c = Left j+1, d = Right j+1; triangles (a, c, b) and (a, c, d)
                var q = (piece.Points[j].Right, piece.Points[j].Left, piece.Points[j + 1].Left, piece.Points[j + 1].Right);
                int x0 = (int)MathF.Floor(MathF.Min(MathF.Min(q.Item1.X, q.Item2.X), MathF.Min(q.Item3.X, q.Item4.X))) >> CellShift;
                int x1 = (int)MathF.Floor(MathF.Max(MathF.Max(q.Item1.X, q.Item2.X), MathF.Max(q.Item3.X, q.Item4.X))) >> CellShift;
                int z0 = (int)MathF.Floor(MathF.Min(MathF.Min(q.Item1.Z, q.Item2.Z), MathF.Min(q.Item3.Z, q.Item4.Z))) >> CellShift;
                int z1 = (int)MathF.Floor(MathF.Max(MathF.Max(q.Item1.Z, q.Item2.Z), MathF.Max(q.Item3.Z, q.Item4.Z))) >> CellShift;
                for (int cz = z0; cz <= z1; cz++)
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        if (!_quads.TryGetValue((cx, cz), out var l)) _quads[(cx, cz)] = l = [];
                        l.Add(q);
                    }
            }
    }

    private int Sample(int sx, int sz) => (_world.Terrain.Sample(sx, sz) ?? 0) & MissionFile.HeightMask;
    private float Vertex(int sx, int sz) => (float)(TerrainTriangles.Despiked(Sample, sx, sz) * MissionWorld.HeightScale);

    /// <summary>The terrain mesh's height at a game-space (x, z).</summary>
    public float Terrain(double x, double z) => TerrainTriangles.DrawnHeight(Vertex, x / 5.0, z / 5.0);

    /// <summary>The road quad's height at (x, z) if a road is drawn there.</summary>
    public float? Road(double x, double z)
    {
        if (!_quads.TryGetValue(((int)Math.Floor(x) >> CellShift, (int)Math.Floor(z) >> CellShift), out var list)) return null;
        float? best = null;
        foreach (var (a, b, c, d) in list)
        {
            float? h = InTriangle(a, c, b, x, z) ?? InTriangle(a, c, d, x, z);
            if (h is { } v && (best is null || v > best)) best = v;
        }
        return best;
    }

    /// <summary>The drawn ground at (x, z): the road over the terrain.</summary>
    public float Height(double x, double z) => Road(x, z) ?? Terrain(x, z);

    private static float? InTriangle(SN.Vector3 p0, SN.Vector3 p1, SN.Vector3 p2, double x, double z)
    {
        double d = (double)(p1.Z - p2.Z) * (p0.X - p2.X) + (double)(p2.X - p1.X) * (p0.Z - p2.Z);
        if (Math.Abs(d) < 1e-9) return null;
        double l0 = ((p1.Z - p2.Z) * (x - p2.X) + (p2.X - p1.X) * (z - p2.Z)) / d;
        double l1 = ((p2.Z - p0.Z) * (x - p2.X) + (p0.X - p2.X) * (z - p2.Z)) / d;
        double l2 = 1 - l0 - l1;
        const double e = -1e-6;
        if (l0 < e || l1 < e || l2 < e) return null;
        return (float)(l0 * p0.Y + l1 * p1.Y + l2 * p2.Y);
    }
}
