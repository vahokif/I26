using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// i76 objects OUT.tsv: every distinct (name, ODEF type, flags) the install's missions place, with how often, in how many
/// missions, of which kinds (.msn campaign / melee .cbt .cf .rac / .lvl), its definition and its model's size. The editor's
/// object catalogue is built from this survey (docs/map-editor.md). Writes no game data.
/// </summary>
public static class ObjectSurvey
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var rows = new Dictionary<(string, uint, uint), (int Placed, HashSet<string> Missions, HashSet<string> Kinds, string? Def)>();
        var dirs = new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct();
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapDocument doc;
                try { doc = MapDocument.Load(f); } catch { continue; }
                string kind = Path.GetExtension(f).ToLowerInvariant();
                foreach (var o in doc.Objects)
                {
                    var key = (o.Name.ToLowerInvariant(), o.ObjectType, o.ObjectFlags);
                    if (!rows.TryGetValue(key, out var r)) rows[key] = r = (0, [], [], null);
                    r.Missions.Add(Path.GetFileNameWithoutExtension(f)); r.Kinds.Add(kind);
                    rows[key] = (r.Placed + 1, r.Missions, r.Kinds, null);
                }
            }
        using var w = new StreamWriter(argv[1]);
        w.WriteLine("name\ttype\tflags\tplaced\tmissions\tkinds\tdef\tsizeX\tsizeY\tsizeZ\tminY\tfaces\texamples");
        foreach (var (k, r) in rows.OrderBy(k => k.Key.Item2).ThenBy(k => k.Key.Item1))
        {
            string? def = k.Item1.Length == 0 || k.Item1.StartsWith('#') ? null : asm.ResolveDefinition(k.Item1);
            string size = "\t\t\t\t";
            if (def is not null && def.EndsWith(".sdf") && asm.Assemble(def) is { } m && m.Parts.Count > 0)
            {
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue); int faces = 0;
                foreach (var p in m.Parts)
                {
                    faces += p.Mesh.Faces.Length;
                    foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); min = Vector3.Min(min, t); max = Vector3.Max(max, t); }
                }
                size = $"{max.X - min.X:0.0}\t{max.Y - min.Y:0.0}\t{max.Z - min.Z:0.0}\t{min.Y:0.0}\t{faces}";
            }
            w.WriteLine($"{k.Item1}\t{k.Item2:x}\t{k.Item3:x}\t{r.Placed}\t{r.Missions.Count}\t{string.Join(",", r.Kinds.Order())}\t{def}\t{size}\t{string.Join(",", r.Missions.Take(4))}");
        }
        Console.WriteLine($"{rows.Count} distinct placements -> {argv[1]}");
        return 0;
    }
}

/// <summary>i76 bridges: for each stock placement of a road-carrying structure, the road points near it in its own frame (along, across), to see whether the road stops at its ends or runs under it.</summary>
public static class BridgeSurvey
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        string[] names = argv.Count > 1 ? argv[1].Split(',') : ["bebridg1", "bebridg2", "bebridg3", "bebridg4", "bebridga", "bebridgb", "beovrps1", "bewdbrg1", "bewdbrg2", "aaramp1", "aaramp3"];
        foreach (var d in new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct())
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapDocument doc;
                try { doc = MapDocument.Load(f); } catch { continue; }
                foreach (var o in doc.Objects.Where(o => names.Contains(o.Name.ToLowerInvariant())))
                {
                    var m = asm.Assemble(o.Name + ".sdf");
                    Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
                    if (m is not null) foreach (var p in m.Parts) foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); mn = Vector3.Min(mn, t); mx = Vector3.Max(mx, t); }
                    float yaw = o.Yaw; var pos = o.Position;
                    Console.WriteLine($"{Path.GetFileName(f)} {o.Name} t{o.ObjectType:x} at ({pos.X:0},{pos.Z:0}) yaw {yaw * 180 / MathF.PI:0} model x[{mn.X:0.0},{mx.X:0.0}] y[{mn.Y:0.0},{mx.Y:0.0}] z[{mn.Z:0.0},{mx.Z:0.0}]");
                    float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
                    for (int ri = 0; ri < doc.Roads.Count; ri++)
                    {
                        var pts = new List<string>();
                        for (int k = 0; k < doc.Roads[ri].Points.Count; k++)
                        {
                            var q = doc.Roads[ri].Points[k].Centre - new Vector2(pos.X, pos.Z);
                            // model frame: yaw about +Y, forward (0,0,1) -> (s,0,c)
                            float lx = q.X * c - q.Y * s, lz = q.X * s + q.Y * c;
                            if (MathF.Abs(lx) < 90 && MathF.Abs(lz) < 90) pts.Add($"#{k}({lx:0},{lz:0})");
                        }
                        if (pts.Count > 0) Console.WriteLine($"    road {ri} kind {doc.Roads[ri].Kind} n={doc.Roads[ri].Points.Count} width {Vector2.Distance(doc.Roads[ri].Points[0].Left, doc.Roads[ri].Points[0].Right):0.0}: {string.Join(" ", pts)}");
                    }
                }
            }
        return 0;
    }
}

