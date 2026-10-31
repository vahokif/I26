using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Intersections;

/// <summary>
/// Where a piece stands: the model's corner (its local origin) in the ground plane and its heading. The engine's frame: the object's
/// forward (local +z) is (sin yaw, cos yaw) and its right (local +x) is (cos yaw, -sin yaw), the rows of the stored rotation.
/// </summary>
public readonly record struct PiecePose(Vector2 Origin, float Yaw)
{
    public Vector2 Ez => new(MathF.Sin(Yaw), MathF.Cos(Yaw));
    public Vector2 Ex => new(MathF.Cos(Yaw), -MathF.Sin(Yaw));
    public Vector2 ToWorld(Vector2 local) => Origin + Ex * local.X + Ez * local.Y;
    public Vector2 DirToWorld(Vector2 local) => Ex * local.X + Ez * local.Y;
    public float HeadingToWorld(float local) => local + Yaw;

    /// <summary>The pose with heading <paramref name="yaw"/> that puts the local point <paramref name="local"/> at <paramref name="world"/>.</summary>
    public static PiecePose Anchored(Vector2 world, Vector2 local, float yaw)
    {
        var p = new PiecePose(Vector2.Zero, yaw);
        return new PiecePose(world - p.DirToWorld(local), yaw);
    }

    /// <summary>The arm in the world: where a road meets the piece, and the unit vector leaving it.</summary>
    public (Vector2 Position, Vector2 Out) World(Arm a) => (ToWorld(a.Position), DirToWorld(a.Out));

    public static float Wrap(float a) { while (a > MathF.PI) a -= 2 * MathF.PI; while (a <= -MathF.PI) a += 2 * MathF.PI; return a; }
}

/// <summary>A piece on the map: its ODEF record (what the file holds) and what the editor knows of it.</summary>
public sealed class PlacedPiece
{
    public int Id { get; }
    public PieceDef Def { get; }
    /// <summary>The record: the model's corner at x, y, z (y is the ground's there, as the loader stands it), the yaw in the rotation.</summary>
    public MapObject Data { get; }
    /// <summary>Whether the map had it (and Save keeps its place in the file) or the editor made it.</summary>
    public bool FromFile { get; }

    internal PlacedPiece(int id, PieceDef def, MapObject data, bool fromFile) { Id = id; Def = def; Data = data; FromFile = fromFile; }

    public PiecePose Pose => new(new Vector2(Data.Position.X, Data.Position.Z), Data.Yaw);
    public Vector2 Origin => Pose.ToWorld(Def.Origin);
    public IEnumerable<(Vector2 Position, Vector2 Out)> Arms { get { var p = Pose; return Def.Arms.Select(p.World); } }
    public IEnumerable<Vector2[]> Footprint { get { var p = Pose; return Def.Polygons.Select(poly => poly.Select(p.ToWorld).ToArray()); } }

    /// <summary>The footprint's rectangle (x0, z0, x1, z1), a little wider.</summary>
    public Vector4 Bounds
    {
        get
        {
            var pts = Def.Polygons.Count > 0 ? Footprint.SelectMany(p => p).ToArray() : Arms.Select(a => a.Position).ToArray();
            return new Vector4(pts.Min(v => v.X) - 2, pts.Min(v => v.Y) - 2, pts.Max(v => v.X) + 2, pts.Max(v => v.Y) + 2);
        }
    }
}

public readonly record struct PieceChange(PlacedPiece Piece, Vector4 Bounds);

/// <summary>One change to the piece list, to be undone with the road edit it belongs to.</summary>
public abstract record PieceOp
{
    public sealed record Insert(int Index, PlacedPiece Piece) : PieceOp;
    public sealed record Remove(int Index, PlacedPiece Piece) : PieceOp;
    public sealed record Replace(int Index, PlacedPiece Before, PlacedPiece After) : PieceOp;

    public void Do(PieceStore s)
    {
        switch (this)
        {
            case Insert i: s.Insert(i.Index, i.Piece); break;
            case Remove r: s.RemoveAt(r.Index); break;
            case Replace p: s.Replace(p.Index, p.After); break;
        }
    }

    public void Undo(PieceStore s)
    {
        switch (this)
        {
            case Insert i: s.RemoveAt(i.Index); break;
            case Remove r: s.Insert(r.Index, r.Piece); break;
            case Replace p: s.Replace(p.Index, p.Before); break;
        }
    }
}

