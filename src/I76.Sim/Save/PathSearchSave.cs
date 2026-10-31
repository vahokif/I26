using System.Numerics;
using I76.Sim.Save;

namespace I76.Sim.Ai;

/// <summary>
/// An A* search in progress (it runs 100 expansions a frame, so it can span a snapshot): its nodes in the order the
/// pool gave them out, each with its costs, parent, children and its place in the open AVL tree, so the tree comes
/// back with the same shape (a relaxed node sits where it was inserted, not where its f would put it).
/// </summary>
public sealed partial class PathSearch
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<PathSearch>()
            .Saved(nameof(Goal), nameof(Expansions), nameof(_all), nameof(_root), nameof(_start), nameof(_end), nameof(_sx), nameof(_sz))
            .Derived("the key tree, rebuilt from the nodes' Seen flags", nameof(_seen))
            .Structural("the static world's bridge tree, the same for the rebuilt mission", nameof(_links));
        a.For<BridgeLinks>().Derived("the bridge tree, built from the static world's decks", "_tree");
        a.For(typeof(BridgeLinks).GetNestedType("Entry", System.Reflection.BindingFlags.NonPublic)!).Derived("the bridge tree, built from the static world's decks", "FarX", "FarZ", "Bridge");
        a.For<Node>().Saved(nameof(Node.X), nameof(Node.Z), nameof(Node.G), nameof(Node.Step), nameof(Node.H), nameof(Node.F), nameof(Node.Parent),
            nameof(Node.Children), nameof(Node.Road), nameof(Node.Link), nameof(Node.Seen), nameof(Node.L), nameof(Node.R), nameof(Node.Bal), nameof(Node.Open));
    }

    internal void Save(SaveWriter w)
    {
        w.V2(Goal); w.Int(Expansions); w.Int(_sx); w.Int(_sz);
        var index = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        w.VarU((ulong)_all.Count);
        foreach (var n in _all) { index[n] = index.Count; w.Int(n.X); w.Int(n.Z); }
        int Ref(Node? n) => n is null ? -1 : index[n];
        foreach (var n in _all)
        {
            w.F64(n.G); w.F64(n.Step); w.F64(n.H); w.F64(n.F); w.Int(Ref(n.Parent));
            w.Int(n.Children?.Length ?? -1);
            if (n.Children is { } kids) foreach (var c in kids) w.Int(Ref(c));
            w.Bool(n.Road); w.Bool(n.Link); w.Bool(n.Seen); w.Bool(n.Open); w.Int(Ref(n.L)); w.Int(Ref(n.R)); w.Int(n.Bal);
        }
        w.Int(Ref(_root)); w.Int(Ref(_start)); w.Int(Ref(_end));
    }

    internal static PathSearch Load(SaveReader r, BridgeLinks? links)
    {
        var goal = r.V2();
        int exp = r.Int(), sx = r.Int(), sz = r.Int();
        var s = new PathSearch(goal, links, sx, sz) { Expansions = exp };
        int count = r.Count();
        if (count > PoolSize) throw new SnapshotFormatException($"a path search with {count} nodes");
        var nodes = new Node[count];
        for (int i = 0; i < count; i++) s._all.Add(nodes[i] = new Node(r.Int(), r.Int()));
        Node? At(int i) => i == -1 ? null : i >= 0 && i < count ? nodes[i] : throw new SnapshotFormatException($"path node {i} of {count}");
        foreach (var n in nodes)
        {
            n.G = r.F64(); n.Step = r.F64(); n.H = r.F64(); n.F = r.F64(); n.Parent = At(r.Int());
            int k = r.Int();
            if (k > 9) throw new SnapshotFormatException($"a path node with {k} children");
            if (k >= 0) { n.Children = new Node?[k]; for (int j = 0; j < k; j++) n.Children[j] = At(r.Int()); }
            n.Road = r.Bool(); n.Link = r.Bool(); n.Seen = r.Bool(); n.Open = r.Bool(); n.L = At(r.Int()); n.R = At(r.Int()); n.Bal = r.Int();
            if (n.Seen && !s._seen.TryAdd(n.Key, n)) throw new SnapshotFormatException("a path search with a node twice");
        }
        s._root = At(r.Int()); s._start = At(r.Int()); s._end = At(r.Int());
        return s;
    }
}