/// <summary>i76 facing: for each building-sized scenery name, which side of its model (+z, +x, -z, -x) the nearest road lies on in the stock maps: the side a building faces the road with.</summary>
public static class FacingSurvey
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var ext = new Dictionary<string, (Vector3 Min, Vector3 Max)?>();
        var hist = new SortedDictionary<string, int[]>();
        var dist = new SortedDictionary<string, List<float>>();
        foreach (var d in new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct())
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapDocument doc;
                try { doc = MapDocument.Load(f); } catch { continue; }
                var centres = doc.Roads.SelectMany(r => r.Points.Select(p => p.Centre)).ToArray();
                if (centres.Length == 0) continue;
                foreach (var o in doc.Objects.Where(o => o.ObjectType == 2 && o.Name.Length > 0 && !o.Name.StartsWith('#')))
                {
                    if (!ext.TryGetValue(o.Name, out var e))
                    {
                        e = null;
                        if (asm.ResolveDefinition(o.Name) is { } def && def.EndsWith(".sdf") && asm.Assemble(def) is { } m && m.Parts.Count > 0)
                        {
                            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
                            foreach (var p in m.Parts) foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); mn = Vector3.Min(mn, t); mx = Vector3.Max(mx, t); }
                            e = (mn, mx);
                        }
                        ext[o.Name] = e;
                    }
                    if (e is not { } ex || (ex.Max.X - ex.Min.X) * (ex.Max.Z - ex.Min.Z) < 60) continue;
                    var pos = o.Position; float yaw = o.Yaw, c = MathF.Cos(yaw), s = MathF.Sin(yaw);
                    // model centre in world
                    var lc = new Vector2((ex.Min.X + ex.Max.X) / 2, (ex.Min.Z + ex.Max.Z) / 2);
                    var wc = new Vector2(pos.X + lc.X * c + lc.Y * s, pos.Z - lc.X * s + lc.Y * c);   // row0 = (c,-s), row2 = (s,c)
                    var best = centres.MinBy(p => Vector2.DistanceSquared(p, wc));
                    float dd = Vector2.Distance(best, wc);
                    if (dd > 70) continue;
                    var v2 = best - wc;
                    float lx = v2.X * c - v2.Y * s, lz = v2.X * s + v2.Y * c;
                    int side = MathF.Abs(lz) >= MathF.Abs(lx) ? (lz > 0 ? 0 : 2) : (lx > 0 ? 1 : 3);
                    if (!hist.TryGetValue(o.Name, out var h)) { hist[o.Name] = h = new int[4]; dist[o.Name] = []; }
                    h[side]++; dist[o.Name].Add(dd);
                }
            }
        foreach (var (n, h) in hist)
        {
            var ex = ext[n]!.Value;
            Console.WriteLine($"{n,-9} +z:{h[0],3} +x:{h[1],3} -z:{h[2],3} -x:{h[3],3}  model x[{ex.Min.X:0},{ex.Max.X:0}] z[{ex.Min.Z:0},{ex.Max.Z:0}] median road dist {dist[n].Order().ElementAt(dist[n].Count / 2):0}");
        }
        return 0;
    }
}

