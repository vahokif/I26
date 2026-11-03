using System.Numerics;
using I76.Formats.Fsm;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Paths;

/// <summary>What a path edit touched: the table entry and the world rectangle (x0, z0, x1, z1) its old and new points cover.</summary>
public readonly record struct PathChange(int Index, Vector4 Bounds);

/// <summary>
/// A path as the editor shows it: one label and its points, and the table entries that hold it. The script compiler wrote a copy
/// of a path for every place the script names it (P18's bridgenav four times), so the copies are one path here and an edit
/// changes them all.
/// </summary>
public sealed record PathGroup(int First, IReadOnlyList<int> Entries, string Label, IReadOnlyList<Vector3> Points);

/// <summary>
/// The map's nav paths while it is being edited: the script's path table in its order. The script names a path by its place in the
/// table, so an entry is replaced in place, a new path goes at the end, and an entry the map's script came with is never removed
/// (it would move every later one). Saving writes the table into the script's payload when anything changed.
/// </summary>
public sealed class PathStore
{
    private readonly List<PathEntry> _paths;
    private int _saved;

    /// <param name="paths">The table as the file has it.</param>
    /// <param name="use">What the script names (<see cref="FsmPathUse"/>).</param>
    /// <param name="hasScript">The map has a script (code to run): its entries stay.</param>
    /// <param name="filePayload">The script's payload in the file (what <see cref="Payload"/> puts the table into).</param>
    public PathStore(IEnumerable<PathEntry> paths, FsmPathUse? use = null, bool hasScript = false, byte[]? filePayload = null)
    {
        _paths = [.. paths];
        FilePayload = filePayload ?? [];
        Use = use ?? FsmPathUse.None(_paths.Count);
        HasScript = hasScript;
        Fixed = hasScript ? _paths.Count : 0;
    }

    /// <summary>The paths of a mission file's bytes, with what its script names.</summary>
    public static PathStore FromMission(byte[] missionBytes)
    {
        var doc = MapDocument.Parse(missionBytes);
        var program = new MissionFile("", missionBytes).ReadFsmProgram();
        bool script = program is { Code.Length: > 0 };
        return new PathStore(doc.Paths, script ? FsmPathUse.Of(program!) : null, script, doc.FsmPayload);
    }

    public IReadOnlyList<PathEntry> Entries => _paths;
    /// <summary>The script's payload as the file has it.</summary>
    public byte[] FilePayload { get; }
    public int Count => _paths.Count;
    public PathEntry this[int i] => _paths[i];
    /// <summary>The places the map's script names each entry (by index), as far as the bytecode tells.</summary>
    public FsmPathUse Use { get; }
    /// <summary>The map has a script that runs: the entries it came with are named by index.</summary>
    public bool HasScript { get; }
    /// <summary>The entries below this are the script's own: replaced in place, never removed.</summary>
    public int Fixed { get; }
    /// <summary>Counts every change (undo and redo included).</summary>
    public int Version { get; private set; }
    public bool Dirty => Version != _saved;
    public void MarkSaved() => _saved = Version;
    public event Action<PathChange>? Changed;

    public void Insert(int index, PathEntry p)
    {
        if (index < Fixed) throw new InvalidOperationException("a path goes after the script's own");
        _paths.Insert(index, p);
        Touch(index, Bounds(p.Points));
    }

    public void RemoveAt(int index)
    {
        if (index < Fixed) throw new InvalidOperationException("the script names its paths by their place: one of them is not removed");
        var old = _paths[index];
        _paths.RemoveAt(index);
        Touch(index, Bounds(old.Points));
    }

    public void Replace(int index, PathEntry p)
    {
        var b = Merge(Bounds(_paths[index].Points), Bounds(p.Points));
        _paths[index] = p;
        Touch(index, b);
    }

    private void Touch(int i, Vector4 b) { Version++; Changed?.Invoke(new PathChange(i, b)); }

    /// <summary>The paths as the editor shows them: the copies of one path (same label, same points) are one, at its first entry.</summary>
    public List<PathGroup> Groups()
    {
        var o = new List<PathGroup>();
        var taken = new bool[_paths.Count];
        for (int i = 0; i < _paths.Count; i++)
        {
            if (taken[i]) continue;
            var members = new List<int> { i };
            for (int j = i + 1; j < _paths.Count; j++)
                if (!taken[j] && _paths[j].SameAs(_paths[i])) { members.Add(j); taken[j] = true; }
            o.Add(new PathGroup(i, members, _paths[i].Name, _paths[i].Points));
        }
        return o;
    }

    /// <summary>The path an entry belongs to (null for an index out of range).</summary>
    public PathGroup? GroupOf(int index) => Groups().FirstOrDefault(g => g.Entries.Contains(index));

    /// <summary>The places the script names a path (all its copies).</summary>
    public IEnumerable<PathUse> UsesOf(PathGroup g) => g.Entries.SelectMany(Use.Of);

    /// <summary>Whether a path can be deleted, and if not, why.</summary>
    public string? WhyNotDelete(PathGroup g) =>
        g.Entries.Any(i => i < Fixed) ? "the mission's script names its paths by their place in its table: a path it came with can be moved and reshaped, not deleted" : null;

    /// <summary>What is wrong with a label for a path (null: it can be used). Another path's label is taken.</summary>
    public string? LabelProblem(string label, PathGroup? self)
    {
        if (label.Length == 0) return "a path needs a label";
        if (label.Length > FsmPaths.MaxLabel) return $"a label has at most {FsmPaths.MaxLabel} characters";
        if (!FsmPaths.ValidLabel(label)) return "letters, digits and plain punctuation only";
        if (Groups().Any(g => g.First != self?.First && string.Equals(g.Label, label, StringComparison.OrdinalIgnoreCase))) return $"another path is called {label}";
        return null;
    }

    /// <summary>A label no path has: path1, path2...</summary>
    public string FreeLabel(string stem = "path")
    {
        for (int n = 1; ; n++)
            if (!_paths.Any(p => string.Equals(p.Name, stem + n, StringComparison.OrdinalIgnoreCase))) return stem + n;
    }

    /// <summary>The paths as the script's loader reads them (<see cref="MissionFile.FsmOverride"/>: the file's payload with this table in it).</summary>
    public byte[] Payload() => FsmPaths.Write(FilePayload, _paths);

    /// <summary>Puts these paths into a project's mission in place of the ones it was read with (Save).</summary>
    public void WriteTo(MapDocument doc)
    {
        doc.Paths.Clear();
        doc.Paths.AddRange(_paths);
    }

    /// <summary>The rectangle (x0, z0, x1, z1) of some points; empty for none.</summary>
    public static Vector4 Bounds(IReadOnlyList<Vector3> pts)
    {
        if (pts.Count == 0) return new Vector4(0, 0, -1, -1);
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var v in pts) { x0 = MathF.Min(x0, v.X); z0 = MathF.Min(z0, v.Z); x1 = MathF.Max(x1, v.X); z1 = MathF.Max(z1, v.Z); }
        return new Vector4(x0, z0, x1, z1);
    }

    public static Vector4 Merge(Vector4 a, Vector4 b) =>
        a.Z < a.X ? b : b.Z < b.X ? a : new Vector4(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));
}
