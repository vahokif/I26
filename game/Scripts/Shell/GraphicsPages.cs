namespace I76.Game.Shell;

/// <summary>A row of Graphic Detail: the original's rows the port honours and the port's own, each on one page (the shell's pages have them all, the in-game pad's the ones a mission can take).</summary>
public enum GraphicsRow
{
    Resolution, MonitorBrightness, DrawDistance, TerrainDetail, ShadowDetail, ExtInstruments, RearMirror,
    Look1997, AntiAliasing, RenderScale, VSync, MaxFps, Renderer, LogFile, HdMenuArt,
    HdTextures, Vegetation,
    FieldOfView, CarInteriors, Garage3D,
    PaletteGrade, CrashBlur,
}

/// <summary>
/// Graphic Detail's pages. The original has one sheet (grxdet1.map: Screen Resolution, Monitor Brightness, Visibility Range, ...,
/// Terrain Detail, ..., Shadow Detail, Ext. Instrum. Disp., Rear Mirror; the 1997 renderer's own detail rows are gone, Screen
/// Resolution is the port's window and Visibility Range its Draw Distance). Here the sheet holds only an entry for each page, and
/// every row, the original's too, is on the page it belongs to, by what it changes: Display (the window, the look and the frame),
/// World (how far and in what detail the world is drawn, its textures and plants), Car &amp; Cockpit (the view, the instruments, the
/// mirror, the cars, the garage) and Effects (the modern look's grade and effects). The original's rows lead their pages in the
/// original's order (Field of View, the most used, first on Car &amp; Cockpit). A page is the same sheet with its name on the first
/// line, its rows and a Back; every page fits the sheet without scrolling (<see cref="GraphicsLayout"/>). The shell's sheet and the
/// in-game pad build from this table, so a row sits on the same page in both; Reset, Cancel and the snapshot taken when Graphic
/// Detail opens copy a page's rows through <see cref="Copy(int, ShellSettings, ShellSettings)"/>. No engine here: the tests check the
/// table, the layout and the copies without one.
/// </summary>
public static class GraphicsPages
{
    public const int Sheet = 0, Display = 1, World = 2, CarCockpit = 3, Effects = 4;
    /// <summary>The first of the pages (the sheet's first entry).</summary>
    public const int FirstPage = Display;
    public static readonly string[] Names = ["Graphic Detail", "Display", "World", "Car & Cockpit", "Effects"];
    /// <summary>Each page's rows in order, the most used first and related rows together; the sheet has none, only the pages' entries.</summary>
    public static readonly GraphicsRow[][] Rows =
    [
        [],
        [GraphicsRow.Resolution, GraphicsRow.MonitorBrightness, GraphicsRow.Look1997, GraphicsRow.AntiAliasing, GraphicsRow.RenderScale, GraphicsRow.VSync, GraphicsRow.MaxFps, GraphicsRow.Renderer, GraphicsRow.LogFile, GraphicsRow.HdMenuArt],
        [GraphicsRow.DrawDistance, GraphicsRow.TerrainDetail, GraphicsRow.ShadowDetail, GraphicsRow.HdTextures, GraphicsRow.Vegetation],
        [GraphicsRow.FieldOfView, GraphicsRow.ExtInstruments, GraphicsRow.RearMirror, GraphicsRow.CarInteriors, GraphicsRow.Garage3D],
        [GraphicsRow.PaletteGrade, GraphicsRow.CrashBlur],
    ];

    /// <summary>Rows a page shows only with a pack installed (HD Menu Art with the menu art pack, HD Textures with a texture pack).</summary>
    public static readonly GraphicsRow[] PackRows = [GraphicsRow.HdMenuArt, GraphicsRow.HdTextures];

    /// <summary>Rows Reset leaves alone, as the one list's Reset did: the start-up choices (Renderer, Log), read when the game starts.</summary>
    public static readonly GraphicsRow[] KeptOnReset = [GraphicsRow.Renderer, GraphicsRow.LogFile];

    /// <summary>The original's sheet's rows the port honours, in grxdet1.map's order (now on Display, World and Car &amp; Cockpit).</summary>
    public static readonly GraphicsRow[] Original =
        [GraphicsRow.Resolution, GraphicsRow.MonitorBrightness, GraphicsRow.DrawDistance, GraphicsRow.TerrainDetail, GraphicsRow.ShadowDetail, GraphicsRow.ExtInstruments, GraphicsRow.RearMirror];

