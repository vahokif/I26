using Godot;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's smoke, drawn as a layer of its own with a cap on its opacity. Every puff of every smoke
/// (DamageSmoke, RocketSmoke, ChemClouds) hands its MultiMesh buffer to <see cref="Submit"/> instead of drawing it; a
/// CompositorEffect on the main camera then draws them all after the opaque pass, into two half-size buffers (the
/// damage_smoke_modern shader ported to GLSL: premultiplied, accumulated, each puff soft against the scene's depth, so a
/// wall or a car in front still hides it), and smoke_layer.gdshader lays the first buffer over the scene with the
/// opacity capped (<see cref="Cap"/>, I76_SMOKE_CAP), so however many puffs overlap, a column is never denser than the
/// cap and what is behind it stays in view. The second buffer holds the mean smoke distance per pixel, which the lamp
/// glares read to dim themselves when smoke lies in front of them.
///
/// Forward+ only (a CompositorEffect, and the RenderingDevice); in the Compatibility renderer, or if the effect cannot
/// be set up, <see cref="Active"/> stays false and each smoke draws itself as before, its puffs capped one by one
/// (the shader's a_cap). I76_SMOKE_LAYER=0 leaves the layer out; I76_SMOKE_SCALE=N draws it at 1/N size (2).
/// </summary>
public partial class SmokeLayer : Node3D
{
    /// <summary>The layer is drawing the smoke: the systems submit their puffs instead of drawing them.</summary>
    public static bool Active => _enabled && !_failed;
    /// <summary>The most opaque a column of smoke gets (0..1).</summary>
    public static float Cap { get; } = float.TryParse(System.Environment.GetEnvironmentVariable("I76_SMOKE_CAP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float c) ? Math.Clamp(c, 0f, 1f) : 0.65f;
    public static float SceneLight { get; set; } = 1f;
    /// <summary>The composite quad's layer, which the mirror's and the weapon camera's cull masks leave out (they have no layer to composite).</summary>
    public const uint QuadLayer = 1u << 7;
    /// <summary>The layer of the puffs drawn for the rear-view mirror alone (MirrorDraw).</summary>
    public const uint MirrorLayer = 1u << 8;
    /// <summary>The rear-view mirror's camera, which the layer's compositor does not run on (set by Cockpit).</summary>
    public static Camera3D? MirrorCamera { get; set; }

    private const int Capacity = 12000, Floats = 12, MaxLights = 16, UboFloats = 96 + MaxLights * 12;
    private static readonly int Scale = int.TryParse(System.Environment.GetEnvironmentVariable("I76_SMOKE_SCALE"), out int sc) && sc >= 1 ? sc : 2;
    private static bool _ready, _failed, _enabled;
    private static readonly object Lock = new();
    private static readonly Dictionary<object, (float[] Data, int Count)> Slots = [];

    /// <summary>
    /// A smoke's puffs for this frame, in its MultiMesh buffer layout (16 floats a puff: the basis with the size on its
    /// diagonal and the position in the last column of each row, then the custom data), far to near; replaces what the
    /// owner submitted before.
    /// </summary>
    public static void Submit(object owner, float[] buf, int count, System.Numerics.Vector3 tint)
    {
        lock (Lock)
        {
            Slots.TryGetValue(owner, out var slot);
            if (slot.Data is null || slot.Data.Length < count * Floats) slot.Data = new float[Math.Max(count, 256) * Floats];
            for (int j = 0; j < count; j++)
            {
                int o = j * 16, q = j * Floats;
                var d = slot.Data;
                d[q] = buf[o + 3]; d[q + 1] = buf[o + 7]; d[q + 2] = buf[o + 11]; d[q + 3] = buf[o];
                d[q + 4] = buf[o + 12]; d[q + 5] = buf[o + 13]; d[q + 6] = buf[o + 14]; d[q + 7] = buf[o + 15];
                d[q + 8] = tint.X; d[q + 9] = tint.Y; d[q + 10] = tint.Z; d[q + 11] = SoftDistance;
            }
            Slots[owner] = (slot.Data, count);
        }
    }

    /// <summary>A smoke that leaves the tree: its slot is dropped (it holds the smoke, and what the smoke holds, as long as it is in the table).</summary>
    public static void Release(object owner) { lock (Lock) Slots.Remove(owner); }

    private const float SoftDistance = 1.5f;
    /// <summary>I76_SMOKE_DEPTH_VIEW=1: the layer draws each puff as a square showing the scene's depth as the layer reads it (a cycle of hues for each doubling of the distance, red where the scene is nearer than the puff), to check its depth test on a new GPU or backend.</summary>
    private static readonly bool DepthView = System.Environment.GetEnvironmentVariable("I76_SMOKE_DEPTH_VIEW") == "1";

    // ---- main thread: the compositor on the camera, the composite quad, the layer textures, the globals ----
    private MeshInstance3D? _quad;
    private ShaderMaterial? _mat;
    private readonly Texture2Drd _t0 = new(), _t1 = new();
    private static Rid _rid0, _rid1;
    private static int _version;
    private int _seen;
    private Compositor? _compositor;
    private DirectionalLight3D? _sun;
    private static volatile bool _hasPuffs;
    private static readonly float[] CabInv = new float[16];
    private static float _cabHalfX, _cabHalfY, _cabHalfZ;
    private static System.Numerics.Vector3 _toSun = new(0.3f, 0.8f, 0.5f);
    private static bool _off;
    private static readonly float[] _lights = new float[MaxLights * 12];
    private static int _lightCount;
    private static readonly Vector4[] LightVec = new Vector4[MaxLights * 3];
    private readonly List<Light3D> _lamps = [];
    private int _lampScan;

    public override void _Ready()
    {
        _off = System.Environment.GetEnvironmentVariable("I76_SMOKE_LAYER") == "0";
        if (_off || RenderingServer.GetCurrentRenderingMethod() != "forward_plus" || RenderingServer.GetRenderingDevice() is null) { _failed = true; return; }
        _compositor = new Compositor { CompositorEffects = [new SmokeEffect()] };
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/smoke_layer.gdshader"), RenderPriority = (int)Material.RenderPriorityMin + 1 };
        _quad = new MeshInstance3D
        {
            Name = "SmokeComposite", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = _mat, Visible = false, Layers = QuadLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
        };
        AddChild(_quad);
        _enabled = true;
    }

    public override void _Process(double delta)
    {
        using var _view = Hitch.Time(Hitch.Phase.Views);
        var cam = GetViewport().GetCamera3D();
        if (cam is not null && (cam.CullMask & MirrorLayer) != 0) cam.CullMask &= ~MirrorLayer;   // the mirror's own puffs (MirrorDraw) are for the mirror's camera alone
        if (_off || _failed || _quad is null)
        {
            // no layer (the Compatibility renderer, or I76_SMOKE_LAYER=0): the smokes draw themselves, lit by these lights
            GatherLights(cam);
            return;
        }
        if (cam is not null && cam.Compositor != _compositor) cam.Compositor = _compositor;
        if (_version != _seen)
        {
            _seen = _version;
            _t0.TextureRdRid = _rid0; _t1.TextureRdRid = _rid1;
            _mat!.SetShaderParameter("layer", _t0);
            RenderingServer.GlobalShaderParameterSet("i76_smoke", _t1);
            _ready = true;
            GD.Print($"smoke layer: on, {_size.X}x{_size.Y}, cap {Cap}");
        }
        int total;
        lock (Lock) { total = 0; foreach (var s in Slots.Values) total += s.Count; }
        _hasPuffs = total > 0;
        RenderingServer.GlobalShaderParameterSet(SmokeCapName, _ready && total > 0 ? Cap : 0f);
        _quad.Visible = _ready && total > 0;
        _sun ??= GetTree().Root.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>().FirstOrDefault();
        var toSun = _sun is null ? new Vector3(0.3f, 0.8f, 0.5f) : _sun.GlobalTransform.Basis.Z;
        var cabHalf = World.CabClip.Half;
        var cabInv = new Projection(World.CabClip.Inv);
        GatherLights(cam);
        lock (Lock)
        {
            _toSun = new System.Numerics.Vector3(toSun.X, toSun.Y, toSun.Z);
            _cabHalfX = cabHalf.X; _cabHalfY = cabHalf.Y; _cabHalfZ = cabHalf.Z;
            Write(cabInv, CabInv);
        }
    }

    // ---- render thread ----

    private const string Vert = """
        #version 450
        layout(set = 0, binding = 0, std140) uniform U {
            mat4 view; mat4 proj; mat4 invproj; mat4 invview; mat4 cab_inv;
            vec4 cab_half; vec4 sun_light; vec4 screen;
            vec4 lcount; vec4 lights[48];
        } u;
        layout(set = 0, binding = 1, std430) readonly buffer P { vec4 d[]; } p;
        layout(location = 0) out vec2 v_uv;
        layout(location = 1) flat out vec4 v_c;
        layout(location = 2) flat out vec4 v_t;
        layout(location = 3) out vec3 v_vpos;
        layout(location = 4) out vec3 v_lit;
        // the lamps' light scattered by the smoke at this point: spots and points in the light buffer (nearest first),
        // a soft isotropic part and a forward lobe (Henyey-Greenstein, g 0.55) that is strongest looking toward the lamp
        vec3 scatter(vec3 w, vec3 cam) {
            vec3 sum = vec3(0.0);
            vec3 toc = normalize(cam - w);
            for (int i = 0; i < int(u.lcount.x); i++) {
                vec4 a = u.lights[i * 3], b = u.lights[i * 3 + 1], c = u.lights[i * 3 + 2];
                vec3 d = w - a.xyz;
                float dist = max(length(d), 0.3);
                if (dist >= a.w) continue;
                vec3 dir = d / dist;
                float att = pow(1.0 - dist / a.w, c.w) * (a.w > 0.0 ? 1.0 : 0.0);
                if (b.w > 0.0) {   // a spot: b.xyz its axis, b.w its angle
                    float ang = acos(clamp(dot(dir, b.xyz), -1.0, 1.0));
                    att *= pow(clamp(1.0 - ang / b.w, 0.0, 1.0), 0.7);
                }
                const float g = 0.55;
                float cs = dot(dir, toc);
                float f = (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * cs, 1.5) / ((1.0 - g * g) / pow(1.0 - g, 3.0));
                sum += vec3(c.xyz) * att * (0.12 + f);
            }
            return sum;
        }
        void main() {
            int i = gl_InstanceIndex;
            vec4 a = p.d[i * 3], c = p.d[i * 3 + 1], t = p.d[i * 3 + 2];
            vec2 k = vec2(float(gl_VertexIndex & 1), float(gl_VertexIndex >> 1)) - 0.5;
            vec4 vp = u.view * vec4(a.xyz, 1.0);
            vp.xy += k * a.w;
            v_uv = vec2(k.x + 0.5, 0.5 - k.y);   // the quad's UV: (0, 0) at its top left
            v_c = c; v_t = t; v_vpos = vp.xyz;
            v_lit = u.lcount.x > 0.0 ? scatter((u.invview * vec4(vp.xyz, 1.0)).xyz, u.invview[3].xyz) : vec3(0.0);
            gl_Position = u.proj * vp;
        }
        """;

    // damage_smoke_modern.gdshader, the same maths
    private const string Frag = """
        #version 450
        layout(set = 0, binding = 0, std140) uniform U {
            mat4 view; mat4 proj; mat4 invproj; mat4 invview; mat4 cab_inv;
            vec4 cab_half; vec4 sun_light; vec4 screen;
            vec4 lcount; vec4 lights[48];
        } u;
        layout(set = 0, binding = 2) uniform sampler2D noise_tex;
        #ifdef MSAA
        layout(set = 0, binding = 3) uniform sampler2DMS depth_tex;
        #else
        layout(set = 0, binding = 3) uniform sampler2D depth_tex;
        #endif
        layout(location = 0) in vec2 v_uv;
        layout(location = 1) flat in vec4 v_c;
        layout(location = 2) flat in vec4 v_t;
        layout(location = 3) in vec3 v_vpos;
        layout(location = 4) in vec3 v_lit;
        layout(location = 0) out vec4 o0;
        layout(location = 1) out vec4 o1;
        // a scene texel's depth; with MSAA the nearest of its samples, read from the multisampled depth itself (the engine's
        // resolved copy averages them, and at this point of the frame some GPUs had not yet written it: all of it on Apple's)
        float depth_at(ivec2 p) {
        #ifdef MSAA
            float d = texelFetch(depth_tex, p, 0).r;
            for (int s = 1; s < int(u.screen.z); s++) {
                float e = texelFetch(depth_tex, p, s).r;
                d = u.screen.w > 0.0 ? max(d, e) : min(d, e);
            }
            return d;
        #else
            return texelFetch(depth_tex, p, 0).r;
        #endif
        }
        // the distance to the scene's nearest surface over every scene texel this layer pixel covers (SmokeDepth.Footprint),
        // not the one under its centre: a layer pixel on a car's or a ridge's outline is hidden by the nearer of the two, so
        // the upscaled layer never lays smoke over the outline
        float scene_dist() {
        #ifdef MSAA
            ivec2 dsz = textureSize(depth_tex);
        #else
            ivec2 dsz = textureSize(depth_tex, 0);
        #endif
            vec2 k = u.screen.xy * vec2(dsz);
            ivec2 lo = clamp(ivec2(floor((gl_FragCoord.xy - 0.5) * k)), ivec2(0), dsz - 1);
            ivec2 hi = clamp(ivec2(ceil((gl_FragCoord.xy + 0.5) * k)) - 1, lo, dsz - 1);
            float depth = depth_at(lo);
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++) {
                    float d = depth_at(lo + ((hi - lo) * ivec2(i, j) + 1) / 2);
                    depth = u.screen.w > 0.0 ? max(depth, d) : min(depth, d);   // screen.w: a larger depth is nearer (reversed z)
                }
            vec2 suv = gl_FragCoord.xy * u.screen.xy;
            vec4 vp = u.invproj * vec4(suv * 2.0 - 1.0, depth, 1.0);
            return -vp.z / vp.w;
        }
        void main() {
            if (u.cab_half.w > 0.0) {   // I76_SMOKE_DEPTH_VIEW: each puff a square showing the scene's distance as the layer reads it, a cycle of hues for each doubling, red where the scene hides the puff
                float sd = scene_dist();
                vec3 band = 0.5 + 0.5 * cos(6.2832 * (fract(log2(max(sd, 0.01))) + vec3(0.0, 0.33, 0.67)));
                o0 = sd < -v_vpos.z ? vec4(1.0, 0.0, 0.0, 1.0) : vec4(band * 0.6, 1.0);
                o1 = vec4(0.0);
                return;
            }
            if (u.cab_half.x > 0.0) {
                vec3 world = (u.invview * vec4(v_vpos, 1.0)).xyz;
                vec3 q = abs((u.cab_inv * vec4(world, 1.0)).xyz) - u.cab_half.xyz;
                float cd = smoothstep(0.0, 0.3, -max(q.x, max(q.y, q.z)));
                if (cd > 0.0 && cd >= fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))))) discard;
            }
            float life = v_c.x, seed = v_c.w;
            vec2 d = v_uv * 2.0 - 1.0;
            float ang = seed * 6.2832 + life * 0.6;
            vec2 r = mat2(vec2(cos(ang), sin(ang)), vec2(-sin(ang), cos(ang))) * d;
            float rad = length(d);
            float n = texture(noise_tex, (r * 2.2 + seed * 17.0 + vec2(0.0, life * 0.8)) * 0.125).r;
            float n2b = texture(noise_tex, (r * 5.0 - seed * 9.0) * 0.125).g;
            float shape = 1.0 - rad - (n - 0.5) * 0.8 - (n2b - 0.5) * (0.5 + 0.6 * v_c.z);
            float erode = mix(0.0, 0.55, smoothstep(0.35, 1.0, life));
            float a = smoothstep(erode, erode + mix(0.7, 0.35, v_c.z), shape + 0.12);
            a *= smoothstep(0.0, 0.04, life);
            a *= 1.0 - smoothstep(0.7, 0.98, rad);
            a *= v_c.y * (1.0 - 0.35 * life) * (0.55 + 0.9 * n);
            if (a < 0.004) discard;
            vec3 nrm = normalize(vec3(d.x, d.y, sqrt(max(1.0 - rad * rad, 0.05))) + vec3((n - 0.5) * 0.5));
            float hl = dot(nrm, normalize(u.sun_light.xyz)) * 0.5 + 0.5;
            float lit = mix(0.25 + 1.0 * hl * hl, 0.04 + 2.6 * pow(hl, 3.0), v_c.z);
            vec3 base = mix(vec3(0.62, 0.64, 0.66), vec3(0.028, 0.028, 0.03), pow(v_c.z, 0.7));
            base = mix(base, base * vec3(1.35, 1.0, 0.75) + vec3(0.004, 0.002, 0.0), v_c.z * fract(seed * 7.13));
            base *= (0.8 + 0.4 * n) * v_t.rgb;
            vec3 col = base * lit * u.sun_light.w;
            // lit by the lamps: soot scatters little, steam and ash more
            col += mix(vec3(0.7), vec3(0.14), pow(v_c.z, 0.7)) * v_t.rgb * v_lit * (0.8 + 0.4 * n);
            float dist = -v_vpos.z;
            a *= clamp((scene_dist() - dist) / v_t.w, 0.0, 1.0);
            if (u.cab_half.x > 0.0) a *= smoothstep(1.5, 5.0, dist);   // as damage_smoke_modern: puffs by the cockpit camera thin out
            a = clamp(a, 0.0, 1.0);
            o0 = vec4(col * a, a);
            o1 = vec4(dist * a, 0.0, 0.0, a);
        }
        """;

    private static readonly Rid[] _shaders = new Rid[2], _pipelines = new Rid[2];
    private static Rid _sampler, _noiseSampler, _ubo, _ssbo, _fb, _t0Rd, _t1Rd;
    private static Vector2I _size, _depthFor;
    private static long _fbFormat = -1;
    private static readonly float[] Ubo = new float[UboFloats];
    private static float[] _flat = new float[Capacity * Floats];
    private static bool _cleared = true;

    private sealed partial class SmokeEffect : CompositorEffect
    {
        public SmokeEffect()
        {
            EffectCallbackType = EffectCallbackTypeEnum.PostOpaque;
            Enabled = true;
        }

        public override void _RenderCallback(int effectCallbackType, RenderData renderData)
        {
            if (_failed) return;
            // the multisampled depth's build of the shader is not there (the backend refused it): the engine's resolved copy then
            AccessResolvedDepth = !_msaaOk;
            try { Render(renderData); }
            catch (Exception e) { _failed = true; GD.PrintErr($"smoke layer: {e}"); }
        }
    }

    private static void Render(RenderData renderData)
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd is null) return;
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers || renderData.GetRenderSceneData() is not RenderSceneDataRD scene) return;
        int count = 0;
        lock (Lock)
        {
            foreach (var s in Slots.Values)
            {
                int n = Math.Min(s.Count, Capacity - count);
                Array.Copy(s.Data, 0, _flat, count * Floats, n * Floats);
                count += n;
            }
        }
        if (count == 0 && _cleared) return;
        var full = buffers.GetInternalSize();
        if (full.X <= 0 || full.Y <= 0) return;
        var size = new Vector2I(Math.Max(1, full.X / Scale), Math.Max(1, full.Y / Scale));
        if (!_shaders[0].IsValid && !Setup(rd)) return;
        if (size != _size || !_t0Rd.IsValid) MakeTargets(rd, size);
        // with MSAA the layer reads the multisampled depth itself, the nearest of each texel's samples, not the engine's resolved
        // copy: read after the opaque pass that copy was empty on Apple GPUs (Metal and MoltenVK alike) and at times in patches elsewhere
        int samples = SmokeDepth.Samples(buffers.GetMsaa3D());
        // without the multisampled build (the backend refused it), the engine's resolved copy (the effect asks for it from then on)
        if (samples > 1 && !_msaaOk) samples = 1;
        int variant = samples > 1 ? 1 : 0;
        var depth = buffers.GetDepthLayer(0, samples > 1);
        // no depth to read (the engine's buffers being remade, or no resolved copy yet): no smoke this frame
        if (!depth.IsValid) return;
        // the samples the depth was made with: a backend gives fewer than asked where the GPU has no more for its format
        // (D3D12 and Vulkan alike take the most it has), and a read past them is undefined
        if (samples > 1)
        {
            if (depth != _samplesOf) { _samplesOf = depth; _made = (int)(rd.TextureGetFormat(depth)?.Samples ?? RenderingDevice.TextureSamples.Samples1); }
            samples = SmokeDepth.Samples(buffers.GetMsaa3D(), _made);
        }

