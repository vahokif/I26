using Godot;
using I76.Sim.Views;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's motion blur (Graphic Detail's Motion Blur, on by default; the original had none): a CompositorEffect on
/// the main camera, after the transparent pass and before the tonemap, so the HUD, the 2D cockpit, the menus and the map,
/// all drawn over the 3D view, are never blurred. The 1997 look and the Compatibility renderer have none.
///
/// It reads the engine's motion vectors (each surface's move on the screen since the last frame, the camera's own and the
/// object's; a car's cabin moves with the camera and has none) and the depth. The sky has no vectors of its own: its move is
/// the camera's turn alone, from the two frames' cameras. A surface whose vector is far from what the camera's move gives it
/// has jumped a long way (a car put back on the road) and takes the camera's. The vectors are scaled to a fixed shutter
/// (<see cref="MotionBlurRules.Scale"/>: a whole 60 fps frame, whatever the frame rate), shortened by a pixel (what crawls stays sharp) and clamped to
/// <see cref="MotionBlurRules.DefaultMaxLength"/> of the view's height.
///
/// Then the reconstruction filter of McGuire, Hennessy, Bukowski and Osman ("A Reconstruction Filter for Plausible Motion
/// Blur", I3D 2012), written for this: the largest motion in each tile and its neighbours', and a gather of a few samples
/// per pixel along it and along the pixel's own, weighed so that a sharp near thing does not smear into a moving far one
/// and a moving near thing smears over a still far one; where everything near streaks about as far (the road and the
/// scenery as the car drives) a plain average along the pixel's own streak. The streaks are worked out at half the view's
/// size each way (a quarter of the pixels, about half the cost: about 1 ms at 1080p on an integrated GPU at speed) and laid
/// over the full-size picture by each pixel's own share, so what does not move (the player's car in the chase view, the
/// cabin) keeps every pixel. A frame whose longest streak is under a pixel is left alone (the blur's passes are sized on
/// the GPU to nothing), and while the camera stands still with nothing moving the effect rests altogether (no motion
/// vectors, no passes), looking again one frame in eight.
///
/// No blur in the frame after a cut (<see cref="Cut"/>: a new camera, a view key, the script camera's cut, a teleport, the
/// floating origin's rebase, the start of a mission) or when the camera jumps, turns or zooms too far in one frame
/// (<see cref="MotionBlurRules.IsCut"/>), nor in a frame after one the effect did not draw.
///
/// I76_MOTION_BLUR=0/1 overrides the setting; I76_MOTION_BLUR_SHUTTER (fraction of a 60 fps frame), I76_MOTION_BLUR_MAX
/// (fraction of the height) and I76_MOTION_BLUR_SAMPLES tune it, I76_MOTION_BLUR_FULL=1 works at full size; I76_MOTION_BLUR_VIEW=1 shows each pixel's motion (red and
/// green across and down, a full streak bright), 2 how far the engine's vectors are from the camera's move alone.
/// </summary>
public partial class MotionBlur : Node
{
    /// <summary>The setting, for the modern look: Graphic Detail's Motion Blur (or I76_MOTION_BLUR), set by Main.</summary>
    public static bool Setting { get; set; } = true;
    /// <summary>The 1997 look is up: no blur, whatever the setting.</summary>
    public Func<bool>? Faithful { get; init; }

    /// <summary>The next frame drawn is a cut: no blur in it (the frame's camera has nothing to blend from).</summary>
    public static void Cut() => System.Threading.Interlocked.Increment(ref _cuts);

    private static readonly float Shutter = Env("I76_MOTION_BLUR_SHUTTER", MotionBlurRules.DefaultShutter, 0f, 4f);
    private static readonly float MaxLength = Env("I76_MOTION_BLUR_MAX", MotionBlurRules.DefaultMaxLength, 0f, 0.2f);
    private static readonly int Samples = (int)Env("I76_MOTION_BLUR_SAMPLES", 8, 2, 64);
    private static readonly int View = (int)Env("I76_MOTION_BLUR_VIEW", 0, 0, 2);
    /// <summary>The streaks are worked out at half the 3D view's size each way and laid over it at full size (I76_MOTION_BLUR_FULL=1: at full size).</summary>
    private static readonly int Scale = System.Environment.GetEnvironmentVariable("I76_MOTION_BLUR_FULL") == "1" ? 1 : 2;
    /// <summary>A vector this long and this far from the camera's move alone (in screen widths and heights, per frame) is a jump, not motion.</summary>
    private const float JumpUv = 0.25f;
    /// <summary>Each streak is this much shorter (view pixels), so what crawls is not smeared, and a frame whose longest streak is shorter than this is left alone.</summary>
    private const float DeadPx = 1f;

