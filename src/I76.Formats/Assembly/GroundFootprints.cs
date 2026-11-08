using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// An upright box's outline on the ground (game-frame X and Z, metres): its centre, its first axis (any length: the user
/// normalises it), half its extent along that axis and across it.
/// </summary>
public readonly record struct GroundBox(Vector2 Centre, Vector2 AxisU, float HalfU, float HalfV)
{
    /// <summary>Whether the box, grown by <paramref name="margin"/> on every side, overlaps the rectangle [min, max] (separating axes).</summary>
    public bool Overlaps(Vector2 min, Vector2 max, float margin)
    {
        var u = AxisU.LengthSquared() < 1e-8f ? Vector2.UnitX : Vector2.Normalize(AxisU);
        var v = new Vector2(-u.Y, u.X);
        float hu = HalfU + margin, hv = HalfV + margin;
        // the box's own reach along x and z against the rectangle's
        float rx = MathF.Abs(u.X) * hu + MathF.Abs(v.X) * hv, rz = MathF.Abs(u.Y) * hu + MathF.Abs(v.Y) * hv;
        if (Centre.X + rx < min.X || Centre.X - rx > max.X || Centre.Y + rz < min.Y || Centre.Y - rz > max.Y) return false;
        // the rectangle's reach along the box's axes
        var c = (min + max) * 0.5f - Centre;
        var h = (max - min) * 0.5f;
        if (MathF.Abs(Vector2.Dot(c, u)) > hu + MathF.Abs(u.X) * h.X + MathF.Abs(u.Y) * h.Y) return false;
        if (MathF.Abs(Vector2.Dot(c, v)) > hv + MathF.Abs(v.X) * h.X + MathF.Abs(v.Y) * h.Y) return false;
        return true;
    }
}

/// <summary>Where placed objects stand on the ground: the outlines of their parts that reach down to it.</summary>
public static class GroundFootprints
{
    /// <summary>A part whose lowest corner is more than this above the ground under its middle (a bridge's deck over a riverbed, a gate's sign) does not stand on it, metres.</summary>
    public const float Reach = 2.5f;

    /// <summary>
    /// The ground-standing parts of a placed model at <paramref name="pos"/>: each part's bounds as an upright box, its outline seen
    /// from above (a tilted part's axes foreshortened, at least 0.3 of their length).
    /// </summary>
    public static IEnumerable<GroundBox> Parts(MissionWorld world, Placement p, Vector3 pos, AssembledModel model)
    {
        var place = Affine.FromEngine(p.Object.Rotation, pos);
        var u = place.Rotate(Vector3.UnitX); var w = place.Rotate(Vector3.UnitZ);
        float lu = new Vector2(u.X, u.Z).Length(), lw = new Vector2(w.X, w.Z).Length();
        foreach (var part in model.Parts)
        {
            if (part.Mesh.Vertices.Length == 0) continue;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in part.Mesh.Vertices) { var q = part.ToModel.Apply(v); mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q); }
            var centre = place.Apply((mn + mx) * 0.5f);
            // the part's lowest corner in the world, against the ground under its middle
            float lowest = float.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) == 0 ? mn.X : mx.X, (k & 2) == 0 ? mn.Y : mx.Y, (k & 4) == 0 ? mn.Z : mx.Z);
                lowest = MathF.Min(lowest, place.Apply(corner).Y);
            }
            if (lowest > world.BilinearHeight(centre.X, centre.Z) + Reach) continue;
            yield return new GroundBox(new Vector2(centre.X, centre.Z), new Vector2(u.X, u.Z),
                (mx.X - mn.X) * 0.5f * MathF.Max(lu, 0.3f), (mx.Z - mn.Z) * 0.5f * MathF.Max(lw, 0.3f));
        }
    }

    /// <summary>
    /// Every placed object's ground-standing parts (<see cref="Parts"/>) and a metre square round where it is put, the line objects' too; a
    /// model that does not assemble stands on a 4 m square. Cars are left out (where they start is ground a car reaches), and so is anything with no model.
    /// </summary>
    public static List<GroundBox> Of(MissionWorld world, ModelAssembler assembler)
    {
        var boxes = new List<GroundBox>();
        var models = new Dictionary<string, AssembledModel?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in world.Placements.Concat(world.LinePlacements))
        {
            if (p.Object.ObjectType == 1 || p.Definition is null) continue;
            var pos = world.GroundedPosition(p);
            if (!models.TryGetValue(p.Definition, out var model)) models[p.Definition] = model = assembler.Assemble(p.Definition);
            if (model is null) { boxes.Add(new GroundBox(new Vector2(pos.X, pos.Z), Vector2.UnitX, 4, 4)); continue; }
            boxes.AddRange(Parts(world, p, pos, model));
            // and where it is put on the ground (a bridge's deck, all of it over a gap, stands at its origin's ground height)
            boxes.Add(new GroundBox(new Vector2(pos.X, pos.Z), Vector2.UnitX, 0.5f, 0.5f));
        }
        return boxes;
    }
}