        // the camera: view, projection (as the scene's own shaders see it: clip y down), their inverses, the cab box, the sun
        var camT = scene.GetCamTransform();
        var proj = scene.GetViewProjection(0);
        var view = new Projection(camT.AffineInverse());
        Write(view, Ubo.AsSpan(0, 16)); Write(proj, Ubo.AsSpan(16, 16)); Write(proj.Inverse(), Ubo.AsSpan(32, 16)); Write(new Projection(camT), Ubo.AsSpan(48, 16));
        lock (Lock)
        {
            Array.Copy(CabInv, 0, Ubo, 64, 16);
            Ubo[80] = _cabHalfX; Ubo[81] = _cabHalfY; Ubo[82] = _cabHalfZ;
            var sv = camT.Basis.Inverse() * new Vector3(_toSun.X, _toSun.Y, _toSun.Z);
            Ubo[84] = sv.X; Ubo[85] = sv.Y; Ubo[86] = sv.Z;
        }
        lock (Lock) { Ubo[92] = _lightCount; Array.Copy(_lights, 0, Ubo, 96, _lightCount * 12); }
        Ubo[87] = SceneLight;
        Ubo[83] = DepthView ? 1f : 0f;
        bool reversed = SmokeDepth.NearerIsLarger(proj);
        if (reversed != _reversed) { _reversed = reversed; GD.Print($"smoke layer: scene depth {(reversed ? "reversed" : "forward")}, {buffers.GetMsaa3D()}, {full.X}x{full.Y}"); }
        Ubo[88] = 1f / size.X; Ubo[89] = 1f / size.Y; Ubo[90] = samples; Ubo[91] = reversed ? 1f : -1f;
        System.Buffer.BlockCopy(Ubo, 0, _uboBytes, 0, UboFloats * 4);
        rd.BufferUpdate(_ubo, 0, UboFloats * 4, _uboBytes);
        if (count > 0)
        {
            if (_puffBytes.Length != count * Floats * 4) _puffBytes = new byte[count * Floats * 4];
            System.Buffer.BlockCopy(_flat, 0, _puffBytes, 0, count * Floats * 4);
            rd.BufferUpdate(_ssbo, 0, (uint)(count * Floats * 4), _puffBytes);
        }

