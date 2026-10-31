using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Editor.Objects;

/// <summary>
/// The gallery map: every breakable or drive-on structure of an install's catalogue, one to a cell of a grid on flat ground, each with
/// a clear approach lane (the cells are far enough apart that the 80 m before an object and the 80 m round it are empty). For looking
/// at what each thing leaves when it is destroyed, and for checking the sim's version of that against the original (`i76 gallery`).
/// </summary>
public static class Gallery
{
    /// <summary>The cell a structure gets: 170 m, so a lane of 80 m reaches no neighbour.</summary>
    public const float Cell = 170;
    public const int Tiles = 4;
    public const string DefaultStem = "gallery";

    /// <summary>The ODEF types the sim gives a collider that takes damage or carries a car: solid 2, breakable 3, the deck and ramp types 0xb-0xd.</summary>
    public static bool Wanted(uint type) => type is 2 or 3 or 0xb or 0xc or 0xd;

    public sealed record Placed(string Name, uint Type, Vector3 At);

    /// <summary>
    /// The items to place: the catalogue's buildings, structures, roadside things, props, vegetation and fences, once each (the game refuses
    /// a second instance of a name: "adding a non-unique instance id"), as the type the stock maps most often give it, else the first
    /// they give it that the sim collides.
    /// </summary>
    public static List<(string Name, uint Type)> Items(IEnumerable<CatalogueItem> catalogue) =>
        catalogue
            .Where(i => i.Marker == MarkerKind.None && i.Category is not (ObjectCategory.Scripted or ObjectCategory.Markers) && !i.IsTunnel)
            .Select(i => (i.Name, Type: Wanted(i.Type) ? i.Type : i.TypesSeen.FirstOrDefault(Wanted)))
            .Where(x => x.Type != 0)
            .DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The grid: row by row from the map's south-west corner (<paramref name="origin"/>, the world bounds' minimum), the first cell's centre half a cell in.</summary>
    public static List<Placed> Layout(IReadOnlyList<(string Name, uint Type)> items, float y, Vector2 origin)
    {
        int perRow = (int)(Tiles * 640 / Cell);
        if (items.Count > perRow * perRow) throw new ArgumentException($"{items.Count} items do not fit {perRow * perRow} cells");
        return items.Select((it, k) => new Placed(it.Name, it.Type, new Vector3(origin.X + (k % perRow + 0.5f) * Cell, y, origin.Y + (k / perRow + 0.5f) * Cell))).ToList();
    }

    /// <summary>Makes the map into <paramref name="saveDir"/>: flat ground 10 m up, the spawn moved to the south-west corner (CreateFlat's is at the middle, inside a cell), and the grid. Returns the files and the placements.</summary>
    public static (IReadOnlyList<string> Files, List<Placed> Placed) Build(GameInstall install, ModelAssembler asm, string saveDir, string stem, string cacheDir, bool nitro)
    {
        Directory.CreateDirectory(cacheDir);
        var catalogue = ObjectCatalogue.LoadOrScan(install, asm, Path.Combine(cacheDir, ObjectCatalogue.CacheName(install) + ".json"));
        var items = Items(catalogue);
        var (project, _) = NewMap.CreateFlat(saveDir, stem, "Gallery", Tiles, NewMap.DefaultMetres, nitro);
        var b = project.WorldBounds();
        var origin = new Vector2(b.X, b.Y);
        project.Mission.Objects.RemoveAll(o => o.IsPlayer);
        project.Mission.Objects.Add(MapObject.Spawn(new Vector3(origin.X + 30, NewMap.DefaultMetres, origin.Y + 30)));
        var placed = Layout(items, NewMap.DefaultMetres, origin);
        foreach (var p in placed) project.Mission.Objects.Add(MapObject.Create(p.Name, p.Type, p.At));
        return (project.SaveTo(saveDir), placed);
    }
}
