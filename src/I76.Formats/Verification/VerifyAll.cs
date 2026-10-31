using System.Globalization;
using I76.Formats.Bwd2;
using I76.Formats.Fsm;
using I76.Formats.Geo;
using I76.Formats.Imaging;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Formats.Verification;

public sealed record CheckResult(string Section, string Name, bool Ok, string Detail);

/// <summary>
/// The structural claims of the format docs (docs/formats), re-derived from the
/// shipped files: a port of the reference verifier, check for check, with the
/// same names and detail strings so the two outputs can be diffed.
/// </summary>
public static class VerifyAll
{
    private static CheckResult C(string section, string name, bool ok, string detail = "") => new(section, name, ok, detail);

    public static List<CheckResult> Archive(ZfsArchive ar)
    {
        const string s = "I76.ZFS container";
        var o = new List<CheckResult>
        {
            C(s, "header constants (ZFSF, 1, 16, 100)", (ar.Version, ar.Const16, ar.PerTable) == (1, 16, 100)),
            C(s, "directory chain length matches count/100+1", ar.DTables == ar.Count / 100 + 1, $"{ar.DTables} tables"),
        };
        int bad = ar.Entries.AsParallel().Count(e => { try { return ar.Read(e).Length != e.Size; } catch { return true; } });
        o.Add(C(s, "every record decompresses to its declared size", bad == 0, $"{ar.Entries.Count} records"));
        return o;
    }

    public static CheckResult PakCoverage(PakSet ps)
    {
        long covered = 0, total = 0;
        int bad = 0;
        foreach (var stem in ps.Stems)
        {
            var members = ps.Members(stem);
            var pk = ps.PakEntry(stem);
            if (pk is null) { bad++; continue; }
            total += pk.Size;
            covered += members.Count > 0 ? members.Max(m => (long)m.Offset + m.Size) : 0;
        }
        return C(".pix / .pak nested container", "pix member extents cover 100% of pak payload",
            bad == 0 && covered == total, $"{ps.Stems.Count} pairs");
    }

    public static readonly string[] AssetExts = ["geo", "m16", "map", "tmt", "vqm"];

    public static List<CheckResult> Assets(PakSet ps)
    {
        var counts = AssetExts.ToDictionary(e => e, _ => 0);
        var bad = AssetExts.ToDictionary(e => e, _ => 0);
        var perStem = ps.Stems.AsParallel().AsOrdered().Select(stem =>
        {
            var c = AssetExts.ToDictionary(e => e, _ => 0);
            var b = AssetExts.ToDictionary(e => e, _ => 0);
            var payload = ps.Payload(stem);
            foreach (var m in ps.Members(stem))
            {
                var ext = Bwd2File.Ext(m.Name);
                if (!c.ContainsKey(ext)) continue;
                c[ext]++;
                try
                {
                    // Python slices clamp; a member past the payload parses short and fails.
                    int len = Math.Max(0, Math.Min(m.Size, payload.Length - m.Offset));
                    var blob = payload.AsSpan(m.Offset, len).ToArray();
                    switch (ext)
                    {
                        case "geo":
                            var g = GeoMesh.Parse(blob);
                            // consumption, declared counts and index bounds
                            if (g.Consumed != m.Size || g.Normals.Length != g.Vertices.Length
                                || g.Faces.Any(f => f.Vertices.Any(fv => fv.Index >= g.Vertices.Length || fv.NormalIndex >= g.Normals.Length)))
                                b[ext]++;
                            break;
                        case "m16": TextureReader.ParseM16(blob); break;
                        case "map": TextureReader.ParseMap(blob); break;
                        case "tmt": _ = new TmtMaterial(blob); break;
                        case "vqm": _ = new VqmImage(blob); break;
                    }
                }
                catch (Exception) { b[ext]++; }
            }
            return (c, b);
        }).ToList();
        foreach (var (c, b) in perStem)
            foreach (var e in AssetExts) { counts[e] += c[e]; bad[e] += b[e]; }
        return AssetExts.Select(e => C("asset formats", $".{e} parses byte-exactly", bad[e] == 0, $"{counts[e]} members")).ToList();
    }

