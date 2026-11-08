using Godot;
using I76.Formats.Assembly;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Game.Sim;

/// <summary>
/// One simulated car on screen: the body and wheel nodes, drawn interpolated
/// between the last two simulation states. Positions are game-frame doubles;
/// the node sits in floating-origin space, placed at (position − origin).
/// </summary>
public partial class CarNode : Node3D
{
    public Vehicle Sim { get; private set; } = null!;
    private Rows _prevRot, _rot;
    private Double3 _prevPos, _pos;
    private readonly List<(Node3D Node, Wheel Wheel, bool Front, System.Numerics.Vector3 Centre)> _wheels = [];
    /// <summary>Each strip-drawn wheel's texture-strip state, made when first drawn; its clock and the speed it reads.</summary>
    private readonly Dictionary<Node3D, WheelStrip> _strips = [];
    private float _stripClock, _stripSpeed;

    private Voice3D? _engine;

    /// <summary>
    /// The engine loop: the car's own engsnd.dat sound. Its pitch follows the
    /// engine's rpm as the original's (EngineSoundPitch).
    /// </summary>
    public void SetEngineSound(AudioStream? loop, bool player, bool revable = true)
    {
        if (loop is null) return;
        _engineRevable = revable;
        _engineRate = loop is AudioStreamWav wav && wav.MixRate > 0 ? wav.MixRate : 11025f;
        (_engineLoop, _enginePlayer) = (loop, player);
        // a DirectSound3D voice as the original's (a mode-1 loop, started at any range): min distance 10, held at
        // 400 m, parked past 600 m from the followed car (Voice3D). The player's own sits at the listener in the cockpit
        // (dead centre, GitHub #83); Godot's model cut a far helicopter's loop at 250 m, so it fell silent.
        _engine = new Voice3D { Stream = loop, Autoplay = true, BaseDb = -6, Name = "Engine" };
        AddChild(_engine);
    }

    private AudioStream? _engineLoop;
    private bool _engineRevable = true;
    private float _engineRate = 11025f;
    private bool _enginePlayer;

    /// <summary>The engine running again (a car renewed after its death: a network melee's respawn).</summary>
    public void RestartEngine()
    {
        if (_engine is null && _engineLoop is not null) SetEngineSound(_engineLoop, _enginePlayer, _engineRevable);
    }

    private Voice3D? _horn;

    /// <summary>A dead car's engine falls silent (sound event 2).</summary>
    public void StopEngine()
    {
        _engine?.Stop();
        _engine?.QueueFree();
        _engine = null;
    }

    /// <summary>
    /// The driver slumped on the wheel: the horn, looped, until the wreck timer stops it.
    /// A mode-1 voice on the car, so started at any range and heard as DirectSound3D heard it.
    /// </summary>
    public void StartHorn(AudioStream? horn)
    {
        if (horn is null || _horn is not null) return;
        _horn = new Voice3D { Stream = horn, Autoplay = true, BaseDb = -4 };
        AddChild(_horn);
    }

    public void StopHorn()
    {
        _horn?.Stop();
        _horn?.QueueFree();
        _horn = null;
    }

    public void OneShot(AudioStream? s, float db = 0)
    {
        if (s is null) return;
        var p = new AudioStreamPlayer3D { Bus = "SFX", Stream = s, UnitSize = 10, MaxDistance = 300, VolumeDb = db };
        AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }

    private readonly Dictionary<string, AudioStreamPlayer3D> _single = new();

    /// <summary>
    /// A one-shot of which only one voice plays at a time (the GPW header's "max voices" of -1 means 1):
    /// asked again while it still plays, the new one is dropped, so a held trigger on a broken weapon (wmiss.wav, asked every frame)
    /// sounds the file's own series of buzzes through, and starts it again when it ends.
    /// </summary>
    public void OneShotSingle(string key, AudioStream? s, float db = 0)
    {
        if (s is null) return;
        if (_single.TryGetValue(key, out var old) && IsInstanceValid(old) && old.Playing) return;
        var p = new AudioStreamPlayer3D { Bus = "SFX", Stream = s, UnitSize = 10, MaxDistance = 300, VolumeDb = db };
        AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
        _single[key] = p;
    }

    /// <summary>Stops a <see cref="OneShotSingle"/> voice that still plays (the radar jammer's activation stops clockon.wav).</summary>
    public void StopSingle(string key)
    {
        if (_single.Remove(key, out var p) && IsInstanceValid(p)) { p.Stop(); p.QueueFree(); }
    }

    /// <summary>A one-shot riding on this car, heard as DirectSound3D heard it from wherever the listener is (<see cref="Voice3D"/>).</summary>
    public void Voice(AudioStream? s, float minDistance = I76.Sim.Sound.Ds3d.MinDistance, float db = 0)
    {
        if (s is null) return;
        var p = Voice3D.OneShot(s, minDistance, db);
        AddChild(p);
        p.Start();
    }

    private MeshInstance3D? _body, _cockpit;
    private float _wreckGap;
    public Camera3D? CockpitCamera { get; private set; }

    /// <summary>Whether the car has a cockpit model; one without (the Armadillo) is driven from the chase view.</summary>
    public bool HasInterior { get; private set; }
    public MeshInstance3D? CockpitMesh => _cockpit;
    /// <summary>The render layer of the interior (the mirror's camera leaves it out).</summary>
    public const uint CockpitLayer = 4;
    /// <summary>Every visual under a car (body, wheels, lamps, weapons): ground decals (oil, scorch) leave this layer out, so they paint the road, not the cars on it.</summary>
    public const uint VehicleLayer = 1u << 5;
    private static bool _layerHook;

    public override void _EnterTree()
    {
        // one tree-wide hook: whatever is added under a car later (a swapped wheel, a weapon, the lamps) joins the layer too
        if (!_layerHook) { _layerHook = true; GetTree().NodeAdded += MarkVehicle; }
        MarkTree(this);
    }

    private static void MarkTree(Node n)
    {
        if (n is VisualInstance3D v && (v.Layers & CockpitLayer) == 0) v.Layers |= VehicleLayer;   // the interior keeps its own layer only (the mirror leaves it out)
        foreach (var c in n.GetChildren()) MarkTree(c);
    }

    private static void MarkVehicle(Node n)
    {
        if (n is not VisualInstance3D v || (v.Layers & CockpitLayer) != 0) return;
        for (var p = n.GetParent(); p is not null; p = p.GetParent())
            if (p is CarNode) { v.Layers |= VehicleLayer; return; }
    }
    public MeshInstance3D? Rtc1 { get; private set; }
    public MeshInstance3D? Rtc6 { get; private set; }
    /// <summary>The eye in the car's frame (VLOC[0], Godot axes).</summary>
    public Vector3 Eye { get; private set; }

    /// <summary>The gauge needles (speedo, tacho): own nodes, at rest at their VLOC; the cockpit turns them about its local Y.</summary>
    public (Node3D Node, Transform3D Pose)?[] Needles { get; } = new (Node3D, Transform3D)?[2];

