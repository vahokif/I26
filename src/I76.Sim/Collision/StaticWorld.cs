using System.Numerics;
using I76.Formats.Assembly;
using I76.Sim.Terrain;
using I76.Sim.Math;

namespace I76.Sim.Collision;

/// <summary>A convex polygon in world space: outward normal n, plane n·p + d = 0.</summary>
public sealed record Poly(Vector3 N, float D, Vector3[] V)
{
    /// <summary>The structure node it belongs to (<see cref="StaticObject.Nodes"/>), or −1 for the placement's root.</summary>
    public int Node { get; init; } = -1;
}

/// <summary>
/// One node of a structure (an SGEO record with isRoot 0): its own
/// hit points (from the record for types 2, 3, 4, 0xb and 0xc, else the class-2
/// constructor's 400), type, flags and
/// parent, −1 when that is the placement's root.
/// </summary>
public sealed class StructureNode
{
    public required string Name { get; init; }
    public int Parent { get; init; } = -1;
    public uint Type { get; init; }
    public uint Flags { get; init; }
    public int Hp, MaxHp;
    /// <summary>Flag 0x200 on the node.</summary>
    public bool Destroyed;
    /// <summary>The bottom of its geometry's bounds, in the world: where its explosion and chunks start (the corners of its bounds averaged). Null without geometry, which throws no chunks.</summary>
    public Vector3? Base { get; set; }
    /// <summary>Its own death explosion, by its flags (0x400000 x1_bldx1, 0x800000 x1_cars1); never for type 3.</summary>
    public string? Explosion => Type == 3 ? null : (Flags & 0x400000) != 0 ? "x1_bldx1" : (Flags & 0x800000) != 0 ? "x1_cars1" : null;
    /// <summary>The slot it goes to: the structure slot takes 2, 3, 0xb, 0xc and 0x5c, 4 is the sign's.</summary>
    public bool Damageable => Type is 2 or 3 or 0xb or 0xc or 0x5c;
    /// <summary>
    /// A type-4 node, whatever its placement's type (Nitro p12's and S01's swend_1 barriers, N28's sdtrn_1): a car
    /// passes through it (the original skips it) and its hit goes to the sign slot, which throws the whole placement.
    /// </summary>
    public bool Sign => Type == 4;
    /// <summary>A type-7 node's slide (<see cref="GateMotion"/>), null for every other node.</summary>
    public GateMotion? Gate { get; set; }
}

/// <summary>
/// A gate: a type-7 node that slides between where it was placed and a point
/// beside it when the script's triggerGate says.
/// The original's scene loader hands the record's offset and time
/// to the gate; its init takes the placed spot
/// as A and the offset through the node's own transform as B, and stores
/// 1 / time. Its pass-A tick, while the gate is idle, moves each axis
/// by dt / time of what is left (all the way when that is outside 0..0.95),
/// and within 0.2 of B on every axis puts it there, marks the gate done and swaps A and
/// B, so the next trigger takes it back. A trigger while it moves does nothing.
/// </summary>
public sealed class GateMotion
{
    /// <summary>The record's offset, in the node's own frame.</summary>
    public required Vector3 Offset { get; init; }
    /// <summary>B − A in the node's parent frame (the node's rotation times the offset): the 0.2 test is on its axes.</summary>
    public required Vector3 Delta { get; init; }
    /// <summary>B − A in the world.</summary>
    public Vector3 World { get; set; }
    /// <summary>1 / the record's time, per second.</summary>
    public required float Rate { get; init; }
    /// <summary>Where it is: 0 where it was placed, 1 at B.</summary>
    public float X;
    /// <summary>Where the next trigger sends it.</summary>
    public float Target = 1;
    /// <summary>Sliding.</summary>
    public bool Moving;

    /// <summary>triggerGate: starts the slide. False when it was sliding already.</summary>
    public bool Trigger()
    {
        if (Moving) return false;
        Moving = true;
        return true;
    }

    /// <summary>One pass-A tick. True when it moved.</summary>
    public bool Tick(float dt)
    {
        if (!Moving) return false;
        float f = dt * Rate, left = Target - X;
        X = f is >= 0 and <= 0.95f ? X + f * left : Target;
        left = System.Math.Abs(Target - X);
        // each axis also snaps once within 0.005; within 0.2 on all three it arrives
        if (System.Math.Abs(Delta.X) * left < 0.2f && System.Math.Abs(Delta.Y) * left < 0.2f && System.Math.Abs(Delta.Z) * left < 0.2f)
        {
            X = Target;
            Target = 1 - Target;
            Moving = false;
        }
        return true;
    }

