using System.Numerics;
using I76.Editor.Objects;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>What the road tool lays: the road on the ground, a bridge carried by deck pieces, or a tunnel through a hill.</summary>
public enum BuildMode { Ground, Bridge, Tunnel }

/// <summary>A choice of pieces for a span: the pieces end to end, their total length (decks are measured along the deck) and how far it is from the length asked for.</summary>
public sealed record Tiling(IReadOnlyList<CatalogueItem> Pieces, float Length, float Error)
{
    public string Describe()
    {
        var groups = Pieces.GroupBy(p => p.Name).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} x {g.Key}");
        return string.Join(" + ", groups);
    }
}

/// <summary>
/// The bridge and tunnel pieces the road tool lays, and how it chooses among them. They are the catalogue's road-carrying structures:
/// bridges are straight, level, fixed-length decks (60, 80, 100 and 160 m long and 10 wide) of three styles, tunnels a mountain with a
/// 12 m tube of 48, 108 or 156 m. Nothing is stretched or scaled: a span is laid in whole pieces, so it is snapped to the sum of their lengths.
/// </summary>
public static class StretchPieces
{
    /// <summary>
    /// A bridge piece: a type-0xc deck of the engineering family (<c>be*</c>), one road wide (the stock overpass <c>beovrps1</c> is 30 wide and
    /// goes over roads; <c>bcaik8</c> is a building the maps give the bridge type), and long enough to span something. The ramps are 0xb, the tunnels have no deck.
    /// </summary>
    public static bool IsBridge(CatalogueItem i) =>
        i.CarriesRoad && !i.IsTunnel && i.Type == 0xc && i.Name.StartsWith("be", StringComparison.OrdinalIgnoreCase) && i.Width is >= 8f and <= 12.5f && Length(i) >= 20;
    public static bool IsTunnel(CatalogueItem i) => i.CarriesRoad && i.IsTunnel;

    /// <summary>How long the piece's road is: a deck's two ends along its axis, a tunnel's tube between its portals (m).</summary>
    public static float Length(CatalogueItem i)
    {
        var e = i.DeckEnds();
        return MathF.Abs(e[1].Position.Z - e[0].Position.Z);
    }

    /// <summary>The style a bridge belongs to, from how tall it stands (its trusses, rails, arches): 0 low concrete decks, 1 timber, 2 tall trusses. Pieces of one style look alike, so the automatic choice never mixes styles.</summary>
    public static int Style(CatalogueItem i) => i.MaxY < 5f ? 0 : i.MaxY < 10f ? 1 : 2;

    public static string StyleName(int style) => style switch { 0 => "CONCRETE", 1 => "TIMBER", _ => "TRUSS" };

    /// <summary>The pieces for a mode, shortest first within a style.</summary>
    public static List<CatalogueItem> For(IEnumerable<CatalogueItem> catalogue, BuildMode mode) => mode switch
    {
        BuildMode.Bridge => [.. catalogue.Where(IsBridge).OrderBy(Style).ThenBy(Length).ThenBy(i => i.Name, StringComparer.Ordinal)],
        BuildMode.Tunnel => [.. catalogue.Where(IsTunnel).OrderBy(Length).ThenBy(i => i.Name, StringComparer.Ordinal)],
        _ => [],
    };

    public const int MaxPieces = 14;

