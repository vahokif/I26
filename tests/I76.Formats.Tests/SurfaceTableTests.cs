using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The map's surface table (WRLD +0x97: grip, rolling, bump, an unread float, damage a second, for each surface id) as the map writer saves it and the readers read it.</summary>
public class SurfaceTableTests
{
    private static readonly SurfaceRow Ice = new(0.2f, 0.01f, 0, 0, 0);

    /// <summary>A new map's WRLD is Rift's (N05): the stock table but for no damage on the rough ground (id 5).</summary>
    [Fact]
    public void ANewMapHasRiftsTable()
    {
        var p = MapProject.CreateBlank("surftst", "Surface Test");
        var rift = SurfaceTable.Stock.With(5, SurfaceTable.Stock[5] with { Damage = 0 });
        Assert.Equal(rift, p.Mission.Surfaces);
        Assert.Equal(rift, SurfaceTable.Of(p.Mission.World!));
    }

    [Fact]
    public void AnEditedRowIsSavedAndReadBackAndNothingElseChanges()
    {
        var p = MapProject.CreateBlank("surftst", "Surface Test");
        var before = p.Mission.ToBytes();
        var wrldBefore = (byte[])p.Mission.WorldPayload!.Clone();
        var table = p.Mission.Surfaces!.With(3, Ice).With(5, SurfaceTable.Stock[5] with { Damage = 200 });
        p.Mission.Surfaces = table;
        var bytes = p.Mission.ToBytes();
        var back = MapDocument.Parse(bytes);
        Assert.Equal(table, back.Surfaces);
        // the game's reader sees it
        var w = WorldSettings.Read(new MissionFile("surftst.cbt", bytes))!;
        Assert.Equal(table, SurfaceTable.Of(w));
        Assert.Equal(0.2f, BitConverter.ToSingle(w.Unk_0x97, 3 * 20));
        Assert.Equal(200, BitConverter.ToInt32(w.Unk_0x97, 5 * 20 + 16));
        // only rows 3 and 5 differ
        var wrld = back.WorldPayload!;
        for (int i = 0; i < wrld.Length; i++)
        {
            bool edited = i is >= SurfaceTable.At + 3 * 20 and < SurfaceTable.At + 4 * 20 or >= SurfaceTable.At + 5 * 20 + 16 and < SurfaceTable.At + 6 * 20;
            if (!edited) Assert.Equal(wrldBefore[i], wrld[i]);
        }
        Assert.Equal(before.Length, bytes.Length);
        // setting the table it already has writes nothing
        back.Surfaces = back.Surfaces!.With(0, back.Surfaces[0]);
        Assert.Equal(bytes, back.ToBytes());
    }

    [Fact]
    public void AnOldLevelKeepsItsTable13BytesEarlier()
    {
        // a WREV 8 WRLD (I76EDIT's .lvl) has no horizon field: the table is at +0x8a
        var wrld = new byte[0x137 - WorldSettings.OldLayoutShift + 4];
        SurfaceTable.Stock.With(2, Ice).WriteTo(wrld, 8);
        Assert.Equal(0.2f, BitConverter.ToSingle(wrld, 0x8a + 2 * 20));
        Assert.Equal(SurfaceTable.Stock.With(2, Ice), SurfaceTable.Read(wrld, 8));
        Assert.Null(SurfaceTable.Read(new byte[0x100], 9));
    }

    [Fact]
    public void APatchedMissionInMemoryReadsTheNewTable()
    {
        // the map editor's playtest: the table goes over the mission's WRLD in memory, the file is not touched
        var p = MapProject.CreateBlank("surftst", "Surface Test");
        var msn = new MissionFile("surftst.cbt", p.Mission.ToBytes());
        var table = SurfaceTable.Stock.With(0, SurfaceTable.Stock[0] with { Damage = 999999999 });
        Assert.True(table.Patch(msn));
        Assert.Equal(table, SurfaceTable.Of(WorldSettings.Read(msn)!));
        Assert.Equal(MapLook.Of(WorldSettings.Read(new MissionFile("surftst.cbt", p.Mission.ToBytes()))!), MapLook.Of(WorldSettings.Read(msn)!));
    }

    [Fact]
    public void RowsCompareByTheirBits()
    {
        Assert.Equal(SurfaceTable.Stock, new SurfaceTable(SurfaceTable.Stock.Rows));
        Assert.NotEqual(SurfaceTable.Stock, SurfaceTable.Stock.With(7, SurfaceTable.Stock[7] with { Unk_0x0c = 1 }));
        Assert.Throws<ArgumentException>(() => new SurfaceTable(SurfaceTable.Stock.Rows.Take(7)));
    }

    /// <summary>The stock table is the one most stock missions hold: every base-game campaign and melee map but M07, S02, S05, T01, T04 and T09.</summary>
    [InstallFact]
    public void MostStockMissionsHoldTheStockTable()
    {
        var install = TestInstall.Install;
        int same = 0, all = 0;
        foreach (var path in install.ListMissions(".msn", addon: false))
        {
            if (WorldSettings.Read(new MissionFile(path)) is not { } w) continue;
            all++;
            if (SurfaceTable.Of(w).Equals(SurfaceTable.Stock)) same++;
        }
        Assert.True(all >= 30, $"{all} missions");
        Assert.Equal(all - 6, same);
    }
}
