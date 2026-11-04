using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using I76.Editor.Objects;
using I76.Formats;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Export;
using I76.Formats.Geo;
using I76.Formats.Imaging;
using I76.Formats.Mission;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// i76 export-obj --out DIR [--install DIR] [--nitro-install DIR] [--no-cars] [--no-objects] [--no-terrain] [--only a,b] [--plan]
///
/// Every vehicle body and every scenery model of the installs as Wavefront OBJ + MTL + PNG, built with the game's own assembly,
/// texture lookup and palettes, plus each map's terrain as a height mesh, and a manifest.json and README.md. The output is the
/// game's data, for the owner's own use: write it outside any repository. See the README the command writes for the conventions.
/// </summary>
public static class ExportObjCommand
{
    private sealed class Game(string label, GameInstall install)
    {
        public string Label { get; } = label;
        public GameInstall Install { get; } = install;
        public AssetVfs Vfs { get; } = new(install);
        public TextureResolver Res => _res ??= new TextureResolver(Vfs);
        public ModelAssembler Asm => _asm ??= new ModelAssembler(Vfs) { SkipParts = [] };
        private TextureResolver? _res;
        private ModelAssembler? _asm;
        /// <summary>The garage's neutral palette (T01's, else the first mission's), which the game builds cars with outside a mission.</summary>
        public string? DefaultPalette => _pal ??= new[] { "t01.act", "p01.act", "m02.act", "t02.act" }.FirstOrDefault(n => Vfs.TryRead(n) is not null);
        private string? _pal;
        public Palette? PaletteOf(string? name) => name is not null && Vfs.TryRead(name) is { } d ? new Palette(d) : null;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public static int Run(List<string> argv, string? installDir)
    {
        string? Opt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
        var outDir = Opt("--out");
        string? nitroDir = Opt("--nitro-install") ?? Environment.GetEnvironmentVariable("I76_NITRO_INSTALL");
        if (outDir is null || (installDir is null && nitroDir is null))
        {
            Console.Error.WriteLine("usage: i76 export-obj --out DIR [--install DIR] [--nitro-install DIR] [--no-cars] [--no-objects] [--no-terrain] [--only a,b] [--plan]");
            return 2;
        }
        var only = Opt("--only")?.Split(',').Select(s => s.ToLowerInvariant()).ToHashSet();
        bool plan = argv.Contains("--plan");
        var games = new List<Game>();
        if (installDir is not null) games.Add(new Game("I76", new GameInstall(installDir)));
        if (nitroDir is not null) games.Add(new Game("Nitro", new GameInstall(nitroDir)));
        Directory.CreateDirectory(outDir);
        var manifest = new JsonObject
        {
            ["format"] = "Wavefront OBJ + MTL + PNG",
            ["units"] = "metres",
            ["axes"] = "right-handed, +Y up; the game's (x, y, z) written as (x, y, -z); a vehicle faces -Z, its right is +X",
            ["winding"] = "triangles, counter-clockwise seen from the front; vertex normals outward",
            ["uv"] = "OBJ convention: v up (v = 1 - the game's v); textures repeat",
            ["generator"] = "i76 export-obj",
        };
        var skipped = new JsonArray();
        if (!argv.Contains("--no-cars")) manifest["cars"] = ExportCars(games, outDir, only, plan, skipped);
        if (plan) return 0;
        var maps = new JsonArray();
        if (!argv.Contains("--no-objects")) manifest["objects"] = ExportObjects(games, outDir, only, skipped, maps);
        if (!argv.Contains("--no-terrain")) manifest["terrain"] = ExportTerrain(games, outDir, only, skipped);
        manifest["maps"] = maps;
        manifest["skipped"] = MergeSkipped(skipped, manifest);
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJsonString(Json));
        File.WriteAllText(Path.Combine(outDir, "README.md"), Readme(manifest));
        return 0;
    }

    private static string Readme(JsonObject m)
    {
        int Count(string k) => m[k] is JsonArray a ? a.Count : 0;
        int schemes = m["cars"] is JsonArray cs ? cs.Sum(c => ((JsonArray)c!["schemes"]!).Count) : 0;
        int wrecks = m["objects"] is JsonArray os ? os.Count(o => o!["destroyed_obj"] is not null) : 0;
        int bodies = m["cars"] is JsonArray cb ? cb.Select(c => c!["vdf"]!.GetValue<string>()).Distinct().Count() : 0;
        int models = m["objects"] is JsonArray ob ? ob.Select(o => o!["sdf"]!.GetValue<string>()).Distinct().Count() : 0;
        return $$"""
            # Interstate '76 and Nitro Pack models (OBJ)

            Every vehicle body and every scenery model of Interstate '76 and the Nitro Pack, and each map's terrain, as Wavefront
            OBJ + MTL + PNG, built from the game files by I26's own model assembly, texture lookup and palettes (`i76 export-obj`).
            This is the games' data: for your own use, not for redistribution.

            Contents: {{bodies}} vehicle bodies in {{Count("cars")}} exports with {{schemes}} paint scheme MTLs (`cars/`), {{models}} scenery models in
            {{Count("objects")}} exports, {{wrecks}} of them also in their destroyed state (`objects/`), {{Count("terrain")}} terrains (`terrain/`) of {{Count("maps")}} maps. `manifest.json` lists every
            file with its game, source files, names, counts, and what was left out and why.

            ## Conventions

            - Units: metres. Axes: right-handed, +Y up; the game's (x, y, z) is written (x, y, -z). A vehicle faces -Z, its right is +X
              (Blender's default OBJ import turns that into facing +Y, up +Z). Scenery is in its own frame as the maps place it; the
              terrains are in the maps' world coordinates, the same frame.
            - Triangles, counter-clockwise seen from the front, vertex normals outward; polygons are fanned from their first corner as
              the game draws them. Texture coordinates: OBJ's v-up convention; textures repeat.
            - Textures: PNG, decoded as the game's 16-bit renderer shows them (each .m16's own palette; the few 8-bit-only textures through
              a palette, named per model in the manifest: the mission palette of a map that places the model, or T01's for the vehicles
              and unplaced models). `*_key.png` carries the colour-keyed transparency (palette index 255) of cut-out faces as alpha
              (map_d); the same texture on an opaque face uses the plain PNG, the keyed colour drawn as the game does.
              Translucent faces (rotor blades and the like) are `d 0.6`, the game's blend. Untextured faces are flat `Kd` colours.
              A material comment says which game texture it is and whether the game draws it unlit.
            - Animated textures (rotor blades, light bars) and wheel/track strips: their first frame.

            ## Vehicles (`cars/<body>/`)

            One OBJ per body (`.vdf`) with one MTL per paint scheme (`<body>.<paint file>.mtl`, the same material names in each); the
            OBJ's `mtllib` names the stock scheme. To use another scheme, change the `mtllib` line (or load that MTL). Groups:
            `body_*` (the body's parts, detail level 1, intact), `weapon<n>_*` (the stock car file's weapons on their hardpoints,
            turrets at rest), `rotor_*` (helicopters), `wheel<slot>_*` (each wheel's own model at its axle position, right and left
            models as the game has them).
            Assembled from the body's stock car file (the shell's variant 1, e.g. valepre1.vcf; the manifest names it). Bodies a game
            ships with no car file of its own (the base game's archive holds the Nitro Pack's bodies) are built from the other game's
            stock car file. The paint schemes are every paint file of the stock one's family (leprecn1-4, bus1-2...).
            Left out: the lamps' lit overlays (tail, brake and head lamp faces, shown only when lit), the cockpit/interior detail level,
            lower detail levels, damage and wreck states, light/siren flares (not geometry), invisible marker nodes, the modern look's
            additions (smoothed wheels, glass interiors).

            ## Scenery (`objects/<name>/`)

            Every scenery model (`.sdf`) any stock map of either game places (objects, fence strings, road pieces), everything the
            I26 map editor's catalogue offers, and the archive's remaining unplaced models (flagged in the manifest). Intact, detail
            level 1, every node at its transform; spinning signs, gates and camera-facing cards (the pine trees) at rest. A road piece
            only ever placed as one is the single node the game draws of it. `<name>_destroyed.obj` (same MTL) is the destroyed state
            where the model has one. Left out: smoke and flame sprite faces of burning wrecks, invisible marker nodes (ZZZ effect
            anchors, placeholder triangles), the ground clutter cards.

            Where the two games' exports of a model differ (other weapons on the stock car, a different palette), both are kept as
            `<name>-i76` and `<name>-nitro`; otherwise one folder serves both (`games` in the manifest).

            ## Terrain (`terrain/<game>_<ter>.obj`)

            One height mesh per distinct `.ter`: a vertex per height sample, 5 m apart, over the map's occupied tiles; heights as the
            game reads them (raw × 0.1 m); each cell split along its diagonal of nearer heights, as I26's 1997 look draws it. No
            textures: a group and a plain colour per terrain surface type. Large: the open-country maps are tens of MB each.

            Regenerate: `i76 --install <I76 dir> export-obj --nitro-install <Nitro dir> --out <dir>` (tools/I76.Cli in I26).

            """;
    }

