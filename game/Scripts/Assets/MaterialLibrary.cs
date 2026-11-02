using Godot;
using I76.Formats.Assembly;

namespace I76.Game.Assets;

/// <summary>
/// One material per texture (or flat colour), shared by every mesh that
/// uses it. Each runs an object shader (object.gdshaderinc) in its modern or
/// its 1997 variant; a mode change swaps the shader on every material.
/// </summary>
public sealed class MaterialLibrary(TextureFactory textures)
{
    private readonly Dictionary<string, Material> _cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The materials of back-to-back faces: never drawn from both sides, where the pair's two faces would z-fight.</summary>
    private readonly HashSet<Material> _oneSided = [];
    private static Shader? _back, _both, _open, _translucent, _back97, _both97, _open97, _translucent97, _translucentBack97;
    private static Shader Translucent => _translucent ??= GD.Load<Shader>("res://Shaders/object_translucent.gdshader");
    private static Shader Back => _back ??= GD.Load<Shader>("res://Shaders/object_back.gdshader");
    private static Shader Both => _both ??= GD.Load<Shader>("res://Shaders/object_disabled.gdshader");
    private static Shader Open => _open ??= GD.Load<Shader>("res://Shaders/object_open.gdshader");
    private static Shader Open97 => _open97 ??= GD.Load<Shader>("res://Shaders/object_open_1997.gdshader");
    private static Shader Translucent97 => _translucent97 ??= GD.Load<Shader>("res://Shaders/object_translucent_1997.gdshader");
    private static Shader Back97 => _back97 ??= GD.Load<Shader>("res://Shaders/object_back_1997.gdshader");
    private static Shader Both97 => _both97 ??= GD.Load<Shader>("res://Shaders/object_disabled_1997.gdshader");
    private static Shader TranslucentBack97 => _translucentBack97 ??= GD.Load<Shader>("res://Shaders/object_translucent_back_1997.gdshader");

    /// <summary>The materials drawn from both sides in the modern look but from the front only in the 1997 look (ForTexture's oneSided1997): their shader in each.</summary>
    private readonly Dictionary<ShaderMaterial, (Shader Modern, Shader Faithful)> _oneSided1997 = [];

    /// <summary>Whether materials run the 1997 variants (new ones too).</summary>
    public bool Mode1997 { get; private set; }

    private Shader Pick(Shader modern) => !Mode1997 ? modern : modern == Translucent ? Translucent97 : modern == Both ? Both97 : modern == Open ? Open97 : Back97;
    private static Shader Swap(Shader s, bool to1997) => to1997
        ? (s == Translucent ? Translucent97 : s == Both ? Both97 : s == Open ? Open97 : s == Back ? Back97 : s)
        : (s == Translucent97 ? Translucent : s == Both97 ? Both : s == Open97 ? Open : s == Back97 ? Back : s);

    /// <summary>The animated textures' materials (a .tmt with header flag 1): each steps through its frames at its rate.</summary>
    private readonly List<(ShaderMaterial Mat, Texture2D[] Frames, float Fps, bool Nearest)> _animated = [];

    /// <summary>
    /// An animated .tmt's material (the default texture animation tick): frame 0's material, its
    /// texture stepped by <see cref="Animate"/>. One per .tmt and mode, so every object using it flickers together.
    /// </summary>
    public Material ForAnimated(string[] frames, float fps, FaceMode mode, I76.Formats.Imaging.CarMaterial.Paint? car = null,
        I76.Formats.Imaging.PropMaterial.Family prop = I76.Formats.Imaging.PropMaterial.Family.General, bool bothSides = false, bool unlit = false)
    {
        var key = $"anim|{string.Join(",", frames)}|{mode}|{(car is null ? "" : "car")}" + (bothSides ? "|2" : "") + (unlit ? "|u" : "");
        if (_cache.TryGetValue(key, out var m)) return m;
        if (ForTexture(frames[0], mode, car, prop, bothSides: bothSides, unlit: unlit) is not ShaderMaterial first)
            return _cache[key] = ForTexture(frames[0], mode, car, prop, bothSides: bothSides, unlit: unlit);
        var mat = (ShaderMaterial)first.Duplicate();
        var tex = frames.Select(f => textures.Get(f)?.Texture).OfType<Texture2D>().ToArray();
        if (tex.Length == frames.Length) _animated.Add((mat, tex, fps, mat.GetShaderParameter("albedo_nearest").VariantType != Variant.Type.Nil));
        return _cache[key] = mat;
    }

    /// <summary>The animated textures' clock (the last <see cref="Animate"/>), for what runs in step with them (a light bar's lights).</summary>
    public static double Clock { get; private set; }

