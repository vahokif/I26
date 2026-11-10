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
/// The ground past a map's tiles (OutsideGround): on every stock map (and the Nitro Pack's) and on what the map editor makes, near the map it
/// is the original's plain at 0, a high edge's last cell falling to it as in the original and a low edge carrying on at its own height,
/// past that the distant terrain rises from the plain gently, and the modern look's chunks near the camera stand exactly on the
/// simulation's ground.
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
        yield return ("plateau, 2 tiles at 150 m", Load(vfs, EditorMaps.FlatMap(_dir, "plateau", 2, 150)));
        yield return ("low flat map, 2 tiles at 1.5 m", Load(vfs, EditorMaps.FlatMap(_dir, "lowflat", 2, 1.5)));
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
    /// Round every map, out to the plain's end (<see cref="OutsideGround.Plain"/> from its bounds), every sample off the tiles is the plain's
    /// 0 or a low edge's carried height (0 to <see cref="OutsideGround.Carry"/>). Off a high edge (the map's samples within a feather of it
    /// all <see cref="OutsideGround.DropFrom"/> or more) the first two samples out are 0 and the simulation's ground in the cells there is the
    /// original's to the bit (height, normal and surface: TerrainGround with no outside ground, the port's own reading of the original),
    /// as it is in every cell whose samples off the tiles read 0. Off a low edge (the map's samples within a feather all
    /// <see cref="OutsideGround.Carry"/> or less, smooth: within a metre of each other) the ground carries on: the first sample out within
    /// 15 cm of the edge's (its neighbours' heights mix in, in tenths), and the slope across the edge and the next cell the map's own within
    /// 0.05 (a tenth's rounding over a sample is 0.02): no step, dip or ditch.
    /// </summary>
    [InstallFact]
    public void HighEdgesAreTheOriginalsDropAndLowEdgesCarryOn()
    {
        var problems = new List<string>();
        int maps = 0;
        foreach (var (label, w) in Maps())
        {
            var o = w.Outside;
            if (o is null) { log.WriteLine($"{label}: none (a map too big for it)"); continue; }
            maps++;
            var t = w.Terrain;
            var ours = new TerrainGround(t, new Surface[8], o);
            var orig = new TerrainGround(t, new Surface[8]);
            var (r0, r1, c0, c1) = t.Bounds();
            int pad = (int)(OutsideGround.Plain / 5);
            int x0 = c0 * 128 - pad, x1 = (c1 + 1) * 128 + pad, z0 = r0 * 128 - pad, z1 = (r1 + 1) * 128 + pad;
            int bad = 0, exact = 0, high = 0, low = 0, carried = 0;
            double worstStep = 0, worstKink = 0;
            string at = "";
            void Problem(string m) { if (bad++ < 6) problems.Add($"{label}: {m}"); }
            int Raw(int sx, int sz) => t.Sample(sx, sz) is { } v ? v & MissionFile.HeightMask : o.Raw(sx, sz);
            bool Original(int sx, int sz)   // the cell's samples off the tiles all read 0, and it is in the grid's range
            {
                if (sx < 0 || sz < 0 || sx >= 0x2800 || sz >= 0x2800) return false;
                for (int dz = 0; dz <= 1; dz++)
                    for (int dx = 0; dx <= 1; dx++)
                        if (t.Sample(sx + dx, sz + dz) is null && o.Raw(sx + dx, sz + dz) != 0) return false;
                return true;
            }
            void Check(double x, double z)
            {
                int sx = (int)Math.Floor(x / 5), sz = (int)Math.Floor(z / 5);
                if (!Original(sx, sz)) return;
                exact++;
                float h = ours.Height(x, z), oh = orig.Height(x, z);
                var n = ours.Normal(x, z); var on = orig.Normal(x, z);
                int s = ours.SurfaceId(x, z), os = orig.SurfaceId(x, z);
                if (h != oh || n != on || s != os) Problem($"at {x:0.00},{z:0.00} height {h} normal {n} surface {s}, the original's {oh} {on} {os}");
            }
            // the map's lowest and highest sample within r of a sample
            (int Lo, int Hi) Span(int sx, int sz, int r)
            {
                int lo = int.MaxValue, hi = int.MinValue;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                        if (t.Sample(sx + dx, sz + dz) is { } v) { lo = Math.Min(lo, v & MissionFile.HeightMask); hi = Math.Max(hi, v & MissionFile.HeightMask); }
                return (lo, hi);
            }
            int feather = (int)(OutsideGround.Feather / 5) + 1;
            for (int sz = z0; sz < z1; sz++)
                for (int sx = x0; sx < x1; sx++)
                {
                    double ex = Math.Max(Math.Max(c0 * 128 - sx, sx - (c1 + 1) * 128), 0), ez = Math.Max(Math.Max(r0 * 128 - sz, sz - (r1 + 1) * 128), 0);
                    if ((ex * ex + ez * ez) * 25 > OutsideGround.Plain * OutsideGround.Plain - 100) continue;
                    if (t.Sample(sx, sz) is null)
                    {
                        int raw = o.Raw(sx, sz);
                        if (raw < 0 || raw > OutsideGround.Carry * 10) Problem($"sample {sx},{sz} off the tiles reads {raw}");
                        if (raw != 0) carried++;
                        if (sx % 13 == 0 && sz % 13 == 0) Check(sx * 5.0 + 0.7, sz * 5.0 + 4.1);
                        continue;
                    }
                    int lr = t.Sample(sx, sz)!.Value & MissionFile.HeightMask;
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        if (t.Sample(sx + dx, sz + dz) is not null || t.Sample(sx - dx, sz - dz) is not { } l1v) continue;
                        var (lo, hi) = Span(sx + dx, sz + dz, feather);
                        int v1 = o.Raw(sx + dx, sz + dz), v2 = o.Raw(sx + 2 * dx, sz + 2 * dz);
                        if (lo >= OutsideGround.DropFrom * 10)
                        {
                            high++;
                            if (v1 != 0 || v2 != 0) Problem($"off a high edge ({lo * 0.1:0.0}..{hi * 0.1:0.0} m) at sample {sx},{sz}: {v1}, {v2}");
                            Check(sx * 5.0 + 2.5 * (dx + 1), sz * 5.0 + 2.5 * (dz + 1));
                            Check(sx * 5.0 + 1.3 + 2.5 * dx, sz * 5.0 + 3.9 + 2.5 * dz);
                        }
                        else if (hi <= OutsideGround.Carry * 10 && hi - lo <= 10)
                        {
                            low++;
                            double L = lr * 0.1, L1 = (l1v & MissionFile.HeightMask) * 0.1, V = v1 * 0.1, V2 = v2 * 0.1;
                            double step = Math.Abs(V - L);
                            if (step > worstStep) { worstStep = step; at = $"at sample {sx + dx},{sz + dz}: {V:0.00} m beside {L:0.00}"; }
                            worstKink = Math.Max(worstKink, Math.Max(Math.Abs((V - L) / 5 - (L - L1) / 5), Math.Abs((V2 - V) / 5 - (V - L) / 5)));
                        }
                    }
                }
            log.WriteLine($"{label,-36} high edge pairs {high,5}, low {low,5} (step {worstStep:0.000} m, kink {worstKink:0.000}), {carried,6} samples carried, {exact,6} points the original's  {at}");
            if (worstStep > 0.15) problems.Add($"{label}: a low edge steps {at}");
            if (worstKink > 0.05) problems.Add($"{label}: the slope changes by {worstKink:0.000} across a low edge");
        }
        Assert.True(maps > 40, $"only {maps} maps");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Past the plain the distant terrain rises from it gently and from nothing: along 256 bearings from each map's centre, the simulation's
    /// ground within 50 m past the plain's end is within half a metre of 0, and in the first <see cref="GentleReach"/> past it no 5 m step
    /// climbs or falls more than <see cref="GentleSlope"/>.
    /// </summary>
    [InstallFact]
    public void PastThePlainTheGroundRisesGently()
    {
        var problems = new List<string>();
        foreach (var (label, w) in Maps())
        {
            if (w.Outside is not { } o) continue;
            var g = new TerrainGround(w.Terrain, new Surface[8], o);
            var (r0, r1, c0, c1) = w.Terrain.Bounds();
            double bx0 = c0 * 640, bx1 = (c1 + 1) * 640, bz0 = r0 * 640, bz1 = (r1 + 1) * 640;
            double cx = (bx0 + bx1) / 2, cz = (bz0 + bz1) / 2;
            double worstStart = 0, worstSlope = 0;
            string at = "";
            for (int b = 0; b < 256; b++)
            {
                double ux = Math.Sin(b * Math.Tau / 256), uz = Math.Cos(b * Math.Tau / 256);
                double Dist(double s)
                {
                    double x = cx + ux * s, z = cz + uz * s;
                    double dx = Math.Max(Math.Max(bx0 - x, x - bx1), 0), dz = Math.Max(Math.Max(bz0 - z, z - bz1), 0);
                    return Math.Sqrt(dx * dx + dz * dz);
                }
                // where along the bearing the plain ends
                double lo = 0, hi = 200_000;
                for (int i = 0; i < 60; i++) { double m = (lo + hi) / 2; if (Dist(m) > OutsideGround.Plain) hi = m; else lo = m; }
                double prev = g.Height(cx + ux * hi, cz + uz * hi);
                for (double s = 5; s <= GentleReach; s += 5)
                {
                    double x = cx + ux * (hi + s), z = cz + uz * (hi + s);
                    double h = g.Height(x, z);
                    if (s <= 50 && Math.Abs(h) > worstStart) worstStart = Math.Abs(h);
                    double slope = Math.Abs(h - prev) / 5;
                    if (slope > worstSlope) { worstSlope = slope; at = $"{s:0} m past the plain at {x:0},{z:0}"; }
                    prev = h;
                }
            }
            log.WriteLine($"{label,-36} within 50 m of the plain {worstStart:0.00} m; steepest in the first {GentleReach:0} m {worstSlope:0.000} ({at})");
            if (worstStart > 0.5) problems.Add($"{label}: {worstStart:0.00} m off the plain's height just past it");
            if (worstSlope > GentleSlope) problems.Add($"{label}: {worstSlope:0.00} in 1 {at}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private const double GentleReach = OutsideGround.Gentle, GentleSlope = 0.2;

    /// <summary>
    /// The modern look's chunks of the outside ground (OutsideTerrain.ModernArrays) stand on the simulation's ground: every vertex, at every
    /// level of detail, is TerrainGround.Height at its point to the bit (on a 150 m plateau too, whose chunks beside it start at the foot of
    /// its last cell's drop).
    /// </summary>
    [InstallTheory]
    [InlineData("t01")]
    [InlineData("m07")]
    [InlineData("plateau")]
    public void NearChunksStandOnTheSimulationsGround(string mission)
    {
        var vfs = TestInstall.Vfs;
        var w = Load(vfs, mission == "plateau" ? EditorMaps.FlatMap(_dir, "plateau", 2, 150) : TestInstall.Install.Resolve("miss16", mission + ".msn")!);
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

    /// <summary>
    /// In the map editor's own view the 1997 look draws the outside ground as far as the modern look does, with no clip (the map's 1997
    /// terrain near the camera, the 10 m chunks beyond); a mission, and so the editor's playtest, keeps the original's 600 m clip. From
    /// the editor's high camera the chunks under it are the coarse ones in either look; a mission's levels go by the distance across the ground.
    /// </summary>
    [Fact]
    public void TheEditorsViewDrawsTheWholeOutsideGroundIn1997()
    {
        // a mission's 1997 look: within 600 m, cut at 600 m view depth, the map's own 1997 terrain
        Assert.Equal(600f, OutsideTerrain.Reach(modern: false, editorView: false));
        Assert.Equal(600f, OutsideTerrain.ClipDepth(editorView: false));
        Assert.Equal(-1, OutsideTerrain.Level(false, false, 550, int.MinValue));
        // the editor's 1997 view: as far as the modern look, uncut
        Assert.Equal(OutsideTerrain.ModernReach, OutsideTerrain.Reach(modern: false, editorView: true));
        Assert.Equal(OutsideTerrain.ModernReach, OutsideTerrain.Reach(modern: true, editorView: false));
        Assert.Equal(0f, OutsideTerrain.ClipDepth(editorView: true));
        Assert.Equal(-1, OutsideTerrain.Level(false, true, 300, int.MinValue));
        Assert.Equal(OutsideTerrain.Levels - 1, OutsideTerrain.Level(false, true, 900, int.MinValue));
        Assert.Equal(OutsideTerrain.Levels - 1, OutsideTerrain.Level(false, true, 595, OutsideTerrain.Levels - 1));   // hysteresis
        // 3000 m up over a chunk: coarse in the editor, by the distance across the ground in a mission
        Assert.Equal(OutsideTerrain.Levels - 1, OutsideTerrain.Level(true, true, OutsideTerrain.LevelDistance(true, 0, 3000), int.MinValue));
        Assert.Equal(OutsideTerrain.Levels - 1, OutsideTerrain.Level(false, true, OutsideTerrain.LevelDistance(true, 0, 3000), int.MinValue));
        Assert.Equal(0, OutsideTerrain.Level(true, false, OutsideTerrain.LevelDistance(false, 0, 3000), int.MinValue));
    }
}