    public void AddNeedle(int which, Mesh? mesh, Transform3D rest)
    {
        if (mesh is null || _cockpit is null) return;
        var n = new Node3D { Transform = rest, Name = which == 0 ? "Speedo" : "Tacho" };
        n.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = CockpitLayer });
        _cockpit.AddChild(n);
        Needles[which] = (n, rest);
    }

    /// <summary>
    /// The in-car view: the vehicle's detail-5 interior and a camera at VLOC[0], the driver's eye. <paramref name="hood"/> builds
    /// the interior's damage-textured parts (the hood's V5 FT TP) for a .TMT entry per face, again as the front zone is hit.
    /// </summary>
    public void AddCockpit(Mesh? interior, System.Numerics.Vector3 eye, Mesh? ring1 = null, Mesh? ring6 = null, Func<Func<string, int>, Mesh?>? hood = null)
    {
        if (interior is not null && interior.GetSurfaceCount() == 0) interior = null;   // a car with no cockpit model (the Armadillo)
        HasInterior = interior is not null;
        if (interior is not null) { _cockpit = new MeshInstance3D { Mesh = interior, Name = "Cockpit", Visible = false, Layers = CockpitLayer }; AddChild(_cockpit); if (Paint is not null) ApplyPaint(_cockpit); }
        if (_cockpit is not null)
        {
            _buildHood = hood;
            RefreshHood();
            // the hood's gunsight ring, twice (RTC1, the low-resolution one; RTC6): the binoculars swap them
            if (ring1 is not null) { Rtc1 = new MeshInstance3D { Mesh = ring1, Name = "Ring1", Layers = CockpitLayer }; _cockpit.AddChild(Rtc1); }
            if (ring6 is not null) { Rtc6 = new MeshInstance3D { Mesh = ring6, Name = "Ring6", Visible = false, Layers = CockpitLayer }; _cockpit.AddChild(Rtc6); }
        }
        Eye = Coords.ToGodot(eye);
        CockpitCamera = new Camera3D { Name = "Eye", Position = Eye, Fov = 70, Near = 0.05f, Far = 20000 };
        AddChild(CockpitCamera);
    }

    private Func<Func<string, int>, Mesh?>? _buildHood;
    private MeshInstance3D? _hood;
    private int _hoodFrame = -1;

    /// <summary>The cockpit's hood with the front zone's damage texture (V5 FT TP), rebuilt when that changes.</summary>
    public void RefreshHood()
    {
        if (_cockpit is null || _buildHood is null) return;
        int frame = TexFrame(DamageTextures.ZoneSlots[0][^1]);
        if (frame == _hoodFrame) return;
        _hoodFrame = frame;
        var mesh = _buildHood(TexFrame);
        if (mesh is null || mesh.GetSurfaceCount() == 0) { _hood?.QueueFree(); _hood = null; return; }
        if (_hood is null) { _hood = new MeshInstance3D { Name = "Hood", Layers = CockpitLayer }; _cockpit.AddChild(_hood); }
        _hood.Mesh = mesh;
        if (Paint is not null) ApplyPaint(_hood);
    }

    /// <summary>The interior's bounds in the car's frame, the hood included.</summary>
    public Aabb? CockpitBounds => _cockpit?.Mesh is not { } m ? null : _hood?.Mesh is { } h ? m.GetAabb().Merge(h.GetAabb()) : m.GetAabb();

    private bool _inCar, _dash3d = true;

    /// <summary>The 3D dash: the interior model is drawn. Off: the 2D dash, the driver's eye over the world with the panels flat on the screen (Hud).</summary>
    public bool Dash3D
    {
        get => _dash3d;
        set { _dash3d = value; Apply(); }
    }

    private bool _outside;

    /// <summary>
    /// A script camera (cutscene, death, mission end) holds the view: the car is seen from outside whatever
    /// the player's view, the body, wheels and weapons shown and the interior hidden; the cockpit state
    /// is kept and returns when the camera is released.
    /// </summary>
    public bool SeenFromOutside
    {
        get => _outside;
        set { if (_outside == value) return; _outside = value; Apply(); }
    }

    public void SetCockpit(bool on)
    {
        _inCar = on;
        Apply();
        if (on && !_outside) CockpitCamera?.MakeCurrent();
    }

    private void Apply()
    {
        bool inside = _inCar && !_outside;
        if (_cockpit is not null) _cockpit.Visible = inside && _dash3d;
        if (_body is not null) _body.Visible = !inside;
        foreach (var (node, _, _, _) in _wheels) node.Visible = !inside && !_flung.Contains(node);
        foreach (var (_, node) in _weapons) node.Visible = !inside && !_flung.Contains(node);
        // a turret's gun too: the in-car view draws the interior, not the car's outside (its underside showed through the roof)
        foreach (var (node, _) in _turrets) node.Visible = !inside && !_flung.Contains(node);
    }

    /// <summary>
    /// The burnt-out wreck: set with the dead car's body
    /// (DamageView builds it), it puts the lamps out for good: the headlamps,
    /// their child the tail lamp and the brake lamp are hidden, and with them
    /// the port's beams, glares and brake glow. A renewed car is whole again.
    /// </summary>
    public bool Wrecked { get; private set; }

    public void SetWrecked(bool on)
    {
        Wrecked = on;
        _rearLevel = -1;
        if (on) return;
        _flung.Clear();
        Apply();
        SetBurn(0, 0);
    }

    /// <summary>
    /// The modern look's burnt wreck (object.gdshaderinc's <c>wreck</c>): how charred the car's own meshes are
    /// (body, wheels, weapons, turrets) and how hot, the fire inside glowing through cracks. (0, 0) is the car
    /// as it was; the 1997 look's shaders do not read it.
    /// </summary>
    public void SetBurn(float charred, float heat)
    {
        var w = new Vector2(charred, heat);
        if (w == _burn) return;
        _burn = w;
        foreach (var m in BurntMeshes()) m.SetInstanceShaderParameter("wreck", w);
    }

    private Vector2 _burn;

    /// <summary>
    /// The car's custom paint (a respray, object.gdshaderinc's <c>paint_a</c> and <c>paint_b</c>): per car, on its own meshes, so cars of one
    /// scheme share their materials and masks and differ in colour. Null is the factory paint.
    /// </summary>
    public I76.Formats.Imaging.CarPaint? Paint { get; private set; }

    public void SetPaint(I76.Formats.Imaging.CarPaint? paint)
    {
        Paint = paint is { IsNone: true } ? null : paint;
        foreach (var m in BurntMeshes()) ApplyPaint(m);
        foreach (var m in new[] { _cockpit, _hood }) if (m is not null) ApplyPaint(m);
    }

    private void ApplyPaint(MeshInstance3D m)
    {
        static Vector4 Of(I76.Formats.Imaging.PaintColour? c, I76.Formats.Imaging.PaintFinish f) =>
            c is { } k ? new Vector4(k.R / 255f, k.G / 255f, k.B / 255f, 1 + (int)f) : Vector4.Zero;
        var p = Paint;
        m.SetInstanceShaderParameter("paint_a", p is null ? Vector4.Zero : Of(p.Primary, p.Finish));
        m.SetInstanceShaderParameter("paint_b", p is null ? Vector4.Zero : Of(p.Secondary, p.Finish));
    }

    /// <summary>The car's own meshes, those a fire chars: not the lamps, glares or the interior.</summary>
    public IEnumerable<MeshInstance3D> BurntMeshes()
    {
        if (_body is not null) yield return _body;
        foreach (var (node, _, _, _) in _wheels) if (node is MeshInstance3D m) yield return m;
        foreach (var (_, node) in _weapons) yield return node;
        foreach (var (b, _) in _turrets)
            foreach (var m in b.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>()) yield return m;
    }

    /// <summary>The body's bounds in the car's frame (Godot axes), for where the fire burns on it.</summary>
    public Aabb BodyBounds => _body?.Mesh?.GetAabb() ?? new Aabb(new Vector3(-1, 0, -2), new Vector3(2, 1.2f, 4));

    /// <summary>The pieces the dying car has thrown off (rolls 0 and 1), hidden on it until it is renewed.</summary>
    private readonly HashSet<Node3D> _flung = [];

    /// <summary>A wheel thrown off (roll 0: the wheel node detached as debris): hidden here; the node, to copy for the debris.</summary>
    public Node3D? FlingWheel(int slot)
    {
        Wheel? w = Sim.WheelAt(slot);
        foreach (var (node, ww, _, _) in _wheels)
            if (ww == w && w is not null && _flung.Add(node)) { node.Visible = false; return node; }
        return null;
    }

    /// <summary>A weapon thrown off (roll 1: the gizmo detached as a "v-chnk"): its gun and turret, hidden here; the nodes, to copy for the debris.</summary>
    public List<Node3D> FlingWeapon(int index)
    {
        var o = new List<Node3D>();
        foreach (var (w, node) in _weapons) if (w == index && _flung.Add(node)) o.Add(node);
        foreach (var (b, w) in _turrets) if (w == index && _flung.Add(b)) o.Add(b);
        foreach (var n in o) n.Visible = false;
        return o;
    }

    private readonly List<(int Weapon, MeshInstance3D Node)> _weapons = [];

    /// <summary>A gun emplacement's death (the gun gizmo hiding): its weapon gizmo, gun and turret, hidden in place.</summary>
    public void HideWeapon(int index)
    {
        foreach (var (w, node) in _weapons) if (w == index && _flung.Add(node)) node.Visible = false;
        foreach (var (b, w) in _turrets) if (w == index && _flung.Add(b)) b.Visible = false;
    }

    /// <summary>A fixed weapon's gizmo as its own node (it leaves the car when a dying car throws it).</summary>
    public void AddWeapon(int weapon, Mesh? mesh)
    {
        if (mesh is null) return;
        var n = new MeshInstance3D { Mesh = mesh, Name = $"Weapon{weapon}", Visible = _body?.Visible ?? true };
        if (Paint is not null) ApplyPaint(n);
        AddChild(n);
        _weapons.Add((weapon, n));
    }

    /// <summary>The body's mesh, swapped for damaged conditions.</summary>
    public void SetBody(Mesh? mesh)
    {
        if (mesh is null || _body is null) return;
        _body.Mesh = mesh;
        // The burnt hull is lower than the car's roof, and the sim rests an upturned car on its roof at RideOffset
        // (the vehicle's ride offset), so on its back the wreck hangs that gap above the ground. Place drops it by the gap as it turns over.
        float top = mesh.GetAabb().End.Y, roof = Sim?.P.RideOffset ?? 0;
        _wreckGap = top > 0.2f && roof > top ? roof - top : 0;
    }

    /// <summary>Which damage level each body part shows (part code → condition digit).</summary>
    public Dictionary<string, char> Conditions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The .TMT entry each zone's face slots show (DamageTextures.Frame of the zone's level; 0 whole).</summary>
    public int[] ZoneFrames { get; } = new int[5];

    /// <summary>
    /// A face texture's .TMT entry on this car (MeshBuilder's frameOf): its zone's damage texture, a wreck's entry 3
    /// The modern look's wreck keeps its zones' worst living shape (ModelAssembler.WreckShape), and so their worst living entry, 2.
    /// </summary>
    public int TexFrame(string faceTexture) =>
        DamageTextures.Zone(faceTexture) is var z and >= 0 ? (!Wrecked ? ZoneFrames[z] : Modern ? 2 : DamageTextures.WreckFrame) : 0;
    public string Definition { get; set; } = "";

    private SpotLight3D[] _lamps = [];
    private const float HeadFar = 200f;
    private MeshInstance3D? _tail, _brake, _head;
    // the modern look's lit lamps: the lamp faces' own lens glows (the brake lamp's while braking, the tail lamp's with the
    // lights on; added on top, only where the face's texture is red, so the lamp keeps its detail and the bumper and
    // surround stay as they are), HDR so the modern look's bloom takes it
    private const string GlowSource = """
        shader_type spatial;
        render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
        uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
        instance uniform float level = 0.0;
        void fragment() {
            vec4 t = texture(albedo_tex, UV);
            float red = clamp((t.r - max(t.g, t.b)) * 2.0, 0.0, 1.0);
            float lum = clamp(t.r * 1.2, 0.0, 1.0);
            ALBEDO = vec3(1.0, 0.1, 0.05) * red * (0.25 + 0.75 * lum) * level;
        }
        """;
    private static Shader? _glowShader;
    /// <summary>One glow material per texture; weakly keyed, so a finished scene's car textures are not held for the rest of the run.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, ShaderMaterial> GlowMats = new();
    internal static void ResetForGameSwitch() => GlowMats.Clear();
    private const float BrakeLevel = 1.3f, TailLevel = 0.6f;

    /// <summary>The tail lamp mesh's glow twin: each surface's own texture under the red-lens shader.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Mesh?> GlowTwins = new();

    /// <summary>The twin is made once for a lamp mesh (cars of one kind share theirs): duplicating a mesh the renderer holds reads its buffers back, tens of ms.</summary>
    private static Mesh? GlowMesh(Mesh? tail)
    {
        if (tail is null) return null;
        if (GlowTwins.TryGetValue(tail, out var twin)) return twin;
        GlowTwins.AddOrUpdate(tail, twin = MakeGlowMesh(tail));
        return twin;
    }

    private static Mesh? MakeGlowMesh(Mesh? tail)
    {
        if (tail is not ArrayMesh src) return null;
        var m = (ArrayMesh)src.Duplicate();
        _glowShader ??= new Shader { Code = GlowSource };
        bool any = false;
        for (int i = 0; i < m.GetSurfaceCount(); i++)
        {
            if (m.SurfaceGetMaterial(i) is not ShaderMaterial sm || sm.GetShaderParameter("albedo_tex").As<Texture2D>() is not { } tex) continue;
            if (!GlowMats.TryGetValue(tex, out var gm)) { gm = new ShaderMaterial { Shader = _glowShader, RenderPriority = 1 }; gm.SetShaderParameter("albedo_tex", tex); GlowMats.Add(tex, gm); }
            m.SurfaceSetMaterial(i, gm); any = true;
        }
        return any ? m : null;
    }
    private MeshInstance3D? _tailGlow, _brakeGlow, _brakeHeld;
    private MeshInstance3D[] _glare = [], _rearGlare = [];
    private OmniLight3D? _brakeLight;
    /// <summary>How long the brake lamps stay lit after the last braking tick (ms), so an AI's brake-throttle flicker reads as braking.</summary>
    private const ulong BrakeHoldMs = 150;
    private ulong _brakeSeenMs;
    private float _rearLevel = -1;
    /// <summary>The modern look: the headlamps flare towards the camera.</summary>
    public bool Modern { get; set; } = true;

    /// <summary>The body mesh's parts: all but the turrets, the lamps and the weapons' gizmos, which are nodes of their own.</summary>
    public static Func<I76.Formats.Assembly.ModelPart, bool> BodyParts(I76.Formats.Assembly.AssembledModel m)
    {
        var turrets = m.Turrets.Select(t => t.Node).ToHashSet();
        return p => !turrets.Contains(p.NodeName) && !IsLamp(p.NodeName) && !m.GizmoOf.ContainsKey(p) && !IsRotor(p);
    }

    /// <summary>A helicopter's rotor part: a flat disc textured with the animated blade flipbook (BLADE101..).</summary>
    public static bool IsRotor(I76.Formats.Assembly.ModelPart p) =>
        p.Mesh.Faces.Length > 0 && p.Mesh.Faces.All(f => f.Texture.StartsWith("BLADE", StringComparison.OrdinalIgnoreCase));

    /// <summary>A rotor's disc in the car's frame (engine axes): where, which axis it turns about, how wide.</summary>
    public readonly record struct RotorSpec(System.Numerics.Vector3 Centre, int Axis, float Radius);

    /// <summary>The rotor discs of a model: each blade part's centre, its thinnest axis and its half width.</summary>
    public static List<RotorSpec> Rotors(I76.Formats.Assembly.AssembledModel m)
    {
        var list = new List<RotorSpec>();
        foreach (var p in m.Parts.Where(IsRotor))
        {
            var v = p.Mesh.Vertices;
            var lo = v.Aggregate(System.Numerics.Vector3.Min); var hi = v.Aggregate(System.Numerics.Vector3.Max);
            var ext = (hi - lo) * 0.5f;
            int axis = ext.X <= ext.Y && ext.X <= ext.Z ? 0 : ext.Y <= ext.Z ? 1 : 2;
            float radius = axis == 0 ? MathF.Max(ext.Y, ext.Z) : axis == 1 ? MathF.Max(ext.X, ext.Z) : MathF.Max(ext.X, ext.Y);
            list.Add(new RotorSpec(p.ToModel.T + (lo + hi) * 0.5f, axis, radius));
        }
        return list;
    }

    private readonly List<(MeshInstance3D Disc, float Dir, float Speed)> _rotors = [];
    private MeshInstance3D? _rotorOriginal;
    private float _rotorSpin = -1, _rotorPhase;
    private static ShaderMaterial? _rotorMat;

    /// <summary>
    /// A helicopter's rotors: the original's animated blade parts (the 1997 look, <paramref name="original"/>) and, in the
    /// modern look, a translucent motion-blurred disc for each (rotor_disc.gdshader). Visuals only.
    /// </summary>
    public void AddRotors(Mesh? original, IReadOnlyList<RotorSpec> specs, float sceneLight)
    {
        if (_body is null) return;
        if (original is not null) { _rotorOriginal = new MeshInstance3D { Mesh = original, Name = "RotorBlades" }; _body.AddChild(_rotorOriginal); }
        _rotorMat ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/rotor_disc.gdshader") };
        _rotorMat.SetShaderParameter("scene_light", sceneLight);
        foreach (var sp in specs)
        {
            var mat = (ShaderMaterial)_rotorMat.Duplicate();
            bool main = sp.Axis == 1;
            mat.SetShaderParameter("blades", main ? 2 : 4);
            mat.SetShaderParameter("blade_width", main ? 0.03f : 0.05f);
            var disc = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(sp.Radius * 2, sp.Radius * 2) }, MaterialOverride = mat, Name = main ? "RotorDisc" : "TailRotorDisc",
                Position = Coords.ToGodot(sp.Centre), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
                // the plane faces +Y; a rotor turning about X lies on its side
                Basis = sp.Axis == 0 ? new Basis(Vector3.Back, -Mathf.Pi / 2) : sp.Axis == 2 ? new Basis(Vector3.Right, Mathf.Pi / 2) : Basis.Identity,
            };
            _body.AddChild(disc);
            _rotors.Add((disc, main ? 1f : -1f, main ? 5.5f : 16f));
        }
    }

    /// <summary>Per frame: the rotors' turn (they spin up in the air and down when landed or shot down) and which look shows.</summary>
    private void UpdateRotors(float dt)
    {
        if (_rotors.Count == 0 && _rotorOriginal is null) return;
        bool modern = Modern;
        if (_rotorOriginal is not null) _rotorOriginal.Visible = !modern;
        float target = (((uint)Sim.Flags & 0x80) != 0 || (Sim.Flags & VehicleFlags.Dead) != 0) ? 0f : 1f;
        if (_rotorSpin < 0) _rotorSpin = target;
        _rotorSpin = Mathf.MoveToward(_rotorSpin, target, dt / (target > _rotorSpin ? 3f : 5f));
        _rotorPhase += dt * _rotorSpin;
        float blur = Mathf.SmoothStep(0.12f, 0.6f, _rotorSpin);
        foreach (var (disc, dir, rps) in _rotors)
        {
            disc.Visible = modern;
            if (!modern) continue;
            disc.SetInstanceShaderParameter("angle", dir * _rotorPhase * rps * Mathf.Tau);
            disc.SetInstanceShaderParameter("blur", blur);
        }
    }

    /// <summary>A fixed weapon's parts: its gizmo's, but for a turret's moving ones.</summary>
    public static Func<I76.Formats.Assembly.ModelPart, bool> WeaponParts(I76.Formats.Assembly.AssembledModel m, int weapon)
    {
        var turrets = m.Turrets.Select(t => t.Node).ToHashSet();
        return p => m.GizmoOf.TryGetValue(p, out int w) && w == weapon && !turrets.Contains(p.NodeName);
    }

    /// <summary>A vehicle lamp part: TLGT (the tail lamp), BLGT (the same lamp lit, 8 mm proud of it), HLGT (the headlamps' lit faces).</summary>
    public static bool IsLamp(string node, string? code = null) =>
        node.Length >= 8 && (code is null ? node[4..8].ToUpperInvariant() is "TLGT" or "BLGT" or "HLGT" : node[4..8].Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The lamps as their own nodes: the headlamps
    /// show while the lights are on, the lit brake lamp while
    /// braking, the plain tail lamp otherwise. A lamp goes dark once its end
    /// of the car (zone 0 front, 3 rear) is under half its armour or chassis.
    /// </summary>
    public void SetLamps(Mesh? tail, Mesh? brake, Mesh? head = null, HeldLamps? held = null)
    {
        _tail?.QueueFree(); _brake?.QueueFree(); _head?.QueueFree(); _tailGlow?.QueueFree(); _tailHeld?.QueueFree(); _brakeGlow?.QueueFree(); _brakeHeld?.QueueFree();
        _tail = tail is null ? null : new MeshInstance3D { Mesh = tail, Name = "TailLamps" };
        _brake = brake is null ? null : new MeshInstance3D { Mesh = brake, Name = "BrakeLamps", Visible = false };
        _head = head is null ? null : new MeshInstance3D { Mesh = head, Name = "HeadLamps", Visible = false };
        _held = held;
        _tailHeld = held?.Tail is { } ht ? new MeshInstance3D { Mesh = ht, Name = "TailLampsHeld", Visible = false } : null;
        _brakeHeld = held?.Brake is { } hb ? new MeshInstance3D { Mesh = hb, Name = "BrakeLampsHeld", Visible = false } : null;
        _tailGlow = GlowMesh(held is null ? tail : held.Tail) is { } gmesh ? new MeshInstance3D { Mesh = gmesh, Name = "TailGlow", Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off } : null;
        // the brake glow over the brake lamp's own lit faces (BLGT), the tail lamp's where a car has none
        _brakeGlow = GlowMesh(held is null ? brake ?? tail : held.Brake ?? held.Tail) is { } bmesh ? new MeshInstance3D { Mesh = bmesh, Name = "BrakeGlow", Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off } : null;
        _rearLevel = -1;
        if (_tail is not null) AddChild(_tail);
        if (_tailHeld is not null) AddChild(_tailHeld);
        if (_tailGlow is not null) AddChild(_tailGlow);
        if (_brake is not null) AddChild(_brake);
        if (_brakeHeld is not null) AddChild(_brakeHeld);
        if (_brakeGlow is not null) AddChild(_brakeGlow);
        if (_head is not null) AddChild(_head);
    }

    /// <summary>
    /// The modern look's lamps on a crushed body (LampSupport): the tail lamp
    /// faces the body still holds (null: none), and whether each lamp's left
    /// and right halves still have a face held, for their glares. The
    /// original keeps the lamps where the intact body held them, hanging in
    /// the air off the crushed one; the 1997 look still draws them so.
    /// </summary>
    public sealed record HeldLamps(Mesh? Tail, bool[] Rear, bool[] Head, Mesh? Brake = null);
    private HeldLamps? _held;
    private MeshInstance3D? _tailHeld;
    private bool RearHeld(int i) => _held is null || i >= _held.Rear.Length || _held.Rear[i];
    private bool HeadHeld(int i) => _held is null || i >= _held.Head.Length || _held.Head[i];

    /// <summary>
    /// The lamps of an assembled car (its TLGT, BLGT and HLGT parts) and, where
    /// its body is crushed away from where the intact one held them, the
    /// modern look's held faces.
    /// </summary>
    public void SetLamps(Assets.MeshBuilder meshes, I76.Formats.Assembly.AssembledModel m, I76.Formats.Assembly.AssembledModel? intact) =>
        SetLamps(BuildLamps(meshes, m, intact));

    /// <summary>The lamp meshes of <see cref="SetLamps(Assets.MeshBuilder, I76.Formats.Assembly.AssembledModel, I76.Formats.Assembly.AssembledModel?)"/>, made apart so that a car of a kind seen before reuses them.</summary>
    public sealed record LampMeshes(Mesh? Tail, Mesh? Brake, Mesh? Head, HeldLamps? Held);

    public void SetLamps(LampMeshes l) => SetLamps(l.Tail, l.Brake, l.Head, l.Held);

    public static LampMeshes BuildLamps(Assets.MeshBuilder meshes, I76.Formats.Assembly.AssembledModel m, I76.Formats.Assembly.AssembledModel? intact)
    {
        var floating = intact is null ? [] : I76.Formats.Assembly.LampSupport.Floating(m, intact, p => IsLamp(p.NodeName), p => !IsLamp(p.NodeName));
        HeldLamps? held = null;
        if (floating.Count > 0)
        {
            // a lamp half (left or right of the middle) keeps its glare while any face of it is held
            bool[] Halves(string code)
            {
                var faces = m.Parts.Where(p => IsLamp(p.NodeName, code)).SelectMany(p => p.Mesh.Faces.Select(f => (p, f))).ToList();
                return [.. new[] { -1, 1 }.Select(side =>
                {
                    var half = faces.Where(x => Math.Sign(I76.Formats.Assembly.LampSupport.Middle(x.p, x.f).X) == side).ToList();
                    return half.Count == 0 || half.Any(x => !floating.Contains(x));
                })];
            }
            bool hasBrake = m.Parts.Any(p => IsLamp(p.NodeName, "BLGT"));
            held = new HeldLamps(meshes.Build(m, wheels: false, include: p => IsLamp(p.NodeName, "TLGT"), skipFace: (p, f) => floating.Contains((p, f)), oneSided1997: true),
                Halves(hasBrake ? "BLGT" : "TLGT"), Halves("HLGT"),
                meshes.Build(m, wheels: false, include: p => IsLamp(p.NodeName, "BLGT"), skipFace: (p, f) => floating.Contains((p, f)), oneSided1997: true));
        }
        // the lamp faces are keyed (and the headlamp cones translucent), which the material rule draws from both sides; the
        // engine culls them as any face of the car, so in the 1997 look a lamp does not show from behind
        return new LampMeshes(meshes.Build(m, wheels: false, include: p => IsLamp(p.NodeName, "TLGT"), oneSided1997: true),
                 meshes.Build(m, wheels: false, include: p => IsLamp(p.NodeName, "BLGT"), oneSided1997: true),
                 meshes.Build(m, wheels: false, include: p => IsLamp(p.NodeName, "HLGT"), oneSided1997: true), held);
    }
    private readonly List<(Node3D Gun, int Weapon)> _turrets = [];

    /// <summary>
    /// A turret's gun as its own node at its pivot, turned by the weapon's aim each frame:
    /// it yaws about the mount's up through its own origin and pitches about its own x. The base stays on the mount
    /// with the weapon's fixed parts.
    /// </summary>
    public void AddTurret(int weapon, Mesh? gunMesh, System.Numerics.Vector3 pivot)
    {
        if (gunMesh is null) return;
        var g = new Node3D { Position = Coords.ToGodot(pivot), Name = $"Turret{weapon}" };
        g.AddChild(new MeshInstance3D { Mesh = gunMesh });
        AddChild(g);
        _turrets.Add((g, weapon));
    }

    private static Shader? _glareShader;
    private static QuadMesh? _glareQuad;

    /// <summary>
    /// Two headlamps at the given points (the centres of the HLGT part's two
    /// halves; else guessed from the box), shown while the car's lights flag is
    /// on: a spotlight each (modern mode only casts shadows from the player's)
    /// and, in the modern look, a glare quad (headlight_glare.gdshader).
    /// </summary>
    private static ShaderMaterial? _rearMat;

    /// <summary>
    /// The modern look's rear lamps: a red glare at each (the centres of the
    /// brake lamp part's halves), full while braking and faint while the
    /// lights are on, wide rather than beamed, and a small red light behind
    /// the car while braking.
    /// </summary>
    public void AddRearLamps(System.Numerics.Vector3[]? at)
    {
        if (at is null) return;
        _glareShader ??= GD.Load<Shader>("res://Shaders/headlight_glare.gdshader");
        _glareQuad ??= new QuadMesh { Size = new Vector2(2, 2) };
        if (_rearMat is null)
        {
            _rearMat = new ShaderMaterial { Shader = _glareShader };
            _rearMat.SetShaderParameter("tint", new Color(1f, 0.1f, 0.06f));
            _rearMat.SetShaderParameter("strength", 0.6f);
            _rearMat.SetShaderParameter("sharpness", 1.5f);
            _rearMat.SetShaderParameter("wide", 0.3f);
            _rearMat.SetShaderParameter("size_near", 0.22f);
            _rearMat.SetShaderParameter("size_far", 0.48f);
            _rearMat.SetShaderParameter("streak_amount", 0.25f);
            _rearMat.SetShaderParameter("halo_amount", 0.2f);
            _rearMat.SetShaderParameter("day_floor", 0.4f);
        }
        // the lamp's -Z looks backwards: half a turn about up
        _rearGlare = at.Select(p =>
        {
            var g = new MeshInstance3D { Mesh = _glareQuad, MaterialOverride = _rearMat, Position = Coords.ToGodot(p) + new Vector3(0, 0, 0.05f),
                Basis = new Basis(Vector3.Up, Mathf.Pi), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 8, Visible = false, Name = "RearGlare" };
            AddChild(g);
            return g;
        }).ToArray();
        var mid = at.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / at.Length;
        _brakeLight = new OmniLight3D { Position = Coords.ToGodot(mid) + new Vector3(0, -0.15f, 0.6f), OmniRange = 4.5f, LightEnergy = 1.0f,
            LightColor = new Color(1f, 0.08f, 0.04f), ShadowEnabled = false, Visible = false, Name = "BrakeLight" };
        AddChild(_brakeLight);
    }

    private SirenBar? _siren;

    /// <summary>The modern look's police light bar (SirenBar), for a car whose model has one.</summary>
    public void AddSirenBar(SirenBar bar)
    {
        _siren?.QueueFree();
        _siren = bar;
        AddChild(bar);
    }

    private MeshInstance3D? _lightPool;
    private Vector3 _lightPoolAt;

    /// <summary>
    /// The night's headlight pool (VLOC kind 38: the "hdlt_msk" object, made only under a night preset):
    /// <paramref name="mesh"/> placed at <paramref name="at"/> (car frame, Godot axes), shown while the HLGT faces are
    /// shown, the 1997 look only. It is also where the car's night light stands (NightLights).
    /// </summary>
    public void AddLightPool(Mesh mesh, Material material, Vector3 at)
    {
        _lightPool?.QueueFree();
        _lightPoolAt = at;
        _lightPool = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = at, Name = LightPoolName, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4 };
        AddChild(_lightPool);
    }

    /// <summary>Whether a mesh instance is a car's headlight pool, which casts no drop shadow.</summary>
    public static bool IsLightPool(MeshInstance3D mi) => mi.Name == LightPoolName;
    private const string LightPoolName = "LightPool";

    /// <summary>The car's night light (a spot at the pool's place along the car's nose) while it is on, in global space; else null.</summary>
    public (Vector3 Pos, Vector3 Forward)? NightLight { get; private set; }

    // The modern look's headlamps (GitHub #52): a long, wide, soft-edged beam, pitched a little down so it lays on the road
    // out to about 50 m. The player's car casts shadows from it; of the others, only the nearest few carry a real light.
    private const float HeadRange = 110f, HeadAngle = 42f, HeadEnergy = 14f, HeadPitch = -2.5f;
    /// <summary>How many other cars' headlamps cast real light at once (the nearest to the camera); I76_HEADLIGHTS overrides it. The player's always do.</summary>
    public static readonly int MaxHeadLit = int.TryParse(System.Environment.GetEnvironmentVariable("I76_HEADLIGHTS"), out int hn) ? hn : 6;
    private static readonly List<CarNode> HeadCars = [];
    private bool _isPlayer;
    private float _headDist2 = float.MaxValue;

    public override void _ExitTree() => HeadCars.Remove(this);

    /// <summary>How many other cars with lit lamps are nearer the camera than this one (distances as of their last update).</summary>
    private int HeadRank()
    {
        int r = 0;
        foreach (var c in HeadCars) if (c != this && !c._isPlayer && c._headDist2 < _headDist2) r++;
        return r;
    }

    public void AddHeadlights(bool player, System.Numerics.Vector3[]? at = null)
    {
        var s = Sim;
        float z = s.BoxZ[3] - 0.1f, y = (s.BoxY[1] + s.BoxY[2]) * 0.5f, x = (s.BoxX[3] - s.BoxX[0]) * 0.3f;
        at ??= [new(-x, y, z), new(x, y, z)];
        _isPlayer = player;
        if (!HeadCars.Contains(this)) HeadCars.Add(this);
        _lamps = at.Select(p =>
        {
            var l = new SpotLight3D { Position = Coords.ToGodot(p), RotationDegrees = new Vector3(HeadPitch, 0, 0), SpotRange = HeadRange, SpotAngle = HeadAngle, SpotAttenuation = 0.6f,
                SpotAngleAttenuation = 0.7f, LightEnergy = HeadEnergy, LightColor = new Color(1f, 0.95f, 0.8f), ShadowEnabled = player, ShadowBias = 0.08f, ShadowNormalBias = 2.5f, Visible = false };
            AddChild(l);
            return l;
        }).ToArray();
        _glareShader ??= GD.Load<Shader>("res://Shaders/headlight_glare.gdshader");
        _glareQuad ??= new QuadMesh { Size = new Vector2(2, 2) };
        var mat = new ShaderMaterial { Shader = _glareShader };
        _glare = at.Select(p =>
        {
            var g = new MeshInstance3D { Mesh = _glareQuad, MaterialOverride = mat, Position = Coords.ToGodot(p) + new Vector3(0, 0, -0.05f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 12, Visible = false, Name = "Glare" };
            AddChild(g);
            return g;
        }).ToArray();
    }

    public void Init(Vehicle vehicle, Mesh? mesh)
    {
        Sim = vehicle;
        if (mesh is not null) { _body = new MeshInstance3D { Mesh = mesh, Name = "Body" }; AddChild(_body); }
        Snapshot(); Snapshot();
    }

    /// <summary>Swap a wheel's mesh: the blown tyre's or the intact one's, as <paramref name="blown"/> says.</summary>
    public void SetWheelMesh(int slot, Mesh? mesh, bool blown)
    {
        if (mesh is null || slot is < 0 or > 5) return;
        _blownShown[slot] = blown;
        Wheel? w = Sim.WheelAt(slot);
        foreach (var (node, ww, _, _) in _wheels) if (ww == w && node is MeshInstance3D mi) mi.Mesh = mesh;
    }

    /// <summary>Which wheel slots show the blown tyre.</summary>
    private readonly bool[] _blownShown = new bool[6];

    /// <summary>
    /// The wheels' meshes made to match the sim's blown tyres (wheel+0x44), however they changed: a hit,
    /// a respawn or renewal, a network snapshot or a late join. <paramref name="build"/> makes slot's mesh.
    /// </summary>
    public void SyncTyres(Func<int, bool, Mesh?> build)
    {
        for (int slot = 0; slot < 6; slot++)
            if (Sim.WheelAt(slot) is { } w && w.Blown != _blownShown[slot]) SetWheelMesh(slot, build(slot, w.Blown), w.Blown);
    }

    public void AddWheel(int slot, Mesh mesh, System.Numerics.Vector3 centre)
    {
        Wheel? w = Sim.WheelAt(slot);
        if (w is null) return;
        var node = new MeshInstance3D { Mesh = mesh, Name = $"Wheel{slot}" };
        AddChild(node);
        _wheels.Add((node, w, slot < 2, centre));
    }

    /// <summary>Shift the current state to previous and take the sim's state as current.</summary>
    public void Snapshot()
    {
        _prevRot = _rot; _prevPos = _pos;
        _rot = Sim.Rot; _pos = Sim.Pos;
        // the wheels roll by the distance the car moved along its nose, signed (the sim's own wheel spin adds the
        // speed's size, so it turns forward in reverse too); a jump (a respawn) is not rolled
        _prevRoll = _roll;
        var moved = new System.Numerics.Vector3((float)(_pos.X - _prevPos.X), (float)(_pos.Y - _prevPos.Y), (float)(_pos.Z - _prevPos.Z));
        float along = System.Numerics.Vector3.Dot(moved, _rot.Forward);
        if (MathF.Abs(along) < 20f) _roll += along;
        CaptureAnim();
    }

    private double _roll, _prevRoll;

    // what else the tick moves, kept for the tick before as well so that it is drawn between the two (at 20 Hz a
    // 50 ms snap of a turret or a steered wheel would show): each turret's yaw and pitch, each wheel's steer and travel
    private (float Yaw, float Pitch)[] _aimPrev = [], _aimCur = [];
    private float _steerShown;
    private float _lean, _leanSpeed, _leanTarget, _leanShown, _velAge;
    private System.Numerics.Vector3 _leanVel0;
    private bool _phaseSet;
    private static readonly float? ForcedSteer = float.TryParse(System.Environment.GetEnvironmentVariable("I76_WINDOWS_STEER"), System.Globalization.CultureInfo.InvariantCulture, out var fs) ? fs : null;
    private (float Steer, float Travel)[] _wheelPrev = [], _wheelCur = [];

    private void CaptureAnim()
    {
        if (_aimCur.Length != _turrets.Count) { _aimPrev = new (float, float)[_turrets.Count]; _aimCur = new (float, float)[_turrets.Count]; }
        if (_wheelCur.Length != _wheels.Count) { _wheelPrev = new (float, float)[_wheels.Count]; _wheelCur = new (float, float)[_wheels.Count]; }
        bool first = _animSeeded == false;
        _animSeeded = true;
        for (int i = 0; i < _turrets.Count; i++)
        {
            _aimPrev[i] = _aimCur[i];
            if (Sim.Armory?.LoadOrder.ElementAtOrDefault(_turrets[i].Weapon) is { } wp) _aimCur[i] = (wp.AimYaw, wp.AimPitch);
            if (first) _aimPrev[i] = _aimCur[i];
        }
        for (int i = 0; i < _wheels.Count; i++)
        {
            _wheelPrev[i] = _wheelCur[i];
            _wheelCur[i] = (_wheels[i].Wheel.SteerAngle, TravelOf(_wheels[i].Wheel));
            if (first) _wheelPrev[i] = _wheelCur[i];
        }
    }

    /// <summary>
    /// A wheel's suspension travel to draw. The middle pair has no suspension in the sim (the original runs it with no
    /// force), so it rides at the mean of its side's front and rear wheels.
    /// </summary>
    private float TravelOf(Wheel w) =>
        w == Sim.MiddleRight ? 0.5f * (Sim.FrontRight.Travel + Sim.RearRight.Travel)
        : w == Sim.MiddleLeft ? 0.5f * (Sim.FrontLeft.Travel + Sim.RearLeft.Travel) : w.Travel;

    private bool _animSeeded;
    private static float LerpAngle(float a, float b, float t) => a + (float)Math.IEEERemainder(b - a, 2 * Math.PI) * t;

    /// <summary>The two poses to draw between, given rather than taken from the sim (a network client's snapshots).</summary>
    public void SetPoses(Rows prevRot, Double3 prevPos, Rows rot, Double3 pos)
    {
        _prevRot = prevRot; _prevPos = prevPos;
        _rot = rot; _pos = pos;
        _prevRoll = _roll;
        var moved = new System.Numerics.Vector3((float)(_pos.X - _prevPos.X), (float)(_pos.Y - _prevPos.Y), (float)(_pos.Z - _prevPos.Z));
        float along = System.Numerics.Vector3.Dot(moved, _rot.Forward);
        if (MathF.Abs(along) < 20f) _roll += along;
        CaptureAnim();
    }

    public System.Numerics.Vector3 GamePosition => _pos.ToVector3();

    /// <summary>For far cars on the original's flat model: the drawn height comes from the ground (display only).</summary>
    public Func<double, double, double>? GroundForDisplay { get; set; }

    /// <summary>A pose from another model, drawn instead of the tick interpolation (the modern physics' body, at its own rate).</summary>
    public Func<(Rows Rot, Double3 Pos)?>? LivePose { get; set; }

    // A far car's drawn heading (display only, docs/deviations.md): the flat model turns by
    // the whole commanded yaw each frame, and the AI's one-frame steering flips it from side to
    // side, a shudder the original never shows (it draws nothing past 600). Eased toward the sim's heading.
    private float? _farHeading;
    private const float FarHeadingTau = 0.2f;

    private Rows FarRows(Rows rows)
    {
        float h = MathF.Atan2(rows.Forward.X, rows.Forward.Z);
        float d = _farHeading ?? h;
        float k = 1f - MathF.Exp(-(float)GetProcessDeltaTime() / FarHeadingTau);
        d += (float)Math.IEEERemainder(h - d, 2 * Math.PI) * k;
        _farHeading = d = (float)Math.IEEERemainder(d, 2 * Math.PI);
        // about world y by (d − h): a forward at heading h turns to heading d
        float delta = d - h, c = MathF.Cos(delta), sn = MathF.Sin(delta);
        System.Numerics.Vector3 Turn(System.Numerics.Vector3 v) => new(v.X * c + v.Z * sn, v.Y, -v.X * sn + v.Z * c);
        return new Rows(Turn(rows.Right), Turn(rows.Up), Turn(rows.Forward));
    }

    /// <summary>Draw at alpha between the previous and current step, relative to the floating origin.</summary>
    public void Place(float alpha, System.Numerics.Vector3 origin)
    {
        double Lerp(double a, double b) => a + (b - a) * alpha;
        bool far = (Sim.Flags & (VehicleFlags)0x20000) != 0 && GroundForDisplay is not null;
        var live = LivePose?.Invoke();
        var rows = live?.Rot ?? new Rows(
            System.Numerics.Vector3.Lerp(_prevRot.Right, _rot.Right, alpha),
            System.Numerics.Vector3.Lerp(_prevRot.Up, _rot.Up, alpha),
            System.Numerics.Vector3.Lerp(_prevRot.Forward, _rot.Forward, alpha)).Orthonormalized();
        if (far && live is null) rows = FarRows(rows);
        else _farHeading = null;
        for (int ti = 0; ti < _turrets.Count && ti < _aimCur.Length; ti++)
        {
            // the sim's own turn of the muzzle (Weapon.TurretRot) relative to the mount's rest, so the barrel is where the rounds leave
            var (g, wi) = _turrets[ti];
            if (Sim.Armory?.LoadOrder.ElementAtOrDefault(wi) is not { } wp) continue;
            var turned = wp.TurretRot(LerpAngle(_aimPrev[ti].Yaw, _aimCur[ti].Yaw, alpha), (float)Lerp(_aimPrev[ti].Pitch, _aimCur[ti].Pitch));
            g.Basis = (Coords.ToGodot(turned.ToEngine()) * Coords.ToGodot(wp.BaseRot.ToEngine()).Inverse()).Orthonormalized();
        }
        // Lamps work while their end of the car holds half its armour and chassis
        bool frontOk = Sim.Health is not { } hd || hd.ZoneRatio(0) >= 0.5f, rearOk = Sim.Health is not { } hr || hr.ZoneRatio(3) >= 0.5f;
        // the lamps hold on a moment after the brake lets go: the AI's controllers flick between brake and throttle tick by tick,
        // which strobed them (the port's; a held brake looks the same)
        ulong nowMs = Time.GetTicksMsec();
        if (Sim.Throttle < -0.2f) _brakeSeenMs = nowMs;
        bool braking = rearOk && _brakeSeenMs != 0 && nowMs - _brakeSeenMs < BrakeHoldMs;
        // the modern look's upturned wreck lies on the ground, not on the sim's taller roof (the 1997 look keeps the original's)
        if (_body is not null)
            _body.Position = Wrecked && Modern ? new Vector3(0, _wreckGap * Mathf.Max(0, -rows.Up.Y), 0) : Vector3.Zero;
        bool shown = (_body?.Visible ?? true) && !Wrecked;
        bool lit = (Sim.Flags & VehicleFlags.Lights) != 0 && frontOk;
        // The brake lamp's lit faces (BLGT) show while braking; the tail lamp (TLGT) is the headlamps' child
        // node, so it shows only with the lights on (and the front holding up): with them off and the brake off the lamp
        // is the body's own unlit lens. Both looks; the modern look adds its glow over the lit faces
        var (brakeOn, tailOn) = I76.Formats.Assembly.LampSupport.RearFaces(braking, (Sim.Flags & VehicleFlags.Lights) != 0, frontOk, _brake is not null);
        bool brakeFaces = shown && brakeOn, tailFaces = shown && tailOn;
        if (_brake is not null) _brake.Visible = brakeFaces && !(Modern && _brakeHeld is not null);
        if (_brakeHeld is not null) _brakeHeld.Visible = brakeFaces && Modern;
        // the modern look draws only the lamp faces the (crushed) body still holds
        if (_tail is not null) _tail.Visible = tailFaces && !(Modern && _held is not null);
        if (_tailHeld is not null) _tailHeld.Visible = tailFaces && Modern;
        if (_brakeGlow is not null)
        {
            _brakeGlow.Visible = Modern && shown && braking;
            if (_brakeGlow.Visible) _brakeGlow.SetInstanceShaderParameter("level", BrakeLevel);
        }
        if (_tailGlow is not null)
        {
            _tailGlow.Visible = Modern && shown && !braking && lit && rearOk;
            if (_tailGlow.Visible) _tailGlow.SetInstanceShaderParameter("level", TailLevel);
        }
        if (_head is not null) _head.Visible = shown && lit && !Modern;   // the original's flat lit faces: the 1997 look only (the modern look has its glare and spotlights)
        // the headlamps' real lights: the player's always, the others' only for the nearest few to the camera
        bool lampsOn = lit && !Wrecked;
        var headCam = GetViewport()?.GetCamera3D();
        _headDist2 = lampsOn && headCam is not null ? headCam.GlobalPosition.DistanceSquaredTo(GlobalPosition) : float.MaxValue;
        bool lampLights = lampsOn && (_isPlayer || (_headDist2 < HeadFar * HeadFar && HeadRank() < MaxHeadLit));
        for (int i = 0; i < _lamps.Length; i++) _lamps[i].Visible = lampLights && HeadHeld(i);
        // the pool lies on the road ahead, so it shows from the cockpit too (the body hidden there)
        if (_lightPool is not null) _lightPool.Visible = !Wrecked && lit && !Modern;
        for (int i = 0; i < _glare.Length; i++) _glare[i].Visible = shown && lit && Modern && HeadHeld(i);
        // the rear glow: full while braking, a tail-lamp glimmer with the lights on, nothing otherwise
        bool lightsOn = (Sim.Flags & VehicleFlags.Lights) != 0 && rearOk;
        float rear = !Modern || !shown ? 0f : braking ? 1f : lightsOn ? 0.3f : 0f;
        if (rear != _rearLevel)
        {
            _rearLevel = rear;
            for (int i = 0; i < _rearGlare.Length; i++) { _rearGlare[i].Visible = rear > 0 && RearHeld(i); _rearGlare[i].SetInstanceShaderParameter("level", rear); }
            if (_brakeLight is not null) _brakeLight.Visible = rear >= 1f && (RearHeld(0) || RearHeld(1));
        }
        _siren?.Update(shown, !Wrecked, Modern);
        if (_engine is not null) _engine.PitchScale = _engineRevable ? EngineSoundPitch.Scale(Sim.Engine.Rpm, _engineRate) : 1f;
        double gx = Lerp(_prevPos.X, _pos.X), gz = Lerp(_prevPos.Z, _pos.Z);
        double gy = far ? GroundForDisplay!(gx, gz) : Lerp(_prevPos.Y, _pos.Y);
        if (live is { } lp) (gx, gy, gz) = (lp.Pos.X, lp.Pos.Y, lp.Pos.Z);
        Transform = new Transform3D(Coords.ToGodot(rows.ToEngine()), Coords.ToGodot(gx - origin.X, gy - origin.Y, gz - origin.Z));
        NightLight = _lightPool is not null && lit && !Wrecked ? (Transform * _lightPoolAt, -Transform.Basis.Z.Normalized()) : null;
        // the wheel strips' clock and speed: the real frame time, and the signed speed of the last
        // grounded step (the strip update is not called in the air, so a flying car's strips keep the speed it left with)
        _stripClock += (float)GetProcessDeltaTime();
        UpdateRotors((float)GetProcessDeltaTime());
        if ((Sim.Flags & VehicleFlags.Airborne) == 0)
            _stripSpeed = System.Numerics.Vector3.Dot(Sim.Vel, Sim.Rot.Forward) < 0 ? -Sim.Speed : Sim.Speed;
        // the driver behind the glass turns the wheel as the car steers (Vehicle.Steer: the cockpit's own, and what a network snapshot carries
        // for a remote car); I76_WINDOWS_STEER forces it, for captures
        if (_body is not null)
        {
            float st = ForcedSteer ?? Sim.Steer;
            if (MathF.Abs(st - _steerShown) > 0.002f) { _steerShown = st; _body.SetInstanceShaderParameter("steer", st); }
            // and he is thrown against the car's sideways acceleration: a spring on his lean, so a hard turn or a hit yanks him over and he
            // settles back (the sim moves the car at its own rate: the acceleration is taken across each change of velocity)
            float dt = (float)GetProcessDeltaTime();
            _velAge += dt;
            if (Sim.Vel != _leanVel0)
            {
                float lat = System.Numerics.Vector3.Dot(Sim.Vel - _leanVel0, Sim.Rot.Right) / MathF.Max(_velAge, 1f / 120f);
                _leanTarget = Math.Clamp(-lat / 12f, -1.5f, 1.5f);
                _leanVel0 = Sim.Vel; _velAge = 0;
            }
            else if (_velAge > 0.2f) _leanTarget = 0;
            _leanSpeed += ((_leanTarget - _lean) * 70f - _leanSpeed * 10f) * dt;
            _lean = Math.Clamp(_lean + _leanSpeed * dt, -1.5f, 1.5f);
            if (MathF.Abs(_lean - _leanShown) > 0.004f) { _leanShown = _lean; _body.SetInstanceShaderParameter("lean", _lean); }
            if (!_phaseSet) { _phaseSet = true; _body.SetInstanceShaderParameter("idle_phase", (GetInstanceId() % 997) * 0.37f); }
        }
        for (int wi = 0; wi < _wheels.Count; wi++)
        {
            var (node, w, front, centre) = _wheels[wi];
            float steerA = wi < _wheelCur.Length ? LerpAngle(_wheelPrev[wi].Steer, _wheelCur[wi].Steer, alpha) : w.SteerAngle;
            float travelA = wi < _wheelCur.Length ? (float)Lerp(_wheelPrev[wi].Travel, _wheelCur[wi].Travel) : TravelOf(w);
            // a round wheel turns about its axle, the port's stand-in for the hubcap frames; anything else (a tank's
            // track) stays put and steps its texture strip as the engine does
            var look = Assets.MeshBuilder.LookOf((node as MeshInstance3D)?.Mesh);
            // the modern look's round wheel in place of the original's polygon (the 1997 look keeps the original)
            if (look is { Original: { } orig } && node is MeshInstance3D wmi && (Modern && look.Smooth is { } smooth ? smooth : orig) is { } want && wmi.Mesh != want) wmi.Mesh = want;
            if (look is { Strips.Count: > 0 })
            {
                if (!_strips.TryGetValue(node, out var strip)) _strips[node] = strip = new WheelStrip(_stripClock);
                int i = strip.Tick(_stripClock, _stripSpeed);
                foreach (var (mat, frames) in look.Strips) Assets.MaterialLibrary.ShowFrame(mat, frames[i % frames.Length]);
            }
            // rolling forward the tyre's top goes toward the nose, Godot's -Z: a negative turn about +X
            float spin = look is { Rolls: false } ? 0f : (float)(Lerp(_prevRoll, _roll) / (w.Rest > 0.05f ? w.Rest : 0.33f));
            var basis = new Basis(Vector3.Right, -spin);
            if (front) basis = new Basis(Vector3.Up, -steerA) * basis;
            node.Transform = new Transform3D(basis, Coords.ToGodot(centre with { Y = centre.Y + travelA }));
        }
    }
}
