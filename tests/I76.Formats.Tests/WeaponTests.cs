using I76.Formats.Bwd2;
using Xunit;

namespace I76.Formats.Tests;

public class WeaponTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static GizmoDef Gdf(string n) => GizmoDef.Read(n, TestInstall.Vfs.Read(n))!;

    [InstallFact]
    public void GizmoFieldsMatchTheW1Table()
    {
        var mg = Gdf("gmlight.gdf");
        Assert.Equal("30cal MG", mg.Name);
        Assert.Equal((2u, 15u, 200u, 2000u, 1u), (mg.Class, mg.Damage, mg.HitPoints, mg.Ammo, mg.Kind));
        Assert.Equal(10f, mg.Rate);
        Assert.Equal(150f, mg.Speed);
        Assert.Equal(3.33f, mg.Life, 2);
        Assert.Equal(32f, mg.Mass);
        Assert.Equal((150f, 0f, 1u), (mg.Ordnance!.TerminalSpeed, mg.Ordnance.Gravity, mg.Ordnance.Mask));
        Assert.All(mg.GunPoints, g => Assert.NotNull(g));
        Assert.Contains(mg.Geometry, n => n.Name == "GMLP11GN");
        int n = 0;
        foreach (var name in TestInstall.Vfs.Names.Where(x => x.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var g = Gdf(name);
            Assert.NotNull(g.Ordnance);
            Assert.Equal(g.Kind, g.Ordnance!.Kind);
            log.WriteLine($"{name,-14} {g.Name,-18} c{g.Class} t{g.Tier} dmg {g.Damage} hp {g.HitPoints} rate {g.Rate} burst {g.Burst} v {g.Speed} vT {g.Ordnance.TerminalSpeed} g {g.Ordnance.Gravity} life {g.Life:0.##} kind {g.Kind} mask {g.Ordnance.Mask} {g.FireSound} {string.Join('/', g.Ordnance.ImpactXdf)}");
            n++;
        }
        Assert.Equal(43, n);
    }

    [InstallFact]
    public void EffectsCarryTheirSplash()
    {
        var x = EffectDef.Read("xgren1.xdf", TestInstall.Vfs.Read("xgren1.xdf"))!;
        Assert.Equal((95f, 25f), (x.SplashDamage, x.SplashRadius));
    }

    [InstallFact]
    public void EveryWeaponSlotHasItsHardpoint()
    {
        int mounts = 0, missing = 0;
        foreach (var name in TestInstall.Vfs.Names.Where(x => x.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)))
        {
            var vcf = new Bwd2File(TestInstall.Vfs.Read(name));
            if (vcf.VcfcFields() is not { } f || TestInstall.Vfs.TryRead(f.Vdf) is not { } vd) continue;
            var hps = Hardpoint.Read(new Bwd2File(vd));
            foreach (var m in vcf.WeaponMounts().Where(m => !m.Empty))
            {
                mounts++;
                if (!hps.Any(h => h.Id == m.Slot)) { missing++; log.WriteLine($"{name}: slot {m.Slot} ({m.Gizmo}) has no HLOC"); }
            }
        }
        log.WriteLine($"{mounts} mounts, {missing} without a hardpoint");
        Assert.True(mounts > 800);
        Assert.True(missing * 50 < mounts);
    }
}