        // the uniform set is kept from frame to frame (it was made and freed every frame, four engine objects with
        // it) until the scene's depth texture or the noise is another, or the engine has dropped it with a texture it read
        var noise = RenderingServer.TextureGetRdTexture(DamageSmoke.Noise.GetRid());
        if (!_set.IsValid || depth != _setDepth || noise != _setNoise || variant != _setVariant || !rd.UniformSetIsValid(_set))
        {
            if (_set.IsValid && rd.UniformSetIsValid(_set)) rd.FreeRid(_set);
            _setDepth = depth; _setNoise = noise; _setVariant = variant;
            _setUniforms ??= [Buf(0, RenderingDevice.UniformType.UniformBuffer, _ubo), Buf(1, RenderingDevice.UniformType.StorageBuffer, _ssbo), Tex(2, _noiseSampler, noise), Tex(3, _sampler, depth)];
            _setUniforms[2].ClearIds(); _setUniforms[2].AddId(_noiseSampler); _setUniforms[2].AddId(noise);
            _setUniforms[3].ClearIds(); _setUniforms[3].AddId(_sampler); _setUniforms[3].AddId(depth);
            _set = rd.UniformSetCreate(_setUniforms, _shaders[variant], 0);
        }
        // a release build of the engine draws without checking the pipeline and the uniforms are there (a debug build refuses
        // the draw): a draw without either is the GPU's to fault on, so none then, and the layer stays clear
        bool drawable = _pipelines[variant].IsValid && _set.IsValid && rd.UniformSetIsValid(_set);
        if (!drawable && !_toldUndrawable) { _toldUndrawable = true; GD.PrintErr($"smoke layer: no {(variant == 1 ? "multisampled " : "")}pipeline or uniform set; no smoke drawn"); }
        var dl = rd.DrawListBegin(_fb, RenderingDevice.DrawFlags.ClearColorAll, ClearColors);
        if (count > 0 && drawable)
        {
            rd.DrawListBindRenderPipeline(dl, _pipelines[variant]);
            rd.DrawListBindUniformSet(dl, _set, 0);
            rd.DrawListDraw(dl, false, (uint)count, 4);
        }
        rd.DrawListEnd();
        _cleared = count == 0;
    }

    private static readonly byte[] _uboBytes = new byte[UboFloats * 4];
    private static byte[] _puffBytes = [];
    private static Rid _set, _setDepth, _setNoise;
    private static int _setVariant = -1;
    private static Godot.Collections.Array<RDUniform>? _setUniforms;
    private static readonly Color[] ClearColors = [new Color(0, 0, 0, 0), new Color(0, 0, 0, 0)];
    private static bool? _reversed;
    private static bool _toldUndrawable;
    /// <summary>The multisampled depth texture last asked, and the samples it was made with (RenderingDevice.TextureSamples).</summary>
    private static Rid _samplesOf;
    private static int _made;
    /// <summary>The multisampled depth's build of the shader and its pipeline were made: with MSAA the layer reads the multisampled depth.</summary>
    private static volatile bool _msaaOk = true;

    private static RDUniform Buf(int binding, RenderingDevice.UniformType type, Rid id)
    {
        var u = new RDUniform { UniformType = type, Binding = binding };
        u.AddId(id);
        return u;
    }

    private static RDUniform Tex(int binding, Rid sampler, Rid tex)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = binding };
        u.AddId(sampler); u.AddId(tex);
        return u;
    }

    /// <summary>
    /// The scene's own lights that matter to smoke at night, as the renderer has them: every lit spot and point light
    /// (the cars' headlamps and brake lights, the sirens, the fire of a wreck, the flashes of a blast) within reach of the
    /// camera, the nearest MaxLights of them. The tree is searched every few frames, not every one.
    /// </summary>
    private void GatherLights(Camera3D? cam)
    {
        if (cam is null) { lock (Lock) _lightCount = 0; return; }
        if (_lampScan++ % 6 == 0)
        {
            _lamps.Clear();
            foreach (var n in GetTree().Root.FindChildren("*", "Light3D", true, false))
                if (n is SpotLight3D or OmniLight3D) _lamps.Add((Light3D)n);
        }
        var cp = cam.GlobalPosition;
        var near = new List<(float D2, Light3D L)>();
        foreach (var l in _lamps)
        {
            if (!IsInstanceValid(l) || !l.IsVisibleInTree() || l.LightEnergy < 0.05f) continue;
            float range = l is SpotLight3D sp ? sp.SpotRange : ((OmniLight3D)l).OmniRange;
            float d2 = l.GlobalPosition.DistanceSquaredTo(cp);
            if (d2 > (range + 40f) * (range + 40f)) continue;
            near.Add((d2, l));
        }
        near.Sort((a, b) => a.D2.CompareTo(b.D2));
        lock (Lock)
        {
            int n = Math.Min(near.Count, MaxLights);
            for (int i = 0; i < n; i++)
            {
                var l = near[i].L;
                int o = i * 12;
                var gp = l.GlobalPosition;
                var e = new Vector3(l.LightColor.R, l.LightColor.G, l.LightColor.B) * l.LightEnergy * LightGain;
                if (l is SpotLight3D s)
                {
                    var dir = -l.GlobalTransform.Basis.Z.Normalized();
                    _lights[o] = gp.X; _lights[o + 1] = gp.Y; _lights[o + 2] = gp.Z; _lights[o + 3] = s.SpotRange;
                    _lights[o + 4] = dir.X; _lights[o + 5] = dir.Y; _lights[o + 6] = dir.Z; _lights[o + 7] = Mathf.DegToRad(s.SpotAngle);
                    _lights[o + 11] = s.SpotAttenuation;
                }
                else
                {
                    var om = (OmniLight3D)l;
                    _lights[o] = gp.X; _lights[o + 1] = gp.Y; _lights[o + 2] = gp.Z; _lights[o + 3] = om.OmniRange;
                    _lights[o + 4] = 0; _lights[o + 5] = 0; _lights[o + 6] = 0; _lights[o + 7] = 0;
                    _lights[o + 11] = om.OmniAttenuation;
                }
                _lights[o + 8] = e.X; _lights[o + 9] = e.Y; _lights[o + 10] = e.Z;
            }
            _lightCount = n;
            for (int i = 0; i < MaxLights * 3; i++) LightVec[i] = i < n * 3 ? new Vector4(_lights[i * 4], _lights[i * 4 + 1], _lights[i * 4 + 2], _lights[i * 4 + 3]) : default;
        }
    }

    /// <summary>Hands the nearest lamps to a smoke material (damage_smoke_modern.gdshader) for it to light its own puffs.</summary>
    public static void Lights(ShaderMaterial m)
    {
        lock (Lock) { m.SetShaderParameter(LightsName, LightVec); m.SetShaderParameter(LightCountName, _lightCount); }
    }
    private static readonly StringName LightsName = "lights", LightCountName = "light_count", SmokeCapName = "i76_smoke_cap";

    /// <summary>
    /// Without the layer's cap, the opacity of overlapping puffs is guessed instead: the puffs (far to near, in the buffer
    /// layout, 16 floats each) are counted in a coarse grid of the camera's screen, and each puff's density is divided down
    /// where many lie on one spot, so a column seen end-on thins toward the cap instead of going solid.
    /// </summary>
    public static void Thin(float[] buf, int count, Camera3D? cam)
    {
        if (cam is null || count == 0) return;
        const int W = 24, H = 14;
        Span<float> cell = stackalloc float[W * H];
        var ct = cam.GlobalTransform.AffineInverse();
        var vp = cam.GetViewport().GetVisibleRect().Size;
        float tan = MathF.Tan(Mathf.DegToRad(cam.Fov) * 0.5f), aspect = vp.X / Math.Max(vp.Y, 1f);
        Span<int> at = count <= 4096 ? stackalloc int[count] : new int[count];
        for (int j = 0; j < count; j++)
        {
            var v = ct * new Vector3(buf[j * 16 + 3], buf[j * 16 + 7], buf[j * 16 + 11]);
            at[j] = -1;
            if (v.Z > -0.1f) continue;
            float x = v.X / (-v.Z * tan * aspect) * 0.5f + 0.5f, y = 0.5f - v.Y / (-v.Z * tan) * 0.5f;
            if (x < 0 || y < 0 || x >= 1 || y >= 1) continue;
            at[j] = (int)(y * H) * W + (int)(x * W);
            cell[at[j]] += buf[j * 16 + 13] * ThinAlpha(buf[j * 16]) ;
        }
        for (int j = 0; j < count; j++)
            if (at[j] >= 0) buf[j * 16 + 13] *= Math.Min(1f, ThinBudget / Math.Max(cell[at[j]], 1e-3f));
    }

    private const float ThinBudget = 0.4f;
    private static float ThinAlpha(float size) => Math.Clamp(size * 0.5f, 0.3f, 3f);   // a bigger puff covers more of its cell

    /// <summary>How strongly the lamps' light shows in smoke (I76_SMOKE_LIGHT); a headlamp's energy is 14 for the road.</summary>
    private static readonly float LightGain = float.TryParse(System.Environment.GetEnvironmentVariable("I76_SMOKE_LIGHT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lg) ? lg : 0.2f;

    private static void Write(Projection p, Span<float> to)
    {
        to[0] = p.X.X; to[1] = p.X.Y; to[2] = p.X.Z; to[3] = p.X.W;
        to[4] = p.Y.X; to[5] = p.Y.Y; to[6] = p.Y.Z; to[7] = p.Y.W;
        to[8] = p.Z.X; to[9] = p.Z.Y; to[10] = p.Z.Z; to[11] = p.Z.W;
        to[12] = p.W.X; to[13] = p.W.Y; to[14] = p.W.Z; to[15] = p.W.W;
    }

    private static bool Setup(RenderingDevice rd)
    {
        // two builds of the fragment shader: the scene's depth single-sampled, and multisampled (MSAA). A backend may refuse one
        // (its shader made from the SPIR-V is no shader): without the single-sampled one there is no layer, without the
        // multisampled one the layer reads the engine's resolved depth under MSAA, as it did before
        for (int v = 0; v < 2; v++)
        {
            var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceVertex = Vert, SourceFragment = v == 0 ? Frag : Frag.Replace("#version 450", "#version 450\n#define MSAA") };
            var spirv = rd.ShaderCompileSpirVFromSource(src);
            string error = $"{spirv.CompileErrorVertex} {spirv.CompileErrorFragment}".Trim();
            if (error.Length == 0) _shaders[v] = rd.ShaderCreateFromSpirV(spirv);
            if (_shaders[v].IsValid) continue;
            GD.PrintErr($"smoke layer: the {(v == 1 ? "multisampled" : "single-sampled")} depth's shader was not made: {(error.Length > 0 ? error : "the backend refused it")}");
            if (v == 1) { _msaaOk = false; continue; }
            _failed = true;
            return false;
        }
        _sampler = rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest, RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge });
        _noiseSampler = rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear, RepeatU = RenderingDevice.SamplerRepeatMode.Repeat, RepeatV = RenderingDevice.SamplerRepeatMode.Repeat });
        _ubo = rd.UniformBufferCreate(UboFloats * 4);
        _ssbo = rd.StorageBufferCreate((uint)(Capacity * Floats * 4));
        return true;
    }

    private static void MakeTargets(RenderingDevice rd, Vector2I size)
    {
        if (_fb.IsValid) rd.FreeRid(_fb);
        if (_t0Rd.IsValid) rd.FreeRid(_t0Rd);
        if (_t1Rd.IsValid) rd.FreeRid(_t1Rd);
        var fmt = new RDTextureFormat
        {
            Width = (uint)size.X, Height = (uint)size.Y, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.ColorAttachmentBit | RenderingDevice.TextureUsageBits.SamplingBit,
        };
        _t0Rd = rd.TextureCreate(fmt, new RDTextureView());
        _t1Rd = rd.TextureCreate(fmt, new RDTextureView());
        _fb = rd.FramebufferCreate([_t0Rd, _t1Rd]);
        var fbFormat = rd.FramebufferGetFormat(_fb);
        if (fbFormat != _fbFormat || !_pipelines[0].IsValid)
        {
            var blend = new RDPipelineColorBlendState();
            for (int i = 0; i < 2; i++)
                blend.Attachments.Add(new RDPipelineColorBlendStateAttachment
                {
                    EnableBlend = true,
                    SrcColorBlendFactor = RenderingDevice.BlendFactor.One, DstColorBlendFactor = RenderingDevice.BlendFactor.OneMinusSrcAlpha, ColorBlendOp = RenderingDevice.BlendOperation.Add,
                    SrcAlphaBlendFactor = RenderingDevice.BlendFactor.One, DstAlphaBlendFactor = RenderingDevice.BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = RenderingDevice.BlendOperation.Add,
                });
            for (int v = 0; v < 2; v++)
            {
                if (_pipelines[v].IsValid) rd.FreeRid(_pipelines[v]);
                _pipelines[v] = _shaders[v].IsValid ? rd.RenderPipelineCreate(_shaders[v], fbFormat, -1, RenderingDevice.RenderPrimitive.TriangleStrips, new RDPipelineRasterizationState(), new RDPipelineMultisampleState(), new RDPipelineDepthStencilState(), blend) : default;
            }
            if (!_pipelines[1].IsValid && _msaaOk) { _msaaOk = false; GD.PrintErr("smoke layer: no pipeline for the multisampled depth's shader; the engine's resolved depth then"); }
            _fbFormat = fbFormat;
        }
        _size = size;
        _cleared = false;
        _rid0 = _t0Rd; _rid1 = _t1Rd;
        _version++;
    }
}

