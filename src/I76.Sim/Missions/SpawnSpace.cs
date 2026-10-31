using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// Where a car can be put down: clear of the live cars, on level ground, outside the structures. A melee's spawn
/// points (<see cref="MeleeGame"/>) and co-op's formations and respawns (<see cref="CoopGroup"/>) share it.
/// </summary>
internal static class SpawnSpace
{
    /// <summary>A car's footprint radius, for spacing and clearance.</summary>
    internal static float Radius(Vehicle v) => System.MathF.Max(v.P.Wheelbase, v.P.Track) * 0.75f + 1f;

    /// <summary>A spawn point's position with its height the ground's, where a car placed there stands.</summary>
    internal static System.Numerics.Vector3 OnGround(SimWorld world, Double3 p) => new((float)p.X, world.Ground.Query(p.X, p.Y + 2, p.Z).Height, (float)p.Z);

    internal static bool Live(Vehicle v) => v.Health is null || !Damage.DamageModel.IsDead(v) || !v.Wreck;

    /// <summary>The distance from a point to the edge of the nearest live car (not <paramref name="self"/>); large with none.</summary>
    internal static float Room(SimWorld world, System.Numerics.Vector3 at, Vehicle? self)
    {
        float room = float.MaxValue;
        foreach (var v in world.Vehicles)
        {
            if (v == self || !Live(v)) continue;
            // on the ground plane; a car a storey above or below (a bridge, a ledge) does not block
            var d = v.Pos.ToVector3() - at;
            if (System.MathF.Abs(d.Y) > 4f) continue;
            room = System.MathF.Min(room, System.MathF.Sqrt(d.X * d.X + d.Z * d.Z) - Radius(v));
        }
        return room;
    }

    /// <summary>Whether a car of this radius could stand at <paramref name="at"/>: no live car's body within a car's length of it.</summary>
    internal static bool Clear(SimWorld world, System.Numerics.Vector3 at, float radius, Vehicle? self)
        => Room(world, at, self) >= radius + 0.5f;

    /// <summary>
    /// A clear spot for a car when its spawn point is occupied: rings of offsets along the point's right and forward
    /// axes (a car and a half apart), the nearest first, on ground within <see cref="SpreadRise"/> of the point's
    /// height and no steeper than a ramp between (no cliff edges), outside any structure, and clear of every live car.
    /// Null when there is none within <see cref="SpreadRings"/> rings.
    /// </summary>
    internal static Double3? SpreadFrom(SimWorld world, Rows rot, Double3 pos, Vehicle v)
    {
        float r = Radius(v), step = 2 * r + 1f;
        float h0 = world.Ground.Query(pos.X, pos.Y + 2, pos.Z).Height;
        var right = new System.Numerics.Vector3(rot.Right.X, 0, rot.Right.Z);
        var fwd = new System.Numerics.Vector3(rot.Forward.X, 0, rot.Forward.Z);
        if (right.LengthSquared() < 1e-6f || fwd.LengthSquared() < 1e-6f) { right = System.Numerics.Vector3.UnitX; fwd = System.Numerics.Vector3.UnitZ; }
        right = System.Numerics.Vector3.Normalize(right);
        fwd = System.Numerics.Vector3.Normalize(fwd);
        var centre = pos.ToVector3();
        for (int ring = 1; ring <= SpreadRings; ring++)
        {
            // the offsets of this ring (Chebyshev distance ring), nearest first, behind the point before ahead of it
            var cells = new List<(int I, int J)>();
            for (int i = -ring; i <= ring; i++)
                for (int j = -ring; j <= ring; j++)
                    if (System.Math.Max(System.Math.Abs(i), System.Math.Abs(j)) == ring) cells.Add((i, j));
            cells.Sort((a, b) =>
            {
                int c = (a.I * a.I + a.J * a.J).CompareTo(b.I * b.I + b.J * b.J);
                return c != 0 ? c : a.J != b.J ? a.J.CompareTo(b.J) : a.I.CompareTo(b.I);
            });
            foreach (var (i, j) in cells)
            {
                var at = centre + right * (i * step) + fwd * (j * step);
                if (!Standable(world, centre, at, h0, r, v)) continue;
                var on = OnGround(world, new Double3(at.X, h0, at.Z));
                if (!Clear(world, on, r, v)) continue;
                return new Double3(at.X, h0, at.Z);
            }
        }
        return null;
    }

    /// <summary>Ground rise allowed from the spawn point to a spread spot, metres, and the steepest grade of the way (rise over run).</summary>
    internal const float SpreadRise = 1.2f, SpreadGrade = 0.5f;
    internal const int SpreadRings = 6;