    /// <summary>
    /// The pieces that cover a span of <paramref name="target"/> metres best. With <paramref name="only"/> that one model is repeated (whole
    /// pieces, nearest to the target). Without it, each style is tried and the one whose total is nearest wins (a joint costs <see cref="JointCost"/> m of error, then the order concrete, timber, truss breaks a tie). A tunnel is one piece (a chain would put a portal in the middle of the hill). Null when the span is
    /// shorter than half the shortest piece.
    /// </summary>
    public static Tiling? Tile(IReadOnlyList<CatalogueItem> candidates, CatalogueItem? only, float target)
    {
        if (candidates.Count == 0 && only is null) return null;
        if (only is not null) return Repeat(only, target, only.IsTunnel ? 1 : MaxPieces);
        var tunnels = candidates.Where(IsTunnel).ToList();
        if (tunnels.Count > 0 && tunnels.Count == candidates.Count)
        {
            var best = tunnels.OrderBy(t => MathF.Abs(Length(t) - target)).ThenBy(Length).First();
            return Repeat(best, target, 1);
        }
        Tiling? win = null;
        foreach (var style in candidates.Select(Style).Distinct().Order())
        {
            // one model per distinct length (the Nitro Pack's bebridga/b are the same decks as bebridg2/3)
            var models = candidates.Where(c => Style(c) == style).GroupBy(c => MathF.Round(Length(c) * 2) / 2).Select(g => g.First()).OrderByDescending(Length).ToList();
            var t = BestMix(models, target);
            if (t is null) continue;
            if (win is null || Score(t) < Score(win) - 0.01f) win = t;
        }
        return win;
    }

    private static float Score(Tiling t) => MathF.Abs(t.Error) + JointCost * (t.Pieces.Count - 1);

    private static Tiling? Repeat(CatalogueItem model, float target, int max)
    {
        float l = Length(model);
        if (target < l / 2) return null;
        int n = Math.Clamp((int)MathF.Round(target / l), 1, max);
        return new Tiling([.. Enumerable.Repeat(model, n)], n * l, n * l - target);
    }

    /// <summary>
    /// What a joint costs in metres of snapping error. A stock deck is a 1 m hump (it rises over its first 20 m and falls over its last 20),
    /// so two decks end to end dip a metre across their joint: a longer piece is worth up to this much error.
    /// </summary>
    public const float JointCost = 6f;

    private static Tiling? BestMix(List<CatalogueItem> models, float target)
    {
        float min = models.Min(Length);
        if (target < min / 2) return null;
        var counts = new int[models.Count];
        (float Score, int N, int[] C)? best = null;
        void Go(int k, float sum, int n)
        {
            if (k == models.Count)
            {
                if (n == 0) return;
                float score = MathF.Abs(sum - target) + JointCost * (n - 1);
                if (best is null || score < best.Value.Score - 0.01f) best = (score, n, (int[])counts.Clone());
                return;
            }
            float l = Length(models[k]);
            for (int c = 0; n + c <= MaxPieces && sum + c * l <= target + min + l; c++)
            {
                counts[k] = c;
                Go(k + 1, sum + c * l, n + c);
            }
            counts[k] = 0;
        }
        Go(0, 0, 0);
        if (best is not { } b) return null;
        var pieces = new List<CatalogueItem>();
        for (int k = 0; k < models.Count; k++) for (int c = 0; c < b.C[k]; c++) pieces.Add(models[k]);
        float total = pieces.Sum(Length);
        return new Tiling(pieces, total, total - target);
    }
}

/// <summary>One piece of a stretch as it will stand: the origin it is written with, the heading and pitch, where its deck starts.</summary>
public sealed record StretchPiece(CatalogueItem Item, Vector2 Origin, float Yaw, float Pitch, float StartHeight, float Length, float Along)
{
    /// <summary>The y the record is written with when the ground at its origin has been brought to the plan: the game stands the origin on the ground.</summary>
    public float OriginY { get; init; }
}

/// <summary>The span the road tool is about to lay (or has laid), with what it says about it.</summary>
public sealed record StretchPlan(
    BuildMode Mode, Vector2 Start, Vector2 End, Vector2 Dir, float Run, float StartHeight, float EndHeight,
    IReadOnlyList<StretchPiece> Pieces, Tiling? Tiling, string Summary, IReadOnlyList<string> Warnings, string? Problem,
    IReadOnlyList<Vector2> Samples, IReadOnlyList<float> Deck, IReadOnlyList<float> Ground)
{
    /// <summary>What the commit will do to the ground, that is not a problem: the piers.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public float Yaw => MathF.Atan2(Dir.X, Dir.Y);
    public float Grade => Run > 0 ? (EndHeight - StartHeight) / Run : 0;
    public bool Ok => Problem is null && Pieces.Count > 0;
}

