using System.Numerics;

namespace I76.Editor.Objects;

/// <summary>Levelling the ground under a footprint with the terrain brush, for "flatten ground" when a building is placed.</summary>
public static class Leveling
{
    /// <summary>The soft skirt around the flat core (m).</summary>
    public const float Skirt = 14f;

    /// <summary>
    /// Levels the ground under <paramref name="f"/> to <paramref name="height"/>, else the mean height of its corners and centre: a flat core a couple of metres wider than
    /// the footprint and a soft skirt, done as the Level brush does (<see cref="BrushStroke"/>) with stamps along the footprint's long
    /// axis, as one command (the two passes' changes joined). Null if the ground already lies as flat as that. The document is changed now.
    /// </summary>
    public static StrokeCommand? Flatten(IMapDocument doc, Footprint f, Func<float, float, float> ground, float? height = null)
    {
        var pts = f.Corners().Append(f.Centre).ToArray();
        float target = height ?? pts.Average(p => ground(p.X, p.Y));
        var half = f.HalfSize;
        bool alongZ = half.Y >= half.X;
        float shortHalf = MathF.Min(half.X, half.Y), longHalf = MathF.Max(half.X, half.Y);
        var axis = alongZ ? f.AxisZ : f.AxisX;
        float core = shortHalf + 2f;
        var stamps = new List<Vector2>();
        if (longHalf - shortHalf < 1) stamps.Add(f.Centre);
        else
        {
            int n = Math.Max(1, (int)MathF.Ceiling(longHalf * 2 / (core * 0.8f)));
            for (int i = 0; i <= n; i++) stamps.Add(f.Centre + axis * (-(longHalf - shortHalf) + 2 * (longHalf - shortHalf) * i / n));
        }
        float radius = Math.Clamp(core + shortHalf * 0.4f, BrushSettings.MinRadius, BrushSettings.MaxRadius);
        var skirt = new BrushStroke(doc, new BrushSettings { Kind = BrushKind.Level, Falloff = Falloff.Smooth, Radius = Math.Clamp(radius + Skirt, BrushSettings.MinRadius, BrushSettings.MaxRadius), Strength = 1f, TargetHeight = target });
        foreach (var p in stamps) skirt.Stamp(p.X / Units.SampleSize, p.Y / Units.SampleSize, 0.05);
        var flat = new BrushStroke(doc, new BrushSettings { Kind = BrushKind.Level, Falloff = Falloff.Plateau, Radius = radius, Strength = 1f, TargetHeight = target });
        foreach (var p in stamps) flat.Stamp(p.X / Units.SampleSize, p.Y / Units.SampleSize, 1.0);
        return Join("Level ground", skirt.End(), flat.End());
    }

    /// <summary>Two strokes (the second made on top of the first) as one command: each sample's first before and last after.</summary>
    public static StrokeCommand? Join(string name, params StrokeCommand?[] strokes)
    {
        var tiles = new Dictionary<int, TileDelta>();
        foreach (var cmd in strokes)
        {
            if (cmd is null) continue;
            foreach (var (tile, d) in cmd.Tiles)
            {
                if (!tiles.TryGetValue(tile, out var have)) { tiles[tile] = d; continue; }
                var map = new Dictionary<int, (ushort Before, ushort After)>();
                for (int i = 0; i < have.Offsets.Length; i++) map[have.Offsets[i]] = (have.Before[i], have.After[i]);
                for (int i = 0; i < d.Offsets.Length; i++) map[d.Offsets[i]] = map.TryGetValue(d.Offsets[i], out var prev) ? (prev.Before, d.After[i]) : (d.Before[i], d.After[i]);
                var offs = map.Keys.OrderBy(k => k).ToArray();
                tiles[tile] = new TileDelta(offs, [.. offs.Select(o => map[o].Before)], [.. offs.Select(o => map[o].After)]);
            }
        }
        return tiles.Count == 0 ? null : new StrokeCommand(name, tiles);
    }
}
