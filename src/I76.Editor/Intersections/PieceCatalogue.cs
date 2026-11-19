using System.Numerics;

namespace I76.Editor.Intersections;

/// <summary>What a piece joins (docs/map-format-writing.md, "Intersections").</summary>
public enum PieceShape
{
    /// <summary>A 10 x 10 square, three arms: a through road (local z) and a stem to +x. ita21_1, itd27_1, itisect1/2.</summary>
    T,
    /// <summary>The same square with a fourth arm: two roads crossing at right angles. ixa21_1, ixd27_1, ixisect1/2.</summary>
    X,
    /// <summary>A 10 x 25 strip with a stub leaving to its +x side at 41 degrees, forward: a through road with a branch on its right. iga2x_1, igd27_1, igw29_1, iyisect1-3.</summary>
    ForkRight,
    /// <summary>The mirror image: the branch on the left. ila2x_1, ild27_1, ilw29_1, iyisect4-6.</summary>
    ForkLeft,
    /// <summary>
    /// The asphalt J strip (10 x 20, ija2n71, idisect1): where an asphalt road turns to dirt. Arm 0 (the near edge) meets the asphalt (a road's
    /// end or a T's or an X's arm), arm 1 (the far edge) the dirt road or a dirt cap. The stock maps lay one on 96 of the 101 asphalt T and X
    /// arms whose road is dirt, between an asphalt road and a dirt one on a straight road, and before a dirt cap at an asphalt road's end.
    /// </summary>
    Strip,
    /// <summary>An end cap (10 x 10, the far edge slanted): ijd27t1, idisect2 (dirt), ijw29t1 (planks). One arm, the near edge; the road ends on it.</summary>
    Cap,
    /// <summary>A turn-around at an asphalt road's end (a 20 x 20 octagon): ica2n_1, ieisect1. One arm, the middle of the near edge.</summary>
    TurnAround,
}

/// <summary>An arm: where a road meets the piece (the middle of an edge), and the heading leaving the piece, radians from +z toward +x, in the piece's own frame.</summary>
public readonly record struct Arm(Vector2 Position, float Heading)
{
    /// <summary>The unit vector leaving the piece.</summary>
    public Vector2 Out => new(MathF.Sin(Heading), MathF.Cos(Heading));
}

/// <summary>One kind of piece as a stock map places it: its object name, what it joins, the road kind it is painted for, its arms and its top faces (model frame, x and z).</summary>
public sealed record PieceDef(string Name, PieceShape Shape, uint Kind, int Variant, IReadOnlyList<Arm> Arms, IReadOnlyList<Vector2[]> Polygons)
{
    /// <summary>The ODEF object type the stock files give this surface: 0x50 asphalt, 0x52 dirt, 0x53 planks (the road-piece row, 0x50-0x5f, the loader paints).</summary>
    public uint ObjectType => Kind switch { 0 => 0x50u, 1 => 0x52u, _ => 0x53u };
    /// <summary>Arms 0 and 1 face each other along local z (heading 180 and 0): the through road of a T, a fork or an X; the others are stems (for an X, the crossing road).</summary>
    public const int Back = 0, Forward = 1;
    /// <summary>Where a road from the middle of a through road meets it, in the piece's frame (the centre of a T or an X; a fork's branch line meets the strip's axis here).</summary>
    public Vector2 Origin => Shape switch
    {
        PieceShape.ForkRight => new(5f, PieceCatalogue.ForkOriginZ),
        PieceShape.ForkLeft => new(20f, PieceCatalogue.ForkOriginZ),
        PieceShape.Strip => new(5f, 10f),
        PieceShape.TurnAround => new(10f, 10f),
        _ => new(5f, 5f),
    };
    /// <summary>A piece at a road's end (a cap, a turn-around, a J strip), placed with the road tool's road-end choice, not by the junction rules.</summary>
    public bool IsEnd => Shape is PieceShape.Strip or PieceShape.Cap or PieceShape.TurnAround;
    /// <summary>How far from its <see cref="Origin"/> another piece's must keep (the junction rules do not put two pieces on top of each other).</summary>
    public float Reach => Shape switch
    {
        PieceShape.ForkLeft or PieceShape.ForkRight => 14f,
        PieceShape.TurnAround => 13f,
        PieceShape.Strip => 10.5f,
        _ => 7.2f,
    };
}

