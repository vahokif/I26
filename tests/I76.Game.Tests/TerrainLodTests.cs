using System.Text.RegularExpressions;
using Godot;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The modern terrain's chunks meet at every level of detail without a crack: a chunk edge between two levels is the same line
/// from both sides once each vertex has slid as terrain_modern.gdshader slides it (a crack shows the sky through a steep face as a thin
/// light streak), and the shading normal along it agrees.
/// </summary>
public class TerrainLodTests(ITestOutputHelper log)
{
    private const int C = TerrainNode.Chunk;

    [Fact]
    public void MorphRangesNestSoNoEdgeSlidesBeforeItsFinerNeighbourHasSettled()
    {
        var range = TerrainLodMesh.Range;
        Assert.Equal(TerrainLodMesh.Levels - 1, range.Length);
        for (int l = 0; l < range.Length; l++)
        {
            Assert.True(TerrainLodMesh.MorphStart[l] < TerrainLodMesh.MorphEnd(l), $"level {l} morphs over a positive stretch");
            if (l > 0) Assert.True(range[l] > range[l - 1]);
            // the coarser chunk of a level l / l+1 edge does not move until the edge is past anything the finer side could still be morphing
            if (l + 1 < range.Length)
                Assert.True(TerrainLodMesh.MorphStart[l + 1] >= range[l] + TerrainLodMesh.EdgeReach, $"level {l + 1} starts morphing at {TerrainLodMesh.MorphStart[l + 1]} m, inside level {l}'s edge reach");
        }
    }

    [Fact]
    public void TheShadersMorphRangesAreTheCodes()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", "terrain_modern.gdshaderinc"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "game", "Shaders", "terrain_modern.gdshaderinc"));
        float[] Read(string name)
        {
            var m = Regex.Match(src, @"const float " + name + @"\[\d+\] = \{([^}]*)\}");
            Assert.True(m.Success, name);
            return m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        Assert.Equal(TerrainLodMesh.MorphStart, Read("morph_start"));
        Assert.Equal(Enumerable.Range(0, TerrainLodMesh.Levels - 1).Select(TerrainLodMesh.MorphEnd).ToArray(), Read("morph_end"));
    }

    // the vertex shader's slide: a vertex of level lvl toward where the next level has it, by its distance from the camera
    private static float K(TerrainLodMesh m, int k, int x0, int z0, Vector3 eye, out int lvl)
    {
        lvl = (int)(m.Custom0[k * 4 + 3] + 0.5f) - 1;
        if (lvl < 0) return 0;
        var org = new Vector3(x0 * 5f, 0, -z0 * 5f);
        float d = (eye - (org + m.Vertices[k])).Length();
        return Math.Clamp((d - TerrainLodMesh.MorphStart[lvl]) / (TerrainLodMesh.MorphEnd(lvl) - TerrainLodMesh.MorphStart[lvl]), 0f, 1f);
    }

    private static Vector3 Pos(TerrainLodMesh m, int k, int x0, int z0, Vector3 eye)
    {
        float kk = K(m, k, x0, z0, eye, out _);
        var v = m.Vertices[k];
        var t = new Vector3(m.Custom0[k * 4], m.Custom0[k * 4 + 1], m.Custom0[k * 4 + 2]);
        return v + (t - v) * kk + new Vector3(x0 * 5f, 0, -z0 * 5f);
    }

    private static Vector3 Nrm(TerrainLodMesh m, int k, int x0, int z0, Vector3 eye)
    {
        float kk = K(m, k, x0, z0, eye, out _);
        var c1 = new Vector3(m.Custom1[k * 4], m.Custom1[k * 4 + 1], m.Custom1[k * 4 + 2]);
        return m.Normals[k].Lerp(c1, kk).Normalized();
    }

    /// <summary>Each chunk's level for a camera, as TerrainNode.ComputeWants: its distance to the chunk's box, neighbours at most one apart.</summary>
    private static Dictionary<(int, int), int> Levels(Dictionary<(int, int), (float Lo, float Hi)> chunks, Vector3 eye)
    {
        var want = new Dictionary<(int, int), int>();
        var keys = chunks.Keys.ToArray();
        float f = (float)TerrainSurface.MaxFree, size = C * TerrainNode.SampleSize;
        foreach (var (x0, z0) in keys)
        {
            var (lo, hi) = chunks[(x0, z0)];
            var bmin = new Vector3(x0 * 5f - f, lo, -z0 * 5f - size - f);
            var bmax = bmin + new Vector3(size + 2 * f, hi - lo, size + 2 * f);
            float d = (eye.Clamp(bmin, bmax) - eye).Length();
            int w = 0;
            while (w < TerrainLodMesh.Range.Length && d >= TerrainLodMesh.Range[w]) w++;
            want[(x0, z0)] = w;
        }
        for (int pass = 0; pass < TerrainLodMesh.Levels - 1; pass++)
            foreach (var k in keys)
                foreach (var (dx, dz) in new[] { (C, 0), (-C, 0), (0, C), (0, -C) })
                    if (want.TryGetValue((k.Item1 + dx, k.Item2 + dz), out var o) && want[k] > o + 1) want[k] = o + 1;
        return want;
    }