    /// <summary>
    /// The graphics block's bytes a row owns (Terrain Detail +0x2, Shadow Detail +0x6, Anti-Aliasing +0x8, Monitor Brightness +0xc,
    /// Rear Mirror +0xd, Ext. Instrum. Disp. +0xe); the rest, the 1997 renderer's detail rows the port does not show, are the sheet's.
    /// </summary>
    public static readonly int[] RowBytes = [0x2, 0x6, 0x8, 0xc, 0xd, 0xe];

    /// <summary>The sheet's lines: fourteen, 23 px apart from y 118 on the shell's sheet, the pad's rows 1 to 14.</summary>
    public const int Lines = 14;
    /// <summary>The sheet's first page entry: its line (0-based), the sheet's first (the sheet has no rows of its own).</summary>
    public static int FirstEntryLine => Rows[Sheet].Length == 0 ? 0 : Rows[Sheet].Length + 1;
    /// <summary>A page on the shell's sheet: its name on the first line, its rows from the next; the pad prints the name over the art's title and starts its rows on row 1.</summary>
    public const int HeadingLine = 0, FirstRowLine = 1;

    public static int PageOf(GraphicsRow row)
    {
        for (int p = 0; p < Rows.Length; p++) if (Array.IndexOf(Rows[p], row) >= 0) return p;
        return -1;
    }

    /// <summary>A page by its name's first word, any case ("car" for Car &amp; Cockpit); -1 for none.</summary>
    public static int PageNamed(string word)
    {
        for (int p = 0; p < Names.Length; p++)
            if (string.Equals(Names[p].Split(' ')[0], word, StringComparison.OrdinalIgnoreCase)) return p;
        return -1;
    }

    /// <summary>What Reset puts back: a fresh block (the default profile and the port's defaults), Crash Blur On as a loaded file has it.</summary>
    public static ShellSettings Defaults() => new() { CrashBlur = I76.Sim.Views.CrashBlurMode.On };

    /// <summary>
    /// A page's rows copied from one settings block to another; the sheet's, the graphics block's bytes no row shows (the original's
    /// Reset and Cancel treat the block whole: with every page's rows that is the whole block).
    /// </summary>
    public static void Copy(int page, ShellSettings from, ShellSettings to)
    {
        if (page == Sheet) CopyUnshownBytes(from, to);
        foreach (var r in Rows[page]) Copy(r, from, to);
    }

    private static void CopyUnshownBytes(ShellSettings from, ShellSettings to)
    {
        for (int i = 0; i < 16; i++) if (Array.IndexOf(RowBytes, i) < 0) to.Graphics[i] = from.Graphics[i];
    }

    /// <summary>Every page's rows copied (the snapshot on opening, and Cancel's restore from it).</summary>
    public static ShellSettings CopyAll(ShellSettings from, ShellSettings to)
    {
        for (int p = 0; p < Rows.Length; p++) Copy(p, from, to);
        return to;
    }

    /// <summary>
    /// Reset: on a page its rows to the defaults; on the sheet every page's (the original's rows and the whole graphics block to the
    /// default profile, as the original's Reset, and every port row to its default); the start-up rows kept.
    /// </summary>
    public static void Reset(ShellSettings s, int page) => Reset(s, page, page == Sheet ? Rows.SelectMany(r => r) : Rows[page]);

    /// <summary>Reset on the in-game pad's page: the rows it offers (on the sheet every page's offered rows, with the graphics block's bytes no row shows).</summary>
    public static void Reset(ShellSettings s, int page, IEnumerable<GraphicsRow> offered)
    {
        var d = Defaults();
        if (page == Sheet) CopyUnshownBytes(d, s);
        foreach (var r in offered) if (Array.IndexOf(KeptOnReset, r) < 0) Copy(r, d, s);
    }