    internal static bool Standable(SimWorld world, System.Numerics.Vector3 from, System.Numerics.Vector3 at, float h0, float r, Vehicle v)
    {
        var g = world.Ground;
        float hPrev = h0, len = (at - from).Length();
        int n = System.Math.Max(1, (int)System.MathF.Ceiling(len / 1.5f));
        for (int k = 1; k <= n; k++)
        {
            var q = from + (at - from) * (k / (float)n);
            if (g.Impassable(q.X, q.Z)) return false;
            float h = g.Query(q.X, hPrev + 2, q.Z).Height;
            if (System.MathF.Abs(h - hPrev) > SpreadGrade * (len / n) + 0.15f || System.MathF.Abs(h - h0) > SpreadRise) return false;
            hPrev = h;
        }
        // the car's footprint: its corners on level ground too
        foreach (var (dx, dz) in new[] { (r, 0f), (-r, 0f), (0f, r), (0f, -r) })
        {
            float h = g.Query(at.X + dx, hPrev + 2, at.Z + dz).Height;
            if (System.MathF.Abs(h - hPrev) > SpreadRise * 0.7f) return false;
        }
        return !InsideStructure(world, new System.Numerics.Vector3(at.X, hPrev, at.Z), r);
    }

    /// <summary>
    /// Whether a car of radius <paramref name="r"/> at <paramref name="at"/> (on the ground) would stand in a solid
    /// placement: a node box (grown by the radius on the ground plane), a type-4 sphere, or the footprint of a mesh.
    /// Conservative: a spot under a bridge deck counts as inside. A ground without statics has none.
    /// </summary>
    internal static bool InsideStructure(SimWorld world, System.Numerics.Vector3 at, float r)
    {
        if (world.Ground is not Collision.IHasStatics hs) return false;
        foreach (var o in hs.World.Objects)
        {
            if (!o.Solid) continue;
            float dx = at.X - o.Centre.X, dz = at.Z - o.Centre.Z, reach = System.MathF.Max(o.Radius, o.BoundRadius) + r;
            if (dx * dx + dz * dz > reach * reach) continue;
            if (o.SphereRadius > 0 && dx * dx + dz * dz < (o.SphereRadius + r) * (o.SphereRadius + r)) return true;
            foreach (var b in o.Boxes)
            {
                var l = b.Rot.ToLocal(at - b.Origin);
                // the box's footprint grown by the radius sideways, the car's height (2.5 m) above the ground
                if (l.X >= b.Min.X - r && l.X <= b.Max.X + r && l.Z >= b.Min.Z - r && l.Z <= b.Max.Z + r && at.Y + 2.5f >= b.Origin.Y + b.Min.Y && at.Y <= b.Origin.Y + b.Max.Y) return true;
            }
            foreach (var poly in o.Polys.Concat(o.Surfaces))
            {
                if (poly.V.Length == 0) continue;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var q in poly.V)
                {
                    (minX, maxX) = (System.MathF.Min(minX, q.X), System.MathF.Max(maxX, q.X));
                    (minZ, maxZ) = (System.MathF.Min(minZ, q.Z), System.MathF.Max(maxZ, q.Z));
                    (minY, maxY) = (System.MathF.Min(minY, q.Y), System.MathF.Max(maxY, q.Y));
                }
                if (at.X >= minX - r && at.X <= maxX + r && at.Z >= minZ - r && at.Z <= maxZ + r && at.Y + 2.5f >= minY && at.Y <= maxY) return true;
            }
        }
        return false;
    }


    /// <summary>
    /// Co-op's formation (docs/multiplayer.md "Co-op"): slots in the anchor car's frame, in steps of a car's clearance
    /// (2r + 1, as the spawn spread), about the nearest first: straight behind it, behind it to either side, beside it,
    /// a second column out, then further back. The first slot that fits and keeps out of the other cars' way is a
    /// teammate's place.
    /// </summary>
    private static readonly (int Right, int Ahead)[] Formation =
        [(0, -1), (0, -2), (1, -1), (-1, -1), (1, -2), (-1, -2), (0, -3), (1, -3), (-1, -3), (1, 0), (-1, 0),
         (2, -1), (-2, -1), (2, -2), (-2, -2), (2, 0), (-2, 0), (0, -4), (2, -3), (-2, -3), (1, -4), (-1, -4),
         (0, -5), (2, -4), (-2, -4), (0, -6), (1, -5), (-1, -5)];

    /// <summary>
    /// How near (on the ground plane) a formation slot may come to where another car is heading, and to a leg of the
    /// script's paths, metres. A car's way is wider than its heading: it swerves round the cars in it (the lead's
    /// among them) by a lane or so.
    /// </summary>
    internal const float CarLane = 8f, PathLane = 6f;
    /// <summary>How far ahead of a car its way is taken to run, and how near the anchor a car (or a script path) must be to count, metres.</summary>
    internal const float Reach = 200f, Near = 400f;

    /// <summary>
    /// Where the other cars are about to drive, as segments on the ground plane (x, z) with the half-width kept clear of
    /// them: each live car that is not a human's (nor a gun emplacement) within <see cref="Near"/> of the anchor, from
    /// where it is along the way it moves (its heading when still) for <see cref="Reach"/> (<see cref="CarLane"/>); and
    /// the legs of the script's paths near it (the nav points its <c>goto</c>/<c>follow</c>/<c>teleport</c> use; a
    /// single point as itself; <see cref="PathLane"/>).
    /// </summary>
    internal static List<(System.Numerics.Vector2 A, System.Numerics.Vector2 B, float Lane)> Ways(SimWorld world, Vehicle anchor, IReadOnlyList<IReadOnlyList<System.Numerics.Vector3>>? paths)
    {
        var o = new System.Numerics.Vector2((float)anchor.Pos.X, (float)anchor.Pos.Z);
        var ways = new List<(System.Numerics.Vector2, System.Numerics.Vector2, float)>();
        foreach (var c in world.Vehicles)
        {
            if (world.IsHuman(c) || c.IsEmplacement || !Live(c)) continue;
            var at = new System.Numerics.Vector2((float)c.Pos.X, (float)c.Pos.Z);
            if (System.Numerics.Vector2.Distance(at, o) > Near) continue;
            var dir = new System.Numerics.Vector2(c.Vel.X, c.Vel.Z);
            if (dir.LengthSquared() < 1f) dir = new System.Numerics.Vector2(c.Rot.Forward.X, c.Rot.Forward.Z);
            if (dir.LengthSquared() < 1e-6f) { ways.Add((at, at, CarLane)); continue; }
            ways.Add((at, at + System.Numerics.Vector2.Normalize(dir) * Reach, CarLane));
        }
        if (paths is not null)
            foreach (var p in paths)
                for (int k = 0; k < p.Count; k++)
                {
                    var a = new System.Numerics.Vector2(p[k].X, p[k].Z);
                    var b = k + 1 < p.Count ? new System.Numerics.Vector2(p[k + 1].X, p[k + 1].Z) : a;
                    if (k + 1 == p.Count && k > 0) break;   // the last point is the previous leg's end
                    if (SegDist(o, a, b) <= Near) ways.Add((a, b, PathLane));
                }
        return ways;
    }

    private static float SegDist(System.Numerics.Vector2 p, System.Numerics.Vector2 a, System.Numerics.Vector2 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-9f ? 0 : System.Math.Clamp(System.Numerics.Vector2.Dot(p - a, ab) / l2, 0f, 1f);
        return System.Numerics.Vector2.Distance(p, a + ab * t);
    }

    /// <summary>How far a spot is out of the ways: the least of its distance to each less that way's lane (≥ 0 is out of all of them; large with none).</summary>
    internal static float Wayside(List<(System.Numerics.Vector2 A, System.Numerics.Vector2 B, float Lane)> ways, System.Numerics.Vector3 at)
    {
        var p = new System.Numerics.Vector2(at.X, at.Z);
        float d = float.MaxValue;
        foreach (var (a, b, lane) in ways) d = System.MathF.Min(d, SegDist(p, a, b) - lane);
        return d;
    }

    /// <summary>
    /// Where <paramref name="v"/> goes beside <paramref name="anchor"/>: the first formation slot on standable ground
    /// (as a spread spawn's: level, no cliff between, outside the structures), clear of every live car and out of the
    /// other cars' ways and the script's <paramref name="paths"/> (<see cref="Ways"/>); with none out of them, the
    /// standable clear slot least in them; else the spawn spread's rings around the anchor,
    /// else straight behind it. Its height is the ground's there.
    /// </summary>
    internal static Double3 Beside(SimWorld world, Vehicle anchor, Vehicle v, IReadOnlyList<IReadOnlyList<System.Numerics.Vector3>>? paths = null)
    {
        float r = System.MathF.Max(Radius(v), Radius(anchor)), step = 2 * r + 1f;
        var a = anchor.Pos;
        float h0 = world.Ground.Query(a.X, a.Y + 2, a.Z).Height;
        var right = new System.Numerics.Vector3(anchor.Rot.Right.X, 0, anchor.Rot.Right.Z);
        var fwd = new System.Numerics.Vector3(anchor.Rot.Forward.X, 0, anchor.Rot.Forward.Z);
        if (right.LengthSquared() < 1e-6f || fwd.LengthSquared() < 1e-6f) { right = System.Numerics.Vector3.UnitX; fwd = System.Numerics.Vector3.UnitZ; }
        right = System.Numerics.Vector3.Normalize(right);
        fwd = System.Numerics.Vector3.Normalize(fwd);
        var centre = new System.Numerics.Vector3((float)a.X, h0, (float)a.Z);
        var ways = Ways(world, anchor, paths);
        System.Numerics.Vector3? best = null;
        float bestRoom = float.MinValue;
        foreach (var (i, j) in Formation)
        {
            var at = centre + right * (i * step) + fwd * (j * step);
            if (!Standable(world, centre, at, h0, r, v)) continue;
            var on = OnGround(world, new Double3(at.X, h0, at.Z));
            if (!Clear(world, on, r, v)) continue;
            float room = Wayside(ways, on);
            if (room >= 0) return new Double3(on.X, on.Y, on.Z);
            if (room > bestRoom) { bestRoom = room; best = on; }
        }
        if (best is { } b) return new Double3(b.X, b.Y, b.Z);
        var spot = SpreadFrom(world, anchor.Rot, a, v) ?? new Double3(centre.X - fwd.X * 2 * step, h0, centre.Z - fwd.Z * 2 * step);
        spot.Y = world.Ground.Query(spot.X, spot.Y + 2, spot.Z).Height;
        return spot;
    }
}
