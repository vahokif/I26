using I76.Formats.Imaging;

namespace I76.Formats.Assembly;

/// <summary>
/// The respray's paint masks of one paint scheme: which texels of the scheme's body textures are its primary
/// paint (red channel) and its secondary paint (green), as weights 0..255, and the mean luma of each paint, which the recolour measures
/// shade against. The scheme is the set of body textures the .vtf gives the car: its primary paint is the fullest hue cluster over them,
/// else the fullest neutral. Computed from the textures alone (no loadout, no variant): a pure function of the scheme, so the same
/// masks come out every time and one set serves every car of the scheme (the caller caches it by <see cref="KeyOf"/>).
/// </summary>
public sealed class PaintMasks
{
    /// <summary>One texture's weights: Rg is W×H×2 bytes (primary, secondary).</summary>
    public sealed record Mask(int Width, int Height, byte[] Rg);

    /// <summary>A texture as RGBA8, index 255 (the key) with alpha 0.</summary>
    public delegate (int Width, int Height, byte[] Rgba)? TextureSource(string member);

    public ResprayPaint.PaintRef Primary { get; }
    public ResprayPaint.PaintRef? Secondary { get; }
    /// <summary>Every texture the masks can be asked for: the scheme's body textures at every damage frame.</summary>
    public IReadOnlySet<string> Members => _members;
    private readonly HashSet<string> _members;
    public bool HasSecondary => Secondary is not null;
    public float PrimaryLuma => Primary.Y;
    public float SecondaryLuma => Secondary?.Y ?? 0.5f;

    private readonly TextureSource _source;
    private readonly Dictionary<string, Mask?> _masks = new(StringComparer.OrdinalIgnoreCase);

    private PaintMasks(ResprayPaint.PaintRef primary, ResprayPaint.PaintRef? secondary, IReadOnlySet<string> members, TextureSource source)
    {
        (Primary, Secondary, _members, _source) = (primary, secondary, new HashSet<string>(members, StringComparer.OrdinalIgnoreCase), source);
    }

    /// <summary>The body textures of a vehicle model, with whether the car draws any of its faces cut out: the car's own parts (node names starting with the object code, not weapon gizmos), at damage frame <paramref name="frame"/>.</summary>
    public static List<(string Member, bool Cutout)> BodyTextures(AssembledModel model, TextureResolver res, int frame = 0)
    {
        var code = model.ObjectCode ?? "";
        var slots = new Dictionary<string, (int Faces, bool Cutout)>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in model.Parts)
        {
            if (!part.NodeName.StartsWith(code, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Texture.Length == 0 || f.Unk_0x20 == 0) continue;
                var s = slots.GetValueOrDefault(f.Texture);
                slots[f.Texture] = (s.Faces + 1, s.Cutout | (f.Unk_0x20 & 4) != 0);
            }
        }
        var o = new List<(string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, s) in slots.OrderByDescending(k => k.Value.Faces).ThenBy(k => k.Key, StringComparer.Ordinal))
            if (res.ResolveFace(slot, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps, frame: frame) is { } m && seen.Add(m))
                o.Add((m, s.Cutout));
        return o;
    }

    /// <summary>Every body texture the scheme can show (all damage frames), sorted: what the masks cover.</summary>
    public static SortedSet<string> MembersOf(AssembledModel model, TextureResolver res)
    {
        var o = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int frame = 0; frame < 4; frame++)
            foreach (var (m, cutout) in BodyTextures(model, res, frame))
                if (!cutout) o.Add(m);
        return o;
    }

    /// <summary>A short name for a scheme: a hash of its body textures' names (the same for every car that draws that scheme, whatever its loadout).</summary>
    public static string KeyOf(IEnumerable<string> members)
    {
        uint h = 2166136261;
        foreach (var m in members) { foreach (char c in m.ToLowerInvariant()) h = (h ^ c) * 16777619; h = (h ^ '|') * 16777619; }
        return $"{h:x8}";
    }

    /// <summary>Reads the scheme's statistics (frame 0's opaque body textures) and settles its primary and secondary paint.</summary>
    public static PaintMasks Compute(AssembledModel model, TextureResolver res, TextureSource source)
    {
        var st = new ResprayPaint.Stats();
        foreach (var (m, cutout) in BodyTextures(model, res))
        {
            if (cutout || source(m) is not { } t) continue;
            st.Add(Rgb(t.Rgba, t.Width * t.Height), t.Width * t.Height);
        }
        var (primary, secondary) = ResprayPaint.Scheme(st);
        return new PaintMasks(primary, secondary, MembersOf(model, res), source);
    }

    /// <summary>The colours alone: index 255 is an ordinary colour on an opaque face (a two-tone's black paint uses it), so the key's alpha is not read.</summary>
    private static byte[] Rgb(byte[] rgba, int n)
    {
        var d = new byte[n * 3];
        for (int i = 0; i < n; i++) { d[i * 3] = rgba[i * 4]; d[i * 3 + 1] = rgba[i * 4 + 1]; d[i * 3 + 2] = rgba[i * 4 + 2]; }
        return d;
    }

    /// <summary>Textures of the same car the masks cover too (its cockpit's), against the same paints.</summary>
    public void AddMembers(IEnumerable<string> members) { lock (_masks) foreach (var m in members) _members.Add(m); }

    /// <summary>The weights of one texture of the scheme (cached); null for a texture that is not one of its body's or does not load.</summary>
    public Mask? MaskOf(string member)
    {
        if (!Members.Contains(member)) return null;
        lock (_masks)
        {
            if (_masks.TryGetValue(member, out var m)) return m;
            return _masks[member] = Make(member);
        }
    }

    private Mask? Make(string member)
    {
        if (_source(member) is not { } t) return null;
        int n = t.Width * t.Height;
        var rgb = Rgb(t.Rgba, n);
        var w1 = ResprayPaint.Mask(rgb, n, Primary);
        var w2 = Secondary is { } s ? ResprayPaint.Mask(rgb, n, s) : null;
        var rg = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            float a = w1[i], b = w2?[i] ?? 0;
            // a texel both would take goes to the stronger
            if (a >= b) b = 0; else a = 0;
            rg[i * 2] = (byte)Math.Clamp(a * 255 + 0.5f, 0, 255);
            rg[i * 2 + 1] = (byte)Math.Clamp(b * 255 + 0.5f, 0, 255);
        }
        return new Mask(t.Width, t.Height, rg);
    }
}
