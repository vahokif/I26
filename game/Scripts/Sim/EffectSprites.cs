using Godot;
using I76.Formats.Bwd2;
using I76.Formats.Imaging;

namespace I76.Game.Sim;

/// <summary>
/// An effect's look (.xdf): its XGEO node is a single quad GEO whose face
/// texture is a .tmt listing animation frames, played at 10 per second for
/// XDFC's frame count.
/// </summary>
public sealed class EffectSprites(World.MissionBuilder builder)
{
    public sealed record Look(float Size, Texture2D[] Frames);
    private readonly Dictionary<string, Look?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Look? Get(EffectDef e) => Get(e.File, e.Geometry.Select(n => n.Name), (int)e.Frames);

    public Look? Get(string key, IEnumerable<string> nodes, int frames)
    {
        if (_cache.TryGetValue(key, out var l)) return l;
        l = null;
        foreach (var node in nodes)
        {
            if (builder.Assembler?.Geo(node) is not { } geo || geo.Vertices.Length == 0) continue;
            var min = geo.Vertices.Aggregate(System.Numerics.Vector3.Min); var max = geo.Vertices.Aggregate(System.Numerics.Vector3.Max);
            var ext = max - min;
            float size = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            var tex = geo.Faces.Select(f => f.Texture).FirstOrDefault(t => t.Length > 0);
            if (tex is null) continue;
            var names = tex.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase) && builder.Vfs.TryRead(tex) is { } td
                ? new TmtMaterial(td).Names : [System.IO.Path.GetFileNameWithoutExtension(tex)];
            var list = new List<Texture2D>();
            foreach (var n in names.Take(frames > 0 ? frames : names.Length))
                if (builder.Resolver?.FindByStem(n) is { } m && builder.Textures?.Get(m) is { } t) list.Add(t.Texture);
            if (list.Count > 0) { l = new Look(size, [.. list]); break; }
        }
        return _cache[key] = l;
    }
}