    /// <summary>How far it stands from where it was placed, in the world.</summary>
    public Vector3 Shift => World * X;
}

/// <summary>One static collider: a placed scenery object.</summary>
public sealed partial class StaticObject
{
    public required string Name { get; init; }
    public required uint Type { get; init; }
    public Vector3 Centre;
    public float Radius;
    /// <summary>Class 0x3000 nodes: the render mesh's polygons, world space.</summary>
    public List<Poly> Polys { get; } = [];
    /// <summary>Types 0xb–0xd: the upward polygons (n.y &gt; 0.4) the ground query drives on.</summary>
    public List<Poly> Surfaces { get; } = [];
    public bool IsStructure => Type is 0xb or 0xc or 0xd;
    /// <summary>Whatever the placement stands for elsewhere (a mission entity).</summary>
    public object? Owner { get; set; }
    /// <summary>A gun emplacement's collider (type 8): hits on any of its nodes go to that vehicle's damage slot, not here.</summary>
    public Vehicles.Vehicle? Vehicle { get; set; }
    /// <summary>Node flag 0x200: destroyed; it no longer collides or takes hits.</summary>
    public bool Destroyed { get; set; }
    /// <summary>
    /// The root's hit points (class-2 block +0/+4): the class-2 ctor's
    /// <see cref="RootHp"/>, which the placement keeps (the original creates
    /// it with isRoot 1). Splash hits only roots (the collider
    /// lists); rounds and rams hit the node they touch (<see cref="Nodes"/>).
    /// </summary>
    public int Hp = RootHp, MaxHp = RootHp;
    /// <summary>Blk = {400, 400, …}.</summary>
    public const int RootHp = 400;
    /// <summary>The structure's SGEO nodes; empty for a collider made without them (hits then go to the root).</summary>
    public List<StructureNode> Nodes { get; } = [];
    /// <summary>The world position of its first 0x1000000 node, where the root's explosion goes off; null without one, and then the root has none.</summary>
    public Vector3? Marker { get; set; }
    /// <summary>The session's stable id: the collider's place in the static world, from 1 (0 until a session assigns it).</summary>
    public int NetId;
    /// <summary>Type 4's sphere about Centre (0.75 × the bounding radius): the spawn spacing's, and the collider of one made without parts (<see cref="StaticWorld.AddBox"/>).</summary>
    public float SphereRadius;
    /// <summary>The collider record's radius (rec[4]): the farthest vertex from <see cref="Centre"/>, in 3D. A splash reaches what it touches.</summary>
    public float BoundRadius;
    /// <summary>The explosion it dies in: the node flags' x1_bldx1 / x1_cars1, or the SDFC explosion name; none for plain nodes.</summary>
    public string? Explosion { get; set; }
    /// <summary>The sound its explosion goes with (the SDFC sound, else vexplode.wav); none without an explosion.</summary>
    public string Sound { get; set; } = "vexplode.wav";
    /// <summary>
    /// What a destroyed structure leaves to collide with: its 'A' geometry
    /// (the grain hopper's fallen bin, a ramp). Null when it leaves nothing
    /// or is type 3, whose dead nodes give no response.
    /// </summary>
    public StaticObject? Remains { get; set; }
    /// <summary>Some of its remains are swapped in: those collide again, but take no more damage.</summary>
    public bool Collapsed { get; private set; }
    /// <summary>Counts the geometry swaps, for whatever mirrors the colliders.</summary>
    public int Revision { get; private set; }
    /// <summary>Whether it still collides and carries cars.</summary>
    public bool Solid => Nodes.Count == 0 || Type == 4 ? !Destroyed || Collapsed : Polys.Count + Boxes.Count + Spheres.Count + Surfaces.Count > 0;

    /// <summary>The whole placement's destruction geometry swap (then the rebuild): an emplacement's wreck, or a collider without nodes.</summary>
    public void Collapse()
    {
        if (Remains is not { } r || Collapsed) return;
        Polys.Clear(); Polys.AddRange(r.Polys);
        Surfaces.Clear(); Surfaces.AddRange(r.Surfaces);
        Boxes.Clear(); Boxes.AddRange(r.Boxes);
        MeshBounds.Clear(); MeshBounds.AddRange(r.MeshBounds);
        Spheres.Clear(); Spheres.AddRange(r.Spheres);
        Centre = r.Centre; Radius = r.Radius; BoundRadius = r.BoundRadius;
        Collapsed = true;
        Revision++;
    }

    private (Poly[] Polys, Poly[] Surfaces, NodeBox[] Boxes, NodeSphere[] Spheres, NodeBox[] MeshBounds, Vector3 Centre, float Radius, float BoundRadius)? _intact;

