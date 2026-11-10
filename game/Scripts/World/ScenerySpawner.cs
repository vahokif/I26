using Godot;
using I76.Formats.Assembly;
using I76.Game.Assets;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// Every ODEF placement: scenery (.sdf) and vehicles (.vcf), each definition
/// built once and instanced. Definitions placed more than once go through a
/// MultiMesh anchored at their placements' mean, which keeps instance
/// transforms small. Keywords (spawn, regen) get a marker. The camera-facing
/// cards are nodes of their own, turned every frame.
/// </summary>
public partial class ScenerySpawner : Node3D
{
    public int Loaded { get; private set; }
    public int Keywords { get; private set; }
    public int Failed { get; private set; }
    public List<string> FailedNames { get; } = [];
    /// <summary>Each placement's instance: a MultiMesh's (its index, its transform relative to the MultiMesh), else its own node (its transform). A placement drawn only as cards has its cards' anchor here.</summary>
    private readonly Dictionary<Placement, (Node3D Node, int Index, Transform3D Local)> _where = [];
    /// <summary>A placement's other instances, placed and hidden with it: the modern look's twin, its cards' anchor, its tree's tiers.</summary>
    private readonly Dictionary<Placement, List<(Node3D Node, int Index)>> _twin = [];
    private readonly HashSet<Node3D> _classic = [], _modernNodes = [];
    private bool _modern = true;
    private MissionWorld? _world;

    /// <summary>The group of the nodes the drop shadows leave out (Main.Casters): the cards, which the original never casts (keyed faces are skipped), and the placed trees.</summary>
    public const string NoDropShadow = "i76_no_drop_shadow";

    /// <summary>
    /// Where the burning wrecks' smoke rises (game coordinates, absolute): the base of the smoke faces of a model that draws
    /// its own (akwreck2's fire model, xsb1 smoke and xfr1 flame sprite planes). The modern look draws those faces out and DamageSmoke's billowing
    /// black smoke from these points instead; the 1997 look keeps the original's faces.
    /// </summary>
    public List<System.Numerics.Vector3> Smokers { get; } = [];

    /// <summary>The smoke planes' height and width (m) of the model whose smokers were last added: the plume the modern smoke matches.</summary>
    public (float Height, float Width) SmokeExtent => Extent;

    /// <summary>The look: true draws the modern meshes (no sprite smoke), false the original's.</summary>
    public bool Modern
    {
        get => _modern;
        set
        {
            _modern = value;
            foreach (var n in _classic) n.Visible = !value;
            foreach (var n in _modernNodes) n.Visible = value;
            foreach (var c in _cards) c.Node.Visible = (c.Looks & (value ? ModernLook : ClassicLook)) != 0;
            Clutter.Visible = !value;
        }
    }

    /// <summary>The flame planes' height and width (m) of the model whose smokers were last added (0 without flame planes): the fire the modern look matches.</summary>
    public (float Height, float Width) FlameExtent => FlameExt;
    private static (float Height, float Width) Extent, FlameExt;
    private static System.Numerics.Vector3? SmokeBase(AssembledModel model)
    {
        if (WreckSmoke.Measure(model) is not { } m) return null;
        Extent = (m.Height, m.Width);
        FlameExt = (m.FlameHeight, m.FlameWidth);
        return m.Base;
    }

    /// <summary>A placement's whole transform in this node's frame, from where its instance is kept.</summary>
    private Transform3D Full(Placement p)
    {
        var w = _where[p];
        return w.Node is MultiMeshInstance3D m ? new Transform3D(w.Local.Basis, w.Local.Origin + m.Position) : w.Local;
    }

    /// <summary>Puts a placement's instance at <paramref name="xf"/> (this node's frame), or hides it (a MultiMesh's shrunk to nothing where it stood).</summary>
    private void Put(Node3D node, int index, Transform3D xf, bool on = true)
    {
        if (node is MultiMeshInstance3D mmi)
            mmi.Multimesh.SetInstanceTransform(index, new Transform3D(on ? xf.Basis : Basis.Identity.Scaled(Vector3.Zero), xf.Origin - mmi.Position));
        else
        {
            node.Transform = xf;
            node.Visible = on && (_modernNodes.Contains(node) ? _modern : !_classic.Contains(node) || !_modern);
        }
    }

    /// <summary>Moves a placement's instance to <paramref name="xf"/>, shown or hidden as it was.</summary>
    private static void Move(Node3D node, int index, Transform3D xf)
    {
        if (node is MultiMeshInstance3D mmi)
        {
            bool shown = mmi.Multimesh.GetInstanceTransform(index).Basis.Determinant() != 0;
            mmi.Multimesh.SetInstanceTransform(index, new Transform3D(shown ? xf.Basis : Basis.Identity.Scaled(Vector3.Zero), xf.Origin - mmi.Position));
        }
        else node.Transform = xf;
    }

