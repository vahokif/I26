using I76.Formats.Assembly;
using System.Numerics;
using I76.Editor.Roads;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Intersections;

public enum PassKind { Through, End }

/// <summary>A road's part in a junction: it runs through two arms (<see cref="ArmA"/>, <see cref="ArmB"/>) and is cut there, or it ends at one (<see cref="ArmA"/>).</summary>
public sealed record Pass(RoadEdit Road, PassKind Kind, int ArmA, int ArmB = -1, bool AtStart = false)
{
    /// <summary>Where the road is cut instead of at arm A (a J strip on the arm: the dirt road ends on the strip's far edge).</summary>
    public (Vector2 Position, Vector2 Out)? AtA { get; init; }
    /// <summary>As <see cref="AtA"/>, for arm B.</summary>
    public (Vector2 Position, Vector2 Out)? AtB { get; init; }
}

/// <summary>A piece that goes down with another (a J strip on a junction's arm, a dirt cap beyond a J strip).</summary>
public sealed record ExtraPiece(PieceDef Def, PiecePose Pose);

/// <summary>What a road's open end is finished with (the road tool's choice): nothing, a cap, a turn-around, or a J strip where asphalt turns to dirt.</summary>
public enum RoadEnd
{
    /// <summary>Nothing: the road just stops (as it always could).</summary>
    Open,
    /// <summary>An end cap: dirt ijd27t1, planks ijw29t1; an asphalt road's is a J strip and a dirt cap beyond it, as the stock maps end one (M03, M08, S04).</summary>
    Cap,
    /// <summary>The asphalt turn-around ica2n_1 (asphalt roads only).</summary>
    TurnAround,
    /// <summary>A J strip alone (asphalt roads only): the road turns to dirt there, and a dirt road can be drawn on from the strip's far edge.</summary>
    ToDirt,
}

/// <summary>A piece the rules would place: which, where, and the roads it joins.</summary>
public sealed record Proposal(PieceDef Def, PiecePose Pose, Vector2 Centre, IReadOnlyList<Pass> Passes)
{
    /// <summary>The pieces that go down with it: a J strip on each arm where a dirt road meets an asphalt T or X (as the stock maps lay them).</summary>
    public IReadOnlyList<ExtraPiece> Extras { get; init; } = [];

    public string Describe => Def.Shape switch
    {
        PieceShape.T => $"T junction ({Def.Name})",
        PieceShape.X => $"X crossing ({Def.Name})",
        PieceShape.ForkRight => $"fork to the right ({Def.Name})",
        PieceShape.ForkLeft => $"fork to the left ({Def.Name})",
        _ => Def.Name,
    } + (Extras.Count > 0 ? $", the dirt road on {(Extras.Count == 1 ? "a J strip" : $"{Extras.Count} J strips")} ({Extras[0].Def.Name})" : "");
}

/// <summary>The state before a road edit: which roads there were (by reference) and which arms of each piece had a road.</summary>
public sealed class JunctionSnapshot
{
    internal HashSet<RoadEdit> Roads = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<PlacedPiece, bool[]> Connected = new(ReferenceEqualityComparer.Instance);
}

/// <summary>One undo step: the road edit, the pieces that go with it, and the ground levelled under them.</summary>
public sealed class JunctionCommand(string name, RoadCommand roads, IReadOnlyList<PieceOp> pieceOps, PieceStore pieces) : IEditCommand
{
    public string Name { get; } = name;
    public int Size => roads.Size + pieceOps.Count * 64;
    public IReadOnlyList<PieceOp> PieceOps => pieceOps;
    public RoadCommand Roads => roads;
    public void Undo(IMapDocument doc) { for (int i = pieceOps.Count - 1; i >= 0; i--) pieceOps[i].Undo(pieces); roads.Undo(doc); }
    public void Redo(IMapDocument doc) { roads.Redo(doc); foreach (var o in pieceOps) o.Do(pieces); }
}

/// <summary>Several commands as one undo step (the road edit, then what the junctions made of it).</summary>
public sealed class StepCommand(string name, IReadOnlyList<IEditCommand> parts) : IEditCommand
{
    public string Name { get; } = name;
    public int Size => parts.Sum(p => p.Size);
    public IReadOnlyList<IEditCommand> Parts => parts;
    public void Undo(IMapDocument doc) { for (int i = parts.Count - 1; i >= 0; i--) parts[i].Undo(doc); }
    public void Redo(IMapDocument doc) { foreach (var p in parts) p.Redo(doc); }
}

/// <summary>
/// The Cities: Skylines way of making junctions, over the stock maps' own: where a road ends on the middle of another, or two cross,
/// the matching intersection piece goes down at the road height, rotated to the roads, and the roads are cut at its arms so their
/// ends meet it as the stock maps' do (docs/map-format-writing.md). Moving, retyping or bulldozing a road settles the pieces beside it.
/// The game has no junction in the road data: every cut road is a road of its own for the AI.
/// </summary>
public sealed class Junctions(RoadStore roads, PieceStore pieces, Func<Vector2, float> ground)
{
    /// <summary>How near a road end must be to another road's centre line to count as on it.</summary>
    public const float OnRoad = 1.5f;
    /// <summary>A road end this near an end of the other road is an extension, not a junction.</summary>
    public const float FromEnds = 6f;
    /// <summary>A road end within this of a piece's arm, heading its way, is that arm's road.</summary>
    public const float ArmReach = 2f;
    /// <summary>The leftover road beyond a cut keeps this much clear of the arm (the points near it are dropped).</summary>
    public const float Trim = 7f;
    /// <summary>The straight lead-in each cut road gets, ending on the arm (stock roads run straight for the last 5 or more).</summary>
    public const float LeadIn = 5f;

