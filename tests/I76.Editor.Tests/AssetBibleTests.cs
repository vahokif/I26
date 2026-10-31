using I76.Editor.Objects;
using Xunit;

namespace I76.Editor.Tests;

public class AssetBibleTests
{
    [Fact]
    public void TheEmbeddedFileLoadsAndDescribesTheObjects()
    {
        Assert.InRange(AssetBible.Entries.Count, 330, 450);
        Assert.Equal("Building No.100(large)", AssetBible.Describe("bcaik1"));
        Assert.Equal("Building No.100(large)", AssetBible.Describe("BCAIK1"));
        Assert.Equal("Tunnel (long)", AssetBible.Describe("#betunnl3"));
        Assert.True(AssetBible.Entries["bhjhous2"].GoldNitroOnly);
        Assert.False(AssetBible.Entries["bhjhous1"].GoldNitroOnly);
        Assert.Null(AssetBible.Describe("nosuchobject"));
        Assert.Null(AssetBible.Describe("ijd28t1"));                       // a broken one has a note, not a label
        Assert.Contains("crashes", AssetBible.Entries["ijd28t1"].Note);
        Assert.Contains("DIVER", AssetBible.Credit);
    }

    [Fact]
    public void ParsingSkipsCommentsAndTheColumnRow()
    {
        var d = AssetBible.Parse(new StringReader("# comment\nname\tdescription\tsection\tgold_nitro_only\tnote\nabc\tAn abc\tSigns\t1\t\nxyz\t\tSigns\t0\tbroken\nshort\n"));
        Assert.Equal(2, d.Count);
        Assert.Equal("An abc", d["abc"].Description);
        Assert.True(d["abc"].GoldNitroOnly);
        Assert.Equal("broken", d["xyz"].Note);
    }

    [Fact]
    public void ItemsGetTheirDescriptionAsTitleAndTheCodeStaysInTheTooltip()
    {
        var item = new CatalogueItem { Name = "bcaik1", Category = ObjectCategory.Buildings, MaxX = 65, MaxZ = 30, MaxY = 7 };
        Assert.Equal("Building No.100(large)", item.Title);
        Assert.Contains("BCAIK1", item.Tooltip());
        var plain = new CatalogueItem { Name = "zzzz9" };
        Assert.Equal("ZZZZ9", plain.Title);
        Assert.Equal("SPAWN", ObjectCatalogue.MarkerItems().First().Title);
    }
}
