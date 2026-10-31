using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Editor.Objects;

/// <summary>The palette's groups. <see cref="Scripted"/> holds what only a mission script drives (vehicles, gun emplacements, helicopters, gates).</summary>
public enum ObjectCategory { Buildings, Structures, Roadside, Props, Vegetation, Fences, Markers, Scripted }

/// <summary>What a marker item places (the keyword objects the loaders recognise by name).</summary>
public enum MarkerKind { None, Spawn, Regen, Checkpoint, Flag }

/// <summary>
/// One placeable thing: a scenery definition (or a marker) with the ODEF type the stock maps give it, learned from what they
/// place (<see cref="ObjectCatalogue.Scan"/>), and its model's box. Nothing here is game data: names and numbers are read from
/// the player's install at run time and cached.
/// </summary>
public sealed class CatalogueItem
{
    public string Name { get; set; } = "";
    public ObjectCategory Category { get; set; }
    /// <summary>The ODEF type the stock maps most often give this name (+0x5c).</summary>
    public uint Type { get; set; } = 2;
    /// <summary>Every type the stock maps give it (a pole is breakable (3) in one map and solid (2) in another).</summary>
    public uint[] TypesSeen { get; set; } = [];
    public string Definition { get; set; } = "";
    public int Placed { get; set; }
    public int Maps { get; set; }
    /// <summary>Placed by a melee, race or CTF map (not only by campaign missions; the base game's melee arenas count).</summary>
    public bool InMelee { get; set; }
    public bool InCampaign { get; set; }
    /// <summary>The model is in the game's data but no stock map places it (<see cref="ObjectCatalogue.Unplaced"/>): the type is the Asset Bible's class, not learned from a map.</summary>
    public bool NotInStockMaps { get; set; }
    /// <summary>Only the Nitro Pack's maps place it (this install is the base game, whose data has the model: <see cref="ObjectCatalogue.NitroPackStructures"/>).</summary>
    public bool NitroMapsOnly { get; set; }
    /// <summary>
    /// A tunnel (betunnl1-3: a mountain with a tube through it): the tube's axis in the model's frame, from its TUN part: the middle x it runs along,
    /// the z of its two portals and its width. All 0 for anything else. The stock maps lay the road straight through it, ground levelled under the mountain.
    /// </summary>
    public float TunnelX { get; set; }
    public float TunnelZ0 { get; set; }
    public float TunnelZ1 { get; set; }
    public float TunnelWidth { get; set; }
    public bool IsTunnel => TunnelZ1 > TunnelZ0;
    public MarkerKind Marker { get; set; }
    /// <summary>The model's box in its own frame (the origin is usually a corner: x and z run 0 to the size).</summary>
    public float MinX { get; set; }
    public float MinY { get; set; }
    public float MinZ { get; set; }
    public float MaxX { get; set; }
    public float MaxY { get; set; }
    public float MaxZ { get; set; }

    /// <summary>
    /// For a structure that carries a road: the ends of its drive-on surface in the model's own frame (<c>StructureAttach.Ends</c> over the
    /// sim's collider, filled in by the game when the item is armed; not cached). Null until then.
    /// </summary>
    [JsonIgnore] public IReadOnlyList<StructureAttach.Attach>? Ends { get; set; }

    /// <summary>The ends to snap with: the measured ones, else the middles of the box's near and far faces (the models run along z).</summary>
    public IReadOnlyList<StructureAttach.Attach> DeckEnds()
    {
        if (Ends is { Count: 2 }) return Ends;
        if (IsTunnel)
            return
            [
                new StructureAttach.Attach(new Vector3(TunnelX, 0, TunnelZ0), new Vector2(0, -1), TunnelWidth, true),
                new StructureAttach.Attach(new Vector3(TunnelX, 0, TunnelZ1), new Vector2(0, 1), TunnelWidth, false),
            ];
        float cx = (MinX + MaxX) / 2;
        return
        [
            new StructureAttach.Attach(new Vector3(cx, 0, MinZ), new Vector2(0, -1), MaxX - MinX, true),
            new StructureAttach.Attach(new Vector3(cx, 0, MaxZ), new Vector2(0, 1), MaxX - MinX, false),
        ];
    }