    public RoadStore Roads => roads;
    public PieceStore Pieces => pieces;

    // ---- detecting ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The pieces the rules would place for <paramref name="road"/> against the roads of the store (itself and <paramref name="ignore"/> left out): a T or a fork where an end meets another road, an X where it crosses one.
    /// Standard roads only: the pieces are 10 wide, so a wide road gets none and none is placed on one.
    /// </summary>
    public List<Proposal> Detect(RoadEdit road, RoadEdit? ignore = null, int variant = 0)
    {
        var o = new List<Proposal>();
        if (road.Points.Count < RoadCurves.MinPoints || RoadBuilder.IsClosed(road) || !RoadBuilder.IsStandard(road)) return o;
        if (variant == 0) variant = Variant();
        var pr = Poly.Of(road);
        var others = roads.Roads.Where(r => !ReferenceEquals(r, road) && !ReferenceEquals(r, ignore) && r.Points.Count >= 2 && !RoadBuilder.IsClosed(r) && RoadBuilder.IsStandard(r)).ToList();
        var polys = others.Select(Poly.Of).ToList();

        // ends on the middle of another road: T or fork
        foreach (bool atStart in new[] { true, false })
        {
            var p = atStart ? pr.P[0] : pr.P[^1];
            float back = MathF.Min(8f, pr.Length * 0.5f);
            var stemOut = Vector2.Normalize(pr.At(atStart ? back : pr.Length - back) - p);
            for (int q = 0; q < others.Count; q++)
            {
                var n = polys[q].Nearest(p);
                if (n.D > OnRoad || n.S < FromEnds || n.S > polys[q].Length - FromEnds) continue;
                if (Classify(others[q], polys[q], n, stemOut, p, variant, road, pr, atStart) is { } prop && !Conflicts(prop.Def, prop.Pose)) { o.Add(prop); break; }
            }
        }

        // crossings: X
        for (int q = 0; q < others.Count; q++)
        {
            var pq = polys[q];
            for (int i = 0; i + 1 < pr.P.Length; i++)
                for (int k = 0; k + 1 < pq.P.Length; k++)
                {
                    if (!Intersect(pr.P[i], pr.P[i + 1], pq.P[k], pq.P[k + 1], out var x)) continue;
                    float sr = pr.S[i] + Vector2.Distance(pr.P[i], x), sq = pq.S[k] + Vector2.Distance(pq.P[k], x);
                    if (sr < 14 || sr > pr.Length - 14 || sq < 14 || sq > pq.Length - 14) continue;
                    var tr = Vector2.Normalize(pr.P[i + 1] - pr.P[i]); var tq = Vector2.Normalize(pq.P[k + 1] - pq.P[k]);
                    if (!JunctionRules.CrossingIsSquare(tr, tq)) continue;
                    if (PieceCatalogue.Pick(PieceShape.X, others[q].Kind, variant) is not { } def) continue;
                    var pose = JunctionRules.Fit(def, x, tq);
                    var prop = new Proposal(def, pose, x, [new Pass(others[q], PassKind.Through, 0, 1), new Pass(road, PassKind.Through, 2, 3)]);
                    // a dirt road crossing an asphalt one: a J strip on each of its arms, the dirt road cut at their far edges
                    if (WantsStrip(def, road) && WithStrips(prop, 1, variant) is { } js && Viable(js) && !js.Extras.Any(e => Conflicts(e.Def, e.Pose))) prop = js;
                    if (Viable(prop) && !Conflicts(def, pose) && !o.Any(e => Vector2.Distance(e.Centre, x) < 14)) o.Add(prop);
                }
        }
        return o;
    }

    private Proposal? Classify(RoadEdit q, Poly pq, (float S, Vector2 Pt, Vector2 Tan, float D) n, Vector2 stemOut, Vector2 p, int variant, RoadEdit road, Poly pr, bool atStart)
    {
        if (JunctionRules.Classify(n.Tan, stemOut) is not { } cls) return null;
        if (PieceCatalogue.Pick(cls.Shape, q.Kind, variant) is not { } def) return null;
        var pose = JunctionRules.Fit(def, n.Pt, cls.Through, stemOut);
        var prop = new Proposal(def, pose, n.Pt, [new Pass(q, PassKind.Through, 0, 1), new Pass(road, PassKind.End, 2, -1, atStart)]);
        // a dirt stem on an asphalt T: a J strip on the stem's arm (the stock maps' way, 96 of 101 such arms), the stem ending on its far edge
        if (cls.Shape == PieceShape.T && WantsStrip(def, road) && WithStrips(prop, 1, variant) is { } js && Viable(js) && !js.Extras.Any(e => Conflicts(e.Def, e.Pose))) return js;
        return Viable(prop) ? prop : null;
    }

    /// <summary>Whether a road meeting an asphalt T or X arm gets a J strip there: a dirt road does (the strip is asphalt turning to dirt).</summary>
    private static bool WantsStrip(PieceDef def, RoadEdit road) => def.Kind == 0 && def.Shape is PieceShape.T or PieceShape.X && road.Kind % 3 == 1;

