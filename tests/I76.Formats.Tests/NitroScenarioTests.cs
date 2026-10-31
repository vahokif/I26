using I76.Formats.Mission;
using I76.Formats.Shell;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class NitroScenarioTests
{
    private static GameInstall Nitro => new(NitroFactAttribute.Dir!);

    [NitroFact]
    public void ScenarioDatHasFourCharactersAndNineteenMissions()
    {
        var d = ScenarioDat.Parse(File.ReadAllText(Nitro.Resolve("addon", "SCENARIO.DAT")!));
        Assert.Equal(["Taurus", "Jade", "Skeeter", "Natty Dread"], d.Characters.Select(c => c.Name));
        Assert.Equal([6, 5, 4, 4], d.Characters.Select(c => c.Missions.Count));
        var m = d.Characters[0].Missions[0];
        Assert.Equal(("A New Hope", "p01.msn", false), (m.Title, m.File, m.Done));
        Assert.Equal([(20, 1), (7, 2), (5, 2)], m.Recommended);
        Assert.Equal("Mad Clown Disease", d.Characters[0].Missions[2].Title);
        Assert.Equal("p02.msn", d.Characters[0].Missions[2].File);
        Assert.False(d.AllDoneExceptLast);
    }

    [Fact]
    public void MarkDoneFlipsTheDoneDigitOnly()
    {
        var t = "CHARACTER\t\tA\r\nMISSION_COUNT\t2\r\nOne,\t\tp01.msn,\t0,\t1,\t20-1\r\nTwo,\tp02.msn, \t0,\t1,\t20-1\r\n";
        var u = ScenarioDat.MarkDone(t, "P01")!;
        Assert.Equal(t.Replace("p01.msn,\t0", "p01.msn,\t1"), u);
        var d = ScenarioDat.Parse(u);
        Assert.True(d.Characters[0].Missions[0].Done);
        Assert.False(d.Characters[0].Missions[1].Done);
        Assert.Null(ScenarioDat.MarkDone(t, "p09"));
    }

    [NitroFact]
    public void CarTablesAndUserMel()
    {
        // nitscar.def and nituser.mel are not shipped (GOG has neither): the original writes them as it runs, so the test reads
        // only what the game ships (nitshell.dll) and makes the rest
        var rows = CarTable.BuiltInNitscar(File.ReadAllBytes(Nitro.FindFile("nitshell.dll")!));
        Assert.Equal(31, rows.Count);
        Assert.Equal(new CarRow(1, 4, 4, "valepre"), rows[0]);
        Assert.Equal("vppirna", rows[23].Stem);
        var nc = CarTable.BuiltInNitcar(File.ReadAllBytes(Nitro.FindFile("nitshell.dll")!));
        Assert.Equal(37, nc.Count);
        Assert.Equal(new CarRow(0, 1, 1, "vagrapl"), nc[0]);
        Assert.Equal("vxhears", nc[36].Stem);
        Assert.Empty(CarTable.BuiltInNitcar(null));
        Assert.Empty(CarTable.BuiltInNitscar(new byte[4096]));
        Assert.Empty(RestrictionTable.Read(null));
        Assert.Empty(RestrictionTable.Read(new byte[4096]));
        Assert.Equal(([], []), RestrictionTable.Grid(new byte[4096]));
        // the tables are found by their contents: the restriction checklist is whole and its grid sits right before it
        var dll = File.ReadAllBytes(Nitro.FindFile("nitshell.dll")!);
        Assert.Equal(43, RestrictionTable.Read(dll).Count);
        var (gx, gy) = RestrictionTable.Grid(dll);
        Assert.Equal((6, 18), (gx.Length, gy.Length));
        // a fresh nituser.mel (what the original writes before a choice is made) round-trips
        var fresh = new NitUserMel().ToBytes();
        var u = NitUserMel.Parse(fresh);
        Assert.Equal((23, 1), (u.ScenarioModel, u.ScenarioVariant));
        Assert.Equal((0, 0, 1, 2, "Unknown"), (u.MeleeArena, u.MeleeModel, u.MeleeVariant, u.MeleeDrivers, u.MeleeDriver));
        Assert.Equal(fresh, u.ToBytes());
    }

    [NitroFact]
    public void ArenaNamesAndTracks()
    {
        var f = new MissionFile(Nitro.FindMission("n01")!);
        Assert.Equal("Evening Stroll", WorldSettings.ArenaName(f));
        Assert.Equal(4u, WorldSettings.Read(f)!.CdTrack);
        Assert.Equal("The Crater", WorldSettings.ArenaName(new MissionFile(Nitro.FindMission("n51")!)));
    }
}
