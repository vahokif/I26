namespace I76.Game.Shell;

/// <summary>
/// Graphic Detail's Quality row (the port's; the original has no preset): one choice that sets the costly rows at once, Original,
/// Balanced, High or Ultra, and shows Custom when the rows match none. Nothing of its own is saved: the value is read back from the
/// rows, and choosing one sets each row as stepping it by hand would. Original is the original game's look in the modern renderer
/// (the port's rows at the original's values, the original's own at the default profile); Balanced is a first start's defaults; High
/// and Ultra are steps up for a strong graphics card. The window, the monitor, Field of View, VSync, Max FPS, 1997 Mode and the rows
/// that are taste or start-up choices (Ext. Instrum. Disp., Rear Mirror, the packs', Car Interiors, 3D Garage, Palette Grade, Crash
/// Blur, Renderer, Log) are not in it. No engine here.
/// </summary>
public static class QualityPreset
{
    public const string Label = "Quality";
    public const int Original = 0, Balanced = 1, High = 2, Ultra = 3;
    public static readonly string[] Names = ["Original", "Balanced", "High", "Ultra"];
    public const string Custom = "Custom";

    /// <summary>A column's value that is a first start's (<see cref="Balanced"/> is all of them).</summary>
    public const int Default = -1;

    /// <summary>
    /// The rows and, per column (Original, Balanced, High, Ultra), the row's value as its index in the row's words. Balanced is every
    /// row's default: today Render Scale 100 %, Anti-Aliasing 4x, Shadow Detail Low, Terrain Detail Low, Draw Distance Far, Vegetation Low.
    /// </summary>
    public static readonly (GraphicsRow Row, int[] Values)[] Table =
    [
        (GraphicsRow.RenderScale,   [0, Default, 1, 2]),   // 100 %, 150 %, 200 %
        (GraphicsRow.AntiAliasing,  [0, Default, 2, 2]),   // Off, 4x
        (GraphicsRow.ShadowDetail,  [1, Default, 2, 2]),   // Low (the default profile), High
        (GraphicsRow.TerrainDetail, [0, Default, 1, 1]),   // Low (the default profile), High
        (GraphicsRow.DrawDistance,  [0, Default, 1, 1]),   // Original (the original's 600 m), Far
        (GraphicsRow.Vegetation,    [0, Default, 2, 2]),   // Off, High
    ];

    public static IEnumerable<GraphicsRow> Rows => Table.Select(t => t.Row);

    public static int Get(ShellSettings s, GraphicsRow r) => r switch
    {
        GraphicsRow.RenderScale => s.RenderScale,
        GraphicsRow.AntiAliasing => s.Graphics[0x8],
        GraphicsRow.ShadowDetail => s.Graphics[0x6],
        GraphicsRow.TerrainDetail => s.Graphics[0x2],
        GraphicsRow.DrawDistance => s.DrawDistance,
        GraphicsRow.Vegetation => s.Vegetation,
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    private static void Set(ShellSettings s, GraphicsRow r, int v)
    {
        switch (r)
        {
            case GraphicsRow.RenderScale: s.RenderScale = v; break;
            case GraphicsRow.AntiAliasing: s.Graphics[0x8] = (byte)v; break;
            case GraphicsRow.ShadowDetail: s.Graphics[0x6] = (byte)v; break;
            case GraphicsRow.TerrainDetail: s.Graphics[0x2] = (byte)v; break;
            case GraphicsRow.DrawDistance: s.DrawDistance = v; break;
            case GraphicsRow.Vegetation: s.Vegetation = v; break;
        }
    }

    private static int Want(GraphicsRow r, int column, int[] values) => values[column] == Default ? Get(GraphicsPages.Defaults(), r) : values[column];

    /// <summary>The column the rows now match, or -1 (Custom).</summary>
    public static int Match(ShellSettings s)
    {
        for (int c = 0; c < Names.Length; c++)
            if (Table.All(t => Get(s, t.Row) == Want(t.Row, c, t.Values))) return c;
        return -1;
    }

    public static string Word(ShellSettings s) => Match(s) is var c and >= 0 ? Names[c] : Custom;

    /// <summary>A column chosen: every row of it set.</summary>
    public static void Apply(ShellSettings s, int column)
    {
        foreach (var (r, values) in Table) Set(s, r, Want(r, column, values));
    }

    /// <summary>A click: the next column (from Custom, Original).</summary>
    public static void Step(ShellSettings s) => Apply(s, (Match(s) + 1) % Names.Length);

    /// <summary>Every row of the preset copied (the in-game pad's Done and Cancel for the row).</summary>
    public static void Copy(ShellSettings from, ShellSettings to)
    {
        foreach (var r in Rows) GraphicsPages.Copy(r, from, to);
    }
}
