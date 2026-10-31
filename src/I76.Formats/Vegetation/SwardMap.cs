using I76.Formats.Mission;

namespace I76.Formats.Vegetation;

/// <summary>
/// The sward over a mission's terrain (docs/modern-vegetation.md, The sward): a coarse map of how grassy the ground
/// between the plants is (<see cref="VegetationPlacer.Sward"/>), one byte a texel, over the bounds of the map's
/// tiles. The modern ground shader tints toward dry grass by it, so a grassland looks covered rather than dotted
/// with tufts on bare dirt; it follows the placer's patches and the grasses' clumps, so the tint lies where the
/// tufts are dense, and keeps off objects (the roads paint over it).
/// </summary>
public sealed class SwardMap
{
    /// <summary>Metres a texel covers.</summary>
    public const float Texel = 4f;
    /// <summary>Metres a texel covers over a map too big for that (<see cref="TerrainTiles.Huge"/>): 50 a tile, so the map stays a few thousand texels across.</summary>
    public const float HugeTexel = TerrainTiles.Tile * 5f / 50;
    /// <summary>The metres a texel of this map covers.</summary>
    public float TexelSize { get; init; } = Texel;
    /// <summary>The broad part (<see cref="VegetationPlacer.SwardBroad"/>) is read every this many texels and interpolated.</summary>
    public const int Broad = 4;
    /// <summary>The game-frame corner of texel (0, 0)'s edge, and the size in texels.</summary>
    public float X0 { get; init; }
    public float Z0 { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Row by row (game Z), 0 bare to 255 a closed sward.</summary>
    public byte[] Data { get; init; } = [];
    /// <summary>The mean over the texels on terrain (0..1), for the statistics.</summary>
    public float Mean { get; init; }

    private static float Bilerp(float a, float b, float c, float d, float tx, float tz) => a + (b - a) * tx + (c - a) * tz + (a - b - c + d) * tx * tz;

    public float At(float x, float z)
    {
        int i = (int)MathF.Floor((x - X0) / TexelSize), j = (int)MathF.Floor((z - Z0) / TexelSize);
        return i < 0 || j < 0 || i >= Width || j >= Height ? 0 : Data[j * Width + i] / 255f;
    }

    /// <summary>The sward under the map's tiles; empty for a biome without one.</summary>
    public static SwardMap Build(VegetationPlacer placer, TerrainTiles tiles)
    {
        var (r0, r1, c0, c1) = tiles.Bounds();
        if (placer.Biome.Sward <= 0 || r1 < r0) return new SwardMap();
        const int tile = TerrainTiles.Tile * 5;   // a tile's side, metres
        float Texel = tiles.Huge ? HugeTexel : SwardMap.Texel;
        int per = (int)(tile / Texel);
        int w = (c1 - c0 + 1) * per, h = (r1 - r0 + 1) * per;
        float x0 = c0 * tile, z0 = r0 * tile;
        var data = new byte[w * h];
        var sums = new double[h];
        var counts = new int[h];
        bool Occupied(float x, float z)
        {
            int col = (int)MathF.Floor(x / tile), row = (int)MathF.Floor(z / tile);
            return row >= 0 && col >= 0 && row < MissionFile.ZmapGrid && col < MissionFile.ZmapGrid && tiles.TileAt(row, col) != MissionFile.ZmapEmpty;
        }
        // the broad part on a lattice Broad texels apart (it varies over tens of metres), where any tile touches a node
        int bw = w / Broad + 1, bh = h / Broad + 1;
        var broad = new (float Cover, float Share)[bw * bh];
        float step = Broad * Texel;
        Parallel.For(0, bh, J =>
        {
            float z = z0 + J * step;
            for (int I = 0; I < bw; I++)
            {
                float x = x0 + I * step;
                if (Occupied(x - 1, z - 1) || Occupied(x + 1, z - 1) || Occupied(x - 1, z + 1) || Occupied(x + 1, z + 1))
                    broad[J * bw + I] = placer.SwardBroad(x, z);
            }
        });
        Parallel.For(0, h, j =>
        {
            int row = r0 + j / per;
            float z = z0 + (j + 0.5f) * Texel;
            float v = (j + 0.5f) / Broad;
            int J = Math.Min((int)v, bh - 2);
            float tz = v - J;
            for (int i = 0; i < w; i++)
            {
                int col = c0 + i / per;
                if (tiles.TileAt(row, col) == MissionFile.ZmapEmpty) continue;
                float u = (i + 0.5f) / Broad;
                int I = Math.Min((int)u, bw - 2);
                float tx = u - I;
                var a = broad[J * bw + I]; var b2 = broad[J * bw + I + 1]; var c = broad[(J + 1) * bw + I]; var d = broad[(J + 1) * bw + I + 1];
                float cover = Bilerp(a.Cover, b2.Cover, c.Cover, d.Cover, tx, tz), share = Bilerp(a.Share, b2.Share, c.Share, d.Share, tx, tz);
                float s = placer.SwardFine(x0 + (i + 0.5f) * Texel, z, cover, share);
                data[j * w + i] = (byte)Math.Clamp((int)(s * 255f + 0.5f), 0, 255);
                sums[j] += s; counts[j]++;
            }
        });
        int n = counts.Sum();
        return new SwardMap { X0 = x0, Z0 = z0, Width = w, Height = h, TexelSize = Texel, Data = data, Mean = n > 0 ? (float)(sums.Sum() / n) : 0 };
    }
}
