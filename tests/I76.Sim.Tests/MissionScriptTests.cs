using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

public class CbRadioTests
{
    private static (CbRadio Cb, List<string> Started, List<string> Cut) Radio(float len = 2f)
    {
        var started = new List<string>(); var cut = new List<string>();
        var cb = new CbRadio(_ => len, _ => false);
        cb.Started += l => started.Add(l.Name);
        cb.Cut += l => cut.Add(l.Name);
        return (cb, started, cut);
    }

    [Fact]
    public void LinesQueueAndPlayInTurn()
    {
        var (cb, started, _) = Radio();
        cb.Say("a", null, 3); cb.Say("b", null, 3);
        Assert.Equal(["a"], started);
        cb.Update(1999); Assert.Equal(["a"], started);
        cb.Update(2000); Assert.Equal(["a", "b"], started);
        cb.Update(4000); Assert.True(cb.Empty);
    }

    [Fact]
    public void PriorityTwoInterruptsButKeepsTheRest()
    {
        var (cb, started, cut) = Radio();
        cb.Say("a", null, 3); cb.Say("b", null, 3); cb.Say("now", null, 2);
        Assert.Equal(["a", "now"], started);
        Assert.Equal(["a"], cut);
        cb.Update(2000); Assert.Equal(["a", "now", "b"], started);
    }

    [Fact]
    public void PriorityOneFlushes_FiveOnlyWhenIdle_FourExpires()
    {
        var (cb, started, _) = Radio();
        cb.Say("a", null, 3); cb.Say("b", null, 3); cb.Say("ambient", null, 5);
        cb.Say("urgent", null, 1);
        Assert.Equal(["a", "urgent"], started);
        Assert.Single(cb.Queue);
        cb.Say("stale", null, 4);
        cb.Update(2000);      // urgent ends; stale has waited 2 s, so it plays
        Assert.Contains("stale", started);
        var (cb2, started2, _) = Radio(20f);
        cb2.Say("long", null, 3); cb2.Say("late", null, 4);
        cb2.Update(20000);
        Assert.DoesNotContain("late", started2);
    }

    [Fact]
    public void StopCbSilencesTheRestOfTheMission()
    {
        var (cb, started, _) = Radio();
        cb.Say(CbRadio.StopName, null, 3);
        cb.Say("a", null, 1);
        Assert.Empty(started);
    }
}