    [InstallTheory]
    [InlineData("s01")]
    [InlineData("t01")]
    public void ChunkEdgesMeetAtEveryLevelPair(string mission)
    {
        var vfs = TestInstall.Vfs;
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), TestInstall.Install.Resolve("miss16", mission + ".msn")!);
        var surf = new TerrainSurface(w.Terrain, TerrainSurface.Seeds(w));
        var (r0, r1, c0, c1) = w.Terrain.Bounds();
        var chunks = new Dictionary<(int, int), (float Lo, float Hi)>();
        for (int z0 = r0 * 128; z0 < (r1 + 1) * 128; z0 += C)
            for (int x0 = c0 * 128; x0 < (c1 + 1) * 128; x0 += C)
                chunks[(x0, z0)] = surf.HeightRange(x0, z0, C);
        var meshes = new Dictionary<(int, int, int), TerrainLodMesh>();
        TerrainLodMesh Mesh(int x, int z, int l)
        {
            if (!meshes.TryGetValue((x, z, l), out var m)) meshes[(x, z, l)] = m = TerrainLodMesh.Build(surf, x, z, l);
            return m;
        }
        // cameras over the map, and some in the ground's own height band, where the nearest chunks come at every level pair
        var rnd = new Random(1);
        var cams = new List<Vector3>();
        for (int i = 0; i < 14; i++)
        {
            int sx = rnd.Next(c0 * 128, (c1 + 1) * 128), sz = rnd.Next(r0 * 128, (r1 + 1) * 128);
            cams.Add(new Vector3(sx * 5f, (float)surf.Bilinear(sx, sz) + 3, -sz * 5f));
        }
        double worstGap = 0, worstAngle = 0;
        var pairsSeen = new HashSet<(int, int)>();
        foreach (var eye in cams)
        {
            var lv = Levels(chunks, eye);
            foreach (var k in chunks.Keys)
                foreach (var (dx, dz) in new[] { (C, 0), (0, C) })
                {
                    var o = (k.Item1 + dx, k.Item2 + dz);
                    if (!lv.TryGetValue(o, out int lb)) continue;
                    int la = lv[k];
                    pairsSeen.Add((Math.Min(la, lb), Math.Max(la, lb)));
                    var ma = Mesh(k.Item1, k.Item2, la); var mb = Mesh(o.Item1, o.Item2, lb);
                    int na = TerrainLodMesh.Quads(la) + 1, nb = TerrainLodMesh.Quads(lb) + 1;
                    // A's far column (east) or far row (south) is B's near one
                    int KA(int t) => dx > 0 ? t * na + na - 1 : (na - 1) * na + t;
                    int KB(int t) => dx > 0 ? t * nb : t;
                    bool aFiner = na >= nb;
                    var (fin, coa, nf, nc) = aFiner ? (ma, mb, na, nb) : (mb, ma, nb, na);
                    var (kf, kc) = aFiner ? ((Func<int, int>)KA, (Func<int, int>)KB) : (KB, KA);
                    var (fx, fz, cx, cz) = aFiner ? (k.Item1, k.Item2, o.Item1, o.Item2) : (o.Item1, o.Item2, k.Item1, k.Item2);
                    for (int t = 0; t < nf; t++)
                    {
                        double u = (double)t / (nf - 1) * (nc - 1);
                        int i0 = Math.Min((int)u, nc - 2);
                        float fr = (float)(u - i0);
                        var pf = Pos(fin, kf(t), fx, fz, eye);
                        var pc = Pos(coa, kc(i0), cx, cz, eye).Lerp(Pos(coa, kc(i0 + 1), cx, cz, eye), fr);
                        worstGap = Math.Max(worstGap, (pf - pc).Length());
                        var nF = Nrm(fin, kf(t), fx, fz, eye);
                        var nC = Nrm(coa, kc(i0), cx, cz, eye).Lerp(Nrm(coa, kc(i0 + 1), cx, cz, eye), fr).Normalized();
                        worstAngle = Math.Max(worstAngle, Math.Acos(Math.Clamp(nF.Dot(nC), -1f, 1f)) * 180 / Math.PI);
                    }
                }
        }
        log.WriteLine($"{mission}: level pairs {string.Join(" ", pairsSeen.OrderBy(p => p).Select(p => $"{p.Item1}/{p.Item2}"))}; worst gap {worstGap:0.0000} m, worst normal {worstAngle:0.00} deg");
        Assert.True(pairsSeen.Contains((0, 1)) && pairsSeen.Contains((1, 2)), "the cameras should meet the finest level pairs");
        Assert.True(worstGap < 0.01, $"a chunk edge opens {worstGap:0.000} m");
        Assert.True(worstAngle < 1.0, $"a chunk edge's shading normal differs by {worstAngle:0.0} deg");
    }
}