    /// <summary>
    /// The geometry after nodes died (and the rebuild):
    /// a live node keeps its own, a dead one takes its 'A' twin from
    /// <see cref="Remains"/>, or nothing.
    /// </summary>
    public void Refresh()
    {
        if (Nodes.Count == 0) { if (Destroyed) Collapse(); return; }
        _intact ??= ([.. Polys], [.. Surfaces], [.. Boxes], [.. Spheres], [.. MeshBounds], Centre, Radius, BoundRadius);
        bool Alive(int n) => n < 0 ? !Destroyed : !Nodes[n].Destroyed;
        bool Dead(int n) => n < 0 ? Destroyed : Nodes[n].Destroyed;
        Polys.Clear(); Surfaces.Clear(); Boxes.Clear(); Spheres.Clear(); MeshBounds.Clear();
        Polys.AddRange(_intact.Value.Polys.Where(p => Alive(p.Node)).Select(Moved));
        Surfaces.AddRange(_intact.Value.Surfaces.Where(p => Alive(p.Node)).Select(Moved));
        Boxes.AddRange(_intact.Value.Boxes.Where(b => Alive(b.Node)).Select(Moved));
        MeshBounds.AddRange(_intact.Value.MeshBounds.Where(b => Alive(b.Node)).Select(Moved));
        Spheres.AddRange(_intact.Value.Spheres.Where(b => Alive(b.Node)).Select(Moved));
        bool swapped = false;
        if (Remains is { } r)
        {
            foreach (var p in r.Polys) if (Dead(p.Node)) { Polys.Add(Moved(p)); swapped = true; }
            foreach (var p in r.Surfaces) if (Dead(p.Node)) { Surfaces.Add(Moved(p)); swapped = true; }
            foreach (var b in r.Boxes) if (Dead(b.Node)) { Boxes.Add(Moved(b)); swapped = true; }
            foreach (var b in r.MeshBounds) if (Dead(b.Node)) MeshBounds.Add(Moved(b));
            foreach (var b in r.Spheres) if (Dead(b.Node)) { Spheres.Add(Moved(b)); swapped = true; }
        }
        Collapsed = swapped;
        Revision++;
        // the collider's sphere is rebuilt round what is left (measured: the grain hopper's
        // 11.84 about its marker before, 12.24 about (1549.4, 31.8, 49738.8) with its bin gone)
        var pts = Polys.Concat(Surfaces).SelectMany(p => p.V)
            .Concat(Boxes.SelectMany(b => new[] { b.Origin + b.Rot.ToWorld(b.Min), b.Origin + b.Rot.ToWorld(b.Max) }))
            .Concat(Spheres.SelectMany(b => new[] { b.Centre - new Vector3(b.Radius), b.Centre + new Vector3(b.Radius) })).ToList();
        if (_intact.Value.Polys.All(p => Alive(p.Node)) && _intact.Value.Surfaces.All(p => Alive(p.Node)) && _intact.Value.Boxes.All(b => Alive(b.Node)) && _intact.Value.Spheres.All(b => Alive(b.Node)))
        {
            (Centre, Radius, BoundRadius) = (_intact.Value.Centre, _intact.Value.Radius, _intact.Value.BoundRadius);
            // a gate slid past the placement's circle still meets cars there (the circle is only the port's broad test)
            if (_gates) foreach (var q in pts) Radius = System.Math.Max(Radius, new Vector2(q.X - Centre.X, q.Z - Centre.Z).Length());
        }
        else if (pts.Count > 0)
        {
            var min = pts.Aggregate(Vector3.Min); var max = pts.Aggregate(Vector3.Max);
            Centre = (min + max) * 0.5f;
            Radius = pts.Max(q => new Vector2(q.X - Centre.X, q.Z - Centre.Z).Length());
            BoundRadius = pts.Max(q => Vector3.Distance(q, Centre));
        }
    }

    private bool _gates;

    /// <summary>A node's gate shift, or zero.</summary>
    private Vector3 ShiftOf(int node) => node >= 0 && node < Nodes.Count && Nodes[node].Gate is { } g ? g.Shift : Vector3.Zero;
    private Poly Moved(Poly p)
    {
        var d = ShiftOf(p.Node);
        return d == Vector3.Zero ? p : new Poly(p.N, p.D - Vector3.Dot(p.N, d), [.. p.V.Select(v => v + d)]) { Node = p.Node };
    }
    private NodeBox Moved(NodeBox b)
    {
        var d = ShiftOf(b.Node);
        return d == Vector3.Zero ? b : b with { Origin = b.Origin + d };
    }
    private NodeSphere Moved(NodeSphere b)
    {
        var d = ShiftOf(b.Node);
        return d == Vector3.Zero ? b : b with { Centre = b.Centre + d };
    }