/// <summary>i76 roadpts NAME: each road's kind, point count, and its centre line every 8th point (to aim a capture at).</summary>
public static class RoadPoints
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var doc = MapDocument.Load(install.FindMission(argv[1]) ?? throw new FileNotFoundException(argv[1]));
        for (int i = 0; i < doc.Roads.Count; i++)
        {
            var r = doc.Roads[i];
            Console.WriteLine($"road {i} kind {r.Kind} n={r.Points.Count} len {r.Length():0} m: {string.Join(" ", r.Points.Where((_, k) => k % 8 == 0 || k == r.Points.Count - 1).Select(p => $"({p.Centre.X:0},{p.Centre.Y:0})"))}");
        }
        foreach (var o in doc.Objects.Where(o => o.Role != I76.Formats.Mission.Edit.MapRole.Object || o.ObjectType is 2 or 3 or 0xb or 0xc)) Console.WriteLine($"obj {o.Name} t{o.ObjectType:x} ({o.Position.X:0},{o.Position.Z:0}) yaw {o.Yaw * 180 / MathF.PI:0}");
        return 0;
    }
}

/// <summary>i76 decks: for every stock bridge, ramp or tunnel, the road points that lie inside its deck: where the stock maps end the road (at its ends, or inside).</summary>
public static class DeckSurvey
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var boxes = new Dictionary<string, (Vector3, Vector3)?>();
        var hist = new SortedDictionary<string, SortedDictionary<int, int>>();
        foreach (var d in new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct())
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapDocument doc;
                try { doc = MapDocument.Load(f); } catch { continue; }
                foreach (var o in doc.Objects.Where(o => (o.ObjectType is 0xb or 0xc) && (o.Name.StartsWith("be") || o.Name.StartsWith("aaramp"))))
                {
                    if (!boxes.TryGetValue(o.Name, out var bx))
                    {
                        bx = null;
                        if (asm.Assemble(o.Name + ".sdf") is { Parts.Count: > 0 } m)
                        {
                            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
                            foreach (var p in m.Parts) foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); mn = Vector3.Min(mn, t); mx = Vector3.Max(mx, t); }
                            bx = (mn, mx);
                        }
                        boxes[o.Name] = bx;
                    }
                    if (bx is not { } b) continue;
                    var item = new I76.Editor.Objects.CatalogueItem { MinX = b.Item1.X, MinZ = b.Item1.Z, MaxX = b.Item2.X, MaxZ = b.Item2.Z };
                    var fp = I76.Editor.Objects.Footprint.Of(item, o.Position, o.Yaw);
                    var deck = I76.Editor.Objects.Deck.Of(fp);
                    var zone = deck.Zone(0);
                    int inside = 0;
                    foreach (var r in doc.Roads) foreach (var p in r.Points) if (zone.Contains(p.Centre, -0.5f)) inside++;
                    if (!hist.TryGetValue(o.Name, out var h)) hist[o.Name] = h = [];
                    h[inside] = h.GetValueOrDefault(inside) + 1;
                }
            }
        foreach (var (n, h) in hist) Console.WriteLine($"{n,-9} road points inside the deck -> placements: {string.Join("  ", h.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        return 0;
    }
}