    /// <summary>The proposal with a J strip on the arms of pass <paramref name="passIndex"/>, that road cut at the strips' far edges.</summary>
    private static Proposal? WithStrips(Proposal prop, int passIndex, int variant)
    {
        if (PieceCatalogue.Pick(PieceShape.Strip, 0, variant) is not { } strip) return null;
        var arms = JunctionRules.WorldArms(prop.Def, prop.Pose);
        var pass = prop.Passes[passIndex];
        var extras = new List<ExtraPiece>(prop.Extras);
        (Vector2, Vector2) On(int arm)
        {
            var (pose, far) = StripOn(strip, arms[arm].Position, arms[arm].Out);
            extras.Add(new ExtraPiece(strip, pose));
            return far;
        }
        var np = pass with { AtA = On(pass.ArmA) };
        if (pass.ArmB >= 0) np = np with { AtB = On(pass.ArmB) };
        var passes = prop.Passes.ToList();
        passes[passIndex] = np;
        return prop with { Passes = passes, Extras = extras };
    }

    /// <summary>A J strip with its near edge (arm 0) on <paramref name="at"/>, running out along <paramref name="dir"/>: its pose and its far arm (where the dirt road meets it).</summary>
    public static (PiecePose Pose, (Vector2 Position, Vector2 Out) Far) StripOn(PieceDef strip, Vector2 at, Vector2 dir)
    {
        dir = Vector2.Normalize(dir);
        var pose = PiecePose.Anchored(at, strip.Arms[0].Position, MathF.Atan2(dir.X, dir.Y));
        return (pose, pose.World(strip.Arms[1]));
    }

    /// <summary>Whether every road of the proposal keeps something to stand on once cut at the arms (a little road beyond each: at least two points' worth).</summary>
    private static bool Viable(Proposal p)
    {
        var arms = JunctionRules.WorldArms(p.Def, p.Pose);
        foreach (var pass in p.Passes)
        {
            var poly = Poly.Of(pass.Road);
            var (armA, armB) = (pass.AtA ?? arms[pass.ArmA], pass.ArmB >= 0 ? pass.AtB ?? arms[pass.ArmB] : default);
            if (pass.Kind == PassKind.Through)
            {
                float s1 = poly.Nearest(armA.Position).S, s2 = poly.Nearest(armB.Position).S;
                if (s1 > s2) (s1, s2) = (s2, s1);
                if (s2 - s1 < 4 || s1 < Trim + 1 || poly.Length - s2 < Trim + 1) return false;
            }
            else
            {
                float s = poly.Nearest(armA.Position).S;
                if (pass.AtStart ? poly.Length - s < Trim + LeadIn : s < Trim + LeadIn) return false;
            }
        }
        return true;
    }

    private bool Conflicts(PieceDef def, PiecePose pose, IReadOnlyCollection<PlacedPiece>? besides = null)
    {
        var c = pose.ToWorld(def.Origin);
        foreach (var e in pieces.Items)
            if (besides?.Contains(e) != true && Vector2.Distance(e.Origin, c) < def.Reach + e.Def.Reach) return true;
        return false;
    }

    /// <summary>The asphalt look the map's pieces mostly have (the variant digit of their names), 1 when it has none.</summary>
    public int Variant()
    {
        var v = pieces.Items.Where(p => p.Def.Kind == 0 && !p.Def.Name.Contains("isect")).GroupBy(p => p.Def.Variant).OrderByDescending(g => g.Count()).FirstOrDefault();
        return v?.Key ?? 1;
    }