public class DamagedModelTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [InstallFact]
    public void TheWorstConditionKeepsEveryPart()
    {
        var intact = new I76.Formats.Assembly.ModelAssembler(TestInstall.Vfs).Assemble("vppirna1.vcf")!;
        var wrecked = new I76.Formats.Assembly.ModelAssembler(TestInstall.Vfs) { PartCondition = _ => '9' }.Assemble("vppirna1.vcf")!;
        log.WriteLine(string.Join(" ", wrecked.Parts.Select(p => p.NodeName)));
        Assert.Equal(intact.Parts.Count(p => !p.NodeName.StartsWith("G")), wrecked.Parts.Count(p => !p.NodeName.StartsWith("G")));
        Assert.Contains(wrecked.Parts, p => p.NodeName == "PP13BDYF");
        Assert.DoesNotContain(wrecked.Parts, p => p.NodeName == "PP11BDYF");
    }

    /// <summary>
    /// The dead car (state 4 on BDYF, BDYM, BDYB, BDYT): each zone's subtree takes its VGEO
    /// state-3 slots, which on the Piranha hold only the burnt hull; the lamps outside the zones stay, to be put out.
    /// </summary>
    [InstallFact]
    public void TheWreckIsTheBurntHullAlone()
    {
        var states = new I76.Formats.Bwd2.Bwd2File(TestInstall.Vfs.Read("vppirnha.vdf")).VgeoStates()!;
        Assert.Equal(35, states.PerGroup);
        Assert.Equal("PP14BDYM", states.At('1', 3, 0)?.Node.Name);
        Assert.Equal(0x42, states.At('1', 0, 0)?.Type);
        Assert.Equal("PP13BDYF", states.At('1', 2, 1)?.Node.Name);
        Assert.Null(states.At('1', 3, 1));
        var wrecked = new I76.Formats.Assembly.ModelAssembler(TestInstall.Vfs) { SkipParts = [], Wrecked = true }.Assemble("vppirna1.vcf")!;
        var body = wrecked.Parts.Where(p => !wrecked.GizmoOf.ContainsKey(p)).Select(p => p.NodeName).Order(StringComparer.Ordinal).ToList();
        log.WriteLine(string.Join(" ", body));
        Assert.Equal(["PP11BLGT", "PP11HLGT", "PP11TLGT", "PP14BDYM"], body);
        Assert.NotEmpty(wrecked.GizmoOf);
    }

    /// <summary>Every shipped car's wreck: its state-3 slots are zone nodes (in the BDYM slot on all but the bus), and assembling it keeps them.</summary>
    [InstallFact]
    public void EveryWreckKeepsItsStateThreeNodes()
    {
        int cars = 0;
        foreach (var name in TestInstall.Vfs.Names.Where(n => n.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var states = new I76.Formats.Bwd2.Bwd2File(TestInstall.Vfs.Read(name)).VgeoStates();
            Assert.NotNull(states);
            var zone = states.ZoneSlots('1').ToHashSet();
            for (int k = 0; k < states.PerGroup; k++)
                if (states.At('1', 3, k) is { } w) { Assert.Contains(k, zone); cars++; }
            var wreck = new I76.Formats.Assembly.ModelAssembler(TestInstall.Vfs) { SkipParts = [], Wrecked = true }.Assemble(name)!;
            foreach (int k in zone)
                if (states.At('1', 3, k) is { } w) Assert.Contains(wreck.Parts, p => p.NodeName.Equals(w.Node.Name, StringComparison.OrdinalIgnoreCase));
                else if (states.At('1', 0, k) is { } alive) Assert.DoesNotContain(wreck.Parts, p => p.NodeName.Equals(alive.Node.Name, StringComparison.OrdinalIgnoreCase));
        }
        log.WriteLine($"{cars} wreck nodes");
        Assert.True(cars > 40);
    }
}