    /// <summary>Steps the animated textures to real time <paramref name="seconds"/> (the engine's frame clock).</summary>
    public void Animate(double seconds)
    {
        Clock = seconds;
        foreach (var (mat, frames, fps, nearest) in _animated)
        {
            var t = frames[(int)(seconds * fps) % frames.Length];
            mat.SetShaderParameter("albedo_tex", t);
            if (nearest) mat.SetShaderParameter("albedo_nearest", t);
        }
    }

    /// <summary>A texture member's Godot texture (null if it does not load).</summary>
    public Texture2D? TextureOf(string member) => textures.Get(member)?.Texture;

    private int _strips;

    /// <summary>
    /// A wheel's own copy of a material, for its texture strip (stepped per wheel by CarNode):
    /// kept with the rest, so a mode change swaps its shader too.
    /// </summary>
    public ShaderMaterial? StripCopy(Material shared)
    {
        if (shared is not ShaderMaterial s) return null;
        var copy = (ShaderMaterial)s.Duplicate();
        _cache[$"strip|{_strips++}"] = copy;
        return copy;
    }

    /// <summary>Shows <paramref name="t"/> on a material (its nearest-neighbour copy too, where it has one).</summary>
    public static void ShowFrame(ShaderMaterial m, Texture2D t)
    {
        m.SetShaderParameter("albedo_tex", t);
        if (m.GetShaderParameter("albedo_nearest").VariantType != Variant.Type.Nil) m.SetShaderParameter("albedo_nearest", t);
    }

    /// <summary>A material drawn from both sides: itself if it already is, is a back-to-back face's or is not an object material, else a copy, made once and kept.</summary>
    public Material BothSides(Material m)
    {
        if (m is not ShaderMaterial s || (s.Shader != Back && s.Shader != Back97) || _oneSided.Contains(m)) return m;
        var key = $"both|{s.GetInstanceId()}";
        if (_cache.TryGetValue(key, out var b)) return b;
        var copy = (ShaderMaterial)s.Duplicate();
        copy.Shader = s.Shader == Back ? Open : Open97;
        return _cache[key] = copy;
    }

    /// <summary>Every mesh under <paramref name="node"/> (and itself) drawn from both sides, by surface override.</summary>
    public void ShowBothSides(Node node)
    {
        foreach (var mi in (node is MeshInstance3D self ? [self] : Array.Empty<MeshInstance3D>()).Concat(node.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>()))
            if (mi.Mesh is { } mesh)
                for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                    if ((mi.GetSurfaceOverrideMaterial(i) ?? mi.MaterialOverride ?? mesh.SurfaceGetMaterial(i)) is { } m)
                        mi.SetSurfaceOverrideMaterial(i, BothSides(m));
    }

    /// <summary>Switches every material between the modern and 1997 shader variants.</summary>
    public void SetMode1997(bool on)
    {
        Mode1997 = on;
        foreach (var m in _cache.Values.OfType<ShaderMaterial>())
            m.Shader = _oneSided1997.TryGetValue(m, out var pair) ? (on ? pair.Faithful : pair.Modern) : Swap(m.Shader, on);
    }

    /// <summary>How a GEO face draws (its draw mode).</summary>
    public enum FaceMode { Opaque, Cutout, Translucent }

    /// <summary>The face's mode from its +0x20 / +0x21 bytes: +0x21 ≠ 0 translucent, +0x20 bit 2 cut-out, else opaque.</summary>
    public static FaceMode ModeOf(byte b20, byte b21) => b21 != 0 ? FaceMode.Translucent : (b20 & 4) != 0 ? FaceMode.Cutout : FaceMode.Opaque;

