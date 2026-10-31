namespace I76.Editor;

/// <summary>The samples of one tile that a command changed: their indices in the tile, before and after (whole raw samples).</summary>
public sealed record TileDelta(int[] Offsets, ushort[] Before, ushort[] After);

public interface IEditCommand
{
    string Name { get; }
    /// <summary>Entries held, for the stack's memory limit.</summary>
    int Size { get; }
    /// <summary>Puts the ground back as it was before the command.</summary>
    void Undo(IMapDocument doc);
    /// <summary>Does it again.</summary>
    void Redo(IMapDocument doc);
}

/// <summary>A brush stroke: the height deltas per touched tile.</summary>
public sealed class StrokeCommand(string name, Dictionary<int, TileDelta> tiles) : IEditCommand
{
    public string Name { get; } = name;
    public IReadOnlyDictionary<int, TileDelta> Tiles => tiles;
    public int Size => tiles.Values.Sum(t => t.Offsets.Length);

    public void Undo(IMapDocument doc)
    {
        foreach (var (tile, d) in tiles)
            for (int i = 0; i < d.Offsets.Length; i++) doc.SetSample(tile, d.Offsets[i], d.Before[i]);
    }

    public void Redo(IMapDocument doc)
    {
        foreach (var (tile, d) in tiles)
            for (int i = 0; i < d.Offsets.Length; i++) doc.SetSample(tile, d.Offsets[i], d.After[i]);
    }
}

/// <summary>A change of the map's look (the map style): undone, it puts the earlier look back (and the map is rebuilt to show it).</summary>
public sealed class LookCommand(string name, I76.Formats.Mission.MapLook before, I76.Formats.Mission.MapLook after) : IEditCommand
{
    public string Name { get; } = name;
    public int Size => 1;
    public void Undo(IMapDocument doc) { if (doc is TerrainDocument t) t.SetLook(before); }
    public void Redo(IMapDocument doc) { if (doc is TerrainDocument t) t.SetLook(after); }
}

/// <summary>Undo and redo: a stack of commands, the oldest dropped past a limit on the samples held.</summary>
public sealed class UndoStack(IMapDocument start, int limit = 6_000_000)
{
    /// <summary>The document the commands apply to (a scene reload makes a new one with the same tiles).</summary>
    public IMapDocument Doc { get; set; } = start;
    private readonly List<IEditCommand> _done = [];
    private readonly Stack<IEditCommand> _undone = new();
    private long _size;

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public int Count => _done.Count;
    public string? NextUndo => _done.Count > 0 ? _done[^1].Name : null;
    public string? NextRedo => _undone.Count > 0 ? _undone.Peek().Name : null;

    /// <summary>Records a command that has just been done; what was undone is forgotten.</summary>
    public void Push(IEditCommand c)
    {
        _undone.Clear();
        _done.Add(c);
        _size += c.Size;
        while (_size > limit && _done.Count > 1) { _size -= _done[0].Size; _done.RemoveAt(0); }
    }

    public bool Undo()
    {
        if (_done.Count == 0) return false;
        var c = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        _size -= c.Size;
        c.Undo(Doc);
        _undone.Push(c);
        return true;
    }

    public bool Redo()
    {
        if (_undone.Count == 0) return false;
        var c = _undone.Pop();
        c.Redo(Doc);
        _done.Add(c);
        _size += c.Size;
        return true;
    }

    public void Clear() { _done.Clear(); _undone.Clear(); _size = 0; }
}

/// <summary>Whole-map edits as undoable commands: the changes between two snapshots (<see cref="TerrainDocument.Snapshot"/>).</summary>
public static class SnapshotCommand
{
    /// <summary>The command taking the map from <paramref name="before"/> to <paramref name="after"/>; null if they are the same.</summary>
    public static StrokeCommand? Between(string name, ushort[] before, ushort[] after)
    {
        const int n = I76.Formats.Mission.TerrainTiles.Tile * I76.Formats.Mission.TerrainTiles.Tile;
        var tiles = new Dictionary<int, TileDelta>();
        for (int t = 0; t < before.Length / n; t++)
        {
            var offs = new List<int>();
            for (int i = 0; i < n; i++) if (before[t * n + i] != after[t * n + i]) offs.Add(i);
            if (offs.Count == 0) continue;
            tiles[t] = new TileDelta([.. offs], [.. offs.Select(i => before[t * n + i])], [.. offs.Select(i => after[t * n + i])]);
        }
        return tiles.Count == 0 ? null : new StrokeCommand(name, tiles);
    }
}