    /// <summary>
    /// A destroyed structure: its instance goes, replaced by the destroyed model if there is one: <paramref name="destroyed"/>, the mesh
    /// of its parts but the cards, and the cards of <paramref name="model"/> (the pine's stump), drawn the same in both looks.
    /// </summary>
    public void Destroy(Placement p, Mesh? destroyed, AssembledModel? model = null, Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skipFace = null)
    {
        // its spinners go with it: their damaged slots are NULL, so the original draws nothing there
        if (_spinOf.TryGetValue(p, out var sr))
            for (int i = sr.Start; i < sr.Start + sr.Count; i++) _spins[i].Node.Visible = false;
        if (!_where.TryGetValue(p, out var w)) return;
        var full = Full(p);
        Put(w.Node, w.Index, full, on: false);
        if (_twin.TryGetValue(p, out var ts)) foreach (var t in ts) Put(t.Node, t.Index, full, on: false);
        // a structure losing nodes one by one: each look replaces the last
        if (_destroyedLook.Remove(p, out var was)) RemoveLook(was);
        var cards = model is not null && p.Definition is { } def && CardNamesOf(def) is { } names ? CardMeshes(model, names, skipFace, null, false) : null;
        if (destroyed is null && cards is not { Count: > 0 }) return;
        Node3D look = destroyed is null ? new Node3D() : new MeshInstance3D { Mesh = destroyed };
        look.Transform = Coords.ToGodot(p.Object.Rotation, _world?.GroundedPosition(p) ?? p.Object.Position);
        look.Name = p.Object.Name + "_destroyed";
        AddChild(look);
        if (cards is not null) AddCards(look, cards);
        _destroyedLook[p] = look;
    }

    /// <summary>
    /// The map editor: the ground changed under the rectangle (game coordinates, metres); every placement standing in it is set on
    /// the new ground (MissionWorld.GroundedPosition), its instance moved. Gates and spinners stay where they were placed.
    /// Returns how many moved.
    /// </summary>
    public int Reground(float gx0, float gz0, float gx1, float gz1)
    {
        if (_world is null) return 0;
        int moved = 0;
        foreach (var p in _where.Keys.ToList())
        {
            var o = p.Object.Position;
            if (o.X < gx0 || o.X > gx1 || o.Z < gz0 || o.Z > gz1) continue;
            var (node, index, local) = _where[p];
            var xf = Coords.ToGodot(p.Object.Rotation, _world.GroundedPosition(p));
            Vector3 anchor = node is MultiMeshInstance3D ? node.Position : Vector3.Zero;
            var now = new Transform3D(xf.Basis, xf.Origin - anchor);
            if (now.Origin.IsEqualApprox(local.Origin)) continue;
            _where[p] = (node, index, now);
            if (_editHidden.Contains(p)) continue;   // bulldozed in the editor: stands in its new place when undone
            Move(node, index, xf);
            if (_twin.TryGetValue(p, out var ts)) foreach (var t in ts) Move(t.Node, t.Index, xf);
            if (_destroyedLook.TryGetValue(p, out var look)) look.Transform = xf;
            _cardsTurned = false;
            moved++;
        }
        return moved;
    }

    private readonly Dictionary<Placement, Node3D> _destroyedLook = [];

    private void RemoveLook(Node3D look)
    {
        _cards.RemoveAll(c => c.Node.GetParent() == look);
        look.QueueFree();
    }

    // ---- the map editor: placements shown, hidden and moved while it runs (game/Scripts/Editor/Objects) ----

