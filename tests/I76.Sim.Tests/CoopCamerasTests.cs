using System.Numerics;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Fsm;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>Co-op's camera scenes (docs/multiplayer.md "Personal cut-scenes"): the survey of every stock mission, and a player's own scene.</summary>
public class CoopCamerasTests(ITestOutputHelper output)
{
    /// <summary>
    /// Every pushCam of every stock mission (the count) and the ones that are a player's own moment (their pcs): the leave-the-map
    /// camera of each thread that has one (pc 2160 in Interstate '76's library, 486 in Nitro's) and the jump cameras. Everything
    /// else is the story's: the default for what is not clearly the player's own.
    /// </summary>
    private static readonly Dictionary<string, (int Scenes, string Personal)> Survey = new()
    {
        ["a01"] = (2, ""),
        ["s01"] = (2, "2160"), ["s02"] = (4, ""), ["s03"] = (5, "2160,2241,2260"), ["s04"] = (3, "2160"), ["s05"] = (6, "2160,2233"), ["s06"] = (3, ""), ["s07"] = (3, "2160"),
        ["t01"] = (3, "2160"), ["t02"] = (5, "2160,4536"), ["t03"] = (3, "2160"), ["t04"] = (2, "2160"), ["t05"] = (6, "2160,4678"),
        ["t06"] = (11, "2160,4774,4807,4849,4887,4920"), ["t07"] = (4, "2160"), ["t08"] = (5, "2160"), ["t09"] = (2, "2160"), ["t10"] = (3, "2160,3408"),
        ["t11"] = (2, "2160"), ["t12"] = (4, "2160,4129"), ["t13"] = (2, "2160"), ["t14"] = (3, "2160"), ["t15"] = (3, "2160"), ["t16"] = (2, "2160"), ["t17"] = (3, "2160"),
        ["b01"] = (8, ""),
        ["p01"] = (4, "486"), ["p02"] = (5, "486"), ["p03"] = (4, "486"), ["p04"] = (4, "486"), ["p05"] = (4, "486"), ["p06"] = (6, "486"), ["p07"] = (4, "486"),
        ["p08"] = (4, "486"), ["p09"] = (6, "486"), ["p10"] = (5, "486"), ["p11"] = (5, "486"), ["p12"] = (4, "486"), ["p13"] = (4, "486"), ["p14"] = (4, "486"),
        ["p15"] = (7, "486"), ["p16"] = (4, "486"), ["p17"] = (5, "486"), ["p18"] = (5, "486"), ["p19"] = (7, "486"),
    };

    [Fact]
    public void EveryStockMissionsCameraScenesAreClassified()
    {
        int seen = 0;
        foreach (var dir in new[] { SnapshotRig.Install, SnapshotRig.NitroInstall })
        {
            if (dir is null) continue;
            var install = new GameInstall(dir);
            foreach (var m in SnapshotRig.Missions(dir))
            {
                if (install.FindMission(m) is not { } path || new MissionFile(path) is var mf && mf.ReadFsmProgram() is not { Code.Length: > 0 } p) continue;
                var objs = mf.PlacedObjects();
                PlacedObject? Of(int a) => a < 0 || a >= p.Actors.Count ? null
                    : objs.LastOrDefault(o => o.Raw.Length >= 8 && o.Raw.AsSpan(0, 8).SequenceEqual(p.Actors[a].InstanceId.AsSpan(0, System.Math.Min(8, p.Actors[a].InstanceId.Length))));
                var sites = CoopCameras.Analyze(p, a => Of(a)?.IsPlayer == true);
                Assert.True(Survey.TryGetValue(m, out var want), $"{m} is not in the survey");
                string personal = string.Join(",", sites.Where(s => s.Kind == CoopCameras.Kind.Personal).Select(s => s.Pc));
                output.WriteLine($"{m}: {sites.Count} scenes, personal [{personal}]");
                Assert.Equal(want.Scenes, sites.Count);
                Assert.Equal(want.Personal, personal);
                seen++;
            }
        }
        // 26 Interstate '76 scripts and 20 Nitro (T-series missions and the trainer included)
        if (SnapshotRig.Install is not null) Assert.True(seen >= 25);
    }

