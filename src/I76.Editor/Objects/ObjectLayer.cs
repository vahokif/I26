using System.Numerics;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Objects;

/// <summary>One placed object in the editor: its ODEF record, and the placement the game side draws from it.</summary>
public sealed class EditObject
{
    public int Id { get; }
    public MapObject Data { get; }
    /// <summary>What the loader makes of the record (replaced by a new instance whenever the record changes, so the game side can tell).</summary>
    public Placement Placement { get; internal set; }
    public CatalogueItem? Item { get; set; }

    internal EditObject(int id, MapObject data, Placement placement) { Id = id; Data = data; Placement = placement; }

    public string Name => Data.Name;
    public MapRole Role => Data.Role;
    public Vector3 Position => Data.Position;
    public float Yaw => Data.Yaw;
    public bool IsPlayer => Data.IsPlayer;
    /// <summary>Road pieces (intersections) are painted into the roads at load and cannot be moved here.</summary>
    public bool Locked => (Data.ObjectType & 0xf0) == 0x50;
}

public enum ChangeKind { Added, Removed, Changed }

/// <summary>A change to the layer: what, to which object, and the placement it had before (for Changed and Removed).</summary>
public readonly record struct LayerChange(ChangeKind Kind, EditObject Item, Placement? Old, int Index);

/// <summary>
/// The map's placed objects as the editor holds them: the ODEF records in file order, edited and written back through
/// <see cref="MapDocument.Objects"/> (docs/map-format-writing.md). Every change goes through here and is reported to whoever draws the
/// objects; the commands (<see cref="ObjectCommands"/>) do and undo the changes. The roads of the same file are held too, for snapping
/// and for cutting a road where a bridge carries it.
/// </summary>
public sealed class ObjectLayer
{
    private readonly List<EditObject> _items = [];
    private Func<string, string?> _resolve;
    private int _nextId = 1;

    /// <summary>The parsed mission file: the records. Only <see cref="ApplyTo"/> puts edits back into a project.</summary>
    public MapDocument Map { get; }
    /// <summary>The map's roads, the road tool's own store: snapping reads it, and a structure placed on a road cuts it through a <see cref="RoadCommand"/>.</summary>
    public RoadStore Roads { get; }

    public ObjectLayer(MapDocument map, Func<string, string?> resolveDefinition, RoadStore roads)
    {
        Map = map;
        Roads = roads;
        _resolve = resolveDefinition;
        foreach (var o in map.Objects) _items.Add(Make(o.Clone()));
    }

    /// <summary>Resolves an object name to its definition file (the game's assembler); replaced when the scene is reloaded.</summary>
    public Func<string, string?> Resolve { get => _resolve; set => _resolve = value; }

    public IReadOnlyList<EditObject> Items => _items;
    public int Version { get; private set; }
    /// <summary>The version last saved (the document sets it).</summary>
    public int SavedVersion { get; set; }
    public event Action<LayerChange>? Changed;
    /// <summary>The ground height at x, z (set by the editor): new and moved objects are written standing on it, as the loader stands them.</summary>
    public Func<float, float, float> Ground { get; set; } = (_, _) => 0;

    private EditObject Make(MapObject o)
    {
        string name = o.Name;
        // as MissionWorld resolves them: the keywords and empty names have no definition
        string? def = name.Length == 0 || name.Equals("spawn", StringComparison.OrdinalIgnoreCase) || name.Equals("regen", StringComparison.OrdinalIgnoreCase) ? null : _resolve(name);
        return new EditObject(_nextId++, o, new Placement(ToPlaced(o), def));
    }

    /// <summary>The loader's view of a record (MissionFile.PlacedObjects).</summary>
    public static PlacedObject ToPlaced(MapObject o) => new(o.Name, o.NameFlags, o.Rotation, o.Position, (byte[])o.Payload.Clone());

    private void Refresh(EditObject e)
    {
        var def = e.Placement.Definition;
        e.Placement = new Placement(ToPlaced(e.Data), def);
    }

