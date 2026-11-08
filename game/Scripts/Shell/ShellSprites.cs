using System.Text.Json;
using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// Upscaled bank sprites under the frame (the modern look): the popups, which are drawn from sprite banks and
/// would otherwise stay 640x480 pixels over the upscaled backgrounds. None ship with the port: they are read
/// from the same directory as the backgrounds (<see cref="HdAssets"/>), sprites/&lt;bank&gt;_&lt;index&gt;.webp
/// with sprites/sprites.json, and the Nitro Pack's own (its own record and sprite numbers) from nitro/sprites/.
/// The json gives each picture's file, its affine [ax, bx, ay, by] (image = a · sprite pixel + b, continuous
/// coordinates, fitted by tools/fit-shellbg.py) and the sprite's size. A Nitro sprite without its own picture
/// shows the base game's, when the sprite map says it is the same picture and the size agrees. The frame hides
/// the sprite's own pixels where it is drawn (ShellFrame.Blit); the picture is placed where the sprite was blitted.
/// </summary>
public partial class ShellSprites : Control
{
    private sealed record Entry(string Path, float Ax, float Bx, float Ay, float By, int W, int H);

    public required ShellFrame Frame { get; init; }
    public required bool Nitro { get; init; }
    private readonly Dictionary<string, Entry> _base = [], _nitro = [];
    private readonly Dictionary<string, ImageTexture?> _tex = [];
    private int _version = -1;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        Scan("sprites/", _base);
        if (Nitro) Scan(ShellBackdrop.NitroSub + "/sprites/", _nitro);
        Frame.HasHdSprite = s => Find(s) is not null;
    }

    private static void Scan(string dir, Dictionary<string, Entry> into)
    {
        var manifest = dir + "sprites.json";
        if (HdAssets.Source?.Read(manifest) is not { } mbytes) return;
        try
        {
            using var doc = JsonDocument.Parse(mbytes);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("file", out var f) || !p.Value.TryGetProperty("affine", out var a) || a.GetArrayLength() != 4) continue;
                var path = dir + (f.GetString() ?? "");
                if (!HdAssets.Source!.Exists(path)) continue;
                var v = a.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                int w = 0, h = 0;
                if (p.Value.TryGetProperty("size", out var sz) && sz.GetArrayLength() == 2) (w, h) = (sz[0].GetInt32(), sz[1].GetInt32());
                into[p.Name] = new Entry(path, v[0], v[1], v[2], v[3], w, h);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            GD.PushWarning($"shell sprites: {manifest} unreadable ({e.Message})");
        }
    }

    /// <summary>The picture for a sprite, or null.</summary>
    private Entry? Find(Sprite s)
    {
        if (s.Bank == 0 || !HdAssets.Enabled) return null;
        if (!Nitro) return _base.GetValueOrDefault($"{s.Bank}_{s.Index}") is { } b && Fits(b, s) ? b : null;
        if (_nitro.GetValueOrDefault($"{s.Bank}_{s.Index}") is { } n && Fits(n, s)) return n;
        // the base game's picture of the same sprite
        if (NitroShellMap.SpriteMap(0x24) is { } map)
            for (int i = 0; i < map.Length; i++)
                if (map[i] == s.Index && _base.GetValueOrDefault($"36_{i}") is { } e && Fits(e, s)) return e;
        return null;
    }

    private static bool Fits(Entry e, Sprite s) => e.W == 0 || (e.W == s.Width && e.H == s.Height);

    public override void _Process(double delta)
    {
        if (Frame.PlacedVersion == _version) return;
        _version = Frame.PlacedVersion;
        QueueRedraw();
    }

    private ImageTexture? Load(Entry e)
    {
        if (_tex.TryGetValue(e.Path, out var t)) return t;
        var img = HdAssets.LoadImage(e.Path);
        if (img is null || img.IsEmpty()) { GD.PushWarning($"shell sprites: {e.Path} unreadable"); return _tex[e.Path] = null; }
        img.GenerateMipmaps();
        return _tex[e.Path] = ImageTexture.CreateFromImage(img);
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        float s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        var off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * s) / 2;
        foreach (var (sprite, x, y, turned) in Frame.Placed)
        {
            if (Find(sprite) is not { } e || Load(e) is not { } tex) continue;
            // the sprite's rectangle on screen and in texels
            var dest = new Rect2(off + new Vector2(x, y) * s, new Vector2(sprite.Width, sprite.Height) * s);
            var src = new Rect2(e.Bx, e.By, e.Ax * sprite.Width, e.Ay * sprite.Height);
            if (!turned) { DrawTextureRectRegion(tex, dest, src); continue; }
            // the rear weapons: the picture turned half a turn about the box's middle, as the frame draws them
            DrawSetTransform(dest.GetCenter(), Mathf.Pi, Vector2.One);
            DrawTextureRectRegion(tex, new Rect2(-dest.Size / 2, dest.Size), src);
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }
    }
}
