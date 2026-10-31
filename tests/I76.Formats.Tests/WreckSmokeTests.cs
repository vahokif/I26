using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

public class WreckSmokeTests(ITestOutputHelper log)
{
    private static (int Placements, Dictionary<string, int> Models, List<string> Bad, int Loose) Scan(AssetVfs vfs, GameInstall install)
    {
        var asm = new ModelAssembler(vfs);
        var models = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var verdict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        int placements = 0, loose = 0;
        foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(x => x.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(x)))
        {
            var w = new MissionWorld(vfs, asm, f);
            foreach (var p in w.Placements.Concat(w.LinePlacements))
            {
                if (p.Definition is not { } d || d.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)) continue;
                if (!verdict.TryGetValue(d, out var v))
                {
                    v = null;
                    if (asm.Assemble(d) is { } m)
                    {
                        bool any = m.Parts.SelectMany(pt => pt.Mesh.Faces).Any(WreckSmoke.IsSpriteFace);
                        // a sprite-textured face the filter would miss (a texture of the family without the sprite flag)
                        loose += m.Parts.SelectMany(pt => pt.Mesh.Faces).Count(fc => !WreckSmoke.IsSpriteFace(fc) && (fc.Texture.StartsWith("XSB1", StringComparison.OrdinalIgnoreCase) || fc.Texture.StartsWith("XFR1", StringComparison.OrdinalIgnoreCase)));
                        if (any)
                        {
                            v = "sprites";
                            if (WreckSmoke.Measure(m) is { } s)
                            {
                                // the source lies over the model's own (non-sprite) footprint, on its ground
                                var pts = m.Parts.SelectMany(pt => pt.Mesh.Faces.Where(fc => !WreckSmoke.IsSpriteFace(fc)).SelectMany(fc => fc.Vertices.Where(q => (uint)q.Index < pt.Mesh.Vertices.Length).Select(q => pt.ToModel.Apply(pt.Mesh.Vertices[q.Index])))).ToList();
                                bool inside = pts.Count > 0 && s.Base.X >= pts.Min(q => q.X) && s.Base.X <= pts.Max(q => q.X) && s.Base.Z >= pts.Min(q => q.Z) && s.Base.Z <= pts.Max(q => q.Z);
                                v = inside ? "ok" : $"smoke source ({s.Base.X:F1},{s.Base.Z:F1}) outside the footprint";
                            }
                        }
                    }
                    verdict[d] = v;
                }
                if (v is null) continue;
                placements++;
                models[d] = models.GetValueOrDefault(d) + 1;
                if (v != "ok" && v != "sprites") bad.Add($"{Path.GetFileName(f)} {d}: {v}");
            }
        }
        return (placements, models, bad, loose);
    }

    private void Report(string label, (int Placements, Dictionary<string, int> Models, List<string> Bad, int Loose) r)
    {
        log.WriteLine($"{label}: {r.Placements} placements of {r.Models.Count} models with sprite smoke/flame faces ({string.Join(", ", r.Models.Select(k => k.Key + " x" + k.Value))}); {r.Bad.Count} with the smoke source off the footprint; {r.Loose} xsb1/xfr1 faces without the sprite flag");
        Assert.Empty(r.Bad);
        Assert.Equal(0, r.Loose);
    }

    [InstallFact]
    public void BaseWreckSmokeSitsOnTheWreck() => Report("base", Scan(TestInstall.Vfs, TestInstall.Install));

    [NitroFact]
    public void NitroWreckSmokeSitsOnTheWreck()
    {
        var install = new GameInstall(NitroFactAttribute.Dir!);
        Report("nitro", Scan(new AssetVfs(install), install));
    }

    [InstallFact]
    public void WreckFlameIsTheSmokeSource()
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var m = WreckSmoke.Measure(asm.Assemble("akwreck2.sdf")!)!.Value;
        log.WriteLine($"akwreck2 base {m.Base} height {m.Height:F1} width {m.Width:F1}");
        Assert.InRange(MathF.Abs(m.Base.X), 0f, 0.5f);   // the smoke planes' own centre is 2.65 m off
        Assert.InRange(m.Height, 14f, 16f);
    }

    /// <summary>Every placed scenery object stands on the terrain, whatever Y its record carries (t15's poles are authored at 0 and 100).</summary>
    [InstallFact]
    public void PlacedObjectsStandOnTheTerrain()
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var path = Directory.EnumerateFiles(TestInstall.Install.MissionDir!).First(x => Path.GetFileName(x).Equals("t15.msn", StringComparison.OrdinalIgnoreCase));
        var w = new MissionWorld(TestInstall.Vfs, asm, path);
        var poles = w.Placements.Where(p => p.Definition?.StartsWith("atpole", StringComparison.OrdinalIgnoreCase) == true).ToList();
        Assert.Contains(poles, p => p.Object.Position.Y > 90);
        foreach (var p in poles)
            Assert.Equal((float)w.BilinearHeight(p.Object.Position.X, p.Object.Position.Z), w.GroundedPosition(p).Y);
    }
}