    private ModelAssembler? _asm;
    private MeshBuilder? _meshes;
    private readonly Dictionary<string, (ArrayMesh? Mesh, AssembledModel? Model)> _editMesh = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model and mesh of a definition for the editor (built once, ground faces left out as the loader does for grounded scenery); null if it has none.</summary>
    public (ArrayMesh Mesh, AssembledModel Model)? EditMeshOf(string def)
    {
        if (_asm is null || _meshes is null) return null;
        if (!_editMesh.TryGetValue(def, out var e))
        {
            var model = _asm.Assemble(def);
            ArrayMesh? mesh = null;
            if (model is not null && model.Parts.Count > 0)
            {
                float lowest = GroundDecals.LowestY(model);
                bool ground = !def.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase);
                mesh = _meshes.Build(model, skipFace: ground ? (part, f) => GroundDecals.IsGroundFace(part, f, lowest) : null);
            }
            _editMesh[def] = e = (mesh, model);
        }
        return e.Mesh is { } m ? (m, e.Model!) : null;
    }

    private Transform3D EditTransform(Placement p) => Coords.ToGodot(p.Object.Rotation, _world!.GroundedPosition(p));

    /// <summary>Draws a placement: shows it again if it was hidden, else makes an instance of its definition. Keywords (spawn, regen) have nothing to draw.</summary>
    public bool EditShow(Placement p)
    {
        if (_world is null) return false;
        if (_where.TryGetValue(p, out _)) { EditVisible(p, true); return true; }
        if (p.Definition is not { } def || EditMeshOf(def) is not { } m) return false;
        var xf = EditTransform(p);
        var names = CardNamesOf(def);
        if (names is null)
        {
            var mi = new MeshInstance3D { Mesh = m.Mesh, Transform = xf, Name = def };
            (RoadPieceObjects.Painted(p.Object.ObjectType, m.Model) ? RoadPieces : this).AddChild(mi);
            _where[p] = (mi, 0, xf);
            return true;
        }
        // its cards turn on their own (the editor's mesh, which has them flat, is only for picking); a tree in the modern look
        float lowest = GroundDecals.LowestY(m.Model);
        Func<ModelPart, I76.Formats.Geo.GeoFace, bool> skip = (part, f) => GroundDecals.IsGroundFace(part, f, lowest);
        var tree = Trees.Has(def, m.Model, names);
        var rest = _meshes!.Build(m.Model, include: part => !names.Contains(part.NodeName), skipFace: skip);
        var cards = AddCards(xf, CardMeshes(m.Model, names, skip, null, tree));
        if (rest is not null)
        {
            var mi = new MeshInstance3D { Mesh = rest, Transform = xf, Name = def };
            AddChild(mi);
            _where[p] = (mi, 0, xf);
            (_twin[p] = []).Add((cards, 0));
        }
        else _where[p] = (cards, 0, xf);
        if (tree) foreach (var t in Trees.Add(def, m.Model, names, [p], [xf])) Twin(p, t.Node, t.Index, modern: true);
        return true;
    }

    /// <summary>Hides a placement (bulldozed); it keeps its instance so that undo can show it.</summary>
    public void EditHide(Placement p) => EditVisible(p, false);

    private readonly HashSet<Placement> _editHidden = [];

    private void EditVisible(Placement p, bool on)
    {
        if (!_where.TryGetValue(p, out var w)) return;
        if (on) _editHidden.Remove(p); else _editHidden.Add(p);
        var full = Full(p);
        Put(w.Node, w.Index, full, on);
        if (_twin.TryGetValue(p, out var ts)) foreach (var t in ts) Put(t.Node, t.Index, full, on);
        if (_spinOf.TryGetValue(p, out var sr)) for (int i = sr.Start; i < sr.Start + sr.Count; i++) _spins[i].Node.Visible = on;
        foreach (var g in _gates) if (g.Placement == p) g.Node.Visible = on;
    }

    /// <summary>A placement was moved or turned (the layer made a new one): its instance, spinners and gates follow and are known by the new placement from now on.</summary>
    public void EditMove(Placement old, Placement now)
    {
        if (_world is null || !_where.Remove(old, out var w)) return;
        var oldXf = EditTransform(old);
        var xf = EditTransform(now);
        Vector3 anchor = w.Node is MultiMeshInstance3D ? w.Node.Position : Vector3.Zero;
        var local = new Transform3D(xf.Basis, xf.Origin - anchor);
        _where[now] = (w.Node, w.Index, local);
        bool hidden = _editHidden.Remove(old);
        if (hidden) _editHidden.Add(now);
        Move(w.Node, w.Index, xf);
        if (_twin.Remove(old, out var ts))
        {
            _twin[now] = ts;
            foreach (var t in ts) Move(t.Node, t.Index, xf);
        }
        if (_spinOf.Remove(old, out var sr))
        {
            _spinOf[now] = sr;
            for (int i = sr.Start; i < sr.Start + sr.Count; i++)
            {
                var s = _spins[i];
                var pivot = oldXf.AffineInverse() * s.Rest.Origin;
                var rest = new Transform3D(xf.Basis, xf * pivot);
                _spins[i] = s with { Rest = rest };
                s.Node.Transform = rest;
            }
        }
        for (int i = 0; i < _gates.Count; i++)
        {
            var g = _gates[i];
            if (g.Placement != old) continue;
            var off = oldXf.AffineInverse() * g.Rest.Origin;
            var rest = new Transform3D(xf.Basis, xf * off);
            _gates[i] = g with { Placement = now, Rest = rest };
            g.Node.Transform = rest;
        }
        if (_destroyedLook.Remove(old, out var look)) { _destroyedLook[now] = look; look.Transform = xf; }
        _cardsTurned = _cardsLit = false;
    }

    /// <summary>Gives the editor the instances' transform of a placement (Godot space, local to this node), or null if it is not drawn.</summary>
    public bool IsDrawn(Placement p) => _where.ContainsKey(p);

    /// <summary>Another instance of a placement, placed and hidden with it; in one look only if <paramref name="modern"/> says which.</summary>
    private void Twin(Placement p, Node3D node, int index, bool? modern = null)
    {
        if (!_twin.TryGetValue(p, out var l)) _twin[p] = l = [];
        l.Add((node, index));
        if (modern is null) return;
        (modern.Value ? _modernNodes : _classic).Add(node);
        node.Visible = modern.Value == _modern;
    }

    /// <summary>A night mission's: the modern look lights the structures' lamps (StructureLamps).</summary>
    public bool Night { get; init; }

    /// <summary>I76_SELF_LIT (tuning): <c>0</c> leaves the modern look's self-lit faces and structure lamps out (before and after captures), <c>noshadow</c> the lamps' shadows.</summary>
    private static readonly string? SelfLitHook = System.Environment.GetEnvironmentVariable("I76_SELF_LIT");

    /// <summary>ODEF class 4, the road signs: their self-lit faces (the original draws them at full brightness to be read at night) stay as they are in the modern look, which the headlamps light.</summary>
    public const uint SignType = 4;

    public void Spawn(MissionWorld world, ModelAssembler asm, MeshBuilder builder, bool skipVehicles = false)
    {
        _world = world; _asm = asm; _meshes = builder;
        AddChild(Trees);
        AddChild(Clutter);
        Clutter.Visible = !_modern;
        Clutter.Load(world, asm, builder);
        var placements = world.Placements.Concat(world.LinePlacements);
        if (skipVehicles) placements = placements.Where(p => p.Definition?.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) != true);
        // a definition's grounded placements share a mesh without its ground faces (painted into the terrain, GroundDecals);
        // its road pieces go apart from its other placements, by each placement's own type (SceneryGroup)
        var groups = placements.GroupBy(SceneryGroup.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            if (g.Key.Length == 0)
            {
                foreach (var p in g)
                {
                    if (p.Object.IsKeyword) { AddMarker(p); Keywords++; }
                    else if (p.Object.Name.Length == 0) Keywords++; // empty names: placeholders, nothing to draw
                    else { Failed++; FailedNames.Add(p.Object.Name); }
                }
                continue;
            }
            bool grounded = g.Key.EndsWith(SceneryGroup.Ground, StringComparison.Ordinal);
            bool roadPiece = g.Key.EndsWith(SceneryGroup.Piece, StringComparison.Ordinal);
            var def = grounded ? g.Key[..^SceneryGroup.Ground.Length] : roadPiece ? g.Key[..^SceneryGroup.Piece.Length] : g.Key;
            var model = asm.Assemble(def);
            // a road piece shows one node of its model and is no object of the world (no spinners, no gates: RoadPieceObjects);
            // a flat one is painted into the terrain, any other drawn
            if (roadPiece && model is not null) model = RoadPieceObjects.Drawn(model);
            bool painted = roadPiece && model is not null && RoadPieceObjects.IsFlat(model);
            float lowest = model is null ? 0 : GroundDecals.LowestY(model);
            Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skip = grounded ? (part, f) => GroundDecals.IsGroundFace(part, f, lowest) : null;
            var spinners = model is null || roadPiece ? null : SpinnersOf(asm.Vfs, def, model);
            var gates = model is null || roadPiece ? null : GatesOf(asm.Vfs, def);
            // the camera-facing cards are drawn on their own
            var cardNames = model is null ? null : CardNamesOf(def);
            Func<ModelPart, bool>? include = spinners is null && gates is null && cardNames is null ? null
                : part => !(spinners?.Any(s => s.Owns(part.NodeName)) ?? false) && !(gates?.Owns(part.NodeName) ?? false) && !(cardNames?.Contains(part.NodeName) ?? false);
            // a structure's self-lit faces shine at night in the modern look (MeshBuilder.SelfLit), its lamps light what is under them
            bool selfLit = SelfLitHook != "0" && !roadPiece && g.Any(p => p.Object.ObjectType != SignType);
            var mesh = model is null ? null : builder.Build(model, include: include, skipFace: skip, selfLit: selfLit);
            if (mesh is null && gates is not null && cardNames is null)
            {
                // a model that is nothing but its gate (T12's bcaikgat): no static mesh, only the sliding one
                var only = g.ToList();
                var xfo = only.Select(p => Coords.ToGodot(p.Object.Rotation, world.GroundedPosition(p))).ToList();
                AddGates(gates, model!, builder, skip, only, xfo, this);
                Loaded += only.Count;
                continue;
            }
            if (mesh is null && cardNames is null && roadPiece && model is not null)
            {
                Loaded += g.Count();   // a road piece whose node has nothing to draw (a bridge's marker): the original shows nothing either
                continue;
            }
            if (mesh is null && cardNames is null)
            {
                Failed += g.Count();
                FailedNames.Add(def);
                continue;
            }
            var list = g.ToList();
            ArrayMesh? modernMesh = null;
            Func<I76.Formats.Geo.GeoFace, bool>? modernDrops = null;
            if (model is not null && SmokeBase(model) is { } smokeAt)
            {
                modernDrops = WreckSmoke.IsSpriteFace;
                modernMesh = builder.Build(model, include: include, skipFace: (part, f) => WreckSmoke.IsSpriteFace(f) || (grounded && GroundDecals.IsGroundFace(part, f, lowest)), selfLit: selfLit);
                foreach (var pl in list)
                {
                    Smokers.Add(Coords.ToGame(Coords.ToGodot(pl.Object.Rotation, world.GroundedPosition(pl)) * Coords.ToGodot(smokeAt)));
                    GD.Print($"static smoker: {def} at {Smokers[^1]} flame {FlameExt.Height:0.0} x {FlameExt.Width:0.0} m");
                }
            }
            // a tree's card gives way to a real tree in the modern look (docs/modern-vegetation.md, Placed trees)
            bool tree = cardNames is not null && Trees.Has(def, model!, cardNames);
            var cards = cardNames is null ? null : CardMeshes(model!, cardNames, skip, modernDrops, tree);
            Node parent = painted ? RoadPieces : this;
            if (painted && RoadPieces.GetParent() is null) AddChild(RoadPieces);
            var xf = list.Select(p => Coords.ToGodot(p.Object.Rotation, world.GroundedPosition(p))).ToList();
            var anchor = xf.Aggregate(Vector3.Zero, (a, t) => a + t.Origin) / xf.Count;
            Node3D Place(ArrayMesh m)
            {
                if (xf.Count == 1)
                {
                    var mi = new MeshInstance3D { Mesh = m, Transform = xf[0], Name = def };
                    parent.AddChild(mi);
                    return mi;
                }
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = m, InstanceCount = xf.Count };
                for (int i = 0; i < xf.Count; i++)
                    mm.SetInstanceTransform(i, new Transform3D(xf[i].Basis, xf[i].Origin - anchor));
                var mmi = new MultiMeshInstance3D { Multimesh = mm, Position = anchor, Name = def };
                parent.AddChild(mmi);
                return mmi;
            }
            var node = mesh is null ? null : Place(mesh);
            for (int i = 0; i < xf.Count; i++)
            {
                var cardAnchor = cards is null ? null : AddCards(xf[i], cards);
                if (node is not null)
                {
                    _where[list[i]] = (node, i, xf.Count == 1 ? xf[0] : new Transform3D(xf[i].Basis, xf[i].Origin - anchor));
                    if (cardAnchor is not null) Twin(list[i], cardAnchor, 0);
                }
                else _where[list[i]] = (cardAnchor!, 0, xf[i]);
            }
            if (modernMesh is not null && node is not null)
            {
                var alt = Place(modernMesh);
                _classic.Add(node); _modernNodes.Add(alt);
                node.Visible = !_modern; alt.Visible = _modern;
                for (int i = 0; i < xf.Count; i++) Twin(list[i], alt, i);
            }
            if (tree)
                foreach (var t in Trees.Add(def, model!, cardNames!, list, xf)) Twin(list[t.Placement], t.Node, t.Index, modern: true);
            if (Night && selfLit && model is { Kind: "sdf" } && node is not null && StructureLamps.Build(model, builder, include, shadows: SelfLitHook != "noshadow") is { } lamps)
            {
                for (int i = 0; i < xf.Count; i++)
                {
                    var each = i == xf.Count - 1 ? lamps : (Node3D)lamps.Duplicate();
                    each.Transform = xf[i];
                    AddChild(each);
                    Twin(list[i], each, 0, modern: true);
                }
            }
            if (spinners is not null) AddSpinners(spinners, model!, builder, skip, list, xf, parent);
            if (gates is not null) AddGates(gates, model!, builder, skip, list, xf, parent);
            Loaded += xf.Count;
        }
        SetProcess(true);
    }

    // ---- camera-facing cards ----

    private const byte ClassicLook = 1, ModernLook = 2;

    /// <summary>One card of a definition: its node, the node's origin and orientation in the model (game frame), its meshes in the node's own frame for each look (null: not drawn in that look; the same mesh when both draw it alike).</summary>
    private sealed record CardMesh(string Node, Affine Rest, ArrayMesh? Classic, ArrayMesh? Modern);

    /// <summary>A card drawn: its node (turned each frame), the looks it shows in, and its node's own orientation in its anchor's frame (the 1997 look lights it by that).</summary>
    private sealed record Card(MeshInstance3D Node, byte Looks, Basis Rest);

    private readonly List<Card> _cards = [];
    private readonly Dictionary<string, HashSet<string>?> _cardDefs = new(StringComparer.OrdinalIgnoreCase);
    private System.Numerics.Vector3 _cardForward;
    private bool _cardsTurned, _cardsLit;
    private static readonly StringName CardX = "card_x", CardY = "card_y", CardZ = "card_z";

    /// <summary>The placed trees of the modern look (World/PlacedTrees.cs).</summary>
    public PlacedTrees Trees { get; } = new() { Name = "Trees" };

    /// <summary>The 1997 look's ground clutter; the modern look hides it.</summary>
    public GroundClutterView Clutter { get; } = new() { Name = "Clutter", Visible = false };

    /// <summary>The names of a definition's nodes the engine draws turned toward the camera (GEO record +0x60 &amp; 0x40), or null for none.</summary>
    private HashSet<string>? CardNamesOf(string def)
    {
        if (_cardDefs.TryGetValue(def, out var known)) return known;
        HashSet<string>? names = null;
        if (_asm?.Vfs.TryRead(def) is { } d && Bwd2Def(d)?.BillboardNodes() is { Count: > 0 } b) names = b;
        return _cardDefs[def] = names;
    }

    private static I76.Formats.Bwd2.Bwd2File? Bwd2Def(byte[] d)
    {
        try { return new I76.Formats.Bwd2.Bwd2File(d); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Whether a node of the placement's definition is a card (left out of its destroyed look's mesh: <see cref="Destroy"/> draws it).</summary>
    public bool IsCardPart(Placement p, string node) => p.Definition is { } d && CardNamesOf(d) is { } names && names.Contains(node);

    /// <summary>
    /// A model's cards: each card part's mesh in its node's own frame (the faces <paramref name="skip"/> keeps, tested where they
    /// stand in the model); the modern look's leaves out <paramref name="modernDrops"/>' faces, or the whole card for a tree.
    /// </summary>
    private List<CardMesh> CardMeshes(AssembledModel model, HashSet<string> names, Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skip,
        Func<I76.Formats.Geo.GeoFace, bool>? modernDrops, bool modernNone)
    {
        var o = new List<CardMesh>();
        foreach (var part in model.Parts)
        {
            if (!names.Contains(part.NodeName)) continue;
            var own = new AssembledModel { Definition = model.Definition, Kind = model.Kind, ObjectCode = model.ObjectCode };
            own.TmtNames.AddRange(model.TmtNames);
            own.VtfMaps.AddRange(model.VtfMaps);
            own.Parts.Add(new ModelPart(part.NodeName, part.Mesh, Affine.Identity));
            bool Skip(I76.Formats.Geo.GeoFace f) => skip?.Invoke(part, f) == true;
            var classic = _meshes!.Build(own, skipFace: (_, f) => Skip(f));
            var modern = modernNone ? null : modernDrops is null ? classic : _meshes.Build(own, skipFace: (_, f) => Skip(f) || modernDrops(f));
            if (classic is null && modern is null) continue;
            o.Add(new CardMesh(part.NodeName, part.ToModel, classic, modern));
        }
        return o;
    }

    /// <summary>A placement's cards under a new anchor at <paramref name="xf"/>.</summary>
    private Node3D AddCards(Transform3D xf, List<CardMesh> cards)
    {
        var a = new Node3D { Transform = xf, Name = "Cards" };
        AddChild(a);
        AddCards(a, cards);
        return a;
    }

    /// <summary>The cards as children of <paramref name="anchor"/> (the placement's frame): at their nodes' origins, turned by <see cref="_Process"/>.</summary>
    private void AddCards(Node3D anchor, List<CardMesh> cards)
    {
        foreach (var c in cards)
        {
            var rest = Coords.ToGodot(new Affine(c.Rest.R, System.Numerics.Vector3.Zero)).Basis;
            void One(ArrayMesh mesh, byte looks)
            {
                var mi = new MeshInstance3D { Mesh = mesh, Position = Coords.ToGodot(c.Rest.T), Name = c.Node, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                mi.AddToGroup(NoDropShadow);
                mi.Visible = (looks & (_modern ? ModernLook : ClassicLook)) != 0;
                anchor.AddChild(mi);
                _cards.Add(new Card(mi, looks, rest));
            }
            if (c.Classic is not null && ReferenceEquals(c.Classic, c.Modern)) One(c.Classic, ClassicLook | ModernLook);
            else
            {
                if (c.Classic is not null) One(c.Classic, ClassicLook);
                if (c.Modern is not null) One(c.Modern, ModernLook);
            }
        }
        _cardsTurned = _cardsLit = false;
        SetProcess(true);
    }

    /// <summary>
    /// The cards turned to the frame's camera: each card's world rotation is Billboard.EngineRows of the view's forward (the
    /// engine's), its node's own and its placement's dropped; its position stays. The 1997 look's light on it
    /// keeps its node's real orientation (object.gdshaderinc, card_x/y/z), as the original's light is handed the node's matrix.
    /// </summary>
    private void TurnCards()
    {
        if (_cards.Count == 0 || GetViewport()?.GetCamera3D() is not { } cam) return;
        var fwd = Coords.ToGame(GlobalBasis.Inverse() * -cam.GlobalBasis.Z);
        if (_cardsTurned && fwd == _cardForward) return;
        _cardForward = fwd; _cardsTurned = true;
        var turn = Coords.ToGodot(Billboard.EngineRows(fwd));
        foreach (var c in _cards)
        {
            if (c.Node.GetParent() is not Node3D anchor) continue;
            c.Node.Basis = anchor.Basis.Inverse() * turn;
            if (_cardsLit) continue;
            // the node's own orientation in this node's frame (the anchors hang from it, unrotated), as columns
            var lit = anchor.Basis * c.Rest;
            c.Node.SetInstanceShaderParameter(CardX, lit.Column0);
            c.Node.SetInstanceShaderParameter(CardY, lit.Column1);
            c.Node.SetInstanceShaderParameter(CardZ, lit.Column2);
        }
        _cardsLit = true;
    }

    /// <summary>
    /// A definition's gates: its type-7 nodes, which slide when the script
    /// says, and the nodes under them. <see cref="Names"/> are the type-7 nodes themselves.
    /// </summary>
    private sealed record Gates(string[] Names, Func<string, bool> Owns);

    /// <summary>One placed gate: its mesh, its rest transform, its placement and its node's name.</summary>
    private readonly record struct GateInstance(MeshInstance3D Node, Transform3D Rest, Placement Placement, string Name);

    private readonly List<GateInstance> _gates = [];

    /// <summary>
    /// Where a placement's gate node stands against where it was placed (game axes, metres): the sim's slide,
    /// set by whoever holds the sim (StaticObject's gate nodes). Unset, the gates stand where they were placed.
    /// </summary>
    public Func<Placement, string, System.Numerics.Vector3>? GateShift { get; set; }

    /// <summary>Whether a part of the placement's definition is drawn as a gate (left out of its other meshes).</summary>
    public bool IsGatePart(Placement p, string node) => _gateDefs.TryGetValue(p.Definition ?? "", out var g) && g.Owns(node);

    private readonly Dictionary<string, Gates> _gateDefs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The definition's gates, or null for none.</summary>
    private Gates? GatesOf(I76.Formats.Vfs.AssetVfs vfs, string def)
    {
        if (_gateDefs.TryGetValue(def, out var known)) return known;
        if (!def.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase) || vfs.TryRead(def) is not { } d) return null;
        var f = new I76.Formats.Bwd2.Bwd2File(d);
        var names = f.NodesOfType(7);
        if (names.Count == 0) return null;
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in f.SceneNodes().Where(t => t.Tag == "SGEO").SelectMany(t => t.Nodes)) parent.TryAdd(n.Name, n.Parent);
        bool Owns(string node)
        {
            for (int k = 0; k < 64; k++)
            {
                if (names.Contains(node)) return true;
                if (!parent.TryGetValue(node, out var up) || up.Equals(node, StringComparison.OrdinalIgnoreCase)) return false;
                node = up;
            }
            return false;
        }
        return _gateDefs[def] = new Gates([.. names], Owns);
    }

    /// <summary>Each placement's gates as their own nodes, for <see cref="_Process"/> to slide.</summary>
    private void AddGates(Gates gates, AssembledModel model, MeshBuilder builder, Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skip,
        List<Placement> list, List<Transform3D> xf, Node parent)
    {
        foreach (var name in gates.Names)
        {
            // the node and what hangs under it; its 'A' twin (the destroyed slot) is another node, not drawn here
            bool Mine(string node) => gates.Owns(node) && (node.Equals(name, StringComparison.OrdinalIgnoreCase) || !gates.Names.Any(n => n.Equals(node, StringComparison.OrdinalIgnoreCase)));
            if (builder.Build(model, include: part => Mine(part.NodeName), skipFace: skip) is not { } m) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var mi = new MeshInstance3D { Mesh = m, Transform = xf[i], Name = list[i].Object.Name + "_gate" };
                parent.AddChild(mi);
                _gates.Add(new GateInstance(mi, xf[i], list[i], name));
            }
        }
    }

    /// <summary>
    /// A spinner: a type-10 node and the nodes under it, which the
    /// original turns every frame about the node's own axes. <see cref="Pivot"/> is the
    /// node's frame in the model, <see cref="Revs"/> its rate in revolutions per second about its x, y, z.
    /// </summary>
    private sealed record Spinner(Affine Pivot, System.Numerics.Vector3 Revs, Func<string, bool> Owns);

    /// <summary>One placed spinner: its node, its rest transform, the axis it turns about there (Godot, unit) and its rate (rad/s).</summary>
    private readonly record struct SpinInstance(MeshInstance3D Node, Transform3D Rest, Vector3 Axis, float Rate);

    private readonly List<SpinInstance> _spins = [];
    private readonly Dictionary<Placement, (int Start, int Count)> _spinOf = [];
    private double _spinClock;

    /// <summary>The definition's spinners, or null for none (every definition but the four gas-station signs).</summary>
    private static List<Spinner>? SpinnersOf(I76.Formats.Vfs.AssetVfs vfs, string def, AssembledModel model)
    {
        if (!def.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase) || vfs.TryRead(def) is not { } d) return null;
        var f = new I76.Formats.Bwd2.Bwd2File(d);
        var rates = f.Spinners();
        if (rates.Count == 0) return null;
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in f.SceneNodes().Where(t => t.Tag == "SGEO").SelectMany(t => t.Nodes)) parent.TryAdd(n.Name, n.Parent);
        var o = new List<Spinner>();
        foreach (var (name, revs) in rates)
        {
            if (revs == System.Numerics.Vector3.Zero || model.Parts.FirstOrDefault(p => p.NodeName.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } top) continue;
            bool Owns(string node)
            {
                for (int k = 0; k < 64; k++)
                {
                    if (node.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                    if (!parent.TryGetValue(node, out var up) || up.Equals(node, StringComparison.OrdinalIgnoreCase)) return false;
                    node = up;
                }
                return false;
            }
            o.Add(new Spinner(top.ToModel, revs, Owns));
        }
        return o.Count == 0 ? null : o;
    }

    /// <summary>Each placement's spinners as their own nodes, about their pivots, for <see cref="_Process"/> to turn.</summary>
    private void AddSpinners(List<Spinner> spinners, AssembledModel model, MeshBuilder builder, Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skip,
        List<Placement> list, List<Transform3D> xf, Node parent)
    {
        var built = new List<(Mesh Mesh, Vector3 Pivot, Vector3 Axis, float Rate)>();
        foreach (var s in spinners)
        {
            if (builder.Build(model, include: part => s.Owns(part.NodeName), origin: s.Pivot.T, skipFace: skip) is not { } m) continue;
            // the node's own axes in the model; the engine turns it by -(ω·dt) in its left-handed frame, which is +ω·dt about the mirrored axis here
            var w = s.Pivot.Rotate(s.Revs);
            built.Add((m, Coords.ToGodot(s.Pivot.T), Coords.ToGodot(w).Normalized(), w.Length() * Mathf.Tau));
        }
        if (built.Count == 0) return;
        for (int i = 0; i < list.Count; i++)
        {
            int start = _spins.Count;
            foreach (var (mesh, pivot, axis, rate) in built)
            {
                var rest = new Transform3D(xf[i].Basis, xf[i] * pivot);
                var mi = new MeshInstance3D { Mesh = mesh, Transform = rest, Name = list[i].Object.Name + "_spin" };
                parent.AddChild(mi);
                _spins.Add(new SpinInstance(mi, rest, axis, rate));
            }
            _spinOf[list[i]] = (start, _spins.Count - start);
        }
    }

    /// <summary>
    /// The cards turn to the camera (<see cref="TurnCards"/>). The spinners turn on the render clock, all in step from their
    /// placed pose as the original's do (each turned by its rate times the raw frame delta, which the original's frame clock keeps within 0.001..0.2 s).
    /// </summary>
    public override void _Process(double delta)
    {
        TurnCards();
        if (GateShift is { } shift)
            foreach (var g in _gates)
                g.Node.Transform = new Transform3D(g.Rest.Basis, g.Rest.Origin + Coords.ToGodot(shift(g.Placement, g.Name)));
        _spinClock += Math.Clamp(delta, 0.001, 0.2);
        for (int i = 0; i < _spins.Count; i++)
        {
            var s = _spins[i];
            if (!s.Node.Visible) continue;
            float a = (float)(_spinClock * s.Rate % Math.Tau);
            s.Node.Transform = new Transform3D(s.Rest.Basis * new Basis(s.Axis, a), s.Rest.Origin);
        }
    }

    /// <summary>Road pieces: intersections, junctions, runway ends (ODEF types 0x50-0x5f, <see cref="RoadPieceObjects"/>).</summary>
    public static bool IsRoadPiece(uint type) => RoadPieceObjects.IsRoadPiece(type);

    /// <summary>The painted road-piece placements (flat models, <see cref="RoadPieceObjects.Painted"/>): painted into the terrain in both looks; their meshes are not drawn.</summary>
    public Node3D RoadPieces { get; } = new() { Name = "RoadPieces" };

    /// <summary>The keyword placements' debug markers (spawn points, regen points): shown in the flyover, hidden in play.</summary>
    public Node3D Markers { get; } = new() { Name = "Markers" };

    /// <summary>The markers' node is only added to the tree when there is a marker; one that never was would stay a stray node for the rest of the run.</summary>
    public override void _ExitTree()
    {
        if (Markers.GetParent() is null && IsInstanceValid(Markers)) Markers.Free();
    }

    private void AddMarker(Placement p)
    {
        if (Markers.GetParent() is null) AddChild(Markers);
        var colour = p.Object.Name.Equals("spawn", StringComparison.OrdinalIgnoreCase) ? Colors.Lime : Colors.Orange;
        var mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 6 };
        mesh.Material = new StandardMaterial3D { AlbedoColor = colour, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        Markers.AddChild(new MeshInstance3D
        {
            Mesh = mesh, Name = p.Object.Name,
            Transform = Coords.ToGodot(p.Object.Rotation, p.Object.Position).Translated(new Vector3(0, 3, 0)),
        });
    }
}
