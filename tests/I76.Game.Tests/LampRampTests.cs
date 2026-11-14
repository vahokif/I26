using I76.Game.Shell;
using I76.Game.Sim;
using I76.Game.Ui;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's incandescent lamps (LampRamp): a filament warms up over its rise time constant and cools over its longer
/// fall, the colour pulling toward orange over the last of the fall; [port] lamp_ramp=off is the original's one-frame switch.
/// </summary>
public class LampRampTests
{
    private const float Frame = 1f / 240f;

    /// <summary>The level after <paramref name="ms"/> of frames toward a target, from a start.</summary>
    private static float After(float start, float target, float ms, float riseMs, float fallMs)
    {
        float level = start;
        for (float t = 0; t < ms - 1e-3f; t += Frame * 1000f) level = LampRamp.Step(level, target, Frame, riseMs, fallMs);
        return level;
    }

    [Fact]
    public void AFilamentWarmsOverItsRiseConstantAndCoolsOverItsLongerFall()
    {
        // a 21 W tail or brake bulb: 63 % at one time constant (30 ms), full within a tenth of a second
        Assert.InRange(After(0, 1, LampRamp.RearRiseMs, LampRamp.RearRiseMs, LampRamp.RearFallMs), 0.60f, 0.69f);
        Assert.InRange(After(0, 1, 100, LampRamp.RearRiseMs, LampRamp.RearFallMs), 0.95f, 1f);
        // and dies away slower: 37 % left at 100 ms, an orange tail under 10 % by 250 ms, out well within a second
        Assert.InRange(After(1, 0, LampRamp.RearFallMs, LampRamp.RearRiseMs, LampRamp.RearFallMs), 0.34f, 0.40f);
        Assert.InRange(After(1, 0, 250, LampRamp.RearRiseMs, LampRamp.RearFallMs), 0.01f, 0.1f);
        Assert.Equal(0f, After(1, 0, 1500, LampRamp.RearRiseMs, LampRamp.RearFallMs));
        // a 55 W sealed beam is slower both ways
        Assert.True(LampRamp.HeadRiseMs > LampRamp.RearRiseMs && LampRamp.HeadFallMs > LampRamp.RearFallMs);
        Assert.InRange(After(0, 1, LampRamp.HeadRiseMs, LampRamp.HeadRiseMs, LampRamp.HeadFallMs), 0.60f, 0.66f);
        Assert.InRange(After(1, 0, LampRamp.HeadFallMs, LampRamp.HeadRiseMs, LampRamp.HeadFallMs), 0.34f, 0.40f);
        Assert.Equal(1f, After(0, 1, 2000, LampRamp.HeadRiseMs, LampRamp.HeadFallMs));
    }

    [Fact]
    public void TheStepIsMonotoneAndNeverOvershoots()
    {
        float level = 0, last = 0;
        for (int i = 0; i < 200; i++)
        {
            level = LampRamp.Rear(level, 1, Frame);
            Assert.True(level >= last && level <= 1f);
            last = level;
        }
        for (int i = 0; i < 400; i++)
        {
            level = LampRamp.Rear(level, 0, Frame);
            Assert.True(level <= last && level >= 0f);
            last = level;
        }
        // a frame's step depends on its delta alone: a long frame gets as far as the short ones it stands for
        float coarse = LampRamp.Head(0, 1, 0.1f), fine = After(0, 1, 100, LampRamp.HeadRiseMs, LampRamp.HeadFallMs);
        Assert.InRange(coarse, fine - 0.01f, fine + 0.01f);
    }

    [Fact]
    public void TheColourCoolsTowardOrangeUnderFortyPercent()
    {
        Assert.Equal((1f, 1f, 1f), LampRamp.Warmth(1f));
        Assert.Equal((1f, 1f, 1f), LampRamp.Warmth(LampRamp.CoolBelow));
        Assert.Equal((LampRamp.CoolR, LampRamp.CoolG, LampRamp.CoolB), LampRamp.Warmth(0f));
        var (r, g, b) = LampRamp.Warmth(LampRamp.CoolBelow / 2);
        Assert.Equal(1f, r);
        Assert.InRange(g, LampRamp.CoolG, 1f);
        Assert.InRange(b, LampRamp.CoolB, g);     // red-shifted: blue goes first
    }

    private static DevSetting Key(string key) => Assert.Single(DevSettings.All, e => e.Key == key);

    /// <summary>[port] lamp_halo: the glows and glares at 65 % by default (a third under), 0 to 200, the default for a word that is not a number.</summary>
    [Fact]
    public void TheHaloIsATHirdUnderByDefaultAndReadsItsPercent()
    {
        int was = LampHalo.Percent;
        try
        {
            Assert.Equal(65, LampHalo.Default);
            Assert.Equal(65, new ShellSettings().LampHalo);
            Assert.Equal(65, LampHalo.Parse(null));
            Assert.Equal(65, LampHalo.Parse(""));
            Assert.Equal(65, LampHalo.Parse("many"));
            Assert.Equal(100, LampHalo.Parse("100"));
            Assert.Equal(200, LampHalo.Parse("999"));
            Assert.Equal(0, LampHalo.Parse("-5"));
            var e = Key("lamp_halo");
            Assert.Equal(DevWhen.Live, e.When);
            Assert.Equal("65", e.Values[e.Get(new ShellSettings())]);
            e.Apply!(new ShellSettings { LampHalo = 100 });
            Assert.Equal(100, LampHalo.Percent);
            if (LampHalo.Hook is null) Assert.Equal(1f, LampHalo.Factor);
            e.Apply!(new ShellSettings());
            if (LampHalo.Hook is null) Assert.Equal(0.65f, LampHalo.Factor);
        }
        finally { LampHalo.Percent = was; }
    }

    [Fact]
    public void LampRampOffIsTheOriginalsOneFrameSwitch()
    {
        bool was = LampRamp.Setting;
        try
        {
            var s = new ShellSettings();
            Assert.True(s.LampRamp);
            var e = Key("lamp_ramp");
            Assert.Equal(DevWhen.Live, e.When);
            e.Apply!(new ShellSettings { LampRamp = false });
            Assert.Equal(LampRamp.Hook is { } h ? h != 0 : false, LampRamp.Enabled);
            if (LampRamp.Hook is null)
            {
                Assert.Equal(1f, LampRamp.Rear(0, 1, Frame));
                Assert.Equal(0f, LampRamp.Head(1, 0, Frame));
            }
            e.Apply!(s);
            Assert.Equal(LampRamp.Hook is { } h2 ? h2 != 0 : true, LampRamp.Enabled);
        }
        finally { LampRamp.Setting = was; }
    }
}
