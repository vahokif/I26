using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Graphic Detail's pages (GraphicsPages, GraphicsLayout): the sheet is the original's rows in the original's order, every port row is
/// on one page, every page fits the sheet's and the pad's lines without scrolling and never on a button, Reset puts back a page's
/// own rows and no other's (the start-up rows kept), and the snapshot Cancel restores from carries every row. Without the engine.
/// </summary>
public class GraphicsPagesTests
{
    private static object? Read(GraphicsRow r, ShellSettings s) => r switch
    {
        GraphicsRow.Resolution => s.Resolution,
        GraphicsRow.MonitorBrightness => s.Graphics[0xc],
        GraphicsRow.DrawDistance => s.DrawDistance,
        GraphicsRow.TerrainDetail => s.Graphics[0x2],
        GraphicsRow.ShadowDetail => s.Graphics[0x6],
        GraphicsRow.ExtInstruments => s.Graphics[0xe],
        GraphicsRow.RearMirror => s.Graphics[0xd],
        GraphicsRow.Look1997 => (s.Look1997, s.SoftwareRenderer),
        GraphicsRow.AntiAliasing => s.Graphics[0x8],
        GraphicsRow.RenderScale => s.RenderScale,
        GraphicsRow.VSync => s.VSync,
        GraphicsRow.MaxFps => s.MaxFps,
        GraphicsRow.Renderer => s.Renderer,
        GraphicsRow.LogFile => s.LogFile,
        GraphicsRow.HdMenuArt => s.HdMenuArt,
        GraphicsRow.HdTextures => s.HdTextures,
        GraphicsRow.Vegetation => s.Vegetation,
        GraphicsRow.FieldOfView => s.FieldOfView,
        GraphicsRow.CarInteriors => s.CarInteriors,
        GraphicsRow.Garage3D => s.Garage3D,
        GraphicsRow.PaletteGrade => s.PaletteGrade,
        GraphicsRow.CrashBlur => s.CrashBlur,
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    /// <summary>A block with every row off its default.</summary>
    private static ShellSettings Changed()
    {
        var s = new ShellSettings();
        for (int i = 0; i < 16; i++) s.Graphics[i] = (byte)(ShellSettings.DefaultGraphics[i] + 1);
        s.Resolution = "1280x720"; s.DrawDistance = 1 - GraphicsPages.Defaults().DrawDistance;
        s.Look1997 = true; s.SoftwareRenderer = true; s.RenderScale = 2; s.VSync = false; s.MaxFps = 1;
        s.Renderer = I76.Game.Boot.RendererChoice.OpenGL; s.LogFile = true; s.HdMenuArt = false; s.HdTextures = false;
        s.Vegetation = 2; s.FieldOfView = 3; s.CarInteriors = false; s.Garage3D = false; s.PaletteGrade = true; s.CrashBlur = I76.Sim.Views.CrashBlurMode.Off;
        return s;
    }

    private static IEnumerable<GraphicsRow> AllRows => Enum.GetValues<GraphicsRow>();

    [Fact]
    public void EveryRowIsOnOnePage()
    {
        Assert.Equal(GraphicsPages.Names.Length, GraphicsPages.Rows.Length);
        foreach (var r in AllRows) Assert.Equal(1, GraphicsPages.Rows.Count(p => p.Contains(r)));
    }

    /// <summary>The original's grxdet1.map in order, without the 1997 renderer's own detail rows: Screen Resolution (the port's window), Monitor Brightness, Visibility Range (Draw Distance), Terrain Detail, Shadow Detail, Ext. Instrum. Disp., Rear Mirror.</summary>
    [Fact]
    public void TheSheetIsTheOriginalsRowsInTheirOrder()
    {
        Assert.Equal([GraphicsRow.Resolution, GraphicsRow.MonitorBrightness, GraphicsRow.DrawDistance, GraphicsRow.TerrainDetail, GraphicsRow.ShadowDetail, GraphicsRow.ExtInstruments, GraphicsRow.RearMirror],
            GraphicsPages.Rows[GraphicsPages.Sheet]);
        Assert.Equal(GraphicsPages.OriginalRows, GraphicsPages.Rows[GraphicsPages.Sheet].Length);
        Assert.Equal(["Graphic Detail", "Display", "World", "Car & Cockpit", "Effects"], GraphicsPages.Names);
        Assert.Equal(GraphicsPages.CarCockpit, GraphicsPages.PageNamed("car"));
        Assert.Equal(-1, GraphicsPages.PageNamed("look"));
    }