public class TurretModelTests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>
    /// Only a turret's gun turns, about its own origin (the original slips the yaw node under the base at the gun's x and
    /// z): the base (TU) is a fixed part of the gizmo, drawn on the mount. Testers saw the whole turret, base and all,
    /// swing round with the target, and its base wander about the roof.
    /// </summary>
    [InstallFact]
    public void OnlyTheTurretsGunTurnsAboutItsOwnOrigin()
    {
        var asm = new I76.Formats.Assembly.ModelAssembler(TestInstall.Vfs);
        int cars = 0;
        // the install's cars only: other tests add scratch cars to the VFS for a while
        foreach (var name in TestInstall.Vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) && !I76.Formats.Vfs.AssetVfs.Addon.ContainsKey(n)).Order().ToList())
        {
            if (TestInstall.Vfs.TryRead(name) is not { } data) continue;
            var vcf = new I76.Formats.Bwd2.Bwd2File(data);
            if (!vcf.WeaponMounts().Any(w => w.Gizmo.StartsWith("t", StringComparison.OrdinalIgnoreCase) && !w.Empty)) continue;
            var m = asm.Assemble(name);
            if (m is null) continue;
            for (int w = 0; w < m.Gizmos.Count; w++)
            {
                var (_, g, hp) = m.Gizmos[w];
                if (!g.IsTurret || g.TurretGun() is not { } gn) continue;
                cars++;
                var t = Assert.Single(m.Turrets, t => t.Weapon == w);
                Assert.EndsWith("GN", t.Node, StringComparison.OrdinalIgnoreCase);
                // the gun's pivot is its own origin on the mount, not the base's
                var pivot = I76.Formats.Assembly.Affine.From(hp.Transform).Apply(gn);
                Assert.True(System.Numerics.Vector3.Distance(pivot, t.Pivot) < 1e-4f, $"{name}: pivot {t.Pivot}, gun at {pivot}");
                // the base is drawn with the fixed parts
                Assert.Contains(m.Parts, p => m.GizmoOf.TryGetValue(p, out int pw) && pw == w && p.NodeName.EndsWith("TU", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(m.Turrets, x => x.Node.EndsWith("TU", StringComparison.OrdinalIgnoreCase));
                if (cars <= 3) log.WriteLine($"{name}: {t.Node} @{t.Pivot}");
            }
        }
        log.WriteLine($"{cars} turrets");
        Assert.True(cars > 5);
    }

    /// <summary>
    /// The sim turns a turret's muzzle by its aim in the mount's frame (Weapon.TurretRot), where the engine turns it under
    /// the gun node and then applies the GPOF rotation; the two agree because every turret gizmo's base, gun and T-style
    /// gun point are unrotated. Every .gdf of both games.
    /// </summary>
    [InstallFact]
    public void TurretGizmosAreUnrotated()
    {
        int n = 0;
        foreach (var vfs in new[] { TestInstall.Vfs }.Concat(NitroVfs() is { } nv ? [nv] : []))
            foreach (var name in vfs.Names.Where(x => x.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var g = GizmoDef.Read(name, vfs.Read(name))!;
                if (!g.MountsAsTurret) continue;
                n++;
                static bool Identity(float[] r) => r.Select((x, i) => System.MathF.Abs(x - (i % 4 == 0 ? 1 : 0))).Max() < 1e-3f;
                Assert.True(g.GunPoint(3) is null || Identity(g.GunPoint(3)!.Rotation), $"{name}: GPOF[2] rotated");
                foreach (var node in g.Geometry.Where(x => x.Name.Length >= 8 && char.ToUpperInvariant(x.Name[3]) == 'T'))
                    Assert.True(Identity(node.Transform.Rotation), $"{name}: {node.Name} rotated");
            }
        log.WriteLine($"{n} turret gizmos");
        Assert.True(n >= 15);
    }

    private static I76.Formats.Vfs.AssetVfs? NitroVfs() =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? new(new I76.Formats.Vfs.GameInstall(d)) : null;
}

public class AllSoundsTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [InstallFact]
    public void VoiceLimitsComeFromTheGas0Header()
    {
        Assert.Equal((80, 2), I76.Formats.Audio.SoundFiles.Limits(TestInstall.Vfs, "vexplode.wav"));
        Assert.Equal((70, 3), I76.Formats.Audio.SoundFiles.Limits(TestInstall.Vfs, "wrock.wav"));
        Assert.Equal((50, 1), I76.Formats.Audio.SoundFiles.Limits(TestInstall.Vfs, "nosuchsound.wav"));
    }

    [InstallFact]
    public void EverySoundParsesOrIsRejectedCleanly()
    {
        int ok = 0, none = 0;
        foreach (var n in TestInstall.Vfs.Names.Where(n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gpw", StringComparison.OrdinalIgnoreCase)))
        {
            var p = I76.Formats.Audio.SoundFiles.Read(TestInstall.Vfs, n);
            if (p is null) { none++; log.WriteLine($"no PCM: {n}"); } else ok++;
        }
        log.WriteLine($"{ok} parsed, {none} without data");
        Assert.True(ok > 500);
    }
}