    /// <summary>
    /// A texture's material for a face mode. Only cut-out and translucent
    /// faces key palette index 255 (drawn from both sides); opaque faces draw
    /// it like any other texel; translucent ones blend at 0x99/255. <paramref name="bothSides"/> draws any face from both sides.
    /// <paramref name="unlit"/>: the 1997 look draws it at full brightness, as the engine does such faces (FaceLight).
    /// <paramref name="oneSided1997"/>: a keyed or translucent face drawn from the front only in the 1997 look, as the
    /// engine draws every face of a scene object (a face whose plane faces away from the eye is left out
    /// before its mode is read); the modern look keeps it both-sided.
    /// </summary>
    public Material ForTexture(string member, FaceMode mode = FaceMode.Cutout, I76.Formats.Imaging.CarMaterial.Paint? car = null,
        I76.Formats.Imaging.PropMaterial.Family prop = I76.Formats.Imaging.PropMaterial.Family.General, bool oneSided = false, bool bothSides = false,
        bool unlit = false, bool oneSided1997 = false, string? scheme = null)
    {
        var key = (car is null ? $"{member}|{mode}" + (prop == I76.Formats.Imaging.PropMaterial.Family.General ? "" : $"|prop{(int)prop}") : $"{member}|{mode}|car") + (oneSided ? "|1" : "") + (bothSides ? "|2" : "") + (unlit ? "|u" : "")
            + (oneSided1997 ? "|f" : "") + (car is not null && scheme is not null ? "|p" + scheme : "");
        if (_cache.TryGetValue(key, out var m)) return m;
        var t = textures.Get(member);
        bool keyed = mode != FaceMode.Opaque && t is { HasKey: true };
        var modern = mode == FaceMode.Translucent ? Translucent : oneSided ? Back : keyed ? Both : bothSides ? Open : Back;
        var mat = new ShaderMaterial { Shader = Pick(modern) };
        if (oneSided) _oneSided.Add(mat);
        if (oneSided1997 && !bothSides && (modern == Translucent || modern == Both))
        {
            var faithful = modern == Translucent ? TranslucentBack97 : Back97;
            _oneSided1997[mat] = (modern, faithful);
            mat.Shader = Mode1997 ? faithful : modern;
        }
        if (t is not null)
        {
            mat.SetShaderParameter("albedo_tex", t.Texture);
            mat.SetShaderParameter("use_texture", true);
            mat.SetShaderParameter("alpha_key", keyed);
            // a wheel's face (WA<dd><ll><dd>): a disc filling its square, in the modern look edged by a true circle
            mat.SetShaderParameter("round_key", keyed && IsWheelFace(member));
        }
        else mat.SetShaderParameter("albedo_color", Colors.Magenta);
        if (unlit) mat.SetShaderParameter("unlit_1997", true);
        // the cockpit's own art (z*: panels, wheel, arms, dash) carries its lighting: the engine applies none
        string stem = member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        if (stem.StartsWith('z') || stem.StartsWith('Z'))
        {
            // drawn nearest-neighbour, as the dash's pixel art was
            mat.SetShaderParameter("prelit", true);
            if (t is not null) mat.SetShaderParameter("albedo_nearest", t.Texture);
        }
        // a car's, in the modern look: the material maps made from the texture (CarMaterial), or for now one
        // clear-coated paint over the whole car
        else if (car is { } paint && t is not null)
        {
            if (CarMaps) SetMaps(mat, member, t, paint);
            else
            {
                mat.SetShaderParameter("car_paint", true);
                if (System.Environment.GetEnvironmentVariable("I76_CAR_NORMALS_VIEW") == "1") mat.SetShaderParameter("normals_debug", true);
                // the windows: where the data labels glass (the table's glass preset), the glass shader and its proxy cabin; the rest of
                // the car is clear-coated paint, one rule all over
                if (GlassOf(member, t) is { } glass) { mat.SetShaderParameter("glass_tex", glass.Tex); mat.SetShaderParameter("glass_frame", glass.Frame); mat.SetShaderParameter("glass_body", glass.Body); mat.SetShaderParameter("has_glass", true); }
            }
            if (scheme is not null) RegisterPaint(scheme, mat, member);
        }
        // scenery and props, in the modern look: their own material maps (PropMaterial), no relief. Not the translucent
        // faces (lamp cones, rotor blades, explosions), which stay as they are
        else if (car is null && t is not null && mode != FaceMode.Translucent && PropMaps)
            SetPropMaps(mat, member, t, prop);
        return _cache[key] = mat;
    }

    // ---- respray: a paint scheme's body materials take their paint masks when a car with custom paint needs them ----

    private sealed class PaintScheme
    {
        public PaintMasks? Masks;
        public readonly List<(ShaderMaterial Mat, string Member)> Mats = [];
        public readonly Dictionary<string, ImageTexture> Textures = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, PaintScheme> _paintSchemes = [];

    private PaintScheme SchemeOf(string key) => _paintSchemes.TryGetValue(key, out var s) ? s : _paintSchemes[key] = new PaintScheme();

    private void RegisterPaint(string scheme, ShaderMaterial mat, string member)
    {
        var ps = SchemeOf(scheme);
        ps.Mats.Add((mat, member));
        if (ps.Masks is not null) AttachMask(ps, mat, member);
    }

    /// <summary>A texture member as RGBA8 (the colours it is drawn in), the paint masks' source.</summary>
    public (int Width, int Height, byte[] Rgba)? RgbaOf(string member) => textures.GetRgba(member) is { } t ? (t.Width, t.Height, t.Rgba) : null;

    /// <summary>Gives the body materials of a paint scheme (<paramref name="key"/>, <see cref="PaintMasks.KeyOf"/>) their masks; once per scheme, and for materials made later too.</summary>
    public void EnablePaint(string key, PaintMasks masks)
    {
        var ps = SchemeOf(key);
        if (ps.Masks is not null) return;
        ps.Masks = masks;
        foreach (var (mat, member) in ps.Mats) AttachMask(ps, mat, member);
    }