    /// <summary>
    /// triggerGate (FSM opcode 63): the object itself if it is type 7, else its first type-7
    /// descendant, starts sliding. False without one.
    /// </summary>
    public bool TriggerGate()
    {
        foreach (var n in Nodes)
            if (n.Type == 7 && n.Gate is { } g) { g.Trigger(); return true; }
        return false;
    }

    /// <summary>Its gates' pass-A tick; the colliders follow. True when one moved.</summary>
    public bool TickGates(float dt)
    {
        bool moved = false;
        foreach (var n in Nodes) if (n.Gate is { } g) moved |= g.Tick(dt);
        if (moved) { _gates = true; Refresh(); }
        return moved;
    }

    /// <summary>Its gates were set from outside (a network mirror): the colliders follow.</summary>
    public void GatesMoved() { _gates = true; Refresh(); }

    /// <summary>The destroyed state on the wire: bit 0 the root, bit n + 1 node n.</summary>
    /// <summary>Whether the node is a <see cref="StructureNode.Sign"/> (−1, the root, is not).</summary>
    public bool SignNode(int node) => node >= 0 && node < Nodes.Count && Nodes[node].Sign;

    /// <summary>
    /// The sign slot's throw on the node's root: the root's HP to 0 and the whole placement
    /// out of the collision world as one piece, so it no longer collides or takes hits. True if it was standing.
    /// </summary>
    public bool Knock()
    {
        if (Destroyed) return false;
        Hp = 0;
        Destroyed = true;
        foreach (var n in Nodes) { n.Destroyed = true; n.Hp = 0; }
        Refresh();
        return true;
    }

    public byte[] DestroyedMask()
    {
        var m = new byte[(Nodes.Count + 8) / 8];
        if (Destroyed) m[0] |= 1;
        for (int i = 0; i < Nodes.Count; i++) if (Nodes[i].Destroyed) m[(i + 1) >> 3] |= (byte)(1 << ((i + 1) & 7));
        return m;
    }

    /// <summary>Takes a destroyed state from the wire (a network mirror): what it marks dies, quietly. True when anything changed.</summary>
    public bool ApplyDestroyedMask(ReadOnlySpan<byte> m)
    {
        var bits = m.ToArray();
        bool Bit(int i) => (i >> 3) < bits.Length && (bits[i >> 3] & (1 << (i & 7))) != 0;
        bool changed = false;
        if (Bit(0) && !Destroyed) { Destroyed = true; Hp = 0; changed = true; }
        for (int i = 0; i < Nodes.Count; i++)
            if (Bit(i + 1) && !Nodes[i].Destroyed) { Nodes[i].Destroyed = true; Nodes[i].Hp = 0; changed = true; }
        if (changed) Refresh();
        return changed;
    }

    /// <summary>Class 0x2000 nodes: a box each, in the node's own frame.</summary>
    public List<NodeBox> Boxes { get; } = [];
    /// <summary>Class 0x3000 nodes: the bounds of each one's mesh, in the node's own frame (the AI's sight test grows them by 3).</summary>
    public List<NodeBox> MeshBounds { get; } = [];
    /// <summary>Class 0x4000 / 0x5000 nodes: a sphere each, the part's SGEO radius x 1 (0x4000) or 0.75 (0x5000, the default of type 4's parts).</summary>
    public List<NodeSphere> Spheres { get; } = [];
    /// <summary>Type 0xc: the first type-0xd node (the deck) in the node walk, for the bridge tree.</summary>
    public NodeFrame? Deck { get; set; }
}

/// <summary>A node's world frame and its geometry's bounds in that frame (taken as the vertex bounds).</summary>
public sealed record NodeFrame(Rows Rot, Vector3 Origin, Vector3 Min, Vector3 Max);

/// <summary>A class-0x2000 node's collider: its geometry's bounds in the node frame, placed in the world.</summary>
public sealed record NodeBox(Rows Rot, Vector3 Origin, Vector3 Min, Vector3 Max, float Radius)
{
    public Vector3 Centre => Origin + Rot.ToWorld((Min + Max) * 0.5f);
    /// <summary>The structure node it belongs to, or −1 for the root.</summary>
    public int Node { get; init; } = -1;
}

/// <summary>A class-0x4000 (radius x 1) or 0x5000 (x 0.75) node's collider: its SGEO sphere, placed in the world.</summary>
public sealed record NodeSphere(Vector3 Centre, float Radius)
{
    /// <summary>The structure node it belongs to, or -1 for the root.</summary>
    public int Node { get; init; } = -1;
}

