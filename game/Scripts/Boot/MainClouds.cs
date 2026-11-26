using Godot;
using I76.Formats.Imaging;

namespace I76.Game.Boot;

/// <summary>
/// Graphic Detail's Clouds: the modern sky's flat cloud layer (sky_modern.gdshader) or volumetric clouds
/// (sky_volumetric.gdshader, raymarched in Godot's quarter-resolution sky pass). The same material either way, its
/// shader swapped, so the mission's colours, texture and sun stay. Forward+ and Mobile only: the Compatibility
/// renderer has no quarter-resolution sky pass and keeps the flat layer. I76_CLOUDS=flat|volumetric overrides the setting.
/// </summary>
public partial class Main
{
    private ShaderMaterial? _modernSkyMat;
    private ImageTexture? _cloudCover;
    private static Texture3D? _cloudShape, _cloudDetail;
    private static System.Threading.Tasks.Task<(byte[] Shape, byte[] Detail)>? _cloudNoise;

    private static readonly string FlatSkyPath = "res://Shaders/sky_modern.gdshader", VolumetricSkyPath = "res://Shaders/sky_volumetric.gdshader",
        VolumetricHalfSkyPath = "res://Shaders/sky_volumetric_half.gdshader";

    /// <summary>I76_CLOUDS=half: the volumetric clouds marched at half resolution instead of a quarter (A/B of the cost).</summary>
    private static bool VolumetricHalf => System.Environment.GetEnvironmentVariable("I76_CLOUDS") == "half";

    /// <summary>Whether this renderer can draw the volumetric clouds.</summary>
    private static bool VolumetricSupported => RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility";

    /// <summary>The setting, or I76_CLOUDS for a run.</summary>
    private static int CloudsMode(I76.Game.Shell.ShellSettings s) =>
        VolumetricHalf ? CloudNoise.Volumetric : System.Environment.GetEnvironmentVariable("I76_CLOUDS") is { Length: > 0 } e ? CloudNoise.Parse(e) : s.Clouds;

    /// <summary>The noise, generated once a run on a worker (about a second), started as soon as it may be wanted.</summary>
    private static void StartCloudNoise() =>
        _cloudNoise ??= System.Threading.Tasks.Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var made = (CloudNoise.Shape(), CloudNoise.Detail());
            GD.Print($"sky: cloud noise made in {clock.ElapsedMilliseconds} ms");
            return made;
        });

    private void ApplyClouds(I76.Game.Shell.ShellSettings s)
    {
        if (_modernSkyMat is not { } m) return;
        bool vol = VolumetricSupported && CloudsMode(s) == CloudNoise.Volumetric;
        var path = !vol ? FlatSkyPath : VolumetricHalf ? VolumetricHalfSkyPath : VolumetricSkyPath;
        if (m.Shader.ResourcePath == path) return;
        if (vol && !PrepareVolumetric(m)) return;
        // the uniforms both shaders share carried across the swap
        var keep = new System.Collections.Generic.Dictionary<string, Variant>();
        foreach (var u in m.Shader.GetShaderUniformList())
        {
            var name = u.AsGodotDictionary()["name"].AsString();
            keep[name] = m.GetShaderParameter(name);
        }
        m.Shader = GD.Load<Shader>(path);
        foreach (var (name, v) in keep) m.SetShaderParameter(name, v);
        GD.Print($"sky: {(vol ? "volumetric" : "flat")} clouds");
    }

    /// <summary>The volumetric shader's own inputs: the noise (waited for, the first time) and the mission's cover map.</summary>
    private bool PrepareVolumetric(ShaderMaterial m)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (_cloudShape is null || _cloudDetail is null)
        {
            StartCloudNoise();
            var (shape, detail) = _cloudNoise!.GetAwaiter().GetResult();
            _cloudShape = Volume(shape, CloudNoise.ShapeSize);
            _cloudDetail = Volume(detail, CloudNoise.DetailSize);
        }
        if (_cloudCover is null)
        {
            if (m.GetShaderParameter("clouds").As<Texture2D>() is not { } sky || sky.GetImage() is not { } img) return false;
            img = (Image)img.Duplicate();
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgb8);
            var cover = CloudNoise.Cover(img.GetData(), img.GetWidth(), img.GetHeight(), m.GetShaderParameter("sky_lum").AsSingle(), m.GetShaderParameter("cloud_lum").AsSingle());
            _cloudCover = ImageTexture.CreateFromImage(Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rg8, cover));
        }
        m.SetShaderParameter("cover_map", _cloudCover);
        m.SetShaderParameter("shape_noise", _cloudShape);
        m.SetShaderParameter("detail_noise", _cloudDetail);
        GD.Print($"sky: volumetric inputs in {clock.ElapsedMilliseconds} ms");
        return true;
    }

    private static ImageTexture3D Volume(byte[] data, int n)
    {
        var slices = new Godot.Collections.Array<Image>();
        for (int z = 0; z < n; z++)
            slices.Add(Image.CreateFromData(n, n, false, Image.Format.R8, data[(z * n * n)..((z + 1) * n * n)]));
        var t = new ImageTexture3D();
        t.Create(Image.Format.R8, n, n, n, false, slices);
        return t;
    }
}
