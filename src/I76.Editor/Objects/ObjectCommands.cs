using System.Numerics;

namespace I76.Editor.Objects;

/// <summary>A pose of an object: its position (y is the ground's) and heading.</summary>
public readonly record struct Pose(Vector3 Position, float Yaw);

/// <summary>The undoable object edits. They hold the layer they act on; the document passed in by the undo stack is not used.</summary>
public abstract class ObjectCommand(string name, int size = 1) : IEditCommand
{
    public string Name { get; } = name;
    public int Size { get; } = size;
    public abstract void Undo(IMapDocument doc);
    public abstract void Redo(IMapDocument doc);
}

public static class ObjectCommands
{
    /// <summary>Places objects that already exist in the layer's list (a new placement is added first, then this recorded).</summary>
    public sealed class Place(ObjectLayer layer, IReadOnlyList<EditObject> items, string name) : ObjectCommand(name)
    {
        private readonly int[] _index = [.. items.Select(e => layer.Items.ToList().IndexOf(e))];

        public override void Undo(IMapDocument doc) { foreach (var e in items.Reverse()) layer.Remove(e); }
        public override void Redo(IMapDocument doc) { for (int i = 0; i < items.Count; i++) layer.Insert(items[i], _index[i]); }
    }

    /// <summary>Bulldoze: removes objects; undo puts them back in their places in the file.</summary>
    public sealed class Remove : ObjectCommand
    {
        private readonly ObjectLayer _layer;
        private readonly List<(EditObject Item, int Index)> _gone;

        /// <summary>Removes the items now and remembers where they were.</summary>
        public Remove(ObjectLayer layer, IEnumerable<EditObject> items, string name = "Bulldoze") : base(name)
        {
            _layer = layer;
            var order = items.Select(e => (Item: e, Index: layer.Items.ToList().IndexOf(e))).Where(p => p.Index >= 0).OrderBy(p => p.Index).ToList();
            _gone = order;
            Redo(null!);
        }

        public override void Redo(IMapDocument doc) { foreach (var (e, _) in _gone.AsEnumerable().Reverse()) _layer.Remove(e); }
        public override void Undo(IMapDocument doc) { foreach (var (e, i) in _gone) _layer.Insert(e, i); }
    }

    /// <summary>Moves and turns objects.</summary>
    public sealed class Transform(ObjectLayer layer, IReadOnlyList<(EditObject Item, Pose Before, Pose After)> moves, string name = "Move") : ObjectCommand(name)
    {
        public override void Undo(IMapDocument doc) { foreach (var (e, b, _) in moves) layer.SetPose(e, b.Position, b.Yaw); }
        public override void Redo(IMapDocument doc) { foreach (var (e, _, a) in moves) layer.SetPose(e, a.Position, a.Yaw); }
    }

    /// <summary>Changes a record (name, type, flags): the inspector.</summary>
    public sealed class Edit(ObjectLayer layer, EditObject item, byte[] before, byte[] after, string name = "Edit object") : ObjectCommand(name)
    {
        public override void Undo(IMapDocument doc) => layer.SetData(item, before);
        public override void Redo(IMapDocument doc) => layer.SetData(item, after);
    }

    /// <summary>Renumbers several records at once (re-ordering checkpoints).</summary>
    public sealed class EditMany(ObjectLayer layer, IReadOnlyList<(EditObject Item, byte[] Before, byte[] After)> edits, string name) : ObjectCommand(name)
    {
        public override void Undo(IMapDocument doc) { foreach (var (e, b, _) in edits) layer.SetData(e, b); }
        public override void Redo(IMapDocument doc) { foreach (var (e, _, a) in edits) layer.SetData(e, a); }
    }

    /// <summary>Several commands as one step: a placement with the ground flattened under it and the road cut for it.</summary>
    public sealed class Group(string name, IReadOnlyList<IEditCommand> parts) : IEditCommand
    {
        public string Name { get; } = name;
        public int Size { get; } = parts.Sum(p => p.Size);
        public IReadOnlyList<IEditCommand> Parts => parts;
        public void Undo(IMapDocument doc) { for (int i = parts.Count - 1; i >= 0; i--) parts[i].Undo(doc); }
        public void Redo(IMapDocument doc) { foreach (var p in parts) p.Redo(doc); }
    }
}