    private static float Env(string name, float def, float lo, float hi) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? Math.Clamp(v, lo, hi) : def;

    // ---- main thread ----
    private Effect? _effect;
    private Compositor? _own, _attached;
    private Camera3D? _cam;
    private static int _cuts, _frame;
    private static float _dt = 1f / 60f;
    private static volatile bool _failed;
    // Idle: while the camera is still and the longest streak the effect last measured is under a pixel (standing or crawling),
    // the effect is off (no motion vectors drawn, no passes), but for one frame in eight, which measures again
    private const int IdleAfter = 8, IdleProbe = 8;
    private static volatile float _measured = float.MaxValue;
    private static volatile bool _gapOk;
    private int _quiet;
    private bool _wasIdle;
    private Transform3D _camAt;
    private float _camFov;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;   // counts the frames under the pause too (the pad's menu), as they are drawn
        Cut();
        if (RenderingServer.GetCurrentRenderingMethod() != "forward_plus" || RenderingServer.GetRenderingDevice() is null) { _failed = true; return; }
        _effect = new Effect();
        GetTree().NodeRemoved += Removed;
        RenderingServer.FramePreDraw += PreDraw;
    }

    // The engine keeps each object's last transform for its motion vector, stamped with the frame it was set in. Freeing a 3D
    // object between two frames drawn (a chunk of terrain or plants streamed out, a spent effect) makes the engine take in
    // every move made so far in that frame early, under the last frame's stamp, and the frame drawn then gives everything that
    // moved (the player's car and cabin, the other cars) no motion of its own: the cabin would streak with the road. In such
    // a frame the effect draws with the last frame's motion instead, which is a frame old but right for what moves.
    private static bool _removed;
    private static volatile bool _stale;
    private static void Removed(Node n) { if (n is VisualInstance3D) _removed = true; }
    private static void PreDraw() { _stale = _removed; _removed = false; }

    public override void _Process(double delta)
    {
        if (_effect is null) return;
        var cam = GetViewport().GetCamera3D();
        if (cam != _cam) { _cam = cam; Cut(); }
        bool on = Setting && !_failed && Faithful?.Invoke() != true && cam is not null;
        _dt = (float)delta;
        System.Threading.Interlocked.Increment(ref _frame);
        if (cam is not null)
        {
            var at = cam.GlobalTransform;
            float dt = Math.Max((float)delta, 1e-3f);
            bool still = at.Origin.DistanceTo(_camAt.Origin) < 0.5f * dt && (-at.Basis.Z).AngleTo(-_camAt.Basis.Z) < 0.05f * dt
                && at.Basis.Y.AngleTo(_camAt.Basis.Y) < 0.05f * dt && MathF.Abs(cam.Fov - _camFov) < 0.01f;
            _quiet = still && _measured < DeadPx ? _quiet + 1 : 0;
            _camAt = at; _camFov = cam.Fov;
        }
        bool idle = on && MotionBlurRules.Idle(_quiet, IdleAfter) && _frame % IdleProbe != 0;
        // the first frame drawn after idle ones (a probe, or the camera moving off): the frames between were still, nothing to cut
        _gapOk = _wasIdle;
        _wasIdle = idle;
        _effect.Enabled = on && !idle;
        if (!on || idle || cam is null) return;
        // the camera's compositor: the smoke layer's, if it has put one there, with this effect added; else one of our own
        var comp = cam.Compositor;
        if (comp is null) cam.Compositor = comp = _own ??= new Compositor();
        if (comp != _attached)
        {
            var fx = comp.CompositorEffects;
            if (!fx.Contains(_effect)) { fx.Add(_effect); comp.CompositorEffects = fx; }
            _attached = comp;
        }
    }

    public override void _ExitTree()
    {
        if (_effect is null) return;
        _effect.Enabled = false;
        GetTree().NodeRemoved -= Removed;
        RenderingServer.FramePreDraw -= PreDraw;
        RenderingServer.CallOnRenderThread(Callable.From(Release));   // the textures and what was made with them (the uniform sets the engine cached)
    }

    // ---- render thread ----

    private sealed partial class Effect : CompositorEffect
    {
        public Effect()
        {
            EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
            NeedsMotionVectors = true;
            AccessResolvedColor = true;
            AccessResolvedDepth = true;
            Enabled = false;
        }

        public override void _RenderCallback(int effectCallbackType, RenderData renderData)
        {
            if (_failed) return;
            try { Render(renderData); }
            catch (Exception e) { _failed = true; GD.PrintErr($"motion blur: {e}"); }
        }
    }

    private const int UboFloats = 16 * 3 + 4 * 4;
    private static readonly float[] Ubo = new float[UboFloats];
    private static readonly byte[] UboBytes = new byte[UboFloats * 4];
    private static bool _hasPrev, _told;
    private static int _lastFrame, _seenCuts, _motionOf = -1;
    private static Transform3D _prevT;
    private static Projection _prevProj;

    private static void Render(RenderData renderData)
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd is null || renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers || renderData.GetRenderSceneData() is not RenderSceneDataRD scene) return;
        if (buffers.GetViewCount() != 1) return;
        var camT = scene.GetCamTransform();
        var proj = Corrected(scene.GetViewProjection(0));
        int frame = _frame, cuts = _cuts;
        float dt = _dt;
        // a cut, the first frame, a frame after one not drawn here: nothing to blend from; this frame's camera is kept for the next
        bool cut = !_hasPrev || (frame != _lastFrame + 1 && !_gapOk) || cuts != _seenCuts || MotionBlurRules.IsCut(
            V(_prevT.Origin), V(-_prevT.Basis.Z.Normalized()), V(_prevT.Basis.Y.Normalized()), MathF.Abs(_prevProj.Y.Y),
            V(camT.Origin), V(-camT.Basis.Z.Normalized()), V(camT.Basis.Y.Normalized()), MathF.Abs(proj.Y.Y), dt);
        var prevT = _prevT;
        var prevProj = _prevProj;
        _prevT = camT; _prevProj = proj; _hasPrev = true; _lastFrame = frame; _seenCuts = cuts;
        if (cut) return;
        var size = buffers.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0) return;
        var color = buffers.GetColorLayer(0, false);
        var depth = buffers.GetDepthLayer(0, false);
        var velocity = buffers.GetVelocityLayer(0, false);
        if (!color.IsValid || !depth.IsValid || !velocity.IsValid) return;
        if (!_shaders[0].IsValid && !Setup(rd)) return;
        if (rd.TextureGetFormat(color)?.Format != RenderingDevice.DataFormat.R16G16B16A16Sfloat)
        {
            if (!_told) { _told = true; GD.PrintErr($"motion blur: the scene's colour is {rd.TextureGetFormat(color)?.Format}, not RGBA16F; no blur"); }
            return;
        }
        // the size worked at (half the view's each way, or all of it), in whose pixels the streaks are
        var work = new Vector2I((size.X + Scale - 1) / Scale, (size.Y + Scale - 1) / Scale);
        float maxPx = MotionBlurRules.MaxPixels(work.Y, MaxLength);
        // a tile is at least as wide as the longest streak's half, so the 3x3 tiles round a pixel hold everything that can reach it
        int tile = 16 * Math.Clamp((int)MathF.Ceiling(maxPx * 0.5f / 16f), 1, 4);
        var tiles = new Vector2I((work.X + tile - 1) / tile, (work.Y + tile - 1) / tile);
        if (work != _size || tiles != _tiles || !_packed.IsValid) MakeTargets(rd, work, tiles);

        // the camera's move: this frame's view to the last frame's clip space, and the same with the turn alone (the sky)
        var inv = proj.Inverse();
        var prevView = prevT.AffineInverse();
        var reproj = prevProj * new Projection(prevView * camT) * inv;
        var reprojSky = prevProj * new Projection(new Transform3D(prevView.Basis * camT.Basis, Vector3.Zero)) * inv;
        Write(reproj, 0); Write(reprojSky, 16); Write(inv, 32);
        Ubo[48] = size.X; Ubo[49] = size.Y; Ubo[50] = 1f / size.X; Ubo[51] = 1f / size.Y;
        Ubo[52] = MotionBlurRules.Scale(dt, Shutter); Ubo[53] = maxPx; Ubo[54] = SmokeDepth.NearerIsLarger(proj) ? 0f : 1f; Ubo[55] = JumpUv;
        Ubo[56] = tile; Ubo[57] = Samples; Ubo[58] = View; Ubo[59] = Scale;
        Ubo[60] = DeadPx / Scale; Ubo[61] = View > 0 ? -1f : DeadPx / Scale;
        System.Buffer.BlockCopy(Ubo, 0, UboBytes, 0, UboBytes.Length);
        rd.BufferUpdate(_ubo, 0, (uint)UboBytes.Length, UboBytes);

        if (!_stale)
        {
            rd.BufferUpdate(_gbuf, 0, 4, Zero4);   // the frame's longest streak, worked out again
            Dispatch(rd, 0, [Ubuf(0), Tex(1, velocity), Tex(2, depth), Img(3, _packed), Img(4, _tile)], (uint)tiles.X, (uint)tiles.Y);
            Dispatch(rd, 1, [Ubuf(0), Tex(1, _tile), Img(2, _near), Sbuf(3)], (uint)(tiles.X + 7) / 8, (uint)(tiles.Y + 7) / 8);
            // the next two passes' sizes, none when the longest streak is under a pixel (standing still, crawling)
            Dispatch(rd, 4, [Ubuf(0), Sbuf(1)], 1, 1);
            rd.BufferGetDataAsync(_gbuf, Measured, 0, 4);
            _motionOf = frame;
        }
        else if (frame - _motionOf > 3) return;   // the last motion worked out (see Removed), for up to three frames running
        Dispatch(rd, 2, [Ubuf(0), Tex(1, color, true), Tex(2, _packed), Tex(3, _near), Img(4, _out)], 4);
        Dispatch(rd, 3, [Ubuf(0), Tex(1, _out, true), Tex(2, _near), Img(3, color)], 16);
    }

    private static readonly byte[] Zero4 = new byte[4];
    private static readonly Callable Measured = Callable.From<byte[]>(b => { if (b.Length >= 4) _measured = BitConverter.ToSingle(b, 0) * Scale; });

    private static System.Numerics.Vector3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The projection as the scene's shaders apply it (y down, depth reversed into 0..1), which the motion vectors and the depth are in.</summary>
    private static Projection Corrected(Projection p)
    {
        if (p.Y.Y < 0) return p;   // already
        var c = Projection.Identity;
        c.Y = new Vector4(0, -1, 0, 0);
        c.Z = new Vector4(0, 0, -0.5f, 0);
        c.W = new Vector4(0, 0, 0.5f, 1);
        return c * p;
    }

    private static void Write(Projection p, int at)
    {
        var to = Ubo.AsSpan(at, 16);
        to[0] = p.X.X; to[1] = p.X.Y; to[2] = p.X.Z; to[3] = p.X.W;
        to[4] = p.Y.X; to[5] = p.Y.Y; to[6] = p.Y.Z; to[7] = p.Y.W;
        to[8] = p.Z.X; to[9] = p.Z.Y; to[10] = p.Z.Z; to[11] = p.Z.W;
        to[12] = p.W.X; to[13] = p.W.Y; to[14] = p.W.Z; to[15] = p.W.W;
    }

    private static void Dispatch(RenderingDevice rd, int pass, RDUniform[] uniforms, uint x, uint y) => Dispatch(rd, pass, uniforms, x, y, -1);

    /// <summary>A pass sized by the buffer the sizes pass wrote, at that byte offset.</summary>
    private static void Dispatch(RenderingDevice rd, int pass, RDUniform[] uniforms, int argsAt) => Dispatch(rd, pass, uniforms, 0, 0, argsAt);

    private static void Dispatch(RenderingDevice rd, int pass, RDUniform[] uniforms, uint x, uint y, int argsAt)
    {
        var set = UniformSetCacheRD.GetCache(_shaders[pass], 0, new Godot.Collections.Array<RDUniform>(uniforms));
        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipelines[pass]);
        rd.ComputeListBindUniformSet(list, set, 0);
        if (argsAt >= 0) rd.ComputeListDispatchIndirect(list, _gbuf, (uint)argsAt);
        else rd.ComputeListDispatch(list, x, y, 1);
        rd.ComputeListEnd();
    }

    private static RDUniform Sbuf(int binding)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding };
        u.AddId(_gbuf);
        return u;
    }

    private static RDUniform Ubuf(int binding)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = binding };
        u.AddId(_ubo);
        return u;
    }

    private static RDUniform Tex(int binding, Rid tex, bool linear = false)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = binding };
        u.AddId(linear ? _linear : _sampler); u.AddId(tex);
        return u;
    }

    private static RDUniform Img(int binding, Rid tex)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(tex);
        return u;
    }

    private static readonly Rid[] _shaders = new Rid[5], _pipelines = new Rid[5];
    // _gbuf: the longest streak (float bits), then the gather's and the composite's dispatch sizes
    private static Rid _sampler, _linear, _ubo, _gbuf, _packed, _tile, _near, _out;
    private static Vector2I _size, _tiles;

    private static bool Setup(RenderingDevice rd)
    {
        string[] names = ["motion", "tiles", "gather", "composite", "sizes"];
        string[] code = [Motion, Neighbours, Gather, Composite, Sizes];
        for (int i = 0; i < code.Length; i++)
        {
            var spirv = rd.ShaderCompileSpirVFromSource(new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = Common + code[i] });
            if (spirv.CompileErrorCompute.Length == 0) _shaders[i] = rd.ShaderCreateFromSpirV(spirv);
            if (_shaders[i].IsValid) _pipelines[i] = rd.ComputePipelineCreate(_shaders[i]);
            if (_pipelines[i].IsValid) continue;
            GD.PrintErr($"motion blur: the {names[i]} shader was not made: {(spirv.CompileErrorCompute.Length > 0 ? spirv.CompileErrorCompute : "the backend refused it")}");
            _failed = true;
            return false;
        }
        _sampler = rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest, RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge });
        _linear = rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear, RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge });
        _ubo = rd.UniformBufferCreate(UboFloats * 4);
        _gbuf = rd.StorageBufferCreate(28, new byte[28], RenderingDevice.StorageBufferUsage.Indirect);
        GD.Print($"motion blur: on, shutter {Shutter} of a 60 fps frame, at most {MaxLength * 100:0.#} % of the height, {Samples} samples, {(Scale == 1 ? "full" : "half")} size");
        return true;
    }

    /// <summary>Frees what the effect made; the next mission's first frame makes it again.</summary>
    private static void Release()
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd is null) return;
        foreach (var t in new[] { _packed, _tile, _near, _out, _ubo, _gbuf, _sampler, _linear }) if (t.IsValid) rd.FreeRid(t);
        for (int i = 0; i < _shaders.Length; i++) if (_shaders[i].IsValid) rd.FreeRid(_shaders[i]);   // its pipeline with it
        _packed = _tile = _near = _out = _ubo = _gbuf = _sampler = _linear = default;
        Array.Clear(_shaders); Array.Clear(_pipelines);
        _hasPrev = false;
    }

    private static void MakeTargets(RenderingDevice rd, Vector2I size, Vector2I tiles)
    {
        foreach (var t in new[] { _packed, _tile, _near, _out }) if (t.IsValid) rd.FreeRid(t);
        Rid Make(Vector2I s, RenderingDevice.DataFormat f) => rd.TextureCreate(new RDTextureFormat
        {
            Width = (uint)s.X, Height = (uint)s.Y, Format = f,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit,
        }, new RDTextureView());
        _packed = Make(size, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _out = Make(size, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _tile = Make(tiles, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _near = Make(tiles, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _size = size; _tiles = tiles;
        _motionOf = -1;
    }

    // ---- the shaders ----

    private const string Common = """
        #version 450
        layout(set = 0, binding = 0, std140) uniform U {
            mat4 reproj;       // this frame's ndc (x, y down, depth) to the last frame's clip space: the camera's move
            mat4 reproj_sky;   // the same with the camera's turn alone, for the sky
            mat4 inv_proj;     // this frame's ndc to view space
            vec4 size;         // the 3D view's width, height, 1 / width, 1 / height
            vec4 a;            // the shutter over the frame, the longest streak (work px), the sky's depth, a jump (uv a frame)
            vec4 b;            // tile (work px), samples, view (0, 1 the motion, 2 the vectors' distance from the camera's), scale
            vec4 c;            // each streak shortened by (work px), the longest streak a frame is blurred for (work px; < 0: always)
        } u;
        // the size worked at: the view's, or half of it each way
        ivec2 work_size() { int s = int(u.b.w); return (ivec2(u.size.xy) + s - 1) / s; }

        """;

    // a pixel's streak (work px, scaled to the shutter, clamped) and distance, and the longest and shortest streaks of each tile;
    // at half size a work pixel takes the nearest surface of the four it covers
    private const string Motion = """
        layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;
        layout(set = 0, binding = 1) uniform sampler2D vel_tex;
        layout(set = 0, binding = 2) uniform sampler2D depth_tex;
        layout(set = 0, binding = 3, rgba16f) uniform restrict writeonly image2D packed_img;
        layout(set = 0, binding = 4, rgba16f) uniform restrict writeonly image2D tile_img;
        shared vec2 s_v[256];
        shared float s_l[256], s_m[256];
        void main() {
            int k = int(u.b.x) / 16, sc = int(u.b.w);
            ivec2 sz = ivec2(u.size.xy), wsz = work_size();
            ivec2 base = ivec2(gl_WorkGroupID.xy) * int(u.b.x) + ivec2(gl_LocalInvocationID.xy) * k;
            bool reversed = u.a.z < 0.5;
            vec2 best = vec2(0.0);
            float bl = -1.0, least = 1e9;
            for (int j = 0; j < k; j++)
                for (int i = 0; i < k; i++) {
                    ivec2 p = base + ivec2(i, j);
                    if (p.x >= wsz.x || p.y >= wsz.y) continue;
                    ivec2 f = min(p * sc, sz - 1);
                    float d = texelFetch(depth_tex, f, 0).r;
                    for (int q = 1; q < sc * sc; q++) {
                        ivec2 g = min(p * sc + ivec2(q % sc, q / sc), sz - 1);
                        float e = texelFetch(depth_tex, g, 0).r;
                        if (reversed ? e > d : e < d) { d = e; f = g; }
                    }
                    vec2 ndc = (vec2(f) + 0.5) * u.size.zw * 2.0 - 1.0;
                    bool sky = d == u.a.z;
                    vec2 vb = texelFetch(vel_tex, f, 0).xy;   // the engine's: the last frame's place minus this one's, in uv
                    vec4 pc = (sky ? u.reproj_sky : u.reproj) * vec4(ndc, d, 1.0);
                    vec2 vc = pc.w > 1e-6 ? (pc.xy / pc.w - ndc) * 0.5 : vec2(0.0);
                    // the sky has no vectors: the camera's turn. A surface whose own vector is a long way and far from the camera's
                    // move has jumped (a car put back on the road): the camera's move. (Not one that moves less than the camera's
                    // move says: the cabin and its mirror, which travel with the camera.)
                    float off = length(vb - vc);
                    vec2 v = sky || (off > u.a.w && length(vb) > u.a.w) ? vc : vb;
                    vec2 px = v * vec2(wsz) * u.a.x;
                    float l = length(px);
                    px *= l > u.c.x ? min(l - u.c.x, u.a.y) / l : 0.0;   // shortened: what crawls stays sharp
                    vec4 vp = u.inv_proj * vec4(ndc, d, 1.0);
                    float z = sky ? 60000.0 : min(-vp.z / vp.w, 60000.0);
                    imageStore(packed_img, p, vec4(px, z, sky ? 0.0 : off * u.size.y));
                    float l2 = dot(px, px);
                    if (l2 > bl) { bl = l2; best = px; }
                    least = min(least, l2);
                }
            uint li = gl_LocalInvocationIndex;
            s_v[li] = best; s_l[li] = bl; s_m[li] = least;
            barrier();
            for (uint s = 128u; s > 0u; s >>= 1) {
                if (li < s) {
                    if (s_l[li + s] > s_l[li]) { s_l[li] = s_l[li + s]; s_v[li] = s_v[li + s]; }
                    s_m[li] = min(s_m[li], s_m[li + s]);
                }
                barrier();
            }
            // the tile's longest streak, and its shortest's length
            if (li == 0u) imageStore(tile_img, ivec2(gl_WorkGroupID.xy), vec4(s_v[0], sqrt(s_m[0]), 0.0));
        }
        """;

    // the longest streak over each tile and its eight neighbours, and the shortest's length
    private const string Neighbours = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        layout(set = 0, binding = 1) uniform sampler2D tile_tex;
        layout(set = 0, binding = 2, rgba16f) uniform restrict writeonly image2D near_img;
        layout(set = 0, binding = 3, std430) restrict buffer G { uint longest; uint sizes[6]; } g;
        void main() {
            ivec2 t = ivec2(gl_GlobalInvocationID.xy), n = textureSize(tile_tex, 0);
            if (t.x >= n.x || t.y >= n.y) return;
            atomicMax(g.longest, floatBitsToUint(length(texelFetch(tile_tex, t, 0).xy)));   // the frame's longest streak
            vec2 best = vec2(0.0);
            float bl = -1.0, least = 1e9;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++) {
                    vec3 v = texelFetch(tile_tex, clamp(t + ivec2(x, y), ivec2(0), n - 1), 0).xyz;
                    float l = dot(v.xy, v.xy);
                    if (l > bl) { bl = l; best = v.xy; }
                    least = min(least, v.z);
                }
            imageStore(near_img, t, vec4(best, least, 0.0));
        }
        """;

    // the reconstruction: samples along the neighbourhood's longest streak and the pixel's own, each weighed by whether it
    // is in front and reaches the pixel, or behind and within the pixel's own streak (McGuire et al. 2012). It writes what the
    // samples other than the pixel itself add, premultiplied by their share, for the composite to lay over the full-size pixel.
    private const string Gather = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        layout(set = 0, binding = 1) uniform sampler2D color_tex;
        layout(set = 0, binding = 2) uniform sampler2D packed_tex;
        layout(set = 0, binding = 3) uniform sampler2D near_tex;
        layout(set = 0, binding = 4, rgba16f) uniform restrict writeonly image2D out_img;
        float cone(float d, float r) { return clamp(1.0 - d / max(r, 1e-3), 0.0, 1.0); }
        float cylinder(float d, float r) { return 1.0 - smoothstep(0.95 * r, 1.05 * r, d); }
        vec3 scene(ivec2 q, vec2 wsz) { return textureLod(color_tex, (vec2(q) + 0.5) / wsz, 0.0).rgb; }   // at half size, the four it covers
        void main() {
            ivec2 p = ivec2(gl_GlobalInvocationID.xy), sz = work_size();
            if (p.x >= sz.x || p.y >= sz.y) return;
            vec2 wsz = vec2(sz);
            vec4 c = texelFetch(packed_tex, p, 0);
            if (u.b.z > 0.5) {   // I76_MOTION_BLUR_VIEW
                imageStore(out_img, p, u.b.z < 1.5 ? vec4(abs(c.xy) / max(u.a.y, 1.0), 0.0, 1.0) : vec4(vec3(c.w / 8.0), 1.0));
                return;
            }
            vec3 near = texelFetch(near_tex, p / int(u.b.x), 0).xyz;
            vec2 nm = near.xy;
            float nl = length(nm);
            if (nl < 0.5) { imageStore(out_img, p, vec4(0.0)); return; }   // nothing moves near
            vec2 vc = c.xy;
            float zc = c.z, rc = 0.5 * length(vc);
            // a sample every two pixels of the streak, up to the most samples: a short streak, most of a frame's, needs few
            int n = clamp(int(ceil(nl * 0.5)), 2, int(u.b.y));
            // a pixel's own jitter (interleaved gradient noise, the same every frame: there is no TAA to settle it), so the samples' steps show as fine noise, not bands
            float jit = fract(52.9829189 * fract(dot(vec2(p), vec2(0.06711056, 0.00583715))));
            if (near.z > 0.5 * nl) {
                // everything near streaks about as far (the road and the scenery as the car drives): a plain average along the
                // pixel's own streak, with no weighing, which reads only the colour
                vec3 acc = vec3(0.0);
                n = clamp(int(ceil(rc)), 1, n);
                for (int i = 0; i < n; i++) {
                    float t = (float(i) + jit) / float(n) - 0.5;
                    acc += scene(clamp(ivec2(floor(vec2(p) + 0.5 + vc * t)), ivec2(0), sz - 1), wsz);
                }
                float share = float(n) / float(n + 1);
                imageStore(out_img, p, vec4(acc / float(n + 1), share));
                return;
            }
            float wc = 1.0 / max(rc, 0.5), w = wc;
            vec3 sum = vec3(0.0);
            vec2 own = rc > 0.25 ? vc : nm;
            for (int i = 0; i < n; i++) {
                float t = (float(i) + jit) / float(n) - 0.5;
                vec2 off = ((i & 1) == 0 ? nm : own) * t;
                ivec2 q = clamp(ivec2(floor(vec2(p) + 0.5 + off)), ivec2(0), sz - 1);
                float dist = length(vec2(q - p));
                if (dist < 0.5) continue;
                vec4 s = texelFetch(packed_tex, q, 0);
                float rs = 0.5 * length(s.xy);
                float soft = 0.05 * min(zc, s.z) + 0.05;
                float front = clamp(1.0 - (s.z - zc) / soft, 0.0, 1.0);   // the sample is in front of the pixel
                float back = clamp(1.0 - (zc - s.z) / soft, 0.0, 1.0);    // the sample is behind it
                float a = front * cone(dist, rs) + back * cone(dist, rc) + cylinder(dist, rs) * cylinder(dist, rc) * 2.0;
                sum += scene(q, wsz) * a;
                w += a;
            }
            imageStore(out_img, p, vec4(sum / w, 1.0 - wc / w));
        }
        """;

    // laid over the scene's colour, full size, where anything moved: the pixel's own colour for its share, the samples' for theirs
    private const string Composite = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        layout(set = 0, binding = 1) uniform sampler2D out_tex;
        layout(set = 0, binding = 2) uniform sampler2D near_tex;
        layout(set = 0, binding = 3, rgba16f) uniform restrict image2D color_img;
        void main() {
            ivec2 p = ivec2(gl_GlobalInvocationID.xy), sz = ivec2(u.size.xy);
            if (p.x >= sz.x || p.y >= sz.y) return;
            int sc = int(u.b.w);
            if (u.b.z < 0.5 && length(texelFetch(near_tex, p / sc / int(u.b.x), 0).xy) < 0.5) return;
            vec4 o = sc == 1 ? texelFetch(out_tex, p, 0) : textureLod(out_tex, (vec2(p) + 0.5) * u.size.zw, 0.0);
            vec4 c = imageLoad(color_img, p);
            imageStore(color_img, p, vec4(c.rgb * (1.0 - o.a) + o.rgb, c.a));
        }
        """;

    // the gather's and the composite's sizes, in work groups: none when the frame's longest streak is too short to blur
    private const string Sizes = """
        layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
        layout(set = 0, binding = 1, std430) restrict buffer G { uint longest; uint sizes[6]; } g;
        void main() {
            bool blur = uintBitsToFloat(g.longest) >= u.c.y;
            ivec2 w = (work_size() + 7) / 8, f = (ivec2(u.size.xy) + 7) / 8;
            g.sizes[0] = blur ? uint(w.x) : 0u; g.sizes[1] = uint(w.y); g.sizes[2] = 1u;
            g.sizes[3] = blur ? uint(f.x) : 0u; g.sizes[4] = uint(f.y); g.sizes[5] = 1u;
        }
        """;
}
