using Godot;
using I76.Formats.Assembly;
using I76.Formats.Imaging;

namespace I76.Game.Assets;

/// <summary>
/// Texture members to Godot textures, with the mission palette for 8-bit
/// data. Index 255 always becomes alpha 0; whether a material honours the
/// key is the material's decision (the key is per draw type).
/// </summary>
public sealed class TextureFactory(TextureResolver resolver, Palette? palette)
{
    /// <summary>
    /// A texture member. <see cref="Texture"/> and <see cref="Rgba"/> are as a keyed draw shows it: the keyed texels' colour
    /// bled from their neighbours, so filtering and mipmaps pull no key colour into the edges. <see cref="Opaque"/> is as an
    /// unkeyed (opaque) draw shows it: the key is only a colour there, and the original draws index 255 in its palette
    /// colour, black past the end of the palette (the bus's stripes and lettering, most of the black limo's paint).
    /// </summary>
    public sealed record Entry(ImageTexture Texture, bool HasKey, int Width, int Height, byte[] Rgba)
    {
        internal Lazy<ImageTexture>? Unkeyed { get; init; }

        /// <summary>The texture as an opaque face draws it: <see cref="Texture"/> itself when it has no key.</summary>
        public ImageTexture Opaque => Unkeyed?.Value ?? Texture;
    }

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
        e = I76.Formats.Vfs.SessionStore.GetOrAdd("tex|" + _scope + member, () => Make(member), x => x is null ? 0 : x.Rgba.LongLength * (x.HasKey ? 7 : 5) / 2);   // a keyed one also keeps its unkeyed colours
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
            e = new Entry(Upload(rgba, t.Width, t.Height), key, t.Width, t.Height, rgba) { Unkeyed = unkeyed };
        }
        return e;
    }

    private static ImageTexture Upload(byte[] rgba, int w, int h)
    {
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }
}