    private static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 p)
    {
        p = default;
        var r = b - a; var s = d - c;
        float den = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(den) < 1e-6f) return false;
        var q = c - a;
        float t = (q.X * s.Y - q.Y * s.X) / den, u = (q.X * r.Y - q.Y * r.X) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        p = a + r * t;
        return true;
    }

    // ---- connections --------------------------------------------------------------------------------------------------------------

    /// <summary>The road ends of the store: where, and the direction from the end into the road. A wide road's are not among them: it is no piece's road (a road widened at an arm leaves the piece as a bulldozed one would).</summary>
    private List<(RoadEdit Road, bool AtStart, Vector2 At, Vector2 Body)> Ends()
    {
        var o = new List<(RoadEdit, bool, Vector2, Vector2)>();
        foreach (var r in roads.Roads)
        {
            if (r.Points.Count < 2 || RoadBuilder.IsClosed(r) || !RoadBuilder.IsStandard(r)) continue;
            var poly = Poly.Of(r);
            float back = MathF.Min(6f, poly.Length * 0.5f);
            o.Add((r, true, poly.P[0], Vector2.Normalize(poly.At(back) - poly.P[0])));
            o.Add((r, false, poly.P[^1], Vector2.Normalize(poly.At(poly.Length - back) - poly.P[^1])));
        }
        return o;
    }

    private static (RoadEdit Road, bool AtStart)? RoadAt((Vector2 Position, Vector2 Out) arm, List<(RoadEdit Road, bool AtStart, Vector2 At, Vector2 Body)> ends)
    {
        foreach (var e in ends)
            if (Vector2.Distance(e.At, arm.Position) <= ArmReach && Vector2.Dot(e.Body, arm.Out) > 0.7f) return (e.Road, e.AtStart);
        return null;
    }

    /// <summary>Which arms of a piece have a road ending on them, or another piece's arm against them (a J strip on a T's stem, a cap beyond a J strip).</summary>
    public bool[] Connected(PlacedPiece p, List<(RoadEdit Road, bool AtStart, Vector2 At, Vector2 Body)>? ends = null)
    {
        ends ??= Ends();
        return [.. p.Arms.Select(a => RoadAt(a, ends) is not null || PieceAt(a, p) is not null)];
    }

    /// <summary>The other piece whose arm meets <paramref name="arm"/> face to face, or null.</summary>
    private PlacedPiece? PieceAt((Vector2 Position, Vector2 Out) arm, PlacedPiece? self)
    {
        foreach (var o in pieces.Items)
            if (!ReferenceEquals(o, self) && o.Arms.Any(b => Vector2.Distance(b.Position, arm.Position) <= ArmReach && Vector2.Dot(b.Out, arm.Out) < -0.7f)) return o;
        return null;
    }

    /// <summary>The arms of the pieces that no road meets, as the structure ends the road tool snaps to (a road meets one square on).</summary>
    public List<StructureAttach.Attach> FreeArms()
    {
        var o = new List<StructureAttach.Attach>();
        var ends = Ends();
        foreach (var p in pieces.Items)
        {
            if (p.Def.Shape is PieceShape.Cap or PieceShape.TurnAround) continue;     // the end of a road: nothing goes on from it
            int i = 0;
            foreach (var a in p.Arms)
            {
                if (RoadAt(a, ends) is null && PieceAt(a, p) is null)
                    o.Add(new StructureAttach.Attach(new Vector3(a.Position.X, ground(a.Position), a.Position.Y), a.Out, RoadBuilder.Width, false) { Slope = 0, Source = $"{p.Def.Name} arm {i}" });
                i++;
            }
        }
        return o;
    }

    public JunctionSnapshot Take()
    {
        var s = new JunctionSnapshot();
        foreach (var r in roads.Roads) s.Roads.Add(r);
        var ends = Ends();
        foreach (var p in pieces.Items) s.Connected[p] = Connected(p, ends);
        return s;
    }

    // ---- making -------------------------------------------------------------------------------------------------------------------

    private sealed class Rec(IMapDocument? doc)
    {
        public readonly List<RoadOp> Roads = [];
        public readonly List<PieceOp> Pieces = [];
        public readonly HeightRecorder? Heights = doc is null ? null : new HeightRecorder(doc);
        public readonly HashSet<RoadEdit> Touched = new(ReferenceEqualityComparer.Instance);
        public bool Any => Roads.Count > 0 || Pieces.Count > 0 || Heights?.Count > 0;
    }

    private int IndexOf(RoadEdit r) { for (int i = 0; i < roads.Count; i++) if (ReferenceEquals(roads[i], r)) return i; return -1; }

    private void ReplaceRoad(Rec rec, RoadEdit before, RoadEdit after)
    {
        int i = IndexOf(before);
        rec.Roads.Add(new RoadOp.Replace(i, before, after)); roads.Replace(i, after); rec.Touched.Add(after);
    }

    private void InsertRoad(Rec rec, int index, RoadEdit r) { rec.Roads.Add(new RoadOp.Insert(index, r)); roads.Insert(index, r); rec.Touched.Add(r); }

    private void RemoveRoad(Rec rec, RoadEdit r)
    {
        int i = IndexOf(r);
        rec.Roads.Add(new RoadOp.Remove(i, r)); roads.RemoveAt(i);
    }

    private void AddPiece(Rec rec, PlacedPiece p) { rec.Pieces.Add(new PieceOp.Insert(pieces.Count, p)); pieces.Insert(pieces.Count, p); }
    private void DropPiece(Rec rec, PlacedPiece p) { int i = IndexOfPiece(p); rec.Pieces.Add(new PieceOp.Remove(i, p)); pieces.RemoveAt(i); }
    private void SwapPiece(Rec rec, PlacedPiece old, PlacedPiece now) { int i = IndexOfPiece(old); rec.Pieces.Add(new PieceOp.Replace(i, old, now)); pieces.Replace(i, now); }
    private int IndexOfPiece(PlacedPiece p) { for (int i = 0; i < pieces.Count; i++) if (ReferenceEquals(pieces[i], p)) return i; return -1; }

    /// <summary>
    /// The part of <paramref name="road"/> before (or after) arc length <paramref name="sCut"/>, ending (or starting) on the arm: the road's own
    /// points up to <see cref="Trim"/> short of the cut, then a point <see cref="LeadIn"/> out along the arm and the arm's point, both heading
    /// along the arm. Null when nothing of the road is left to stand on.
    /// </summary>
    public static RoadEdit? Part(RoadEdit road, Poly poly, float sCut, Vector2 armPos, Vector2 armOut, bool before)
    {
        var keep = new List<RoadEditPoint>();
        for (int i = 0; i < road.Points.Count; i++)
            if (before ? poly.S[i] <= sCut - Trim : poly.S[i] >= sCut + Trim) keep.Add(road.Points[i].Clone());
        var lead = armPos + armOut * LeadIn;
        while (keep.Count > 0 && Vector2.Distance((before ? keep[^1] : keep[0]).Centre, lead) < 2.5f) keep.RemoveAt(before ? keep.Count - 1 : 0);
        if (keep.Count < 1) return null;
        var pts = new List<RoadEditPoint>();
        if (before) { pts.AddRange(keep); pts.Add(RoadBuilder.Point(lead, -armOut)); pts.Add(RoadBuilder.Point(armPos, -armOut)); }
        else { pts.Add(RoadBuilder.Point(armPos, armOut)); pts.Add(RoadBuilder.Point(lead, armOut)); pts.AddRange(keep); }
        // the stock pattern: the first two points flag 0, the rest 36 (nothing reads it)
        for (int i = 0; i < pts.Count; i++)
            if (!keep.Contains(pts[i])) pts[i].Flag = i < 2 ? 0 : RoadBuilder.FlagBody;
        return new RoadEdit(road.Kind, pts) { Tail = (byte[])road.Tail.Clone() };
    }

    /// <summary>Puts the piece of a proposal down and cuts the roads at its arms; false (nothing changed) when a road has too little left.</summary>
    private bool Apply(Rec rec, Proposal pr, bool conform, RoadGrade.Settings? grade = null)
    {
        var arms = JunctionRules.WorldArms(pr.Def, pr.Pose);
        var plans = new List<(RoadEdit Old, RoadEdit?[] Parts)>();
        foreach (var pass in pr.Passes)
        {
            var poly = Poly.Of(pass.Road);
            if (pass.Kind == PassKind.Through)
            {
                var (a1, a2) = (pass.AtA ?? arms[pass.ArmA], pass.AtB ?? arms[pass.ArmB]);
                float s1 = poly.Nearest(a1.Position).S, s2 = poly.Nearest(a2.Position).S;
                if (s1 > s2) { (a1, a2) = (a2, a1); (s1, s2) = (s2, s1); }
                plans.Add((pass.Road, [Part(pass.Road, poly, s1, a1.Position, a1.Out, true), Part(pass.Road, poly, s2, a2.Position, a2.Out, false)]));
            }
            else
            {
                var a = pass.AtA ?? arms[pass.ArmA];
                float s = poly.Nearest(a.Position).S;
                var part = Part(pass.Road, poly, s, a.Position, a.Out, before: !pass.AtStart);
                if (part is null) return false;
                plans.Add((pass.Road, [part]));
            }
        }
        foreach (var (old, parts) in plans)
        {
            int i = IndexOf(old);
            if (i < 0) return false;
            if (parts.Length == 1) { ReplaceRoad(rec, old, parts[0]!); continue; }
            if (parts[0] is { } first && parts[1] is { } second) { ReplaceRoad(rec, old, first); InsertRoad(rec, i + 1, second); }
            else if ((parts[0] ?? parts[1]) is { } one) ReplaceRoad(rec, old, one);
            else RemoveRoad(rec, old);
        }
        var origin = pr.Pose.Origin;
        float h = ground(origin);
        AddPiece(rec, pieces.Make(pr.Def, pr.Pose, h));
        foreach (var x in pr.Extras) AddPiece(rec, pieces.Make(x.Def, x.Pose, h));
        if (rec.Heights is not null)
        {
            if (conform)
            {
                Level(rec, pr, arms);
                foreach (var x in pr.Extras) LevelPiece(rec, x.Def, x.Pose, h);
            }
            PaintSurface(rec, pr.Def, pr.Pose);
            foreach (var x in pr.Extras) PaintSurface(rec, x.Def, x.Pose);
        }
        return true;
    }

    /// <summary>
    /// The surface nibble the stock maps put under their pieces (surveyed: 0xc under the asphalt ones, 0xe under dirt and planks, where the
    /// roads' own are 0x4, 0x6 and 0x8): bits 13-15 are the surface id (6 and 7 are the intersection rows of the world's surface table,
    /// grip and rolling resistance), bit 12 the impassable flag, left clear. Every sample whose ground the piece covers takes it.
    /// </summary>
    public static int SurfaceNibble(uint kind) => kind % 3 == 0 ? 0xc : 0xe;

    private void PaintSurface(Rec rec, PieceDef def, PiecePose pose)
    {
        int nib = SurfaceNibble(def.Kind);
        var polys = def.Polygons.Select(p => p.Select(pose.ToWorld).ToArray()).ToList();
        if (polys.Count == 0) return;
        var all = polys.SelectMany(p => p).ToArray();
        // the engine reads the nearest sample under a point (SampleScale, rounded): every sample that is the nearest to some point of the footprint
        var cells = new HashSet<(int, int)>();
        float step = (float)Units.SampleSize;
        for (float z = MathF.Floor(all.Min(v => v.Y)); z <= all.Max(v => v.Y); z += 0.5f)
            for (float x = MathF.Floor(all.Min(v => v.X)); x <= all.Max(v => v.X); x += 0.5f)
                if (polys.Any(poly => Inside(poly, new Vector2(x, z)))) cells.Add(((int)MathF.Floor(x / step + 0.5f), (int)MathF.Floor(z / step + 0.5f)));
        foreach (var (sx, sz) in cells) rec.Heights!.SetNibble(sx, sz, nib);
    }

    private static bool Inside(Vector2[] poly, Vector2 q)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    /// <summary>Flattens the ground under a piece to the height it stands at (the engine draws it as a plane at its corner's ground height), through the same shoulders a road gets.</summary>
    private void Level(Rec rec, Proposal pr, (Vector2 Position, Vector2 Out)[] arms)
    {
        if (LevelDoc is not { } doc) return;
        float h = ground(pr.Pose.Origin);
        foreach (var (a, b) in LevelSpines(pr.Def, arms, pr.Centre))
        {
            var line = RoadCurves.Resample([a, b], 4f);
            if (line.Count < 2) continue;
            RoadTerrain.Conform(doc, rec.Heights!, line, [.. line.Select(_ => h)], new RoadTerrain.Settings(RoadBuilder.HalfWidth + 1f, 8f));
        }
    }

    private static IEnumerable<(Vector2, Vector2)> LevelSpines(PieceDef def, (Vector2 Position, Vector2 Out)[] arms, Vector2 centre)
    {
        yield return (arms[0].Position, arms[1].Position);
        for (int i = 2; i < arms.Length; i++) yield return (centre, arms[i].Position);
    }

    /// <summary>Levels the ground under a road-end piece (a J strip, a cap, a turn-around) to <paramref name="h"/>: along its middle from the arm to the far edge, as wide as the piece.</summary>
    private void LevelPiece(Rec rec, PieceDef def, PiecePose pose, float h)
    {
        if (LevelDoc is not { } doc) return;
        float depth = def.Shape switch { PieceShape.Strip => 20f, PieceShape.TurnAround => 20f, _ => 10f };
        float half = def.Shape == PieceShape.TurnAround ? 10f : RoadBuilder.HalfWidth;
        var a = pose.ToWorld(def.Arms[0].Position);
        var b = pose.ToWorld(def.Arms[0].Position + new Vector2(0, depth));
        var line = RoadCurves.Resample([a, b], 4f);
        if (line.Count < 2) return;
        RoadTerrain.Conform(doc, rec.Heights!, line, [.. line.Select(_ => h)], new RoadTerrain.Settings(half + 1f, 8f));
    }

    /// <summary>The document whose ground is levelled under a new piece (set by the editor; null: the ground is left alone).</summary>
    public IMapDocument? LevelDoc { get; set; }

    // ---- road ends --------------------------------------------------------------------------------------------------------------------

    /// <summary>The ends a road of a kind can be given: every road can stay open or be capped; asphalt can also turn around or turn to dirt.</summary>
    public static IReadOnlyList<RoadEnd> EndChoices(uint kind) => kind % 3 == 0 ? [RoadEnd.Open, RoadEnd.Cap, RoadEnd.TurnAround, RoadEnd.ToDirt] : [RoadEnd.Open, RoadEnd.Cap];

    /// <summary>A road's end: the point and the unit vector from it into the road (along its last 5 m, which the stock roads run straight).</summary>
    public static (Vector2 At, Vector2 Body) EndFrame(RoadEdit road, bool atStart)
    {
        var poly = Poly.Of(road);
        float back = MathF.Min(5f, poly.Length * 0.5f);
        var at = atStart ? poly.P[0] : poly.P[^1];
        return (at, Vector2.Normalize(poly.At(atStart ? back : poly.Length - back) - at));
    }

    /// <summary>
    /// What a road's end is finished with and the pieces that make it (a J strip and the cap beyond it count as one asphalt cap), or null
    /// when the end is not open: it meets a junction's arm or another road's end, the road is a loop or wide (the pieces are 10 m wide), or too short.
    /// </summary>
    public (RoadEnd End, IReadOnlyList<PlacedPiece> Pieces)? EndOf(RoadEdit road, bool atStart)
    {
        if (road.Points.Count < RoadCurves.MinPoints || RoadBuilder.IsClosed(road) || !RoadBuilder.IsStandard(road)) return null;
        var (at, body) = EndFrame(road, atStart);
        if (Poly.Of(road).Length < 12f) return null;
        foreach (var p in pieces.Items)
        {
            if (!p.Def.IsEnd) continue;
            var a = p.Pose.World(p.Def.Arms[0]);
            if (Vector2.Distance(a.Position, at) > ArmReach || Vector2.Dot(a.Out, body) < 0.7f) continue;
            if (p.Def.Shape == PieceShape.TurnAround) return (RoadEnd.TurnAround, [p]);
            if (p.Def.Shape == PieceShape.Cap) return (RoadEnd.Cap, [p]);
            // a J strip: with a cap on its far edge it is an asphalt road's cap, else the road turns to dirt there
            var far = p.Pose.World(p.Def.Arms[1]);
            var cap = pieces.Items.FirstOrDefault(c => c.Def.Shape == PieceShape.Cap && c.Pose.World(c.Def.Arms[0]) is var ca && Vector2.Distance(ca.Position, far.Position) <= ArmReach && Vector2.Dot(ca.Out, far.Out) < -0.7f);
            return cap is null ? (RoadEnd.ToDirt, [p]) : (RoadEnd.Cap, [p, cap]);
        }
        // any other piece's arm, or another road's end, here: not an open end
        foreach (var p in pieces.Items)
            if (p.Arms.Any(a => Vector2.Distance(a.Position, at) <= ArmReach)) return null;
        foreach (var r in roads.Roads)
        {
            if (ReferenceEquals(r, road) || r.Points.Count < 2) continue;
            if (Vector2.Distance(r.Points[0].Centre, at) <= ArmReach || Vector2.Distance(r.Points[^1].Centre, at) <= ArmReach) return null;
        }
        return (RoadEnd.Open, []);
    }

    /// <summary>Whether the pieces of an end are the ones its road's kind takes (a road retyped since keeps pieces of the old kind).</summary>
    private static bool EndFits(RoadEnd end, IReadOnlyList<PlacedPiece> ps, uint kind) => end switch
    {
        RoadEnd.Cap => ps[0].Def.Kind == kind % 3,
        RoadEnd.TurnAround or RoadEnd.ToDirt => kind % 3 == 0,
        _ => true,
    };

    /// <summary>
    /// The pieces that finish a road's end as <paramref name="want"/> asks, in the stock maps' layout: the road ends on the middle of the
    /// piece's near edge (a cap's (5, 0), a turn-around's (10, 0), a J strip's (5, 0), a dirt cap on the strip's far edge (5, 20)), the piece
    /// running on straight from it. Empty for <see cref="RoadEnd.Open"/> or a choice the road's kind has no piece for.
    /// </summary>
    public List<ExtraPiece> EndPieces(RoadEdit road, bool atStart, RoadEnd want, int variant = 0)
    {
        var o = new List<ExtraPiece>();
        if (want == RoadEnd.Open || !EndChoices(road.Kind).Contains(want)) return o;
        if (variant == 0) variant = Variant();
        var (at, body) = EndFrame(road, atStart);
        var dir = -body;
        float yaw = MathF.Atan2(dir.X, dir.Y);
        uint kind = road.Kind % 3;
        if (want == RoadEnd.TurnAround && PieceCatalogue.Pick(PieceShape.TurnAround, 0, variant) is { } turn)
            o.Add(new ExtraPiece(turn, PiecePose.Anchored(at, turn.Arms[0].Position, yaw)));
        else if (kind == 0 && want is RoadEnd.Cap or RoadEnd.ToDirt && PieceCatalogue.Pick(PieceShape.Strip, 0, variant) is { } strip)
        {
            var (pose, far) = StripOn(strip, at, dir);
            o.Add(new ExtraPiece(strip, pose));
            if (want == RoadEnd.Cap && PieceCatalogue.Pick(PieceShape.Cap, 1) is { } dirt)
                o.Add(new ExtraPiece(dirt, PiecePose.Anchored(far.Position, dirt.Arms[0].Position, yaw)));
        }
        else if (want == RoadEnd.Cap && PieceCatalogue.Pick(PieceShape.Cap, kind) is { } cap)
            o.Add(new ExtraPiece(cap, PiecePose.Anchored(at, cap.Arms[0].Position, yaw)));
        return o;
    }

    /// <summary>Puts a road end's pieces down (at the height of the road's end), levels the ground under them and gives it the pieces' surface.</summary>
    private bool PlaceEnd(Rec rec, RoadEdit road, bool atStart, RoadEnd want, bool conform, IReadOnlyCollection<PlacedPiece>? besides = null)
    {
        var made = EndPieces(road, atStart, want);
        if (made.Any(x => Conflicts(x.Def, x.Pose, besides))) return false;
        float h = ground(EndFrame(road, atStart).At);
        foreach (var x in made) AddPiece(rec, pieces.Make(x.Def, x.Pose, h));
        if (rec.Heights is not null)
            foreach (var x in made)
            {
                if (conform) LevelPiece(rec, x.Def, x.Pose, h);
                PaintSurface(rec, x.Def, x.Pose);
            }
        return true;
    }

    /// <summary>
    /// Finishes a road's open end as <paramref name="want"/> asks (taking away what finished it before): one undo step with the pieces,
    /// the levelled ground and its surface. Null when nothing changes, the end is not open, or the pieces would stand on another piece
    /// (<paramref name="why"/> says which).
    /// </summary>
    public IEditCommand? SetEnd(RoadEdit road, bool atStart, RoadEnd want, out string why, bool conform = true)
    {
        why = "";
        if (EndOf(road, atStart) is not { } now) { why = "That end meets a junction, another road or a structure: it is not an open end"; return null; }
        if (now.End == want) return null;
        if (!EndChoices(road.Kind).Contains(want)) { why = $"A {RoadBuilder.KindName(road.Kind).ToLowerInvariant()} road has no {Describe(want)}"; return null; }
        var rec = new Rec(LevelDoc);
        if (want != RoadEnd.Open && EndPieces(road, atStart, want).Any(x => Conflicts(x.Def, x.Pose, now.Pieces))) { why = "It would overlap another junction piece"; return null; }
        foreach (var p in now.Pieces) DropPiece(rec, p);
        if (want != RoadEnd.Open) PlaceEnd(rec, road, atStart, want, conform);
        return Finish(rec, want == RoadEnd.Open ? "Open road end" : $"Road end: {Describe(want)}");
    }

    public static string Describe(RoadEnd e) => e switch
    {
        RoadEnd.Cap => "end cap",
        RoadEnd.TurnAround => "turn-around",
        RoadEnd.ToDirt => "turn to dirt",
        _ => "open end",
    };

    // ---- settling after an edit -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// After a road edit that has been made (a road drawn, moved, retyped, bulldozed): pieces that lost a road at an arm are changed (an
    /// X that lost one becomes a T; one that lost its through road goes, the two roads it joined are one again) and the roads the edit
    /// made or changed get their junctions. Returns the step to push after the edit's own command, or null if nothing followed.
    /// </summary>
    public IEditCommand? Settle(JunctionSnapshot before, string name = "Junctions", bool conform = true)
    {
        var rec = new Rec(LevelDoc);
        var ends = Ends();
        // a piece that goes can leave its neighbour without what met its arm (a J strip whose dirt road went takes the T it stood on): again until nothing changes
        for (bool again = true; again;)
        {
            again = false;
            foreach (var p in pieces.Items.ToList())
            {
                if (IndexOfPiece(p) < 0 || !before.Connected.TryGetValue(p, out var old)) continue;
                var now = Connected(p, ends);
                if (old.Zip(now).Any(z => z.First && !z.Second)) { Broken(rec, p, now, ends); ends = Ends(); again = true; }
            }
        }
        Retyped(rec, conform);
        // the roads the edit made: the ones the snapshot did not have, and whatever cutting them makes
        foreach (var r in roads.Roads) if (!before.Roads.Contains(r)) rec.Touched.Add(r);
        var tried = new HashSet<(int, int)>();
        for (int guard = 0; guard < 16; guard++)
        {
            Proposal? next = null;
            foreach (var r in roads.Roads.Where(r => rec.Touched.Contains(r)).ToList())
                if (Detect(r).FirstOrDefault(p => !tried.Contains(((int)MathF.Round(p.Centre.X), (int)MathF.Round(p.Centre.Y)))) is { } p) { next = p; break; }
            if (next is null) break;
            tried.Add(((int)MathF.Round(next.Centre.X), (int)MathF.Round(next.Centre.Y)));
            Apply(rec, next, conform);
        }
        return Finish(rec, name);
    }

    private IEditCommand? Finish(Rec rec, string name)
    {
        if (!rec.Any) return null;
        return new JunctionCommand(name, new RoadCommand(name, roads, rec.Roads, rec.Heights?.Tiles()), rec.Pieces, pieces);
    }

    /// <summary>A piece lost a road at an arm.</summary>
    private void Broken(Rec rec, PlacedPiece p, bool[] now, List<(RoadEdit Road, bool AtStart, Vector2 At, Vector2 Body)> ends)
    {
        var arms = p.Arms.ToArray();
        var left = Enumerable.Range(0, arms.Length).Where(i => now[i]).ToList();
        // an X that lost a stem is a T of the remaining three arms
        if (p.Def.Shape == PieceShape.X && left.Count == 3 && PieceCatalogue.Pick(PieceShape.T, p.Def.Kind, p.Def.Variant) is { } t
            && JunctionRules.FitArms(t, p.Origin, p.Pose.Yaw, [.. left.Select(i => arms[i].Out)]) is { } pose)
        {
            SwapPiece(rec, p, pieces.Remake(p, t, pose, ground(pose.Origin)));
            return;
        }
        DropPiece(rec, p);
        if (p.Def.IsEnd) return;            // a road's end: nothing ran through it
        // the roads that ran through it are one road again: the two ends it kept apart
        foreach (var (a, b) in new[] { (0, 1), (2, 3) })
            if (a < arms.Length && b < arms.Length && now[a] && now[b]
                && RoadAt(arms[a], ends) is { } ra && RoadAt(arms[b], ends) is { } rb && !ReferenceEquals(ra.Road, rb.Road))
                Merge(rec, ra, rb);
    }

    /// <summary>One road from two that met at a piece: the first's points, a straight run over the gap, the second's.</summary>
    private void Merge(Rec rec, (RoadEdit Road, bool AtStart) a, (RoadEdit Road, bool AtStart) b)
    {
        var ca = RoadBuilder.Centres(a.Road); if (a.AtStart) ca.Reverse();        // ends at the join
        var cb = RoadBuilder.Centres(b.Road); if (!b.AtStart) cb.Reverse();       // starts at the join
        var all = new List<Vector2>(ca);
        float gap = Vector2.Distance(ca[^1], cb[0]);
        int steps = (int)MathF.Round(gap / RoadCurves.Spacing);
        for (int k = 1; k < steps; k++) all.Add(Vector2.Lerp(ca[^1], cb[0], k / (float)steps));
        all.AddRange(cb);
        var merged = RoadBuilder.Build(all, a.Road.Kind, width: RoadBuilder.WidthOf(a.Road));
        ReplaceRoad(rec, a.Road, merged);
        RemoveRoad(rec, b.Road);
    }

    /// <summary>
    /// A road retyped under a piece: the piece takes the through road's kind (no T or X exists for planks: it goes). A road whose end
    /// pieces no longer fit its kind gets the same end in its new kind (a cap stays a cap; a turn-around becomes a cap, a J strip alone goes).
    /// </summary>
    private void Retyped(Rec rec, bool conform = true)
    {
        foreach (var r in roads.Roads.ToList())
            foreach (bool atStart in new[] { true, false })
            {
                if (EndOf(r, atStart) is not { Pieces.Count: > 0 } e || EndFits(e.End, e.Pieces, r.Kind)) continue;
                foreach (var p in e.Pieces) DropPiece(rec, p);
                var want = r.Kind % 3 == 0 ? e.End : e.End == RoadEnd.ToDirt ? RoadEnd.Open : RoadEnd.Cap;
                PlaceEnd(rec, r, atStart, want, conform);
            }
        var ends = Ends();
        foreach (var p in pieces.Items.ToList())
        {
            if (p.Def.IsEnd) continue;
            var arms = p.Arms.ToArray();
            var r = RoadAt(arms[0], ends) ?? RoadAt(arms[1], ends);
            if (r is null || r.Value.Road.Kind % 3 == p.Def.Kind) continue;
            var def = PieceCatalogue.Pick(p.Def.Shape, r.Value.Road.Kind, p.Def.Variant);
            if (def is null) DropPiece(rec, p);
            else
            {
                SwapPiece(rec, p, pieces.Remake(p, def, p.Pose, p.Data.Position.Y));
                if (rec.Heights is not null) PaintSurface(rec, def, p.Pose);
            }
        }
    }

    /// <summary>Pushes the edit's command and what followed as one step (null follow-up: the edit alone).</summary>
    public static IEditCommand Join(string name, IEditCommand main, IEditCommand? follow) => follow is null ? main : new StepCommand(name, [main, follow]);
}
