using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The police tank (vstank2.vcf): its tracks are wheel slots, its cannon a turret (as on the CD).</summary>
public class TankTests
{
    [InstallFact]
    public void TheTracksAreTheRearWheelsAndDoNotRoll()
    {
        var m = new ModelAssembler(TestInstall.Vfs).Assemble("vstank2.vcf")!;
        // front: wctnk_1a's one black quad at the hub, a wheel the port may turn; rear: wetnk_1a's tracks
        foreach (int slot in new[] { 0, 1 })
            Assert.True(Wheels.RollsAboutHub(m.WheelGeos[slot].Intact, m.WheelGeos[slot].Node));
        foreach (int slot in new[] { 4, 5 })
        {
            var (geo, blown, node) = m.WheelGeos[slot];
            Assert.StartsWith("WE1", geo.Name);
            Assert.False(Wheels.RollsAboutHub(geo, node));
            Assert.False(Wheels.RollsAboutHub(blown!, node));
            // the track's middle lies 2.9 ahead of the WLOC at −2.88: it spans the hull, −3.46..3.51
            var zs = geo.Vertices.Select(v => node.Apply(v).Z + m.Locations.First(l => l.Tag == "WLOC" && l.Index == slot).Transform.Translation[2]).ToList();
            Assert.Equal(-3.46f, zs.Min(), 1);
            Assert.Equal(3.51f, zs.Max(), 1);
        }
        // and every car's tyres do
        var p = new ModelAssembler(TestInstall.Vfs).Assemble("vppirna1.vcf")!;
        Assert.All(p.WheelGeos.Values, w => Assert.True(Wheels.RollsAboutHub(w.Intact, w.Node)));
    }

    [InstallFact]
    public void TheTrackStripCyclesThreeFramesInEveryRow()
    {
        var tmt = new TmtMaterial(TestInstall.Vfs.Read("we11ls01.tmt"));
        Assert.Equal((2u, 3u, 12u), (tmt.Kind, tmt.CountA, tmt.CountB));
        Assert.False(tmt.Animated);   // stepped by the wheel's callback, not the engine's own texture timer
        for (int i = 0; i < 36; i++) Assert.Equal($"WE11LS0{i % 3 + 1}", tmt.Names[i]);
        var frames = new TextureResolver(TestInstall.Vfs).TmtFrames("WE11LS01.TMT")!;
        Assert.Equal(36, frames.Length);
    }

    [InstallFact]
    public void TheCannonIsATurretAsOnTheCd()
    {
        var vdf = new Bwd2File(TestInstall.Vfs.Read("vstank.vdf"));
        var hp = Hardpoint.Read(vdf).Single(h => h.Id == 1);
        Assert.Equal((1u, 1u), (hp.Facing, hp.Style));   // P: front
        var g = GizmoDef.Read("gtptank.gdf", TestInstall.Vfs.Read("gtptank.gdf"))!;
        Assert.Equal(("Police Tank Cann", 2u, 15u), (g.Name, g.Class, g.Tier));
        Assert.False(g.IsTurret);              // not a turret twin: the shop and the car checks see a cannon
        Assert.Equal(3u, g.StyleOn(hp));       // but it mounts as a turret, by the 1997 build's rule (tier ≥ 10)
        Assert.NotNull(g.TurretGun());         // on its T-style gun, which turns about its own pivot
    }

    /// <summary>
    /// The 1997 rule (tier ≥ 10) against the 1998 one (tier ≥ 100) over every .gdf of both games: only the two tank guns
    /// change, so no other gun of the GOG or Nitro data starts turning.
    /// </summary>
    [InstallFact]
    public void OnlyTheTankGunsLieBetweenTheTwoTurretRules()
    {
        var vfss = new List<I76.Formats.Vfs.AssetVfs> { TestInstall.Vfs };
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } nd && Directory.Exists(nd))
            vfss.Add(new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(nd)));
        var between = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vfs in vfss)
            foreach (var name in vfs.Names.Where(n => n.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase)))
                if (GizmoDef.Read(name, vfs.Read(name)) is { } g && g.MountsAsTurret && !g.IsTurret) between.Add(name.ToLowerInvariant());
        Assert.Equal(new[] { "gtktank.gdf", "gtptank.gdf" }, between);
    }
}
