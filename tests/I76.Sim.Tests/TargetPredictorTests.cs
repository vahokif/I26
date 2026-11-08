using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Math;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The AI's target predictor (ai.md, the target predictor): tau + 0.09 s, a straight line below 0.05 rad/s of yaw rate,
/// otherwise the circle of radius speed / |yaw rate| centred on the right row by the yaw sign times the gear, the
/// angle tau' x side x yaw rate from the car, along the rows rather than the velocity.
/// </summary>
public class TargetPredictorTests
{
    private const float R = 20f, Th = 0.5f;   // 10 m/s at 0.5 rad/s, tau' = 1 s
    private static readonly float Ax = R * (1 - MathF.Cos(Th)), Az = R * MathF.Sin(Th);   // 2.4483, 9.5885

    private static Vehicle Car(float yawRate, float gear = 1f, Rows? rot = null) => new()
    {
        Pos = new Double3(100, 5, -50), Rot = rot ?? Rows.Identity, Vel = new Vector3(0, 0, 10 * gear), Speed = 10,
        AngVel = new Vector3(0, yawRate, 0), Gear = gear,
    };

    private static void Near(Vector3 want, Vector3 got)
    {
        Assert.True(Vector3.Distance(want, got) < 1e-3f, $"want {want}, got {got}");
    }

    [Fact]
    public void BelowTheYawThresholdItIsAStraightLineAlongTheVelocityPlusTheFixedLead()
    {
        var v = Car(0.049f);
        v.Vel = new Vector3(3, -1, 9);   // sliding and going downhill: all three axes
        Near(new Vector3(100, 5, -50) + v.Vel * 1.09f, TargetPredictor.Predict(v, 1f));
        Near(new Vector3(100, 5, -50) + v.Vel * 0.09f, TargetPredictor.Predict(v, 0f));
    }

    [Fact]
    public void ATurningCarIsPutOnItsCircleOnTheTurningSide()
    {
        var p = new Vector3(100, 5, -50);
        // forward, turning left (positive yaw rate in the sim's axes): centre 20 m to the left, carried along its travel
        Near(p + new Vector3(-Ax, 0, Az), TargetPredictor.Predict(Car(Th), 0.91f));
        // forward, turning right: centre 20 m to the right, but carried back round the circle, as the original's
        Near(p + new Vector3(Ax, 0, -Az), TargetPredictor.Predict(Car(-Th), 0.91f));
        Assert.Equal(R, Vector3.Distance(p + new Vector3(R, 0, 0), TargetPredictor.Predict(Car(-Th), 0.91f)), 3);
        Assert.Equal(R, Vector3.Distance(p + new Vector3(-R, 0, 0), TargetPredictor.Predict(Car(Th), 0.91f)), 3);
    }

    [Fact]
    public void TheArcFollowsTheRowsAndTheSpeedNotTheVelocitysDirection()
    {
        var v = Car(Th);
        v.Vel = new Vector3(-6, 0, 8);   // sliding: 37 degrees off the nose, same speed
        Near(new Vector3(100 - Ax, 5, -50 + Az), TargetPredictor.Predict(v, 0.91f));
        // facing +x (right row along -z): the same local offset turned with the car
        var east = Car(Th, 1f, Rows.AboutY(MathF.PI / 2));
        Near(new Vector3(100 + Az, 5, -50 + Ax), TargetPredictor.Predict(east, 0.91f));
    }

    [Fact]
    public void AReversingCarHasItsCentreOnTheMirroredSide()
    {
        var p = new Vector3(100, 5, -50);
        // in reverse with the heading turning right the tail swings left: centre 20 m to the left, carried along its travel
        Near(p + new Vector3(-Ax, 0, -Az), TargetPredictor.Predict(Car(-Th, -1f), 0.91f));
        // heading turning left: centre on the right, carried back round the circle (ahead of the car)
        Near(p + new Vector3(Ax, 0, Az), TargetPredictor.Predict(Car(Th, -1f), 0.91f));
    }

    [Fact]
    public void InGearZeroThePointIsTheRadiusAlongTheForwardRowWhateverTheTime()
    {
        var pitched = new Rows(Vector3.UnitX, new Vector3(0, 0.8f, -0.6f), new Vector3(0, 0.6f, 0.8f));
        var v = Car(-Th, 0f, pitched);
        var want = new Vector3(100, 5, -50) + pitched.Forward * R;
        Near(want, TargetPredictor.Predict(v, 0f));
        Near(want, TargetPredictor.Predict(v, 3f));
    }
}