public class MissionScriptTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The Crater's 17 spawn and 2 regen points, the roster at free spawn points, no script, waves.</summary>
    [Fact]
    public void AMeleeSpawnsItsRosterAndRespawnsItAWaveLater()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, "valepre1.vcf", melee: ["vppirna1.vcf", "vcgcour1.vcf"]);
        var game = setup.Melee!;
        Assert.Equal((17, 2), (game.Spawns.Count, game.Regens.Count));
        Assert.Null(setup.Script!.Vm);
        int spawned = 0;
        game.Spawned += (_, _) => spawned++;
        game.Start();
        Assert.Equal(2, spawned);
        Assert.Equal(3, setup.World.Vehicles.Count);
        var cars = setup.World.Vehicles;
        Assert.All(cars, c => Assert.Contains(game.Spawns, s => System.Math.Abs(s.Pos.X - c.Pos.X) < 1e-3 && System.Math.Abs(s.Pos.Z - c.Pos.Z) < 1e-3));
        Assert.Equal(3, cars.Select(c => (c.Pos.X, c.Pos.Z)).Distinct().Count());    // each at a free point
        var host = setup.Script.Host;
        Assert.Equal(1, host.Player!.Team);
        Assert.All(cars.Where(c => c != setup.World.Player), c => Assert.Equal(2, host.Of(c)!.Team));
        Assert.All(cars.Where(c => c != setup.World.Player), c => Assert.All(c.Health!.Slots.Skip(17), sp => Assert.Null(sp)));   // no specials

        // both wrecks gone: the roster again, a skill step better, the attacker cap one higher
        foreach (var c in cars.Where(c => c != setup.World.Player).ToList()) c.Wreck = true;
        game.Frame(0.036f);
        Assert.Equal(4, spawned);
        Assert.Equal(3, setup.World.Vehicles.Count);
        Assert.Equal((2, 9), (game.Aggression, game.AttackCap));
    }

    /// <summary>T01's fireworks stand dies in its own xbldx2.xdf (the SDFC name field, which the engine keeps as "xbldx2.x") to afworks.wav.</summary>
    [Fact]
    public void TheFireworksStandGoesUpInItsOwnExplosion()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "t01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm);
        var stand = setup.Statics.Objects.Single(o => setup.PlacementOf.TryGetValue(o, out var p) && p.Definition is { } d && d.StartsWith("bcfirew1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("xbldx2.xdf", "afworks.wav"), (stand.Explosion, stand.Sound));
        setup.Damage.DestroyStructure(stand);
        var boom = Assert.Single(setup.Combat.Events.OfType<I76.Sim.Weapons.Explosion>());
        Assert.Equal("xbldx2.xdf", boom.Effect.File, ignoreCase: true);
        Assert.Equal(150f, boom.Effect.SplashDamage);
    }

    /// <summary>
    /// The SDFC explosion is the root's, and only a structure with a 0x1000000
    /// marker node has one: A01's metal ramp names xbldx1.xdf there but
    /// has no marker (and no exploding node flag), so it dies quietly.
    /// </summary>
    [Fact]
    public void OnlyAMarkedStructureGoesUpInItsSdfcExplosion()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        foreach (var (mission, def) in new[] { ("a01", "aaramp3"), ("t01", "bcbank1") })
        {
            var w = new MissionWorld(vfs, asm, install.Resolve("miss16", mission + ".msn")!);
            var setup = MissionSetup.Build(vfs, w, asm);
            var o = setup.Statics.Objects.First(o => setup.PlacementOf.TryGetValue(o, out var p) && p.Definition is { } d && d.StartsWith(def, StringComparison.OrdinalIgnoreCase));
            Assert.True(o.Explosion is null or "x1_bldx1" or "x1_cars1" or "xbldx1.xdf", $"{def}: {o.Explosion}");
            if (def == "aaramp3") Assert.Null(o.Explosion);
        }
    }

    /// <summary>A scenario's car (the shell's vehscn.vcf) takes the placement flagged as the player: S03's opening shot and its AI have it.</summary>
    [Fact]
    public void AScenarioCarReplacesThePlayersPlacement()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "s03.msn")!);
        var flagged = w.Placements.Single(p => p.Object.IsPlayer && p.Definition?.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) == true);
        var setup = MissionSetup.Build(vfs, w, asm, "valepre1.vcf");
        var player = setup.World.Player!;
        Assert.Equal(flagged.Object.Position.X, (float)player.Pos.X, 0.01f);
        Assert.Equal(flagged.Object.Position.Z, (float)player.Pos.Z, 0.01f);
        Assert.Equal("valepre1.vcf", setup.Cars.Single(c => c.Vehicle == player).Definition, ignoreCase: true);
        Assert.Same(player, setup.Script!.Host.Player!.Vehicle);
        // no second car on the player's placement
        Assert.DoesNotContain(setup.Cars, c => c.Vehicle != player && c.Placement == flagged);
    }

    /// <summary>A01's grain hopper, shot down, leaves its fallen bin: a ramp the ground query drives up.</summary>
    [Fact]
    public void TheGrainHopperFallsIntoARamp()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "a01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm);
        var hopper = setup.Statics.Objects.Single(o => o.Name == "bcrhopr1");
        var ramp = hopper.Remains!.Surfaces.OrderByDescending(p => p.V.Average(v => v.Y)).First();
        var at = new System.Numerics.Vector3(ramp.V.Average(v => v.X), 0, ramp.V.Average(v => v.Z));
        float terrain = setup.Ground.BareHeight(at.X, at.Z);
        setup.Damage.DestroyStructure(hopper);
        Assert.True(hopper.Solid);
        var (h, n) = setup.Ground.Query(at.X, terrain + 5, at.Z);
        Assert.True(h > terrain + 1, $"on the ramp: {h:0.00} over terrain {terrain:0.00}");
        Assert.True(n.Y < 0.99f, $"sloped: {n}");
        Assert.DoesNotContain(hopper.Polys, p => p.V.Any(v => v.Y > terrain + 6));   // the legs and the bin are gone
    }

    /// <summary>T01 with nobody at the wheel: Taurus leads, waits, then the mission fails for not keeping up.</summary>
    [Fact]
    public void T01FailsForNotKeepingUpWithTaurus()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "t01.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm);
        var script = setup.Script!;
        var h = script.Host;
        var lines = new List<string>();
        h.Cb.Started += l => lines.Add(l.Name);
        var taurus = setup.Entities.Single(e => e.Name == "t01js01");
        for (int f = 0; f < 200 / 0.036f && script.Result is null; f++)
        {
            setup.World.Step.CarHits.Clear();
            setup.World.Frame(0.036f);
            script.AfterFrame(0.036f);
        }
        Assert.False(script.Result);
        var end = h.End!.Value;
        Assert.Contains("keep up with Taurus", setup.ObjectiveText!.Failures[end.FailMessage]);
        Assert.Equal(Objectives.Succeeded & 0, h.Objectives.Flags[0] & Objectives.Succeeded);
        Assert.Equal(0, h.Objectives.Flags[0] & Objectives.Hidden);    // revealed
        Assert.True(taurus.NavIndex > 0);
        Assert.Contains("01sta09.wav", lines);
    }
}

