using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

public class GroundLevelTests(ITestOutputHelper log)
{
    private static (int Models, int Changed, int Before, List<string> Bad, List<string> Changes) Scan(AssetVfs vfs, GameInstall install)
    {
        var asm = new ModelAssembler(vfs);
        var seen = new Dictionary<string, AssembledModel?>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>(); var changes = new List<string>();
        int changed = 0, before = 0;
        foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(x => x.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(x)))
        {
            var w = new MissionWorld(vfs, asm, f);
            foreach (var p in w.Placements.Concat(w.LinePlacements))
            {
                if (p.Definition is not { } d || d.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) || (p.Object.ObjectType & 0xf0) == 0x50) continue;
                if (p.Object.ObjectType is 0xb or 0xc or 0xd) continue;
                if (seen.ContainsKey(d)) continue;
                seen[d] = asm.Assemble(d);
                if (seen[d] is not { } m) continue;
                var (low, slab) = GroundLevel.Measure(m);
                float now = GroundLevel.Of(m);
                if (!float.IsInfinity(slab) && slab - low > GroundLevel.MaxAboveLowest) before++;   // fc85bef raised these to a roof or deck
                else if (!float.IsInfinity(slab) && slab - low > 1e-4f) { changed++; changes.Add($"{d} {low:F2}->{now:F2}"); }
                if (now - low > GroundLevel.MaxAboveLowest) bad.Add($"{d}: ground {now:F2}, lowest vertex {low:F2}");
            }
        }
        return (seen.Count, changed, before, bad, changes);
    }

    private void Report(string label, (int Models, int Changed, int Before, List<string> Bad, List<string> Changes) r)
    {
        log.WriteLine($"{label}: {r.Models} grounded models; {r.Before} have a slab more than 0.5 m above the lowest vertex (fc85bef put their ground level there; now the lowest vertex), {r.Changed} differ from the lowest-vertex rule by a slab within 0.5 m");
        foreach (var c in r.Changes) log.WriteLine("  " + c);
        Assert.Empty(r.Bad);
    }

    [InstallFact]
    public void BaseGroundLevelStaysNearTheLowestVertex() => Report("base", Scan(TestInstall.Vfs, TestInstall.Install));

    [NitroFact]
    public void NitroGroundLevelStaysNearTheLowestVertex()
    {
        var install = new GameInstall(NitroFactAttribute.Dir!);
        Report("nitro", Scan(new AssetVfs(install), install));
    }

    /// <summary>p11's gas station has a footing 0.13 under its slab; the slab must still be the ground.</summary>
    [InstallFact]
    public void GasStationLotIsTheGround()
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var m = asm.Assemble(asm.ResolveDefinition("bfsince1")!)!;
        var (low, slab) = GroundLevel.Measure(m);
        log.WriteLine($"bfsince1 lowest {low:F3} slab {slab:F3}");
        Assert.True(slab > low + 0.05f);
        Assert.Equal(slab, GroundLevel.Of(m));
    }
}
