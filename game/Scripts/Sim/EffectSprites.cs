using Godot;
using I76.Formats.Bwd2;
using I76.Formats.Imaging;

namespace I76.Game.Sim;

/// <summary>
/// An effect's look (.xdf): its XGEO node is a single quad GEO whose face
/// texture is a .tmt listing animation frames, played at 10 per second for
/// XDFC's frame count. The quad stands on the effect's origin (the WP mortar's burst from 0 to 5 m) with v = 0 at its
/// foot in every stock effect but one, so the frame's first row is drawn at the bottom: a burst's flat edge lies on the
/// ground, not across its top.
/// </summary>
public sealed class EffectSprites(World.MissionBuilder builder)
{
    /// <param name="FlipV">the frame's first row at the bottom (the quad's v grows upward)</param>
    /// <param name="Lift">the quad's centre above the effect's origin, in metres</param>
    public sealed record Look(float Size, Texture2D[] Frames, bool FlipV = false, float Lift = 0)
    {
        /// <summary>A billboard of the look's first frame at <paramref name="at"/>: sized, lifted and turned as the quad.</summary>
        public Sprite3D Sprite(Vector3 at)
        {
            var tex = Frames[0];
            float px = Size / MathF.Max(1, tex.GetWidth());
            return new Sprite3D
            {
                Texture = tex, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, DoubleSided = true,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard, PixelSize = px, FlipV = FlipV, Offset = new Vector2(0, Lift / px),
                Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
        }
    }
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
                if (builder.Resolver?.FindByStem(n) is { } m && builder.Textures?.Get(m) is { } t) list.Add(t.Drawn);
            var (flip, lift) = I76.Formats.Geo.EffectQuad.Of(geo);
            if (list.Count > 0) { l = new Look(size, [.. list], flip, lift); break; }
        }
        return _cache[key] = l;
    }
}