    private void AttachMask(PaintScheme ps, ShaderMaterial mat, string member)
    {
        if (ps.Masks!.MaskOf(member) is not { } mk) return;
        if (!ps.Textures.TryGetValue(member, out var tex))
        {
            var rg = mk.Rg;
            // the paint is where the car material table says paint, and nowhere else (a window or a bumper painted the same colour stays glass or chrome)
            if (textures.Get(member) is { } te && CarTable.Ids(member, te.Rgba, te.Width, te.Height) is { } ids && ids.Length * 2 == rg.Length)
            {
                rg = (byte[])rg.Clone();
                var pw = I76.Formats.Imaging.CarMaterialTable.PaintWeight(ids);
                for (int i = 0; i < pw.Length; i++) if (pw[i] == 0) { rg[i * 2] = 0; rg[i * 2 + 1] = 0; }
            }
            var img = Image.CreateFromData(mk.Width, mk.Height, false, Image.Format.Rg8, rg);
            img.GenerateMipmaps();
            ps.Textures[member] = tex = ImageTexture.CreateFromImage(img);
        }
        mat.SetShaderParameter("paint_mask", tex);
        mat.SetShaderParameter("has_paint_mask", true);
        // the windows' surroundings (the body painted up to the glass, the frame fill, the trim line) are baked from the scheme's own
        // colours: their paint weights, made the same way, let the shader respray them too
        if (_glass.TryGetValue(member, out var ge) && ge is not null && ge.Width * ge.Height * 2 == ps.Masks.MaskOf(member)!.Rg.Length)
        {
            if (!ps.Textures.TryGetValue(member + "#glass", out var gp) || !ps.Textures.TryGetValue(member + "#trim", out var gt))
            {
                var (bf, tr) = GlassPaint(ge, tex.GetImage().GetData()[..(ge.Width * ge.Height * 2)]);
                var bi = Image.CreateFromData(ge.Width, ge.Height, false, Image.Format.Rgba8, bf);
                var ti = Image.CreateFromData(ge.Width, ge.Height, false, Image.Format.Rg8, tr);
                bi.GenerateMipmaps(); ti.GenerateMipmaps();
                ps.Textures[member + "#glass"] = gp = ImageTexture.CreateFromImage(bi);
                ps.Textures[member + "#trim"] = gt = ImageTexture.CreateFromImage(ti);
            }
            mat.SetShaderParameter("glass_paint", gp);
            mat.SetShaderParameter("glass_trim_paint", gt);
        }
        mat.SetShaderParameter("paint_luma", new Vector2(ps.Masks.PrimaryLuma, ps.Masks.SecondaryLuma));
    }

    /// <summary>An intact wheel face (WA&lt;dd&gt;&lt;ll&gt;&lt;dd&gt;): a full disc. The blown faces (WA&lt;d&gt;A..) are flattened at the bottom, so they keep their texture's alpha.</summary>
    private static readonly System.Text.RegularExpressions.Regex WheelStem = new(@"^wa\d\d[a-z]{2}\d\d$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static bool IsWheelFace(string member)
    {
        string stem = member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        int dot = stem.LastIndexOf('.');
        return WheelStem.IsMatch(dot > 0 ? stem[..dot] : stem);
    }

    /// <summary>Whether cars get per-texel material maps (CarMaterial) rather than one paint; and with them normal maps.</summary>
    public static readonly bool CarMaps = false, CarNormals = false;

    /// <summary>Whether scenery and props get their material maps (PropMaterial); I76_PROP_MAPS=0 turns them off, to compare.</summary>
    public static readonly bool PropMaps = System.Environment.GetEnvironmentVariable("I76_PROP_MAPS") != "0";

    /// <summary>How many prop material maps were made, and the load time they cost (the wait for them on the main thread).</summary>
    public static int PropMapCount { get; private set; }
    public static double PropMapMs { get; private set; }

    private readonly Dictionary<string, ImageTexture> _propMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<I76.Formats.Imaging.PropMaterial.Maps>> _propJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(ShaderMaterial Mat, string Member)> _propPending = [];
    private bool _propFlushQueued;

    /// <summary>
    /// A prop's material maps: made on worker threads while the mission goes on building, and put on the
    /// materials at the end of the frame (FlushPropMaps); until then the material draws as it did without them.
    /// </summary>
    private void SetPropMaps(ShaderMaterial mat, string member, TextureFactory.Entry t, I76.Formats.Imaging.PropMaterial.Family family)
    {
        // one texture can be a car's and its driver's (the army jeep's camouflage): the maps are per family
        string job = family == I76.Formats.Imaging.PropMaterial.Family.Figure ? member + "|figure" : member;
        if (_propMaps.TryGetValue(job, out var orm)) { ApplyPropMaps(mat, orm); return; }
        if (!_propJobs.ContainsKey(job))
        {
            string stem = member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..];
            int dot = stem.LastIndexOf('.');
            if (dot > 0) stem = stem[..dot];
            var (rgba, w, h) = (t.Rgba, t.Width, t.Height);
            _propJobs[job] = Task.Run(() => I76.Formats.Imaging.PropMaterial.Build(rgba, w, h, stem, family));
        }
        _propPending.Add((mat, job));
        if (!_propFlushQueued)
        {
            _propFlushQueued = true;
            Callable.From(FlushPropMaps).CallDeferred();
        }
    }