/// <summary>What the stretch is asked to be.</summary>
public sealed class StretchQuery
{
    public required IReadOnlyList<CatalogueItem> Catalogue { get; init; }
    public required BuildMode Mode { get; init; }
    public required Vector2 Start { get; init; }
    /// <summary>Where the cursor is: the span runs toward it and ends on whole pieces (never past the pieces it needs).</summary>
    public required Vector2 Aim { get; init; }
    public required Func<Vector2, float> Ground { get; init; }
    /// <summary>The model to repeat; null for the automatic choice.</summary>
    public CatalogueItem? Only { get; init; }
    /// <summary>Set when the start is on a deck's end: the span goes on straight from it.</summary>
    public Vector2? Heading { get; init; }
    /// <summary>The height the start must have (the end of a road or a deck): else the ground's.</summary>
    public float? StartHeight { get; init; }
    public float MaxGrade { get; init; } = RoadStructures.BridgeMaxGrade;
    public Func<Vector2, bool>? OnMap { get; init; }
}

/// <summary>
/// Bridge and tunnel stretches for the road tool (docs/map-editor.md, Bridges and tunnels). The game stands every placed object on the ground at its
/// origin and bridge pieces are fixed straight decks, so a stretch is a chain of pieces laid on a straight line, each pitched so that the
/// decks join end to end on one grade; the ground at each piece's origin is brought to the deck's height (a pier of terrain under the joint;
/// the pieces have no piers of their own). A tunnel is one level piece with the road running through and the ground levelled under its mountain.
/// </summary>
public static class RoadStructures
{
    /// <summary>The steepest a bridge is pitched: below StructureAttach.FlatSlope, so that the road tool still snaps a road to its ends as to a level deck.</summary>
    public const float BridgeMaxGrade = 0.07f;
    public const float BridgeWidth = 10f;
    /// <summary>The ground at an origin that is nearer than this to the plan's height is left alone (m).</summary>
    public const float Tolerance = 0.06f;
    /// <summary>A bridge more than this off the map's axes is not used by the AI (the engine's bridge search wants within 0.9 of an axis: 25.8 degrees).</summary>
    public const float AxisToleranceDegrees = 25f;

    /// <summary>The rotation record of a piece with a heading and a pitch (rows are the right, up and forward axes; pitch lifts the forward axis).</summary>
    public static float[] Rotation(float yaw, float pitch)
    {
        float s = MathF.Sin(yaw), c = MathF.Cos(yaw), sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        return [c, 0, -s, -s * sp, cp, -c * sp, s * cp, sp, c * cp];
    }

    private static Vector2 Right(float yaw) => new(MathF.Cos(yaw), -MathF.Sin(yaw));

    public static StretchPlan Plan(StretchQuery q) => q.Mode switch
    {
        BuildMode.Bridge => PlanBridge(q),
        BuildMode.Tunnel => PlanTunnel(q),
        _ => throw new ArgumentException("a ground road has no stretch"),
    };

    private static StretchPlan Fail(StretchQuery q, string problem) =>
        new(q.Mode, q.Start, q.Start, Vector2.UnitY, 0, 0, 0, [], null, "", [], problem, [], [], []);

    private static bool Direction(StretchQuery q, out Vector2 u, out float d)
    {
        var delta = q.Aim - q.Start;
        if (q.Heading is { } h && h.LengthSquared() > 1e-6f) { u = Vector2.Normalize(h); d = Vector2.Dot(delta, u); return d > 1; }
        d = delta.Length();
        u = d > 1e-3f ? delta / d : Vector2.UnitY;
        return d > 1;
    }

    // ---- bridge --------------------------------------------------------------------------------------------------------------

