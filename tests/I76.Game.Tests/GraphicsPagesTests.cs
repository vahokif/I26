using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Graphic Detail's pages (GraphicsPages, GraphicsLayout): the sheet holds only the pages' entries, every row (the original's too) is
/// on one page, the original's leading their pages in its order, every page fits the sheet's and the pad's lines without scrolling
/// and never on a button, Reset puts back a page's own rows and no other's (the sheet's every page's; the start-up rows kept), and
/// the snapshot Cancel restores from carries every row. Without the engine.
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
        GraphicsRow.Quality => null,   // derived from the rows (QualityPreset)
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

    /// <summary>
    /// The original's grxdet1.map in order, without the 1997 renderer's own detail rows: Screen Resolution (the port's window), Monitor
    /// Brightness, Visibility Range (Draw Distance), Terrain Detail, Shadow Detail, Ext. Instrum. Disp., Rear Mirror. The sheet has no
    /// rows: each of the original's leads its page in the original's order (but for Field of View, first on Car &amp; Cockpit).
    /// </summary>
    [Fact]
    public void TheSheetHas1997ModeAndQualityAndTheOriginalsRowsLeadTheirPages()
    {
        Assert.Equal([GraphicsRow.Look1997, GraphicsRow.Quality], GraphicsPages.Rows[GraphicsPages.Sheet]);
        Assert.Equal([GraphicsRow.Resolution, GraphicsRow.MonitorBrightness, GraphicsRow.DrawDistance, GraphicsRow.TerrainDetail, GraphicsRow.ShadowDetail, GraphicsRow.ExtInstruments, GraphicsRow.RearMirror],
            GraphicsPages.Original);
        Assert.Equal([GraphicsPages.Display, GraphicsPages.Display, GraphicsPages.World, GraphicsPages.World, GraphicsPages.World, GraphicsPages.CarCockpit, GraphicsPages.CarCockpit],
            GraphicsPages.Original.Select(GraphicsPages.PageOf));
        for (int p = GraphicsPages.FirstPage; p < GraphicsPages.Names.Length; p++)
        {
            var mine = GraphicsPages.Rows[p].Where(GraphicsPages.Original.Contains).ToArray();
            Assert.Equal(GraphicsPages.Original.Where(mine.Contains), mine);
            Assert.Equal(mine, GraphicsPages.Rows[p].Skip(p == GraphicsPages.CarCockpit ? 1 : 0).Take(mine.Length));
        }
        Assert.Equal(["Graphic Detail", "Display", "World", "Car & Cockpit", "Effects"], GraphicsPages.Names);
        Assert.Equal(GraphicsPages.CarCockpit, GraphicsPages.PageNamed("car"));
        Assert.Equal(-1, GraphicsPages.PageNamed("look"));
    }

    [Fact]
    public void ThePagesRows()
    {
        Assert.Equal([GraphicsRow.Resolution, GraphicsRow.MonitorBrightness, GraphicsRow.AntiAliasing, GraphicsRow.RenderScale, GraphicsRow.VSync, GraphicsRow.MaxFps, GraphicsRow.Renderer, GraphicsRow.LogFile, GraphicsRow.HdMenuArt],
            GraphicsPages.Rows[GraphicsPages.Display]);
        Assert.Equal([GraphicsRow.DrawDistance, GraphicsRow.TerrainDetail, GraphicsRow.ShadowDetail, GraphicsRow.HdTextures, GraphicsRow.Vegetation],
            GraphicsPages.Rows[GraphicsPages.World]);
        Assert.Equal([GraphicsRow.FieldOfView, GraphicsRow.ExtInstruments, GraphicsRow.RearMirror, GraphicsRow.CarInteriors, GraphicsRow.Garage3D],
            GraphicsPages.Rows[GraphicsPages.CarCockpit]);
        Assert.Equal([GraphicsRow.PaletteGrade, GraphicsRow.CrashBlur], GraphicsPages.Rows[GraphicsPages.Effects]);
    }

    /// <summary>The shell's sheet: 1997 Mode and Quality on its first lines, a blank line, the four entries; every page's rows from line 1 inside the fourteen lines.</summary>
    [Fact]
    public void TheShellsLinesHoldEveryPage()
    {
        var sheet = GraphicsLayout.Shell(GraphicsPages.Sheet);
        Assert.Equal(3, GraphicsPages.FirstEntryLine);
        Assert.True(sheet.Fits(GraphicsPages.Rows[GraphicsPages.Sheet].Length));
        Assert.Equal([3, 4, 5, 6], sheet.Entries);
        Assert.True(GraphicsLayout.LineY(GraphicsPages.Lines - 1) + GraphicsLayout.LineH - 2 <= GraphicsLayout.ButtonY);
        for (int p = GraphicsPages.FirstPage; p < GraphicsPages.Names.Length; p++)
        {
            var page = GraphicsLayout.Shell(p);
            Assert.True(page.Fits(GraphicsPages.Rows[p].Length), GraphicsPages.Names[p]);
            Assert.Equal(GraphicsPages.FirstRowLine, page.LineOf(0));
            Assert.InRange(page.LineOf(GraphicsPages.Rows[p].Length - 1), GraphicsPages.FirstRowLine, GraphicsPages.Lines - 1);
        }
    }

    /// <summary>The pad: the sheet's two rows on rows 1 and 2, its entries on rows 4 to 7; every page's rows on pad rows 1 to 14, never on a button's pair (15 to 17).</summary>
    [Fact]
    public void ThePadsRowsSitOnTheirOwnRows()
    {
        var sheet = GraphicsLayout.Pad(GraphicsPages.Sheet, 2);
        Assert.Equal([4, 5, 6, 7], sheet.Entries);
        foreach (int r in sheet.Entries) Assert.DoesNotContain(r, GraphicsLayout.PadButtons);
        for (int p = GraphicsPages.FirstPage; p < GraphicsPages.Names.Length; p++)
        {
            var page = GraphicsLayout.Pad(p, GraphicsPages.Rows[p].Length);
            Assert.True(page.Fits(GraphicsPages.Rows[p].Length));
            Assert.All(Enumerable.Range(0, GraphicsPages.Rows[p].Length).Select(page.LineOf), r => { Assert.InRange(r, 1, GraphicsLayout.PadRows); Assert.DoesNotContain(r, GraphicsLayout.PadButtons); });
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
            bool reset = (page == GraphicsPages.Sheet || GraphicsPages.PageOf(r) == page) && !GraphicsPages.KeptOnReset.Contains(r);
            Assert.Equal(Read(r, reset ? d : Changed()), Read(r, s));
        }
    }

    /// <summary>The sheet's Reset is every page's: the graphics block back to the default profile (the bytes no row shows too), every row to its default but the start-up rows. A page's Reset leaves the bytes no row shows alone.</summary>
    [Fact]
    public void TheSheetsResetIsTheDefaultProfileAndEveryPage()
    {
        var s = Changed();
        GraphicsPages.Reset(s, GraphicsPages.Display);
        Assert.Equal(ShellSettings.DefaultGraphics[0x8], s.Graphics[0x8]);
        Assert.Equal(Changed().Graphics[0x0], s.Graphics[0x0]);
        Assert.Equal(Changed().Graphics[0x2], s.Graphics[0x2]);   // World's
        GraphicsPages.Reset(s, GraphicsPages.Sheet);
        Assert.Equal(ShellSettings.DefaultGraphics, s.Graphics);
        Assert.Equal("", s.Resolution);
        Assert.Equal(I76.Game.Boot.RendererChoice.OpenGL, s.Renderer);
        Assert.Equal(I76.Sim.Views.CrashBlurMode.On, GraphicsPages.Defaults().CrashBlur);
    }

    /// <summary>Every byte of the graphics block is one row's or the sheet's: Copy and Reset touch each from one page.</summary>
    [Fact]
    public void EachBlockByteHasOneOwner()
    {
        for (int i = 0; i < 16; i++)
        {
            var from = new ShellSettings();
            from.Graphics[i] ^= 0x5a;
            int owners = 0;
            for (int p = 0; p < GraphicsPages.Names.Length; p++)
            {
                var to = new ShellSettings();
                GraphicsPages.Copy(p, from, to);
                if (to.Graphics[i] == from.Graphics[i]) owners++;
            }
            Assert.True(owners == 1, $"byte {i:x} copied by {owners} pages");
        }
    }

    /// <summary>The in-game pad's sheet Reset: every pad's offered rows and the bytes no row shows, not a row the pad leaves out (its Cancel could not put it back).</summary>
    [Fact]
    public void ThePadsSheetResetLeavesRowsItDoesNotOfferAlone()
    {
        var s = Changed();
        var offered = AllRows.Where(r => r is not (GraphicsRow.Garage3D or GraphicsRow.HdMenuArt)).ToArray();
        GraphicsPages.Reset(s, GraphicsPages.Sheet, offered);
        Assert.Equal(ShellSettings.DefaultGraphics, s.Graphics);
        Assert.False(s.Garage3D);
        Assert.False(s.HdMenuArt);
        Assert.Equal("", s.Resolution);
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
