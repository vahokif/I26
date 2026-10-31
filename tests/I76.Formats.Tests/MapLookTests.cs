using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The map's look (palette, sky, ground, horizon, hour: WRLD fields) as the map writer saves it and the readers read it.</summary>
public class MapLookTests
{
    private static readonly MapLook Other = new("t02_16.act", "nk_6cld0.map", "tp081sd6.map", "p13_16.hzd", 6);

    [Fact]
    public void AChosenLookIsSavedAndReadBackAndNothingElseChanges()
    {
        var p = MapProject.CreateBlank("looktst", "Look Test");
        var before = p.Mission.ToBytes();
        var wrldBefore = (byte[])p.Mission.WorldPayload!.Clone();
        Assert.Equal(new MapLook("p18_16.act", "nk_6cldb.map", "tp181sw6.map", "p18_16.hzd", 18), p.Mission.Look);

        p.Mission.Look = Other;
        var bytes = p.Mission.ToBytes();
        var back = MapDocument.Parse(bytes);
        Assert.Equal(Other, back.Look);
        // the game's readers see it: the WRLD fields, and the names WDEF lists (what the world is built from)
        var msn = new MissionFile("looktst.cbt", bytes);
        var w = WorldSettings.Read(msn)!;
        Assert.Equal((Other.Palette, Other.Sky, Other.Ground, Other.Horizon, Other.Hour), (w.Palette, w.Sky, w.Ground, w.Horizon, w.Hour));
        Assert.Contains("tp081sd6.map", msn.WdefRefs());
        Assert.Contains("nk_6cld0.map", msn.WdefRefs());
        Assert.Equal("t02_16.act", msn.Palette());
        Assert.Equal(Lighting.ForHour(6).Index, Lighting.ForHour(w.Hour).Index);
        // only the four names and the hour differ, the rest of the WRLD and the file are as they were
        var wrld = back.WorldPayload!;
        for (int i = 0; i < wrld.Length; i++)
        {
            bool field = i is >= MapLook.PaletteAt and < MapLook.PaletteAt + 13 or >= MapLook.SkyAt and < MapLook.SkyAt + 13
                or >= MapLook.GroundAt and < MapLook.GroundAt + 13 or >= MapLook.HorizonAt and < MapLook.HorizonAt + 13 or >= MapLook.HourAt and < MapLook.HourAt + 4;
            if (!field) Assert.Equal(wrldBefore[i], wrld[i]);
        }
        Assert.Equal(before.Length, bytes.Length);
        // setting the look it already has (in any case) writes nothing
        back.Look = back.Look! with { Ground = "TP081SD6.MAP" };
        Assert.Equal(bytes, back.ToBytes());
    }

    [Fact]
    public void NoHorizonIsAnEmptyField()
    {
        var p = MapProject.CreateBlank("looktst", "Look Test");
        p.Mission.Look = p.Mission.Look! with { Horizon = "" };
        Assert.Equal("", MapDocument.Parse(p.Mission.ToBytes()).Look!.Horizon);
        Assert.Equal("", WorldSettings.Read(new MissionFile("x.cbt", p.Mission.ToBytes()))!.Horizon);
    }

    [Fact]
    public void NamesThatDoNotFitTheirFieldsAreRefused()
    {
        var p = MapProject.CreateBlank("looktst", "Look Test");
        var look = p.Mission.Look!;
        Assert.Throws<ArgumentException>(() => p.Mission.Look = look with { Ground = "tp081sd6" });             // no extension
        Assert.Throws<ArgumentException>(() => p.Mission.Look = look with { Ground = "tp082sd6.map" });         // not the family's level 1
        Assert.Throws<ArgumentException>(() => p.Mission.Look = look with { Sky = "a_very_long_sky.map" });     // does not fit 13 bytes
        Assert.Throws<ArgumentException>(() => p.Mission.Look = look with { Palette = "t01_16.pal" });
        Assert.Throws<ArgumentException>(() => p.Mission.Look = look with { Horizon = "p01.map" });
        Assert.Throws<ArgumentException>(() => p.Mission.Look = look with { Hour = 24 });
        Assert.Equal(look, p.Mission.Look);   // nothing was written
    }

    [Fact]
    public void ALookPatchedIntoAMissionInMemoryIsWhatTheReadersSee()
    {
        var p = MapProject.CreateBlank("looktst", "Look Test");
        var bytes = p.Mission.ToBytes();
        var msn = new MissionFile("looktst.cbt", bytes);
        Assert.True(WorldSettings.Patch(msn, Other));
        Assert.Equal(Other, MapLook.Of(WorldSettings.Read(msn)!));
        Assert.Equal("t02_16.act", msn.Palette());
        Assert.Contains("tp081sd6.map", msn.WdefRefs());
        Assert.NotEqual(p.Mission.ToBytes(), bytes);   // the buffer it was given is what was patched
    }

    [Fact]
    public void AnOldWrev8WrldHasNoHorizonAndKeepsItsHourWhereItIs()
    {
        // WREV 8: the WRLD without its 13-byte horizon field
        var p = MapProject.CreateBlank("looktst", "Look Test");
        var full = p.Mission.WorldPayload!;
        var old = full.Take(MapLook.HorizonAt).Concat(full.Skip(MapLook.HorizonAt + 13)).ToArray();
        var look = MapLook.Read(old, 8)!;
        Assert.Equal("", look.Horizon);
        Assert.Equal(18, look.Hour);
        (Other with { Horizon = "" }).WriteTo(old, 8);
        var back = MapLook.Read(old, 8)!;
        Assert.Equal(Other with { Horizon = "" }, back);
        (Other with { Horizon = "p01_16.hzd" }).WriteTo(old, 8);   // the field is not there: ignored
        Assert.Equal("", MapLook.Read(old, 8)!.Horizon);
    }
}