    public Vector3 Min => new(MinX, MinY, MinZ);
    public Vector3 Max => new(MaxX, MaxY, MaxZ);
    public float Width => MaxX - MinX;
    public float Depth => MaxZ - MinZ;
    public float Height => MaxY - MinY;
    public float Area => Width * Depth;
    /// <summary>A structure that carries a road (a bridge, ramp or tunnel): the road meets its ends.</summary>
    public bool CarriesRoad => Category == ObjectCategory.Structures;
    /// <summary>The road is cut where this structure carries it (a bridge's or ramp's deck is the road there); a tunnel is laid over the road, which runs through it.</summary>
    public bool CutsRoad => CarriesRoad && !IsTunnel;
    /// <summary>Needs the mission script to do anything: placed, it is inert in a melee.</summary>
    public bool NeedsScript => Category == ObjectCategory.Scripted;
    /// <summary>Bridges are looked up by the road AI only when they lie along an axis (the engine logs "bridge not orthogonal" for a slanted one).</summary>
    public bool AxisAligned => Type == 0xc;
    /// <summary>The Complete Asset Bible's description of the object, or null.</summary>
    [JsonIgnore] public string? Description => Marker == MarkerKind.None ? AssetBible.Describe(Name) : null;
    /// <summary>What the picker calls it: the bible's description, else the code name in capitals.</summary>
    [JsonIgnore] public string Title => Description ?? Label;
    public string Label => Marker switch
    {
        MarkerKind.Spawn => "SPAWN",
        MarkerKind.Regen => "REGEN",
        MarkerKind.Checkpoint => "CHECKPOINT",
        MarkerKind.Flag => Name.ToUpperInvariant().Replace("A1FLAG", "FLAG "),
        _ => Name.ToUpperInvariant(),
    };

    public string Tooltip()
    {
        if (Marker != MarkerKind.None) return Label;
        string kind = Category == ObjectCategory.Scripted ? "needs a mission script" : NotInStockMaps ? "not in any stock map" : NitroMapsOnly ? "placed by the Nitro Pack's maps only" : InMelee ? "used by melee maps" : "campaign missions only";
        string placed = NotInStockMaps || NitroMapsOnly ? $"type {Type:x}" : $"type {Type:x}, placed {Placed} times in {Maps} maps";
        return $"{(Description is { } d ? d + "\n" : "")}{Name.ToUpperInvariant()}\n{Width:0.#} x {Depth:0.#} m, {Height:0.#} m high\n{placed}\n{kind}";
    }
}

/// <summary>The catalogue of placeable things, built from the install's own maps and cached.</summary>
public static class ObjectCatalogue
{
    public const int CacheVersion = 5;

    /// <summary>Fixed marker items, placed with the marker tools.</summary>
    public static IEnumerable<CatalogueItem> MarkerItems()
    {
        yield return new CatalogueItem { Name = "spawn", Category = ObjectCategory.Markers, Marker = MarkerKind.Spawn, Type = 1, InMelee = true };
        yield return new CatalogueItem { Name = "regen", Category = ObjectCategory.Markers, Marker = MarkerKind.Regen, Type = 1, InMelee = true };
        yield return new CatalogueItem { Name = "check1", Category = ObjectCategory.Markers, Marker = MarkerKind.Checkpoint, Type = 1, InMelee = true };
        for (int t = 1; t <= 4; t++)
            yield return new CatalogueItem { Name = $"a1flag{t}", Definition = $"a1flag{t}.sdf", Category = ObjectCategory.Markers, Marker = MarkerKind.Flag, Type = 2, InMelee = true };
    }

    /// <summary>Which palette group a name belongs in, from its ODEF type, its prefix (the stock naming) and its size; null for what is not placed by hand (road pieces, the player's car).</summary>
    public static ObjectCategory? Classify(string name, uint type, bool vehicle, float width, float depth)
    {
        string n = name.ToLowerInvariant();
        if ((type & 0xf0) == 0x50 || Intersections.PieceCatalogue.Lookup(name) is not null) return null;   // intersections, runway ends: painted into the roads, made by the road tool
        if (vehicle || type is 1 or 7 or 8 or 9) return ObjectCategory.Scripted;
        if (n.StartsWith("afence") || n.StartsWith("bcaikwl") || n.StartsWith("bhyceme") && n[^1] <= '3') return ObjectCategory.Fences;   // the cemetery's gate and walls; its graves are props
        if (n.StartsWith("aaramp") || n.StartsWith("be") || type == 0xc) return ObjectCategory.Structures;
        if (n[0] == 'n') return ObjectCategory.Vegetation;
        if (n[0] == 's' && n.Length >= 4 && n[^2] == '_' ) return ObjectCategory.Roadside;      // road signs sXXXX_1
        if (type == 4) return ObjectCategory.Roadside;
        if (n.StartsWith("atpole") || n.StartsWith("bczlite") || n.StartsWith("aphone") || n.StartsWith("aobilbd") || n.StartsWith("adumps") || n.StartsWith("augate")) return ObjectCategory.Roadside;
        if (n[0] == 'b' && width * depth >= 30) return ObjectCategory.Buildings;
        return ObjectCategory.Props;
    }

