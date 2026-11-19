using Godot;
using I76.Formats.Imaging;
using I76.Formats.Shell;

namespace I76.Game.Assets;

/// <summary>
/// The one place upscaled world, car and cockpit textures are found (<see cref="HdTexture"/> has the layout). None ships with
/// the port; sources in order: $I76_TEX_DIR/world/ (a folder, for development), then the packs <see cref="HdPacks"/> found:
/// tex-world-8x, tex-world-4x, tex-world-3x and tex-world-2x, every one present read in that order (a partial pack over a full
/// one), else tex-world; each a folder in the user folder (or one level too deep, inside the folder Extract All made, or beside
/// the program) or the pack's zip read in place (I26-HD-*.zip, i76-hd-*.zip). Only the
/// modern look draws them, and only on the models' faces (<see cref="TextureFactory"/>); the "HD Textures" setting switches
/// them off.
/// </summary>
public static class HdTextures
{
    private static HdSource? _source;
    private static volatile bool _probed;
    private static readonly object Gate = new();

    /// <summary>The player's switch (ShellSettings.HdTextures): textures made after a change follow it, and a look change.</summary>
    public static bool Enabled { get; set; } = true;

    public static HdSource? Source { get { Probe(); return _source; } }
    public static bool Installed => Source is not null;
    /// <summary>"name version", or "folder"; null without a pack.</summary>
    public static string? Label => Source is { } s ? (s.Info?.ToString() ?? "folder") : null;

    /// <summary>Finds the pack once and logs what was found (Main calls it at start; a mission's build thread may be first).</summary>
    public static void Probe()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            ProbeOnce();
            _probed = true;
        }
    }

    private static bool UserHas(string folder) => HdPacks.Found.Has(folder);

    private static void ProbeOnce()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var env = System.Environment.GetEnvironmentVariable("I76_TEX_DIR") is { Length: > 0 } d ? Path.Combine(d, "world") : null;
        try
        {
            var folders = HdTexture.Folders(UserHas);
            if (env is not null && Directory.Exists(env)) _source = new HdFlatDirSource(env);
            else if (folders.Length == 1) _source = HdPacks.Found.Get(folders[0])!.Open();
            else if (folders.Length > 1) _source = new HdChainSource(folders.Select(f => HdPacks.Found.Get(f)!.Open()));
            if (_source is { Count: 0 }) { _source.Dispose(); _source = null; }
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException) { GD.PushWarning($"HD textures: {e.Message}"); _source = null; }
        if (_source is null) { GD.Print("HD textures: none (no I76_TEX_DIR/world, and no tex-world-Nx or tex-world folder, or zip holding one, in the user folder or beside the program)"); return; }
        GD.Print($"HD textures: {_source.Origin} ({_source.Count} files{(_source.Info is { } i ? $", {i}" : "")}; {watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>Whether the pack has an upscale of the member (whatever the switch).</summary>
    public static bool Has(string member) => Source is { } s && HdTexture.Candidates(member).Any(s.Exists);

    /// <summary>
    /// The member's upscale as the GPU takes it, or null (no pack, none for it, or one that does not decode): a .dds as stored, its
    /// mipmaps and block compression kept; a picture decoded to RGBA8 and fitted to the original (<see cref="HdTexture.Fit"/>,
    /// <paramref name="original"/> the original's RGBA bled under its key, stored order), with mipmaps. Touches no renderer.
    /// </summary>
    public static Image? Load(string member, byte[] original, int ow, int oh)
    {
        if (Source is not { } src) return null;
        foreach (var rel in HdTexture.Candidates(member))
        {
            byte[]? b;
            try { b = src.Read(rel); }
            catch (System.Exception e) when (e is IOException or InvalidDataException or System.UnauthorizedAccessException) { GD.PushWarning($"HD texture {rel}: {e.Message}"); return null; }
            if (b is null) continue;
            var img = new Image();
            var err = rel.EndsWith(".dds") ? img.LoadDdsFromBuffer(b) : rel.EndsWith(".png") ? img.LoadPngFromBuffer(b)
                : rel.EndsWith(".webp") ? img.LoadWebpFromBuffer(b) : img.LoadJpgFromBuffer(b);
            if (err != Error.Ok || img.IsEmpty()) { GD.PushWarning($"HD texture {rel}: does not decode"); return null; }
            int w = img.GetWidth(), h = img.GetHeight(), s = HdTexture.ScaleOf(w, h, ow, oh);
            if (s == 0) { GD.PushWarning($"HD texture {rel}: {w}x{h} is not a whole multiple of {ow}x{oh}, the same both ways"); return null; }
            Served(s, member);
            if (img.IsCompressed()) return img;
            img.Convert(Image.Format.Rgba8);
            var fitted = Image.CreateFromData(w, h, false, Image.Format.Rgba8,
                HdTexture.Fit(img.GetData(), w, h, s, original, ow, oh, !member.EndsWith(".vqm", System.StringComparison.OrdinalIgnoreCase)));
            fitted.GenerateMipmaps();
            return fitted;
        }
        return null;
    }

    private static readonly int[] _served = new int[17];

    /// <summary>Counts an upscale served (build threads too); the first at each scale is logged.</summary>
    private static void Served(int s, string member)
    {
        if (s >= _served.Length) return;
        if (System.Threading.Interlocked.Increment(ref _served[s]) == 1) GD.Print($"HD textures: first {s}x upscale served ({member})");
    }
}