/// <summary>
/// The mission's static colliders and drive-on surfaces, built from the
/// placements (registration meshes,
/// Surface lists). Nodes collide by class: polygon meshes
/// (0x3000), boxes (0x2000, taken as the geometry's bounds in the node
/// frame) and type 4's
/// sphere.
/// </summary>
public sealed class StaticWorld
{
    public List<StaticObject> Objects { get; } = [];

    private List<StaticObject>? _gated;
    /// <summary>The placements with a gate (type-7 node).</summary>
    public IReadOnlyList<StaticObject> Gated => _gated ??= [.. Objects.Where(o => o.Nodes.Any(n => n.Gate is not null))];

    /// <summary>Every gate's pass-A tick, once a frame.</summary>
    public void TickGates(float dt)
    {
        foreach (var o in Gated) o.TickGates(dt);
    }

    public static bool Collides(uint type) => type is 2 or 3 or 4 or 7 or 8 or 10 or 0xb or 0xc or 0xd;

    /// <summary>Node class default by object type: 0x2000 box, 0x3000 mesh, 0x5000 sphere.</summary>
    public static uint DefaultClass(uint type) => type switch { 2 or 7 or 8 or 10 => 0x2000, 4 => 0x5000, _ => 0x3000 };

    /// <summary>
    /// Registers a placement. <paramref name="nodeClass"/> gives a node's own
    /// class (the SGEO flags &amp; 0xf000, 0 for none), which wins over the type's
    /// default; 0x1000 nodes do not collide.
    /// </summary>
    public StaticObject? Add(string name, uint type, float[] engineRotation, Vector3 position, AssembledModel model, Func<string, uint>? nodeClass = null, Func<string, uint>? nodeType = null,
        Func<string, int>? nodeIndex = null, Action<int, Vector3>? nodeBase = null, Func<string, (Vector3 Centre, float Radius)?>? nodeSphere = null)
    {
        if (Build(name, type, engineRotation, position, model, nodeClass, nodeType, nodeIndex, nodeBase, nodeSphere) is not { } o) return null;
        Objects.Add(o);
        return o;
    }

