using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A turret's muzzle object hangs under its gun node (GGEO), and the gun turns about its own origin
/// (the yaw node under the base, at the gun's x and z, turns it): so the rounds, and the flash,
/// leave from the barrel's tip wherever the turret points. They used to leave from where the tip is at rest, ahead of
/// the turret, whichever way it pointed (GitHub #41: the Police Car's turret flash out of line).
/// </summary>
public class TurretMuzzleTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static string? Dir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;

    private static string? Nitro => Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;

    /// <summary>
    /// Every car with a turret in both games, the hidden ones too (the Howitzer, the gunner MGs, the helicopters' side
    /// turrets): its muzzle sits at the barrel's tip (GPOF block 2 lies along the gun's model), and swings with the gun.
    /// </summary>
    [Fact]
    public void EveryTurretsMuzzleIsAtItsBarrelsTipWhereverItPoints()
    {
        int n = 0;
        foreach (var dir in new[] { Dir, Nitro }.OfType<string>())
        {
            var vfs = new AssetVfs(new GameInstall(dir));
            foreach (var car in vfs.Names.Where(x => x.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) && !AssetVfs.Addon.ContainsKey(x)).Order().ToList())
            {
                Vehicle v;
                try { v = VehicleFactory.Create(vfs, car); } catch (Exception) { continue; }
                if (v.Armory is null || !v.Armory.LoadOrder.Any(w => w.Style == 3)) continue;
                CheckMuzzle(vfs, car);
                n++;
            }
        }
        if (Dir is not null) Assert.True(n > 40, $"{n} cars");
    }

    /// <summary>
    /// The tank cannons (tier 15) are turrets, as on the 1997 CD (its mount-style rule is tier ≥ 10; the 1998
    /// build's tier &lt; 100 left them fixed). Their muzzle sits on the T-style gun and swings with the aim.
    /// </summary>
    [Fact]
    public void TheTankCannonsAreTurrets()
    {
        if (Dir is null) return;
        var vfs = new AssetVfs(new GameInstall(Dir));
        foreach (var car in new[] { "vstank1.vcf", "vstank2.vcf", "vstankf.vcf", "t16st01.vcf" })
        {
            var w = VehicleFactory.Create(vfs, car).Armory!.LoadOrder.Single(w => w.Def.File.Contains("tank", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3u, w.Style);
            Assert.NotNull(w.TurretPivot);
            CheckMuzzle(vfs, car);
        }
    }

    /// <summary>The police tank's cannon slews onto a target at its right as any turret does (the original's 1998 rate).</summary>
    [Fact]
    public void ThePoliceTanksCannonTurnsOntoItsTarget()
    {
        if (Dir is null) return;
        var vfs = new AssetVfs(new GameInstall(Dir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var tank = VehicleFactory.Create(vfs, "vstank2.vcf");
        tank.Flags = VehicleFlags.EngineOn;
        var target = new Vehicle { Pos = new Double3(60, 0, 0), Flags = VehicleFlags.EngineOn };   // 90° to its right
        world.Vehicles.Add(tank); world.Vehicles.Add(target);
        world.Player = tank;
        var c = new Combat(world, _ => null);
        c.RadarTarget[tank] = target;
        var w = tank.Armory!.LoadOrder.Single(w => w.Def.File == "gtptank.gdf");
        const float dt = 0.036f;
        c.Frame(dt);
        Assert.Equal(1.7f * dt, MathF.Abs(w.AimYaw), 4);   // full health: h = 0.75 + 0.25·1
        for (int f = 0; f < 60; f++) c.Frame(dt);
        Assert.Equal(MathF.PI / 2, MathF.Abs(w.AimYaw), 1);
        Assert.True(w.OnTarget);
    }

    [Theory]
    [InlineData("vcpolic1.vcf")]   // the Police Car, 25mm turret
    [InlineData("vfcoupe2.vcf")]   // the 7.62 turret
    [InlineData("vjsovrn3.vcf")]   // a radar turret
    [InlineData("vuchuey3.vcf")]   // two turrets
    [InlineData("vxturr1.vcf")]    // a pole turret
    public void TheMuzzleSwingsWithTheGun(string car)
    {
        if (Dir is null) return;
        CheckMuzzle(new AssetVfs(new GameInstall(Dir)), car);
    }

    private static void CheckMuzzle(AssetVfs vfs, string car)
    {
        var v = VehicleFactory.Create(vfs, car);
        var turrets = v.Armory!.LoadOrder.Where(w => w.Style == 3).ToList();
        Assert.NotEmpty(turrets);
        foreach (var w in turrets)
        {
            var hp = Affine.From(w.Hardpoint.Transform);
            var rest = hp.Compose(Affine.From(w.Def.GunPoint(3)!)).T;
            var pivot = hp.Apply(w.Def.TurretGun()!.Value);
            Assert.Equal(pivot, w.TurretPivot);
            w.AimYaw = 0; w.AimPitch = 0;
            Assert.True(Vector3.Distance(rest, w.MuzzlePos) < 1e-3f, $"{car}: rest {rest} vs {w.MuzzlePos}");
            // in the mount's frame (a helicopter's side turrets hang on their sides, and turn about the mount's up)
            var arm = w.BaseRot.ToLocal(rest - pivot);
            Assert.True(arm.Z > 0.3f, $"{car}: the gun point lies ahead of the gun ({arm})");
            // and at the front of the gun's model (the gun node is unrotated in every turret gizmo)
            var gn = w.Def.Geometry.First(x => x.Name.Length >= 8 && char.ToUpperInvariant(x.Name[3]) == 'T' && x.Name[4] == '1' && x.Name[5] == '1'
                && x.Name[6..8].Equals("GN", StringComparison.OrdinalIgnoreCase));
            var g = I76.Formats.Geo.GeoMesh.Parse(vfs.Read(gn.Name + ".geo"));
            float zmax = g.Vertices.Max(p => p.Z), zmin = g.Vertices.Min(p => p.Z);
            var front = g.Vertices.Where(p => p.Z > zmax - 0.15f * (zmax - zmin)).ToList();
            var tip = new Vector3(front.Average(p => p.X), front.Average(p => p.Y), zmax);
            Assert.True(MathF.Abs(arm.Z - tip.Z) < 0.06f && new Vector2(arm.X - tip.X, arm.Y - tip.Y).Length() < 0.15f,
                $"{car} {w.Def.File}: gun point {arm}, the gun's front {tip}");
            foreach (float yaw in new[] { MathF.PI / 2, -MathF.PI / 2, MathF.PI })
            {
                w.AimYaw = yaw;
                var d = w.BaseRot.ToLocal(w.MuzzlePos - pivot);
                // a turn about the mount's up through the gun's origin: the same height and reach, and the tip where the barrel points
                Assert.True(MathF.Abs(arm.Y - d.Y) < 0.002f, $"{car} yaw {yaw}: height {d.Y} against {arm.Y}");
                Assert.True(MathF.Abs(new Vector2(arm.X, arm.Z).Length() - new Vector2(d.X, d.Z).Length()) < 0.002f, $"{car} yaw {yaw}: reach {d} against {arm}");
                var f = w.BaseRot.ToLocal(w.MuzzleRot.Forward);
                Assert.True(Vector2.Dot(Vector2.Normalize(new Vector2(d.X, d.Z)), Vector2.Normalize(new Vector2(f.X, f.Z))) > 0.99f, $"{car} yaw {yaw}: tip {d}, barrel {f}");
            }
            // pitched up, the tip rises about the gun's own origin
            w.AimYaw = 0; w.AimPitch = 0.3f;
            Assert.True(w.BaseRot.ToLocal(w.MuzzlePos - pivot).Y > arm.Y + 0.25f * arm.Z, $"{car}: pitched tip {w.MuzzlePos - pivot}");
            w.AimPitch = 0;
        }
    }
}