    /// <summary>The shell's sheet: the seven rows, a blank line, the four entries, all inside the fourteen lines; every page's rows from line 1 inside them.</summary>
    [Fact]
    public void TheShellsLinesHoldEveryPage()
    {
        var sheet = GraphicsLayout.Shell(GraphicsPages.Sheet);
        Assert.True(sheet.Fits(GraphicsPages.Rows[GraphicsPages.Sheet].Length));
        Assert.Equal([8, 9, 10, 11], sheet.Entries);
        var used = Enumerable.Range(0, GraphicsPages.Rows[GraphicsPages.Sheet].Length).Select(sheet.LineOf).Concat(sheet.Entries).ToList();
        Assert.Equal(used.Count, used.Distinct().Count());
        Assert.All(used, l => Assert.InRange(l, 0, GraphicsPages.Lines - 1));
        Assert.True(GraphicsLayout.LineY(GraphicsPages.Lines - 1) + GraphicsLayout.LineH - 2 <= GraphicsLayout.ButtonY);
        for (int p = GraphicsPages.FirstPage; p < GraphicsPages.Names.Length; p++)
        {
            var page = GraphicsLayout.Shell(p);
            Assert.True(page.Fits(GraphicsPages.Rows[p].Length), GraphicsPages.Names[p]);
            Assert.Equal(GraphicsPages.FirstRowLine, page.LineOf(0));
            Assert.NotEqual(GraphicsPages.HeadingLine, page.LineOf(0));
        }
    }

    /// <summary>The pad: every page's rows on pad rows 1 to 14, never on a button's pair (15 to 17), the sheet's entries on rows of their own under a blank row.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ThePadsRowsSitOnTheirOwnRows(int sheetRows)
    {
        var sheet = GraphicsLayout.Pad(GraphicsPages.Sheet, sheetRows);
        Assert.True(sheet.Fits(sheetRows));
        var used = Enumerable.Range(0, sheetRows).Select(sheet.LineOf).Concat(sheet.Entries).ToList();
        Assert.Equal(used.Count, used.Distinct().Count());
        Assert.Equal(sheetRows + 2, sheet.Entries[0]);
        foreach (int r in used) { Assert.InRange(r, 1, GraphicsLayout.PadRows); Assert.DoesNotContain(r, GraphicsLayout.PadButtons); }
        for (int p = GraphicsPages.FirstPage; p < GraphicsPages.Names.Length; p++)
        {
            var page = GraphicsLayout.Pad(p, GraphicsPages.Rows[p].Length);
            Assert.True(page.Fits(GraphicsPages.Rows[p].Length));
            Assert.All(Enumerable.Range(0, GraphicsPages.Rows[p].Length).Select(page.LineOf), r => Assert.DoesNotContain(r, GraphicsLayout.PadButtons));
        }
    }

    [Theory]
    [InlineData(GraphicsPages.Sheet)]
    [InlineData(GraphicsPages.Display)]
    [InlineData(GraphicsPages.World)]
    [InlineData(GraphicsPages.CarCockpit)]
    [InlineData(GraphicsPages.Effects)]
    public void ResetPutsBackThePagesRowsAlone(int page)
    {
        var s = Changed();
        GraphicsPages.Reset(s, page);
        var d = GraphicsPages.Defaults();
        foreach (var r in AllRows)
        {
            bool reset = GraphicsPages.PageOf(r) == page && !GraphicsPages.KeptOnReset.Contains(r);
            Assert.Equal(Read(r, reset ? d : Changed()), Read(r, s));
        }
    }

    /// <summary>The sheet's Reset is the original's: the graphics block back to the default profile, the bytes no row shows too, but Anti-Aliasing's, which is Display's.</summary>
    [Fact]
    public void TheSheetsResetIsTheDefaultProfile()
    {
        var s = Changed();
        GraphicsPages.Reset(s, GraphicsPages.Sheet);
        for (int i = 0; i < 16; i++) Assert.Equal(i == 0x8 ? Changed().Graphics[i] : ShellSettings.DefaultGraphics[i], s.Graphics[i]);
        GraphicsPages.Reset(s, GraphicsPages.Display);
        Assert.Equal(ShellSettings.DefaultGraphics, s.Graphics);
        Assert.Equal(I76.Sim.Views.CrashBlurMode.On, GraphicsPages.Defaults().CrashBlur);
    }

    [Fact]
    public void TheSnapshotCarriesEveryRowAndCancelRestoresIt()
    {
        var opened = Changed();
        var snap = GraphicsPages.CopyAll(opened, new ShellSettings());
        foreach (var r in AllRows) Assert.Equal(Read(r, opened), Read(r, snap));
        Assert.Equal(opened.Graphics, snap.Graphics);
        var live = new ShellSettings();
        GraphicsPages.CopyAll(snap, live);
        foreach (var r in AllRows) Assert.Equal(Read(r, opened), Read(r, live));
    }
}
