using Godot;
using I76.Sim.Sound;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// What combat looks and sounds like: rounds in flight, the original's XDFC
/// sprite animations for muzzles and explosions, flashes, fire and impact
/// sounds where they happen. In the modern look rounds are glowing streaks
/// with sparks, dust, smoke trails and light (ModernFx); 1997 mode draws
/// them as plain bars. Rounds are interpolated between sim ticks.
/// </summary>
public partial class CombatView : Node3D
{
    public required Combat Combat { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public Assets.AudioBank? Audio { get; init; }
    public EffectSprites? Sprites { get; init; }
    /// <summary>The car a vehicle is drawn as, so a weapon's sound rides with it.</summary>
    public Func<I76.Sim.Vehicles.Vehicle, Node3D?>? CarOf { get; init; }
    /// <summary>How far the frame is between the last sim tick and the next (SimDriver.Alpha).</summary>
    public Func<float>? Alpha { get; init; }
    /// <summary>A network client's own predicted rounds (Combat.PredictedRounds) step on its input clock: how far the frame is into its next tick.</summary>
    public Func<float>? PredictedAlpha { get; init; }
    private readonly List<Round> _all = [];
    /// <summary>The mission's light for explosion smoke: 1 by day, lower at night.</summary>
    public float SceneLight { get; init; } = 1f;
    /// <summary>The modern look (false in 1997 mode).</summary>
    public Func<bool>? Modern { get; init; }
    /// <summary>True for the car the camera sits in: its own muzzle flashes hang off a body the in-car view does not draw.</summary>
    public Func<I76.Sim.Vehicles.Vehicle, bool>? Inside { get; init; }
    /// <summary>For the rounds' own models (GDFC OGEO: the mine, the rocket, the mortar shell).</summary>
    public World.MissionBuilder? Builder { get; init; }
    private readonly Dictionary<I76.Formats.Bwd2.GizmoDef, Mesh?> _ordnance = [];
    private readonly List<MeshInstance3D> _models = [];
    private ModernFx _fx = null!;
    private Contrails _contrails = null!;
    private readonly List<MeshInstance3D> _streaks = [];
    private RocketSmoke _smoke = null!;
    private RocketSmoke _sootSmoke = null!;
    private readonly HashSet<Round> _smoking = [];
    private readonly HashSet<Round> _balls = [];
    private readonly List<Round> _gone = [];
    private readonly Dictionary<Weapon, Voice3D> _gunSounds = [];

    /// <summary>Looping fire voices (sound mode 1): the time each one's weapon last fired.</summary>
    private readonly Dictionary<Weapon, double> _gunLastShot = [], _gunGap = [], _gunPrevShot = [];

    /// <summary>
    /// The fire sound, as the original plays it. Sound mode 1
    /// (GDFC +0x80: every MG, flamers, droppers, rockets, the .45) is one
    /// looping voice per weapon, kept alive by each shot and stopped once a
    /// shot is overdue (1.5 shot intervals, but never past one run of the
    /// sample; the original stopped it at the first frame end without a
    /// shot). Mode 0 (the 20/25/30 mm and HADES
    /// cannons) plays the sample once per shot. The voice rides on the car,
    /// heard as DirectSound3D heard it (Voice3D, min distance 10); a looping
    /// voice is neither started nor kept alive 600 m or more from the
    /// player's car.
    /// </summary>
    private void GunSound(ShotFired s)
    {
        var name = s.Weapon.Def.FireSound;
        bool loop = s.Weapon.Def.SoundMode == 1;
        if (Audio is null || name.Length == 0 || name.Equals("null", StringComparison.OrdinalIgnoreCase) || Audio.Get(name, loop) is not { } stream) return;
        if (loop && Combat.World.Player is { } me && !Ds3d.LoopStarts((s.Shooter.Pos.ToVector3() - me.Pos.ToVector3()).Length())) return;
        if (!_gunSounds.TryGetValue(s.Weapon, out var p) || !IsInstanceValid(p))
        {
            var car = CarOf?.Invoke(s.Shooter);
            p = new Voice3D { Stream = stream, BaseDb = -4 };
            if (car is not null) { car.AddChild(p); p.Position = Coords.ToGodot(s.Weapon.MuzzlePos); }
            else { AddChild(p); p.Position = G(s.Muzzle); }
            _gunSounds[s.Weapon] = p;
        }
        if (loop)
        {
            // the gap to the shot before, if it was a burst's (not an idle pause's): how often this weapon really fires
            if (_gunPrevShot.TryGetValue(s.Weapon, out var before)) _gunGap[s.Weapon] = _t - before <= 0.4 ? _t - before : 0;
            _gunPrevShot[s.Weapon] = _t;
            _gunLastShot[s.Weapon] = _t;
            if (!p.Playing) { p.Play(); Voice3D.Trace($"gun loop start: {name} t={_t:0.000} v={s.Shooter.Speed:0.0}"); }       // a running loop is only kept alive, never restarted
        }
        else p.Start();
    }

    private readonly Dictionary<string, Voice3D> _lockTones = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lockWanted = [];

    /// <summary>
    /// The 4Get-U-Not's tones for the player's car (Combat.LockTone, Combat.LockedOn): looping voices kept
    /// while the sim asks for them each frame and stopped the first frame it does not.
    /// </summary>
    private void LockTones()
    {
        _lockWanted.Clear();
        if (Combat.World.Player is { } me)
        {
            if (Combat.LockTone.TryGetValue(me, out var tone)) _lockWanted.Add(tone);
            if (Combat.LockedOn.Contains(me)) _lockWanted.Add("msllock3.wav");
        }
        var car = Combat.World.Player is { } p ? CarOf?.Invoke(p) : null;
        foreach (var (name, v) in _lockTones.ToList())
        {
            if (IsInstanceValid(v) && car is not null && v.GetParent() == car && _lockWanted.Contains(name)) continue;
            if (IsInstanceValid(v)) v.QueueFree();
            _lockTones.Remove(name);
        }
        if (car is null) return;
        foreach (var name in _lockWanted)
        {
            if (_lockTones.ContainsKey(name) || Audio?.Get(name, loop: true) is not { } st) continue;
            var v = new Voice3D { Stream = st, Autoplay = true };
            car.AddChild(v);
            _lockTones[name] = v;
        }
    }

    /// <summary>
    /// Stops the looping fire voices whose weapon has stopped firing. It depends on the sim's shots and this node's clock alone,
    /// never on the camera, the view or the car's model: a voice that is parked by the cull or whose car was freed is stopped and
    /// forgotten as well, so nothing can start it again.
    /// </summary>
    private void StopIdleGuns()
    {
        foreach (var (w, last) in _gunLastShot.ToList())
        {
            if (!_gunSounds.TryGetValue(w, out var p) || !IsInstanceValid(p)) { _gunLastShot.Remove(w); _gunGap.Remove(w); _gunPrevShot.Remove(w); _gunSounds.Remove(w); continue; }
            // never past one run of the sample: a slow weapon (a mine a second) plays it once, not round again
            // The original's frame was its sim step and a shot came every frame, so "overdue" meant one rate interval. Here shots
            // come once per sim tick (a network client's predicted ones jittering by a render frame, a dropper's dribble farther
            // apart still), and the dropper's rate (60 a second) is no cadence at all: a voice must outlast two ticks and one and
            // a half of the gap it has been seeing, or it is cut after a few milliseconds and restarted by the next shot.
            double keep = System.Math.Max(1.5 / System.Math.Max(w.Def.Rate, 0.5f), System.Math.Max(2 * SimClock.Tick, 1.5 * _gunGap.GetValueOrDefault(w)));
            keep = System.Math.Min(keep, (p.Stream?.GetLength() ?? 1) * 0.98);
            if (_t - last <= keep) continue;
            if (p.Playing) Voice3D.Trace($"gun loop stop: {w.Def.FireSound} t={_t:0.000} keep={keep:0.000}");
            p.Release();
            _gunLastShot.Remove(w);
        }
    }

    private readonly List<(Sprite3D Node, Texture2D[] Frames, double Start)> _anims = [];
    private readonly List<MeshInstance3D> _pool = [];
    private readonly List<(Node3D Node, double Until, double Start, float Size)> _flashes = [];
    private StandardMaterial3D _tracer = null!, _missile = null!, _flash = null!;
    private BoxMesh _box = null!;
    private ArrayMesh _shell = null!;
    private StandardMaterial3D? _shellMat;
    private bool _shellMatTried;
    private SphereMesh _ball = null!;
    private double _t;

    public override void _Ready()
    {
        _tracer = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.85f, 0.4f), EmissionEnabled = true, Emission = new Color(1f, 0.7f, 0.2f) };
        _missile = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.55f, 0.2f) };
        _flash = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(1f, 0.6f, 0.15f, 0.8f) };
        _box = new BoxMesh { Size = new Vector3(0.06f, 0.06f, 1f) };
        _shell = ShellMesh();
        _ball = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };
        _fx = new ModernFx { Name = "Fx" };
        AddChild(_fx);
        // the 1997 look's contrails behind rockets and mortar rounds, in the mission's own trail textures
        _contrails = new Contrails
        {
            Name = "Contrails", Origin = Origin, SceneLight = SceneLight,
            Texture = n => Builder?.Resolver?.FindByStem(n) is { } m && Builder.Textures?.Get(m) is { } t ? t.Texture : null,
        };
        AddChild(_contrails);
        _oil = new OilSlicks { Name = "OilSlicks", GroundAt = DrawnY };
        AddChild(_oil);
        _fire = new FireDrops { Name = "FireDrops", GroundAt = DrawnY };
        AddChild(_fire);
        _caltrops = new CaltropPatches { Name = "Caltrops", GroundAt = DrawnY, Origin = Origin };
        AddChild(_caltrops);
        _chem = new ChemClouds { Name = "ChemClouds", SceneLight = SceneLight, Texture = ImageByStem };
        AddChild(_chem);
        // the flame throwers' streams: the original's frames in the 1997 look, NapalmModern in the modern
        AddChild(new FlameStreams { Name = "Flames", Combat = Combat, Origin = Origin, CarOf = CarOf, Texture = TextureByStem, Modern = () => IsModern, SceneLight = SceneLight });
        _fx.SceneLight = SceneLight;
        _smoke = new RocketSmoke { Name = "RocketSmoke", Origin = Origin, SceneLight = SceneLight };
        AddChild(_smoke);
        // the Ball O'Flame's tail of soot: its own, darker, thin smoke
        _sootSmoke = new RocketSmoke { Name = "BallSmoke", Origin = Origin, SceneLight = SceneLight, Tint = new System.Numerics.Vector3(0.38f, 0.36f, 0.34f) };
        AddChild(_sootSmoke);
    }

    private bool IsModern => Modern?.Invoke() ?? true;

    private Texture2D? TextureByStem(string n) => Builder?.Resolver?.FindByStem(n) is { } m && Builder.Textures?.Get(m) is { } t ? t.Texture : null;

    private OilSlicks _oil = null!;
    private FireDrops _fire = null!;
    private CaltropPatches _caltrops = null!;
    private ChemClouds _chem = null!;

    private Image? ImageByStem(string n) =>
        Builder?.Resolver?.FindByStem(n) is { } m && Builder.Textures?.GetRgba(m) is { } t ? Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba) : null;
    private readonly Dictionary<object, double> _slickVoice = [];
    private readonly Dictionary<object, double> _chemBlast = [];
    private readonly Dictionary<string, StandardMaterial3D?> _slickMaterials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A dropped slick's texture by its age (the default texture animation tick):
    /// oil plays XOS1_101..108 once at 6 a second and stays a full black pool; fire loops the burning
    /// row of its table (XFP1_201, 202, 203, ...: eight steps) at 6 a second; caltrops play MCALT101..108 once at 6 a second, the jacks spreading out (#104).
    /// </summary>
    private StandardMaterial3D? SlickMaterial(Round r)
    {
        int step = (int)(r.Age * 6f);
        string name = r.Kind == 0x11 ? $"XFP1_20{step % 8 % 3 + 1}" : r.Kind == 0xe ? $"MCALT10{System.Math.Min(step, 7) + 1}" : $"XOS1_10{System.Math.Min(step, 7) + 1}";
        if (_slickMaterials.TryGetValue(name, out var m)) return m;
        m = TextureByStem(name) is { } t
            ? new StandardMaterial3D
            {
                AlbedoTexture = t, Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            }
            : null;
        return _slickMaterials[name] = m;
    }

    /// <summary>
    /// The Ball O'Flame's fireball (Nitro, kind 0x15; the Nitro Pack's spawn): its MBALFIRE quad is flagged a
    /// camera-facing card and plays the XFS1_101 table's first row, XFS1_101..106,
    /// as a loop at six frames a second (1/6 s a frame, row 0, 6 frames, looping); both looks draw this.
    /// </summary>
    private StandardMaterial3D? FireballMaterial(Round r)
    {
        string name = I76.Formats.Assembly.FireballCard.Frame(r.Age);
        if (_slickMaterials.TryGetValue(name, out var m)) return m;
        m = TextureByStem(name) is { } t
            ? new StandardMaterial3D
            {
                AlbedoTexture = t, Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            }
            : null;
        return _slickMaterials[name] = m;
    }

    private static bool IsRocket(uint kind) => kind is 2 or 3 or 8 or 0x14;
    /// <summary>Dropped behind the car (mines, Blox, oil, fire): no tracer.</summary>
    private static bool IsDropped(uint kind) => kind is 0xc or 0xe or 0xf or 0x10 or 0x11 or 0x13;
    /// <summary>Drawn with the ordnance's own model rather than a bar (the chemical mortar's shell only in flight: it is released on landing).</summary>
    private static bool HasModel(Round r) => IsDropped(r.Kind) || IsRocket(r.Kind) || r.Kind is 4 or 5 or 7 or 0x15 || (r.Kind == 0x16 && !r.Settled);

    /// <summary>The gizmo's OGEO node as a mesh (cached per weapon).</summary>
    private Mesh? OrdnanceMesh(I76.Formats.Bwd2.GizmoDef def)
    {
        if (_ordnance.TryGetValue(def, out var m)) return m;
        m = null;
        if (Builder?.Assembler is { } asm && Builder.Meshes is { } meshes)
        {
            var model = new I76.Formats.Assembly.AssembledModel { Definition = def.File, Kind = "sdf" };
            foreach (var n in def.OrdnanceGeometry)
                if (asm.Geo(n.Name) is { } geo && geo.Vertices.Length > 0)
                    model.Parts.Add(new I76.Formats.Assembly.ModelPart(n.Name, geo, I76.Formats.Assembly.Affine.From(n.Transform)));
            if (model.Parts.Count > 0) m = meshes.Build(model);
        }
        return _ordnance[def] = m;
    }

    /// <summary>
    /// The 1997 look's cannon round (tracer style 1): a three-sided tube of 0.08 m corner radius along the round's
    /// flight, textured with MCANNON.MAP. The tube's ends are at z = -0.5 (head) and +0.5, u running from the head back.
    /// A machine gun's round (style 2) is only a one-pixel line.
    /// </summary>
    private static ArrayMesh ShellMesh()
    {
        const float R = 0.08f;
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            float a0 = i * MathF.Tau / 3, a1 = (i + 1) * MathF.Tau / 3;
            var p0 = new Vector3(MathF.Sin(a0) * R, MathF.Cos(a0) * R, 0);
            var p1 = new Vector3(MathF.Sin(a1) * R, MathF.Cos(a1) * R, 0);
            int b = v.Count;
            v.Add(p0 + new Vector3(0, 0, -0.5f)); uv.Add(new Vector2(0, 0));
            v.Add(p1 + new Vector3(0, 0, -0.5f)); uv.Add(new Vector2(0, 1));
            v.Add(p1 + new Vector3(0, 0, 0.5f)); uv.Add(new Vector2(1, 1));
            v.Add(p0 + new Vector3(0, 0, 0.5f)); uv.Add(new Vector2(1, 0));
            idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var m = new ArrayMesh();
        m.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
        return m;
    }

    /// <summary>MCANNON.MAP as the cannon round's material (null when the install lacks it: the plain bar stands in).</summary>
    private StandardMaterial3D? ShellMaterial()
    {
        if (_shellMatTried) return _shellMat;
        _shellMatTried = true;
        if (Builder?.Resolver?.FindByStem("MCANNON") is { } name && Builder.Textures?.Get(name) is { } t)
            _shellMat = new StandardMaterial3D
            {
                AlbedoTexture = t.Texture, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
        return _shellMat;
    }

    /// <summary>
    /// A machine gun's tracer colour: the original draws the round as a line in the mission palette's entry its gizmo's ORDF
    /// names (the palette's RGB table is read by that index): 240 white (30 cal), 223 yellow (50 cal), 108 red-orange
    /// (7.62), 215 green (the Nitro Pack's 12.5 mm). Null for anything else (the .45 names entry 0, black) or without a palette.
    /// </summary>
    private Color? TracerColor(Round r)
    {
        if (r.Kind != 1 || r.Ord.Tracer == 0 || r.Ord.Tracer > 255 || Builder?.ActPalette is not { } pal) return null;
        var c = pal[(int)r.Ord.Tracer];
        return Color.Color8(c.R, c.G, c.B);
    }

    private readonly Dictionary<uint, StandardMaterial3D> _tracerMats = [];
    private StandardMaterial3D TracerMaterial(Color c)
    {
        uint key = c.ToRgba32();
        if (!_tracerMats.TryGetValue(key, out var m))
            _tracerMats[key] = m = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c };
        return m;
    }

    /// <summary>The streak's look by ordnance kind: colour, length behind the head, half-width, brightness.</summary>
    private static (Color Tint, float Length, float Width, float Energy) StreakLook(uint kind) => kind switch
    {
        6 => (new Color(1f, 0.5f, 0.15f), 7f, 0.6f, 2.0f),                       // cannons: a short fat bolt (the original's tube against the guns' one-pixel line)
        2 or 3 or 8 or 0x14 => (new Color(1f, 0.42f, 0.1f), 3.5f, 0.5f, 2.2f),   // rocket exhaust
        4 or 5 or 7 or 0x16 => (new Color(1f, 0.7f, 0.35f), 1.2f, 0.3f, 1.4f),   // mortar shells
        _ => (new Color(1f, 0.68f, 0.25f), 10f, 0.12f, 1.3f),                    // machine guns, the .45
    };

    /// <summary>
    /// The modern look's volumetric explosion for an XDFC, by what it is: car
    /// deaths, buildings, grenades and phosphorus, cluster bombs, missiles,
    /// mines. Bullet puffs and flames (no splash) keep the original sprites.
    /// </summary>
    private static (float Size, double Life, Color Smoke, float Fire)? Blast(I76.Formats.Bwd2.EffectDef e)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(e.File).ToLowerInvariant();
        var dark = new Color(0.16f, 0.15f, 0.14f);
        if (n.StartsWith("xcarx")) return (6f, 3.2, dark, 1f);
        if (n.StartsWith("xcars")) return (3f, 2.2, dark, 1f);
        if (n.StartsWith("xbld")) return (10f, 4.0, dark, 1f);
        if (n.StartsWith("xwhph")) return (7f, 4.5, new Color(0.85f, 0.85f, 0.82f), 0.45f);   // white phosphorus: white smoke
        if (n.StartsWith("xgren")) return (6f, 2.8, dark, 1f);
        if (n.StartsWith("xclst")) return (4.5f, 2.4, dark, 1f);
        if (n.StartsWith("xmine")) return (4f, 2.4, new Color(0.3f, 0.25f, 0.2f), 1f);
        if (n.StartsWith("xmsl")) return (3f, 2.0, dark, 1f);
        if (e.SplashDamage > 0 && !n.StartsWith("xfire")) return (Mathf.Clamp(e.SplashRadius * 0.3f, 2.5f, 8f), 2.5, dark, 1f);
        return null;
    }

    private Vector3 CameraPos() => GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;

    private Vector3 G(System.Numerics.Vector3 p) => Coords.ToGodot(p - Origin());

    private World.Drawn1997? _drawn;

    /// <summary>
    /// The drawn ground's height (Godot y) under a Godot-space point at the level of something lying on the sim's
    /// ground: the terrain mesh's triangles (Drawn1997.Terrain; both looks paint the roads into it), which part from
    /// the sim's bilinear height by up to about 0.3 m. On a structure (a bridge, a ramp: the sim's surface there is off
    /// the bare terrain) the point keeps its own height.
    /// </summary>
    private float DrawnY(Vector3 g)
    {
        if (Builder?.World is not { } w) return g.Y;
        var o = Origin();
        var p = new System.Numerics.Vector3(g.X + o.X, g.Y + o.Y, -g.Z + o.Z);
        if (OnStructure(p)) return g.Y;
        _drawn ??= new World.Drawn1997(w, Builder.RoadPieces);
        return _drawn.Terrain(p.X, p.Z) - o.Y;
    }

    private bool OnStructure(System.Numerics.Vector3 p) =>
        MathF.Abs(Combat.World.Ground.Query(p.X, p.Y, p.Z).Height - Combat.World.Ground.BareHeight(p.X, p.Z)) > 0.05f;

    // the 1997 look's slicks (oil, fire, caltrops), laid on the drawn ground: the original painted its ground first and
    // these over it, so its flat 4 x 4 m quad never sank into a slope; with a depth buffer a flat quad does, in patches
    private readonly Dictionary<Round, (ArrayMesh Mesh, System.Numerics.Vector3 At)> _draped = [];
    private readonly List<Round> _drapedGone = [];
    private const float SlickLift = 0.04f, SlickSpacing = 0.5f;

    /// <summary>A slick's quad as a grid on the drawn ground, local to the drop; null on a structure (the flat quad lies right there).</summary>
    private ArrayMesh? DrapedSlick(Round r, Mesh model)
    {
        if (_draped.TryGetValue(r, out var known) && known.At == r.Pos) return known.Mesh;
        if (Builder?.World is null || OnStructure(r.Pos) || model.GetSurfaceCount() == 0) return null;
        var at = G(r.Pos);
        var arrays = model.SurfaceGetArrays(0);
        var vs = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        if (vs.Length < 3 || uvs.Length != vs.Length) return null;
        // the quad's corners in its own plane (x, z) and their texture coordinates, bilinear between them
        float x0 = vs.Min(v => v.X), x1 = vs.Max(v => v.X), z0 = vs.Min(v => v.Z), z1 = vs.Max(v => v.Z);
        Vector2 Corner(float cx, float cz)
        {
            int best = 0;
            for (int i = 1; i < vs.Length; i++)
                if (new Vector2(vs[i].X - cx, vs[i].Z - cz).LengthSquared() < new Vector2(vs[best].X - cx, vs[best].Z - cz).LengthSquared()) best = i;
            return uvs[best];
        }
        Vector2 c00 = Corner(x0, z0), c10 = Corner(x1, z0), c01 = Corner(x0, z1), c11 = Corner(x1, z1);
        var basis = Coords.ToGodot(r.Rot.ToEngine());
        var centre = at + basis * new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2);
        var mesh = GroundMark.Drape(basis.X, basis.Z, centre, x1 - x0, z1 - z0, SlickSpacing, SlickLift, DrawnY,
            (u, v) => c00.Lerp(c10, u).Lerp(c01.Lerp(c11, u), v));
        // local to the drop, so the node stands at the drop wherever the origin is
        var shift = centre - at;
        var a = mesh.SurfaceGetArrays(0);
        var verts = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        for (int i = 0; i < verts.Length; i++) verts[i] += shift;
        a[(int)Mesh.ArrayType.Vertex] = verts;
        mesh.ClearSurfaces();
        mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, a);
        _draped[r] = (mesh, r.Pos);
        return mesh;
    }

    /// <summary>A one-shot where something happened, heard as DirectSound3D heard it (Voice3D).</summary>
    private void Sound(string name, System.Numerics.Vector3 at, float db = 0, float minDistance = Ds3d.MinDistance)
    {
        if (Audio is null || name.Length == 0 || name.Equals("null", StringComparison.OrdinalIgnoreCase) || Audio.Get(name) is not { } s) return;
        var p = Voice3D.OneShot(s, name, Audio.Limits(name), minDistance, db);
        p.Position = G(at);
        AddChild(p);
        p.Start();
    }

    private readonly Dictionary<string, EffectSprites.Look?> _muzzles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GDFC +0x66 names the muzzle flash's .xdf (xmuzz1: X1_MUZF1 front, X1_MUZS1 side).</summary>
    private EffectSprites.Look? MuzzleLook(string xdf)
    {
        if (Sprites is null || xdf.Length == 0 || xdf.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (_muzzles.TryGetValue(xdf, out var l)) return l;
        var def = Combat.Effects(xdf);
        return _muzzles[xdf] = def is null ? null : Sprites.Get(def);
    }

    /// <summary>A billboard sprite playing the effect's frames at 10 per second.</summary>
    private bool Animate(EffectSprites.Look? look, System.Numerics.Vector3 at, Node3D? on = null, System.Numerics.Vector3 local = default)
    {
        if (look is null) return false;
        var tex = look.Frames[0];
        var sp = new Sprite3D
        {
            Texture = tex, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, DoubleSided = true,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard, PixelSize = look.Size / MathF.Max(1, tex.GetWidth()),
            Position = on is null ? G(at) : Coords.ToGodot(local), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        (on ?? (Node)this).AddChild(sp);
        _anims.Add((sp, look.Frames, _t));
        return true;
    }

    /// <summary>The modern muzzle flash on each weapon, re-lit by every shot, and when it was lit.</summary>
    private readonly Dictionary<Weapon, (MeshInstance3D Node, double Start, float Energy)> _muzzleFlashes = [];
    private readonly Dictionary<Weapon, double> _lastSmoke = [];
    private const double MuzzleLife = 0.05;

    /// <summary>The flash's size by gun: the tongue's width and length, and its brightness.</summary>
    private static (float Width, float Length, float Energy) MuzzleSize(uint kind) => kind switch
    {
        6 => (0.75f, 1.9f, 3f),          // cannons
        0x12 => (0.28f, 0.5f, 2f),       // the driver's .45s
        _ => (0.45f, 1.2f, 2.5f),        // machine guns
    };

    /// <summary>
    /// The modern look's flash for a shot (in place of the original's
    /// xmuzz sprite): a flame tongue and star at the muzzle, turned and
    /// sized afresh each shot, and a whiff of smoke left in the air.
    /// </summary>
    private void ModernMuzzle(ShotFired s)
    {
        var car = CarOf?.Invoke(s.Shooter);
        if (!_muzzleFlashes.TryGetValue(s.Weapon, out var mf) || !IsInstanceValid(mf.Node) || mf.Node.GetParent() != (car ?? (Node)this))
        {
            if (mf.Node is not null && IsInstanceValid(mf.Node)) mf.Node.QueueFree();
            mf.Node = _fx.NewMuzzleFlash();
            (car ?? (Node)this).AddChild(mf.Node);
        }
        var (w, l, e) = MuzzleSize(s.Weapon.Def.Kind);
        float jitter = (float)GD.RandRange(0.8, 1.2);
        var fwd = car is not null ? Coords.ToGodot(s.Weapon.MuzzleRot.Forward) : Coords.ToGodot(s.Forward);
        var f = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Forward;
        // -Z down the barrel, a random roll about it
        var b = Basis.LookingAt(f, Mathf.Abs(f.Y) > 0.95f ? Vector3.Forward : Vector3.Up).Rotated(f, (float)GD.RandRange(0, Mathf.Tau));
        var basis = new Basis(b.Column0 * w * jitter, b.Column1 * w * jitter, b.Column2 * l * jitter);
        var at = car is not null ? Coords.ToGodot(s.Weapon.MuzzlePos) : G(s.Muzzle);
        mf.Node.Transform = new Transform3D(basis, at);
        mf.Node.Visible = true;
        ModernFx.SetMuzzleFlash(mf.Node, 0, (float)GD.Randf(), e);
        _muzzleFlashes[s.Weapon] = (mf.Node, _t, e);
        // smoke at most ten times a second a gun, so a machine gun leaves a haze rather than a wall
        if (!_lastSmoke.TryGetValue(s.Weapon, out var last) || _t - last > 0.1)
        {
            _lastSmoke[s.Weapon] = _t;
            _fx.GunSmoke(G(s.Muzzle + s.Forward * (l * 0.5f)), Coords.ToGodot(s.Forward), w * 2);
        }
    }

    private void Flash(System.Numerics.Vector3 at, float size, double life)
    {
        var m = new MeshInstance3D { Mesh = _ball, MaterialOverride = _flash, Position = G(at), Scale = Vector3.One * size * 0.3f };
        var light = new OmniLight3D { LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 2f, OmniRange = size * 4 };
        m.AddChild(light);
        AddChild(m);
        _flashes.Add((m, _t + life, _t, size));
    }

    private bool _wasModern = true;

    /// <summary>F12: the other look's effects in flight go at once (the burning, smoking ones come back by themselves from the rounds and flames still there).</summary>
    private void SwitchLook(bool modern)
    {
        _wasModern = modern;
        _contrails.Clear();
        _smoke.Clear();
        _sootSmoke.Clear();
        if (!modern)
        {
            _fx.Clear();
        }
        else
        {
            foreach (var a in _anims) if (IsInstanceValid(a.Node)) a.Node.QueueFree();
            _anims.Clear();
        }
    }

    private ModernFx? _warmFx;

    /// <summary>
    /// The first-use rehearsal of the explosion effects, in a private ModernFx (the game's own is not touched) at <paramref name="at"/>,
    /// out of everyone's sight; <see cref="EndWarm"/> frees it. The renderer makes a material's pipelines when its instance is made,
    /// seen or not.
    /// </summary>
    public void Warm(Vector3 at)
    {
        if (!IsModern || !_fx.HasExplosions) return;
        _warmFx = new ModernFx { Name = "WarmFx", SceneLight = SceneLight, ProcessMode = ProcessModeEnum.Always };
        AddChild(_warmFx);
        _warmFx.Warm(at);
        // the sounds a blast and a death use, decoded now rather than in the frame of the first one
        Audio?.Get("vexplode.wav");
        Audio?.Get("tblow.wav");
    }

    private readonly List<Sprite3D> _warmSprites = [];

    /// <summary>
    /// The impacts of every weapon on the field, made ready behind the loading card: the sprite look of each impact effect
    /// (its geometry and textures) and its sound, and one sprite at <paramref name="at"/> so that its pipeline is made.
    /// The first bullet that hit a car cost 20 ms (the effect's look 10, its sound 4), a stall as the fight began.
    /// </summary>
    public void WarmImpacts(Vector3 at)
    {
        var ords = Combat.World.Vehicles.Select(v => v.Armory).OfType<Armory>().SelectMany(a => a.Weapons).Select(w => w.Def.Ordnance)
            .Append(Armory.HandgunDef(1).Ordnance).OfType<I76.Formats.Bwd2.OrdnanceDef>().DistinctBy(o => string.Join('|', o.ImpactXdf) + string.Join('|', o.ImpactWav));
        EffectSprites.Look? any = null;
        foreach (var o in ords)
        {
            foreach (var fx in o.ImpactXdf)
                if (fx.Length > 0 && !fx.Equals("null", StringComparison.OrdinalIgnoreCase) && Combat.Effects(fx) is { } e && Sprites?.Get(e) is { } look) any ??= look;
            foreach (var wav in o.ImpactWav) if (wav.Length > 0 && !wav.Equals("null", StringComparison.OrdinalIgnoreCase)) Audio?.Get(wav);
        }
        if (any is null) return;
        var tex = any.Frames[0];
        var sp = new Sprite3D
        {
            Texture = tex, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, DoubleSided = true,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard, PixelSize = any.Size / MathF.Max(1, tex.GetWidth()),
            Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(sp);
        _warmSprites.Add(sp);
    }

    public void EndWarm()
    {
        foreach (var sp in _warmSprites) sp.QueueFree();
        _warmSprites.Clear();
        _warmFx?.QueueFree();
        _warmFx = null;
    }

    public override void _Process(double delta)
    {
        using var _view = Hitch.Time(Hitch.Phase.Views);
        _t += delta;
        bool skipBlast = false;
        foreach (var e in Combat.Events)
        {
            if (e is not Explosion) skipBlast = false;
            switch (e)
            {
                case ShotFired s:
                    GunSound(s);
                    // the flash rides on the muzzle (it hangs off the muzzle node), not where the car was;
                    // only weapons whose GDFC names one have it (the guns' xmuzz1), droppers and launchers none
                    if (Inside?.Invoke(s.Shooter) != true && MuzzleLook(s.Weapon.Def.MuzzleFlash) is { } ml)
                    {
                        if (IsModern) ModernMuzzle(s);
                        else Animate(ml, s.Muzzle + s.Forward * 0.3f, CarOf?.Invoke(s.Shooter), s.Weapon.MuzzlePos + s.Weapon.MuzzleRot.Forward * 0.3f);
                    }
                    if (IsModern && !IsDropped(s.Weapon.Def.Kind) && s.Weapon.Def.Class != 5 && G(s.Muzzle).DistanceTo(CameraPos()) < 250)
                        _fx.Light(G(s.Muzzle + s.Forward * 1.2f), 0.8f, 4, 0.05, new Color(1f, 0.75f, 0.4f));
                    break;
                case Impact i when i.Kind is 0xc or 0xe or 0x11 || (i.Kind == 0x16 && i.Victim is not null):
                    // a slick's hit test fires every frame a car is over it: one voice at a time per car (the
                    // voice is the object's own, so a replay restarts it), and no flash; so does the chemical cloud's,
                    // whose xclst1 burst every frame is drawn at most four times a second a car
                    if (i.Victim is { } sv && (!_slickVoice.TryGetValue(sv, out var last) || _t - last > 0.5))
                    {
                        _slickVoice[sv] = _t;
                        Sound(i.Sound, i.At, -2, Ds3d.LoudMinDistance);
                    }
                    if (i.Kind == 0x16 && i.Victim is { } cv)
                    {
                        skipBlast = _chemBlast.TryGetValue(cv, out var lb) && _t - lb < 0.25;
                        if (!skipBlast) _chemBlast[cv] = _t;
                        continue;
                    }
                    break;
                case Impact i:
                    // The impact's voice is flagged 0x200, min distance 100
                    Sound(i.Sound, i.At, -2, Ds3d.LoudMinDistance);
                    bool bullet = i.Kind is 1 or 6 or 0x12;
                    if (IsModern && i.Kind == 0x15 && _fx.HasExplosions) _fx.FireBurst(G(i.At));
                    if (IsModern && bullet && i.Hit != default && G(i.Hit).DistanceTo(CameraPos()) < 400)
                    {
                        // off a surface the sparks glance away along the reflection; off the ground, up and back
                        var d = i.Dir; var n = i.N;
                        var spray = n.LengthSquared() > 0.5f
                            ? System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Reflect(d, n) * 0.6f + n * 0.4f)
                            : System.Numerics.Vector3.Normalize(-d * 0.5f + System.Numerics.Vector3.UnitY);
                        _fx.Strike(G(i.Hit), Coords.ToGodot(spray), i.Victim is null || i.Victim is I76.Sim.Collision.StaticObject, i.Kind == 6);
                    }
                    else if (i.Effect.Length == 0 || i.Effect.Equals("null", StringComparison.OrdinalIgnoreCase)) Flash(i.At, 0.5f, 0.08);
                    break;
                case Explosion when skipBlast:
                    break;
                // the napalm stream hit burst (xflht1): the modern look draws the fire on the target itself (NapalmModern)
                case Explosion x when IsModern && System.IO.Path.GetFileNameWithoutExtension(x.Effect.File).StartsWith("xflht", StringComparison.OrdinalIgnoreCase):
                    break;
                case Explosion x when IsModern && _fx.HasExplosions && Blast(x.Effect) is { } bl:
                    _fx.Explode(G(x.At), bl.Size, bl.Life, bl.Smoke, bl.Fire);
                    if (System.IO.Path.GetFileNameWithoutExtension(x.Effect.File).Equals("xbldx2", StringComparison.OrdinalIgnoreCase)) _fx.Fireworks(G(x.At));
                    break;
                case Explosion x when Sprites?.Get(x.Effect) is { } look:
                    Animate(look, x.At);
                    break;
                case Explosion x:
                    if (x.Effect.SplashDamage > 0) Flash(x.At, MathF.Max(1.5f, x.Effect.SplashRadius * 0.25f), System.Math.Clamp(x.Effect.Life, 0.2, 1.2));
                    else Flash(x.At, 0.35f, 0.08);
                    break;
                // the driver's own (cammo.wav, clockon.wav): the original plays them on the local player alone
                case WeaponClick c when Combat.IsDriverSound(c.Sound):
                    if (c.Shooter == Combat.World.Player && CarOf?.Invoke(c.Shooter) is CarNode own)
                    {
                        if (c.Sound.Equals("clockon.wav", StringComparison.OrdinalIgnoreCase)) own.OneShotSingle("clockon.wav", Audio?.Get(c.Sound));
                        else own.OneShot(Audio?.Get(c.Sound));
                    }
                    break;
                case WeaponClick c:
                    // The radar jammer's activation stops the lock warning as it plays cjamm.wav
                    if (c.Sound.Equals("cjamm.wav", StringComparison.OrdinalIgnoreCase) && c.Shooter == Combat.World.Player && CarOf?.Invoke(c.Shooter) is CarNode jammer)
                        jammer.StopSingle("clockon.wav");
                    if (Audio?.Get(c.Sound) is { } cs) Sound(c.Sound, c.Shooter.Pos.ToVector3());
                    break;
            }
        }
        Combat.Events.Clear();
        StopIdleGuns();
        LockTones();

        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            var (node, frames, start) = _anims[i];
            int f = (int)((_t - start) * 10);
            if (f >= frames.Length) { node.QueueFree(); _anims.RemoveAt(i); continue; }
            node.Texture = frames[f];
        }

        foreach (var (_, (node, start, e)) in _muzzleFlashes)
        {
            if (!IsInstanceValid(node) || !node.Visible) continue;
            float age = (float)((_t - start) / MuzzleLife);
            if (age >= 1f || !IsModern) node.Visible = false;
            else node.SetInstanceShaderParameter("age", age);
        }

        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var (node, until, start, size) = _flashes[i];
            if (_t >= until) { node.QueueFree(); _flashes.RemoveAt(i); continue; }
            float k = (float)((_t - start) / (until - start));
            node.Scale = Vector3.One * size * (0.3f + 0.7f * k);
        }

        // the server's rounds (or a local game's), then a network client's own predicted ones, on their own clock
        IReadOnlyList<Round> rounds = Combat.Rounds;
        int predictedFrom = rounds.Count;
        if (Combat.PredictedRounds.Count > 0)
        {
            _all.Clear();
            _all.AddRange(Combat.Rounds);
            _all.AddRange(Combat.PredictedRounds);
            rounds = _all;
        }
        bool modern = IsModern;
        if (modern != _wasModern) SwitchLook(modern);
        float serverAlpha = Alpha?.Invoke() ?? 1f, predictedAlpha = PredictedAlpha?.Invoke() ?? serverAlpha;
        float alpha = serverAlpha;
        _contrails.Frame(rounds, serverAlpha, modern, predictedFrom, predictedAlpha);
        _oil.Frame(rounds, modern, G);
        _caltrops.Frame(rounds, modern, G, r => OrdnanceMesh(r.Def)?.GetAabb());
        _fire.Frame(rounds, modern, G, CameraPos());
        _chem.Frame(rounds, modern, G, delta);
        while (_pool.Count < rounds.Count)
        {
            var m = new MeshInstance3D { Mesh = _box, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(m);
            _pool.Add(m);
        }
        while (modern && _streaks.Count < rounds.Count) _streaks.Add(_fx.NewTracer());
        while (_models.Count < rounds.Count)
        {
            var mm = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
            AddChild(mm);
            _models.Add(mm);
        }
        for (int i = rounds.Count; i < _models.Count; i++) _models[i].Visible = false;
        var live = new HashSet<Round>();
        for (int i = 0; i < System.Math.Max(_pool.Count, _streaks.Count); i++)
        {
            var body = i < _pool.Count ? _pool[i] : null;
            var streak = i < _streaks.Count ? _streaks[i] : null;
            if (i >= rounds.Count)
            {
                if (body is not null) body.Visible = false;
                if (streak is not null) streak.Visible = false;
                continue;
            }
            var r = rounds[i];
            live.Add(r);
            alpha = i < predictedFrom ? serverAlpha : predictedAlpha;
            bool rocket = IsRocket(r.Kind);
            // where it is this frame, between the last two ticks; which way it flies
            var step = r.Pos - r.PrevPos;
            var headSim = r.PrevPos + step * alpha;
            var dirSim = step.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(step) : r.Rot.Forward;
            var head = G(headSim);
            var dir = Coords.ToGodot(dirSim);
            // how far it has come from the muzzle by this frame: neither look draws it further back than that
            float flown = I76.Formats.Assembly.TracerLength.Flown(r.Travelled, step.Length(), alpha);
            // the round's own model, posed as the sim has it (mines lie flat once they settle)
            var model = HasModel(r) ? OrdnanceMesh(r.Def) : null;
            var mi = _models[i];
            mi.Visible = model is not null && !(modern && r.Kind is 0xc or 0xe or 0x11 or 0x15); // the modern look draws the fireball as ModernFx.Fireball, oil as decals (OilSlicks), fire as FireDrops and caltrops as jacks (CaltropPatches)
            if (model is not null)
            {
                // a slick lies 0.0001 over the sim's ground: laid on the drawn ground instead (DrapedSlick), its texture animated
                bool slick = r.Kind is 0xc or 0xe or 0x11;
                var draped = slick && mi.Visible ? DrapedSlick(r, model) : null;
                mi.Mesh = draped ?? model;
                bool ball = r.Kind == 0x15;
                // the fireball is a card: the frame's camera-facing turn instead of its own pose (the position stays)
                var pose = ball && GetViewport()?.GetCamera3D() is { } cam
                    ? Coords.ToGodot(I76.Formats.Assembly.Billboard.EngineRows(Coords.ToGame(-cam.GlobalBasis.Z)))
                    : Coords.ToGodot(r.Rot.ToEngine());
                mi.Transform = draped is not null ? new Transform3D(Basis.Identity, G(r.Pos)) : new Transform3D(pose, head);
                mi.MaterialOverride = slick ? SlickMaterial(r) : ball ? FireballMaterial(r) : null;
                mi.CastShadow = slick || ball ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;
                if (slick && draped is null) mi.Position += Vector3.Up * SlickLift;
            }
            if (modern && streak is not null && !IsDropped(r.Kind) && r.Kind != 0x15 && !(r.Kind == 0x16 && r.Settled))
            {
                var look = StreakLook(r.Kind);
                if (TracerColor(r) is { } tc) look.Tint = tc;
                var tail = head - dir * I76.Formats.Assembly.TracerLength.Streak(look.Length, flown);
                streak.Visible = true;
                ModernFx.SetTracer(streak, tail, head, look.Tint, look.Width, look.Energy);
            }
            else if (streak is not null) streak.Visible = false;
            if (modern && r.Kind == 0x15)
            {
                // a roiling ball of flame with a fiery tail, embers, a little smoke and its own light (ModernFx.Fireball)
                _fx.Fireball(r, head);
                _sootSmoke.Emit(r, headSim - dirSim * 0.6f, step / SimClock.Tick, 0.4f, 0.3f);
                _smoking.Add(r);
                _balls.Add(r);
            }
            if (modern && rocket)
            {
                _smoke.Emit(r, headSim - dirSim * 1.2f, step / SimClock.Tick);
                _smoking.Add(r);
            }
            if (body is null) continue;
            // the round itself: a bar along its flight (in the modern look only rockets keep a body)
            body.Visible = model is null && (!modern || rocket);
            if (!body.Visible) continue;
            var shellMat = r.Kind == 6 && !rocket ? ShellMaterial() : null;
            body.Mesh = shellMat is not null ? _shell : _box;
            body.MaterialOverride = rocket ? _missile : shellMat ?? (TracerColor(r) is { } lc ? TracerMaterial(lc) : _tracer);
            // no longer than the round has flown: a new round's bar reached up to 4 m back from the muzzle, through the gun
            // and out of a turret turned to the side, so the shot seemed to leave from the turret's base
            float len = rocket ? 1.2f : I76.Formats.Assembly.TracerLength.Bar(step.Length(), flown);
            var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up;
            var wide = shellMat is not null ? 1f : r.Kind == 6 ? 2.5f : 1f;
            var basis = Basis.LookingAt(dir, up) * Basis.FromScale(new Vector3(rocket ? 2.5f : wide, rocket ? 2.5f : wide, len));
            body.Transform = new Transform3D(basis, rocket ? head - dir * 0.6f : head - dir * (len / 2));
        }
        _gone.Clear();
        foreach (var r in _smoking) if (!modern || !live.Contains(r)) _gone.Add(r);
        foreach (var r in _gone) { _smoke.Release(r); _sootSmoke.Release(r); _smoking.Remove(r); }
        _gone.Clear();
        foreach (var r in _balls) if (!modern || !live.Contains(r)) _gone.Add(r);
        foreach (var r in _gone) { _fx.ReleaseFireball(r); _balls.Remove(r); }
        foreach (var r in _draped.Keys) if (!live.Contains(r)) _drapedGone.Add(r);
        foreach (var r in _drapedGone) _draped.Remove(r);
        _drapedGone.Clear();
    }
}
