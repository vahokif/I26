using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// Needs the community's Nitro map pack unzipped (I76_CUSTOM_MAPS: the folder holding its miss16/, miss8/ and addon/)
/// and the Nitro install (I76_NITRO_INSTALL). Tests only: the game itself reads custom maps from the install's own
/// folders. Third-party content: never commit any of it.
/// </summary>
public sealed class CustomMapsFactAttribute : FactAttribute
{
    public static string? Dir =>
        Environment.GetEnvironmentVariable("I76_CUSTOM_MAPS") is { Length: > 0 } d && Directory.Exists(Path.Combine(d, "miss16")) ? d : null;

    public CustomMapsFactAttribute()
    {
        if (Dir is null || NitroFactAttribute.Dir is null)
            Skip = "set I76_CUSTOM_MAPS to the unzipped community map pack (and I76_NITRO_INSTALL) to run";
    }
}

/// <summary>
/// Needs base Interstate '76 community maps unzipped under one folder (I76_CUSTOM_MAPS_I76: each map's .lvl and .ter,
/// in a folder per map or not) and the base install (I76_INSTALL). Tests only; third-party content: never commit it.
/// </summary>
public sealed class BaseMapsFactAttribute : FactAttribute
{
    public static string? Dir =>
        Environment.GetEnvironmentVariable("I76_CUSTOM_MAPS_I76") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public BaseMapsFactAttribute()
    {
        if (Dir is null || TestInstall.Dir is null) Skip = "set I76_CUSTOM_MAPS_I76 to unzipped base-game maps (and I76_INSTALL) to run";
    }
}

/// <summary>
/// A scratch install under the temp directory: links to every top-level entry of a real install (Nitro or base), except
/// miss16, miss8 and addon, which are real directories of links to the stock files and a pack's; extra files can go into
/// addon or a folder inside it. Nothing is ever written through a link; disposing removes the links one by one, never
/// recursively.
/// </summary>
public sealed class ScratchInstall : IDisposable
{
    public string Root { get; }
    public GameInstall Install { get; }

    public ScratchInstall(string install, string? pack, IEnumerable<(string Folder, string File)>? addon = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"i76-custommaps-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        string[] merged = ["miss16", "miss8", "addon"];
        foreach (var e in Directory.EnumerateFileSystemEntries(install))
        {
            var name = Path.GetFileName(e);
            if (merged.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var dst = Path.Combine(Root, name);
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(dst, e); else File.CreateSymbolicLink(dst, e);
        }
        foreach (var m in merged)
        {
            var own = Find(install, m);
            var dir = Path.Combine(Root, own is null ? m : Path.GetFileName(own));
            Directory.CreateDirectory(dir);
            foreach (var src in new[] { own, pack is null ? null : Find(pack, m) })
                if (src is not null)
                    foreach (var f in Directory.EnumerateFiles(src)) Link(dir, f);
            if (m == "addon" && addon is not null)
                foreach (var (folder, f) in addon)
                {
                    var d = folder.Length == 0 ? dir : Path.Combine(dir, folder);
                    Directory.CreateDirectory(d);
                    Link(d, f);
                }
        }
        Install = new GameInstall(Root);
    }

    private static void Link(string dir, string file)
    {
        var dst = Path.Combine(dir, Path.GetFileName(file));
        if (!File.Exists(dst)) File.CreateSymbolicLink(dst, file);
    }

    private static string? Find(string root, string name) =>
        Directory.EnumerateDirectories(root).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (var e in Directory.EnumerateFileSystemEntries(Root)) Remove(e, 2);
        Directory.Delete(Root);
    }

    /// <summary>A link is unlinked; a real directory (miss16, addon, a folder in addon) is emptied of links, depth levels down, then removed.</summary>
    private static void Remove(string e, int depth)
    {
        if (new FileInfo(e).LinkTarget is not null) { if (Directory.Exists(e)) Directory.Delete(e); else File.Delete(e); return; }
        if (!Directory.Exists(e) || depth == 0) return;
        foreach (var c in Directory.EnumerateFileSystemEntries(e)) Remove(c, depth - 1);
        Directory.Delete(e);
    }
}

public class CustomMapTests(ITestOutputHelper output)
{
    private static string Pack(params string[] parts)
    {
        string cur = CustomMapsFactAttribute.Dir!;
        foreach (var p in parts)
            cur = Directory.EnumerateFileSystemEntries(cur).First(e => Path.GetFileName(e).Equals(p, StringComparison.OrdinalIgnoreCase));
        return cur;
    }

    [Fact]
    public void CrcIsTheShellsBzip2Crc()
    {
        // the CRC-32/BZIP2 check value
        Assert.Equal(0xfc891918u, MapFingerprint.Crc("123456789"u8));
        Assert.Equal(0u, MapFingerprint.Crc([]));
    }

