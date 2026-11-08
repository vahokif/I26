using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The Taurus car: the original gives the car the script's actor "taurus" names (any case, class-1 cars only) flag 0x10000,
/// load-based cornering, once the mission is read, whatever the play options say. Interstate '76's missions with such a
/// car are T01, T02, T03, T10, T11, T14 and S02; in T05 the label names a wreck, which is not a car.
/// </summary>
public class TaurusCorneringTests
{
    private static MissionSetup Load(string dir, string mission)
    {
        var l = new SnapshotRig.Loaded(dir);
        var world = new I76.Formats.Assembly.MissionWorld(l.Vfs, l.Asm, l.Install.FindMission(mission) ?? throw new FileNotFoundException(mission));
        return MissionSetup.Build(l.Vfs, world, l.Asm, rand: new CrtRand(1));
    }

    private static bool Flagged(Vehicle v) => (v.Flags & VehicleFlags.LoadBasedCornering) != 0;

    [Theory]
    [InlineData("t01")]
    [InlineData("t02")]
    [InlineData("t03")]
    [InlineData("t10")]
    [InlineData("t11")]
    [InlineData("t14")]
    [InlineData("s02")]
    public void TaurusCarCornersOnLoadAndNoOtherCarDoes(string mission)
    {
        if (SnapshotRig.Install is not { } dir) return;
        var setup = Load(dir, mission);
        var taurus = (Entity)setup.Script!.Vm!.Actors.Last(a => a.Label.Equals("taurus", StringComparison.OrdinalIgnoreCase)).Handle!;
        Assert.True(Flagged(taurus.Vehicle!), $"{mission}: {taurus.Name} has load-based cornering");
        Assert.Equal([taurus.Vehicle!], setup.Cars.Select(c => c.Vehicle).Where(Flagged));
    }

    [Fact]
    public void T05sTaurusIsAWreckAndNoCarCornersOnLoad()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var setup = Load(dir, "t05");
        var taurus = setup.Script!.Vm!.Actors.Last(a => a.Label.Equals("taurus", StringComparison.OrdinalIgnoreCase)).Handle as Entity;
        Assert.False(taurus?.Vehicle is { ObjectClass: 1 }, "T05's taurus is no car");
        Assert.DoesNotContain(setup.Cars, c => Flagged(c.Vehicle));
    }

    [Fact]
    public void OnlyTheCarLabelledTaurusInAnyCaseIsFlagged()
    {
        Entity Car(string name, uint cls) => new(name, [], 1, 0) { Vehicle = new Vehicle { ObjectClass = cls } };
        var taurus = Car("t03js01", 1);
        var other = Car("t02fr03", 1);
        var heli = Car("heli", 9);
        var wreck = new Entity("wreck", [], 2, 0);
        var actors = new List<(string Label, byte[] Id, object? Handle)>
        {
            ("user", [], other), ("TaUrUs", [], taurus), ("taurus2", [], heli), ("ledsled", [], null),
        };
        Assert.Same(taurus.Vehicle, MissionSetup.MarkTaurus(actors));
        Assert.True(Flagged(taurus.Vehicle!));
        Assert.False(Flagged(other.Vehicle!));
        Assert.False(Flagged(heli.Vehicle!));

        // a label naming a helicopter, a static object or nothing flags nothing
        Assert.Null(MissionSetup.MarkTaurus([("taurus", [], heli)]));
        Assert.Null(MissionSetup.MarkTaurus([("taurus", [], wreck)]));
        Assert.Null(MissionSetup.MarkTaurus([("taurus", [], null)]));
        Assert.False(Flagged(heli.Vehicle!));
    }

    [Fact]
    public void WithTwoActorsLabelledTaurusTheLastOneIsTheTaurus()
    {
        // the original's actor binding keeps the last match: only that car is flagged, and a last match that is no car
        // flags none
        Entity Car(string name) => new(name, [], 1, 0) { Vehicle = new Vehicle { ObjectClass = 1 } };
        var first = Car("a"); var last = Car("b");
        Assert.Same(last.Vehicle, MissionSetup.MarkTaurus([("taurus", [], first), ("TAURUS", [], last)]));
        Assert.False(Flagged(first.Vehicle!));
        Assert.True(Flagged(last.Vehicle!));
        var c = Car("c");
        Assert.Null(MissionSetup.MarkTaurus([("taurus", [], c), ("taurus", [], new Entity("wreck", [], 2, 0))]));
        Assert.False(Flagged(c.Vehicle!));
    }
}
