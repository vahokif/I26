using I76.Game.Shell;
using I76.Game.Sim;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's muzzle light (MuzzleReach): a shot lights the world round the gun at night, the road and ground and the
/// cars about it, by the weapon (a cannon furthest), a trace by day. [port] muzzle_reach in percent: 100 the table, 0 the light
/// before the key (4 m, 0.8, on the firing car alone), and the 1997 look untouched.
/// </summary>
public class MuzzleReachTests
{
    private static string Repo(params string[] path)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine([dir, .. path]))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!, .. path])).ReplaceLineEndings("\n");
    }

    [Fact]
    public void TheKeyReadsAsAPercentAndTheDeveloperPanelStepsItLive()
    {
        Assert.Equal(100, MuzzleReach.Default);
        Assert.Equal(100, new ShellSettings().MuzzleReach);
        // absent or not a number: the default; out of range: the end
        Assert.Equal(100, MuzzleReach.Parse(null));
        Assert.Equal(100, MuzzleReach.Parse(""));
        Assert.Equal(100, MuzzleReach.Parse("far"));
        Assert.Equal(50, MuzzleReach.Parse("50"));
        Assert.Equal(200, MuzzleReach.Parse("999"));
        Assert.Equal(0, MuzzleReach.Parse("-3"));
        var e = Assert.Single(I76.Game.Ui.DevSettings.All, e => e.Key == "muzzle_reach");
        Assert.Equal(I76.Game.Ui.DevWhen.Live, e.When);
        int was = MuzzleReach.Percent;
        try
        {
            var s = new ShellSettings { MuzzleReach = 0 };
            Assert.Equal("0*", e.Values[e.Get(s)]);
            e.Apply!(s);
            Assert.Equal(0, MuzzleReach.Percent);
            s.MuzzleReach = 120;   // set by hand: shows as the nearest step
            Assert.Equal("125*", e.Values[e.Get(s)]);
            Assert.Equal("100", e.Values[e.Get(new ShellSettings())]);
        }
        finally { MuzzleReach.Percent = was; }
    }

    /// <summary>Every stock gun's ordnance kind (the install's weapon definitions) to its row; the droppers and flamers have no light.</summary>
    [Theory]
    [InlineData(0x12u, MuzzleReach.Gun.Handgun)]       // gh45
    [InlineData(1u, MuzzleReach.Gun.MachineGun)]       // gmlight, gmmedium, gmheavy and their turrets
    [InlineData(6u, MuzzleReach.Gun.Cannon)]           // gclight, gcmedium, gcheavy, gchades, the tank guns and the turrets
    [InlineData(2u, MuzzleReach.Gun.Rocket)]           // gdumb, tdumb
    [InlineData(3u, MuzzleReach.Gun.Rocket)]           // gsheat
    [InlineData(8u, MuzzleReach.Gun.Rocket)]           // gsradar, gscherub
    [InlineData(4u, MuzzleReach.Gun.Mortar)]           // ggrenade, gezkill, tthowitz
    [InlineData(5u, MuzzleReach.Gun.Mortar)]           // gcluster
    [InlineData(7u, MuzzleReach.Gun.Mortar)]           // gwhiteph
    [InlineData(0x16u, MuzzleReach.Gun.Mortar)]        // the Nitro chemical mortar
    [InlineData(0x15u, MuzzleReach.Gun.Other)]         // the Ball O'Flame: its own fire light
    public void EachGunHasItsRow(uint kind, MuzzleReach.Gun gun) => Assert.Equal(gun, MuzzleReach.Of(kind));

    [Fact]
    public void TheTableReachesFurtherByTheGunAndWellPastTheLightBeforeTheKey()
    {
        var guns = new[] { MuzzleReach.Gun.Handgun, MuzzleReach.Gun.MachineGun, MuzzleReach.Gun.Cannon, MuzzleReach.Gun.Rocket, MuzzleReach.Gun.Mortar };
        var b = MuzzleReach.Before;
        // the light before the key: one 4 m omni light, 0.8, 1.2 m ahead, on the world and the cabin alike; no far light
        Assert.Equal(new MuzzleReach.Lamp(4f, 0.8f, 1f), b.Near);
        Assert.Equal(0f, b.Far.Energy);
        Assert.Equal((1.2f, 0f, 0.05f, 1f, 0.8f), (b.Ahead, b.Lift, b.Life, b.Specular, b.Cabin));
        foreach (var g in guns)
        {
            var n = MuzzleReach.For(g, 0f, 100);
            Assert.Equal(MuzzleReach.Night(g), n);
            // the near light on the firing car: the old light's reach at less of its energy, the cabin's and the gloss below the old;
            // the far light: falling off far slower than the inverse distance, lifted above the barrel, its cone short of straight back
            Assert.InRange(n.Near.Range, 2f, 6f);
            Assert.True(n.Near.Energy < b.Near.Energy && n.Cabin < b.Cabin && n.Specular < b.Specular, $"{g}");
            Assert.InRange(n.Far.Decay, 0.1f, 0.5f);
            Assert.InRange(n.Lift, 1f, 4f);
            Assert.InRange(n.Cone, 80f, 120f);
            Assert.InRange(n.Ahead, 0.5f, 2f);
            Assert.InRange(n.Life, 0.04f, 0.12f);
            Assert.True(n.Far.Range >= 6 * b.Near.Range && n.Far.Energy > n.Near.Energy, $"{g}: {n.Far.Range} m");
        }
        float R(MuzzleReach.Gun g) => MuzzleReach.Night(g).Far.Range;
        float E(MuzzleReach.Gun g) => MuzzleReach.Night(g).Far.Energy;
        Assert.True(R(MuzzleReach.Gun.Cannon) > R(MuzzleReach.Gun.Rocket) && R(MuzzleReach.Gun.Rocket) > R(MuzzleReach.Gun.MachineGun));
        Assert.True(R(MuzzleReach.Gun.MachineGun) > R(MuzzleReach.Gun.Handgun) && R(MuzzleReach.Gun.Mortar) > R(MuzzleReach.Gun.Handgun));
        Assert.True(E(MuzzleReach.Gun.Cannon) > E(MuzzleReach.Gun.MachineGun) && E(MuzzleReach.Gun.MachineGun) > E(MuzzleReach.Gun.Handgun));
        Assert.Equal(b, MuzzleReach.Night(MuzzleReach.Gun.Other));
    }

    [Fact]
    public void ZeroIsTheLightBeforeTheKeyDayAndNight()
    {
        foreach (var g in Enum.GetValues<MuzzleReach.Gun>())
            foreach (float day in new[] { 0f, 0.625f, 1f })
                Assert.Equal(MuzzleReach.Before, MuzzleReach.For(g, day, 0));
    }

    [Fact]
    public void FullAtNightATraceInTheSunAndTheStepsBetween()
    {
        var g = MuzzleReach.Gun.Cannon;
        var n = MuzzleReach.Night(g);
        var b = MuzzleReach.Before;
        // full sun: the table's reach with a trace of its energy (less than the light before the key gave by day)
        var sun = MuzzleReach.For(g, 1f, 100);
        Assert.Equal(n.Far.Range, sun.Far.Range);
        Assert.Equal(n.Far.Energy * MuzzleReach.DayFloor, sun.Far.Energy, 5);
        Assert.Equal(n.Near.Energy * MuzzleReach.DayFloor, sun.Near.Energy, 5);
        Assert.True(sun.Far.Energy < b.Near.Energy && sun.Near.Energy < b.Near.Energy);
        // dusk: between
        Assert.InRange(MuzzleReach.For(g, 0.625f, 100).Far.Energy, sun.Far.Energy, n.Far.Energy);
        // half: halfway along each figure; 200: as far again
        var half = MuzzleReach.For(g, 0f, 50);
        Assert.Equal((b.Far.Range + n.Far.Range) / 2, half.Far.Range, 4);
        Assert.Equal((b.Far.Energy + n.Far.Energy) / 2, half.Far.Energy, 4);
        Assert.Equal((b.Near.Energy + n.Near.Energy) / 2, half.Near.Energy, 4);
        Assert.Equal((b.Lift + n.Lift) / 2, half.Lift, 4);
        Assert.Equal((b.Cabin + n.Cabin) / 2, half.Cabin, 4);
        var twice = MuzzleReach.For(g, 0f, 200);
        Assert.Equal(2 * n.Far.Range - b.Far.Range, twice.Far.Range, 4);
        Assert.Equal(twice, MuzzleReach.For(g, 0f, 999));
        // the reach grows with the setting
        float last = 0;
        foreach (int p in new[] { 0, 25, 50, 75, 100, 125, 150, 200 }) { float r = MuzzleReach.For(g, 0f, p).Far.Range; Assert.True(r > last); last = r; }
    }

    [Fact]
    public void TheCombatViewLightsEachShotFromTheTableInTheModernLookOnly()
    {
        var cv = Repo("game", "Scripts", "Sim", "CombatView.cs");
        // one call, behind the modern look's test, and the light it sets is the table's
        Assert.Matches(@"if \(IsModern && !IsDropped\(s\.Weapon\.Def\.Kind\) && s\.Weapon\.Def\.Class != 5 && [^\n]*\)\n\s*MuzzleLight\(s\);", cv);
        Assert.Contains("MuzzleReach.Current(MuzzleReach.Of(s.Weapon.Def.Kind))", cv);
        foreach (var f in new[] { "l.OmniRange = lamp.Range;", "l.OmniAttenuation = lamp.Decay;", "ShadowEnabled = false" }) Assert.Contains(f, cv);
        // the far light: a spot, lifted, aimed level along the barrel, its cone the table's; the cabin's lights only the cabin
        foreach (var f in new[] { "new SpotLight3D", "far.SpotAngle = f.Cone;", "far.GlobalPosition = at + Vector3.Up * f.Lift;", "far.LookAt(far.GlobalPosition + level, Vector3.Up);" })
            Assert.Contains(f, cv);
        Assert.Contains("LightCullMask = CarNode.CockpitLayer", cv);
        // hidden at the end of their life, and at once in the 1997 look
        Assert.Contains("(_t >= until || !IsModern)) light.Visible = false;", cv);
        Assert.Contains("(_t >= until || !IsModern)) far.Visible = false;", cv);
        Assert.Contains("MuzzleReach.Percent = s.MuzzleReach;", Repo("game", "Scripts", "Boot", "Main.cs"));
    }
}