/// <summary>The stock intersection pieces: geometry surveyed from the .sdf files, arms from the stock maps' roads (docs/map-format-writing.md).</summary>
public static class PieceCatalogue
{
    /// <summary>The branch's heading off the through road in a fork, degrees (the stub's edges lie at 41, the stock roads leave at about 45).</summary>
    public const float ForkDegrees = 40.9f;
    /// <summary>Where the stub's centre line meets the strip's axis, along the strip.</summary>
    public const float ForkOriginZ = 2.73f;
    public const float Half = 5f;

    private static readonly Vector2[] Square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
    private static readonly float Fork = ForkDegrees * MathF.PI / 180f;

    private static Arm[] TArms() => [new(new(5, 0), MathF.PI), new(new(5, 10), 0), new(new(10, 5), MathF.PI / 2)];
    private static Arm[] XArms() => [.. TArms(), new(new(0, 5), -MathF.PI / 2)];
    private static Arm[] GArms() => [new(new(5, 0), MathF.PI), new(new(5, 25), 0), new(new(20, 20), Fork)];
    private static Arm[] LArms() => [new(new(20, 0), MathF.PI), new(new(20, 25), 0), new(new(5, 20), -Fork)];

    private static readonly Vector2[][] GPolys =
    [
        [new(0, 0), new(10, 0), new(10, 25), new(0, 25)],
        [new(10, 0.9f), new(23.8f, 16.7f), new(16.2f, 23.3f), new(10, 16.1f)],
    ];

    private static readonly Vector2[][] LPolys = GPolys.Select(p => p.Select(v => new Vector2(25 - v.X, v.Y)).Reverse().ToArray()).ToArray();

    private static Arm[] StripArms() => [new(new(5, 0), MathF.PI), new(new(5, 20), 0)];
    private static Arm[] CapArms() => [new(new(5, 0), MathF.PI)];
    private static Arm[] TurnArms() => [new(new(10, 0), MathF.PI)];
    private static readonly Vector2[] StripPoly = [new(0, 0), new(10, 0), new(10, 20), new(0, 20)];
    /// <summary>The cap's two top faces: the square with its far edge slanted (z 9.1 at x 10 to 8 at x 0, bulging to 10 at x 5.9).</summary>
    private static readonly Vector2[][] CapPolys =
    [
        [new(0, 0), new(8, 8.6f), new(5.9f, 10), new(2.5f, 9.3f), new(0, 8)],
        [new(0, 0), new(10, 0), new(10, 9.1f), new(8, 8.6f)],
    ];
    private static readonly Vector2[] Octagon = [new(5, 0), new(15, 0), new(20, 6), new(20, 14), new(15, 20), new(5, 20), new(0, 14), new(0, 6)];

    public static readonly IReadOnlyList<PieceDef> All = Build();

