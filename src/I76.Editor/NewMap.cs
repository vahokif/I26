using System.Numerics;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Editor;

/// <summary>New maps and the list of the player's own: files the editor wrote into the install's addon\ folder.</summary>
public static class NewMap
{
    public const int MaxTiles = 4;
    public const int DefaultMetres = 10;

    /// <summary>
    /// A flat map of <paramref name="tiles"/> x <paramref name="tiles"/> tiles (640 m a side each) at <paramref name="metres"/>, with a
    /// spawn in the middle, saved into <paramref name="saveDir"/>. Melee maps of the Nitro Pack's kind (.cbt) in a Nitro install, base-game
    /// levels (.lvl) otherwise. The stock sky, palette and light of a Nitro arena (MapProject.CreateBlank). Returns the project and its files.
    /// </summary>
    public static (MapProject Project, IReadOnlyList<string> Files) CreateFlat(string saveDir, string name, string displayName, int tiles, double metres, bool nitro)
    {
        if (!MapSaver.ValidName(name)) throw new ArgumentException($"map name '{name}': 1-{MapProject.MaxStem} letters, digits and underscores");
        if (!MapSaver.ValidDisplayName(displayName)) throw new ArgumentException($"listed name '{displayName}': 1-{MapSaver.MaxDisplayName} characters");
        tiles = Math.Clamp(tiles, 1, MaxTiles);
        int raw = (int)Math.Round(Math.Clamp(metres, 0, 409.5) / Units.HeightScale);
        // (72, 4) is where CreateBlank puts it; a bigger map keeps the grid cell's room (rows 72-75, columns 4-7 are free)
        var project = MapProject.CreateBlank(name, displayName, nitro ? ".cbt" : ".lvl", tiles, tiles, raw);
        var b = project.WorldBounds();
        var centre = new Vector3((b.X + b.Z) / 2, (float)(raw * Units.HeightScale), (b.Y + b.W) / 2);
        project.Mission.Objects.Add(MapObject.Spawn(centre));
        return (project, project.SaveTo(saveDir));
    }

    /// <summary>The maps directly in a folder (the install's addon\): file stem and listed name, by stem.</summary>
    public static List<(string Stem, string Listed, string Path)> UserMaps(string? addonDir)
    {
        var o = new List<(string, string, string)>();
        if (addonDir is null || !Directory.Exists(addonDir)) return o;
        foreach (var f in Directory.EnumerateFiles(addonDir).Where(GameInstall.IsMissionFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var doc = MapDocument.Load(f);
                o.Add((Path.GetFileNameWithoutExtension(f), doc.ArenaName?.Trim() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(f), f));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        return o;
    }
}
