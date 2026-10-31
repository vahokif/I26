using I76.Formats.Mission;
using I76.Formats.Assembly;

namespace I76.Game.World;

/// <summary>
/// Which chunks of a map that fills the grid (<see cref="TerrainNode"/>'s streaming) a camera wants made and which it lets go:
/// pure arithmetic, so the invariant that nothing the camera draws is freed holds by test. A chunk is wanted when any of it is
/// within <c>radius</c> of the camera and freed only past <c>keep</c>, which is never less than that.
/// </summary>
public static class StreamPlan
{
    /// <summary>A chunk's size in metres.</summary>
    public const float ChunkMetres = TerrainNode.Chunk * TerrainNode.SampleSize;

    /// <summary>The distance in metres from (px, pz) to the box of the chunk at sample origin (x0, z0).</summary>
    public static float BoxDistance(float px, float pz, int x0, int z0)
    {
        float bx = x0 * TerrainNode.SampleSize, bz = z0 * TerrainNode.SampleSize;
        float dx = MathF.Max(0, MathF.Max(bx - px, px - (bx + ChunkMetres))), dz = MathF.Max(0, MathF.Max(bz - pz, pz - (bz + ChunkMetres)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Whether the camera lets the chunk at (x0, z0) go.</summary>
    public static bool Frees(float px, float pz, int x0, int z0, float keep) => BoxDistance(px, pz, x0, z0) > keep;

    /// <summary>The chunks within <paramref name="radius"/> of the camera that the map has (<paramref name="present"/> says by sample origin), with their distances.</summary>
    public static List<(float D, int X0, int Z0)> Within(Func<int, int, bool> present, float px, float pz, float radius)
    {
        int cells = MissionFile.ZmapGrid * TerrainTiles.Tile / TerrainNode.Chunk, span = (int)MathF.Ceiling(radius / ChunkMetres) + 1;
        int ccx = (int)MathF.Floor(px / ChunkMetres), ccz = (int)MathF.Floor(pz / ChunkMetres);
        var found = new List<(float, int, int)>();
        for (int cz = Math.Max(0, ccz - span); cz <= Math.Min(cells - 1, ccz + span); cz++)
            for (int cx = Math.Max(0, ccx - span); cx <= Math.Min(cells - 1, ccx + span); cx++)
            {
                int x0 = cx * TerrainNode.Chunk, z0 = cz * TerrainNode.Chunk;
                if (!present(x0, z0)) continue;
                float d = BoxDistance(px, pz, x0, z0);
                if (d <= radius) found.Add((d, x0, z0));
            }
        return found;
    }
}