public class PoetryTests
{
    [Fact]
    public void RequestPoemSignOffInTurn_OnlyWhenQuiet()
    {
        var started = new List<(string Name, object? Speaker)>();
        var cb = new CbRadio(_ => 2f, _ => false);
        cb.Started += l => started.Add((l.Name, l.Speaker));
        object player = "player", taurus = "taurus";
        var poetry = new Poetry(taurus, 1);

        cb.Say("chatter", null, 3);
        poetry.Request(cb, player);
        Assert.Single(cb.Queue);                       // someone is talking: nothing

        cb.Update(2000);
        poetry.Request(cb, player);
        Assert.Equal(3, cb.Queue.Count);
        Assert.Equal((Poetry.RequestLine, player), (cb.Queue[0].Name, cb.Queue[0].Speaker));
        Assert.StartsWith("POSTA", cb.Queue[1].Name);
        Assert.Same(taurus, cb.Queue[1].Speaker);
        Assert.StartsWith("GDSGC4", cb.Queue[2].Name);
        Assert.Same(player, cb.Queue[2].Speaker);
        poetry.Request(cb, player);
        Assert.Equal(3, cb.Queue.Count);               // the poem holds the radio
    }

    [Fact]
    public void EveryPoemOnceBeforeAnyAgain()
    {
        var cb = new CbRadio(_ => 1f, _ => false);
        var poetry = new Poetry(null, 7);
        var poems = new List<string>();
        double t = 0;
        for (int i = 0; i < 30; i++)
        {
            poetry.Request(cb, null);
            poems.Add(cb.Queue[1].Name);
            for (int k = 0; k < 3; k++) cb.Update(t += 1000);   // request, poem, sign-off
            Assert.True(cb.Empty);
        }
        Assert.Equal(15, poems.Take(15).Distinct().Count());
        Assert.Equal(15, poems.Skip(15).Distinct().Count());
    }

    [Theory]
    [InlineData(2, "rltau")]
    [InlineData(3, "rljad")]
    [InlineData(4, "rlske")]
    [InlineData(5, "rlnat")]
    public void NitroKey_SaysTheDriversFiveLinesInAnyOrderBeforeRepeating(int variant, string stem)
    {
        var cb = new CbRadio(_ => 1f, _ => false);
        var poetry = new Poetry(null, 11, variant);
        object player = "me";
        var said = new List<string>();
        double t = 0;
        for (int i = 0; i < 10; i++)
        {
            poetry.Request(cb, player);
            Assert.Single(cb.Queue);                       // one line, no request and no sign-off
            Assert.Equal((5, player), (cb.Queue[0].Priority, cb.Queue[0].Speaker));
            said.Add(cb.Queue[0].Name);
            cb.Update(t += 1000);
            Assert.True(cb.Empty);
        }
        var names = Enumerable.Range(1, 5).Select(k => $"{stem}{k:00}.WAV").ToHashSet();
        Assert.Equal(names, said.Take(5).ToHashSet());
        Assert.Equal(names, said.Skip(5).ToHashSet());
    }