    [Fact]
    public void ShortTerrainPadsItsMissingTilesWithFlatGround()
    {
        var grid = Enumerable.Repeat(MissionFile.ZmapEmpty, MissionFile.ZmapGrid * MissionFile.ZmapGrid).ToArray();
        grid[0] = 0; grid[1] = 1; grid[2] = 2; grid[3] = 7;   // tile 7: past the count, read as empty
        var ter = new byte[MissionFile.TerrainTileBytes];   // one tile of three
        ter[0] = 0x34; ter[1] = 0x12;
        var t = new TerrainTiles(ter, 3, grid);
        Assert.Equal(2, t.MissingTiles);
        Assert.Equal((ushort)0x1234, t.Sample(0, 0));
        Assert.Equal((ushort)0, t.Sample(128, 0));
        Assert.Null(t.Sample(3 * 128, 0));
        Assert.Equal(MissionFile.ZmapEmpty, t.TileAt(0, 3));
        // a longer file: the extra bytes are not tiles
        Assert.Equal(0, new TerrainTiles(new byte[MissionFile.TerrainTileBytes * 2 + 7], 1, grid).MissingTiles);
    }

    [CustomMapsFact]
    public void ZoneNamesTheTerrainAndALevelKeepsTheOldWorldLayout()
    {
        // ZONE, not the stem: ofjuicy_8.cbt plays on ofjuicy.ter
        Assert.Equal("ofjuicy.ter", new MissionFile(Pack("miss16", "ofjuicy_8.cbt")).TerrainName());
        Assert.Equal("elim2.ter", new MissionFile(Pack("miss16", "elim2.cbt")).TerrainName());

        // a .lvl is WREV 8: no horizon, everything after it 13 bytes earlier; the name where its author put it
        var lvl = new MissionFile(Pack("miss16", "r66rcway.lvl"));
        var rac = new MissionFile(Pack("miss16", "r66rcway.rac"));
        Assert.Equal(8u, lvl.WorldRevision());
        Assert.Equal(9u, rac.WorldRevision());
        Assert.Equal("R66 Raceway", WorldSettings.ArenaName(lvl));
        Assert.Equal("R66 Raceway", WorldSettings.ArenaName(rac));
        var a = WorldSettings.Read(lvl)!;
        var b = WorldSettings.Read(rac)!;
        Assert.Equal("", a.Horizon);
        Assert.Equal("n02_16.HZD", b.Horizon);
        Assert.Equal(15, a.Hour);
        Assert.Equal(b.Unk_0x97, a.Unk_0x97);       // the same surface table, read from its own offset
        Assert.Equal(b.Unk_0x137, a.Unk_0x137);
        Assert.Equal("t14.ACT", a.Palette);
    }

    [CustomMapsFact]
    public void TheTerrainIsFoundInAddonOrMiss16AndAShortOneIsPadded()
    {
        using var s = new ScratchInstall(NitroFactAttribute.Dir!, CustomMapsFactAttribute.Dir!);
        var i = s.Install;
        // canyonlx.rac's terrain is only in addon\ (the engine opens addon\canyonlx.ter, measured)
        var canyon = new MissionFile(i.FindMission("canyonlx.rac")!);
        Assert.EndsWith(Path.Combine("addon", "canyonlx.ter"), MissionWorld.FindTerrain(i, canyon));
        // h2h.rac's ZONE names h2h16.ter, which nobody shipped: the port falls back to its stem, addon\h2h.ter
        var h2h = new MissionFile(i.FindMission("h2h.rac")!);
        Assert.Equal("h2h16.ter", h2h.TerrainName());
        Assert.EndsWith(Path.Combine("addon", "h2h.ter"), MissionWorld.FindTerrain(i, h2h));
        // obsid.cbt: ZMAP counts ten tiles, its .ter holds one
        var obsid = new MissionFile(i.FindMission("obsid")!);
        var ter = TerrainTiles.Load(obsid, MissionWorld.FindTerrain(i, obsid)!);
        Assert.Equal(9, ter.MissingTiles);
        // the shell's lists: miss16 then addon, a .cbt and a .rac of one stem both there
        var cbt = i.ListMissions(".cbt").Select(Path.GetFileName).ToList();
        Assert.Contains("ofjuicy_8.cbt", cbt);
        Assert.Contains("elim2.cbt", cbt);
        Assert.Contains("ofjuicy_8.rac", i.ListMissions(".rac").Select(Path.GetFileName));
        Assert.True(cbt.IndexOf("N01.CBT") >= 0);
        // the fingerprint: a copy of the map matches itself, a missing one says so
        var fp = MapFingerprint.Of(i, "elim2.cbt")!;
        Assert.Null(MapFingerprint.Mismatch(i, "elim2.cbt", fp.MissionSize, fp.MissionCrc, fp.TerrainSize, fp.TerrainCrc));
        Assert.Contains("does not match", MapFingerprint.Mismatch(i, "elim2.cbt", fp.MissionSize, fp.MissionCrc ^ 1, fp.TerrainSize, fp.TerrainCrc));
        Assert.Contains("terrain", MapFingerprint.Mismatch(i, "elim2.cbt", fp.MissionSize, fp.MissionCrc, fp.TerrainSize + 1, fp.TerrainCrc));
        Assert.Contains("not in your game", MapFingerprint.Mismatch(i, "nosuchmap.cbt", 1, 2, 3, 4));
    }