    /// <summary>Waits for the prop material maps under way and puts them on their materials.</summary>
    public void FlushPropMaps()
    {
        _propFlushQueued = false;
        if (_propPending.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int made = 0;
        foreach (var (mat, member) in _propPending)
        {
            if (!_propMaps.TryGetValue(member, out var orm))
            {
                var maps = _propJobs[member].Result;
                _propJobs.Remove(member);
                var img = Image.CreateFromData(maps.Width, maps.Height, false, Image.Format.Rgba8, maps.Orm);
                img.GenerateMipmaps();
                _propMaps[member] = orm = ImageTexture.CreateFromImage(img);
                made++;
            }
            ApplyPropMaps(mat, orm);
        }
        _propPending.Clear();
        PropMapCount += made;
        PropMapMs += sw.Elapsed.TotalMilliseconds;
        GD.Print($"prop material maps: {made} made, {sw.Elapsed.TotalMilliseconds:0} ms waiting for them ({PropMapCount} in {PropMapMs:0} ms so far)");
    }

    private static void ApplyPropMaps(ShaderMaterial mat, ImageTexture orm)
    {
        mat.SetShaderParameter("orm_tex", orm);
        mat.SetShaderParameter("has_maps", true);
        mat.SetShaderParameter("has_normal", false);
    }

    /// <summary>The car windows' data (CarMaterials.tsv over the segmentation maps): which texels of a car texture are glass.</summary>
    public static readonly I76.Formats.Imaging.CarMaterialTable CarTable = I76.Formats.Imaging.CarMaterialTable.Default;
    private static readonly bool TableOff = System.Environment.GetEnvironmentVariable("I76_CAR_TABLE") == "0";

    // ---- car windows: the glass shading and the proxy cabin behind it (object.gdshaderinc, MeshBuilder.CabinOf) ----

    /// <summary>A texture's glass: per texel 255 where the car material table says glass (preset 11: the hand-made rows over the segmentation maps).</summary>
/// <summary>A texture's windows: the glass (4x), the frame fill, the body round it, and what a respray needs to recolour the last three: the
    /// texel each frame-fill texel took its colour from (-1 where none) and the texture's colours (<see cref="GlassPaint"/>).</summary>
    public sealed record GlassEntry(ImageTexture Tex, ImageTexture Frame, ImageTexture Body, byte[] Mask, int Width, int Height, int[] FrameSrc, byte[] Rgba);

    private readonly Dictionary<string, GlassEntry?> _glass = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The glass a texture got when its material was made (null: none, or not made yet).</summary>
    /// <summary>Graphic Detail's Car Interiors, for every car's glass at once (the shaders' global i76_car_interiors).</summary>
    public static void ApplyCarInteriors(bool on) => RenderingServer.GlobalShaderParameterSet("i76_car_interiors", on);

    /// <summary>A debug key (Ctrl+F8, in a mission and in the 3D garage): every car's material map and glass off, the all-over clear coat back, to compare; returns the new state.</summary>
    public static bool ToggleCarMaterials()
    {
        CarMaterialsOn = !CarMaterialsOn;
        RenderingServer.GlobalShaderParameterSet("i76_car_materials", CarMaterialsOn);
        return CarMaterialsOn;
    }

    public static bool CarMaterialsOn { get; private set; } = true;

    public GlassEntry? GlassCached(string member) => _glass.GetValueOrDefault(member);

    // the glass texels' colour, a Gaussian of three texels over the glass alone (normalised: colour times mask over mask); a texel with no
    // glass near takes the texture's mean glass colour
    private static byte[] GlassTint(byte[] rgba, byte[] mask, int w, int h)
    {
        const int r = 6;
        var k = new float[2 * r + 1];
        for (int j = -r; j <= r; j++) k[j + r] = MathF.Exp(-j * j / 18f);
        var num = new float[w * h * 4];
        double mr = 0, mg = 0, mb = 0, mn = 0;
        for (int i = 0; i < w * h; i++)
            if (mask[i] != 0)
            {
                num[i * 4] = rgba[i * 4]; num[i * 4 + 1] = rgba[i * 4 + 1]; num[i * 4 + 2] = rgba[i * 4 + 2]; num[i * 4 + 3] = 1;
                mr += rgba[i * 4]; mg += rgba[i * 4 + 1]; mb += rgba[i * 4 + 2]; mn++;
            }
        var tmp = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int j = -r; j <= r; j++)
                {
                    int s = (y * w + Math.Clamp(x + j, 0, w - 1)) * 4, d = (y * w + x) * 4;
                    for (int c = 0; c < 4; c++) tmp[d + c] += k[j + r] * num[s + c];
                }
        Array.Clear(num);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int j = -r; j <= r; j++)
                {
                    int s = (Math.Clamp(y + j, 0, h - 1) * w + x) * 4, d = (y * w + x) * 4;
                    for (int c = 0; c < 4; c++) num[d + c] += k[j + r] * tmp[s + c];
                }
        var o = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            float wsum = num[i * 4 + 3];
            for (int c = 0; c < 3; c++)
            {
                double mean = (c == 0 ? mr : c == 1 ? mg : mb) / Math.Max(mn, 1);
                o[i * 3 + c] = (byte)Math.Clamp((int)(wsum > 1e-3f ? num[i * 4 + c] / wsum : mean), 0, 255);
            }
        }
        return o;
    }

    // RGBA8: the frame's colour carried in over the pane (from the nearest texel at least two off the glass, past the painted edge's
    // blend) and alpha 255 on each glass texel, and on each texel next to the glass that looks more like the pane (tint) than like that
    // frame colour (the artist's antialiased edge); elsewhere the texel's own colour and alpha 0. A pillar a texel wide looks like
    // neither pane and keeps its own colour.
    // RGB8: the colour of the painted edge round the glass (the texels next to it), blurred along it (a Gaussian of two texels over
    // those texels only, normalised), for every texel
    private static byte[] TrimColour(byte[] rgba, byte[] mask, int w, int h)
    {
        var edge = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (mask[y * w + x] != 0) continue;
                for (int dy = -1; dy <= 1 && edge[y * w + x] == 0; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h && mask[yy * w + xx] != 0) { edge[y * w + x] = 255; break; }
                    }
            }
        return GlassTint(rgba, edge, w, h);
    }

    /// <summary>
    /// The respray weights (the paint mask's primary, secondary) of what the window shader draws round the glass, at the texture's size,
    /// made as those colours are: RGBA8 red and green the body's (<see cref="BodyColour"/>), blue and alpha the frame fill's (from the
    /// texel each took its colour from, <see cref="FrameFill"/>); and RG8 the trim line's (<see cref="TrimColour"/>).
    /// </summary>
    private static (byte[] BodyFrame, byte[] Trim) GlassPaint(GlassEntry g, byte[] rg)
    {
        int w = g.Width, h = g.Height;
        var asRgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { asRgba[i * 4] = rg[i * 2]; asRgba[i * 4 + 1] = rg[i * 2 + 1]; asRgba[i * 4 + 3] = 255; }
        var body = BodyColour(asRgba, g.Mask, w, h);
        var trim = TrimColour(asRgba, g.Mask, w, h);
        var bf = new byte[w * h * 4];
        var tr = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            bf[i * 4] = body[i * 3]; bf[i * 4 + 1] = body[i * 3 + 1];
            if (g.FrameSrc[i] is int f and >= 0) { bf[i * 4 + 2] = rg[f * 2]; bf[i * 4 + 3] = rg[f * 2 + 1]; }
            tr[i * 2] = trim[i * 3]; tr[i * 2 + 1] = trim[i * 3 + 1];
        }
        return (bf, tr);
    }

    // RGB8: the colour of the ring of texels six to eight off the glass (8-neighbour steps), blurred along it, for every texel
    private static byte[] BodyColour(byte[] rgba, byte[] mask, int w, int h)
    {
        var dist = new int[w * h];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < w * h; i++) if (mask[i] != 0) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int p = q.Dequeue(), x = p % w, y = p / w;
            if (dist[p] >= 8) continue;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int n = yy * w + xx;
                    if (dist[n] <= dist[p] + 1) continue;
                    dist[n] = dist[p] + 1;
                    q.Enqueue(n);
                }
        }
        var ring = new byte[w * h];
        for (int i = 0; i < w * h; i++) if (dist[i] is >= 6 and <= 8) ring[i] = 255;
        return GlassTint(rgba, ring, w, h);
    }

    private static byte[] FrameFill(byte[] rgba, byte[] mask, byte[] tint, int w, int h, out int[] used)
    {
        var dist = new int[w * h];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < w * h; i++) if (mask[i] != 0) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int p = q.Dequeue(), x = p % w, y = p / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int n = yy * w + xx;
                    if (dist[n] <= dist[p] + 1) continue;
                    dist[n] = dist[p] + 1;
                    q.Enqueue(n);
                }
        }
        // the frame colour: from the nearest texel at distance two or more, filled inward
        var from = new int[w * h];
        for (int i = 0; i < w * h; i++) { from[i] = dist[i] >= 2 ? i : -1; if (dist[i] >= 2) q.Enqueue(i); }
        while (q.Count > 0)
        {
            int p = q.Dequeue(), x = p % w, y = p / w;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                int n = yy * w + xx;
                if (from[n] >= 0) continue;
                from[n] = from[p];
                q.Enqueue(n);
            }
        }
        var o = new byte[w * h * 4];
        used = new int[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int src = from[i] >= 0 ? from[i] : i;
            bool flag = mask[i] != 0;
            if (dist[i] == 1 && from[i] >= 0)
            {
                int dp = 0, df = 0;
                for (int c = 0; c < 3; c++) { dp += Math.Abs(rgba[i * 4 + c] - tint[i * 3 + c]); df += Math.Abs(rgba[i * 4 + c] - rgba[src * 4 + c]); }
                flag = dp < df;
            }
            for (int c = 0; c < 3; c++) o[i * 4 + c] = flag ? rgba[src * 4 + c] : rgba[i * 4 + c];
            o[i * 4 + 3] = flag ? (byte)255 : (byte)0;
            used[i] = flag ? src : -1;
        }
        return o;
    }

    public GlassEntry? GlassOf(string member, TextureFactory.Entry t)
    {
        if (_glass.TryGetValue(member, out var made)) return made;
        // the windows come from the segmentation maps (I76_CAR_MASKS=0 leaves them out); the cabin behind them is Car Interiors'
        if (!I76.Formats.Imaging.CarMaskMap.Enabled || TableOff) return _glass[member] = null;
        int w = t.Width, h = t.Height;
        var mask = new byte[w * h];
        int n = 0;
        if (CarTable.Ids(member, t.Rgba, w, h) is { } ids)
            for (int i = 0; i < ids.Length; i++) if (ids[i] == 11) { mask[i] = 255; n++; }
        if (System.Environment.GetEnvironmentVariable("I76_CAR_WINDOWS_LOG") == "1") { long r = 0, gg = 0, b = 0; for (int i = 0; i < w * h; i++) if (mask[i] != 0) { r += t.Rgba[i * 4]; gg += t.Rgba[i * 4 + 1]; b += t.Rgba[i * 4 + 2]; } GD.Print($"glass {I76.Formats.Imaging.CarMaterialTable.StemOf(member)}: {n} of {w * h} texels, mean colour {r / Math.Max(n, 1)},{gg / Math.Max(n, 1)},{b / Math.Max(n, 1)}"); }
        if (n < 6) return _glass[member] = null;
        // the texture: each window's outline straightened into a polygon and filled antialiased at four times the texture's size
        // (GlassShape), so a pane's edge runs straight instead of in the texel steps and a pillar keeps its width; the shader cuts it at
        // one half with a pixel-wide ramp. Its colour: the window texels' own, blurred among themselves (the frame's left out), which
        // tints the cabin seen through it
        const int k = I76.Formats.Imaging.GlassShape.Scale;
        var shape = I76.Formats.Imaging.GlassShape.Field(mask, w, h);
        var tint = GlassTint(t.Rgba, mask, w, h);
        var trim = TrimColour(t.Rgba, mask, w, h);
        int W = w * k, H = h * k;
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, ti = (y / k) * w + x / k;
                // rgb: on the glass its tint, off it the colour of the painted edge round it (the trim the shader draws straight)
                var src = shape[i] >= 128 ? tint : trim;   // by the straightened outline, not the texel's stepped mask
                rgba[i * 4] = src[ti * 3]; rgba[i * 4 + 1] = src[ti * 3 + 1]; rgba[i * 4 + 2] = src[ti * 3 + 2]; rgba[i * 4 + 3] = shape[i];
            }
        var img = Image.CreateFromData(W, H, false, Image.Format.Rgba8, rgba);
        img.GenerateMipmaps();
        // the frame round each pane, carried in over the pane's texels and its painted edge (FrameFill): where the straightened edge leaves
        // a painted pane texel outside the glass, the shader shows the frame there instead of a blue tooth
        var frame = FrameFill(t.Rgba, mask, tint, w, h, out var frameSrc);
        var fimg = Image.CreateFromData(w, h, false, Image.Format.Rgba8, frame);
        fimg.GenerateMipmaps();
        // the body round the window, a few texels out (past its painted trim): the shader paints it straight up to the trim line, over the
        // painting's own stepped edge between trim and body
        var bimg = Image.CreateFromData(w, h, false, Image.Format.Rgb8, BodyColour(t.Rgba, mask, w, h));
        bimg.GenerateMipmaps();
        return _glass[member] = new GlassEntry(ImageTexture.CreateFromImage(img), ImageTexture.CreateFromImage(fimg), ImageTexture.CreateFromImage(bimg), mask, w, h, frameSrc, t.Rgba);
    }

    private readonly Dictionary<string, (ImageTexture Orm, ImageTexture Normal)> _maps = new(StringComparer.OrdinalIgnoreCase);

    private void SetMaps(ShaderMaterial mat, string member, TextureFactory.Entry t, I76.Formats.Imaging.CarMaterial.Paint paint)
    {
        if (!_maps.TryGetValue(member, out var mp))
        {
            var maps = I76.Formats.Imaging.CarMaterial.Build(t.Rgba, t.Width, t.Height, paint);
            var orm = Image.CreateFromData(maps.Width, maps.Height, false, Image.Format.Rgba8, maps.Orm);
            var nrm = Image.CreateFromData(maps.Width, maps.Height, false, Image.Format.Rgb8, maps.Normal);
            orm.GenerateMipmaps();
            nrm.GenerateMipmaps();
            _maps[member] = mp = (ImageTexture.CreateFromImage(orm), ImageTexture.CreateFromImage(nrm));
        }
        mat.SetShaderParameter("orm_tex", mp.Orm);
        mat.SetShaderParameter("normal_tex", mp.Normal);
        mat.SetShaderParameter("has_maps", true);
        mat.SetShaderParameter("has_normal", CarNormals);
    }

    /// <summary>The paint a car's material maps are made against: the commonest colour over its textures.</summary>
    public I76.Formats.Imaging.CarMaterial.Paint PaintOf(IEnumerable<string> members) =>
        I76.Formats.Imaging.CarMaterial.DominantPaint(members.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(textures.Get).OfType<TextureFactory.Entry>().Select(e => (e.Rgba, e.Width, e.Height)));

    /// <summary>Every material made so far for a texture member.</summary>
    public IEnumerable<Material> MadeFor(string member) =>
        _cache.Where(kv => kv.Key.StartsWith(member + "|", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value);

    /// <summary>Points every material made for a texture member at another texture (the cockpit's live panels).</summary>
    public void OverrideTexture(string member, Texture2D texture)
    {
        foreach (var (key, m) in _cache)
            if (m is ShaderMaterial sm && key.StartsWith(member + "|", StringComparison.OrdinalIgnoreCase))
            {
                sm.SetShaderParameter("albedo_tex", texture);
                sm.SetShaderParameter("albedo_nearest", texture);
            }
    }

    /// <summary>A flat-coloured face's material; a prop's (not a car's) weighed as PropMaterial weighs a texel, in the modern look.</summary>
    public Material ForColour(byte r, byte g, byte b, bool prop = false,
        I76.Formats.Imaging.PropMaterial.Family family = I76.Formats.Imaging.PropMaterial.Family.General, bool bothSides = false, bool oneSided = false)
    {
        prop &= PropMaps;
        bothSides &= !oneSided;
        var key = (prop ? $"#{r:x2}{g:x2}{b:x2}|prop{(int)family}" : $"#{r:x2}{g:x2}{b:x2}") + (bothSides ? "|2" : "") + (oneSided ? "|1" : "");
        if (_cache.TryGetValue(key, out var m)) return m;
        var mat = new ShaderMaterial { Shader = Pick(bothSides ? Open : Back) };
        if (oneSided) _oneSided.Add(mat);
        mat.SetShaderParameter("albedo_color", Color.Color8(r, g, b));
        if (prop)
        {
            var (rough, metal, spec) = I76.Formats.Imaging.PropMaterial.ForColour(r, g, b, family);
            mat.SetShaderParameter("base_roughness", rough);
            mat.SetShaderParameter("base_metallic", metal);
            mat.SetShaderParameter("base_specular", spec);
        }
        return _cache[key] = mat;
    }

    /// <summary>
    /// The modern look's round wheel's tread: matte rubber of the tyre's colour with the shader's tread pattern (object.gdshaderinc, UVs
    /// as <see cref="MeshBuilder"/> lays them). In the 1997 look it swaps to the plain shader like any other, which never draws it (the
    /// round wheel is the modern look's alone).
    /// </summary>
    public Material ForTread(byte r, byte g, byte b)
    {
        var key = $"tread|#{r:x2}{g:x2}{b:x2}";
        if (_cache.TryGetValue(key, out var m)) return m;
        var mat = new ShaderMaterial { Shader = Pick(Back) };
        mat.SetShaderParameter("albedo_color", Color.Color8(r, g, b));
        mat.SetShaderParameter("tread", true);
        return _cache[key] = mat;
    }

    /// <summary>A wheel face's material (<see cref="ForTexture"/>'s) with the tyre's rubber matte and the hubcap's metal shining (modern look; a copy, kept with the rest).</summary>
    public Material ForWheelFace(Material face)
    {
        if (face is not ShaderMaterial s) return face;
        var key = $"wheelface|{s.GetInstanceId()}";
        if (_cache.TryGetValue(key, out var m)) return m;
        var copy = (ShaderMaterial)s.Duplicate();
        copy.SetShaderParameter("wheel_face", true);
        return _cache[key] = copy;
    }
}