/// <summary>
/// The intersection pieces of the map while it is edited: the road-piece objects (ODEF type 0x50-0x5f) of the names the catalogue knows,
/// those the map has and those the road tool places. The other objects of the file stay with whoever holds the objects; Save puts these
/// back at their old places in the file (new ones at the end). Every road edit that makes, moves or removes a piece does so through
/// <see cref="PieceOp"/>s inside the same undo step as the road edit.
/// </summary>
public sealed class PieceStore
{
    private readonly List<PlacedPiece> _items = [];
    private int _nextId = 1, _saved;

    public PieceStore() { }

    /// <summary>The pieces among a file's objects, in file order.</summary>
    public PieceStore(IEnumerable<MapObject> objects)
    {
        foreach (var o in objects)
            if (Recognise(o) is { } def) _items.Add(new PlacedPiece(_nextId++, def, o.Clone(), true));
        _fileOrder = _items.Select(p => p.Id).ToList();
    }

    /// <summary>The definition if the object is a piece of the catalogue (a road-piece type and a known name).</summary>
    public static PieceDef? Recognise(MapObject o) => PieceCatalogue.IsPieceType(o.ObjectType) ? PieceCatalogue.Lookup(o.Name) : null;

    public IReadOnlyList<PlacedPiece> Items => _items;
    public int Count => _items.Count;
    public PlacedPiece this[int i] => _items[i];
    public int Version { get; private set; }
    public bool Dirty => Version != _saved;
    public void MarkSaved() => _saved = Version;
    public event Action<PieceChange>? Changed;

    /// <summary>A new piece standing at <paramref name="pose"/> with the ground's height <paramref name="y"/>.</summary>
    public PlacedPiece Make(PieceDef def, PiecePose pose, float y) =>
        new(_nextId++, def, MapObject.Create(def.Name, def.ObjectType, new Vector3(pose.Origin.X, y, pose.Origin.Y), pose.Yaw), false);

    /// <summary>The same piece (its place in the file) as another kind: another look or shape at a new pose.</summary>
    public PlacedPiece Remake(PlacedPiece old, PieceDef def, PiecePose pose, float y) =>
        new(old.Id, def, MapObject.Create(def.Name, def.ObjectType, new Vector3(pose.Origin.X, y, pose.Origin.Y), pose.Yaw), old.FromFile);

    public void Insert(int index, PlacedPiece p) { _items.Insert(Math.Clamp(index, 0, _items.Count), p); Version++; Changed?.Invoke(new(p, p.Bounds)); }
    public void RemoveAt(int index) { var p = _items[index]; _items.RemoveAt(index); Version++; Changed?.Invoke(new(p, p.Bounds)); }
    public void Replace(int index, PlacedPiece p)
    {
        var old = _items[index];
        _items[index] = p;
        Version++;
        Changed?.Invoke(new(p, RoadsBounds(old.Bounds, p.Bounds)));
    }

    private static Vector4 RoadsBounds(Vector4 a, Vector4 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));

    /// <summary>
    /// Puts the pieces into a project's mission: every piece object of the file the store knows is replaced, in its place, by the
    /// store's piece with the same number (dropped when it was taken away); the pieces made here follow at the end. Objects of other
    /// names (a J connector, a runway end) are left alone.
    /// </summary>
    public void WriteTo(MapDocument doc)
    {
        var fromFile = _items.Where(p => p.FromFile).ToDictionary(p => p.Id);
        // the file's recognised pieces were numbered in file order when the store was made, and ids are never reused
        int slot = 0;
        var objects = new List<MapObject>();
        var originals = _fileOrder;
        foreach (var o in doc.Objects)
        {
            if (Recognise(o) is null) { objects.Add(o); continue; }
            int id = slot < originals.Count ? originals[slot] : -1;
            slot++;
            if (fromFile.TryGetValue(id, out var p)) objects.Add(p.Data.Clone());
        }
        foreach (var p in _items.Where(p => !p.FromFile)) objects.Add(p.Data.Clone());
        doc.Objects.Clear();
        doc.Objects.AddRange(objects);
    }

    private readonly List<int> _fileOrder = [];
}
