using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Gates: type-7 nodes that slide when the script's triggerGate says.
/// Measured in the game (T07): gate1 slides 15 → 25 along its x from the start,
/// at rate 0.2, then stands with its target swapped; a trigger while it slides does nothing.
/// </summary>
public class GateTests(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void AGateSlidesToItsTargetAndBackOnTheNextTrigger()
    {
        var g = new GateMotion { Offset = new Vector3(10, 0, 0), Delta = new Vector3(10, 0, 0), Rate = 0.2f, World = new Vector3(0, 0, -10) };
        Assert.False(g.Tick(0.05f));               // still until triggered
        Assert.True(g.Trigger());
        int ticks = 0;
        while (g.Tick(0.05f) && g.Moving) ticks++;
        // 1 - (1 - 0.01)^n within 0.2 of 10 m: n = ln(0.02) / ln(0.99), about 19.5 s at 20 Hz
        Assert.InRange(ticks, 380, 395);
        Assert.Equal(1f, g.X);
        Assert.Equal(new Vector3(0, 0, -10), g.Shift);
        Assert.True(g.Trigger());
        Assert.False(g.Trigger());                  // sliding: the second trigger is lost
        while (g.Moving) g.Tick(0.05f);
        Assert.Equal(0f, g.X);
    }

    [Fact]
    public void AStepPastTheRateSnapsToTheTarget()
    {
        // dt / time outside 0..0.95 puts each axis at the target at once
        var g = new GateMotion { Offset = Vector3.UnitX, Delta = Vector3.UnitX, Rate = 1f / 0.04f };
        g.Trigger();
        g.Tick(0.05f);
        Assert.False(g.Moving);
        Assert.Equal(1f, g.X);
    }

    [Fact]
    public void TheGatesBoxFollowsIt()
    {
        var o = new StaticObject { Name = "gate", Type = 2 };
        o.Nodes.Add(new StructureNode { Name = "post", Type = 2, Hp = 5000, MaxHp = 5000 });
        o.Nodes.Add(new StructureNode { Name = "arm", Type = 7, Hp = 5000, MaxHp = 5000,
            Gate = new GateMotion { Offset = new Vector3(10, 0, 0), Delta = new Vector3(10, 0, 0), Rate = 100f, World = new Vector3(10, 0, 0) } });
        o.Boxes.Add(new NodeBox(Rows.Identity, Vector3.Zero, new Vector3(-1), new Vector3(1), 1.7f) { Node = 0 });
        o.Boxes.Add(new NodeBox(Rows.Identity, new Vector3(5, 0, 0), new Vector3(-1), new Vector3(1), 1.7f) { Node = 1 });
        Assert.True(o.TriggerGate());
        Assert.True(o.TickGates(0.05f));
        Assert.Equal(Vector3.Zero, o.Boxes.Single(b => b.Node == 0).Origin);
        Assert.Equal(new Vector3(15, 0, 0), o.Boxes.Single(b => b.Node == 1).Origin);
        Assert.False(o.TickGates(0.05f));
    }

    private static bool Inside(NodeBox b, Vector3 p)
    {
        var l = b.Rot.ToLocal(p - b.Origin);
        return l.X >= b.Min.X && l.X <= b.Max.X && l.Y >= b.Min.Y && l.Y <= b.Max.Y && l.Z >= b.Min.Z && l.Z <= b.Max.Z;
    }

    /// <summary>
    /// T07's "abandoned road block" (gate1, afence5 at 1455, 47900): open from the start, so the road
    /// through it is clear; shut again once the player has passed it (within 40) and comes within 600 of
    /// gatebloc, the cops' block, where it stops the car as the original's does.
    /// </summary>
    [Fact]
    public void T07RoadBlockOpensAtTheStartAndShutsBehindThePlayer()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t07.msn")!), asm);
        var gate1 = setup.Statics.Objects.Single(o => o.Name == "afence5" && o.Centre.X > 1000);
        int gi = gate1.Nodes.FindIndex(n => n.Gate is not null);
        Assert.True(gi >= 0);
        var gate = gate1.Nodes[gi].Gate!;
        Vector3 Arm() => gate1.Boxes.Single(b => b.Node == gi).Centre;
        // the arm across the road, as placed; the road crosses it here
        var across = Arm();
        output.WriteLine($"arm at {across}, slides by {gate.World}");
        Assert.InRange(gate.World.Length(), 9.99f, 10.01f);

        var s = new SimSession(setup);
        var player = setup.World.Player!;
        s.AddDriver(player);
        float now = 0;
        void Run(float seconds, Func<bool>? until = null, float throttle = 0)
        {
            var input = new CarInput { Buttons = CarButtons.Analog, Throttle = (short)(throttle * 32767) };
            for (float end = now + seconds; now < end && until?.Invoke() != true; now += s.TickSeconds) s.Tick([input]);
        }
        void Place(float x, float z, Vector3 heading, float speed)
        {
            setup.World.Ai.Remove(player);   // nobody else at the wheel (the intro's goto)
            player.Pos = new Double3(x, setup.Ground.BareHeight(x, z) + 0.5f, z);
            player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, heading), Vector3.UnitY, heading);
            player.Vel = heading * speed;
            player.AngVel = default;
        }

        Run(1);
        Assert.True(gate.Moving);                 // triggered on the first tick
        Run(25);
        Assert.False(gate.Moving);
        Assert.Equal(1f, gate.X);
        Assert.InRange(Vector3.Distance(Arm(), across), 9.9f, 10.1f);
        Assert.DoesNotContain(gate1.Boxes, b => Inside(b, across));

        // open: a car driving west along the road goes through where the arm stood
        Place(across.X + 25, across.Z, -Vector3.UnitX, 15);
        Run(4, throttle: 0.6f);
        output.WriteLine($"through the open block: player at {player.Pos.X:0.0}, {player.Pos.Z:0.0}");
        Assert.True(player.Pos.X < across.X - 10, $"stopped at {player.Pos.X:0.0}");

        // within 600 of gatebloc (610, 47615) once the radio is done: gate1 shuts behind the player
        Place(1000, 47750, -Vector3.UnitX, 0);
        Run(30, () => gate.Moving);
        Assert.True(gate.Moving, "gate1 was not triggered again");
        output.WriteLine($"shuts at {now:0.0} s");
        Run(25);
        Assert.False(gate.Moving);
        Assert.Equal(0f, gate.X);
        Assert.InRange(Vector3.Distance(Arm(), across), 0, 0.01f);

        // shut: a car driving east into it stops there
        Place(across.X - 25, across.Z, Vector3.UnitX, 15);
        Run(4, throttle: 0.6f);
        output.WriteLine($"against the shut block: player at {player.Pos.X:0.0}, {player.Pos.Z:0.0}");
        Assert.True(player.Pos.X < across.X, $"went through to {player.Pos.X:0.0}");
    }

    /// <summary>
    /// T12's main gate (bcaikgat at 3825, 46900) is a model that is nothing but its sliding leaf. The game layer drew a placement's
    /// gates only after building its static mesh and skipped a definition whose static mesh came out empty, so this gate never
    /// appeared (the road stood open to the eye) while its box still stopped the car. The sim side: every part is the gate's.
    /// </summary>
    [Fact]
    public void T12MainGateIsAModelOfNothingButItsLeaf()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var model = new ModelAssembler(vfs).Assemble("bcaikgat.sdf");
        Assert.NotNull(model);
        var gateNodes = new I76.Formats.Bwd2.Bwd2File(vfs.TryRead("bcaikgat.sdf")!).NodesOfType(7);
        Assert.NotEmpty(gateNodes);
        Assert.NotEmpty(model!.Parts);
        Assert.All(model.Parts, p => Assert.Contains(p.NodeName, gateNodes));
        // and the sim has it: a type-7 root whose box lies across the road at 46895 and slides 10 m south
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, new ModelAssembler(vfs), install.Resolve("miss16", "t12.msn")!), new ModelAssembler(vfs));
        var gate = setup.Statics.Objects.Single(o => o.Name == "bcaikgat");
        Assert.Equal(7u, gate.Type);
        Assert.InRange(gate.Nodes.Single().Gate!.World.Z, -10.01f, -9.99f);
    }
}