    private static Vector3 Path(MissionSetup setup, string name) =>
        setup.Script!.Vm!.Program.Paths.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Points[0];

    /// <summary>
    /// T06's first jump zone: the human who jumps (airborne in the zone) gets the jump camera on them, for them alone: the global
    /// camera stays off, the keys of that car are held as in a cut-scene, the other human's are not, and the scene ends by its own clock.
    /// </summary>
    [Fact]
    public void AJumpCameraIsTheJumpersAlone()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(l.Dir, "t06", coop: CoopSetup.Of(2)), SimRate.R20, 1);
        var host = setup.Script!.Host;
        void Idle(int ticks, CarInput[]? inputs = null) { for (int i = 0; i < ticks; i++) { s.Tick(inputs ?? new CarInput[s.Drivers.Count]); SnapshotRig.TakeEvents(s); } }
        // the opening is the story's, everyone waits in it; wait it out
        Idle(10);
        for (int i = 0; i < 6000 && host.View.Active; i++) Idle(1);
        Assert.False(host.View.Active);
        Idle(5);   // the others form up behind the lead once the opening lets go
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        var at = Path(setup, "jump1");
        // seat 1 is over the zone, high enough to be in the air for a while
        b.Pos = new Math.Double3(at.X, setup.World.Ground.Query(at.X, 500, at.Z).Height + 12, at.Z);
        b.Vel = Vector3.Zero; b.Speed = 0;
        bool started = false;
        for (int i = 0; i < 6 && !started; i++) { Idle(1); started = host.PersonalView(1) is not null; output.WriteLine($"tick {i}: pos {b.Pos.X:0.0},{b.Pos.Y:0.0},{b.Pos.Z:0.0} zone {at} flags {b.Flags} sites {string.Join(",", setup.Script!.Vm!.CameraSites.Where(x => x.Kind == CoopCameras.Kind.Personal).Select(x => x.Pc))} threads {string.Join(",", setup.Script.Vm.Threads.Where(t => t.Scene >= 0).Select(t => t.Index))} global {host.View.Active}/{host.View.Op}/{host.View.Depth}"); }
        Assert.True(started, "the jump camera did not start");
        Assert.False(host.View.Active);
        Assert.Null(host.PersonalView(0));
        Assert.True(s.CutsceneOf(b));
        Assert.False(s.CutsceneOf(a));
        Assert.False(s.Cutscene);
        // the camera watches the jumper
        var view = host.PersonalView(1)!;
        Idle(1);
        Assert.True(Vector3.Distance(view.LookAt ?? view.Eye, b.Pos.ToVector3()) < 6f, "the camera does not look at the jumper");
        // the other human keeps control: full throttle moves their car
        var forward = new CarInput { Buttons = CarButtons.ThrottleUp };
        double before = a.Speed;
        for (int i = 0; i < 20; i++) Idle(1, [forward, default]);
        Assert.True(a.Speed > before + 0.5 || a.Speed > 1, $"seat 0 did not get to drive on (speed {a.Speed})");
        // the scene ends on its timer (one second)
        for (int i = 0; i < 60 && host.PersonalView(1) is not null; i++) Idle(1);
        Assert.Null(host.PersonalView(1));
        Assert.False(s.CutsceneOf(b));
    }

    /// <summary>A human at a map exit is put back (the leave-map routine), and their leave-the-map camera ends with it: it no longer holds everybody's view for good.</summary>
    [Fact]
    public void TheExitCameraEndsWhenTheHumanIsPutBack()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(l.Dir, "t05", coop: CoopSetup.Of(2)), SimRate.R20, 1);
        var host = setup.Script!.Host;
        void Idle(int ticks) { for (int i = 0; i < ticks; i++) { s.Tick(new CarInput[s.Drivers.Count]); SnapshotRig.TakeEvents(s); } }
        Idle(200);
        var exit = Path(setup, "exit1");
        var b = setup.Humans[1];
        b.Pos = new Math.Double3(exit.X, setup.World.Ground.Query(exit.X, 500, exit.Z).Height, exit.Z);
        b.Vel = Vector3.Zero; b.Speed = 0;
        Idle(10);
        Assert.False(host.View.Active, "a human at the edge of the map took everyone's view");
        Assert.Null(host.PersonalView(1));
        Assert.Null(host.End);
    }
}
