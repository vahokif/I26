using I76.Formats.Bwd2;
using I76.Sim.Damage;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The Developer panel's loadout (DevTools.SwapWeapon, RefillAmmo) on a mission from the install (skipped without one).</summary>
public class DevLoadoutTests
{
    /// <summary>For each of the car's weapon mounts, the last weapon the garage lets onto it that is not the one there.</summary>
    private static List<(uint Hp, GizmoDef Def)> Swaps(SnapshotRig.Loaded l, Vehicle v)
    {
        var db = PartsDatabase.Build(l.Vfs);
        var vdf = new Bwd2File(l.Vfs.Read(TripCar.FromVcf(l.Vfs.Read(v.Definition!)).Vdf));
        var hps = Hardpoint.Read(vdf);
        var list = new List<(uint, GizmoDef)>();
        foreach (var w in v.Armory!.LoadOrder)
        {
            var slot = ChassisRules.WeaponSlot((int)w.Hardpoint.Id, hps.FirstOrDefault(h => h.Id == w.Hardpoint.Id));
            var pick = ChassisRules.WeaponsFor(db, slot, nitro: false, hidden: false)
                .LastOrDefault(p => !(p.Stem.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase) ? p.Stem : p.Stem + ".gdf").Equals(w.Def.File, StringComparison.OrdinalIgnoreCase));
            if (pick is null) continue;
            var file = pick.Stem.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase) ? pick.Stem : pick.Stem + ".gdf";
            list.Add((w.Hardpoint.Id, GizmoDef.Read(file, l.Vfs.Read(file))!));
        }
        return list;
    }

    /// <summary>
    /// Every mount of t01's car takes another weapon the garage allows there: the car keeps its make-up (as many weapons, the same
    /// damage slots, each pointing at its weapon), the new weapons are whole and loaded on their mounts, the selection is valid and
    /// the car's mass changes by the weights' difference. A snapshot after the swap restores in place and replays tick for tick; it
    /// is refused by a car built from the files (whose weapons differ).
    /// </summary>
    [Fact]
    public void ASwapKeepsTheCarValidAndItsSnapshotRoundTrips()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t01"), SimRate.R20, 3);
        for (int i = 0; i < 100; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); }
        var p = s.World.Player!;
        var a = p.Armory!;
        int count = a.LoadOrder.Count, sorted = a.Weapons.Count;
        var swaps = Swaps(l, p);
        Assert.NotEmpty(swaps);
        foreach (var (hp, def) in swaps)
        {
            float mass = p.P.Mass;
            var old = DevTools.SwapWeapon(s, p, hp, def);
            Assert.NotNull(old);
            var w = a.LoadOrder.Single(x => x.Hardpoint.Id == hp);
            Assert.Same(def, w.Def);
            Assert.Equal((int)def.HitPoints, w.Hp);
            Assert.True(w.Ammo >= (int)def.Ammo);
            Assert.Equal(mass + def.Mass - old!.Def.Mass, p.P.Mass, 3);
            Assert.Equal(1f / p.P.Mass, p.P.DriveScale, 6);
        }
        Assert.Equal(count, a.LoadOrder.Count);
        Assert.Equal(sorted, a.Weapons.Count);
        Assert.True(a.Selected >= 0 && a.Selected < a.Weapons.Count);
        Assert.All(a.Weapons, w => Assert.Contains(w, a.LoadOrder));
        for (int i = 0; i < count && i < 7; i++)
        {
            var part = Assert.IsType<WeaponPart>(p.Health!.Slots[10 + i]);
            Assert.Same(a.LoadOrder[i], part.Weapon);
            for (int r = 0; r < 7; r++)
                Assert.Equal((VehicleDamage.WeaponMask(part.Weapon) & 1 << r) != 0, p.Health.Rows[r].Contains(10 + i));
        }

        var snap = s.Snapshot();
        var dump = StateDump.Of(s);
        List<ulong> Go(int n)
        {
            var list = new List<ulong>();
            for (int i = 0; i < n; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); list.Add(s.StateHash()); }
            return list;
        }
        var first = Go(200);
        s.Restore(snap);
        SnapshotRig.TakeEvents(s);
        Assert.Null(StateDump.FirstDifference(dump, StateDump.Of(s)));
        Assert.Equal(first, Go(200));

        var (fresh, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t01"), SimRate.R20, 3);
        Assert.Throws<SnapshotMismatchException>(() => fresh.Restore(snap));
    }

    /// <summary>Refill: every weapon loaded and the specials' charges back, nothing repaired; a network session refuses it and the swap.</summary>
    [Fact]
    public void RefillLoadsWithoutRepairingAndANetworkSessionRefuses()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t01"), SimRate.R20, 3);
        var p = s.World.Player!;
        var w = p.Armory!.LoadOrder[0];
        int full = w.Ammo;
        w.Ammo = 1;
        w.Hp = 1;
        Assert.True(DevTools.RefillAmmo(s, p));
        Assert.Equal(full, w.Ammo);
        Assert.Equal(1, w.Hp);

        var (n, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t01"), SimRate.R20, 3);
        var np = n.World.Player!;
        var before = np.Armory!.LoadOrder[0].Def;
        var net = new SimSession(n.World, n.Combat, n.Script, SessionOptions.Network);
        Assert.False(DevTools.RefillAmmo(net, np));
        Assert.Null(DevTools.SwapWeapon(net, np, np.Armory.LoadOrder[0].Hardpoint.Id, Swaps(l, np)[0].Def));
        Assert.Same(before, np.Armory.LoadOrder[0].Def);
    }
}