    private static StretchPlan PlanBridge(StretchQuery q)
    {
        var cands = StretchPieces.For(q.Catalogue, BuildMode.Bridge);
        if (cands.Count == 0 && q.Only is null) return Fail(q, "The palette has no bridge pieces");
        if (!Direction(q, out var u, out float d)) return Fail(q, "Drag out the length of the bridge");
        var tiling = StretchPieces.Tile(cands, q.Only, d);
        if (tiling is null) return Fail(q, $"Too short for a bridge: the shortest piece is {cands.Min(StretchPieces.Length):0} m");
        float total = tiling.Length;
        float h0 = q.StartHeight ?? q.Ground(q.Start);
        float cap = Math.Min(q.MaxGrade, BridgeMaxGrade);
        // the far end rises or falls to the ground there as far as the grade allows; the run is a little shorter than the deck (the deck is pitched)
        float s = 0, cp = 1;
        var end = q.Start + u * total;
        for (int k = 0; k < 4; k++)
        {
            s = Math.Clamp((q.Ground(end) - h0) / total, -cap, cap);
            cp = MathF.Sqrt(1 - s * s);
            end = q.Start + u * (total * cp);
        }
        float h1 = h0 + s * total;
        float yaw = MathF.Atan2(u.X, u.Y), pitch = MathF.Asin(s);
        var right = Right(yaw);
        var pieces = new List<StretchPiece>();
        float along3 = 0;
        foreach (var item in tiling.Pieces)
        {
            var e0 = item.DeckEnds()[0];
            float l = StretchPieces.Length(item);
            var at = q.Start + u * (along3 * cp);
            float hStart = h0 + s * along3;
            var origin = at - right * e0.Position.X - u * (e0.Position.Z * cp);
            pieces.Add(new StretchPiece(item, origin, yaw, pitch, hStart, l, along3 * cp) { OriginY = hStart - (e0.Position.Y * cp + e0.Position.Z * s) });
            along3 += l;
        }
        float run = total * cp;
        var warnings = new List<string>();
        // the ground under the deck: where it stands above the deck, the deck is buried
        var samples = new List<Vector2>(); var deck = new List<float>(); var ground = new List<float>();
        int steps = Math.Max(2, (int)MathF.Ceiling(run / 4f));
        float buried = 0, worst = 0, worstAt = 0, clear = 0;
        var joints = pieces.Select(p => p.Along).ToList();
        for (int i = 0; i <= steps; i++)
        {
            float t = run * i / steps;
            var c = q.Start + u * t;
            float hd = h0 + s * (t / cp);
            float g = MathF.Max(q.Ground(c), MathF.Max(q.Ground(c + right * 4.5f), q.Ground(c - right * 4.5f)));
            samples.Add(c); deck.Add(hd); ground.Add(q.Ground(c));
            clear = MathF.Max(clear, hd - q.Ground(c));
            bool atJoint = joints.Any(j => MathF.Abs(j - t) < 4f);
            if (g > hd + 0.4f && !atJoint)
            {
                buried += run / steps;
                if (g - hd > worst) { worst = g - hd; worstAt = t; }
            }
        }
        if (buried > 0) warnings.Add($"The deck is below the ground for {buried:0} m (up to {worst:0.0} m, {worstAt:0} m along): the bridge is buried there. Use a tunnel, or start higher.");
        if (MathF.Abs(q.Ground(end) - h1) > 1f) warnings.Add($"The grade is held to {cap * 100:0}%: the far end stands {MathF.Abs(h1 - q.Ground(end)):0.0} m {(h1 > q.Ground(end) ? "above" : "below")} the ground, which is graded to meet it.");
        int piers = pieces.Skip(1).Count(p => MathF.Abs(q.Ground(p.Origin) - p.OriginY) > Tolerance);
        var notes = new List<string>();
        if (clear > 1.5f && piers > 0) notes.Add($"The deck stands up to {clear:0} m above the ground: the ground is raised under {piers} joint{(piers == 1 ? "" : "s")} like a pier.");
        float axisOff = MathF.Abs(MathF.Atan2(u.X, u.Y) * 180 / MathF.PI % 90); axisOff = MathF.Min(axisOff, 90 - axisOff);
        if (axisOff > AxisToleranceDegrees) warnings.Add($"The bridge is {axisOff:0} degrees off the map's axes: AI cars will not cross a bridge more than {AxisToleranceDegrees:0} degrees off them.");
        if (q.OnMap is { } on && (!on(q.Start) || !on(end) || pieces.Any(p => !on(p.Origin)))) return Fail(q, "The bridge runs off the map");
        string drawn = $"{d:0} m drawn";
        string summary = $"{tiling.Describe()}: {total:0} m ({(MathF.Abs(tiling.Error) < 0.5f ? "as drawn" : drawn)}), grade {s * 100:+0.0;-0.0;0}%";
        return new StretchPlan(BuildMode.Bridge, q.Start, end, u, run, h0, h1, pieces, tiling, summary, warnings, null, samples, deck, ground) { Notes = notes };
    }

