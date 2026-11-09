using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Formats.Vfs;
using I76.Game.Boot;
using I76.Game.Shell;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The briefing played while the mission builds ([port] briefing_while_loading, Play Options' Load During Briefing).</summary>
public class BriefingOverlapTests
{
    private static bool Plan(bool setting = true, string? briefing = "int01f01.smk", bool found = true,
        bool skip = false, bool network = false, bool editor = false, bool retry = false)
        => BriefingOverlap.Plan(setting, briefing, found, skip, network, editor, retry);

    [Fact]
    public void AStartWithABriefingBuildsUnderIt() => Assert.True(Plan());

    [Fact]
    public void ItIsOnUnlessTheFileSaysOff() => Assert.True(new ShellSettings().BriefingWhileLoading);

    /// <summary>The original's order (the build, then the briefing) everywhere else.</summary>
    [Fact]
    public void EveryOtherStartKeepsTheOriginalsOrder()
    {
        Assert.False(Plan(setting: false));
        Assert.False(Plan(briefing: null));
        Assert.False(Plan(briefing: ""));
        Assert.False(Plan(found: false));     // a briefing named but not in smk/: nothing to build under
        Assert.False(Plan(skip: true));       // --skip-movies
        Assert.False(Plan(network: true));    // co-op plays its briefing before hosting or joining
        Assert.False(Plan(editor: true));
        Assert.False(Plan(retry: true));      // a checkpoint's retry has no briefing
    }

    [Fact]
    public void ItIsAPlayOptionsRow() => Assert.Contains("Load During Briefing", PlayOptionsScreen.PortRows);

    /// <summary>
    /// The mission read and the simulation set up on a thread of its own (the build under the briefing: a 64 MB stack, while this
    /// thread does the same) and here make the same game: the same seed hashes the same, tick for tick, for 30 s of T01 with its
    /// script and a driver.
    /// </summary>
    [InstallFact]
    public void TheGameSetUpOnTheBuildThreadIsTheSame()
    {
        var install = new GameInstall(TestInstall.Dir!);
        string msn = install.FindMission("t01")!;
        MissionSetup SetUp()
        {
            var vfs = new AssetVfs(install);
            var asm = new ModelAssembler(vfs);
            var world = new MissionWorld(vfs, asm, msn);
            var c = world.Centre();
            return MissionSetup.Build(vfs, world, asm, "", new System.Numerics.Vector3(c.X, c.Y + 60, c.Z), rand: new CrtRand(5), seed: 5);
        }
        MissionSetup? threaded = null;
        var thread = new Thread(() => threaded = SetUp(), 64 << 20);
        thread.Start();
        var here = SetUp();
        thread.Join();
        Assert.Equal(Hashes(here), Hashes(threaded!));
    }

    private static List<ulong> Hashes(MissionSetup setup)
    {
        var s = new SimSession(setup, SessionOptions.SinglePlayer);
        s.AddDriver(setup.World.Player!);
        var hashes = new List<ulong>();
        for (int t = 0; t < 600; t++)
        {
            // throttle, a slow weave, the guns now and then
            var b = CarButtons.ThrottleUp | ((t / 40) % 2 == 0 ? CarButtons.SteerLeft : CarButtons.SteerRight);
            if (t % 120 < 30) b |= CarButtons.Fire;
            var input = new CarInput { Buttons = b };
            s.Tick([input]);
            setup.Combat.Events.Clear(); setup.Damage.Events.Clear();
            hashes.Add(s.StateHash());
        }
        return hashes;
    }
}
