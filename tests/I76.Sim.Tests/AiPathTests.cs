using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Ai;
using I76.Sim.Fsm;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// AI cars on their paths (issue #24): the Seminole intro's bridge, Skeeter's van in T10 and the Milk Toast semi.
/// <list type="bullet">
/// <item>The on-road collision test skips its terrain check on a waypoint flagged as a bridge point (the waypoint's
/// second flag), not a road point: over a bridge the bare terrain below is a canyon.</item>
/// <item>Only one AI plans at a time. A search in progress carries on toward a moved goal, and ends when
/// the 1000-node pool runs out; restarting on every move, or running to 20000 nodes, kept the token from everyone else,
/// so a car that had reached a leg's nav point kept to its old waypoints and circled the last one.</item>
/// </list>
/// </summary>
public class AiPathTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Open : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 1;   // off-road everywhere: a step costs 15
        public Surface Surface(int id) => new(1f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void ASearchEndsWhenTheNodePoolRunsOut()
    {
        // 3 km over open ground: a node is created before it is expanded, so 1000 nodes last at most 10 calls
        // of 100 expansions (the old 20000-node cap ran for about 180)
        var s = PathSearch.Begin(new Vector2(1000, 1000), new Vector2(4000, 2500));
        int calls = 1;
        while (!s.Step(new Open(), true) && calls < 1000) calls++;
        Assert.True(calls <= 11, $"the search took {calls} calls");
        Assert.NotEmpty(s.Chain());
    }

    [Fact]
    public void ASearchCarriesOnTowardAMovingGoal()
    {
        // handle too far snapshots its target on every re-entry, so the goal moves each frame; the search still ends
        var goal = new Vector2(1300, 1000);
        var s = PathSearch.Begin(new Vector2(1000, 1000), goal);
        int calls = 1;
        while (!s.Step(new Open(), true) && calls < 1000) { s.Goal = goal += new Vector2(-0.3f, 0); calls++; }
        Assert.True(calls <= 11, $"the search took {calls} calls");
    }

    private static (MissionSetup Setup, SimSession Session) Load(string mission, int hz)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission(mission)!), asm);
        var session = new SimSession(setup, rate: SimRate.FromLabel(hz));
        session.AddDriver(setup.World.Player!);
        return (setup, session);
    }

    /// <summary>The escorts' work: armour and chassis pools refilled, so a car outlives the fight around it.</summary>
    private static void Heal(Vehicle v)
    {
        if (v.Health is not { } h) return;
        Array.Copy(h.ArmourMax, h.Armour, 4); Array.Copy(h.ChassisMax, h.Chassis, 4);
    }

    private static float Heading(Vehicle v) => MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z);

    /// <summary>The most the car turned, net, within any 30 s while it was in follow path (state 23 on top).</summary>
    private sealed class Winding(float tick)
    {
        private readonly Queue<float> _turns = new();
        private float _sum, _last = float.NaN;
        public float Most { get; private set; }

        public void Add(Entity e)
        {
            float h = Heading(e.Vehicle!), d = float.IsNaN(_last) ? 0 : h - _last;
            _last = h;
            if (d > MathF.PI) d -= 2 * MathF.PI;
            if (d < -MathF.PI) d += 2 * MathF.PI;
            if (e.Stack.Count == 0 || e.Stack[^1] != Ptable.FollowPath) d = 0;
            _turns.Enqueue(d); _sum += d;
            if (_turns.Count > (int)(30f / tick)) _sum -= _turns.Dequeue();
            Most = MathF.Max(Most, MathF.Abs(_sum) / (2 * MathF.PI));
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    public void TheSeminoleIntroCarStaysOnTheBridge(int hz)
    {
        if (InstallDir is null) return;
        var (setup, session) = Load("s04", hz);
        var me = setup.World.Player!;
        var e = setup.Entities.Single(x => x.Vehicle == me);
        float lowest = float.MaxValue;
        bool escaped = false;
        // the script drives the player's car west over the bridge (x 3650..3530 at z 50618, deck ~105, river ~15)
        // until the camera lets go at 16.5 s
        while (setup.Script!.Host.Now < 16f)
        {
            session.Tick([new CarInput()]);
            if (me.Pos.X < 3650 && me.Pos.X > 3530) lowest = MathF.Min(lowest, (float)me.Pos.Y);
            escaped |= e.Stack.Any(s => s is 16 or 17 or 18 or 19);
        }
        Assert.False(escaped, "the car took an escape state on the bridge");
        Assert.True(lowest > 100f, $"the car dropped to {lowest:0.0} at {hz} Hz");
        Assert.True(me.Pos.X < 3570, $"the car is at {me.Pos.X:0} at 16 s, short of the bridge's end");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    public void SkeetersVanReachesTatum(int hz)
    {
        if (InstallDir is null) return;
        var (setup, session) = Load("t10", hz);
        var host = setup.Script!.Host;
        var van = setup.Entities.Single(x => x.Name == "t10sv01");
        var me = setup.World.Player!;
        var winding = new Winding(session.TickSeconds);
        while (host.Now < 420f && !(Vector2.Distance(new((float)van.Vehicle!.Pos.X, (float)van.Vehicle.Pos.Z), new(640, 47870)) < 40f && van.Order == AiOrder.Sit))
        {
            // the cops lured away (objective 2): the flag thread 2 waits on, its argument -7, data word 14
            if (host.Now >= 36f) setup.Script.Vm!.Data[14] = 1;
            Heal(van.Vehicle); Heal(me);
            session.Tick([new CarInput()]);
            winding.Add(van);
        }
        Assert.True(host.Now < 420f, $"the van is at {van.Vehicle.Pos.X:0},{van.Vehicle.Pos.Z:0} after 420 s at {hz} Hz, not in Tatum");
        Assert.True(winding.Most < 1f, $"the van circled {winding.Most:0.00} turns in 30 s on its path");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    public void TheMilkToastSemiDoesNotCircle(int hz)
    {
        if (InstallDir is null) return;
        var (setup, session) = Load("s07", hz);
        var host = setup.Script!.Host;
        var semi = setup.Entities.Single(x => x.Name == "s07xm01");
        var me = setup.World.Player!;
        var winding = new Winding(session.TickSeconds);
        int moved = 0;
        void Put(float x, float z, float yawDeg)
        {
            float yaw = yawDeg * MathF.PI / 180f;
            var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
            me.Pos = new Math.Double3(x, setup.Ground.BareHeight(x, z), z);
            me.Rot = new Math.Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
            me.Vel = default;
        }
        // the player shows up by the convoy (the escorts turn on him), then is far away (they chase him across country)
        while (setup.Script.Result is null && host.Now < 320f)
        {
            if (moved == 0 && host.Now >= 30f) { Put(2700, 50260, 90); moved++; }
            if (moved == 1 && host.Now >= 45f) { Put(2000, 49000, 0); moved++; }
            Heal(me);
            session.Tick([new CarInput()]);
            winding.Add(semi);
        }
        Assert.True(winding.Most < 1f, $"the semi circled {winding.Most:0.00} turns in 30 s on its path at {hz} Hz");
        Assert.False(setup.Script.Result, "the mission should end with the semi in its compound");
    }
}