    [Fact]
    public void NitroKey_GrooveWithI76Installed_SaysOneOfHisKillLines_WithoutIt_Nothing()
    {
        var cb = new CbRadio(_ => 1f, _ => false);
        var poetry = new Poetry("taurus", 4, 1, i76Sounds: true);
        var said = new List<string>();
        double t = 0;
        for (int i = 0; i < 36; i++)
        {
            poetry.Request(cb, "me");
            Assert.Single(cb.Queue);                       // one line: no request, poem or sign-off
            Assert.Equal((5, "me"), (cb.Queue[0].Priority, cb.Queue[0].Speaker));
            said.Add(cb.Queue[0].Name);
            cb.Update(t += 1000);
        }
        var all = I76.Formats.Audio.CompanionSounds.GrooveKillLines.Select(n => n.ToUpperInvariant() + ".WAV").ToHashSet();
        Assert.Equal(18, all.Count);
        for (int d = 0; d < 36; d += 18) Assert.Equal(all, said.Skip(d).Take(18).ToHashSet());   // a deck: all 18 before any repeats
        var none = new CbRadio(_ => 1f, _ => false);
        new Poetry("taurus", 4, 1).Request(none, "me");
        Assert.True(none.Empty);
        var other = new CbRadio(_ => 1f, _ => false);       // the other drivers ignore I76
        new Poetry("taurus", 4, 2, i76Sounds: true).Request(other, "me");
        Assert.StartsWith("RLTAU", other.Queue[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GDSGC67.WAV", true)]
    [InlineData("gdsgc01.wav", true)]
    [InlineData("GDSGC65.WAV", true)]
    [InlineData("GDSGC03.WAV", false)]
    [InlineData("GDSGC40.WAV", false)]
    [InlineData("POSTA01.WAV", false)]
    [InlineData("rltau01.WAV", false)]
    public void CompanionSounds_OnlyGroovesKillLinesAreTakenFromTheOtherGame(string name, bool expected) =>
        Assert.Equal(expected, I76.Formats.Audio.CompanionSounds.IsKillLine(name));

    [Fact]
    public void NitroKey_NothingWhileTheRadioIsBusy_AndNothingForAGrooveOrUnknownDriver()
    {
        var cb = new CbRadio(_ => 2f, _ => false);
        var poetry = new Poetry(null, 3, 2);
        cb.Say("chatter", null, 3);
        poetry.Request(cb, "me");
        Assert.Single(cb.Queue);
        cb.Update(2000);
        var fresh = new CbRadio(_ => 2f, _ => false);
        new Poetry(null, 3, 2).Request(fresh, "me");
        poetry.Request(cb, "me");                           // the busy request moved no card
        Assert.Equal(fresh.Queue[0].Name, cb.Queue[0].Name);

        foreach (int none in new[] { 1, 6 })
        {
            var quiet = new CbRadio(_ => 1f, _ => false);
            new Poetry(null, 3, none).Request(quiet, "me");
            Assert.True(quiet.Empty);
        }
        Assert.Empty(Poetry.DriverLines(1));
    }

    [Fact]
    public void NitroKey_TheDeckIsSavedAsSeedAndCount()
    {
        var cb = new CbRadio(_ => 1f, _ => false);
        var a = new Poetry(null, 5, 3);
        double t = 0;
        for (int i = 0; i < 7; i++) { a.Request(cb, "me"); cb.Update(t += 1000); }
        var b = new Poetry(null, 99, 3);
        b.Restore(5, 7);
        var cb2 = new CbRadio(_ => 1f, _ => false);
        for (int i = 0; i < 8; i++)
        {
            a.Request(cb, "me"); b.Request(cb2, "me");
            Assert.Equal(cb.Queue[0].Name, cb2.Queue[0].Name);
            cb.Update(t += 1000); cb2.Update(t);
        }
    }
}
