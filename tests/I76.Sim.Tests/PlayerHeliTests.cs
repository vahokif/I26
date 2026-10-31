using I76.Formats;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The player-flown helicopter: the class comes from the .vdf, not the placement.</summary>
public class PlayerHeliTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void EveryCarPlacementHasItsVdfClassAsItsOdefType()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var classOf = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint ClassOf(string vcf)
        {
            if (classOf.TryGetValue(vcf, out var c)) return c;
            var f = new Bwd2File(vfs.Read(vcf));
            var vcfc = f.First("VCFC")!.Value;
            var vdf = new Bwd2File(vfs.Read(Bin.CStr(f.Data, vcfc.PayloadOffset + 0x10, 13)));
            return classOf[vcf] = Bin.U32(vdf.Data, vdf.First("VDFC")!.Value.PayloadOffset + 0x14);
        }
        int checkedCount = 0;
        var bad = new List<string>();
        foreach (var dir in new[] { "miss16", "miss8" })
        {
            if (install.Resolve(dir) is not { } path) continue;
            foreach (var msn in Directory.EnumerateFiles(path).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)))
            {
                var w = new MissionWorld(vfs, asm, msn);
                foreach (var p in w.Placements.Where(p => p.Definition?.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) == true))
                {
                    checkedCount++;
                    if (ClassOf(p.Definition!) != p.Object.ObjectType) bad.Add($"{Path.GetFileName(msn)}: {p.Definition} placed as {p.Object.ObjectType}");
                }
            }
        }
        Assert.True(checkedCount > 0);
        Assert.Empty(bad);
    }

    [Fact]
    public void APlayerCarBuiltFromTheHelicopterVcfIsTypeNineAndClimbsAndFliesLevel()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "t17.msn")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vhelif.vcf", script: false);
        var world = setup.World;
        var p = world.Player!;
        Assert.Equal(9u, p.ObjectClass);
        Assert.True(p.IsHeli);
        Assert.True(setup.Script is null || setup.Script.Host.Player!.IsHeli);
        Assert.Equal((6000, 6000), (p.Heli!.Hp, p.Heli.MaxHp));
        Assert.Equal(VehicleFlags.EngineOn | HeliStep.Landed, p.Flags);
        Assert.NotNull(Specials.Slot(p, 0));   // the jammers survive
        var start = p.Pos;
        p.Throttle = 0.9999f;
        for (int i = 0; i < 28 * 4; i++)
        {
            world.Frame(0.036f);
            p.Throttle = 0.9999f;
        }
        Assert.True((p.Flags & HeliStep.Landed) == 0);
        float ground = setup.Ground.BareHeight(p.Pos.X, p.Pos.Z);
        Assert.InRange(p.Pos.Y - ground, 10f, 30f);
        Assert.True(System.Numerics.Vector3.Dot(p.Vel, p.Rot.Forward) > 5f);
        Assert.True(System.Numerics.Vector3.Distance(p.Pos.ToVector3(), start.ToVector3()) > 10f);
        Assert.True(p.Rot.Up.Y > 0.6f, $"up.y {p.Rot.Up.Y}");
        Assert.True((p.Flags & VehicleFlags.Dead) == 0);
    }

    /// <summary>
    /// #55: the ignition key does nothing to a helicopter, landed or flying, and its engine runs from the start. The key's
    /// only reader is in the car update, which a class-9 vehicle never runs. Measured in the original game (M01 melee in
    /// <c>vhelif</c>, S held 10 frames landed and again flying): the ignition state stays 1 through the hold, the car flags
    /// unchanged throughout, no cranking timer; the rotor loop plays from the start.
    /// </summary>
    [Fact]
    public void TheIgnitionKeyLeavesAHelicoptersEngineRunning()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!), asm, "vhelif.vcf", melee: ["vhelif.vcf"]);
        setup.Melee?.Start();
        var session = new I76.Sim.Session.SimSession(setup, rate: I76.Sim.Session.SimRate.FromLabel(28));
        var p = setup.World.Player!;
        session.AddDriver(p);
        Assert.True(p.IsHeli);
        var none = new I76.Sim.Session.CarInput();
        var ignition = new I76.Sim.Session.CarInput { Commands = I76.Sim.Session.CarCommands.StartEngine };
        for (int i = 0; i < 20; i++) session.Tick([none]);
        Assert.Equal(VehicleFlags.EngineOn | HeliStep.Landed, p.Flags & (VehicleFlags.EngineOn | HeliStep.Landed));
        for (int i = 0; i < 10; i++)
        {
            session.Tick([ignition]);
            Assert.True((p.Flags & VehicleFlags.EngineOn) != 0, "landed: still running");
            Assert.True((p.Flags & HeliStep.Landed) != 0, "landed: still on the ground");
        }
        // off the ground on the throttle, then the key again
        var up = new I76.Sim.Session.CarInput { Buttons = I76.Sim.Session.CarButtons.Analog, Throttle = 32767 };
        for (int i = 0; i < 28 && (p.Flags & HeliStep.Landed) != 0; i++) session.Tick([up]);
        Assert.True((p.Flags & HeliStep.Landed) == 0);
        for (int i = 0; i < 10; i++)
        {
            session.Tick([ignition]);
            Assert.True((p.Flags & VehicleFlags.EngineOn) != 0, "flying: still running");
        }
    }

    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The Rattler's "retpocileh" variant: the base shell names vuchuey.vdf, which ships nowhere (Nitro's shell: vuhuey.vdf).</summary>
    [Fact]
    public void TheMissingVuchueyVdfReadsAsTheHuey()
    {
        if (NitroDir is null) return;
        var vfs = new AssetVfs(new GameInstall(NitroDir));
        Assert.True(vfs.Exists("vuhuey.vdf"));
        Assert.Equal(vfs.Read("vuhuey.vdf"), vfs.TryRead("vuchuey.vdf"));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(28)]
    public void AFlownHelicopterInTheAirBaseMeleeTakesOffAndFlies(int hz)
    {
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("n53")!);
        // the Rattler's secret as an older build saved it: the Rattler's file with the body vuchuey.vdf and huey2.vtf
        var rat = vfs.Read("vfratlr1.vcf").ToArray();
        var vcfc = new I76.Formats.Bwd2.Bwd2File(rat).First("VCFC")!.Value.PayloadOffset;
        void Put(int off, int len, string t) { Array.Clear(rat, vcfc + off, len); System.Text.Encoding.ASCII.GetBytes(t).CopyTo(rat, vcfc + off); }
        Put(0x10, 13, "vuchuey.vdf"); Put(0x1d, 13, "huey2.vtf");
        AssetVfs.Addon["zzheli1.vcf"] = rat;
        try {
        var setup = MissionSetup.Build(vfs, w, asm, "zzheli1.vcf", melee: ["zzheli1.vcf", "valepre1.vcf"]);
        setup.Melee?.Start();
        var session = new I76.Sim.Session.SimSession(setup, rate: I76.Sim.Session.SimRate.FromLabel(hz));
        var p = setup.World.Player!;
        session.AddDriver(p);
        Assert.True(p.IsHeli);
        var up = new I76.Sim.Session.CarInput { Buttons = I76.Sim.Session.CarButtons.ThrottleUp };
        for (int i = 0; i < hz * 30; i++)
        {
            session.Tick([up]);
        }
        Assert.True((p.Flags & HeliStep.Landed) == 0);
        Assert.True(p.Speed > 30f);
        } finally { AssetVfs.Addon.TryRemove("zzheli1.vcf", out _); }
    }
}
