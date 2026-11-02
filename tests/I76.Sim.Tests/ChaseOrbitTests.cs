using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The chase view's turn (<see cref="ChaseOrbit"/>): the pad's stick keeps the easing it always had (the stick's offset eased with
/// the track keys' targets at 2·dt), and only the mouse gets the closer easing of its own.
/// </summary>
public class ChaseOrbitTests
{
    private const float MinPitch = 0.017453292f, MaxPitch = 1.5707963f;

    /// <summary>The chase camera's angles as they were before the mouse had its own offset: one offset for stick and mouse, eased with the targets.</summary>
    private sealed class Before
    {
        private readonly MouseOrbitOffset _offset = new();
        public float Pitch, Yaw;

        public void Frame(float stickYaw, float stickPitch, float pitchT, float yawT, float dt, bool returnBehind)
        {
            _offset.Add(stickYaw, stickPitch);
            _offset.Step(dt, returnBehind);
            float pitchWant = System.Math.Clamp(pitchT + _offset.Pitch, MinPitch, MaxPitch);
            float yawWant = yawT + _offset.Yaw;
            float f = dt + dt;
            static float Ease(float cur, float want, float f) =>
                f >= 0 && f <= 0.95f && MathF.Abs(want - cur) >= 0.005f ? f * (want - cur) + cur : want;
            Pitch = Ease(Pitch, pitchWant, f);
            Yaw = Ease(Yaw, Yaw + ChaseOrbit.Wrap(yawWant - Yaw), f);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePadsStickTurnsTheViewExactlyAsBefore(bool returnBehind)
    {
        var rng = new Random(76);
        var before = new Before { Pitch = 0.5235988f };
        var now = new ChaseOrbit { Pitch = 0.5235988f };
        float pitchT = 0.5235988f, yawT = 0;
        for (int frame = 0; frame < 2000; frame++)
        {
            float dt = 1 / 60f + (float)rng.NextDouble() * 0.03f;
            // the stick pushed for a while, then let go (so the return runs), the keys now and then
            bool pushing = frame % 400 < 150;
            float sy = pushing ? (float)(rng.NextDouble() * 2 - 1) * MathF.PI * dt : 0;
            float sp = pushing ? (float)(rng.NextDouble() * 2 - 1) * MathF.PI / 2 * dt : 0;
            if (frame % 97 < 10) yawT += MathF.PI / 2 * dt;
            if (frame % 131 < 8) pitchT = System.Math.Clamp(pitchT - MathF.PI / 4 * dt, MinPitch, MaxPitch);
            before.Frame(sy, sp, pitchT, yawT, dt, returnBehind);
            now.Move(sy, sp, 0, 0, dt, returnBehind);
            now.Ease(pitchT, yawT, dt);
            Assert.Equal((before.Pitch, before.Yaw), now.View);
        }
    }

    [Fact]
    public void TheMouseFollowsTheHandMoreCloselyThanTheStick()
    {
        const float dt = 1 / 60f, turn = 0.5f;
        var stick = new ChaseOrbit { Pitch = 0.5235988f };
        var mouse = new ChaseOrbit { Pitch = 0.5235988f };
        stick.Move(turn, 0, 0, 0, dt, true);
        mouse.Move(0, 0, turn, 0, dt, true);
        for (int i = 0; i < 6; i++)
        {
            stick.Ease(0.5235988f, 0, dt);
            mouse.Ease(0.5235988f, 0, dt);
            stick.Move(0, 0, 0, 0, dt, true);
            mouse.Move(0, 0, 0, 0, dt, true);
        }
        // a tenth of a second on: the stick's turn eased at 2·dt (about a fifth of the way), the mouse's at 18·dt (most of it)
        Assert.InRange(stick.View.Yaw, 0.08f, 0.12f);
        Assert.InRange(mouse.View.Yaw, 0.40f, 0.5f);
    }

    [Fact]
    public void TheReturnWaitsForTheLastOfMouseAndStick()
    {
        const float dt = 1 / 30f;
        var o = new ChaseOrbit { Pitch = 0.5235988f };
        o.Move(0, 0, 1f, 0, dt, true);
        // the stick keeps moving (a little) for longer than the return's wait: the mouse's offset stays put meanwhile
        for (int i = 0; i < (int)(MouseOrbitOffset.IdleSeconds / dt) + 30; i++) { o.Move(0.0001f, 0, 0, 0, dt, true); o.Ease(0.5235988f, 0, dt); }
        Assert.Equal(1f, o.MouseYaw, 3);
        // then both rest, and it goes back behind the car
        for (int i = 0; i < 600; i++) { o.Move(0, 0, 0, 0, dt, true); o.Ease(0.5235988f, 0, dt); }
        Assert.Equal(0f, o.MouseYaw, 3);
    }
}
