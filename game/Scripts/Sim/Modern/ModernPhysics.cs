using Godot;
using I76.Sim;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Engine = Godot.Engine;
using N = System.Numerics;

namespace I76.Game.Sim.Modern;

/// <summary>
/// The optional modern physics (docs/modern-physics.md): the player's car as
/// a Jolt rigid body in a physics world of its own (a SubViewport with its
/// own World3D, never drawn), on a window of the mission's heightfield and
/// the scenery's colliders near it. Coordinates there are the game's own
/// (left-handed numbers taken as they are: physics does not care about
/// handedness), relative to <see cref="Origin"/>, which follows the car.
///
/// The sim keeps running everything else. Each frame it still runs the
/// object pass and the car pairs for the player (SimWorld.External): the
/// original's own detectors find scenery and car contacts and damage them
/// as the original does, with this body's state; this body answers a car
/// contact with an impulse, and Jolt itself answers scenery and terrain.
/// While the mission script holds the camera the original's model drives
/// the car, as the script's driving was tuned against it.
/// </summary>
public partial class ModernPhysics : Node
{
    public required SimWorld World { get; init; }
    public required Vehicle Car { get; init; }
    public required StaticWorld Statics { get; init; }
    public required IGround Ground { get; init; }
    /// <summary>Whether the script holds the camera (and drives the player).</summary>
    public Func<bool> Cutscene { get; init; } = () => false;
    /// <summary>On or off; switching takes effect on the next physics step.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// The player's keys or pad as they are held now (null: none to read, so the car keeps the controls the sim
    /// or a test script wrote). Read at every physics step, so the throttle and steering do not wait for the sim's tick.
    /// </summary>
    public Func<I76.Sim.Session.CarInput?>? Live { get; set; }
    public ModernCar.Tuning Tuning { get; } = new();
    /// <summary>The session's Simulation Rate: the body steps at 120 Hz whatever it is, but its handling and the keys'
    /// ramp follow the original's at this rate (<see cref="I76.Sim.Session.ModernRateTuning"/>). A switch mid-mission
    /// keeps it, as the session does.</summary>
    public I76.Sim.Session.SimRate Rate { get; init; } = I76.Sim.Session.SimRate.Default;

    /// <summary>The physics world's (0, 0, 0) in game coordinates: a terrain sample near the car.</summary>
    public Double3 Origin { get; private set; }
    public ulong TerrainId { get; private set; }
    public ModernCar Body => _car;
    /// <summary>Whether the body drives the car now.</summary>
    public bool Active { get; private set; }

    /// <summary>The heightfield window: 257² samples (1.28 km), moved when the car strays 300 from its centre.</summary>
    private const int Size = 257;
    private const float Cell = 1f / TerrainGround.Scale;
    private const float Recentre = 300f;
    private const float StaticNear = 800f, StaticFar = 1000f;
    private static readonly int PhysicsRate = 120;

    private SubViewport _vp = null!;
    private Node3D _root = null!;
    private StaticBody3D _terrain = null!;
    private HeightMapShape3D _hm = null!;
    private ModernCar _car = null!;
    private int _oldRate;
    private readonly Dictionary<StaticObject, (StaticBody3D Body, int Revision)> _statics = [];
    private (Double3 Pos, Basis B) _prev, _cur;
    private readonly bool _debug = System.Environment.GetEnvironmentVariable("I76_DEBUG_MODERN") is not null;
    private int _ticks;