/// <summary>
/// How the smoke layer reads the scene's depth: each of its pixels (1/Scale of the scene's size each way, rounded down) takes
/// the nearest surface over the scene texels it covers, so the layer's pixels between them leave out no scene texel at the
/// right or bottom edge whatever the size, and the projection says which way the depth runs.
/// </summary>
public static class SmokeDepth
{
    /// <summary>The scene texels (first, last) along one axis that layer pixel <paramref name="px"/> covers, as the layer's shader takes them.</summary>
    public static (int Lo, int Hi) Footprint(int px, int layer, int scene)
    {
        double k = (double)scene / layer;
        int lo = Math.Clamp((int)Math.Floor(px * k), 0, scene - 1);
        int hi = Math.Clamp((int)Math.Ceiling((px + 1) * k) - 1, lo, scene - 1);
        return (lo, hi);
    }

    /// <summary>The scene texels the shader reads in a footprint along one axis (three, spread from first to last).</summary>
    public static int[] Taps(int lo, int hi) => [lo + ((hi - lo) * 0 + 1) / 2, lo + ((hi - lo) * 1 + 1) / 2, lo + ((hi - lo) * 2 + 1) / 2];

    /// <summary>The samples a scene texel has under the viewport's MSAA, which the layer reads for the nearest (1: the depth is single-sampled).</summary>
    public static int Samples(RenderingServer.ViewportMsaa msaa) => msaa switch
    {
        RenderingServer.ViewportMsaa.Msaa2X => 2,
        RenderingServer.ViewportMsaa.Msaa4X => 4,
        RenderingServer.ViewportMsaa.Msaa8X => 8,
        _ => 1,
    };

