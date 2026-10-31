using System.Numerics;
using I76.Formats.Mission;

namespace I76.Formats.Assembly;

/// <summary>A road point: the two edges of the road, as the engine keeps them.</summary>
public readonly record struct RoadEdge(Vector3 Left, Vector3 Right);

/// <summary>A run of road points drawn as one strip; quads are numbered from 0 within it.</summary>
public sealed record RoadPiece(uint Kind, RoadEdge[] Points);

/// <summary>
/// Roads as the original game builds and draws them:
///
/// RSEG point record, 24 bytes, loaded verbatim by the RSEG handler
/// Left edge (x, y, z) then right edge (x, y, z). The file's
/// y words are not heights (left.y holds the centre z, right.y 0 or 36);
/// The original overwrites both with the bilinear terrain
/// height, at load.
///
/// The original cuts each segment into pieces of at most 25 points that
/// share their end points; a final piece of a single quad is dropped.
///
/// It draws each consecutive pair of points as one quad
/// (right_j, left_j, left_j+1, right_j+1) with UVs (0,0) (1,0) (1,1) (0,1):
/// one whole texture per quad, u across the road. The texture is
/// TextureTable[kind % 3][(quadIndex % 10) + (viewDepth &lt; 60 ? 0 : 10)]
/// into a list of 30 names: the mission's &lt;name&gt;.rtm if there is one
/// (none of the 40 campaign missions has), else the defaults below.
/// </summary>
public static class Roads
{
    public static readonly string[] DefaultTextures =
    [
        "r2anr_11.map", "r2anr_21.map", "r2anr_31.map", "r2anr_41.map", "r2anr_51.map",
        "r2anr_61.map", "r2anr_71.map", "r2anr_81.map", "r2anr_91.map", "r2anr_01.map",
        "r2ayr_11.map", "r2ayr_21.map", "r2ayr_31.map", "r2ayr_41.map", "r2ayr_51.map",
        "r2ayr_61.map", "r2ayr_71.map", "r2ayr_81.map", "r2ayr_91.map", "r2ayr_01.map",
        "r2dnr_17.map", "r2dnr_27.map", "r2dnr_37.map", "r2dnr_47.map", "r2dnr_57.map",
        "r2wnr_19.map", "r2wnr_29.map", "r2wnr_39.map", "r2wnr_49.map", "r2wnr_59.map",
    ];

    /// <summary>Per kind % 3, 20 indices into the texture list; the second ten are the far set.</summary>
    public static readonly int[][] TextureTable =
    [
        [0, 10, 1, 11, 2, 12, 3, 13, 4, 14, 5, 15, 6, 16, 7, 17, 8, 18, 9, 19],
        [20, 21, 22, 23, 24, 20, 21, 22, 23, 24, 20, 21, 22, 23, 24, 20, 21, 22, 23, 24],
        [25, 26, 27, 28, 29, 25, 26, 27, 28, 29, 25, 26, 27, 28, 29, 25, 26, 27, 28, 29],
    ];

    /// <summary>Near/far switch on view depth, and the distance past which a quad is not drawn.</summary>
    public const float FarDepth = 60f, DrawDepth = 450f;

    public static int TextureIndex(uint kind, int quad, bool far) => TextureTable[kind % 3][quad % 10 + (far ? 10 : 0)];

    /// <summary>The texture list for a mission: its .rtm (one name per line) or the defaults.</summary>
    public static string[] TextureList(Func<string, byte[]?> read, string missionName)
    {
        var names = (string[])DefaultTextures.Clone();
        if (read(missionName + ".rtm") is { } d)
        {
            var words = Bin.Latin1(d).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < names.Length && i < words.Length; i++) names[i] = words[i];
        }
        return names;
    }

    public static List<RoadPiece> Build(MissionFile msn, Func<double, double, double> height)
    {
        var o = new List<RoadPiece>();
        foreach (var seg in Segments(msn, height))
            foreach (var (start, count) in Split(seg.Points.Length))
                o.Add(new RoadPiece(seg.Kind, seg.Points.AsSpan(start, count).ToArray()));
        return o;
    }

    /// <summary>Each RSEG whole, before the piece split: its points with their edges on the bilinear terrain height.</summary>
    public static List<RoadPiece> Segments(MissionFile msn, Func<double, double, double> height) =>
        msn.RoadSegments().Select(seg => new RoadPiece(seg.Kind, seg.Points.Select(p =>
        {
            float lx = Bin.F32(p.Raw, 0), lz = Bin.F32(p.Raw, 8), rx = Bin.F32(p.Raw, 12), rz = Bin.F32(p.Raw, 20);
            return new RoadEdge(new Vector3(lx, (float)height(lx, lz), lz), new Vector3(rx, (float)height(rx, rz), rz));
        }).ToArray())).ToList();

    /// <summary>The original's piece split, as (first point, point count) runs.</summary>
    public static IEnumerable<(int Start, int Count)> Split(int points)
    {
        int quads = points - 1;
        int step = quads;
        while (step > 24) step = (step & 1) + (step >> 1);
        int rem = quads - step, consumed = 0;
        if (rem > 4)
        {
            int left = rem;
            do
            {
                yield return (consumed, step + 1);
                consumed += step;
                left -= step;
            } while (left > 4);
        }
        int last = step + (rem - consumed);
        if (last > 1) yield return (consumed, last + 1);
    }
}
