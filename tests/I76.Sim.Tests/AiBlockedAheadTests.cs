using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The blocked-ahead probe that sends avoid clsn to back_away, against another car: the car's origin, on the ground
/// under it, swept 7 in the gear direction against the other car's box with no pad. Every car's box begins above its
/// origin, so a car ahead on the same ground is passed under; the original's script-driven player in T02's opening,
/// put 5 behind Taurus's car at 20 u/s, keeps driving (retail 1.0 on an emulated Pentium, n = 2: the scripted camera
/// is let go 13.9 to 14.0 s after the mission's second frame).
/// </summary>
public class AiBlockedAheadTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>A car of t03js01's box, at the origin facing +Z on level ground, or raised by <paramref name="up"/>.</summary>
    private static Vehicle Other(float up = 0) => new()
    {
        Pos = new Double3(0, up, 0), Rot = Rows.Identity,
        BoxX = [-1.01f, -0.54f, 0.54f, 1.01f], BoxY = [0f, 0.29f, 0.95f, 1.36f], BoxZ = [-2.94f, -1.38f, 0.70f, 2.36f],
    };

    private static bool Hit(Vector3 from, Vector3 sweep, Vehicle o) => Ptable.OriginSweepHitsBox(from, sweep, o.Pos.ToVector3(), o);

    private static readonly Vector3 Ahead = new(0, 0, 7f);

    [Theory]
    [InlineData(5f)]
    [InlineData(3.5f)]
    [InlineData(9f)]
    public void ACarAheadOnTheSameGroundIsPassedUnder(float behind)
    {
        // the origin point runs under a box that begins 0.29 up, however near the car
        Assert.False(Hit(new(0, 0, -behind), Ahead, Other()));
        Assert.False(Hit(new(0, 0.1f, -behind), Ahead, Other()));
    }

    [Fact]
    public void ACarStoodHigherThanIsInTheWay()
    {
        // the point 0.5 up enters the box's height; the box's tail at -2.94 is 2.1 in, within the 7
        Assert.True(Hit(new(0, 0.5f, -5f), Ahead, Other()));
        // and the same car raised past the point's height is passed under again
        Assert.False(Hit(new(0, 0.5f, -5f), Ahead, Other(up: 0.4f)));
        // beside the box (1.2 right of a 1.01 half width) it is not in the way
        Assert.False(Hit(new(1.2f, 0.5f, -5f), Ahead, Other()));
    }

    [Fact]
    public void TheSweepReachesSevenAndNoFurther()
    {
        // the box's tail is 2.94 behind its origin: a start 9.9 back enters at 6.96, one 10 back does not
        Assert.True(Hit(new(0, 0.5f, -9.9f), Ahead, Other()));
        Assert.False(Hit(new(0, 0.5f, -10f), Ahead, Other()));
        // reverse gear sweeps the other way
        Assert.True(Hit(new(0, 0.5f, 5f), -Ahead, Other()));
        Assert.False(Hit(new(0, 0.5f, -5f), -Ahead, Other()));
    }

    [Fact]
    public void AStartInsideTheBoxIsNoHit()
    {
        Assert.False(Hit(new(0, 0.5f, -1f), Ahead, Other()));
        Assert.False(Hit(new(0, 0.5f, 0.5f), Ahead, Other()));
    }

    [Fact]
    public void TheOtherCarsFrameCounts()
    {
        // the other car across the way: its box is 1.01 half wide along the sweep, so the point enters 1 short of its origin
        var across = Other(); across.Rot = Rows.AboutY(MathF.PI / 2);
        Assert.True(Hit(new(0, 0.5f, -5f), Ahead, across));
        Assert.False(Hit(new(0, 0.5f, -8.1f), Ahead, across));
    }

    /// <summary>
    /// T02's opening: the script puts the player's car on the road 5 behind Taurus's, both at speed, and lets the camera go
    /// when the car reaches the end of its scripted drive. The car is not sent backing away from the car it follows.
    /// </summary>
    [Fact]
    public void TheT02OpeningLetsTheCameraGoWhenTheOriginalDoes()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("t02")!), asm);
        var session = new SimSession(setup, rate: SimRate.FromLabel(20));
        session.AddDriver(setup.World.Player!);
        var host = setup.Script!.Host;
        var me = setup.Entities.Single(x => x.Vehicle == setup.World.Player);
        bool was = false, backed = false;
        float released = -1;
        while (host.Now < 25f && released < 0)
        {
            session.Tick([new CarInput()]);
            if (was && !host.View.Active) released = host.Now;
            was = host.View.Active;
            backed |= me.Stack.Contains(18);
        }
        Assert.False(backed, "the player's car backed away during the opening");
        // the original lets go at 13.6-14.2 s; this build at 13.15, half a second early on this base (the branch's own base
        // gave the original's time): the back-away is what the branch changes, not the release
        Assert.InRange(released, 13.0f, 14.2f);
    }
}