    private static List<PieceDef> Build()
    {
        var l = new List<PieceDef>();
        // asphalt comes in three looks (the middle digit of the name, the texture's variant), dirt and planks in one
        for (int v = 1; v <= 3; v++)
        {
            l.Add(new($"ita2{v}_1", PieceShape.T, 0, v, TArms(), [Square]));
            l.Add(new($"ixa2{v}_1", PieceShape.X, 0, v, XArms(), [Square]));
            l.Add(new($"iga2{v}_1", PieceShape.ForkRight, 0, v, GArms(), GPolys));
            l.Add(new($"ila2{v}_1", PieceShape.ForkLeft, 0, v, LArms(), LPolys));
        }
        l.Add(new("itd27_1", PieceShape.T, 1, 1, TArms(), [Square]));
        l.Add(new("ixd27_1", PieceShape.X, 1, 1, XArms(), [Square]));
        l.Add(new("igd27_1", PieceShape.ForkRight, 1, 1, GArms(), GPolys));
        l.Add(new("ild27_1", PieceShape.ForkLeft, 1, 1, LArms(), LPolys));
        l.Add(new("igw29_1", PieceShape.ForkRight, 2, 1, GArms(), GPolys));
        l.Add(new("ilw29_1", PieceShape.ForkLeft, 2, 1, LArms(), LPolys));
        // the road ends: the asphalt J strip (asphalt to dirt) and turn-around in the three looks, the dirt and planks caps
        for (int v = 1; v <= 3; v++)
        {
            l.Add(new($"ija2{v}71", PieceShape.Strip, 0, v, StripArms(), [StripPoly]));
            l.Add(new($"ica2{v}_1", PieceShape.TurnAround, 0, v, TurnArms(), [Octagon]));
        }
        l.Add(new("ijd27t1", PieceShape.Cap, 1, 1, CapArms(), CapPolys));
        l.Add(new("ijw29t1", PieceShape.Cap, 2, 1, CapArms(), CapPolys));
        // the library's own names for the same meshes (the maps use both): same geometry, same texture
        l.Add(new("itisect1", PieceShape.T, 0, 1, TArms(), [Square]));
        l.Add(new("itisect2", PieceShape.T, 1, 1, TArms(), [Square]));
        l.Add(new("ixisect1", PieceShape.X, 0, 1, XArms(), [Square]));
        l.Add(new("ixisect2", PieceShape.X, 1, 1, XArms(), [Square]));
        l.Add(new("iyisect1", PieceShape.ForkRight, 0, 1, GArms(), GPolys));
        l.Add(new("iyisect2", PieceShape.ForkRight, 1, 1, GArms(), GPolys));
        l.Add(new("iyisect3", PieceShape.ForkRight, 2, 1, GArms(), GPolys));
        l.Add(new("iyisect4", PieceShape.ForkLeft, 0, 1, LArms(), LPolys));
        l.Add(new("iyisect5", PieceShape.ForkLeft, 1, 1, LArms(), LPolys));
        l.Add(new("iyisect6", PieceShape.ForkLeft, 2, 1, LArms(), LPolys));
        l.Add(new("idisect1", PieceShape.Strip, 0, 1, StripArms(), [StripPoly]));
        l.Add(new("idisect2", PieceShape.Cap, 1, 1, CapArms(), CapPolys));
        l.Add(new("ieisect1", PieceShape.TurnAround, 0, 1, TurnArms(), [Octagon]));
        return l;
    }

    private static readonly Dictionary<string, PieceDef> ByName = All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The definition of an object name, or null when it is not one of the road tool's pieces. The pieces placed by hand are not (the
    /// object palette's Road pieces: the runway, the start and finish lines, the forks without a yellow line, the dirt split iyd27_1).
    /// </summary>
    public static PieceDef? Lookup(string name) => ByName.GetValueOrDefault(name.Trim().TrimStart('#'));

    /// <summary>The piece of a shape for a road kind (RSEG kind % 3) in the asphalt look <paramref name="variant"/> (1-3), or null when the game has none (no planks T or X, no asphalt cap, no dirt or planks turn-around or J strip).</summary>
    public static PieceDef? Pick(PieceShape shape, uint kind, int variant = 1)
    {
        kind %= 3;
        var of = All.Where(d => d.Shape == shape && d.Kind == kind && !d.Name.Contains("isect")).ToList();
        return of.FirstOrDefault(d => d.Variant == variant) ?? of.FirstOrDefault();
    }

    /// <summary>The ODEF object type for a road-piece name (0x50 range), used when none is known.</summary>
    public static bool IsPieceType(uint objectType) => (objectType & 0xf0) == 0x50;
}
