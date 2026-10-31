using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>One change to the road list.</summary>
public abstract record RoadOp
{
    public sealed record Insert(int Index, RoadEdit Road) : RoadOp;
    public sealed record Remove(int Index, RoadEdit Road) : RoadOp;
    public sealed record Replace(int Index, RoadEdit Before, RoadEdit After) : RoadOp;

    public void Do(RoadStore s)
    {
        switch (this)
        {
            case Insert i: s.Insert(i.Index, i.Road); break;
            case Remove r: s.RemoveAt(r.Index); break;
            case Replace p: s.Replace(p.Index, p.After); break;
        }
    }

    public void Undo(RoadStore s)
    {
        switch (this)
        {
            case Insert i: s.RemoveAt(i.Index); break;
            case Remove r: s.Insert(r.Index, r.Road); break;
            case Replace p: s.Replace(p.Index, p.Before); break;
        }
    }
}

/// <summary>
/// A road edit and the ground cut and filled for it, as one undo step: the changes to the road list, and the height samples
/// the road's profile moved (<see cref="TileDelta"/>s, as a brush stroke records them).
/// </summary>
public sealed class RoadCommand(string name, RoadStore store, IReadOnlyList<RoadOp> ops, Dictionary<int, TileDelta>? terrain = null) : IEditCommand
{
    private readonly Dictionary<int, TileDelta> _terrain = terrain ?? [];
    public string Name { get; } = name;
    public IReadOnlyList<RoadOp> Ops => ops;
    public int Size => _terrain.Values.Sum(t => t.Offsets.Length) + ops.Count * 64;

    /// <summary>Applies the road changes now (the terrain was written while the command was made, through a <see cref="HeightRecorder"/>).</summary>
    public RoadCommand Apply() { foreach (var o in ops) o.Do(store); return this; }

    public void Undo(IMapDocument doc)
    {
        for (int i = ops.Count - 1; i >= 0; i--) ops[i].Undo(store);
        foreach (var (tile, d) in _terrain)
            for (int i = 0; i < d.Offsets.Length; i++) doc.SetSample(tile, d.Offsets[i], d.Before[i]);
    }

    public void Redo(IMapDocument doc)
    {
        foreach (var o in ops) o.Do(store);
        foreach (var (tile, d) in _terrain)
            for (int i = 0; i < d.Offsets.Length; i++) doc.SetSample(tile, d.Offsets[i], d.After[i]);
    }
}
