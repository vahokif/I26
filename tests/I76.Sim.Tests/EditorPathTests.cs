using System.Numerics;
using I76.Editor.Paths;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The map editor's paths in the sim: the world a playtest builds reads the script's paths through the editor's override of the
/// script payload (EditorSession.Prepare), so the script's orders send its cars along the edited points.
/// </summary>
public class EditorPathTests(ITestOutputHelper log)
{
    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void TheScriptsOrdersFollowTheEditedPoints()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var world = new MissionWorld(vfs, asm, install.FindMission("s04")!);
        var file = world.Mission.ReadFsmProgram()!;
        // every point of every path a metre east, as the path tool's drags would put them (one command a point)
        var store = PathStore.FromMission(world.Mission.Data);
        foreach (int first in store.Groups().Select(g => g.First).ToList())
            for (int k = 0; k < store[first].Points.Count; k++)
            {
                var g = store.GroupOf(first)!;
                PathEdits.MovePoint(store, g, k, new Vector2(g.Points[k].X + 1, g.Points[k].Z)).Apply();
            }
        world.Mission.FsmOverride = store.Payload();

        var setup = MissionSetup.Build(vfs, world, asm);
        var session = new SimSession(setup, rate: SimRate.FromLabel(20));
        session.AddDriver(setup.World.Player!);
        var program = setup.Script!.Vm!.Program;
        Assert.Equal(store.Count, program.Paths.Count);
        Assert.Equal(file.Code, program.Code);
        for (int i = 0; i < store.Count; i++) Assert.Equal(store[i].Points, program.Paths[i].Points);

        // the intro's order: the player's car driven along a path, the edited one
        var followed = new HashSet<int>();
        while (setup.Script.Host.Now < 12f)
        {
            session.Tick([new CarInput()]);
            foreach (var e in setup.Entities)
                if (e.NavPath is { Count: > 0 } np)
                {
                    int i = program.Paths.FindIndex(p => ReferenceEquals(p.Points, np) || p.Points.SequenceEqual(np));
                    Assert.True(i >= 0, "an entity follows a path that is not in the edited table");
                    Assert.False(file.Paths[i].Points.SequenceEqual(np));
                    followed.Add(i);
                }
        }
        log.WriteLine($"paths followed in the first 12 s: {string.Join(", ", followed.Select(i => program.Paths[i].Name))}");
        Assert.NotEmpty(followed);
    }
}