/// <summary>i76 catalogue: what the map editor's palette would hold for this install, by group (the scan the editor makes and caches).</summary>
public static class CatalogueCommand
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var asm = new ModelAssembler(new AssetVfs(install));
        var items = I76.Editor.Objects.ObjectCatalogue.Scan(install, asm, argv.Contains("-v") ? Console.Error.WriteLine : null);
        foreach (var g in items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key,-10} {g.Count(),4}   melee-used {g.Count(i => i.InMelee),4}   campaign-only {g.Count(i => !i.InMelee && i.Marker == I76.Editor.Objects.MarkerKind.None),4}");
        Console.WriteLine($"total {items.Count}");
        if (argv.Count > 1) foreach (var i in items) Console.WriteLine($"{i.Category}\t{i.Name}\t{i.Type:x}\t{i.Width:0.#}x{i.Depth:0.#}x{i.Height:0.#}\t{(i.InMelee ? "melee" : "campaign")}");
        return 0;
    }
}

/// <summary>
/// i76 roadsurf: the surface nibble of every stock terrain sample by its distance to the nearest road centre line (2.5 m bands) and
/// road kind (kind % 3), apart from samples nearest to a road's end (within the end cap) and samples near a bridge, ramp or tunnel
/// (ODEF 0xb, 0xc) or a road piece (0x5x). Numbers behind docs/map-format-writing.md "The ground under a road".
/// </summary>
public static class RoadSurfaceSurvey
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var hist = new SortedDictionary<string, SortedDictionary<int, int>>();
        void Add(string k, int n) { if (!hist.TryGetValue(k, out var h)) hist[k] = h = []; h[n] = h.GetValueOrDefault(n) + 1; }
        foreach (var d in new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct())
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapProject proj;
                try { proj = MapProject.Load(f, install); } catch { continue; }
                var special = proj.Mission.Objects.Where(o => (o.ObjectType & 0xf0) == 0x50 || o.ObjectType is 0xb or 0xc).Select(o => new Vector2(o.Position.X, o.Position.Z)).ToList();
                var roads = proj.Mission.Roads.Select(r => (r.Kind, P: r.Points.Select(p => p.Centre).ToArray())).Where(r => r.P.Length > 1).ToList();
                if (roads.Count == 0) continue;
                float minX = roads.Min(r => r.P.Min(p => p.X)) - 25, maxX = roads.Max(r => r.P.Max(p => p.X)) + 25, minZ = roads.Min(r => r.P.Min(p => p.Y)) - 25, maxZ = roads.Max(r => r.P.Max(p => p.Y)) + 25;
                for (int sz = Math.Max(0, (int)(minZ / 5)); sz <= maxZ / 5; sz++)
                    for (int sx = Math.Max(0, (int)(minX / 5)); sx <= maxX / 5; sx++)
                    {
                        ushort? s = proj.Terrain.Sample(sx, sz);
                        if (s is null) continue;
                        var q = new Vector2(sx * 5f, sz * 5f);
                        float best = float.MaxValue; uint kind = 0; bool end = false;
                        foreach (var (k, p) in roads)
                            for (int i = 0; i + 1 < p.Length; i++)
                            {
                                var ab = p[i + 1] - p[i]; float l2 = ab.LengthSquared();
                                float t = l2 < 1e-9f ? 0 : Vector2.Dot(q - p[i], ab) / l2;
                                float tc = Math.Clamp(t, 0, 1);
                                float dd = Vector2.Distance(q, p[i] + ab * tc);
                                if (dd < best) { best = dd; kind = k; end = (i == 0 && t < 0) || (i + 2 == p.Length && t > 1); }
                            }
                        if (best > 20) continue;
                        bool near = special.Any(v => Vector2.Distance(v, q) < 30);
                        string band = best < 2.5f ? "0-2.5" : best < 5 ? "2.5-5" : best < 7.5f ? "5-7.5" : best < 10 ? "7.5-10" : best < 11 ? "10-11" : best < 12 ? "11-12" : best < 13 ? "12-13" : best < 14 ? "13-14" : best < 15 ? "14-15" : "15-20";
                        Add($"kind{kind % 3} {(near ? "near-special" : end ? "past-end" : "plain")} {band} m", s.Value >> 12);
                    }
            }
        foreach (var (k, h) in hist) Console.WriteLine($"{k,-34}" + string.Join(" ", h.Select(e => $"{e.Key:x}:{e.Value}")));
        return 0;
    }
}