    /// <summary>
    /// The samples to read: the viewport's MSAA's, but no more than the depth texture was made with (<paramref name="made"/>, the
    /// texture's <see cref="RenderingDevice.TextureSamples"/>): the backends make fewer than asked where the GPU has no more for the
    /// format, and a sample past them is undefined.
    /// </summary>
    public static int Samples(RenderingServer.ViewportMsaa msaa, int made) => Math.Min(Samples(msaa), 1 << Math.Clamp(made, 0, 6));

    /// <summary>Whether a larger depth is nearer the eye under <paramref name="proj"/> (the reversed z of Godot's Forward+ projection).</summary>
    public static bool NearerIsLarger(Projection proj)
    {
        var a = proj * new Vector4(0, 0, -1, 1);
        var b = proj * new Vector4(0, 0, -2, 1);
        return a.Z / a.W > b.Z / b.W;
    }
}

/// <summary>
/// One smoke's puffs for the rear-view mirror: the mirror has a camera of its own, which the smoke layer's compositor does
/// not run on, so while the layer draws the smoke for the main view each smoke also draws itself plainly, on a layer only the
/// mirror's camera shows (SmokeLayer.MirrorLayer), with the lamps' light and the overlap thinned (SmokeLayer.Thin).
/// </summary>
public sealed class MirrorDraw
{
    private readonly MultiMesh _mm;
    private readonly ShaderMaterial _mat;
    private readonly float[] _buf;
    private readonly Node3D _node;

