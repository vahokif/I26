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
    /// <summary>A stem that splits into two parallel arms 20 apart (30 x 45): iyd27_1, iyisect8. Recognised, never placed by the tool.</summary>
    Split,
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
    public Vector2 Origin => Shape is PieceShape.ForkRight ? new(5f, PieceCatalogue.ForkOriginZ) : Shape is PieceShape.ForkLeft ? new(20f, PieceCatalogue.ForkOriginZ) : new(5f, 5f);
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

    private static readonly Arm[] SplitArms = [new(new(15, 0), MathF.PI), new(new(5, 45), 0), new(new(25, 45), 0)];

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
        l.Add(new("iyd27_1", PieceShape.Split, 1, 1, SplitArms, []));
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
        l.Add(new("iyisect8", PieceShape.Split, 1, 1, SplitArms, []));
        return l;
    }

    private static readonly Dictionary<string, PieceDef> ByName = All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The definition of an object name, or null when it is not a piece the editor knows (the J connectors, caps and runway pieces are not).</summary>
    public static PieceDef? Lookup(string name) => ByName.GetValueOrDefault(name.Trim().TrimStart('#'));

    /// <summary>The piece of a shape for a road kind (RSEG kind % 3) in the asphalt look <paramref name="variant"/> (1-3), or null when the game has none (no planks T or X).</summary>
    public static PieceDef? Pick(PieceShape shape, uint kind, int variant = 1)
    {
        kind %= 3;
        var of = All.Where(d => d.Shape == shape && d.Kind == kind && !d.Name.Contains("isect")).ToList();
        return of.FirstOrDefault(d => d.Variant == variant) ?? of.FirstOrDefault();
    }

    /// <summary>The ODEF object type for a road-piece name (0x50 range), used when none is known.</summary>
    public static bool IsPieceType(uint objectType) => (objectType & 0xf0) == 0x50;
}