    public EditObject? ById(int id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>Adds a new record (at the end, or at <paramref name="index"/>).</summary>
    public EditObject Add(MapObject o, int? index = null)
    {
        var e = Make(o);
        Insert(e, index ?? _items.Count);
        return e;
    }

    /// <summary>Puts an object that was removed back where it was (what undo and redo do).</summary>
    public void Insert(EditObject e, int index)
    {
        index = Math.Clamp(index, 0, _items.Count);
        _items.Insert(index, e);
        Version++;
        Changed?.Invoke(new(ChangeKind.Added, e, null, index));
    }

    public int Remove(EditObject e)
    {
        int i = _items.IndexOf(e);
        if (i < 0) return -1;
        _items.RemoveAt(i);
        Version++;
        Changed?.Invoke(new(ChangeKind.Removed, e, e.Placement, i));
        return i;
    }

    /// <summary>Moves and turns an object; its y is the ground's there. Quiet if nothing changed.</summary>
    public void SetPose(EditObject e, Vector3 position, float yaw)
    {
        position = new Vector3(position.X, Ground(position.X, position.Z), position.Z);
        if (position == e.Data.Position && MathF.Abs(Snapping.Wrap(yaw - e.Data.Yaw)) < 1e-6f) return;
        var old = e.Placement;
        e.Data.Position = position;
        if (MathF.Abs(Snapping.Wrap(yaw - e.Data.Yaw)) > 1e-6f) e.Data.Yaw = yaw;
        Refresh(e);
        Version++;
        Changed?.Invoke(new(ChangeKind.Changed, e, old, _items.IndexOf(e)));
    }

    /// <summary>Replaces an object's whole record (its name, type, flags and pose): the inspector's edits and their undo.</summary>
    public void SetData(EditObject e, byte[] payload, string? definition = null)
    {
        var old = e.Placement;
        Array.Copy(payload, e.Data.Payload, Math.Min(payload.Length, e.Data.Payload.Length));
        string n = e.Data.Name;
        var def = definition ?? (n.Length == 0 || n.Equals("spawn", StringComparison.OrdinalIgnoreCase) || n.Equals("regen", StringComparison.OrdinalIgnoreCase) ? null : _resolve(n));
        e.Placement = new Placement(ToPlaced(e.Data), def);
        Version++;
        Changed?.Invoke(new(ChangeKind.Changed, e, old, _items.IndexOf(e)));
    }

    /// <summary>The roads as polylines, for snapping (rebuilt when the roads change).</summary>
    public IReadOnlyList<RoadPolyline> Polylines
    {
        get
        {
            if (_poly is null || _polyVersion != Roads.Version) { _poly = RoadGeometry.Build(Roads.Roads); _polyVersion = Roads.Version; }
            return _poly;
        }
    }
    private List<RoadPolyline>? _poly;
    private int _polyVersion = -1;

    /// <summary>Writes the layer's records into a project's mission, in order. (The roads are written by the road store: <see cref="RoadStore.WriteTo"/>.)</summary>
    public void ApplyTo(MapDocument mission)
    {
        mission.Objects.Clear();
        foreach (var e in _items) mission.Objects.Add(e.Data.Clone());
    }

    // ---- queries ----

    public IEnumerable<EditObject> OfRole(MapRole role) => _items.Where(i => i.Role == role);
    public IEnumerable<EditObject> Spawns => OfRole(MapRole.Spawn);
    public IEnumerable<EditObject> Regens => OfRole(MapRole.Regen);
    public IEnumerable<EditObject> Checkpoints => OfRole(MapRole.Checkpoint).OrderBy(i => i.Data.Number);
    public IEnumerable<EditObject> Flags => OfRole(MapRole.Flag).OrderBy(i => i.Data.Number);

    /// <summary>The next free checkpoint number (1-98).</summary>
    public int NextCheckpointNumber()
    {
        var used = Checkpoints.Select(c => c.Data.Number).Where(n => n < 99).ToHashSet();
        for (int n = 1; n < 99; n++) if (!used.Contains(n)) return n;
        return 98;
    }
}
