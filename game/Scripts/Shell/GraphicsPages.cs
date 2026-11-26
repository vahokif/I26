namespace I76.Game.Shell;

/// <summary>A row of Graphic Detail: the original's rows the port honours (on the sheet, in the original's order) and the port's own (on the pages; the shell's sheet has them all, the in-game pad the ones a mission can take).</summary>
public enum GraphicsRow
{
    Resolution, MonitorBrightness, DrawDistance, TerrainDetail, ShadowDetail, ExtInstruments, RearMirror,
    Look1997, AntiAliasing, RenderScale, VSync, MaxFps, Renderer, LogFile, HdMenuArt,
    HdTextures, Vegetation,
    FieldOfView, CarInteriors, Garage3D,
    PaletteGrade, CrashBlur,
}

/// <summary>
/// Graphic Detail's pages: the sheet is the original's, its rows in the original's order (grxdet1.map: Screen Resolution, Monitor
/// Brightness, Visibility Range, ..., Terrain Detail, ..., Shadow Detail, Ext. Instrum. Disp., Rear Mirror; the 1997 renderer's own
/// detail rows are gone, Screen Resolution is the port's window and Visibility Range its Draw Distance), then under a blank line an
/// entry for each page of the port's own rows, by what a row changes: Display (the look and the frame), World (the world's textures
/// and plants), Car &amp; Cockpit (the view, the cars, the garage) and Effects (the modern look's grade and effects). A page is the same
/// sheet with its name on the first line, its rows and a Back; every page fits the sheet without scrolling (<see cref="GraphicsLayout"/>).
/// The shell's sheet and the in-game pad build from this table, so a row sits on the same page in both; Reset, Cancel and the
/// snapshot taken when Graphic Detail opens copy a page's rows through <see cref="Copy(int, ShellSettings, ShellSettings)"/>. No
/// engine here: the tests check the table, the layout and the copies without one.
/// </summary>
public static class GraphicsPages
{
    public const int Sheet = 0, Display = 1, World = 2, CarCockpit = 3, Effects = 4;
    /// <summary>The first of the port's pages (the sheet's first entry).</summary>
    public const int FirstPage = Display;
    public static readonly string[] Names = ["Graphic Detail", "Display", "World", "Car & Cockpit", "Effects"];
    /// <summary>Each page's rows in order; the sheet's are the original's, as the original has them.</summary>
    public static readonly GraphicsRow[][] Rows =
    [
        [GraphicsRow.Resolution, GraphicsRow.MonitorBrightness, GraphicsRow.DrawDistance, GraphicsRow.TerrainDetail, GraphicsRow.ShadowDetail, GraphicsRow.ExtInstruments, GraphicsRow.RearMirror],
        [GraphicsRow.Look1997, GraphicsRow.AntiAliasing, GraphicsRow.RenderScale, GraphicsRow.VSync, GraphicsRow.MaxFps, GraphicsRow.Renderer, GraphicsRow.LogFile, GraphicsRow.HdMenuArt],
        [GraphicsRow.HdTextures, GraphicsRow.Vegetation],
        [GraphicsRow.FieldOfView, GraphicsRow.CarInteriors, GraphicsRow.Garage3D],
        [GraphicsRow.PaletteGrade, GraphicsRow.CrashBlur],
    ];

    /// <summary>Rows a page shows only with a pack installed (HD Menu Art with the menu art pack, HD Textures with a texture pack).</summary>
    public static readonly GraphicsRow[] PackRows = [GraphicsRow.HdMenuArt, GraphicsRow.HdTextures];

    /// <summary>Rows Reset leaves alone, as the one list's Reset did: the start-up choices (Renderer, Log), read when the game starts.</summary>
    public static readonly GraphicsRow[] KeptOnReset = [GraphicsRow.Renderer, GraphicsRow.LogFile];

    /// <summary>The original's rows: the sheet's seven.</summary>
    public const int OriginalRows = 7;

    /// <summary>The graphics block's bytes that are a page's row, not the sheet's: Anti-Aliasing's (+0x8, the port's MSAA in a byte no screen of the original showed), Display's.</summary>
    public static readonly int[] PageBytes = [0x8];

    /// <summary>The sheet's lines: fourteen, 23 px apart from y 118 on the shell's sheet, the pad's rows 1 to 14.</summary>
    public const int Lines = 14;
    /// <summary>The sheet's first page entry: its line (0-based), under a blank line under the sheet's rows.</summary>
    public static int FirstEntryLine => Rows[Sheet].Length + 1;
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
    /// A page's rows copied from one settings block to another. The sheet's take the graphics block with them (the original's Reset and
    /// Cancel treat it whole) but for the bytes a page owns (<see cref="PageBytes"/>).
    /// </summary>
    public static void Copy(int page, ShellSettings from, ShellSettings to)
    {
        if (page == Sheet)
            for (int i = 0; i < 16; i++) if (Array.IndexOf(PageBytes, i) < 0) to.Graphics[i] = from.Graphics[i];
        foreach (var r in Rows[page]) Copy(r, from, to);
    }

    /// <summary>Every page's rows copied (the snapshot on opening, and Cancel's restore from it).</summary>
    public static ShellSettings CopyAll(ShellSettings from, ShellSettings to)
    {
        for (int p = 0; p < Rows.Length; p++) Copy(p, from, to);
        return to;
    }

    /// <summary>Reset on a page: its rows to the defaults (the sheet's the default profile, as the original's Reset), the start-up rows kept.</summary>
    public static void Reset(ShellSettings s, int page) => Reset(s, page, Rows[page]);

    /// <summary>Reset on the in-game pad's page: the rows it offers (on the sheet with the graphics block, as the shell's).</summary>
    public static void Reset(ShellSettings s, int page, IEnumerable<GraphicsRow> offered)
    {
        var d = Defaults();
        if (page == Sheet)
            for (int i = 0; i < 16; i++) if (Array.IndexOf(PageBytes, i) < 0) s.Graphics[i] = d.Graphics[i];
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

    /// <summary>The shell's sheet: the sheet's rows from line 0, a blank line, the entries; a page's rows from line 1 under its name.</summary>
    public static Place Shell(int page)
    {
        int entries = GraphicsPages.Names.Length - GraphicsPages.FirstPage;
        return page == GraphicsPages.Sheet
            ? new(0, GraphicsPages.FirstEntryLine - 1, [.. Enumerable.Range(GraphicsPages.FirstEntryLine, entries)])
            : new(GraphicsPages.FirstRowLine, GraphicsPages.Lines - GraphicsPages.FirstRowLine, []);
    }

    /// <summary>The in-game pad (no Screen Resolution on its sheet): the sheet's rows from row 1, a blank row, the entries; a page's rows from row 1 (its name is the pad's title).</summary>
    public static Place Pad(int page, int rows)
    {
        int entries = GraphicsPages.Names.Length - GraphicsPages.FirstPage;
        return page == GraphicsPages.Sheet
            ? new(1, PadRows - entries - 1, [.. Enumerable.Range(rows + 2, entries)])
            : new(1, PadRows, []);
    }
}