    // ---- tunnel --------------------------------------------------------------------------------------------------------------

    private static StretchPlan PlanTunnel(StretchQuery q)
    {
        var cands = StretchPieces.For(q.Catalogue, BuildMode.Tunnel);
        if (cands.Count == 0 && q.Only is null) return Fail(q, "The palette has no tunnels");
        if (!Direction(q, out var u, out float d)) return Fail(q, "Drag out the length of the tunnel");
        var tiling = StretchPieces.Tile(cands, q.Only, d);
        if (tiling is null) return Fail(q, $"Too short for a tunnel: the shortest is {cands.Min(StretchPieces.Length):0} m");
        var item = tiling.Pieces[0];
        float len = StretchPieces.Length(item);
        float yaw = MathF.Atan2(u.X, u.Y);
        var right = Right(yaw);
        var origin = q.Start - right * item.TunnelX - u * item.TunnelZ0;
        var end = q.Start + u * len;
        var fp = Footprint.Of(item, new Vector3(origin.X, 0, origin.Y), yaw);
        var pts = fp.Corners().Append(fp.Centre).ToArray();
        // the tunnel is level: at the road it starts from, else the mean of the ground under the mountain (the levelling the stock maps have)
        float h = q.StartHeight ?? pts.Average(p => q.Ground(p));
        var warnings = new List<string>();
        float above = 0, below = 0;
        var samples = new List<Vector2>(); var deck = new List<float>(); var ground = new List<float>();
        int steps = Math.Max(2, (int)MathF.Ceiling(len / 4f));
        for (int i = 0; i <= steps; i++)
        {
            var c = q.Start + u * (len * i / steps);
            samples.Add(c); deck.Add(h); ground.Add(q.Ground(c));
        }
        for (int ix = 0; ix <= 6; ix++)
            for (int iz = 0; iz <= 8; iz++)
            {
                var g = q.Ground(fp.ToWorld(new Vector2(item.MinX + (item.MaxX - item.MinX) * ix / 6f, item.MinZ + (item.MaxZ - item.MinZ) * iz / 8f)));
                above = MathF.Max(above, g - h); below = MathF.Max(below, h - g);
            }
        if (below > 6f) warnings.Add($"The ground is up to {below:0} m below the tunnel floor: the mountain stands on {below:0} m of fill and sticks out of the terrain. Start lower, or pick a hill.");
        else if (below > 3f) warnings.Add($"The mountain stands on up to {below:0} m of fill.");
        if (above > 25f) warnings.Add($"The tunnel is cut {above:0} m into the hill at its highest: a deep cut.");
        float portalA = q.Ground(q.Start), portalB = q.Ground(end);
        if (MathF.Abs(portalA - h) > 4f || MathF.Abs(portalB - h) > 4f) warnings.Add($"A portal stands {MathF.Max(MathF.Abs(portalA - h), MathF.Abs(portalB - h)):0} m from the natural ground: the ground is levelled under the mountain and the road is graded out of it.");
        if (q.OnMap is { } on && (!on(q.Start) || !on(end) || fp.Corners().Any(p => !on(p)))) return Fail(q, "The tunnel runs off the map");
        var piece = new StretchPiece(item, origin, yaw, 0, h, len, 0) { OriginY = h };
        string summary = $"{item.Name}: tube {len:0} m ({(MathF.Abs(tiling.Error) < 0.5f ? "as drawn" : $"{d:0} m drawn")}), level at {h:0.0} m";
        return new StretchPlan(BuildMode.Tunnel, q.Start, end, u, len, h, h, [piece], tiling, summary, warnings, null, samples, deck, ground);
    }