    /// <summary>One row's keys copied.</summary>
    public static void Copy(GraphicsRow r, ShellSettings f, ShellSettings t)
    {
        switch (r)
        {
            case GraphicsRow.Resolution: t.Resolution = f.Resolution; break;
            case GraphicsRow.MonitorBrightness: t.Graphics[0xc] = f.Graphics[0xc]; break;
            case GraphicsRow.DrawDistance: t.DrawDistance = f.DrawDistance; break;
            case GraphicsRow.TerrainDetail: t.Graphics[0x2] = f.Graphics[0x2]; break;
            case GraphicsRow.ShadowDetail: t.Graphics[0x6] = f.Graphics[0x6]; break;
            case GraphicsRow.ExtInstruments: t.Graphics[0xe] = f.Graphics[0xe]; break;
            case GraphicsRow.RearMirror: t.Graphics[0xd] = f.Graphics[0xd]; break;
            case GraphicsRow.Look1997: t.Look1997 = f.Look1997; t.SoftwareRenderer = f.SoftwareRenderer; break;
            case GraphicsRow.AntiAliasing: t.Graphics[0x8] = f.Graphics[0x8]; break;
            case GraphicsRow.RenderScale: t.RenderScale = f.RenderScale; break;
            case GraphicsRow.VSync: t.VSync = f.VSync; break;
            case GraphicsRow.MaxFps: t.MaxFps = f.MaxFps; break;
            case GraphicsRow.Renderer: t.Renderer = f.Renderer; break;
            case GraphicsRow.LogFile: t.LogFile = f.LogFile; break;
            case GraphicsRow.HdMenuArt: t.HdMenuArt = f.HdMenuArt; break;
            case GraphicsRow.HdTextures: t.HdTextures = f.HdTextures; break;
            case GraphicsRow.Vegetation: t.Vegetation = f.Vegetation; break;
            case GraphicsRow.FieldOfView: t.FieldOfView = f.FieldOfView; break;
            case GraphicsRow.CarInteriors: t.CarInteriors = f.CarInteriors; break;
            case GraphicsRow.Garage3D: t.Garage3D = f.Garage3D; break;
            case GraphicsRow.PaletteGrade: t.PaletteGrade = f.PaletteGrade; break;
            case GraphicsRow.CrashBlur: t.CrashBlur = f.CrashBlur; break;
        }
    }
}

/// <summary>
/// Where a Graphic Detail page's rows, the sheet's page entries and the Back, Reset, Done and Cancel go, on the shell's sheet (its
/// fourteen ruled lines, 23 px apart from y 118, the original's row pitch, the buttons on the foot line at y 438) and on the in-game
/// pad (its rows 1 to 14, the buttons on pairs 15 to 17). Pure, so the tests check every page at every row count.
/// </summary>
public static class GraphicsLayout
{
    public const int ListY = 118, LineH = 23, ButtonY = 438;
    public static int LineY(int line) => ListY + LineH * line;

    /// <summary>The pad's rows (pairs 1..14) and its buttons' pairs (Done, Cancel, Reset).</summary>
    public const int PadRows = 14;
    public static readonly int[] PadButtons = [15, 16, 17];

    /// <summary>A page's places: its rows on the lines (or pad rows) from <see cref="First"/>, and each page entry's line.</summary>
    public readonly record struct Place(int First, int Room, int[] Entries)
    {
        /// <summary>Whether <paramref name="rows"/> rows fit the page's lines.</summary>
        public bool Fits(int rows) => rows <= Room;
        public int LineOf(int k) => First + k;
    }

    /// <summary>The shell's sheet: the entries from <see cref="GraphicsPages.FirstEntryLine"/> (line 0: the sheet has no rows); a page's rows from line 1 under its name.</summary>
    public static Place Shell(int page)
    {
        int entries = GraphicsPages.Names.Length - GraphicsPages.FirstPage;
        return page == GraphicsPages.Sheet
            ? new(0, GraphicsPages.FirstEntryLine - 1, [.. Enumerable.Range(GraphicsPages.FirstEntryLine, entries)])
            : new(GraphicsPages.FirstRowLine, GraphicsPages.Lines - GraphicsPages.FirstRowLine, []);
    }

    /// <summary>The in-game pad: the sheet's entries from row 1 (under its rows and a blank row, had it rows); a page's rows from row 1 (its name is the pad's title).</summary>
    public static Place Pad(int page, int rows)
    {
        int entries = GraphicsPages.Names.Length - GraphicsPages.FirstPage;
        return page == GraphicsPages.Sheet
            ? new(1, PadRows - entries - 1, [.. Enumerable.Range(rows > 0 ? rows + 2 : 1, entries)])
            : new(1, PadRows, []);
    }
}
