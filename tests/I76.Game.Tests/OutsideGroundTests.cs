using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Formats.Vfs;
using I76.Game.World;
using I76.Sim.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The ground past a map's tiles (OutsideGround): on every stock map (and the Nitro Pack's) and on what the map editor makes it runs on from
/// the map's edge with no step the map does not have there itself and no kink where the map is smooth, and the modern look's chunks near
/// the camera stand exactly on the simulation's ground.
/// </summary>
public class OutsideGroundTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("i26-outside-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static MissionWorld Load(AssetVfs vfs, string path) => new(vfs, new ModelAssembler(vfs), path);

    private IEnumerable<(string Label, MissionWorld World)> Maps()
    {
        var vfs = TestInstall.Vfs;
        yield return ("new flat map, 4 tiles", Load(vfs, EditorMaps.FlatMap(_dir, "flat4", 4, 10)));
        yield return ("real-world map, rolling 40-340 m", Load(vfs, EditorMaps.RealWorldMap(_dir, "geohill", 6, 6, 40, 300)));
        yield return ("canyon strip, 100 m rim", Load(vfs, EditorMaps.CanyonMap(_dir, "canyon", 5)));
        foreach (var f in Directory.EnumerateFiles(TestInstall.Install.MissionDir!).Where(MapFileSet.IsStockMission).OrderBy(x => x))
            yield return (Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), Load(vfs, f));
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } nd && Directory.Exists(nd))
        {
            var ni = new GameInstall(nd);
            var nv = new AssetVfs(ni);
            foreach (var f in Directory.EnumerateFiles(ni.MissionDir!).Where(MapFileSet.IsStockMission).OrderBy(x => x))
                yield return ("nitro " + Path.GetFileName(f).ToLowerInvariant(), Load(nv, f));
        }
    }

    /// <summary>
    /// All round every map's edge, the first sample outside (V, beside one of the map's last samples, L) lies within the heights of the map's
    /// samples within a feather's width of it, give or take the edge's slope carried on: the ground outside never stands out from the map's
    /// own. Where the map is smooth (every sample within a feather's width of L within a metre of it) the slope across the edge (L to V) and
    /// the next (V to the second sample out) are the map's own slope there within 0.15: no crest or dip a car feels. Off long smooth
    /// stretches of edge (the map within 2 m for 375 m round) the first 300 m out are nowhere steeper than 1 in 1.
    /// </summary>
    [InstallFact]
    public void TheGroundRunsOnFromEveryMapsEdgeWithoutAStep()
    {
        var problems = new List<string>();
        int maps = 0;
        foreach (var (label, w) in Maps())
        {
            var o = w.Outside;
            if (o is null) { log.WriteLine($"{label}: none (a map too big for it)"); continue; }
            maps++;
            var t = w.Terrain;
            var (r0, r1, c0, c1) = t.Bounds();
            // the map's lowest and highest sample per block of 8, for the windows below
            int bx0 = c0 * 16, bz0 = r0 * 16, bw = (c1 - c0 + 1) * 16, bh = (r1 - r0 + 1) * 16;
            var bmin = new int[bw * bh]; var bmax = new int[bw * bh];
            Array.Fill(bmin, int.MaxValue); Array.Fill(bmax, int.MinValue);
            for (int sz = r0 * 128; sz < (r1 + 1) * 128; sz++)
                for (int sx = c0 * 128; sx < (c1 + 1) * 128; sx++)
                    if (t.Height(sx, sz) is { } q)
                    {
                        int k = (sz / 8 - bz0) * bw + sx / 8 - bx0;
                        bmin[k] = Math.Min(bmin[k], q); bmax[k] = Math.Max(bmax[k], q);
                    }
            (double Lo, double Hi) Span(int sx, int sz, int r)
            {
                int lo = int.MaxValue, hi = int.MinValue;
                for (int z = Math.Max((sz - r) / 8, bz0); z <= Math.Min((sz + r) / 8, bz0 + bh - 1); z++)
                    for (int x = Math.Max((sx - r) / 8, bx0); x <= Math.Min((sx + r) / 8, bx0 + bw - 1); x++)
                    {
                        int k = (z - bz0) * bw + x - bx0;
                        lo = Math.Min(lo, bmin[k]); hi = Math.Max(hi, bmax[k]);
                    }
                return (lo * 0.1, hi * 0.1);
            }
            int pairs = 0, smoothPairs = 0, longPairs = 0;
            double worstStep = 0, worstKink = 0, worstBand = 0;
            string at = "";
            double H(int x, int z) => (t.Height(x, z) ?? o.Raw(x, z)) * 0.1;
            int feather = (int)(OutsideGround.Feather / 5) + 2;
            for (int sz = r0 * 128; sz < (r1 + 1) * 128; sz++)
                for (int sx = c0 * 128; sx < (c1 + 1) * 128; sx++)
                {
                    if (t.Height(sx, sz) is not { } lr) continue;
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        if (t.Height(sx + dx, sz + dz) is not null || t.Height(sx - dx, sz - dz) is not { } l1r) continue;
                        pairs++;
                        double L = lr * 0.1, L1 = l1r * 0.1, V = H(sx + dx, sz + dz), V2 = H(sx + 2 * dx, sz + 2 * dz);
                        // V against the map's samples within a feather of it (block-wise, so a little wider)
                        var (lo, hi) = Span(sx + dx, sz + dz, feather);
                        double step = Math.Max(lo - V, V - hi) - OutsideGround.MaxSlope * OutsideGround.Ease / 2;
                        if (step > worstStep) { worstStep = step; at = $"at sample {sx + dx},{sz + dz}: {V:0.0} m beside {lo:0.0}..{hi:0.0}"; }
                        // smooth for the feather's width: every map sample within it within a metre of L
                        bool smooth = true;
                        for (int a = -feather; a <= feather && smooth; a++)
                            for (int b = -feather; b <= feather && smooth; b++)
                                if (t.Height(sx + a, sz + b) is { } q && Math.Abs(q * 0.1 - L) > 1) smooth = false;
                        if (!smooth) continue;
                        smoothPairs++;
                        double sIn = (L - L1) / 5, sEdge = (V - L) / 5, sOut = (V2 - V) / 5;
                        worstKink = Math.Max(worstKink, Math.Max(Math.Abs(sEdge - sIn), Math.Abs(sOut - sEdge)));
                        // the first 300 m out, off a long smooth stretch
                        if ((sx + sz) % 8 != 0) continue;
                        var (llo, lhi) = Span(sx, sz, 75);
                        if (lhi - llo > 4) continue;
                        longPairs++;
                        for (int k = 1; k < OutsideGround.Margin / 5; k++)
                            worstBand = Math.Max(worstBand, Math.Abs(H(sx + dx * (k + 1), sz + dz * (k + 1)) - H(sx + dx * k, sz + dz * k)) / 5);
                    }
                }
            log.WriteLine($"{label,-36} edge pairs {pairs,5} (smooth {smoothPairs,5}, long {longPairs,4}): past the map's own {worstStep:0.00} m, kink {worstKink:0.000}, slope in the first 300 m {worstBand:0.00}  {at}");
            Assert.True(pairs > 0, label);
            if (worstStep > 0.05) problems.Add($"{label}: stands out {at}");
            if (worstKink > 0.15) problems.Add($"{label}: the slope changes by {worstKink:0.00} across a smooth edge");
            if (worstBand > 1.0) problems.Add($"{label}: the first 300 m off a long smooth edge climb {worstBand:0.00} in 1");
        }
        Assert.True(maps > 40, $"only {maps} maps");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// The modern look's chunks of the outside ground (OutsideTerrain.ModernArrays) stand on the simulation's ground: every vertex, at every
    /// level of detail, is TerrainGround.Height at its point to the bit.
    /// </summary>
    [InstallTheory]
    [InlineData("t01")]
    [InlineData("m07")]
    public void NearChunksStandOnTheSimulationsGround(string mission)
    {
        var vfs = TestInstall.Vfs;
        var w = Load(vfs, TestInstall.Install.Resolve("miss16", mission + ".msn")!);
        var o = w.Outside!;
        var ground = new TerrainGround(w.Terrain, new Surface[8], o);
        var c = o.Centre;
        var (r0, _, c0, _) = w.Terrain.Bounds();
        // chunks round the map's south-west corner: beside the map, beyond it, and diagonally off its corner
        var origins = new[] { (c0 * 128 - 32, r0 * 128), (c0 * 128, r0 * 128 - 32), (c0 * 128 - 32, r0 * 128 - 32), (c0 * 128 - 96, r0 * 128 - 160) };
        int checkedVerts = 0;
        foreach (var (x0, z0) in origins)
            for (int level = 0; level < OutsideTerrain.Levels; level++)
            {
                var a = OutsideTerrain.ModernArrays(o, x0, z0, level, null);
                float step = (1 << level) / 4f;   // samples between vertices
                int n = (int)(OutsideTerrain.Chunk / step) + 1;
                for (int k = 0; k < n * n; k++)
                {
                    var v = a.V[k];
                    float gx = (x0 + k % n * step) * 5f, gz = (z0 + k / n * step) * 5f;
                    Assert.Equal(gx - c.X, v.X);
                    float sim = ground.Height(gx, gz);
                    Assert.True(sim == v.Y, $"{mission} chunk {x0},{z0} level {level}: vertex at {gx:0.00},{gz:0.00} is {v.Y} where the simulation has {sim}");
                    checkedVerts++;
                }
            }
        log.WriteLine($"{mission}: {checkedVerts} vertices on the simulation's ground");
    }
}