    public static List<CheckResult> Definitions(ZfsArchive ar)
    {
        const string s = "BWD2 definitions";
        int ok = 0, badfile = 0, nodes = 0, badnode = 0, mounts = 0, badmount = 0;
        foreach (var e in ar.Entries.Where(e => Bwd2File.IsDefinition(e.Name)))
        {
            Bwd2File f;
            try { f = new Bwd2File(ar.Read(e)); }
            catch (Exception) { badfile++; continue; }
            ok++;
            foreach (var c in f.Chunks)
                if (Bwd2File.TypeChunkSize.TryGetValue(c.Tag, out var want) && c.PayloadSize != want) badfile++;
            foreach (var w in f.WeaponMounts())
            {
                mounts++;
                if (w.Gizmo.Length == 0) badmount++;
            }
            try
            {
                foreach (var (_, ns) in f.SceneNodes())
                    foreach (var n in ns)
                    {
                        nodes++;
                        if (!(n.Transform.Orthonormal() || n.Transform.IsZero)) badnode++;
                    }
            }
            catch (Exception) { badfile++; }
        }
        return
        [
            C(s, "every definition walks to its exact length", badfile == 0, $"{ok} records"),
            C(s, "scene-graph rotations orthonormal", badnode == 0, $"{nodes} nodes"),
            C(s, "weapon mounts all name a gizmo or null", badmount == 0, $"{mounts} mounts"),
        ];
    }

    public static List<CheckResult> Missions(string missdir)
    {
        string s = $"missions ({Path.GetFileName(missdir.TrimEnd('/'))})";
        var files = Directory.EnumerateFiles(missdir).Select(Path.GetFileName)
            .Where(f => GameInstall.IsMissionFile(f!))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        int badSeq = 0, badTer = 0, badGrid = 0, badObj = 0, objs = 0, roads = 0, progOk = 0, progTotal = 0;
        var fsmSizes = new SortedSet<int>();
        foreach (var f in files)
        {
            var msn = new MissionFile(Path.Combine(missdir, f!));
            var tags = msn.ChunkTags();
            if (!tags.Skip(2).Take(6).SequenceEqual(MissionFile.MissionChunks)) badSeq++;
            var (n, grid) = msn.TerrainGrid();
            // the reference verifier names the .TER in upper case.
            var ter = Path.Combine(missdir, f![..^4] + ".TER");
            if (new FileInfo(ter).Length != (long)n * MissionFile.TerrainTileBytes) badTer++;
            var used = grid.Where(v => v != MissionFile.ZmapEmpty).Select(v => (int)v).ToHashSet();
            if (!used.SetEquals(Enumerable.Range(0, n))) badGrid++;
            foreach (var o in msn.PlacedObjects())
            {
                objs++;
                if (!MathUtil.Orthonormal(o.Rotation)) badObj++;
            }
            roads += msn.RoadSegments().Sum(r => r.Points.Count);
            var ops = msn.FsmOpcodes();
            if (ops.Count > 0)
            {
                fsmSizes.Add(ops.Count);
                progTotal++;
                var r = FsmDisassembler.Disassemble(msn);
                if (r is { } rr && rr.Code.All(i => FsmDisassembler.Ops.ContainsKey(i.Op))) progOk++;
            }
        }
        string sizes = "[" + string.Join(", ", fsmSizes) + "]";
        return
        [
            C(s, "chunk sequence identical in every mission", badSeq == 0, $"{files.Count} missions"),
            C(s, "TER size == tiles * 128*128*2", badTer == 0),
            C(s, "ZMAP grid uses exactly tile ids 0..n-1", badGrid == 0),
            C(s, "ODEF rotations orthonormal", badObj == 0, $"{objs} placements"),
            C(s, "RSEG point records parse", roads > 0, $"{roads} road points"),
            C(s, "FSM opcode table is 95 entries everywhere", fsmSizes.Count == 0 || fsmSizes.SetEquals([95]), sizes),
            C(s, "FSM bytecode disassembles with no unknown instruction", progOk == progTotal, $"{progOk}/{progTotal} scripted missions"),
        ];
    }

    /// <summary>Every check, in the reference verifier's order.</summary>
    public static List<CheckResult> Run(string zfsPath, IEnumerable<string> missdirs)
    {
        var ar = new ZfsArchive(zfsPath);
        var ps = new PakSet(ar);
        var o = new List<CheckResult>();
        o.AddRange(Archive(ar));
        o.Add(PakCoverage(ps));
        o.AddRange(Assets(ps));
        o.AddRange(Definitions(ar));
        foreach (var md in missdirs) o.AddRange(Missions(md));
        return o;
    }

    /// <summary>The report, formatted exactly as the reference verifier prints it.</summary>
    public static string Format(IReadOnlyList<CheckResult> results)
    {
        var sb = new System.Text.StringBuilder();
        string? section = null;
        foreach (var r in results)
        {
            if (r.Section != section)
            {
                sb.Append('\n').Append(r.Section).Append('\n');
                section = r.Section;
            }
            sb.Append("  [").Append(r.Ok ? "ok  " : "FAIL").Append("] ").Append(r.Name);
            if (r.Detail.Length > 0) sb.Append("  (").Append(r.Detail).Append(')');
            sb.Append('\n');
        }
        int failed = results.Count(r => !r.Ok);
        sb.Append(CultureInfo.InvariantCulture, $"\n{results.Count - failed}/{results.Count} checks passed\n");
        return sb.ToString();
    }
}