    /// <summary>The skipped list with one entry per thing and reason (the games it applies to), leaving out what another game's data exported.</summary>
    private static JsonArray MergeSkipped(JsonArray skipped, JsonObject manifest)
    {
        var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { "cars", "objects" })
            if (manifest[kind] is JsonArray arr)
                foreach (var e in arr.OfType<JsonObject>())
                {
                    if (e["vdf"] is JsonValue v) exported.Add("car|" + v.GetValue<string>());
                    if (e["sdf"] is JsonValue sd) exported.Add("object|" + Path.GetFileNameWithoutExtension(sd.GetValue<string>()));
                }
        var o = new JsonArray();
        foreach (var gr in skipped.OfType<JsonObject>().GroupBy(e => (Kind: e["kind"]!.GetValue<string>(), Name: Path.GetFileNameWithoutExtension(e["name"]!.GetValue<string>()), Reason: e["reason"]!.GetValue<string>())))
        {
            if (exported.Contains(gr.Key.Kind + "|" + (gr.Key.Kind == "car" ? gr.First()["name"]!.GetValue<string>() : gr.Key.Name))) continue;
            o.Add(new JsonObject
            {
                ["kind"] = gr.Key.Kind, ["name"] = gr.First()["name"]!.GetValue<string>(), ["reason"] = gr.Key.Reason,
                ["games"] = new JsonArray([.. gr.Select(e => (JsonNode)e["game"]!.GetValue<string>()).Distinct()]),
            });
        }
        return o;
    }

    // ---------------------------------------------------------------- materials and textures

    /// <summary>A face's material, before a paint scheme picks its texture: its texture field and mode, or its flat colour.</summary>
    private sealed record MatKey(string Field, ModelObj.FaceMode Mode, bool Unlit, byte R, byte G, byte B)
    {
        public bool Flat => Field.Length == 0;
    }

    /// <summary>
    /// The materials of one exported model: names for the keys its faces use, and per paint scheme (or the one look of a
    /// scenery model) each key's texture, written as PNGs into the model's tex/ folder, decoded through the palette.
    /// </summary>
    private sealed class Materials(Game game, string dir, Palette? palette)
    {
        public readonly Dictionary<MatKey, string> Names = [];
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Member, bool Keyed), (string Path, bool HasKey)?> _png = [];
        public bool PaletteUsed { get; private set; }
        public readonly HashSet<string> Members = new(StringComparer.OrdinalIgnoreCase);

        public string NameOf(MatKey k)
        {
            if (Names.TryGetValue(k, out var n)) return n;
            string baseName = k.Flat ? $"rgb_{k.R:x2}{k.G:x2}{k.B:x2}"
                : Sanitise(TextureResolver.Stem(k.Field)) + k.Mode switch { ModelObj.FaceMode.Cutout => "_cut", ModelObj.FaceMode.Translucent => "_trans", _ => "" } + (k.Unlit ? "_unlit" : "");
            n = baseName;
            for (int i = 2; !_taken.Add(n); i++) n = $"{baseName}_{i}";
            return Names[k] = n;
        }

        /// <summary>The PNG for a texture member (written once), keyed (index 255 transparent) or not; null if it does not decode.</summary>
        public (string Path, bool HasKey)? Png(string member, bool keyed)
        {
            if (_png.TryGetValue((member, keyed), out var hit)) return hit;
            (string, bool)? r = null;
            if (game.Res.Load(member) is { } t)
            {
                if (t.Palette is null) PaletteUsed = true;
                Members.Add(member);
                var rgba = TextureReader.ToRgba(t, palette, keyIndex255: keyed);
                bool hasKey = keyed && rgba.Where((_, i) => i % 4 == 3).Any(a => a == 0);
                string stem = Sanitise(TextureResolver.Stem(member));
                string rel = $"tex/{stem}{(hasKey ? "_key" : "")}.png";
                byte[] png;
                if (hasKey)
                {
                    BleedUnderKey(rgba, t.Width, t.Height);
                    png = PngWriter.Encode(t.Width, t.Height, rgba, alpha: true);
                }
                else
                {
                    var rgb = new byte[t.Width * t.Height * 3];
                    for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3) { rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2]; }
                    png = PngWriter.Encode(t.Width, t.Height, rgb, alpha: false);
                }
                var path = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, png);
                r = (rel, hasKey);
                if (keyed && !hasKey) _png[(member, false)] = r;
            }
            return _png[(member, keyed)] = r;
        }

        /// <summary>The MTL material for a key whose texture resolved to <paramref name="member"/> (null: the face's flat colour, as the game falls back).</summary>
        public ObjMaterial Material(MatKey k, string? member)
        {
            string name = NameOf(k);
            if (!k.Flat && member is not null && Png(member, k.Mode != ModelObj.FaceMode.Opaque) is { } png)
                return new ObjMaterial
                {
                    Name = name, Texture = png.Path, AlphaTexture = png.HasKey ? png.Path : null, Unlit = k.Unlit,
                    Opacity = k.Mode == ModelObj.FaceMode.Translucent ? 0x99 / 255f : 1,
                    Note = $"{(k.Field.Length > 0 ? k.Field + " -> " : "")}{member}, {k.Mode.ToString().ToLowerInvariant()}{(k.Unlit ? ", unlit" : "")}",
                };
            return new ObjMaterial
            {
                Name = name, Colour = new Vector3(k.R, k.G, k.B) / 255f,
                Note = k.Flat ? "flat colour" : $"{k.Field} does not resolve here: the face's flat colour, as the game draws it",
            };
        }
    }

    private static string Sanitise(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        var o = sb.ToString().Trim('_');
        while (o.Contains("__")) o = o.Replace("__", "_");
        return o.Length == 0 ? "x" : o;
    }

    /// <summary>
    /// Keyed texels take the colour of an opaque neighbour (four passes), so a filtered or mip-mapped texture does not pull the key
    /// colour into the edges of a cut-out; their alpha stays 0. As the game prepares its keyed textures.
    /// </summary>
    private static void BleedUnderKey(byte[] p, int w, int h)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var src = (byte[])p.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (src[i + 3] != 0 || (pass > 0 && (src[i] | src[i + 1] | src[i + 2]) != 0)) continue;
                    int n = 0, r = 0, g = 0, b = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = (yy * w + xx) * 4;
                            if (src[j + 3] == 0 && (pass == 0 || (src[j] | src[j + 1] | src[j + 2]) == 0)) continue;
                            r += src[j]; g += src[j + 1]; b += src[j + 2]; n++;
                        }
                    if (n == 0) { if (pass == 0) p[i] = p[i + 1] = p[i + 2] = 0; continue; }
                    p[i] = (byte)(r / n); p[i + 1] = (byte)(g / n); p[i + 2] = (byte)(b / n);
                }
        }
    }

    private static MatKey KeyOf(GeoFace f, Func<string, bool> resolvesSomewhere) =>
        ModelObj.Textured(f) && resolvesSomewhere(f.Texture)
            ? new MatKey(f.Texture.ToUpperInvariant(), ModelObj.ModeOf(f), FaceLight.Unlit(f), 0, 0, 0)
            : new MatKey("", ModelObj.FaceMode.Opaque, false, f.R, f.G, f.B);

    // ---------------------------------------------------------------- cars

    private sealed record Scheme(string Vtf, string Name, List<string> Tmts, List<string> Maps);

    /// <summary>
    /// What a body is exported from: <paramref name="Vcf"/> the car file assembled (served from <paramref name="VcfBytes"/> when it is
    /// borrowed from the other game's data, <paramref name="BorrowedFrom"/>), every paint scheme, the stock one first.
    /// </summary>
    private sealed record CarPlan(Game Game, string Vdf, string? Vcf, byte[]? VcfBytes, string? BorrowedFrom, List<string> AllVcfs, List<Scheme> Schemes,
        string StockVtf, uint Class);

    private static string Lower(string s) => s.ToLowerInvariant();

    /// <summary>One game's car files and paint schemes.</summary>
    private sealed class CarSurvey
    {
        public readonly Dictionary<string, List<string>> VcfOf = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Name, string Vdf, string Vtf)> Fields = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every .vtf: the scheme and the body its VTFC names.</summary>
        public readonly List<(Scheme Scheme, string Vdf)> Vtfs = [];
        public readonly List<string> Vdfs = [];
        public readonly Dictionary<string, string?> Stock = new(StringComparer.OrdinalIgnoreCase);

        public CarSurvey(Game g)
        {
            var offered = CarTable.OfferedStems(g.Install);
            foreach (var n in g.Vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (g.Vfs.TryRead(n) is not { } d) continue;
                try
                {
                    if (new Bwd2File(d).VcfcFields() is not { } f) continue;
                    Fields[n] = f;
                    if (!VcfOf.TryGetValue(f.Vdf, out var l)) VcfOf[f.Vdf] = l = [];
                    l.Add(Lower(n));
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
            }
            foreach (var n in g.Vfs.Names.Where(n => n.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (g.Vfs.TryRead(n) is not { } d) continue;
                var vtf = new Bwd2File(d);
                if (vtf.First("VTFC") is not { } c) continue;
                Vtfs.Add((new Scheme(Lower(n), Bin.CStr(d, c.PayloadOffset + Bwd2File.FileField, Bwd2File.NameField).Trim(), ModelAssembler.VtfTmtNames(vtf),
                    vtf.References().Where(r => r.EndsWith(".map")).Order(StringComparer.Ordinal).ToList()), Lower(Bin.CStr(d, c.PayloadOffset, Bwd2File.FileField))));
            }
            Vdfs.AddRange(g.Vfs.Names.Where(n => n.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase)).Select(Lower).Distinct().Order(StringComparer.Ordinal));
            foreach (var vdf in Vdfs) Stock[vdf] = StockVcf(VcfOf.GetValueOrDefault(vdf) ?? [], offered);
        }
    }

    /// <summary>
    /// The plans of every game's bodies. A body a game ships without any car file naming it (the base game's archive holds the Nitro
    /// Pack's new bodies) is assembled from the other game's stock car file. The paint schemes: every .vtf of the stock car file's paint family
    /// (its name but the number: leprecn1..4, bus1..2; the garage's paint list groups by the first four letters, which misses the bus's
    /// second), else the ones whose VTFC names the body.
    /// </summary>
    private static List<CarPlan> PlanCars(List<Game> games)
    {
        var surveys = games.Select(g => new CarSurvey(g)).ToList();
        var plans = new List<CarPlan>();
        for (int gi = 0; gi < games.Count; gi++)
        {
            var g = games[gi];
            var sv = surveys[gi];
            foreach (var vdf in sv.Vdfs)
            {
                uint cls = g.Vfs.TryRead(vdf) is { } vd && new Bwd2File(vd).First("VDFC") is { } vc ? Bin.U32(vd, vc.PayloadOffset + 0x14) : 0;
                string? vcf = sv.Stock[vdf];
                byte[]? bytes = null;
                string? from = null;
                string? stockVtf = vcf is not null ? Lower(sv.Fields[vcf].Vtf) : null;
                if (vcf is null)
                    for (int oi = 0; oi < games.Count; oi++)
                        if (oi != gi && surveys[oi].Stock.GetValueOrDefault(vdf) is { } other && games[oi].Vfs.TryRead(other) is { } ob)
                        {
                            (vcf, bytes, from, stockVtf) = (other, ob, games[oi].Label, Lower(surveys[oi].Fields[other].Vtf));
                            break;
                        }
                var schemes = stockVtf is { Length: > 0 }
                    ? sv.Vtfs.Where(v => SchemeStem(v.Scheme.Vtf) == SchemeStem(stockVtf)).Select(v => v.Scheme).ToList() : [];
                if (schemes.Count == 0) schemes = sv.Vtfs.Where(v => v.Vdf == vdf).Select(v => v.Scheme).ToList();
                stockVtf ??= schemes.FirstOrDefault()?.Vtf ?? "";
                schemes = [.. schemes.OrderBy(s => s.Vtf == stockVtf ? 0 : 1).ThenBy(s => s.Vtf, StringComparer.Ordinal)];
                plans.Add(new CarPlan(g, vdf, vcf, bytes, from, sv.VcfOf.GetValueOrDefault(vdf) ?? [], schemes, stockVtf, cls));
            }
        }
        return plans;
    }

    /// <summary>A paint file's family: its name without the number ("bus" of bus2.vtf, "leprecn" of leprecn3.vtf).</summary>
    private static string SchemeStem(string vtf) => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(vtf), @"\d+$", "").ToLowerInvariant();

    /// <summary>
    /// The stock car file of a body: the shell's own for a model its car list offers (the model's stem and variant 1, "valepre1.vcf"),
    /// else a variant 1 of a "v" stem (the shells' naming, "vdrampg1.vcf"), else the lowest-numbered file of the stem most files share.
    /// </summary>
    private static string? StockVcf(List<string> vcfs, HashSet<string> offered)
    {
        if (vcfs.Count == 0) return null;
        if (offered.Select(st => Lower(st) + "1.vcf").Where(vcfs.Contains).Order(StringComparer.Ordinal).FirstOrDefault() is { } shell) return shell;
        if (vcfs.Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, @"^v[a-z]+1\.vcf$")).Order(StringComparer.Ordinal).FirstOrDefault() is { } v1) return v1;
        static string Stem(string n) => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(n), @"\d+$", "");
        var best = vcfs.GroupBy(Stem).OrderByDescending(gr => gr.Count()).ThenBy(gr => gr.Key, StringComparer.Ordinal).First();
        return best.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).First();
    }

    private static JsonArray ExportCars(List<Game> games, string outDir, HashSet<string>? only, bool plan, JsonArray skipped)
    {
        var all = new JsonArray();
        var byName = new Dictionary<string, (JsonObject Entry, string Dir, string Hash)>(StringComparer.OrdinalIgnoreCase);
        var plans = PlanCars(games);
        // paint files no exported body wears
        foreach (var g in games)
        {
            var worn = plans.Where(p => p.Game == g).SelectMany(p => p.Schemes).Select(sc => sc.Vtf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var n in g.Vfs.Names.Where(n => n.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) && !worn.Contains(n)))
                skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "paint", ["name"] = Lower(n), ["reason"] = "no body's paint family" });
        }
        foreach (var p in plans)
        {
            var g = p.Game;
            {
                string stem = Path.GetFileNameWithoutExtension(p.Vdf);
                if (only is not null && !only.Contains(stem) && !only.Contains(p.Vdf)) continue;
                if (plan)
                {
                    Console.WriteLine($"{g.Label}\t{p.Vdf}\tclass {p.Class}\tstock {p.Vcf ?? "-"}{(p.BorrowedFrom is null ? "" : " from " + p.BorrowedFrom)} ({p.StockVtf})\tvcfs {p.AllVcfs.Count}: {string.Join(",", p.AllVcfs.Take(6))}\tschemes {string.Join(",", p.Schemes.Select(s => $"{s.Vtf}:{s.Name}"))}");
                    continue;
                }
                var stage = Path.Combine(outDir, "cars", ".stage");
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                JsonObject? e;
                try { e = ExportCar(p, stage, skipped); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
                {
                    skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "car", ["name"] = p.Vdf, ["reason"] = "export failed: " + ex.Message });
                    Console.Error.WriteLine($"{g.Label} {p.Vdf}: {ex}");
                    continue;
                }
                if (e is null) { Directory.Delete(stage, true); continue; }
                Place(e, stem, Path.Combine(outDir, "cars"), stage, g.Label, byName, all, "cars");
                Console.WriteLine($"{g.Label} {p.Vdf}: {e["triangles"]} triangles, {((JsonArray)e["schemes"]!).Count} schemes");
            }
        }
        return all;
    }

    /// <summary>
    /// Moves a staged export to its folder: the model's name, shared by both games when their exports are byte for byte the same;
    /// where they differ, each game's under name-i76 and name-nitro.
    /// </summary>
    private static void Place(JsonObject e, string name, string root, string stage, string game, Dictionary<string, (JsonObject Entry, string Dir, string Hash)> byName,
        JsonArray all, string kind)
    {
        string hash = HashDir(stage);
        string target = Path.Combine(root, name);
        if (byName.TryGetValue(name, out var prev))
        {
            if (prev.Hash == hash)
            {
                ((JsonArray)prev.Entry["games"]!).Add(game);
                AddMaps(prev.Entry, e);
                Directory.Delete(stage, true);
                return;
            }
            // differs: the first game's moves to name-<game>, this one to name-<this game>
            if (prev.Dir == target)
            {
                string prevGame = ((JsonArray)prev.Entry["games"]!)[0]!.GetValue<string>().ToLowerInvariant();
                string moved = target + "-" + prevGame;
                if (Directory.Exists(moved)) Directory.Delete(moved, true);
                Directory.Move(target, moved);
                Relocate(prev.Entry, $"{kind}/{name}/", $"{kind}/{name}-{prevGame}/");
                byName[name] = (prev.Entry, moved, prev.Hash);
            }
            target = Path.Combine(root, name + "-" + game.ToLowerInvariant());
            Relocate(e, $"{kind}/{name}/", $"{kind}/{name}-{game.ToLowerInvariant()}/");
        }
        else byName[name] = (e, target, hash);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(stage, target);
        all.Add(e);
    }

    /// <summary>A second game's identical export: its maps join the entry's, and its flags count too.</summary>
    private static void AddMaps(JsonObject into, JsonObject from)
    {
        if (into["maps"] is JsonArray a && from["maps"] is JsonArray b)
            foreach (var m in b) a.Add(m!.DeepClone());
        foreach (var flag in new[] { "placed_by_maps", "in_editor_catalogue" })
            if (into[flag] is JsonValue x && from[flag] is JsonValue y && !x.GetValue<bool>() && y.GetValue<bool>()) into[flag] = true;
        if (into["road_piece_only"] is JsonValue r && from["road_piece_only"] is JsonValue r2 && r.GetValue<bool>() && !r2.GetValue<bool>()) into["road_piece_only"] = false;
    }

    /// <summary>Rewrites the entry's file paths after its folder moved.</summary>
    private static void Relocate(JsonNode node, string from, string to)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).ToList())
                    if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith(from, StringComparison.Ordinal)) o[k] = to + s[from.Length..];
                    else if (o[k] is { } child) Relocate(child, from, to);
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith(from, StringComparison.Ordinal)) arr[i] = to + s[from.Length..];
                    else if (arr[i] is { } child) Relocate(child, from, to);
                break;
        }
    }

    private static string HashDir(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, f)));
            // the comment lines name where a file came from, not what it holds
            if (f.EndsWith(".obj", StringComparison.Ordinal) || f.EndsWith(".mtl", StringComparison.Ordinal))
                foreach (var line in File.ReadLines(f).Where(l => !l.StartsWith('#'))) sha.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            else sha.AppendData(File.ReadAllBytes(f));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>
    /// An invisible marker node: a ZZZ part (an effect's anchor: where a structure's explosion goes off), or a node holding only the
    /// modelling tool's placeholder (one or two small untextured black faces, the same 0.39 m² triangle in most), which stands in for
    /// a node that has nothing of its own to show.
    /// </summary>
    private static bool IsMarker(ModelPart p)
    {
        var n = p.NodeName;
        if (n.Length >= 7 && n[4..7].Equals("ZZZ", StringComparison.OrdinalIgnoreCase)) return true;
        var faces = p.Mesh.Faces;
        if (faces.Length is 0 or > 2 || faces.Any(f => ModelObj.Textured(f) || f.R != 0 || f.G != 0 || f.B != 0)) return false;
        float area = 0;
        foreach (var f in faces)
            for (int k = 1; k + 1 < f.Vertices.Length; k++)
            {
                if (f.Vertices.Any(v => (uint)v.Index >= p.Mesh.Vertices.Length)) return true;
                var a = p.Mesh.Vertices[f.Vertices[0].Index];
                area += Vector3.Cross(p.Mesh.Vertices[f.Vertices[k].Index] - a, p.Mesh.Vertices[f.Vertices[k + 1].Index] - a).Length() / 2;
            }
        return area < 1f;
    }

    /// <summary>A lamp part: the lit faces of the tail, brake and head lamps, drawn only while lit (the game's lamp overlays).</summary>
    private static bool IsLamp(string node) => node.Length >= 8 && node[4..8].ToUpperInvariant() is "TLGT" or "BLGT" or "HLGT";

    /// <summary>A helicopter's rotor: a flat disc wearing the animated blade flipbook (BLADE…).</summary>
    private static bool IsRotor(ModelPart p) => p.Mesh.Faces.Length > 0 && p.Mesh.Faces.All(f => f.Texture.StartsWith("BLADE", StringComparison.OrdinalIgnoreCase));

    private static JsonObject? ExportCar(CarPlan p, string dir, JsonArray skipped)
    {
        var g = p.Game;
        string stem = Path.GetFileNameWithoutExtension(p.Vdf);
        string vcf;
        IDisposable? served = null;
        if (p.Vcf is null)
        {
            // a body no car file of either game names: a car file of our own naming it and its first paint scheme, no wheels, no weapons
            if (p.Schemes.Count == 0)
            {
                skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "car", ["name"] = p.Vdf, ["reason"] = "no car file and no paint scheme names this body" });
                return null;
            }
            vcf = "zexport_" + stem + ".vcf";
            AssetVfs.Addon[vcf] = SyntheticVcf(p.Vdf, p.Schemes[0].Vtf);
            served = new Release(vcf);
        }
        else if (p.VcfBytes is not null)
        {
            // the other game's stock car file, read against this game's data
            vcf = "zexport_" + p.Vcf;
            AssetVfs.Addon[vcf] = p.VcfBytes;
            served = new Release(vcf);
        }
        else vcf = p.Vcf;
        using var _ = served;
        var model = g.Asm.Assemble(vcf);
        if (model is null || model.Parts.Count == 0)
        {
            skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "car", ["name"] = p.Vdf, ["reason"] = "assembles to no geometry" });
            return null;
        }
        bool heli = p.Class == 9;
        var schemes = p.Schemes.Count > 0 ? p.Schemes : [new Scheme(p.StockVtf, "", [.. model.TmtNames], [.. model.VtfMaps])];
        var resolveCache = new Dictionary<(string, string), string?>();
        string? Resolve(Scheme s, string field)
        {
            if (resolveCache.TryGetValue((s.Vtf, field), out var r)) return r;
            return resolveCache[(s.Vtf, field)] = g.Res.ResolveFace(field, model.ObjectCode, s.Tmts, vtfMaps: s.Maps);
        }
        bool Somewhere(string field) => schemes.Any(s => Resolve(s, field) is not null);

        var palName = g.DefaultPalette;
        var mats = new Materials(g, dir, g.PaletteOf(palName));
        var mesh = new ObjMesh();
        string MatOf(ModelPart _, GeoFace f) => mats.NameOf(KeyOf(f, Somewhere));
        var turrets = model.Turrets.Select(t => t.Node).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = new SortedSet<string>(StringComparer.Ordinal);
        // the body: every part but the lamps' lit overlays, the weapons and the rotors (a car's rotor-like parts are never drawn)
        ModelObj.AddModel(mesh, model, part =>
        {
            if (IsLamp(part.NodeName)) { left.Add($"{part.NodeName}: lamp overlay (lit state)"); return null; }
            if (IsMarker(part)) { left.Add($"{part.NodeName}: invisible marker"); return null; }
            if (model.GizmoOf.ContainsKey(part) || turrets.Contains(part.NodeName)) return null;
            if (IsRotor(part)) { if (!heli) left.Add($"{part.NodeName}: rotor disc on a non-flying body (not drawn)"); return null; }
            return "body_" + Lower(part.NodeName);
        }, MatOf);
        // the weapons, each with its turret's gun at rest
        var weapons = new JsonArray();
        for (int w = 0; w < model.Gizmos.Count; w++)
        {
            int wi = w;
            ModelObj.AddModel(mesh, model, part => model.GizmoOf.TryGetValue(part, out int pw) && pw == wi && !IsMarker(part) ? $"weapon{wi}_{Lower(part.NodeName)}" : null, MatOf);
            var (slot, gz, hp) = model.Gizmos[w];
            weapons.Add(new JsonObject { ["slot"] = slot, ["gizmo"] = Lower(gz.Name) });
        }
        if (heli) ModelObj.AddModel(mesh, model, part => IsRotor(part) && !model.GizmoOf.ContainsKey(part) ? "rotor_" + Lower(part.NodeName) : null, MatOf);
        // the wheels: each WLOC slot's own .wdf mesh at the slot, or the synthesised disc where the car file names none (a helicopter has none)
        var wheels = new JsonArray();
        if (!heli)
            foreach (var loc in model.Locations.Where(l => l.Tag == "WLOC"))
            {
                var t = loc.Transform.Translation;
                if (MathF.Abs(t[0]) + MathF.Abs(t[1]) + MathF.Abs(t[2]) < 1e-6f) continue;
                if (model.WheelGeos.TryGetValue(loc.Index, out var wg))
                {
                    var at = Affine.FromEngine([1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3(t[0], t[1], t[2]));
                    var wm = new AssembledModel { Definition = model.Definition, Kind = "sdf", ObjectCode = model.ObjectCode };
                    wm.Parts.Add(new ModelPart(wg.Intact.Name, wg.Intact, at.Compose(wg.Node)));
                    ModelObj.AddModel(mesh, wm, part => $"wheel{loc.Index}_{Lower(part.NodeName)}", MatOf);
                    wheels.Add(new JsonObject { ["slot"] = loc.Index, ["mesh"] = Lower(wg.Intact.Name) });
                }
                else if (g.Res.FindByStem(Wheels.DefaultTexture) is not null)
                {
                    var key = new MatKey(Wheels.DefaultTexture.ToUpperInvariant() + ".MAP", ModelObj.FaceMode.Cutout, false, 0, 0, 0);
                    ModelObj.AddSynth(mesh, $"wheel{loc.Index}_synth", Wheels.Build(model, onlySlot: loc.Index), mats.NameOf(key));
                    wheels.Add(new JsonObject { ["slot"] = loc.Index, ["mesh"] = "synthesised disc (" + Wheels.DefaultTexture + ")" });
                }
            }
        if (mesh.TriangleCount == 0) return null;
        // one MTL per scheme, the same material names in each
        var schemeOut = new JsonArray();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var used = mesh.Materials.ToHashSet(StringComparer.Ordinal);
        foreach (var s in schemes)
        {
            var list = new List<ObjMaterial>();
            foreach (var (k, name) in mats.Names.OrderBy(kv => kv.Value, StringComparer.Ordinal))
            {
                if (!used.Contains(name)) continue;
                string? member = k.Flat ? null : Resolve(s, k.Field);
                if (!k.Flat && member is null) unresolved.Add($"{s.Vtf}: {k.Field}");
                list.Add(mats.Material(k.Flat ? k : member is null ? FirstColour(model, k) : k, member) is var m && m.Name != name
                    ? new ObjMaterial { Name = name, Colour = m.Colour, Note = m.Note } : m);
            }
            string mtl = $"{stem}.{Path.GetFileNameWithoutExtension(s.Vtf)}.mtl";
            using (var w = new StreamWriter(Path.Combine(dir, mtl)))
                MtlWriter.Write(w, list, [$"{CarNames.Of(g.Vfs, p.Vdf) ?? stem}: paint scheme {s.Vtf} \"{s.Name}\""]);
            schemeOut.Add(new JsonObject { ["vtf"] = s.Vtf, ["name"] = s.Name, ["mtl"] = $"cars/{stem}/{mtl}", ["stock"] = s.Vtf == p.StockVtf });
        }
        string objName = stem + ".obj";
        string defaultMtl = $"{stem}.{Path.GetFileNameWithoutExtension(schemes[0].Vtf)}.mtl";
        using (var w = new StreamWriter(Path.Combine(dir, objName)))
            mesh.Write(w, defaultMtl, [
                $"{CarNames.Of(g.Vfs, p.Vdf) ?? stem} ({p.Vdf}), assembled as the car file {p.Vcf ?? "(none: the body alone)"} builds it",
                "metres; right-handed, +Y up, facing -Z (the game's z negated); counter-clockwise front faces",
                $"paint schemes: one .mtl each beside this file ({schemes.Count}); this file names the stock one, {schemes[0].Vtf}",
            ]);
        var (lo, hi) = mesh.Bounds();
        return new JsonObject
        {
            ["games"] = new JsonArray(g.Label),
            ["name"] = CarNames.Of(g.Vfs, p.Vdf) ?? stem,
            ["vdf"] = p.Vdf,
            ["vcf"] = p.Vcf,
            ["vcf_from_other_game"] = p.BorrowedFrom,
            ["car_files_naming_body"] = new JsonArray([.. p.AllVcfs.Select(v => (JsonNode)v)]),
            ["class"] = p.Class switch { 1 => "car", 8 => "emplacement", 9 => "helicopter", _ => $"class {p.Class}" },
            ["obj"] = $"cars/{stem}/{objName}",
            ["vertices"] = mesh.Positions.Count,
            ["triangles"] = mesh.TriangleCount,
            ["bounds_min"] = new JsonArray(lo.X, lo.Y, lo.Z),
            ["bounds_max"] = new JsonArray(hi.X, hi.Y, hi.Z),
            ["schemes"] = schemeOut,
            ["weapons"] = weapons,
            ["wheels"] = wheels,
            ["groups"] = new JsonArray([.. mesh.Groups.Where(gr => gr.Faces.Count > 0).Select(gr => (JsonNode)gr.Name)]),
            ["left_out"] = new JsonArray([.. left.Select(l => (JsonNode)l)]),
            ["palette"] = mats.PaletteUsed ? palName : null,
            ["unresolved_textures"] = new JsonArray([.. unresolved.Select(u => (JsonNode)u)]),
            ["missing_geometry"] = new JsonArray([.. model.MissingGeo.Select(m => (JsonNode)m)]),
        };
    }

    /// <summary>A textured key that does not resolve in a scheme: the flat colour of the first face wearing it.</summary>
    private static MatKey FirstColour(AssembledModel model, MatKey k)
    {
        foreach (var part in model.Parts)
            foreach (var f in part.Mesh.Faces)
                if (f.Texture.Equals(k.Field, StringComparison.OrdinalIgnoreCase)) return new MatKey("", ModelObj.FaceMode.Opaque, false, f.R, f.G, f.B);
        return new MatKey("", ModelObj.FaceMode.Opaque, false, 255, 0, 255);
    }

    /// <summary>A minimal car file naming a body and a paint scheme, with no wheels and no weapons.</summary>
    private static byte[] SyntheticVcf(string vdf, string vtf)
    {
        // the BWD2 container: a REV chunk then VCFC (the name, the body, the paint, the three axles' wheel files) and EXIT
        var vcfc = new byte[0x60];
        void Put(int at, string s, int max) { var b = Encoding.ASCII.GetBytes(s); Array.Copy(b, 0, vcfc, at, Math.Min(b.Length, max - 1)); }
        Put(0, "export", Bwd2File.NameField);
        Put(Bwd2File.NameField, vdf, Bwd2File.FileField);
        Put(Bwd2File.NameField + Bwd2File.FileField, vtf, Bwd2File.FileField);
        for (int a = 0; a < 3; a++) Put(0x36 + 13 * a, "null", 13);
        using var ms = new MemoryStream();
        void Chunk(string tag, byte[] payload)
        {
            var t = new byte[4]; Encoding.ASCII.GetBytes(tag).CopyTo(t, 0);
            ms.Write(t);
            ms.Write(BitConverter.GetBytes(payload.Length + Bwd2File.HeaderSize));
            ms.Write(payload);
        }
        Chunk("BWD2", new byte[4]);
        Chunk("VCFC", vcfc);
        Chunk("EXIT", []);
        return ms.ToArray();
    }

    private sealed class Release(string name) : IDisposable
    {
        public void Dispose() => AssetVfs.Addon.TryRemove(name, out _);
    }

    // ---------------------------------------------------------------- scenery objects

    private sealed class ObjectUse
    {
        public readonly SortedSet<string> Maps = new(StringComparer.OrdinalIgnoreCase);
        public string? FirstMap;
        public string? Palette;
        public bool Placed, AsObject, AsRoadPiece, InCatalogue;
        /// <summary>Why the map editor leaves an unplaced model out of its catalogue, where it says.</summary>
        public string? Note;
    }

    private static JsonArray ExportObjects(List<Game> games, string outDir, HashSet<string>? only, JsonArray skipped, JsonArray mapsOut)
    {
        var all = new JsonArray();
        var byName = new Dictionary<string, (JsonObject Entry, string Dir, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
        {
            var uses = new Dictionary<string, ObjectUse>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in GameInstall.MissionExtensions)
                foreach (var path in g.Install.ListMissions(ext).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string map = Path.GetFileName(path);
                    MissionWorld world;
                    try { world = new MissionWorld(g.Vfs, g.Asm, path); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
                    {
                        skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "map", ["name"] = map, ["reason"] = "does not load: " + ex.Message });
                        continue;
                    }
                    var placed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pl in world.Placements.Concat(world.LinePlacements))
                    {
                        if (pl.Definition is not { } def || !def.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!uses.TryGetValue(def, out var u)) uses[def] = u = new ObjectUse();
                        u.Placed = true;
                        if (u.Maps.Add(map) && u.FirstMap is null) { u.FirstMap = map; u.Palette = world.PaletteName; }
                        if (RoadPieceObjects.IsRoadPiece(pl.Object.ObjectType)) u.AsRoadPiece = true; else u.AsObject = true;
                        placed.Add(Lower(def));
                    }
                    if (only is null || only.Contains(Lower(Path.GetFileNameWithoutExtension(map))) || placed.Any(d => only.Contains(Path.GetFileNameWithoutExtension(d))))
                        mapsOut.Add(new JsonObject
                        {
                            ["game"] = g.Label, ["file"] = map, ["title"] = MissionTitle(g, map), ["palette"] = world.PaletteName,
                            ["terrain_file"] = Path.GetFileName(world.TerrainPath), ["objects"] = new JsonArray([.. placed.Select(d => (JsonNode)Path.GetFileNameWithoutExtension(d))]),
                        });
                }
            // the map editor's object catalogue: what the maps place, and the unplaced models it offers
            foreach (var item in ObjectCatalogue.Scan(g.Install, g.Asm))
            {
                if (!item.Definition.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase)) continue;
                if (!uses.TryGetValue(item.Definition, out var u)) uses[item.Definition] = u = new ObjectUse { AsObject = true };
                u.InCatalogue = true;
            }
            // and every other scenery model the archive holds (no stock map places it, the editor does not offer it): exported too, so
            // nothing is missing, but for the ground clutter cards (an effect the engine scatters, not an object)
            foreach (var n in g.Vfs.Names.Where(n => n.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (uses.ContainsKey(n)) continue;
                string stem = Lower(Path.GetFileNameWithoutExtension(n));
                if (stem.StartsWith("ngscrng", StringComparison.Ordinal))
                {
                    skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "object", ["name"] = stem, ["reason"] = "ground clutter cards (the engine scatters them round the camera by surface type): an effect, not a placed object" });
                    continue;
                }
                uses[n] = new ObjectUse { AsObject = true, Note = ObjectCatalogue.Excluded.GetValueOrDefault(stem) };
            }

            foreach (var (def, u) in uses.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = Lower(Path.GetFileNameWithoutExtension(def));
                if (only is not null && !only.Contains(name) && !u.Maps.Any(m => only.Contains(Lower(Path.GetFileNameWithoutExtension(m))))) continue;
                var stage = Path.Combine(outDir, "objects", ".stage");
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                JsonObject? e;
                try { e = ExportObject(g, def, u, stage, skipped); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
                {
                    skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "object", ["name"] = def, ["reason"] = "export failed: " + ex.Message });
                    continue;
                }
                if (e is null) { Directory.Delete(stage, true); continue; }
                Place(e, name, Path.Combine(outDir, "objects"), stage, g.Label, byName, all, "objects");
            }
            Console.WriteLine($"{g.Label}: {uses.Count} object models");
        }
        return all;
    }

    private static readonly Dictionary<Game, List<MapListing>> Titles = [];

    private static string? MissionTitle(Game g, string map)
    {
        if (!Titles.TryGetValue(g, out var list))
        {
            try { list = MissionTitles.Stock(g.Install); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { list = []; }
            Titles[g] = list;
        }
        var stem = Path.GetFileNameWithoutExtension(map);
        return list.FirstOrDefault(l => l.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase)) is { Title.Length: > 0 } hit ? hit.Title : null;
    }

    private static JsonObject? ExportObject(Game g, string def, ObjectUse u, string dir, JsonArray skipped)
    {
        string name = Lower(Path.GetFileNameWithoutExtension(def));
        var intact = g.Asm.Assemble(def);
        if (intact is null || intact.Parts.Count == 0)
        {
            skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "object", ["name"] = name, ["reason"] = "assembles to no geometry" });
            return null;
        }
        // placed only as a road piece, the game draws one node of it (its last top-level node and what hangs from it)
        bool pieceOnly = u.AsRoadPiece && !u.AsObject;
        var model = pieceOnly ? RoadPieceObjects.Drawn(intact) : intact;
        if (model.Parts.Count == 0)
        {
            skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "object", ["name"] = name, ["reason"] = "placed only as a road piece whose drawn node has no geometry (the game shows nothing)" });
            return null;
        }
        string? palName = u.Palette ?? g.DefaultPalette;
        var mats = new Materials(g, dir, g.PaletteOf(palName));
        var left = new SortedSet<string>(StringComparer.Ordinal);
        var resolve = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Resolve(AssembledModel m, string field) => resolve.TryGetValue(field, out var r) ? r : resolve[field] = g.Res.ResolveFace(field, m.ObjectCode, m.TmtNames);
        // the original's sprite smoke and flame planes on wrecks are effects, not the model
        bool Skip(ModelPart part, GeoFace f)
        {
            if (!WreckSmoke.IsSpriteFace(f)) return false;
            left.Add($"{part.NodeName}: smoke/flame sprite face ({f.Texture})");
            return true;
        }
        ObjMesh Build(AssembledModel m)
        {
            var mesh = new ObjMesh();
            ModelObj.AddModel(mesh, m, part =>
            {
                if (!IsMarker(part)) return Lower(part.NodeName);
                left.Add($"{part.NodeName}: invisible marker");
                return null;
            }, (_, f) => mats.NameOf(KeyOf(f, field => Resolve(m, field) is not null)), Skip);
            return mesh;
        }
        var mesh = Build(model);
        if (mesh.TriangleCount == 0)
        {
            skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "object", ["name"] = name, ["reason"] = "no faces left to draw" });
            return null;
        }
        // the destroyed state, where the model has one: its 'A' parts in place of their intact twins
        ObjMesh? wreck = null;
        // (not the road pieces' 'A' twins: nothing damages a road piece)
        bool roadPiece = name.StartsWith('i') || name.StartsWith("rerwend", StringComparison.Ordinal) || name.StartsWith("rmrwmid", StringComparison.Ordinal);
        if (!pieceOnly && !roadPiece && new ModelAssembler(g.Vfs) { Destroyed = true, SkipParts = [] }.Assemble(def) is { Parts.Count: > 0 } dm
            && !dm.Parts.Select(p => p.NodeName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(model.Parts.Select(p => p.NodeName)))
        {
            var intactLeft = new SortedSet<string>(left, StringComparer.Ordinal);
            wreck = Build(dm);
            left.IntersectWith(intactLeft);
            if (wreck.TriangleCount == 0) wreck = null;
        }
        var used = mesh.Materials.Concat(wreck?.Materials ?? []).ToHashSet(StringComparer.Ordinal);
        var list = new List<ObjMaterial>();
        foreach (var (k, mname) in mats.Names.OrderBy(kv => kv.Value, StringComparer.Ordinal))
            if (used.Contains(mname)) list.Add(mats.Material(k, k.Flat ? null : Resolve(model, k.Field)));
        string mtl = name + ".mtl";
        string title = AssetBible.Describe(name) ?? name;
        using (var w = new StreamWriter(Path.Combine(dir, mtl)))
            MtlWriter.Write(w, list, [$"{name}{(mats.PaletteUsed ? $", 8-bit textures in {palName}" : "")}"]);
        string[] header = [
            $"{name}: {title} ({Lower(def)})",
            "metres; right-handed, +Y up (the game's z negated); counter-clockwise front faces",
        ];
        using (var w = new StreamWriter(Path.Combine(dir, name + ".obj"))) mesh.Write(w, mtl, header);
        if (wreck is not null)
            using (var w = new StreamWriter(Path.Combine(dir, name + "_destroyed.obj"))) wreck.Write(w, mtl, [.. header, "destroyed state"]);
        var (lo, hi) = mesh.Bounds();
        return new JsonObject
        {
            ["games"] = new JsonArray(g.Label),
            ["name"] = name,
            ["description"] = AssetBible.Describe(name),
            ["sdf"] = Lower(def),
            ["obj"] = $"objects/{name}/{name}.obj",
            ["destroyed_obj"] = wreck is null ? null : $"objects/{name}/{name}_destroyed.obj",
            ["mtl"] = $"objects/{name}/{mtl}",
            ["vertices"] = mesh.Positions.Count,
            ["triangles"] = mesh.TriangleCount,
            ["destroyed_triangles"] = wreck?.TriangleCount,
            ["bounds_min"] = new JsonArray(lo.X, lo.Y, lo.Z),
            ["bounds_max"] = new JsonArray(hi.X, hi.Y, hi.Z),
            ["placed_by_maps"] = u.Placed,
            ["in_editor_catalogue"] = u.InCatalogue,
            ["note"] = u.Note,
            ["road_piece_only"] = pieceOnly,
            ["maps"] = new JsonArray([.. u.Maps.Select(m => (JsonNode)$"{g.Label}:{m}")]),
            ["palette"] = mats.PaletteUsed ? palName : null,
            ["palette_from"] = mats.PaletteUsed ? (u.FirstMap is null ? "the garage's neutral palette (no stock map places it)" : $"{g.Label}:{u.FirstMap}") : null,
            ["textures"] = new JsonArray([.. mats.Members.Order(StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)Lower(m))]),
            ["groups"] = new JsonArray([.. mesh.Groups.Where(gr => gr.Faces.Count > 0).Select(gr => (JsonNode)gr.Name)]),
            ["left_out"] = new JsonArray([.. left.Select(l => (JsonNode)l)]),
            ["missing_geometry"] = new JsonArray([.. model.MissingGeo.Select(m => (JsonNode)m)]),
        };
    }

    // ---------------------------------------------------------------- terrain

    private static JsonArray ExportTerrain(List<Game> games, string outDir, HashSet<string>? only, JsonArray skipped)
    {
        var all = new JsonArray();
        var done = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var g in games)
            foreach (var ext in GameInstall.MissionExtensions)
                foreach (var path in g.Install.ListMissions(ext).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string map = Path.GetFileName(path);
                    if (only is not null && !only.Contains(Lower(Path.GetFileNameWithoutExtension(map)))) continue;
                    MissionWorld world;
                    try { world = new MissionWorld(g.Vfs, g.Asm, path); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { continue; }
                    // one mesh per distinct terrain (several maps share a .ter)
                    var hash = world.Terrain.ContentHash().ToString("x16");
                    if (done.TryGetValue(hash, out var prev)) { ((JsonArray)prev["maps"]!).Add($"{g.Label}:{map}"); continue; }
                    string stem = $"{g.Label.ToLowerInvariant()}_{Lower(Path.GetFileNameWithoutExtension(world.TerrainPath))}";
                    var e = WriteTerrain(world, Path.Combine(outDir, "terrain"), stem);
                    if (e is null) { skipped.Add(new JsonObject { ["game"] = g.Label, ["kind"] = "terrain", ["name"] = map, ["reason"] = "no terrain tiles" }); continue; }
                    e["games"] = new JsonArray(g.Label);
                    e["terrain_file"] = Path.GetFileName(world.TerrainPath);
                    e["maps"] = new JsonArray($"{g.Label}:{map}");
                    done[hash] = e;
                    all.Add(e);
                    Console.WriteLine($"terrain {stem}: {e["triangles"]} triangles");
                }
        return all;
    }

    /// <summary>
    /// A map's terrain: a vertex per height sample (5 m apart) over its occupied tiles, the raw heights times 0.1 m, each cell cut
    /// along its diagonal of nearer heights as the game's 1997 look draws it; a material per surface type (the sample's top nibble), in
    /// a plain colour. Positions are the map's own world coordinates (the same frame as the objects: z negated).
    /// </summary>
    private static JsonObject? WriteTerrain(MissionWorld world, string dir, string stem)
    {
        var t = world.Terrain;
        var (r0, r1, c0, c1) = t.Bounds();
        if (r1 < r0) return null;
        int sx0 = c0 * TerrainTiles.Tile, sx1 = (c1 + 1) * TerrainTiles.Tile - 1, sz0 = r0 * TerrainTiles.Tile, sz1 = (r1 + 1) * TerrainTiles.Tile - 1;
        int w = sx1 - sx0 + 1;
        var index = new int[(sz1 - sz0 + 1) * w];
        Directory.CreateDirectory(dir);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        int verts = 0, tris = 0;
        var surfaces = new SortedSet<int>();
        using var o = new StreamWriter(Path.Combine(dir, stem + ".obj")) { NewLine = "\n" };
        o.WriteLine($"# terrain {Path.GetFileName(world.TerrainPath)}: 5 m height samples, metres, right-handed +Y up (the game's z negated)");
        o.WriteLine($"mtllib {stem}.mtl");
        var heights = new float[index.Length];
        var surf = new int[index.Length];
        for (int sz = sz0; sz <= sz1; sz++)
            for (int sx = sx0; sx <= sx1; sx++)
            {
                int i = (sz - sz0) * w + (sx - sx0);
                if (t.Sample(sx, sz) is not { } s) { index[i] = -1; continue; }
                heights[i] = (s & MissionFile.HeightMask) * (float)MissionWorld.HeightScale;
                surf[i] = s >> 12;
                index[i] = verts++;
                o.WriteLine(string.Create(ic, $"v {sx * 5} {heights[i]:0.#} {-sz * 5}"));
            }
        var faces = new Dictionary<int, StringBuilder>();
        for (int sz = sz0; sz < sz1; sz++)
            for (int sx = sx0; sx < sx1; sx++)
            {
                int a = (sz - sz0) * w + (sx - sx0), b = a + 1, c = a + w, d = c + 1;
                if (index[a] < 0 || index[b] < 0 || index[c] < 0 || index[d] < 0) continue;
                surfaces.Add(surf[a]);
                if (!faces.TryGetValue(surf[a], out var sb)) faces[surf[a]] = sb = new StringBuilder();
                // seen from above (+Y), with z negated: a (x, -z), b (x+5, -z), c (x, -z-5), d (x+5, -z-5); counter-clockwise from above
                if (MathF.Abs(heights[a] - heights[d]) < MathF.Abs(heights[b] - heights[c]))
                {
                    sb.Append($"f {index[a] + 1} {index[d] + 1} {index[c] + 1}\nf {index[a] + 1} {index[b] + 1} {index[d] + 1}\n");
                }
                else
                {
                    sb.Append($"f {index[a] + 1} {index[b] + 1} {index[c] + 1}\nf {index[b] + 1} {index[d] + 1} {index[c] + 1}\n");
                }
                tris += 2;
            }
        foreach (var (s, sb) in faces.OrderBy(k => k.Key))
        {
            o.WriteLine($"g surface{s}");
            o.WriteLine($"usemtl surface{s}");
            o.Write(sb);
        }
        using (var m = new StreamWriter(Path.Combine(dir, stem + ".mtl")))
            MtlWriter.Write(m, surfaces.Select(s => new ObjMaterial { Name = $"surface{s}", Colour = SurfaceColour(s), Note = $"terrain surface type {s}" }));
        return new JsonObject
        {
            ["obj"] = $"terrain/{stem}.obj",
            ["vertices"] = verts,
            ["triangles"] = tris,
            ["sample_spacing_m"] = 5,
            ["x_range_m"] = new JsonArray(sx0 * 5, sx1 * 5),
            ["z_range_m_game"] = new JsonArray(sz0 * 5, sz1 * 5),
            ["surfaces"] = new JsonArray([.. surfaces.Select(s => (JsonNode)s)]),
        };
    }

    /// <summary>A plain colour per surface type, only to tell them apart.</summary>
    private static Vector3 SurfaceColour(int s)
    {
        float h = (s * 0.618034f) % 1f;
        float r = Math.Clamp(MathF.Abs(h * 6 - 3) - 1, 0, 1), gg = Math.Clamp(2 - MathF.Abs(h * 6 - 2), 0, 1), b = Math.Clamp(2 - MathF.Abs(h * 6 - 4), 0, 1);
        return new Vector3(0.35f + 0.4f * r, 0.35f + 0.4f * gg, 0.35f + 0.4f * b);
    }
}
