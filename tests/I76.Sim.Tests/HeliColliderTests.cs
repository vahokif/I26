using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A helicopter's collider sphere is its assembled model's, as measured in the original game: centred on the middle of
/// the model's bounds (rotors and weapons included), the radius the farthest model point or the farthest COLP corner +
/// 0.1. A splash reaches it over its radius + the blast's, horizontally from that centre.
/// </summary>
public class HeliColliderTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Theory]
    [InlineData("t08uc01.vcf", 0, 2.882, -3.397, 9.868)]    // the Huey
    [InlineData("vuchuey1.vcf", 0, 2.882, -3.397, 9.868)]
    [InlineData("t05ug01.vcf", 0, 2.017, -1.127, 8.414)]    // the Cobra
    [InlineData("t11ug01.vcf", 0, 2.055, -1.127, 8.406)]    // the Cobra with other guns
    public void AHelicoptersSphereIsItsModels(string vcf, double x, double y, double z, double r)
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var v = VehicleFactory.Create(vfs, vcf);
        Assert.Equal(9u, v.ObjectClass);
        var (c, radius) = v.ModelSphere!.Value;
        Assert.Equal(x, c.X, 0.01);
        Assert.Equal(y, c.Y, 0.01);
        Assert.Equal(z, c.Z, 0.01);
        Assert.Equal(r, radius, 0.01);
        // Combat's sphere is it, placed with the aircraft
        v.Pos = new Double3(100, 20, -50);
        var (wc, wr) = Combat.Collider(v);
        Assert.Equal(r, wr, 0.01);
        Assert.True(Vector3.Distance(new Vector3(100, 20, -50) + c, wc) < 1e-3f);
        Assert.True(Vector3.Distance(wc, Combat.ColliderCentre(v)) < 1e-4f);
    }

    [Fact]
    public void ACarKeepsTheBoxsSphere()
    {
        if (InstallDir is null) return;
        var v = VehicleFactory.Create(new AssetVfs(new GameInstall(InstallDir)), "vppa01.vcf");
        Assert.Null(v.ModelSphere);
    }

    /// <summary>
    /// The HE mortar's explosion (X1_GREN1, 95 over 25 m) set off on the ground beside T08's Huey 20 m up: measured in the
    /// original, it hits at 34.5 m from the collider's centre horizontally and misses at 35.2 (25 + 9.868).
    /// </summary>
    [Theory]
    [InlineData(34.5f, 1)]
    [InlineData(35.2f, 0)]
    public void TheMortarsSplashReachesTheHueyAsInTheOriginal(float distance, int packets)
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t08.msn")!), asm, script: false, rand: new CrtRand(1));
        var huey = setup.World.Vehicles.Single(v => string.Equals(v.Definition, "t08uc01.vcf", StringComparison.OrdinalIgnoreCase));
        var at = huey.Pos.ToVector3();
        float ground = setup.Ground.BareHeight(at.X, at.Z);
        huey.Pos = new Double3(at.X, ground + 20, at.Z);
        int hits = 0;
        setup.Damage.Damaging += (v, p) => { if (v == huey) { hits++; Assert.Equal(95, p.Ch[1]); } };
        var c = Combat.ColliderCentre(huey);
        var dir = Vector3.Normalize(new Vector3(1, 0, 2));
        var blast = new Vector3(c.X, ground, c.Z) + dir * distance;
        setup.Combat.Explode("x1_gren1", blast, null);
        Assert.Equal(packets, hits);
    }
}
