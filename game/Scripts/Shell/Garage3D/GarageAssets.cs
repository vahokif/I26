using Godot;
using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;
using I76.Game.Assets;
using I76.Game.Sim;
using I76.Sim.Trip;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// What the 3D garage builds cars from without a mission loaded: the install's textures through a neutral mission palette
/// (T01's, else the first mission's), the same material library, mesh builder and car assembly the missions use, and the look
/// switch (1997 or modern shaders) for all of it. One per install, kept between visits.
/// </summary>
public sealed class GarageAssets
{
    private static GarageAssets? _held;

    /// <summary>The assets of this shell's install (rebuilt when the install changes, as a switch of game does).</summary>
    public static GarageAssets For(Shell shell)
    {
        if (_held is { } h && h.Vfs == shell.Vfs) return h;
        return _held = new GarageAssets(shell.Vfs);
    }

    public static void Forget() => _held = null;

    public AssetVfs Vfs { get; }
    public TextureResolver Resolver { get; }
    public TextureFactory Textures { get; }
    public MaterialLibrary Materials { get; }
    public MeshBuilder Meshes { get; }
    public Palette? Palette { get; }
    /// <summary>The neutral daylight the garage is lit by (noon), as a mission's preset.</summary>
    public I76.Formats.Mission.LightPreset Light { get; } = I76.Formats.Mission.Lighting.ForHour(12);
    /// <summary>The light ramp of the palette's mission (LumaTable) for the 1997 look, or null.</summary>
    public byte[]? Luma { get; }

    private readonly Dictionary<string, CarPartsCache.Parts> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly CarPartsCache _cache;
    private const int Keep = 40;

    private GarageAssets(AssetVfs vfs)
    {
        Vfs = vfs;
        string? act = new[] { "t01.act", "p01.act", "m02.act", "t02.act" }.FirstOrDefault(n => vfs.TryRead(n) is not null);
        if (act is not null && vfs.TryRead(act) is { } pd) Palette = new Palette(pd);
        if (act is not null && Palette is not null && vfs.TryRead(act[..^4] + ".lum") is { } lum) Luma = LumaTable.Build(Palette, lum);
        Resolver = new TextureResolver(vfs);
        Textures = new TextureFactory(Resolver, Palette);
        Materials = new MaterialLibrary(Textures);
        Meshes = new MeshBuilder(Resolver, Materials);
        _cache = new CarPartsCache(vfs, () => Meshes);
    }

    /// <summary>The scratch name a car's file is served under (the vfs's addon table) for the assembly to read: by its bytes, so a repeat pick is a cache hit.</summary>
    public string NameOf(byte[] vcf) => "zgar" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(vcf))[..12].ToLowerInvariant() + ".vcf";

    /// <summary>The car's parts (assembly, meshes, lamps, weapons, wheels), made once per distinct file; the last few kept.</summary>
    public CarPartsCache.Parts PartsOf(string name, byte[] vcf, bool heli = false)
    {
        if (_parts.TryGetValue(name, out var hit)) return hit;
        CarPartsCache.Parts parts;
        using (Serve(name, vcf))
        {
            var model = new ModelAssembler(Vfs) { SkipParts = [] }.Assemble(name) ?? throw new InvalidDataException($"the garage cannot assemble {name}");
            parts = _cache.Make(model, heli: heli, CarNode.BuildLamps(Meshes, model, null));
        }
        _parts[name] = parts;
        _order.Add(name);
        while (_order.Count > Keep) { _parts.Remove(_order[0]); _order.RemoveAt(0); }
        return parts;
    }

    /// <summary>The file served under its scratch name while the returned handle lives (the vfs's addon table is the process's: nothing stays there).</summary>
    public IDisposable Serve(string name, byte[] vcf)
    {
        AssetVfs.Addon[name] = vcf;
        return new Release(name);
    }

    private sealed class Release(string name) : IDisposable
    {
        public void Dispose() => AssetVfs.Addon.TryRemove(name, out _);
    }

    /// <summary>The look: the shaders of every material.</summary>
    public void SetLook(bool mode1997) => Materials.SetMode1997(mode1997);

    /// <summary>The 1997 object shaders' global light (what a mission's sky sets): the garage's noon, through the palette's ramp.</summary>
    /// <summary>The globals as the mission last had them, put back when the garage goes: a garage opened over a game that goes on (Reconfigure).</summary>
    public void RestoreGlobals() => I76.Game.World.ObjectLight.Reapply();

    public void SetGlobals(bool mode1997)
    {
        var l = Coords.ToGodot(Light.Direction).Normalized();
        RenderingServer.GlobalShaderParameterSet("i76_light_dir", l);
        RenderingServer.GlobalShaderParameterSet("i76_light_intensity", Light.Intensity);
        RenderingServer.GlobalShaderParameterSet("i76_ambient", Light.Ambient);
        RenderingServer.GlobalShaderParameterSet("i76_night", false);
        RenderingServer.GlobalShaderParameterSet("i76_fade", new Vector2(I76.Formats.Mission.Lighting.FadeA, I76.Formats.Mission.Lighting.FadeB));
        RenderingServer.GlobalShaderParameterSet("i76_mode_1997", mode1997);
        RenderingServer.GlobalShaderParameterSet("i76_luma", LumaRamp());
    }

    private ImageTexture LumaRamp()
    {
        var img = Image.CreateEmpty(32, 1, false, Image.Format.R8);
        for (int j = 0; j < 32; j++) img.SetPixel(j, 0, new Color((Luma?[j] ?? (byte)(255 * Mathf.Pow(j / 31f, 0.4f))) / 255f, 0, 0));
        return ImageTexture.CreateFromImage(img);
    }
}
