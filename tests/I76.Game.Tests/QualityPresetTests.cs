using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Quality row (QualityPreset): each column read back as its own name, Balanced a first start's defaults, any row changed after a column reads Custom, the sheet's Reset back to Balanced. Without the engine.</summary>
public class QualityPresetTests
{
    [Theory]
    [InlineData(QualityPreset.Original)]
    [InlineData(QualityPreset.Balanced)]
    [InlineData(QualityPreset.High)]
    [InlineData(QualityPreset.Ultra)]
    public void EachColumnReadsBackAsItsOwnName(int column)
    {
        var s = new ShellSettings();
        QualityPreset.Apply(s, column);
        Assert.Equal(column, QualityPreset.Match(s));
        Assert.Equal(QualityPreset.Names[column], QualityPreset.Word(s));
    }

    [Fact]
    public void TheColumnsDiffer()
    {
        var words = Enumerable.Range(0, QualityPreset.Names.Length).Select(c =>
        {
            var s = new ShellSettings();
            QualityPreset.Apply(s, c);
            return string.Join(",", QualityPreset.Rows.Select(r => QualityPreset.Get(s, r)));
        }).ToList();
        Assert.Equal(words.Count, words.Distinct().Count());
    }

    [Fact]
    public void AFirstStartAndResetAreBalanced()
    {
        Assert.Equal(QualityPreset.Balanced, QualityPreset.Match(GraphicsPages.Defaults()));
        Assert.All(QualityPreset.Table, t => Assert.Equal(QualityPreset.Default, t.Values[QualityPreset.Balanced]));
        var s = new ShellSettings();
        QualityPreset.Apply(s, QualityPreset.Ultra);
        GraphicsPages.Reset(s, GraphicsPages.Sheet);
        Assert.Equal(QualityPreset.Balanced, QualityPreset.Match(s));
    }

    [Fact]
    public void ChangingAnyRowGivesCustomOrAnotherColumn()
    {
        foreach (var (row, _) in QualityPreset.Table)
            for (int c = 0; c < QualityPreset.Names.Length; c++)
            {
                var s = new ShellSettings();
                QualityPreset.Apply(s, c);
                switch (row)
                {
                    case GraphicsRow.RenderScale: s.RenderScale = (s.RenderScale + 1) % I76.Game.Boot.RenderScale.Names.Length; break;
                    case GraphicsRow.AntiAliasing: s.Graphics[0x8] = (byte)((s.Graphics[0x8] + 1) % 4); break;
                    case GraphicsRow.ShadowDetail: s.Graphics[0x6] = (byte)((s.Graphics[0x6] + 1) % 3); break;
                    case GraphicsRow.TerrainDetail: s.Graphics[0x2] = (byte)(1 - s.Graphics[0x2]); break;
                    case GraphicsRow.DrawDistance: s.DrawDistance = 1 - s.DrawDistance; break;
                    case GraphicsRow.Vegetation: s.Vegetation = (s.Vegetation + 1) % 3; break;
                }
                Assert.True(QualityPreset.Match(s) != c, $"{row} changed on {QualityPreset.Names[c]}");
            }
        var t = new ShellSettings();
        QualityPreset.Apply(t, QualityPreset.High);
        t.Vegetation = 1;
        Assert.Equal(QualityPreset.Custom, QualityPreset.Word(t));
    }

    /// <summary>The columns as written down in docs/playing.md.</summary>
    [Fact]
    public void TheColumnsAreAsDocumented()
    {
        var o = new ShellSettings();
        QualityPreset.Apply(o, QualityPreset.Original);
        Assert.Equal((0, 0, 1, 0, I76.Formats.Imaging.DrawDistance.Original, 0), (o.RenderScale, (int)o.Graphics[0x8], (int)o.Graphics[0x6], (int)o.Graphics[0x2], o.DrawDistance, o.Vegetation));
        var u = new ShellSettings();
        QualityPreset.Apply(u, QualityPreset.Ultra);
        Assert.Equal("200%", I76.Game.Boot.RenderScale.Names[u.RenderScale]);
        Assert.Equal("4x", I76.Game.Boot.RenderScale.AntiAliasing[u.Graphics[0x8]]);
        Assert.Equal((2, 1, I76.Formats.Imaging.DrawDistance.Far, 2), ((int)u.Graphics[0x6], (int)u.Graphics[0x2], u.DrawDistance, u.Vegetation));
    }

    [Fact]
    public void ThePresetIsDerived()
    {
        Assert.Equal(GraphicsPages.Sheet, GraphicsPages.PageOf(GraphicsRow.Quality));
        Assert.All(QualityPreset.Rows, r => Assert.NotEqual(GraphicsPages.Sheet, GraphicsPages.PageOf(r)));
        var u = new ShellSettings();
        QualityPreset.Apply(u, QualityPreset.Ultra);
        var t = new ShellSettings();
        QualityPreset.Copy(u, t);
        Assert.Equal(QualityPreset.Ultra, QualityPreset.Match(t));
        Assert.Equal(QualityPreset.Ultra, QualityPreset.Match(GraphicsPages.CopyAll(u, new ShellSettings())));
        u.Vegetation = 0;
        QualityPreset.Step(u);
        Assert.Equal(QualityPreset.Original, QualityPreset.Match(u));
    }
}