    /// <summary>
    /// A placement's colliders, not registered (a destroyed structure's remains).
    /// <paramref name="nodeType"/> gives a node's own type (from the SGEO record; the
    /// root's is the placement's type, so give 0 for it): a bridge's (0xc) drive-on surfaces
    /// come from its type-0xd nodes only, and the first of them
    /// is its <see cref="StaticObject.Deck"/>. Without it every node of a bridge counts.
    /// </summary>
    /// <param name="nodeIndex">A part's structure node (<see cref="StaticObject.Nodes"/>) by part name, −1 for none: its polygons and boxes carry it.</param>
    /// <param name="nodeBase">Told each node's <see cref="StructureNode.Base"/>, from its part's geometry.</param>
    /// <param name="nodeSphere">A part's SGEO sphere (centre in part space, radius), for its class-0x4000 / 0x5000 collider; null takes the geometry's bounds.</param>
    public static StaticObject? Build(string name, uint type, float[] engineRotation, Vector3 position, AssembledModel model, Func<string, uint>? nodeClass = null, Func<string, uint>? nodeType = null,
        Func<string, int>? nodeIndex = null, Action<int, Vector3>? nodeBase = null, Func<string, (Vector3 Centre, float Radius)?>? nodeSphere = null)
    {
        if (!Collides(type)) return null;
        var place = Affine.FromEngine(engineRotation, position);
        var o = new StaticObject { Name = name, Type = type };
        var all = new List<Vector3>();
        var surfaces = new List<List<Poly>>();
        foreach (var part in model.Parts)
        {
            var g = part.Mesh;
            if (g.Vertices.Length <= 2) continue;
            var world = g.Vertices.Select(v => place.Apply(part.ToModel.Apply(v))).ToArray();
            all.AddRange(world);
            Rows Frame() => new(Vector3.Normalize(place.Rotate(part.ToModel.Rotate(Vector3.UnitX))),
                                Vector3.Normalize(place.Rotate(part.ToModel.Rotate(Vector3.UnitY))),
                                Vector3.Normalize(place.Rotate(part.ToModel.Rotate(Vector3.UnitZ))));
            bool deck = type == 0xc && nodeType?.Invoke(part.NodeName) == 0xd;
            if (deck && o.Deck is null)
                o.Deck = new NodeFrame(Frame(), place.Apply(part.ToModel.Apply(Vector3.Zero)), g.Vertices.Aggregate(Vector3.Min), g.Vertices.Aggregate(Vector3.Max));
            int node = nodeIndex?.Invoke(part.NodeName) ?? -1;
            if (node >= 0 && nodeBase is not null)
            {
                // The four bottom corners of the node's bounds box, averaged in the world
                var lo = g.Vertices.Aggregate(Vector3.Min); var hi = g.Vertices.Aggregate(Vector3.Max);
                nodeBase(node, place.Apply(part.ToModel.Apply(new Vector3((lo.X + hi.X) * 0.5f, lo.Y, (lo.Z + hi.Z) * 0.5f))));
            }
            uint cls = nodeClass?.Invoke(part.NodeName) ?? 0;
            if (cls == 0) cls = DefaultClass(type);
            if (cls == 0x1000) continue;
            if (cls is 0x4000 or 0x5000)
            {
                // a flag base (part type 0x5a / 0x5b): the car's collide slot ignores it, the CTF game takes the flag
                if (type != 4 && nodeType?.Invoke(part.NodeName) is 0x5a or 0x5b) continue;
                // a sphere: the part's SGEO centre and radius, x 1.0 (0x4000) or 0.75 (0x5000); a breakable (type 4) has
                // one per part
                var (sc, sr) = nodeSphere?.Invoke(part.NodeName) ?? BoundSphere(g.Vertices);
                var sw = place.Apply(part.ToModel.Apply(sc));
                o.Spheres.Add(new NodeSphere(sw, sr * (cls == 0x4000 ? 1f : 0.75f)) { Node = node });
                // a breakable's bounds stay its vertices' (the spawn spacing's sphere)
                if (type != 4) { all.Add(sw - new Vector3(sr)); all.Add(sw + new Vector3(sr)); }
                continue;
            }
            if (cls == 0x2000 && type != 4)
            {
                var rot = Frame();
                var bmin = g.Vertices.Aggregate(Vector3.Min); var bmax = g.Vertices.Aggregate(Vector3.Max);
                o.Boxes.Add(new NodeBox(rot, place.Apply(part.ToModel.Apply(Vector3.Zero)), bmin, bmax, g.Vertices.Max(v => v.Length())) { Node = node });
                continue;   // a box is solid only, a structure's too (aaramp5's rails): the original walks polygon meshes alone
            }
            if (cls == 0x3000)
                o.MeshBounds.Add(new NodeBox(Frame(), place.Apply(part.ToModel.Apply(Vector3.Zero)), g.Vertices.Aggregate(Vector3.Min), g.Vertices.Aggregate(Vector3.Max), g.Vertices.Max(v => v.Length())) { Node = node });
            // A bridge's surfaces are its deck nodes' only (not its trusses and spans); only a mesh part's faces are walkable
            bool drive = cls == 0x3000 && o.IsStructure && (type != 0xc || nodeType is null || deck);
            var mine = new List<Poly>();
            surfaces.Add(mine);
            foreach (var f in g.Faces)
            {
                if (f.Vertices.Length < 3) continue;
                var pts = f.Vertices.Select(fv => (uint)fv.Index < world.Length ? world[fv.Index] : world[0]).ToArray();
                // the plane from the polygon itself, outward (GEO winds and points normals inward)
                var n = -Vector3.Normalize(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]));
                if (float.IsNaN(n.X)) continue;
                var fn = new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z);
                var fw = place.Rotate(part.ToModel.Rotate(fn));
                if (fw.LengthSquared() > 0.5f) n = -Vector3.Normalize(fw);   // n = −(R·faceNormal)
                var poly = new Poly(n, -Vector3.Dot(n, pts[0]), pts) { Node = node };
                if (cls == 0x3000) o.Polys.Add(poly);
                if (drive && n.Y > 0.4f) mine.Add(poly);
            }
        }
        // the list is built by prepending a node's mesh polygons, which the original itself
        // prepended: the nodes come last first, each one's faces in their own order
        for (int k = surfaces.Count - 1; k >= 0; k--) o.Surfaces.AddRange(surfaces[k]);
        if (all.Count == 0) return null;
        var min = all.Aggregate(Vector3.Min); var max = all.Aggregate(Vector3.Max);
        o.Centre = (min + max) * 0.5f;
        // type 4: 0.75 × the whole object's bounding radius, for the spawn spacing (its parts collide by their own spheres)
        o.BoundRadius = all.Max(p => Vector3.Distance(p, o.Centre));
        o.SphereRadius = 0.75f * o.BoundRadius;
        o.Radius = all.Max(p => new Vector2(p.X - o.Centre.X, p.Z - o.Centre.Z).Length());
        return o;
    }

    private static (Vector3, float) BoundSphere(Vector3[] v)
    {
        var c = (v.Aggregate(Vector3.Min) + v.Aggregate(Vector3.Max)) * 0.5f;
        return (c, v.Max(p => Vector3.Distance(p, c)));
    }

    /// <summary>A segment p + v t, t in [0, maxT], against spheres: the earliest entry, its outward normal and the sphere.</summary>
    public static (float T, Vector3 N, NodeSphere Sphere)? RaySpheres(IReadOnlyList<NodeSphere> spheres, Vector3 p, Vector3 v, float maxT)
    {
        (float T, Vector3 N, NodeSphere Sphere)? best = null;
        float a = v.LengthSquared();
        if (a < 1e-12f) return null;
        foreach (var s in spheres)
        {
            var m = p - s.Centre;
            float b = Vector3.Dot(m, v), c = m.LengthSquared() - s.Radius * s.Radius;
            float disc = b * b - a * c;
            if (disc < 0) continue;
            float t = (-b - MathF.Sqrt(disc)) / a;
            if (t < 0) t = c <= 0 ? 0 : float.MaxValue;   // from inside: at once; behind: none
            if (t > maxT || (best is { } bb && t >= bb.T)) continue;
            var n = p + v * t - s.Centre;
            best = (t, n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY, s);
        }
        return best;
    }

    /// <summary>An axis-aligned box collider (tests, and objects without a model).</summary>
    public StaticObject AddBox(string name, uint type, Vector3 min, Vector3 max)
    {
        var o = new StaticObject { Name = name, Type = type, Centre = (min + max) / 2, Radius = new Vector2(max.X - min.X, max.Z - min.Z).Length() / 2, BoundRadius = (max - min).Length() / 2 };
        Vector3 P(int i) => new((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        void Face(Vector3 n, params int[] idx)
        {
            var v = idx.Select(P).ToArray();
            var poly = new Poly(n, -Vector3.Dot(n, v[0]), v);
            o.Polys.Add(poly);
            if (o.IsStructure && n.Y > 0.4f) o.Surfaces.Add(poly);
        }
        Face(-Vector3.UnitX, 0, 2, 6, 4); Face(Vector3.UnitX, 1, 3, 7, 5);
        Face(-Vector3.UnitY, 0, 1, 5, 4); Face(Vector3.UnitY, 2, 3, 7, 6);
        Face(-Vector3.UnitZ, 0, 1, 3, 2); Face(Vector3.UnitZ, 4, 5, 7, 6);
        Objects.Add(o);
        return o;
    }

    /// <summary>Inside a convex polygon in the XZ plane (edges count as inside).</summary>
    public static bool InsideXZ(Poly p, float x, float z)
    {
        int n = p.V.Length;
        bool? side = null;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = p.V[j]; var b = p.V[i];
            float cross = (b.X - a.X) * (z - a.Z) - (b.Z - a.Z) * (x - a.X);
            if (MathF.Abs(cross) < 1e-6f) continue;
            bool s = cross > 0;
            if (side is null) side = s; else if (side != s) return false;
        }
        return true;
    }

    /// <summary>Point in polygon, projected on the normal's dominant axis plane.</summary>
    public static bool Inside(Poly p, Vector3 q)
    {
        var a = Vector3.Abs(p.N);
        Func<Vector3, Vector2> proj = a.Y >= a.X && a.Y >= a.Z ? v => new(v.X, v.Z) : a.Z >= a.X ? v => new(v.X, v.Y) : v => new(v.Y, v.Z);
        var pq = proj(q);
        int n = p.V.Length;
        bool? side = null;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a2 = proj(p.V[j]); var b2 = proj(p.V[i]);
            float cross = (b2.X - a2.X) * (pq.Y - a2.Y) - (b2.Y - a2.Y) * (pq.X - a2.X);
            if (MathF.Abs(cross) < 1e-7f) continue;
            bool s = cross > 0;
            if (side is null) side = s; else if (side != s) return false;
        }
        return true;
    }

    /// <summary>For a segment p + v·t, t in [0, maxT], against class-0x2000 node boxes: the earliest entry and its face normal.</summary>
    public static (float T, Vector3 N, NodeBox Box)? RayBoxes(IReadOnlyList<NodeBox> boxes, Vector3 p, Vector3 v, float maxT)
    {
        (float T, Vector3 N, NodeBox Box)? best = null;
        foreach (var b in boxes)
        {
            var lp = b.Rot.ToLocal(p - b.Origin); var lv = b.Rot.ToLocal(v);
            float tIn = float.MinValue, tOut = float.MaxValue; Vector3 n = default;
            bool miss = false;
            for (int a = 0; a < 3 && !miss; a++)
            {
                float pa = lp[a], va = lv[a], lo = b.Min[a], hi = b.Max[a];
                if (MathF.Abs(va) < 1e-8f) { miss = pa < lo || pa > hi; continue; }
                float t0 = (lo - pa) / va, t1 = (hi - pa) / va;
                if (t0 > t1) (t0, t1) = (t1, t0);
                if (t0 > tIn) { tIn = t0; n = Vector3.Zero; n[a] = va > 0 ? -1 : 1; }
                tOut = MathF.Min(tOut, t1);
            }
            if (miss || tIn > tOut || tIn < 0 || tIn > maxT) continue;
            if (best is null || tIn < best.Value.T) best = (tIn, b.Rot.ToWorld(n), b);
        }
        return best;
    }

    /// <summary>
    /// For a segment p + v·t, t in [0, maxT], against a sphere (type 4's): the entry and its normal. A segment that
    /// starts inside meets nothing, as with the boxes.
    /// </summary>
    public static (float T, Vector3 N)? RaySphere(Vector3 c, float r, Vector3 p, Vector3 v, float maxT)
    {
        var m = p - c;
        float a = Vector3.Dot(v, v), b = Vector3.Dot(m, v), k = Vector3.Dot(m, m) - r * r;
        if (r <= 0 || a < 1e-12f || k <= 0 || b >= 0) return null;
        float disc = b * b - a * k;
        if (disc < 0) return null;
        float t = (-b - MathF.Sqrt(disc)) / a;
        if (t > maxT) return null;
        return (t, Vector3.Normalize(m + v * t));
    }

    /// <summary>
    /// The earliest polygon a point moving at v meets within dt.
    /// Polygons facing the motion (n·v ≤ −1e-4) with the point in front of them.
    /// </summary>
    public static (float T, Poly? Hit) RayMesh(IReadOnlyList<Poly> polys, Vector3 p, Vector3 v, float dt)
    {
        float best = float.MaxValue; Poly? hit = null;
        foreach (var poly in polys)
        {
            float nv = Vector3.Dot(poly.N, v);
            if (nv > -1e-4f) continue;
            float dist = Vector3.Dot(poly.N, p) + poly.D;
            if (dist <= 0) continue;
            float t = -dist / nv;
            if (t < 0 || t > dt || t >= best) continue;
            if (!Inside(poly, p + v * t)) continue;
            best = t; hit = poly;
        }
        return (best, hit);
    }
}