    // ---- making it -----------------------------------------------------------------------------------------------------------

    /// <summary>What <see cref="Commit"/> made.</summary>
    public sealed record Result(IEditCommand Command, IReadOnlyList<EditObject> Objects, RoadCommand? Road, int Piers, int Cuts, string Message);

    /// <summary>
    /// Lays the stretch: the ground brought to the plan's heights where the pieces' origins (and the far end) stand, the roads under a bridge cut
    /// at its ends, the pieces written to the object layer with the pitch that joins their decks, and, for a tunnel, the ground levelled under its
    /// mountain and the road laid through it. All of it is one undo step. The terrain is changed now. <paramref name="start"/> is the road end the
    /// tunnel road extends, if any.
    /// </summary>
    public static Result? Commit(IMapDocument doc, ObjectLayer layer, RoadStore roads, StretchPlan plan, bool cutRoads, RoadGrade.Settings grade, RoadTerrain.Settings shoulder,
        uint roadKind = 0, SnapResult? start = null, bool conform = true)
    {
        if (!plan.Ok) return null;
        return plan.Mode == BuildMode.Tunnel ? CommitTunnel(doc, layer, roads, plan, grade, shoulder, roadKind, start, conform) : CommitBridge(doc, layer, roads, plan, cutRoads, shoulder);
    }

    private static Result CommitBridge(IMapDocument doc, ObjectLayer layer, RoadStore roads, StretchPlan plan, bool cutRoads, RoadTerrain.Settings shoulder)
    {
        var rec = new HeightRecorder(doc);
        float Ground(Vector2 p) => RoadTerrain.Ground(doc, p.X, p.Y);
        int piers = 0;
        var right = Right(plan.Yaw);
        for (int i = 0; i < plan.Pieces.Count; i++)
        {
            var p = plan.Pieces[i];
            if (MathF.Abs(Ground(p.Origin) - p.OriginY) <= Tolerance) continue;
            bool edge = i == 0;
            Pad(doc, rec, p.Origin, right, p.OriginY, edge ? 6.5f : 2.5f, edge ? shoulder.Shoulder : 5f);
            if (!edge) piers++;
        }
        if (MathF.Abs(Ground(plan.End) - plan.EndHeight) > 0.15f)
            Conform(doc, rec, [plan.End - plan.Dir * 2f, plan.End + plan.Dir * 8f], plan.EndHeight, 6.5f, shoulder.Shoulder);
        var ops = new List<RoadOp>();
        int cuts = 0;
        if (cutRoads) ops = RoadSplit.Ops(roads.Roads, new Deck(plan.Start, plan.End, BridgeWidth), out cuts);
        var terrain = new RoadCommand(cuts > 0 ? $"Cut {cuts} road{(cuts == 1 ? "" : "s")}" : "Ground under the bridge", roads, ops, rec.Tiles()).Apply();
        // the pieces, each pitched to meet the next at the height the ground at its origin now has
        var made = new List<EditObject>();
        int n = plan.Pieces.Count;
        var oy = plan.Pieces.Select(p => Ground(p.Origin)).ToArray();
        for (int i = 0; i < n; i++)
        {
            var p = plan.Pieces[i];
            var e0 = p.Item.DeckEnds()[0];
            float cp0 = MathF.Cos(p.Pitch), s0 = MathF.Sin(p.Pitch);
            float DeckAt(int k) => oy[k] + e0.Position.Y * cp0 + e0.Position.Z * s0;
            float next = i + 1 < n ? DeckAt(i + 1) : plan.EndHeight;
            float pitch = MathF.Asin(Math.Clamp((next - DeckAt(i)) / p.Length, -0.2f, 0.2f));
            var data = MapObject.Create(p.Item.Name, p.Item.Type, new Vector3(p.Origin.X, oy[i], p.Origin.Y), p.Yaw);
            data.Rotation = Rotation(p.Yaw, pitch);
            made.Add(layer.Add(data));
        }
        var name = plan.Pieces.Count == 1 ? $"Bridge ({plan.Pieces[0].Item.Name})" : $"Bridge ({plan.Pieces.Count} pieces)";
        var cmd = new ObjectCommands.Group(name, [terrain, new ObjectCommands.Place(layer, made, "Place")]);
        string msg = $"Bridge: {plan.Tiling!.Describe()}, {plan.Run:0} m{(piers > 0 ? $", {piers} pier{(piers == 1 ? "" : "s")} of ground" : "")}{(cuts > 0 ? $", {cuts} road{(cuts == 1 ? "" : "s")} cut" : "")}";
        return new Result(cmd, made, terrain, piers, cuts, msg);
    }

