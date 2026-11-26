using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A placement thrown whole (a sign knocked off, a flat piece placed as a Sign and blown away). Measured in the original
/// (T13's idisect1 placed as a Sign, one HE mortar blast on it, the game's rand() pinned to seed 1): thrown at
/// (−2.88, 6.44, −0.15), it flew at (−1.98, 3.13, −1.64) spinning at (−1.181, 3.741, 4.204), the first six values of the
/// jitter table that seed makes, x 10 and x 20; it turned about the middle of its bounds (5, 0, 10 in its own frame),
/// came down 0.5 s later and lay still, tilted 30 degrees, after a hop.
/// </summary>
public class ThrownPieceTests
{
    [Fact]
    public void TheTableIsTenRandsFromItsSeed()
    {
        var t = DebrisTable.Build(1);
        float[] measured = [0.0895f, -0.331f, -0.1487f, -0.0591f, 0.187f, 0.2102f];
        for (int i = 0; i < measured.Length; i++) Assert.InRange(t[i], measured[i] - 0.002f, measured[i] + 0.002f);
        var t0 = DebrisTable.Build(0);
        Assert.Equal(DebrisTable.Count, t0.Length);
        Assert.All(t0, x => Assert.InRange(x, -1f, 1f));
        Assert.InRange(t0.Average(), -0.05f, 0.05f);
        Assert.Equal(t0[0], DebrisTable.At(0));
    }

    [Fact]
    public void AThrowTakesSixTableValuesInTurn()
    {
        var table = new DebrisTable();
        var p = ThrownPiece.Throw(Vector3.Zero, Rows.Identity, Vector3.Zero, new Vector3(1, 2, 3), table);
        Assert.Equal(new Vector3(1, 2, 3) + 10 * new Vector3(DebrisTable.At(0), DebrisTable.At(1), DebrisTable.At(2)), p.Vel);
        Assert.Equal(20 * new Vector3(DebrisTable.At(3), DebrisTable.At(4), DebrisTable.At(5)), p.Spin);
        var q = ThrownPiece.Throw(Vector3.Zero, Rows.Identity, Vector3.Zero, Vector3.Zero, table);
        Assert.Equal(10 * DebrisTable.At(6), q.Vel.X);
    }

    private static (float, Vector3) Flat(Vector3 _) => (0f, Vector3.UnitY);

    [Fact]
    public void ItTurnsAboutTheMiddleOfItsBounds()
    {
        // idisect1: its middle 5, 10 from its origin, a corner
        var p = new ThrownPiece { Pivot = new Vector3(5, 0, 10), Rot = Rows.Identity, Centre = new Vector3(105, 20, 210), Vel = Vector3.Zero, Spin = new Vector3(0, 4, 0) };
        for (int i = 0; i < 10; i++) p.Step(1 / 30f, Flat);
        Assert.Equal(105, p.Centre.X, 4);
        Assert.Equal(210, p.Centre.Z, 4);
        Assert.NotEqual(new Vector3(100, p.Origin.Y, 200), p.Origin);   // the corner swung round it
        Assert.Equal(new Vector3(5, 0, 10).Length(), (p.Centre - p.Origin).Length(), 3);
    }

    [Fact]
    public void ItHopsAQuarterAndThenLiesStill()
    {
        var p = new ThrownPiece { Rot = Rows.Identity, Centre = new Vector3(0, 0.5f, 0), Vel = new Vector3(2, 6, 0), Spin = new Vector3(1, 2, 3) };
        float dt = 1 / 28f;
        bool landed = false;
        for (int i = 0; i < 56 && !landed; i++)
        {
            float vy = p.Vel.Y;
            p.Step(dt, Flat);
            if (p.Centre.Y == 0) { landed = true; Assert.True(p.Vel.Y <= -0.25f * (vy - 9.8f * dt) + 1e-3f); }
        }
        Assert.True(landed);
        Assert.False(p.Resting);                       // first a hop, a quarter of its speed
        for (int i = 0; i < 60; i++) p.Step(dt, Flat);
        Assert.True(p.Resting);
        Assert.Equal(0, p.Centre.Y);
        var lay = p.Rot;
        for (int i = 0; i < 60; i++) Assert.True(p.Step(dt, Flat));
        Assert.Equal(lay.Up, p.Rot.Up);                  // tilted as it landed, still
    }

    [Fact]
    public void ItIsGoneAfterFifteenSeconds()
    {
        var p = new ThrownPiece { Rot = Rows.Identity, Centre = new Vector3(0, 0, 0) };
        int frames = 0;
        while (p.Step(0.05f, Flat) && frames < 1000) frames++;
        Assert.InRange(frames * 0.05f, 14.9f, 15.1f);
    }

    [Fact]
    public void OneStillRisingThroughTheGroundIsGone()
    {
        var p = new ThrownPiece { Rot = Rows.Identity, Centre = new Vector3(0, -2, 0), Vel = new Vector3(0, 10, 0) };
        Assert.False(p.Step(0.05f, Flat));
    }
}