/// <summary>
/// Drive-on structure surfaces first (within ±3 of the query
/// height the highest wins; else the highest below), then the terrain.
/// </summary>
public interface IHasStatics
{
    StaticWorld World { get; }
}

public sealed class WorldGround(TerrainGround terrain, StaticWorld world) : IGround, IHasStatics
{
    public (float Height, Vector3 Normal) Query(double x, double y, double z)
    {
        float near = -1e30f, below = -1e30f;
        Vector3 nn = Vector3.UnitY, nb = Vector3.UnitY;
        float fx = (float)x, fy = (float)y, fz = (float)z;
        foreach (var s in world.Objects)
        {
            if (s.Surfaces.Count == 0 || !s.Solid) continue;
            float dx = fx - s.Centre.X, dz = fz - s.Centre.Z;
            if (dx * dx + dz * dz > s.Radius * s.Radius) continue;
            foreach (var p in s.Surfaces)
            {
                if (!StaticWorld.InsideXZ(p, fx, fz)) continue;
                float hp = -(p.N.X * fx + p.N.Z * fz + p.D) / p.N.Y;
                if (near == -1e30f && hp < fy && hp > below) { below = hp; nb = p.N; }
                if (hp > fy - 3 && hp < fy + 3 && hp > near) { near = hp; nn = p.N; }
                break;      // only the first containing polygon of each structure
            }
        }
        if (near > -1e30f) return (near, nn);
        if (below > -1e30f) return (below, nb);
        return terrain.Query(x, y, z);
    }

    public int SurfaceId(double x, double z) => terrain.SurfaceId(x, z);
    public bool Impassable(double x, double z) => terrain.Impassable(x, z);
    public Surface Surface(int id) => terrain.Surface(id);
    public float BareHeight(double x, double z) => terrain.Height(x, z);
    public Vector3 BareNormal(double x, double z) => terrain.Normal(x, z);
    public StaticWorld World => world;
    /// <summary>The heightfield under the structures.</summary>
    public TerrainGround Terrain => terrain;
}
