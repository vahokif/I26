namespace I76.Formats.Vfs;

/// <summary>Where a name resolved to.</summary>
public abstract record AssetSource(string Name)
{
    public sealed record Loose(string Name, string Path) : AssetSource(Name);
    public sealed record Zfs(string Name, ZfsEntry Entry) : AssetSource(Name);
    public sealed record Pak(string Name, string Stem, PakMember Member) : AssetSource(Name);
    /// <summary>A file the game itself provides (the addon overlay).</summary>
    public sealed record Memory(string Name, byte[] Data) : AssetSource(Name);
}

/// <summary>
/// Name-keyed access to every game file, in the engine's order
/// (docs/formats/zix-index.md): loose files in the ZIX directory sources
/// other than i76.zfs first, then ZFS records, then members of the nested
/// .pak files. Names are case-insensitive.
///
/// The pak member order is ours: when two paks hold the same member name the
/// first stem in ordinal order wins. Which one the engine picks is not
/// established; <see cref="PakDuplicates"/> lists the names concerned.
/// </summary>
public sealed class AssetVfs
{
    public GameInstall Install { get; }
    public ZfsArchive Zfs { get; }
    public PakSet Paks { get; }
    public ZixIndex? Zix { get; }

    private readonly Dictionary<string, AssetSource> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pakDup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PakMember>> _pakMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<byte[]>> _pakPayload = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _archive = new(StringComparer.OrdinalIgnoreCase);

    public AssetVfs(GameInstall install)
    {
        Install = install;
        Zfs = new ZfsArchive(install.ZfsPath);
        Paks = new PakSet(Zfs);
        if ((install.FindFile("I76.ZIX") ?? install.FindFile("nitro.zix")) is { } zixPath)
            Zix = new ZixIndex(File.ReadAllBytes(zixPath));

        // Lowest precedence first; later sources overwrite.
        foreach (var stem in Paks.Stems)
        {
            var members = Paks.Members(stem);
            _pakMembers[stem] = members;
            foreach (var m in members)
            {
                _archive.Add(m.Name);
                if (_map.TryGetValue(m.Name, out var prev) && prev is AssetSource.Pak pp)
                {
                    if (!_pakDup.TryGetValue(m.Name, out var l)) _pakDup[m.Name] = l = [pp.Stem];
                    l.Add(stem);
                    continue;
                }
                _map[m.Name] = new AssetSource.Pak(m.Name, stem, m);
            }
        }
        // the mission directory is the engine's last source (the original's ZIX reader, the Nitro Pack, appends miss16
        // or miss8 after the index's own): below the archive
        if (install.MissionDir is { } missDir) AddLoose(missDir);
        foreach (var e in Zfs.Entries)
        {
            _map[e.Name] = new AssetSource.Zfs(e.Name, e);
            _archive.Add(e.Name);
        }
        foreach (var dir in LooseDirs()) AddLoose(dir);
        // addon\ is its first: what a player drops there overrides the archive (measured under Wine: the Nitro Pack opens
        // addon\t08_16.hzd and addon\m05.npt over nitro.zfs's)
        if (install.FindDir("addon") is { } addon) AddLoose(addon);
        // a map unpacked into its own folder inside addon\ (the port's: GameInstall.FindMission) brings its text (.npt) and
        // the like; such files only add names, never replace one the game already has
        foreach (var dir in install.AddonFolders())
            foreach (var p in Directory.EnumerateFiles(dir))
                if (Path.GetFileName(p) is { } n && !n.StartsWith('.')) _map.TryAdd(n, new AssetSource.Loose(n, p));
        // a map downloaded from a network game's host (GameInstall.MapDir): its files are the host's, over everything on disk
        if (install.MapDir is { } dl && Directory.Exists(dl)) AddLoose(dl);
    }

    /// <summary>Whether the install's archive (the ZFS and its paks) has a file of this name: stock data, whatever overrides it on disk.</summary>
    public bool InArchive(string name) => _archive.Contains(name);

    private void AddLoose(string dir)
    {
        foreach (var p in Directory.EnumerateFiles(dir))
        {
            var n = Path.GetFileName(p);
            if (!n.StartsWith('.')) _map[n] = new AssetSource.Loose(n, p);
        }
    }

    private IEnumerable<string> LooseDirs()
    {
        if (Zix is null) yield break;
        foreach (var s in Zix.Sources)
        {
            if (!s.IsDir || s.Name.EndsWith(".zfs", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = s.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Install.Resolve(parts) is { } d && Directory.Exists(d)) yield return d;
        }
    }

    /// <summary>
    /// Files the game writes for itself, ahead of everything else: the
    /// original's addon\ directory, where the shell puts the player's car
    /// (vehscn.vcf) and the engine registers it (callback [2]). Process-wide,
    /// in memory; never written into the install.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Addon = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, List<string>> PakDuplicates => _pakDup;
    public IEnumerable<string> Names => _map.Keys.Union(Addon.Keys, StringComparer.OrdinalIgnoreCase);
    public bool Exists(string name) => Addon.ContainsKey(name) || _map.ContainsKey(name);
    public AssetSource? Locate(string name) => Addon.TryGetValue(name, out var a) ? new AssetSource.Memory(name, a)
        : _map.GetValueOrDefault(name) ?? (name.Equals("vuchuey.vdf", StringComparison.OrdinalIgnoreCase) ? _map.GetValueOrDefault("vuhuey.vdf") : null);
    // vuchuey.vdf: the base game's shell names it for the Phaedra Rattler's "retpocileh" secret, but no such file ships (Nitro's
    // shell says vuhuey.vdf); variants saved by older builds of ours carry the base spelling, so it reads as the Huey.
    public IReadOnlyList<PakMember> PakMembers(string stem) => _pakMembers[stem];

    public byte[]? TryRead(string name) => Locate(name) is { } s ? Read(s) : null;

    public byte[] Read(string name) => TryRead(name) ?? throw new FileNotFoundException(name);

    public byte[] Read(AssetSource s) => s switch
    {
        AssetSource.Memory m => m.Data,
        AssetSource.Loose l => File.ReadAllBytes(l.Path),
        AssetSource.Zfs z => Zfs.Read(z.Entry),
        AssetSource.Pak p => PakPayload(p.Stem).AsSpan(p.Member.Offset, p.Member.Size).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    /// <summary>A whole .pak payload, cached weakly: one pak holds a model's members together.</summary>
    public byte[] PakPayload(string stem)
    {
        lock (_pakPayload)
        {
            if (_pakPayload.TryGetValue(stem, out var w) && w.TryGetTarget(out var hit)) return hit;
            var d = Paks.Payload(stem);
            _pakPayload[stem] = new WeakReference<byte[]>(d);
            return d;
        }
    }
}