    private static bool IsMarker(string name)
    {
        var n = name.TrimStart('#');
        return name.Length == 0 || new[] { "spawn", "regen", "check" }.Any(k => n.StartsWith(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The pack's maps that still do not play, and why. Every other map in its miss16 must load and run.</summary>
    private static readonly Dictionary<string, string> KnownFailures = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    /// <summary>
    /// Every mission file in the pack's miss16 (.cbt, .rac, .cf2-4, .msn, .lvl), from a scratch install of the Nitro
    /// install plus the pack: the world (terrain by its ZONE name, placements), the melee setup with two A.I. cars, and
    /// two seconds of simulation. Prints a table of what each needed; fails on any map not in <see cref="KnownFailures"/>.
    /// </summary>
    [CustomMapsFact]
    public void EveryPackMapLoadsAndRuns()
    {
        using var s = new ScratchInstall(NitroFactAttribute.Dir!, CustomMapsFactAttribute.Dir!);
        var vfs = new AssetVfs(s.Install);
        var asm = new ModelAssembler(vfs);
        var files = Directory.EnumerateFiles(Pack("miss16")).Where(GameInstall.IsMissionFile).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var failures = new List<string>();
        int ok = 0, padded = 0, fallback = 0, unresolvedMaps = 0, missingRefs = 0;
        foreach (var f in files)
        {
            var (why, notes, flags) = TryLoad(s.Install, vfs, asm, f!);
            if (why is null) ok++;
            if (flags.Padded) padded++;
            if (flags.Fallback) fallback++;
            if (flags.Unresolved) unresolvedMaps++;
            if (flags.MissingRefs) missingRefs++;
            output.WriteLine($"{f,-18} {(why is null ? "ok" : "FAIL " + why)}{(notes.Count > 0 ? "  [" + string.Join("; ", notes) + "]" : "")}");
            if (why is not null && !KnownFailures.ContainsKey(f!)) failures.Add($"{f}: {why}");
            if (why is null && KnownFailures.ContainsKey(f!)) failures.Add($"{f}: loads now; take it off the known failures");
        }
        output.WriteLine($"{ok}/{files.Count} loaded and ran; {padded} padded terrains, {fallback} ZONE fallbacks, {unresolvedMaps} with unresolved objects, {missingRefs} with missing WRLD files");
        Assert.True(files.Count > 250, $"only {files.Count} pack maps found");
        Assert.Empty(failures);
    }

    private record struct LoadFlags(bool Padded, bool Fallback, bool Unresolved, bool MissingRefs);

    /// <summary>One map as the game builds it, headless: the world, the melee setup with two A.I. cars, two seconds of simulation. Null if it played.</summary>
    private static (string? Why, List<string> Notes, LoadFlags Flags) TryLoad(GameInstall install, AssetVfs vfs, ModelAssembler asm, string file)
    {
        var notes = new List<string>();
        var flags = new LoadFlags();
        try
        {
            var path = install.FindMission(file) ?? throw new FileNotFoundException("not found");
            var w = new MissionWorld(vfs, asm, path);
            if (w.Terrain.MissingTiles > 0) { flags.Padded = true; notes.Add($"{w.Terrain.MissingTiles} tiles missing"); }
            if (w.Mission.TerrainName() is { } zn && !Path.GetFileName(w.TerrainPath).Equals(zn, StringComparison.OrdinalIgnoreCase))
            { flags.Fallback = true; notes.Add($"ZONE {zn} -> {Path.GetFileName(w.TerrainPath)}"); }
            // spawnN, #spawnN (CTF), regen and checkN are markers the loader takes by prefix, not objects
            var unresolved = w.Placements.Concat(w.LinePlacements).Where(p => !IsMarker(p.Object.Name) && p.Definition is null)
                .Select(p => p.Object.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unresolved.Count > 0) { flags.Unresolved = true; notes.Add("unresolved " + string.Join(",", unresolved.Take(5))); }
            var st = WorldSettings.Read(w.Mission);
            // a .map is a texture name, found as .vqm / .m16 (the texture pipeline); the rest are files by name
            bool Tex(string r) => new[] { ".map", ".vqm", ".m16" }.Any(e => vfs.Exists(Path.ChangeExtension(r, e)));
            var refs = st is null ? [] : new[] { st.Palette, st.Luminance, st.Translucency, st.Horizon }.Where(r => r.Length > 0 && !vfs.Exists(r))
                .Concat(new[] { st.Sky, st.Overview }.Where(r => r.Length > 0 && !Tex(r))).ToList();
            if (refs.Count > 0) { flags.MissingRefs = true; notes.Add("missing " + string.Join(",", refs)); }
            var setup = MissionSetup.Build(vfs, w, asm, null, melee: ["valepre1.vcf", "vdlight1.vcf"]);
            setup.Melee?.Start();
            var session = new SimSession(setup);
            if (setup.World.Player is { } player) session.AddDriver(player);
            for (int t = 0; t < 2 * session.Rate.Label; t++) session.Tick([new CarInput()]);
            if (setup.Cars.Count == 0) throw new InvalidDataException("no cars spawned");
            return (null, notes, flags);
        }
        catch (Exception ex) { return ($"{ex.GetType().Name}: {ex.Message}", notes, flags); }
    }

    /// <summary>The base-game maps' files (.lvl and what came with them), by the folder each came in.</summary>
    private static List<(string Folder, string File)> BaseMapFiles() =>
        [.. Directory.EnumerateFiles(BaseMapsFactAttribute.Dir!, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetExtension(f).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.GetFileName(Path.GetDirectoryName(f)!), f))];

    /// <summary>
    /// Base Interstate '76 community maps (a .lvl and its .ter, made for the 1997 release; "unzip into addon"), in a
    /// scratch base install: half dropped into ADDON as the readmes say, half left in a folder of their own inside it.
    /// Each is listed (addon\*.lvl), named by its WRLD, and loads and runs.
    /// </summary>
    [BaseMapsFact]
    public void BaseGameMapsInAddonAreListedAndPlay()
    {
        var all = BaseMapFiles();
        var folders = all.Select(x => x.Folder).Distinct().Order().ToList();
        var flat = folders.Where((_, i) => i % 2 == 0).ToHashSet();
        using var s = new ScratchInstall(TestInstall.Dir!, null, all.Select(x => (flat.Contains(x.Folder) ? "" : x.Folder, x.File)));
        var listed = s.Install.ListMissions(".lvl", missionDir: false).Select(Path.GetFileName).ToList();
        var levels = all.Where(x => Path.GetExtension(x.File).Equals(".lvl", StringComparison.OrdinalIgnoreCase)).Select(x => Path.GetFileName(x.File)).ToList();
        Assert.True(levels.Count >= 5, $"only {levels.Count} base maps");
        Assert.Equal(levels.Order(StringComparer.OrdinalIgnoreCase), listed.Order(StringComparer.OrdinalIgnoreCase));
        var vfs = new AssetVfs(s.Install);
        var asm = new ModelAssembler(vfs);
        var failures = new List<string>();
        foreach (var f in levels)
        {
            var name = WorldSettings.ArenaName(new MissionFile(s.Install.FindMission(f)!));
            var (why, notes, _) = TryLoad(s.Install, vfs, asm, f);
            output.WriteLine($"{f,-16} \"{name}\" {(why is null ? "ok" : "FAIL " + why)}{(notes.Count > 0 ? "  [" + string.Join("; ", notes) + "]" : "")}");
            if (string.IsNullOrWhiteSpace(name)) failures.Add($"{f}: no name");
            if (why is not null) failures.Add($"{f}: {why}");
        }
        Assert.Empty(failures);
    }

    /// <summary>The same base-game maps dropped into a Nitro install's addon: Nitro never lists .lvl, the port does (as combat arenas) and they play.</summary>
    [BaseMapsFact]
    public void BaseGameMapsPlayInTheNitroPackToo()
    {
        if (NitroFactAttribute.Dir is null) return;
        using var s = new ScratchInstall(NitroFactAttribute.Dir, null, BaseMapFiles().Select(x => ("", x.File)));
        var vfs = new AssetVfs(s.Install);
        var asm = new ModelAssembler(vfs);
        var failures = new List<string>();
        foreach (var f in s.Install.ListMissions(".lvl").Select(Path.GetFileName))
            if (TryLoad(s.Install, vfs, asm, f!).Why is { } why) failures.Add($"{f}: {why}");
        Assert.Empty(failures);
        // an Interstate '82 map (.msa) is not a mission file of either game
        Assert.False(GameInstall.IsMissionFile("m49.msa"));
    }
}
