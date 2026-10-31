using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A mortar round's flight against the real game (measured in the original game: a detour on the original's code
/// in the original, a Piranha with the WP Mortar on hardpoint 1, fired in M04, the frame deltas it logged). The
/// original keeps a round's velocity in the frame it had before the pose is re-aimed along the displacement, so the
/// velocity turns with the pose on top of gravity: the flight is much shorter than the ballistic arc and depends on
/// the frame rate (GitHub issue 4).
/// </summary>
public class MortarRangeTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>vppirna1.vcf with the WP Mortar (gwhiteph) where the flame thrower was: the same bytes the original ran.</summary>
    private static byte[] WpPiranha(AssetVfs vfs)
    {
        var d = vfs.Read("vppirna1.vcf");
        var s = System.Text.Encoding.Latin1.GetString(d).Replace("gfmedium.gdf", "gwhiteph.gdf");
        return System.Text.Encoding.Latin1.GetBytes(s);
    }

    /// <summary>Fires the WP Mortar standing still on flat ground and flies it with the given frame deltas (in 1/2048 s).</summary>
    private static (float Horizontal, float Apex, float Flight, float Rise1) Fire(int[] ulps)
    {
        var vfs = new AssetVfs(new GameInstall(InstallDir!));
        AssetVfs.Addon["vppirwp.vcf"] = WpPiranha(vfs);
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var car = VehicleFactory.Create(vfs, "vppirwp.vcf");
        world.Vehicles.Add(car);
        world.Player = car;
        var combat = new Combat(world, _ => null);
        var a = car.Armory!;
        int wp = a.Weapons.FindIndex(w => w.Def.Name == "WP Mortar");
        var keys = new bool[5]; keys[wp] = true;
        a.PlayerTriggers(false, keys);
        Round? r = null; Vector3 first = default; float apex = 0, flight = 0, rise1 = 0;
        for (int f = 0; f < ulps.Length; f++)
        {
            combat.Frame(ulps[f] / 2048f);
            r ??= combat.Rounds.Single();
            if (f == 0) { first = r.Pos; rise1 = r.Pos.Y - 0.6379f; }
            apex = MathF.Max(apex, r.Pos.Y - first.Y);
            flight += ulps[f] / 2048f;
            if (combat.Events.OfType<Impact>().FirstOrDefault() is { } hit)
                return (MathF.Sqrt((hit.Hit.X - first.X) * (hit.Hit.X - first.X) + (hit.Hit.Z - first.Z) * (hit.Hit.Z - first.Z)), apex, flight, rise1);
            combat.Events.Clear();
            a.PlayerTriggers(false, new bool[5]);
        }
        throw new Exception("the round did not land");
    }

    // The original's numbers, measured from the end of the round's first step to the impact: horizontal distance and apex above that point.
    // (20 / 28 / 60 fps caps; the dt sequences are the ones the game logged, in 1/2048 s.)
    [Theory]
    [InlineData(new[] { 102, 103, 102, 103, 102, 102, 103, 102, 103, 102, 102 }, 37.058f, 2.783f)]
    [InlineData(new[] { 73, 72, 74, 74, 73, 72, 74, 73, 72, 74, 74, 73 }, 32.589f, 2.597f)]
    [InlineData(new[] { 35, 33, 35, 35, 32, 35, 35, 33, 35, 34, 33, 35, 35, 33, 34, 35, 35 }, 23.842f, 2.061f)]
    public void TheWpMortarLandsWhereTheOriginalDid(int[] ulps, float horizontal, float apex)
    {
        if (InstallDir is null) return;
        // the first frame of the original's list is the round's first step; a tick later the round has flown it
        var (h, ap, _, _) = Fire(ulps);
        Assert.InRange(h, horizontal - 0.4f, horizontal + 0.4f);
        Assert.InRange(ap, apex - 0.12f, apex + 0.12f);
    }

    /// <summary>The round goes about 40 m at 20 fps (the original's whole flight: 42 m from the muzzle in 0.45 s), not the 136 m of a plain ballistic arc.</summary>
    [Fact]
    public void TheWpMortarFliesAboutFortyMetresAtTwentyFps()
    {
        if (InstallDir is null) return;
        var (h, _, flight, _) = Fire(Enumerable.Repeat(102, 20).ToArray());
        Assert.InRange(h + 4.8f, 38f, 46f);
        Assert.InRange(flight, 0.4f, 0.55f);
    }
}
