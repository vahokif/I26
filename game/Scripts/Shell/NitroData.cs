using Godot;
using I76.Formats.Mission;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Game.Shell;

/// <summary>
/// The campaign's progress: the original rewrites addon\SCENARIO.DAT in the install; the port keeps a
/// copy in user://saves/nitro/scenario.dat, seeded from the install's file (its done flags) the first time, and never writes the install.
/// </summary>
public sealed class NitroProgress
{
    public const string Path = "user://saves/nitro/scenario.dat";
    private string _text = "";
    public ScenarioDat Data { get; private set; } = new();
    public bool AllDoneExceptLast => Data.AllDoneExceptLast;

    public static NitroProgress Load(GameInstall install)
    {
        var p = new NitroProgress();
        if (Godot.FileAccess.FileExists(Path)) p._text = Godot.FileAccess.GetFileAsString(Path);
        else if (install.Resolve("addon", "SCENARIO.DAT") is { } src)
        {
            p._text = System.IO.File.ReadAllText(src, System.Text.Encoding.Latin1);
            p.Write();
        }
        p.Data = ScenarioDat.Parse(p._text);
        return p;
    }

    /// <summary>A won mission: its done flag (a mission the file does not name changes nothing).</summary>
    public void MarkDone(string mission)
    {
        if (ScenarioDat.MarkDone(_text, mission) is not { } t) return;
        _text = t;
        Write();
        Data = ScenarioDat.Parse(_text);
    }

    private void Write()
    {
        DirAccess.MakeDirRecursiveAbsolute("user://saves/nitro");
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(_text);
    }
}

/// <summary>nituser.mel, remembered under user://nitro (seeded from the install's): the scenario car and the instant melee's setup.</summary>
public static class NitroUser
{
    private const string Path = "user://nitro/nituser.mel";

    public static NitUserMel Load(GameInstall install)
    {
        if (Godot.FileAccess.FileExists(Path)) return NitUserMel.Parse(Godot.FileAccess.GetFileAsBytes(Path));
        return install.FindFile("nituser.mel") is { } f ? NitUserMel.Parse(System.IO.File.ReadAllBytes(f)) : new NitUserMel();
    }

    public static void Save(NitUserMel u)
    {
        DirAccess.MakeDirRecursiveAbsolute("user://saves/nitro");
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        f?.StoreBuffer(u.ToBytes());
    }
}

/// <summary>What the shells list from the install: the arenas, with the names the files carry.</summary>
public static class NitroLists
{
    /// <summary>The .cbt arenas, their WRLD names: miss16's, then addon\'s, each in file-name order.</summary>
    public static List<(string Name, string Msn)> CombatArenas(GameInstall install) => Arenas(install, ".cbt");

    /// <summary>
    /// The arenas of one kind by extension (.cbt, .rac, .cf2, .cf3, .cf4): *.ext in miss16 then addon\*.ext, each named by its WRLD (the shell's +0x58 callback) or, without one, its
    /// stem. The file is named with its extension, so a .cbt and a .rac of one stem (ofjuicy_8) are two arenas.
    /// </summary>
    public static List<(string Name, string Msn)> Arenas(GameInstall install, string ext)
    {
        var list = Named(install.ListMissions(ext));
        if (ext.Equals(".cbt", StringComparison.OrdinalIgnoreCase)) list.AddRange(Named(BaseLevels(install)));
        return list;
    }

    /// <summary>
    /// Base I76's .lvl levels in a Nitro install (miss16, addon and the folders inside it), for the combat lists, after the
    /// .cbt arenas. Nitro never lists .lvl (a level for it has to be converted); the port
    /// reads them as they are, so a player who drops base-game maps into Nitro's folders gets them as combat arenas. A
    /// level whose stem has a Nitro arena of its own (the pack's r66rcway.lvl beside its conversion r66rcway.rac) is left out.
    /// </summary>
    public static IEnumerable<string> BaseLevels(GameInstall install)
    {
        var converted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in new[] { ".cbt", ".rac", ".cf2", ".cf3", ".cf4", ".msn" })
            foreach (var f in install.ListMissions(e)) converted.Add(System.IO.Path.GetFileNameWithoutExtension(f));
        return install.ListMissions(".lvl").Where(f => !converted.Contains(System.IO.Path.GetFileNameWithoutExtension(f)));
    }

    /// <summary>
    /// Base I76's arena list: the fifteen of the table, then every addon\*.lvl, each
    /// named by its WRLD's 32-byte name field.
    /// </summary>
    public static List<(string Name, string Msn)> BaseArenas(GameInstall install) =>
        [.. MeleeData.Arenas, .. Named(install.ListMissions(".lvl", missionDir: false))];

    private static List<(string Name, string Msn)> Named(IEnumerable<string> files)
    {
        var list = new List<(string, string)>();
        foreach (var f in files)
        {
            var file = System.IO.Path.GetFileName(f);
            string? name = null;
            try { name = WorldSettings.ArenaName(new MissionFile(f)); }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                GD.PrintErr($"arena {file}: {ex.Message}");
                continue;
            }
            list.Add((name is { Length: > 0 } n ? n : System.IO.Path.GetFileNameWithoutExtension(f), file));
        }
        return list;
    }

    /// <summary>An arena's name as the lists give it (a session's arena on the JOIN popup): the base table's, else the file's own WRLD name, else the file name.</summary>
    public static string DisplayName(GameInstall install, string arena)
    {
        if (MeleeData.Arenas.FirstOrDefault(a => a.Msn.Equals(arena, StringComparison.OrdinalIgnoreCase)).Name is { } b) return b;
        // a name from the network: a local file of that name may be anything (a broken map, a folder, unreadable); the name it is, then
        try { if (install.FindMission(arena) is { } p && WorldSettings.ArenaName(new MissionFile(p)) is { Length: > 0 } n) return n; }
        catch (Exception ex) { Godot.GD.PrintErr($"arena name of {arena}: {ex.Message}"); }
        return arena;
    }

    /// <summary>nitscar.def's rows, the scenario's cars: the file if there is one, else the shell's built-in table (31).</summary>
    public static List<CarRow> ScenarioCars(GameInstall install) => CarTable.NitroScenario(install);

    /// <summary>nitcar.def's rows, the melee cars: the file if there is one, else the shell's built-in table (37).</summary>
    public static List<CarRow> MeleeCars(GameInstall install) => CarTable.NitroMelee(install);
}