    public MirrorDraw(Node parent, int capacity, System.Numerics.Vector3 tint)
    {
        _buf = new float[capacity * 16];
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/damage_smoke_modern.gdshader") };
        _mat.SetShaderParameter("noise_tex", DamageSmoke.Noise);
        _mat.SetShaderParameter("tint", new Vector3(tint.X, tint.Y, tint.Z));
        _mat.SetShaderParameter("a_cap", 0.35f);
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _mat }, InstanceCount = capacity, VisibleInstanceCount = 0,
        };
        _node = new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1000, Name = "MirrorBillows", Layers = SmokeLayer.MirrorLayer };
        parent.AddChild(_node);
    }

    /// <summary>The puffs this frame (the MultiMesh buffer layout, far to near), as the mirror's camera sees them.</summary>
    public void Update(float[] src, int count, float sceneLight)
    {
        var cam = SmokeLayer.MirrorCamera;
        if (cam is null || !GodotObject.IsInstanceValid(cam) || count == 0 || (cam.GetViewport() as SubViewport)?.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled)
        {
            _mm.VisibleInstanceCount = 0;
            return;
        }
        count = Math.Min(count, _buf.Length / 16);
        // the mirror looks backward: its far-to-near order is the reverse of the main camera's more often than not, so sort by its own depth
        var ct = cam.GlobalTransform;
        var key = new float[count]; var idx = new int[count];
        for (int j = 0; j < count; j++)
        {
            key[j] = ct.Basis.Z.Dot(new Vector3(src[j * 16 + 3], src[j * 16 + 7], src[j * 16 + 11]) - ct.Origin);   // -depth along the view
            idx[j] = j;
        }
        Array.Sort(key, idx);
        for (int j = 0; j < count; j++) Array.Copy(src, idx[j] * 16, _buf, j * 16, 16);
        SmokeLayer.Thin(_buf, count, cam);
        _mat.SetShaderParameter("scene_light", sceneLight);
        _sun ??= _node.GetTree().Root.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>().FirstOrDefault();
        var toSun = _sun is null ? new Vector3(0.3f, 0.8f, 0.5f) : _sun.GlobalTransform.Basis.Z;
        _mat.SetShaderParameter("sun_view", ct.Basis.Inverse() * toSun);
        SmokeLayer.Lights(_mat);
        _mm.Buffer = _buf;
        _mm.VisibleInstanceCount = count;
    }

    private DirectionalLight3D? _sun;
}
