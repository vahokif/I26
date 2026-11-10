using Godot;
using I76.Formats.Assembly;
using I76.Formats.Imaging;

namespace I76.Game.Assets;

/// <summary>
/// Texture members to Godot textures, with the mission palette for 8-bit
/// data. Index 255 always becomes alpha 0; whether a material honours the
/// key is the material's decision (the key is per draw type).
/// In the modern look a model's face draws a member's upscale from the player's pack (<see cref="HdTextures"/>) where there is
/// one, through a texture of its own (<see cref="Entry.Drawn"/>) whose picture a look change swaps (<see cref="SetHdSteps"/>);
/// everything else (<see cref="Entry.Texture"/>, <see cref="Entry.Rgba"/>, <see cref="GetRgba"/>) stays the original's.
/// </summary>
public sealed class TextureFactory(TextureResolver resolver, Palette? palette)
{
    /// <summary>
    /// A texture member. <see cref="Texture"/> and <see cref="Rgba"/> are as a keyed draw shows it: the keyed texels' colour
    /// bled from their neighbours, so filtering and mipmaps pull no key colour into the edges. <see cref="Opaque"/> is as an
    /// unkeyed (opaque) draw shows it: the key is only a colour there, and the original draws index 255 in its palette
    /// colour, black past the end of the palette (the bus's stripes and lettering, most of the black limo's paint).
    /// Both, and the size, are the original's whatever a pack holds: what the material analyses read is on its texel grid.
    /// </summary>
    public sealed record Entry(ImageTexture Texture, bool HasKey, int Width, int Height, byte[] Rgba)
    {
        internal Lazy<ImageTexture>? Unkeyed { get; init; }
        /// <summary>The pack's upscale (the original's picture in the 1997 look), made when a face first draws it; null when the pack has none.</summary>
        internal Lazy<ImageTexture?>? Upscale { get; init; }

        /// <summary>The texture as a model's keyed or plain face draws it: the pack's upscale where there is one, else <see cref="Texture"/>.</summary>
        public ImageTexture Drawn => Upscale?.Value ?? Texture;

        /// <summary>The texture as an opaque face draws it: <see cref="Drawn"/> itself when it has no key.</summary>
        public ImageTexture Opaque => Unkeyed?.Value ?? Drawn;
    }

    /// <summary>Whether upscales are drawn: the modern look's (<see cref="SetHdSteps"/>), while HD Textures is on. Set before the first texture.</summary>
    public bool Hd { get; set; } = true;
    private bool Serving => Hd && HdTextures.Enabled;
    /// <summary>What the upscales made so far show: the pack's pictures, or the originals'.</summary>
    private bool _shown;
    private readonly List<(string Member, ImageTexture Texture, byte[] Rgba, int Width, int Height)> _upscaled = [];

    private readonly Dictionary<string, I76.Formats.Assembly.RoadRaster.Tex?> _rgba = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a stored texture depends on besides its name: the files it was read from (the install and the map's own folder) and the palette.</summary>
    private readonly string _scope = $"{resolver.Vfs.Install.Root}|{resolver.Vfs.Install.MapDir}|{(palette is null ? "-" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(palette.Rgb)))}|";

    /// <summary>A texture's RGBA (index 255 keyed to alpha 0), for CPU-side painting.</summary>
    public I76.Formats.Assembly.RoadRaster.Tex? GetRgba(string member)
    {
        if (_rgba.TryGetValue(member, out var r)) return r;
        r = I76.Formats.Vfs.SessionStore.GetOrAdd("rgba|" + _scope + member,
            () => resolver.Load(member) is { } t ? new I76.Formats.Assembly.RoadRaster.Tex(t.Width, t.Height, TextureReader.ToRgba(t, palette)) : null,
            x => x?.Rgba.Length ?? 0);
        return _rgba[member] = r;
    }

    private readonly Dictionary<string, Entry?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Entry? Get(string member)
    {
        if (_cache.TryGetValue(member, out var e)) return e;
        // with a pack the entries are this factory's own: a look change swaps their pictures
        e = HdTextures.Installed ? Make(member) : I76.Formats.Vfs.SessionStore.GetOrAdd("tex|" + _scope + member, () => Make(member), x => x is null ? 0 : x.Rgba.LongLength * (x.HasKey ? 7 : 5) / 2);   // a keyed one also keeps its unkeyed colours
        _cache[member] = e;
        return e;
    }

    private Entry? Make(string member)
    {
        Entry? e = null;
        if (resolver.Load(member) is { } t)
        {
            var plain = TextureReader.ToRgba(t, palette);
            bool key = TextureReader.HasKey(plain);
            var rgba = plain;
            if (key) { rgba = (byte[])plain.Clone(); TextureReader.BleedUnderKey(rgba, t.Width, t.Height); }
            // the opaque faces' copy is made when one first asks for it (most keyed textures are only drawn keyed)
            Lazy<ImageTexture>? unkeyed = key ? new(() => Upload(plain, t.Width, t.Height)) : null;
            // the upscale only where a model's face draws the texture (not the ground, the sky, effects or sprites)
            Lazy<ImageTexture?>? upscale = HdTextures.Has(member) ? new(() => UpscaleOf(member, rgba, t.Width, t.Height)) : null;
            e = new Entry(Upload(rgba, t.Width, t.Height), key, t.Width, t.Height, rgba) { Unkeyed = unkeyed, Upscale = upscale };
        }
        return e;
    }

    /// <summary>
    /// The member's upscale texture, or null without one: the pack's picture while upscales are drawn, else the original's (the
    /// pack's is read when the look changes). Null too for an upscale that does not load: the original is drawn.
    /// </summary>
    private ImageTexture? UpscaleOf(string member, byte[] rgba, int w, int h)
    {
        if (_upscaled.Count == 0) _shown = Serving;
        var img = _shown ? HdTextures.Load(member, rgba, w, h) : Picture(rgba, w, h);
        if (img is null) return null;
        var tex = ImageTexture.CreateFromImage(img);
        _upscaled.Add((member, tex, rgba, w, h));
        return tex;
    }

    /// <summary>
    /// The look's pictures on the upscales made so far: the pack's in the modern look while HD Textures is on (<paramref name="modern"/>),
    /// the originals' otherwise. As steps, a null after every 10 ms or so (a mission loading under its briefing).
    /// </summary>
    public IEnumerable<object?> SetHdSteps(bool modern)
    {
        Hd = modern;
        if (Serving == _shown) yield break;
        _shown = Serving;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (member, tex, rgba, w, h) in _upscaled)
        {
            tex.SetImage((_shown ? HdTextures.Load(member, rgba, w, h) : null) ?? Picture(rgba, w, h));
            if (sw.Elapsed.TotalMilliseconds > 10) { yield return null; sw.Restart(); }
        }
    }

    private static Image Picture(byte[] rgba, int w, int h)
    {
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        img.GenerateMipmaps();
        return img;
    }

    private static ImageTexture Upload(byte[] rgba, int w, int h) => ImageTexture.CreateFromImage(Picture(rgba, w, h));
}