    /// <summary>
    /// Reads every mission of the install (miss16\ and addon\), counts what each places (name, ODEF type) and what kind of map it is,
    /// measures the models, and returns the placeable items by category and name. The player's cars, road pieces and the
    /// objects with no definition are left out.
    /// </summary>
    public static List<CatalogueItem> Scan(GameInstall install, ModelAssembler asm, Action<string>? log = null)
    {
        var rows = new Dictionary<string, (Dictionary<uint, int> Types, int Placed, HashSet<string> Maps, bool Melee, bool Campaign)>(StringComparer.OrdinalIgnoreCase);
        var dirs = new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct();
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapDocument doc;
                try { doc = MapDocument.Load(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { continue; }
                string ext = Path.GetExtension(f).ToLowerInvariant();
                string map = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                // .msn is the campaign's, except the base game's own melee arenas (m01-m15, which are .msn too)
                bool campaign = ext == ".msn" && !(install.Profile == GameProfile.I76 && Array.Exists(MissionTitles.BaseArenas, a => a.Msn == map));
                foreach (var o in doc.Objects)
                {
                    string n = o.Name.TrimStart('#').ToLowerInvariant();
                    if (n.Length == 0 || o.IsPlayer || o.Role != MapRole.Object) continue;
                    if (!rows.TryGetValue(n, out var r)) rows[n] = r = ([], 0, [], false, false);
                    r.Types[o.ObjectType] = r.Types.GetValueOrDefault(o.ObjectType) + 1;
                    r.Maps.Add(map);
                    rows[n] = (r.Types, r.Placed + 1, r.Maps, r.Melee || !campaign, r.Campaign || campaign);
                }
            }
        var items = new List<CatalogueItem>();
        var unplaced = new List<CatalogueItem>();
        foreach (var n in UnplacedNames(asm.Vfs, rows.Keys)) log?.Invoke($"unplaced: {n}");
        foreach (var (n, type) in Unplaced)
            if (!rows.ContainsKey(n) && asm.Vfs.Exists(n + ".sdf") && asm.Assemble(n + ".sdf") is { Parts.Count: > 0 } um)
            {
                var umn = new Vector3(float.MaxValue); var umx = new Vector3(float.MinValue);
                foreach (var p in um.Parts)
                    foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); umn = Vector3.Min(umn, t); umx = Vector3.Max(umx, t); }
                if (Classify(n, type, false, umx.X - umn.X, umx.Z - umn.Z) is { } ucat)
                {
                    var tun = TunnelOf(um);
                    unplaced.Add(new CatalogueItem
                    {
                        TunnelX = tun?.X ?? 0, TunnelZ0 = tun?.Z0 ?? 0, TunnelZ1 = tun?.Z1 ?? 0, TunnelWidth = tun?.Width ?? 0,
                        Name = n, Category = ucat, Type = type, TypesSeen = [type], Definition = n + ".sdf", NotInStockMaps = true,
                        MinX = umn.X, MinY = umn.Y, MinZ = umn.Z, MaxX = umx.X, MaxY = umx.Y, MaxZ = umx.Z,
                    });
                }
            }
        foreach (var (n, (type, melee)) in NitroPackStructures)
            if (!rows.ContainsKey(n) && asm.Vfs.Exists(n + ".sdf") && asm.Assemble(n + ".sdf") is { Parts.Count: > 0 } nm)
            {
                var nmn = new Vector3(float.MaxValue); var nmx = new Vector3(float.MinValue);
                foreach (var p in nm.Parts)
                    foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); nmn = Vector3.Min(nmn, t); nmx = Vector3.Max(nmx, t); }
                if (Classify(n, type, false, nmx.X - nmn.X, nmx.Z - nmn.Z) is { } ncat)
                {
                    var tun = TunnelOf(nm);
                    unplaced.Add(new CatalogueItem
                    {
                        TunnelX = tun?.X ?? 0, TunnelZ0 = tun?.Z0 ?? 0, TunnelZ1 = tun?.Z1 ?? 0, TunnelWidth = tun?.Width ?? 0,
                        Name = n, Category = ncat, Type = type, TypesSeen = [type], Definition = n + ".sdf", NitroMapsOnly = true, InMelee = melee, InCampaign = !melee,
                        MinX = nmn.X, MinY = nmn.Y, MinZ = nmn.Z, MaxX = nmx.X, MaxY = nmx.Y, MaxZ = nmx.Z,
                    });
                }
            }
        foreach (var (n, r) in rows.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var types = r.Types.Where(t => t.Key is >= 1 and <= 0x5f).OrderByDescending(t => t.Value).ToList();   // the stray records with float-looking types are not objects
            if (types.Count == 0) continue;
            uint type = types[0].Key;
            if (asm.ResolveDefinition(n) is not { } def) continue;
            Vector3 mn = default, mx = default;
            bool veh = def.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase);
            if (asm.Assemble(def) is { Parts.Count: > 0 } m)
            {
                mn = new Vector3(float.MaxValue); mx = new Vector3(float.MinValue);
                foreach (var p in m.Parts)
                    foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); mn = Vector3.Min(mn, t); mx = Vector3.Max(mx, t); }
            }
            else continue;
            if (Classify(n, type, veh, mx.X - mn.X, mx.Z - mn.Z) is not { } cat) continue;
            var tun = TunnelOf(asm.Assemble(def)!);
            items.Add(new CatalogueItem
            {
                TunnelX = tun?.X ?? 0, TunnelZ0 = tun?.Z0 ?? 0, TunnelZ1 = tun?.Z1 ?? 0, TunnelWidth = tun?.Width ?? 0,
                Name = n, Category = cat, Type = type, TypesSeen = [.. types.Select(t => t.Key).OrderBy(t => t)], Definition = def,
                Placed = r.Placed, Maps = r.Maps.Count, InMelee = r.Melee, InCampaign = r.Campaign,
                MinX = mn.X, MinY = mn.Y, MinZ = mn.Z, MaxX = mx.X, MaxY = mx.Y, MaxZ = mx.Z,
            });
        }
        items.AddRange(unplaced);
        // the flags have models of their own: give the marker items their size
        foreach (var mk in MarkerItems())
        {
            if (mk.Marker == MarkerKind.Flag && items.FirstOrDefault(i => i.Name == mk.Name) is { } flag) { flag.Category = ObjectCategory.Markers; flag.Marker = MarkerKind.Flag; continue; }
            items.Add(mk);
        }
        log?.Invoke($"object catalogue: {items.Count} items from {rows.Count} names");
        return items.OrderBy(i => i.Category).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Models in the game's data that no stock map places, each with the ODEF type the Asset Bible gives its class (Struct1 solid 2,
    /// Struct2 breakable 3, SIGN 4, Ramp 0xb, a fence is a string of solid pieces 2), checked in the editor and the game (docs/map-editor.md).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, uint> Unplaced = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["a3pline1"] = 3, ["abarrel1"] = 3, ["actrash1"] = 3, ["aitire1"] = 3, ["nadillo1"] = 3,
        ["aaramp6"] = 0xb, ["afence1"] = 2, ["afence2"] = 2, ["afence3"] = 2,
        ["bcaik1"] = 2, ["bcaik3"] = 2, ["bcaik5"] = 2, ["bcaik7"] = 2, ["bcaik_s2"] = 2, ["bcaik_s5"] = 2, ["bcaik_s6"] = 2, ["bcaik_s7"] = 2, ["bcaikwl6"] = 2,
        ["bczlite2"] = 3, ["betunnl2"] = 2, ["betunnl3"] = 2, ["bfdepot1"] = 2,
        ["bhyceme1"] = 2, ["bhyceme2"] = 2, ["bhyceme3"] = 2, ["bhyceme4"] = 2, ["bhyceme5"] = 2, ["bhyceme6"] = 2,
        ["npinetr2"] = 3, ["ntree1"] = 3, ["ntree2"] = 3, ["ntree3"] = 3, ["ntree4"] = 3,
        ["scros_1"] = 4, ["sedon_1"] = 4, ["sh38e_2"] = 4, ["sh38w_2"] = 4, ["sh82e_2"] = 4, ["sh82w_2"] = 4, ["sj13l_1"] = 4, ["sj13r_1"] = 4,
        ["sj21l_1"] = 4, ["sj21r_1"] = 4, ["sj38l_1"] = 4, ["sj38r_1"] = 4, ["sl55x_2"] = 4, ["smcar_1"] = 4, ["swbrn_1"] = 4,
        ["swfed_1"] = 4, ["swrop_1"] = 4, ["swwht_1"] = 4,
    };

    /// <summary>The models that are in the data and unplaced but left out of the palette, with why (the Asset Bible's notes, and what trying them in the editor and the game showed).</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["aaramp8"] = "Asset Bible: a ramp that never appears; its definition assembles to no geometry",
        ["agstlgt1"] = "Asset Bible: a street light that is part of a combined object, not for single use; assembles to no geometry",
        ["swarn_1"] = "its definition assembles to no geometry (the bible: a warning sign, probably replaced by the 'danger dog on duty' one)",
        ["axlot1"] = "Asset Bible: a parking lot that is part of the surface texture (TM151AS_.MAP), not usable as an object",
        ["axlot2"] = "Asset Bible: as axlot1",
        ["ngscrng1"] = "Asset Bible: scrounge, not an object; the World Defs name it",
        ["ngscrng2"] = "Asset Bible: as ngscrng1",
    };

    /// <summary>
    /// The road-carrying structures only the Nitro Pack's stock maps place, which the base game's data has too (the model files are the same):
    /// the palette of a base-game install lists them (type, and whether the Nitro Pack places it in a melee map, not only a mission).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (uint Type, bool Melee)> NitroPackStructures = new Dictionary<string, (uint, bool)>(StringComparer.Ordinal)
    {
        ["bebridga"] = (0xc, true), ["bebridgb"] = (0xc, true), ["bewdbrg1"] = (0xc, true), ["bewdbrg2"] = (0xc, true),
        ["aaramp2"] = (0xb, true), ["aaramp7"] = (0xb, true), ["aarampa"] = (0xb, true), ["aarampb"] = (0xb, true), ["betunnl1"] = (2, false),
    };

    /// <summary>A tunnel model's tube: the box of its TUN part (the mountain MTN, the portals AD and the kerbs CR are its other parts), or null for a model without one.</summary>
    public static (float X, float Z0, float Z1, float Width)? TunnelOf(AssembledModel m)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var p in m.Parts.Where(p => p.NodeName.Contains("_TUN", StringComparison.OrdinalIgnoreCase)))
            foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); mn = Vector3.Min(mn, t); mx = Vector3.Max(mx, t); }
        return mx.Z > mn.Z && mx.X > mn.X ? ((mn.X + mx.X) / 2, mn.Z, mx.Z, mx.X - mn.X) : null;
    }

    /// <summary>The scenery models the data holds that no map places: every .sdf whose name is not in <paramref name="placed"/>, apart from the intersections (the intersection tool's) and the flags.</summary>
    public static IEnumerable<string> UnplacedNames(AssetVfs vfs, IEnumerable<string> placed)
    {
        var have = new HashSet<string>(placed, StringComparer.OrdinalIgnoreCase);
        return vfs.Names.Where(n => n.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase)).Select(n => n[..^4].ToLowerInvariant())
            .Where(n => !have.Contains(n) && n[0] != 'i' && !n.StartsWith("a1flag")).Distinct().Order(StringComparer.Ordinal);
    }

    /// <summary>Cached or scanned: <paramref name="cachePath"/> holds the JSON for this install (its key says which); scanned again when missing or stale.</summary>
    public static List<CatalogueItem> LoadOrScan(GameInstall install, ModelAssembler asm, string cachePath, Action<string>? log = null)
    {
        string key = CacheKey(install);
        try
        {
            if (File.Exists(cachePath))
            {
                var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath));
                if (c is { } && c.Key == key && c.Version == CacheVersion && c.Items.Count > 0) { log?.Invoke($"object catalogue: {c.Items.Count} items from the cache"); return c.Items; }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        var items = Scan(install, asm, log);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheFile { Key = key, Version = CacheVersion, Items = items }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return items;
    }

    /// <summary>A short stable name for an install's cache folder and file (string.GetHashCode changes from run to run).</summary>
    public static string CacheName(GameInstall install) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(CacheKey(install))))[..12].ToLowerInvariant();

    public sealed class CacheFile
    {
        public string Key { get; set; } = "";
        public int Version { get; set; }
        public List<CatalogueItem> Items { get; set; } = [];
    }

    /// <summary>Changes when the install or the maps in it do (the archive's size and date, the number and newest date of the stock mission files; the player's own maps in addon\ do not, so saving a map does not rescan).</summary>
    public static string CacheKey(GameInstall install)
    {
        long z = 0, newest = 0; int count = 0;
        try { var fi = new FileInfo(install.ZfsPath); z = fi.Length ^ fi.LastWriteTimeUtc.Ticks; } catch (IOException) { }
        if (install.MissionDir is { } md)
            foreach (var f in Directory.EnumerateFiles(md).Where(GameInstall.IsMissionFile))
            { count++; newest = Math.Max(newest, File.GetLastWriteTimeUtc(f).Ticks); }
        return $"{install.Profile}-{z:x}-{count}-{newest:x}";
    }
}
