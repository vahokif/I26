using System.Text.Json;
using Godot;

namespace I76.Game.Shell;

/// <summary>
/// Upscaled shell backgrounds under the frame (the modern look). None ship
/// with the port: HdAssets finds them (a folder or the HD Menu Pack zip), as shellbg&lt;record&gt;.webp, .png or .jpg (shellbg0N
/// also, below 10). shellbg.json there may give each its file and an affine
/// [ax, bx, ay, by]: image x = ax·frame x + bx, image y = ay·frame y + by, in
/// continuous coordinates of the 640x480 frame and the image
/// (tools/fit-shellbg.py fits it); without one the image's height is the
/// frame's and its middle the frame's middle. The picture is drawn so that
/// the frame's content lands on its own in it, and, wider than 4:3, fills the
/// window's width beside it. The frame over it is transparent wherever it
/// still shows the plain background.
///
/// The Nitro Pack's own backgrounds (the records that differ from the base
/// game's, or have no base number) are read from the nitro/ folder of the same
/// directory, shellbg&lt;Nitro record&gt;.webp and nitro/shellbg.json, keyed by
/// database.tvf's own numbers; the base files are not reused for them. A Nitro
/// record identical to a base one shows the base game's upscale; a redrawn one
/// (NitroShellMap.Redrawn) without its own shows its original pixels.
///
/// The Joe's Fish Shack pads differ from the Options Menu's (record 6) only
/// in their title, and two in their check boxes: one without its own upscale
/// borrows record 6's, the frame kept opaque where the two originals differ
/// (Shell.Kept), so only the title is the original's pixels.
/// </summary>
public partial class ShellBackdrop : Control
{
    private sealed record Entry(string Path, float Ax, float Bx, float Ay, float By);

