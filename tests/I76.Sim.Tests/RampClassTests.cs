using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Structures placed with the Ramp class id (ODEF type 0xb) and what a car meets of them. The original's test of a car
/// against a placement walks the placement's tree and stops at the first node any of the car's rays reach, each node
/// pre-tested with a sphere about its own origin; a contact is a drive-on (no response, the ground query carries the car)
/// only when the node touched is itself a ramp's or bridge's (type 0xb-0xd) and faces up. Measured in the original game
/// on the stock placements: T01's water tower and A01's grain hopper, both placed as type 0xb, destroyed and then driven
/// at (run-ups at 15 u/s, the car held on its heading until its first contact).
/// </summary>
public class RampClassTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>
    /// The player's car put down at <paramref name="from"/> facing <paramref name="dir"/> and held at 15 u/s along it for
    /// <paramref name="ticks"/> ticks. Returns the first object it hit (null for none) and where along the run it first
    /// stood more than 1.5 m over the bare terrain (null if it never did), with that height.
    /// </summary>
    private static (string? Hit, Vector2? Lifted, float Height) Run(MissionSetup setup, SimSession s, Vector2 from, Vector2 dir, int ticks = 50)
    {
        var player = setup.World.Player!;
        var fwd = Vector3.Normalize(new Vector3(dir.X, 0, dir.Y));
        player.Pos = new Double3(from.X, setup.Ground.BareHeight(from.X, from.Y) + 1f, from.Y);
        player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        for (int i = 0; i < 10; i++) s.Tick([default]);
        string? hit = null;
        Vector2? lifted = null;
        float height = 0;
        for (int i = 0; i < ticks; i++)
        {
            player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
            player.Vel = new Vector3(fwd.X * 15, player.Vel.Y, fwd.Z * 15);
            player.Speed = 15;
            player.AngVel = default;
            s.Tick([default]);
            foreach (var h in setup.World.Step.ObjectHits) if (h.Self == player) hit ??= h.Name;
            var p = player.Pos.ToVector3();
            float over = p.Y - setup.Ground.BareHeight(p.X, p.Z);
            if (lifted is null && over > 1.5f) { lifted = new Vector2(p.X, p.Z); height = over; }
        }
        return (hit, lifted, height);
    }

    private static (MissionSetup, SimSession, StaticObject) Destroyed(string mission, string name, float x, float z)
    {
        var (setup, s) = ClassIdTests.Build(mission);
        var o = ClassIdTests.At(setup, name, x, z);
        Assert.Equal(0xbu, o.Type);
        setup.Damage.DestroyStructure(o);
        return (setup, s, o);
    }

    /// <summary>
    /// T01's water tower (awatert1 at 3515, 40160, placed as type 0xb) destroyed: its legs lie on the ground as a ramp and
    /// its tank at their top end, its 'A' geometry far below the tank node's origin, so a car never meets the tank. Measured
    /// in the original: run at the tank's end from 40186 north, the car was lifted from 4.6 to 6.9 at z 40172.2 and drove
    /// over it and down the legs; run at its side from x 3545 along z 40170, lifted to 6.7 at x 3520.3. No contact in either.
    /// </summary>
    [Theory]
    [InlineData(3517.5f, 40186f, 0f, -1f, 40172.2f)]
    [InlineData(3545f, 40170f, -1f, 0f, 3520.3f)]
    public void ADestroyedWaterTowersTankIsClimbedNotHit(float x, float z, float dx, float dz, float liftedAt)
    {
        if (InstallDir is null) return;
        var (setup, s, _) = Destroyed("t01.msn", "awatert1", 3515, 40160);
        var (hit, lifted, height) = Run(setup, s, new Vector2(x, z), new Vector2(dx, dz));
        Assert.Null(hit);
        Assert.NotNull(lifted);
        float along = dz != 0 ? lifted!.Value.Y : lifted!.Value.X;
        Assert.InRange(along, liftedAt - 1.2f, liftedAt + 1.2f);
        Assert.InRange(height, 1.5f, 3.5f);
    }

    /// <summary>
    /// A01's grain hopper (bcrhopr1, type 0xb) destroyed: its bin lies as a ramp whose high end is a 4.6 m face. Measured in
    /// the original: run from (1553.5, 49710) north into that face, the car met nothing, passed under the bin and was lifted
    /// onto its top (27.1 to 30.3) at z 49736.2.
    /// </summary>
    [Fact]
    public void ADestroyedGrainHoppersHighEndIsNoWall()
    {
        if (InstallDir is null) return;
        var (setup, s, _) = Destroyed("a01.msn", "bcrhopr1", 1547.7f, 49743.3f);
        var (hit, lifted, height) = Run(setup, s, new Vector2(1553.5f, 49710), new Vector2(0, 1));
        Assert.Null(hit);
        Assert.NotNull(lifted);
        Assert.InRange(lifted!.Value.Y, 49735f, 49737.5f);
        Assert.InRange(height, 2.5f, 4f);
    }

    /// <summary>
    /// The breakable wooden ramps (aaramp5, aaramp7) placed with the Ramp class id: their wreck, a heap of planks, is the
    /// root's 'A' geometry and so takes the placement's type, a ramp's: driven over, never hit. A mission maker's check in
    /// the original (the heap keeps the Ramp class; 0.12 drives it so).
    /// </summary>
    [Theory]
    [InlineData("aaramp5")]
    [InlineData("aaramp7")]
    public void AWoodenRampsWreckIsDrivenOver(string name)
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("t01.msn", (name, 0xbu, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, name, 4100, 36700);
        setup.Damage.DestroyStructure(o);
        Assert.True(o.Destroyed);
        var heap = o.Polys.SelectMany(p => p.V).ToList();
        float far = heap.Max(v => v.Z);
        var (hit, _, _) = Run(setup, s, new Vector2(o.Centre.X, heap.Min(v => v.Z) - 10), new Vector2(0, 1), ticks: 60);
        Assert.Null(hit);
        Assert.True(setup.World.Player!.Pos.ToVector3().Z > far, "the car did not get past the heap");
    }

    /// <summary>
    /// The standing water tower's legs (its type-0xb node, walls only) stop a car where they stand: the placement's sphere
    /// is the collider record's, its 3D radius about its middle 6.5 m up, not the 3.5 m the tower spans across. Measured in
    /// the original: run north at it from z 40148, the car met AW1_BAS1 with its middle at z 40157.7, its nose at the legs'
    /// south face (40160.0).
    /// </summary>
    [Fact]
    public void AStandingWaterTowerStopsACarAtItsLegs()
    {
        if (InstallDir is null) return;
        var (setup, s) = ClassIdTests.Build("t01.msn");
        var (hit, reached, _) = ClassIdTests.Ram(setup, s, new Vector2(3517.5f, 40148), new Vector2(0, 1));
        Assert.Equal("awatert1", hit);
        Assert.InRange(reached, 9f, 10.5f);
    }

    /// <summary>
    /// The walk: each node before its children, a node's children last record first. The hopper's records are CR1_LEG1,
    /// CR1_BIN1 (LEG1's child) and the marker CR1_ZZZ1: measured in the original, the root's first child is the marker,
    /// its second the leg.
    /// </summary>
    [Fact]
    public void TheWalkTakesEachNodesChildrenLastRecordFirst()
    {
        var walk = StructureNode.Order([-1, 0, -1]);
        Assert.Equal([2, 3, 1], walk);
        // a garage whose floor and door hang under its walls, and one whose parts are all top-level (bcaik_s3, bcaik_s7)
        Assert.Equal([1, 3, 2], StructureNode.Order([-1, 0, 0]));
        Assert.Equal([3, 2, 1], StructureNode.Order([-1, -1, -1]));
    }

    /// <summary>
    /// The Autowerks garages placed with the Ramp class id: the ground query takes a structure's first polygon that holds
    /// the point, in the original's order (the last node walked first). bcaik_s7's walls, floor and door are all top-level,
    /// so its roof comes before its floor and a car stays on it; bcaik_s3's and bcaik_s8's floor hangs under their walls,
    /// so the floor comes first and a car on the roof falls through. A mission maker's comparison in the original: a car stays on
    /// bcaik_s7's roof, not on bcaik_s3's or bcaik_s8's.
    /// </summary>
    [Theory]
    [InlineData("bcaik_s7", true)]
    [InlineData("bcaik_s3", false)]
    [InlineData("bcaik_s8", false)]
    public void AGaragesRoofHoldsACarWhenTheOriginalListsItFirst(string name, bool holds)
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", (name, 0xb, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, name, 4100, 36700);
        var roof = o.Surfaces.Where(p => p.N.Y > 0.99f).MaxBy(p => p.V.Max(v => v.Y))!;
        var c = new Vector3(roof.V.Average(v => v.X), roof.V.Max(v => v.Y), roof.V.Average(v => v.Z));
        // over the roof's middle, from just above it
        var (h, _) = setup.Ground.Query(c.X, c.Y + 0.5, c.Z);
        if (holds) Assert.Equal(c.Y, h, 0.05f);
        else Assert.True(h < c.Y - 3, $"{name}: the ground under the roof's middle is at {h:0.0}, the roof at {c.Y:0.0}");
    }
}