    private static Result CommitTunnel(IMapDocument doc, ObjectLayer layer, RoadStore roads, StretchPlan plan, RoadGrade.Settings grade, RoadTerrain.Settings shoulder, uint roadKind, SnapResult? start, bool conform)
    {
        var p = plan.Pieces[0];
        var parts = new List<IEditCommand>();
        var fp = Footprint.Of(p.Item, new Vector3(p.Origin.X, 0, p.Origin.Y), p.Yaw);
        if (Leveling.Flatten(doc, fp, (x, z) => RoadTerrain.Ground(doc, x, z), plan.StartHeight) is { } stroke) parts.Add(stroke);
        // the road runs through, level: every point is pinned to the floor
        var path = new List<Vector2> { plan.Start, plan.End };
        var roadPlan = RoadPlanner.Plan(path, roadKind, start is { Kind: SnapKind.Structure or SnapKind.RoadEnd } s ? s : null, null, roads);
        RoadCommand? road = null;
        if (roadPlan.Problem is null)
        {
            var pins = Enumerable.Range(0, roadPlan.Centres.Count).ToDictionary(i => i, _ => plan.StartHeight);
            roadPlan = roadPlan with { Pins = pins };
            if (RoadPlanner.Commit(doc, roads, roadPlan, conform, grade, shoulder, "Road through the tunnel") is { } r) { road = r.Command; parts.Add(road); }
        }
        float oy = RoadTerrain.Ground(doc, p.Origin.X, p.Origin.Y);
        var data = MapObject.Create(p.Item.Name, p.Item.Type, new Vector3(p.Origin.X, oy, p.Origin.Y), p.Yaw);
        var e = layer.Add(data);
        parts.Add(new ObjectCommands.Place(layer, [e], "Place"));
        var cmd = new ObjectCommands.Group($"Tunnel ({p.Item.Name})", parts);
        return new Result(cmd, [e], road, 0, 0, $"Tunnel: {p.Item.Name}, {plan.Run:0} m, level at {plan.StartHeight:0.0} m{(road is null ? "" : ", the road runs through")}");
    }

    /// <summary>
    /// Brings the ground at a piece's origin to <paramref name="height"/>: a plateau along the deck's start edge (the samples near the edge, so the
    /// pier is a small flat top with a short slope), then the four samples round the origin forced to the height, so that the bilinear ground
    /// the game stands the origin on is exactly it (to a tenth of a metre).
    /// </summary>
    private static void Pad(IMapDocument doc, HeightRecorder rec, Vector2 origin, Vector2 right, float height, float flat, float shoulder)
    {
        Conform(doc, rec, [origin, origin + right * BridgeWidth], height, flat, shoulder);
        int raw = (int)Math.Round(height / Units.HeightScale);
        int sx = (int)Math.Floor(origin.X / Units.SampleSize), sz = (int)Math.Floor(origin.Y / Units.SampleSize);
        for (int dz = 0; dz <= 1; dz++) for (int dx = 0; dx <= 1; dx++) rec.Set(sx + dx, sz + dz, raw);
    }

    private static void Conform(IMapDocument doc, HeightRecorder rec, Vector2[] line, float height, float flat, float shoulder) =>
        RoadTerrain.Conform(doc, rec, line, [height, height], new RoadTerrain.Settings(flat, shoulder));
}