    private readonly Dictionary<int, Entry> _entries = [];
    private readonly List<(int Record, ImageTexture Texture)> _loaded = [];
    private const int Keep = 4;
    private Entry? _entry;
    private ImageTexture? _texture;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        Scan("");
    }

    /// <summary>Folder of the Nitro Pack's own pictures, in the HD assets (<see cref="HdAssets"/>).</summary>
    public const string NitroSub = "nitro";

    private void Scan(string dir)
    {
        Scan(dir, 1);
        Scan(NitroSub + "/", -1);
    }

    /// <summary>Reads a folder's pictures: keys are the record numbers times <paramref name="sign"/> (Nitro's own are negative).</summary>
    private void Scan(string dir, int sign)
    {
        if (!HdAssets.Installed) return;
        var affine = new Dictionary<int, (string? File, float[]? A)>();
        var manifest = dir + "shellbg.json";
        if (HdAssets.Source?.Read(manifest) is { } mbytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(mbytes);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out int rec) || p.Value.ValueKind != JsonValueKind.Object) continue;
                    string? file = p.Value.TryGetProperty("file", out var f) ? f.GetString() : null;
                    float[]? a = p.Value.TryGetProperty("affine", out var af) && af.GetArrayLength() == 4
                        ? [.. af.EnumerateArray().Select(v => v.GetSingle())] : null;
                    affine[rec] = (file, a);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                GD.PushWarning($"shell backgrounds: {manifest} unreadable ({e.Message})");
            }
        }
        for (int rec = 1; rec <= (sign > 0 ? 32 : 76); rec++)
        {
            affine.TryGetValue(rec, out var m);
            string? path = m.File is { } named && HdAssets.Source!.Exists(dir + named) ? dir + named : null;
            foreach (var stem in rec < 10 ? new[] { $"shellbg{rec}", $"shellbg0{rec}" } : [$"shellbg{rec}"])
                foreach (var ext in new[] { ".webp", ".png", ".jpg" })
                    path ??= HdAssets.Source!.Exists(dir + stem + ext) ? dir + stem + ext : null;
            if (path is null) continue;
            _entries[sign * rec] = m.A is [var ax, var bx, var ay, var by] ? new Entry(path, ax, bx, ay, by) : new Entry(path, float.NaN, 0, 0, 0);
        }
    }

    /// <summary>Joe's pads with record 6's art but for the title (8, 9, 32) and the check boxes (7, 10).</summary>
    private static readonly Dictionary<int, int> Borrows = new() { [7] = 6, [8] = 6, [9] = 6, [10] = 6, [32] = 6 };

    /// <summary>The record whose upscale this one shows: its own, else the one it borrows; 0 for none.</summary>
    public int Source(int record, int native = 0) =>
        !HdAssets.Enabled ? 0
        : native > 0 && _entries.ContainsKey(-native) ? -native
        : native > 0 && I76.Formats.Shell.NitroShellMap.Redrawn(native) ? 0   // Nitro's own art: its original pixels, not the base game's upscale
        : _entries.ContainsKey(record) ? record : Borrows.TryGetValue(record, out int b) && _entries.ContainsKey(b) ? b : 0;

    /// <summary>Whether there is an upscale for this background record.</summary>
    public bool Has(int record, int native = 0) => Source(record, native) != 0;

    /// <summary>Shows the record's upscale (or the one it borrows), or nothing (0, or none there).</summary>
    public void Show(int record, int native = 0)
    {
        record = Source(record, native);
        var e = _entries.GetValueOrDefault(record);
        if (e == _entry) return;
        _entry = e;
        _texture = e is null ? null : Load(record, e);
        QueueRedraw();
    }

    private ImageTexture? Load(int record, Entry e)
    {
        int i = _loaded.FindIndex(l => l.Record == record);
        if (i >= 0)
        {
            var hit = _loaded[i];
            _loaded.RemoveAt(i);
            _loaded.Add(hit);
            return hit.Texture;
        }
        var img = HdAssets.LoadImage(e.Path);
        if (img is null || img.IsEmpty()) { GD.PushWarning($"shell backgrounds: {e.Path} unreadable"); _entries.Remove(record); return null; }
        img.GenerateMipmaps();
        var tex = ImageTexture.CreateFromImage(img);
        _loaded.Add((record, tex));
        if (_loaded.Count > Keep) _loaded.RemoveAt(0);
        return tex;
    }

    public override void _Draw()
    {
        if (_entry is not { } e || _texture is null) return;
        var size = GetViewportRect().Size;
        float s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        var off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * s) / 2;
        var tsz = _texture.GetSize();
        float ax = e.Ax, bx = e.Bx, ay = e.Ay, by = e.By;
        if (float.IsNaN(ax))
        {
            // no fit: the picture's height is the frame's, centred on it
            ax = ay = tsz.Y / ShellFrame.H;
            bx = (tsz.X - ShellFrame.W * ax) / 2;
            by = 0;
        }
        // the picture's own rectangle on screen
        var p0 = off + new Vector2(-bx / ax, -by / ay) * s;
        var p1 = off + new Vector2((tsz.X - bx) / ax, (tsz.Y - by) / ay) * s;
        // a fitted picture can fall a pixel or two short of the window's edge (or, 16:9 on a 16:9 window, of the 4:3
        // box's): the edge texels carried out to it, rather than a seam of the black behind
        float slackX = 0.02f * (p1.X - p0.X), slackY = 0.02f * (p1.Y - p0.Y);
        float x0 = p0.X > 0 && p0.X < slackX ? 0 : p0.X, y0 = p0.Y > 0 && p0.Y < slackY ? 0 : p0.Y;
        float x1 = p1.X < size.X && size.X - p1.X < slackX ? size.X : p1.X, y1 = p1.Y < size.Y && size.Y - p1.Y < slackY ? size.Y : p1.Y;
        var dest = new Rect2(x0, y0, x1 - x0, y1 - y0);
        // the same rectangle in texels: screen → frame → image
        Vector2 Tex(Vector2 q) { var f = (q - off) / s; return new Vector2(ax * f.X + bx, ay * f.Y + by); }
        var t0 = Tex(dest.Position);
        var t1 = Tex(dest.End);
        DrawTextureRectRegion(_texture, dest, new Rect2(t0, t1 - t0));
    }
}