    public override void _Ready()
    {
        _vp = new SubViewport { Name = "ModernPhysicsWorld", OwnWorld3D = true, Size = new Vector2I(2, 2), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Disable3D = false };
        AddChild(_vp);
        _root = new Node3D { Name = "Root" };
        _vp.AddChild(_root);
        _hm = new HeightMapShape3D { MapWidth = Size, MapDepth = Size };
        _terrain = new StaticBody3D { Name = "Terrain", CollisionLayer = 1, CollisionMask = 0, PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.6f } };
        // heights are stored divided by the cell size so that one uniform scale makes the 5-unit grid
        _terrain.AddChild(new CollisionShape3D { Shape = _hm, Scale = new Vector3(Cell, Cell, Cell) });
        _root.AddChild(_terrain);
        TerrainId = _terrain.GetInstanceId();
        ModernCar.ApplyRate(Tuning, I76.Sim.Session.ModernRateTuning.For(Rate));
        ModernCar.ApplyOverrides(Tuning, System.Environment.GetEnvironmentVariable("I76_MODERN_TUNE"));
        _car = new ModernCar { Name = "Car", Host = this, Ground = Ground, T = Tuning };
        _car.Setup(Car);
        _root.AddChild(_car);
        _oldRate = Engine.PhysicsTicksPerSecond;
        // I76_MODERN_ROLL=deg: start the car rolled over by that much, 1.5 above the ground (to see it right itself)
        if (float.TryParse(System.Environment.GetEnvironmentVariable("I76_MODERN_ROLL"), System.Globalization.CultureInfo.InvariantCulture, out var roll))
        {
            Car.Rot = Rows.Concat(Rows.AboutZ(Mathf.DegToRad(roll)), Car.Rot);
            Car.Pos = Car.Pos with { Y = Car.Pos.Y + 1.5 };
        }
        MoveOrigin(Car.Pos);
        _car.TakeOver();
        _car.Freeze = true;
        World.External = v => ReferenceEquals(v, Car) && Car.ObjectClass == 1 && Active;
        World.ExternalFrame = SimFrame;
        _prev = _cur = (Car.Pos, BasisOf(Car.Rot));
        if (_debug)
            foreach (var w in World.Vehicles)
                GD.Print($"modern vehicle at {w.Pos.X:0.0},{w.Pos.Y:0.0},{w.Pos.Z:0.0} fwd {w.Rot.Forward.X:0.00},{w.Rot.Forward.Z:0.00} mass {w.P.Mass:0}{(w == Car ? " (player)" : "")}");
        if (_debug)
            foreach (var o in Statics.Objects)
                GD.Print($"modern static {o.Name} type {o.Type} centre {o.Centre.X:0},{o.Centre.Y:0},{o.Centre.Z:0} radius {o.Radius:0.0} polys {o.Polys.Count} surfaces {o.Surfaces.Count} boxes {o.Boxes.Count} spheres {o.Spheres.Count}"
                    + string.Concat(o.Surfaces.Take(3).Select(p => $" [{p.V.Min(q => q.X):0}..{p.V.Max(q => q.X):0} x {p.V.Min(q => q.Z):0}..{p.V.Max(q => q.Z):0} y {p.V.Min(q => q.Y):0.0}..{p.V.Max(q => q.Y):0.0}]")));
        UpdateActive();
    }

    /// <summary>Hand the car to the body or back to the original's model as the option and the script say.</summary>
    private void UpdateActive()
    {
        bool want = Enabled && !Cutscene() && !Car.Wreck;
        if (want == Active) return;
        Active = want;
        // Jolt at 120 Hz while the body drives (the suspension and tyres want short steps), the project's rate otherwise
        Engine.PhysicsTicksPerSecond = want ? PhysicsRate : _oldRate;
        if (want) { MoveOrigin(Car.Pos); _car.Freeze = false; _car.TakeOver(); }
        else _car.Freeze = true;
    }

    public override void _ExitTree()
    {
        Engine.PhysicsTicksPerSecond = _oldRate;
        World.External = null;
        World.ExternalFrame = null;
    }

    private static Basis BasisOf(Rows r) => new(new Vector3(r.Right.X, r.Right.Y, r.Right.Z), new Vector3(r.Up.X, r.Up.Y, r.Up.Z), new Vector3(r.Forward.X, r.Forward.Y, r.Forward.Z));

    public override void _PhysicsProcess(double delta)
    {
        UpdateActive();
        if (!Active) { _prev = _cur = (Car.Pos, BasisOf(Car.Rot)); return; }
        var p = Car.Pos;
        if (Math.Abs(p.X - Origin.X) > Recentre || Math.Abs(p.Z - Origin.Z) > Recentre || Math.Abs(p.Y - Origin.Y) > Recentre)
            MoveOrigin(p);
        SyncStatics();
        _prev = (Origin + ToN(_car.Position), _car.Basis);
    }

    private I76.Sim.Session.DriverState _keys;

    /// <summary>
    /// The controls for one physics step of dt seconds, from the live keys: the throttle ramp by time
    /// (<see cref="I76.Sim.Session.KeyRamp"/>, the original's per-tick ramp at the session's rate, per second), steering's √(t/3), or
    /// the pad's axes. Written to the Vehicle, which the sim's tick reads for the HUD and the engine's sound.
    /// </summary>
    public void Control(float dt)
    {
        if (Live?.Invoke() is not { } input || (Car.Flags & VehicleFlags.Dead) != 0) return;
        float throttle = Car.Throttle, steer = Car.Steer;
        I76.Sim.Session.KeyRamp.Apply(ref _keys, input, dt, Rate.TickSeconds, ref throttle, ref steer);
        Car.Throttle = throttle;
        Car.Steer = steer;
        Car.Handbrake = input.Has(I76.Sim.Session.CarButtons.EBrake) ? 1 : 0;
    }

    private static N.Vector3 ToN(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The body's pose for drawing, between the last two physics steps (null while the original's model drives).</summary>
    public (Rows Rot, Double3 Pos)? LivePose()
    {
        if (!Active) return null;
        _cur = (Origin + ToN(_car.Position), _car.Basis);
        float a = (float)Engine.GetPhysicsInterpolationFraction();
        var pos = new Double3(_prev.Pos.X + (_cur.Pos.X - _prev.Pos.X) * a, _prev.Pos.Y + (_cur.Pos.Y - _prev.Pos.Y) * a, _prev.Pos.Z + (_cur.Pos.Z - _prev.Pos.Z) * a);
        var q = _prev.B.Orthonormalized().GetRotationQuaternion().Slerp(_cur.B.Orthonormalized().GetRotationQuaternion(), a);
        var b = new Basis(q);
        return (new Rows(ToN(b.X), ToN(b.Y), ToN(b.Z)), pos);
    }

    /// <summary>
    /// Re-centre the physics world on a terrain sample near <paramref name="at"/>:
    /// the heightfield window is refilled from the mission's samples, and the
    /// car and the scenery are moved by the same amount.
    /// </summary>
    private void MoveOrigin(Double3 at)
    {
        int cx = (int)Math.Round(at.X / Cell), cz = (int)Math.Round(at.Z / Cell);
        double oy = Math.Round(at.Y / 50) * 50;
        var o = new Double3(cx * Cell, oy, cz * Cell);
        var shift = new Vector3((float)(Origin.X - o.X), (float)(Origin.Y - o.Y), (float)(Origin.Z - o.Z));
        Origin = o;
        int h = (Size - 1) / 2;
        var data = new float[Size * Size];
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                data[z * Size + x] = (float)((Ground.BareHeight((cx - h + x) * (double)Cell, (cz - h + z) * (double)Cell) - oy) / Cell);
        _hm.MapData = data;
        // HeightMapShape3D is centred on its body; the window's centre sample is the origin
        _terrain.Position = Vector3.Zero;
        if (!_car.Freeze) _car.Position += shift;
        foreach (var (so, (body, _)) in _statics) body.Position = Local(so.Centre);
        SyncStatics();
    }

    private Vector3 Local(N.Vector3 world) => new((float)(world.X - Origin.X), (float)(world.Y - Origin.Y), (float)(world.Z - Origin.Z));

    /// <summary>
    /// The scenery colliders near the origin: each StaticObject as a body of
    /// its polygons (a trimesh, both faces) and its node boxes. Signs (type 4)
    /// have none, nor have a structure's sign nodes: the original's own pass
    /// knocks them over (VehicleStep.StaticContacts, signsOnly). A destroyed object
    /// goes; a collapsed one comes back as its remains.
    /// </summary>
    private void SyncStatics()
    {
        foreach (var o in Statics.Objects)
        {
            if (o.Type == 4) continue;
            double dx = o.Centre.X - Origin.X, dz = o.Centre.Z - Origin.Z;
            double d = Math.Sqrt(dx * dx + dz * dz) - o.Radius;
            _statics.TryGetValue(o, out var have);
            bool exists = have.Body is not null;
            if (exists && (!o.Solid || have.Revision != o.Revision || d > StaticFar))
            {
                _byId.Remove(have.Body!.GetInstanceId());
                have.Body!.QueueFree();
                _statics.Remove(o);
                exists = false;
            }
            if (!exists && o.Solid && d < StaticNear)
                if (Build(o) is { } b) _statics[o] = (b, o.Revision);
        }
    }

    private readonly Dictionary<ulong, StaticObject> _byId = [];

    /// <summary>The scenery object a body in the physics world stands for.</summary>
    public StaticObject? StaticOf(ulong id) => _byId.GetValueOrDefault(id);

    private StaticBody3D? Build(StaticObject o)
    {
        var body = new StaticBody3D { CollisionLayer = 1, CollisionMask = 0, Position = Local(o.Centre), PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.35f } };
        var c = o.Centre;
        var tris = new List<Vector3>();
        var seen = new HashSet<Poly>();
        foreach (var poly in o.Polys.Concat(o.Surfaces))
        {
            if (!seen.Add(poly) || poly.V.Length < 3 || o.SignNode(poly.Node)) continue;
            for (int i = 1; i + 1 < poly.V.Length; i++)
            {
                tris.Add(G(poly.V[0] - c));
                tris.Add(G(poly.V[i] - c));
                tris.Add(G(poly.V[i + 1] - c));
            }
        }
        if (tris.Count > 0) body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = [.. tris], BackfaceCollision = true } });
        foreach (var bx in o.Boxes)
        {
            if (o.SignNode(bx.Node)) continue;
            var size = G(bx.Max - bx.Min);
            if (size.X < 1e-3f || size.Y < 1e-3f || size.Z < 1e-3f) continue;
            var centre = G(bx.Origin + bx.Rot.ToWorld((bx.Min + bx.Max) * 0.5f) - c);
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = new Transform3D(BasisOf(bx.Rot), centre) });
        }
        // the class-0x4000 / 0x5000 parts' spheres (a breakable's are knocked off, never met: the sim's sign pass has them)
        foreach (var sp in o.Spheres)
        {
            if (o.Type == 4 || o.SignNode(sp.Node) || sp.Radius < 1e-3f) continue;
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = sp.Radius }, Position = G(sp.Centre - c) });
        }
        if (body.GetChildCount() == 0) { body.Free(); return null; }
        _root.AddChild(body);
        _byId[body.GetInstanceId()] = o;
        return body;
    }

    private static Vector3 G(N.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// The sim's frame for the car (SimWorld.ExternalFrame): answer a car
    /// contact the pass found, run the step's bookkeeping, and hand the body's
    /// terrain impacts and touchdowns to the original's damage entry points.
    /// </summary>
    private void SimFrame(Vehicle v, float frameDelta)
    {
        var step = World.Step;
        if (v.Rec.Hit && v.Rec.Other is Vehicle other)
        {
            // a momentum exchange along the contact normal (restitution 0.3) against the other car's mass;
            // the other car answers in its own step as the original does
            var n = new Vector3(v.Rec.N.X, v.Rec.N.Y, v.Rec.N.Z);
            var c = v.Rot.ToWorld(v.Rec.C);
            var at = new Vector3(c.X, c.Y, c.Z);
            var rel = G(v.Vel - other.Vel);
            float vn = rel.Dot(n);
            if (vn < 0)
            {
                float m = v.P.Mass, mo = MathF.Max(other.P.Mass, 1f);
                float j = -(1 + 0.3f) * vn / (1 / m + 1 / mo);
                // at the centre of mass's height: the pair test's points are at the boxes' mid-height, well above
                // the bumpers, and would lift the nose of a car that rams another
                var com = _car.Transform.Basis * _car.CenterOfMass;
                _car.QueueImpulse(n * j, new Vector3(at.X, com.Y, at.Z));
            }
            if (_debug) GD.Print($"modern car hit: other {other.Pos.X:0.0},{other.Pos.Z:0.0} closing {-vn:0.0} u/s, n {n.X:0.00},{n.Y:0.00},{n.Z:0.00}");
            step.Impacts.Add((v, other, v.Rec.RelVel, v.Rec.N, v.Rec.C));
            step.CarHits.Add((v, other, v.Rec.RelVel));
        }
        v.Rec.Clear();      // scenery records: Jolt has already answered them
        if (_debug && _ticks == 1)
        {
            // the window's layout: rays at four points around the origin against the sim's own ground
            var space = _car.GetWorld3D().DirectSpaceState;
            foreach (var (dx, dz) in new[] { (100f, 0f), (-100f, 35f), (20f, 120f), (-60f, -110f) })
            {
                var r = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(dx, 500, dz), new Vector3(dx, -500, dz), 1));
                GD.Print($"modern window check at {dx},{dz}: ray {(r.Count > 0 ? ((Vector3)r["position"]).Y + Origin.Y : double.NaN):0.00} ground {Ground.BareHeight(Origin.X + dx, Origin.Z + dz):0.00}");
            }
        }
        if (_debug && _ticks++ % 5 == 0)
        {
            // the heightfield window against the sim's own ground under the car (a check of the window's layout)
            var ray = _car.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(_car.Position + Vector3.Up * 50, _car.Position - Vector3.Up * 200, 1));
            float hit = ray.Count > 0 ? ((Vector3)ray["position"]).Y + (float)Origin.Y : float.NaN;
            GD.Print($"modern tick {_ticks} pos {v.Pos.X:0.00},{v.Pos.Y:0.00},{v.Pos.Z:0.00} fwd {v.Rot.Forward.X:0.00},{v.Rot.Forward.Z:0.00} up {v.Rot.Up.Y:0.00} v {v.Speed:0.00} ({_car.SignedSpeed * 3.6f:0} km/h) gear {v.Engine.Gear} rpm {v.Engine.Rpm:0} thr {v.Throttle:0.00} steer {v.Steer:0.00} ground {Ground.BareHeight(v.Pos.X, v.Pos.Z):0.00} ray {hit:0.00} flags 0x{(uint)v.Flags:x} near {World.Vehicles.Where(o => o != v).Select(o => (float?)(o.Pos.ToVector3() - v.Pos.ToVector3()).Length()).Min() ?? 0:0.0} zones {(v.Health is { } hd ? string.Join(",", Enumerable.Range(0, 4).Select(z => hd.ZoneRatio(z).ToString("0.00"))) : "-")} {_car.Debug()}");
        }
        step.ExternalFrame(v, frameDelta, _car.Grounded, _car.SignedSpeed);

        // the hull's new contacts: the terrain as the original's ground bounce (above 4 u/s), scenery as its collide callbacks
        foreach (var h in _car.Hits)
        {
            var n = new N.Vector3(h.N.X, h.N.Y, h.N.Z);
            var cl = v.Rot.ToLocal(new N.Vector3(h.At.X, h.At.Y, h.At.Z));
            var vel = new N.Vector3(h.Vel.X, h.Vel.Y, h.Vel.Z);
            if (h.Other is null)
            {
                if (h.Speed <= 4f) continue;
                step.Impacts.Add((v, null, -vel, n, cl));         // vtcoll.wav
                step.Damage?.TerrainImpact(v, -vel, n, cl);
            }
            else if (h.Other is StaticObject so && so.Solid)
            {
                // the original reads the car's velocity here: the one it hit with
                var now = v.Vel;
                v.Vel = vel;
                step.ObjectHits.Add((v, so.Name, so.Type, n, vel.Length()));
                step.Damage?.StaticContact(v, so, new Contact { Hit = true, Other = so, C = cl, N = n, RelVel = -vel });
                v.Vel = now;
            }
        }
        _car.Hits.Clear();
        if (_car.Landed is { } l)
        {
            _car.Landed = null;
            if ((v.Flags & (VehicleFlags)0x8020) == 0)
            {
                step.Landings.Add(v);
                if (l.Fall > 35) step.Damage?.FallKill(v);
                else step.Damage?.Landing(v, -new N.Vector3(l.Vel.X, l.Vel.Y, l.Vel.Z), new N.Vector3(l.N.X, l.N.Y, l.N.Z));
            }
        }
    }
}
