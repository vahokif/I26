using System.Numerics;
using I76.Formats;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>What a road edit touched: the road's index and the world rectangle (x0, z0, x1, z1) its old and new shapes cover, for local redraws.</summary>
public readonly record struct RoadChange(int Index, Vector4 Bounds);

/// <summary>
/// The map's roads while it is being edited: the RSEG list in file order. A road is replaced, never edited in place, so an
/// undo command can hold the before and after. Untouched roads stay the objects the file was read into (their header counts and
/// tails are written back as read); saving writes the list over the file's RDEF when anything changed.
/// </summary>
public sealed class RoadStore
{
    private readonly List<RoadEdit> _roads;
    private int _saved;

    public RoadStore(IEnumerable<RoadEdit> roads) => _roads = [.. roads];

    /// <summary>The roads of a mission file's bytes (what <see cref="MapDocument"/> reads).</summary>
    public static RoadStore FromMission(byte[] missionBytes) => new(MapDocument.Parse(missionBytes).Roads);

    public IReadOnlyList<RoadEdit> Roads => _roads;
    public int Count => _roads.Count;
    public RoadEdit this[int i] => _roads[i];
    /// <summary>Counts every change (undo and redo included).</summary>
    public int Version { get; private set; }
    public bool Dirty => Version != _saved;
    public void MarkSaved() => _saved = Version;
    public event Action<RoadChange>? Changed;
    /// <summary>The map's intersection pieces, when the editor holds them: Save writes them with the roads (they are edited together).</summary>
    public Intersections.PieceStore? Pieces { get; set; }

    public void Insert(int index, RoadEdit r) { _roads.Insert(index, r); Touch(index, Bounds(r)); }
    public void RemoveAt(int index) { var old = _roads[index]; _roads.RemoveAt(index); Touch(index, Bounds(old)); }
    public void Replace(int index, RoadEdit r)
    {
        var b = Merge(Bounds(_roads[index]), Bounds(r));
        _roads[index] = r;
        Touch(index, b);
    }

    private void Touch(int i, Vector4 b) { Version++; Changed?.Invoke(new RoadChange(i, b)); }

    /// <summary>The rectangle a road's edges cover (x0, z0, x1, z1); empty for a road with no points.</summary>
    public static Vector4 Bounds(RoadEdit r)
    {
        if (r.Points.Count == 0) return new Vector4(0, 0, -1, -1);
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var p in r.Points)
            foreach (var v in new[] { p.Left, p.Right })
            { x0 = MathF.Min(x0, v.X); z0 = MathF.Min(z0, v.Y); x1 = MathF.Max(x1, v.X); z1 = MathF.Max(z1, v.Y); }
        return new Vector4(x0, z0, x1, z1);
    }

    public static Vector4 Merge(Vector4 a, Vector4 b) =>
        a.Z < a.X ? b : b.Z < b.X ? a : new Vector4(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));

    /// <summary>The roads as the game's loader keeps them (<see cref="MissionFile.RoadOverride"/>).</summary>
    public List<RoadSegment> ToSegments() => _roads.Select(r => new RoadSegment(r.Kind, r.Points.Select(p =>
        new RoadPoint(Bin.F32(p.Raw, 0), Bin.F32(p.Raw, 4), (byte[])p.Raw.Clone())).ToList())).ToList();

    /// <summary>Puts these roads into a project's mission, in place of the ones it was read with (Save).</summary>
    public void WriteTo(MapDocument doc)
    {
        doc.Roads.Clear();
        doc.Roads.AddRange(_roads);
        Pieces?.WriteTo(doc);
    }
}
