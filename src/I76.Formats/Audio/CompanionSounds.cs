using I76.Formats.Vfs;

namespace I76.Formats.Audio;

/// <summary>
/// Interstate '76's kill lines for a Nitro Pack player whose driver is Groove (the Nitro Pack has no lines for Groove,
/// docs/deviations.md): when the machine also has Interstate '76, those few sounds (Groove's kill lines, <see cref="GrooveKillLines"/>) are read from
/// that install. The game sets <see cref="Locator"/>; it is asked once, the first time a Nitro mission wants to know.
/// </summary>
public static class CompanionSounds
{
    /// <summary>The folder of an Interstate '76 install on this machine, or null (set by the game; unset: no other game).</summary>
    public static Func<string?>? Locator { get; set; }

    private static AssetVfs? _vfs;
    private static bool _looked;

    /// <summary>A fixed I76 install (tests, tools); null clears it and asks <see cref="Locator"/> again.</summary>
    public static void Set(AssetVfs? vfs) { _vfs = vfs; _looked = vfs is not null; }

    /// <summary>The other game's files, or null when there is no Interstate '76 to be had.</summary>
    public static AssetVfs? Vfs
    {
        get
        {
            if (_looked) return _vfs;
            _looked = true;
            try
            {
                if (Locator?.Invoke() is { Length: > 0 } dir && InstallSearch.IsInstall(dir) && new GameInstall(dir) is { IsNitro: false } i) _vfs = new AssetVfs(i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return _vfs;
        }
    }

    public static bool Available => Vfs is not null;

    /// <summary>
    /// Groove's kill lines in Interstate '76: the 18 sounds every t*, s* and a01 script picks from (rand(35), one of the first 18, priority 5)
    /// once an enemy has died by Groove's fault (isGroovesFault), the same set in 25 missions.
    /// </summary>
    public static readonly string[] GrooveKillLines =
        ["gdsgc67", "gdsgc68", "gdsgc69", "gdsgc02", "gdsgc04", "gdsgc05", "gdsgc08", "gdsgc09", "gdsgc10", "gdsgc11",
         "gdsgc13", "gdsgc14", "gdsgc15", "gdsgc16", "gdsgc17", "gdsgc20", "gdsgc01", "gdsgc65"];

    /// <summary>A sound taken from the other game (Groove's kill lines).</summary>
    public static bool IsKillLine(string name)
    {
        int dot = name.LastIndexOf('.');
        return GrooveKillLines.Contains(dot < 0 ? name : name[..dot], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The other game's files for a Nitro install's missing kill line, else null.</summary>
    internal static AssetVfs? For(AssetVfs vfs, string name) => vfs.Install.IsNitro && IsKillLine(name) ? Vfs : null;
}
