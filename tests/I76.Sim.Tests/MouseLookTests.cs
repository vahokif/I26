using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class MouseLookTests
{
    [Fact]
    public void OffDoesNothing() => Assert.Equal((0f, 0f), MouseLook.Orbit(100, -50, MouseLook.Off, false));

    [Fact]
    public void RightSwingsTheCameraToTheCarsLeftAndUpDropsIt()
    {
        var (yaw, pitch) = MouseLook.Orbit(100, -50, MouseLook.Normal, false);
        Assert.True(yaw < 0);     // the camera's yaw target goes down: it moves round to the left, the view turns right
        Assert.True(pitch < 0);   // up the screen lowers the camera
        Assert.Equal(-0.3f, yaw, 4);
    }

    [Fact]
    public void InvertYFlipsOnlyThePitch()
    {
        var a = MouseLook.Orbit(10, 10, MouseLook.Normal, false);
        var b = MouseLook.Orbit(10, 10, MouseLook.Normal, true);
        Assert.Equal(a.Yaw, b.Yaw);
        Assert.Equal(a.Pitch, -b.Pitch);
    }

    [Fact]
    public void FasterLevelsTurnFurther()
    {
        float Yaw(int l) => System.Math.Abs(MouseLook.Orbit(10, 0, l, false).Yaw);
        Assert.True(Yaw(1) < Yaw(2) && Yaw(2) < Yaw(3));
    }

    [Fact]
    public void FineSensitivityScalesFromTenToTwoHundredPercentInTenPercentSteps()
    {
        Assert.Equal((0f, 0f), MouseLook.Orbit(100, -50, false, 1.0f, false));
        Assert.Equal(-0.03f, MouseLook.Orbit(100, 0, true, 0.1f, false).Yaw, 4);
        Assert.Equal(-0.15f, MouseLook.Orbit(100, 0, true, 0.5f, false).Yaw, 4);
        Assert.Equal(-0.30f, MouseLook.Orbit(100, 0, true, 1.0f, false).Yaw, 4);
        Assert.Equal(-0.60f, MouseLook.Orbit(100, 0, true, 2.0f, false).Yaw, 4);

        Assert.Equal(0.1f, MouseLook.ClampSensitivity(0.01f), 4);
        Assert.Equal(2.0f, MouseLook.ClampSensitivity(5.0f), 4);
        Assert.Equal(1.1f, MouseLook.NextSensitivity(1.0f, 1), 4);
        Assert.Equal(0.9f, MouseLook.NextSensitivity(1.0f, -1), 4);
        Assert.Equal(2.0f, MouseLook.NextSensitivity(2.0f, 1, wrap: false), 4);
        Assert.Equal(0.1f, MouseLook.NextSensitivity(2.0f, 1, wrap: true), 4);
        Assert.Equal(2.0f, MouseLook.NextSensitivity(0.1f, -1, wrap: true), 4);
    }

    [Fact]
    public void LegacyMouseCameraLevelsMigrateToExactSensitivityRates()
    {
        Assert.Equal(0.5f, MouseLook.SensitivityFromLevel(MouseLook.Slow), 4);
        Assert.Equal(1.0f, MouseLook.SensitivityFromLevel(MouseLook.Normal), 4);
        Assert.Equal(2.0f, MouseLook.SensitivityFromLevel(MouseLook.Fast), 4);
        Assert.Equal(MouseLook.Off, MouseLook.LevelFromSensitivity(false, 1.0f));
        Assert.Equal(MouseLook.Slow, MouseLook.LevelFromSensitivity(true, 0.5f));
        Assert.Equal(MouseLook.Normal, MouseLook.LevelFromSensitivity(true, 1.0f));
        Assert.Equal(MouseLook.Fast, MouseLook.LevelFromSensitivity(true, 2.0f));
    }

    [Fact]
    public void CockpitMouseLookTurnsHeadRightOnMouseRightAndReturnsOrHolds()
    {
        var head = new MouseCockpitOffset();
        var (oy, op) = MouseLook.Orbit(100, -50, true, 1.0f, false);
        head.Step(0.016f, autoReturn: true, keyHeld: false, oy, op);
        Assert.True(head.Yaw > 0);     // moving mouse right turns pilot's head right (+yaw)
        Assert.True(head.Pitch < 0);   // moving mouse up tilts pilot's head up (-pitch)
        float startYaw = head.Yaw;

        // with autoReturn off, the head stays where the mouse left it
        head.Step(5.0f, autoReturn: false, keyHeld: false, 0, 0);
        Assert.Equal(startYaw, head.Yaw, 4);

        // with autoReturn on, waits ReturnDelay then eases back to forward
        head.Step(1.0f, autoReturn: true, keyHeld: false, 0, 0);
        Assert.Equal(startYaw, head.Yaw, 4);
        head.Step(1.0f, autoReturn: true, keyHeld: false, 0, 0);
        Assert.True(head.Yaw < startYaw && head.Yaw > 0);

        // holding a glance key clears the mouse offset immediately
        head.Step(0.016f, autoReturn: true, keyHeld: true, 0, 0);
        Assert.False(head.HasOffset);
    }

    /// <summary>#165: looking down is held at each heading to what keeps the arms' cut-off ends out of view, and turning the head pulls it up to that.</summary>
    [Fact]
    public void CockpitMouseLookLooksLessLowToTheSides()
    {
        const float d = MathF.PI / 180;
        var head = new MouseCockpitOffset();
        head.Step(0.016f, autoReturn: false, keyHeld: false, 0, 1);                        // straight ahead, the mouse pulled far down
        Assert.Equal(I76.Formats.Input.CockpitReach.MaxPitch(0), I76.Formats.Input.FreeLook.Rest + head.Pitch / d, 3);
        Assert.True(head.Pitch < MouseCockpitOffset.MaxDown);
        head.Step(0.016f, autoReturn: false, keyHeld: false, -90 * d, 0);                 // turned to the right window
        Assert.Equal(90, head.Yaw / d, 3);
        Assert.Equal(I76.Formats.Input.CockpitReach.MaxPitch(90), I76.Formats.Input.FreeLook.Rest + head.Pitch / d, 3);
        head.Step(0.016f, autoReturn: false, keyHeld: false, 0, 1);                        // and down again: no lower
        Assert.Equal(I76.Formats.Input.CockpitReach.MaxPitch(90), I76.Formats.Input.FreeLook.Rest + head.Pitch / d, 3);
        head.Step(0.016f, autoReturn: false, keyHeld: false, 0, -10 * d);                 // up is free
        Assert.Equal(I76.Formats.Input.CockpitReach.MaxPitch(90) - 10, I76.Formats.Input.FreeLook.Rest + head.Pitch / d, 3);
        // easing back to forward with Camera Return never passes under the reach either
        head.Step(0.016f, autoReturn: false, keyHeld: false, 30 * d, 1);                  // 60° right, as low as it goes
        for (int i = 0; i < 200; i++)
        {
            head.Step(0.05f, autoReturn: true, keyHeld: false, 0, 0);
            Assert.True(I76.Formats.Input.FreeLook.Rest + head.Pitch / d <= I76.Formats.Input.CockpitReach.MaxPitch(head.Yaw / d) + 1e-3f);
        }
        Assert.False(head.HasOffset);
    }

    [Fact]
    public void ArmoryCyclePrevStepsUpTheWeaponRackAndRespectsLinks()
    {
        var arm = new I76.Sim.Weapons.Armory();
        for (uint id = 0; id < 4; id++)
        {
            var def = I76.Sim.Weapons.Armory.HandgunDef(1) with { Class = id < 2 ? 2u : id + 1u };
            var hp = new I76.Formats.Bwd2.Hardpoint("w", id, 1, 2, new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]));
            arm.Weapons.Add(new I76.Sim.Weapons.Weapon
            {
                Def = def, Hardpoint = hp, Style = 2, MountKind = 1, MuzzlePos = default, BaseRot = default,
                Hp = 100, MaxHp = 100, Damage = 10, Ammo = 100,
            });
        }
        arm.Selected = 0;

        // unlinked: CyclePrev steps 0 -> 3 -> 2 -> 1 -> 0
        arm.Select(false, false, out bool changed, cyclePrevKey: true);
        Assert.True(changed);
        Assert.Equal(3, arm.Selected);
        arm.Select(false, false, out _, cyclePrevKey: true);
        Assert.Equal(2, arm.Selected);
        arm.Select(false, false, out _, cyclePrevKey: true);
        Assert.Equal(1, arm.Selected);
        arm.Select(false, false, out _, cyclePrevKey: true);
        Assert.Equal(0, arm.Selected);

        // linked (weapons 0 and 1 share class 2): CyclePrev from 0+1 goes to 3, then 2, then back to 0+1
        arm.Select(false, true, out _);
        Assert.Equal(0, arm.Selected);
        Assert.Equal([1], arm.Group);

        arm.Select(false, false, out _, cyclePrevKey: true);
        Assert.Equal(3, arm.Selected);
        arm.Select(false, false, out _, cyclePrevKey: true);
        Assert.Equal(2, arm.Selected);
        arm.Select(false, false, out _, cyclePrevKey: true);
        Assert.Equal(0, arm.Selected);
        Assert.Equal([1], arm.Group);
    }
}
