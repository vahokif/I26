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
    public sealed record Entry(ImageTexture Texture, bool HasKey, int Width, int Height, byte[] Rgba);

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
        e = I76.Formats.Vfs.SessionStore.GetOrAdd("tex|" + _scope + member, () => Make(member), x => x is null ? 0 : x.Rgba.LongLength * 5 / 2);
        _cache[member] = e;
        return e;
    }

    private Entry? Make(string member)
    {
        Entry? e = null;
        if (resolver.Load(member) is { } t)
        {
            var rgba = TextureReader.ToRgba(t, palette);
            bool key = false;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] == 0) { key = true; break; }
            if (key) BleedUnderKey(rgba, t.Width, t.Height);
            var img = Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, rgba);
            img.GenerateMipmaps();
            e = new Entry(ImageTexture.CreateFromImage(img), key, t.Width, t.Height, rgba);
        }
        return e;
    }

    /// <summary>
    /// Give keyed texels the colour of an opaque neighbour, so filtering and
    /// mipmaps do not pull the key colour into the edges. Alpha is untouched.
    /// </summary>
    private static void BleedUnderKey(byte[] p, int w, int h)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var src = (byte[])p.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (src[i + 3] != 0 || (pass > 0 && (src[i] | src[i + 1] | src[i + 2]) != 0)) continue;
                    int n = 0, r = 0, g = 0, b = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = (yy * w + xx) * 4;
                            if (src[j + 3] == 0 && (pass == 0 || (src[j] | src[j + 1] | src[j + 2]) == 0)) continue;
                            r += src[j]; g += src[j + 1]; b += src[j + 2]; n++;
                        }
                    if (n == 0) { if (pass == 0) { p[i] = p[i + 1] = p[i + 2] = 0; } continue; }
                    p[i] = (byte)(r / n); p[i + 1] = (byte)(g / n); p[i + 2] = (byte)(b / n);
                }
        }
    }
}
