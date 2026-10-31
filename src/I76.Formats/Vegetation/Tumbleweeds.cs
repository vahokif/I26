using System.Numerics;

namespace I76.Formats.Vegetation;

/// <summary>
/// The one wind of the modern look's loose things (docs/modern-vegetation.md, Tumbleweeds): the plants sway toward it
/// (vegetation.gdshader's <c>wind_dir</c> is this in Godot's mirrored frame), the damage smoke drifts in it and the
/// tumbleweeds roll before it. Light, the same everywhere and in every mission; visual only.
/// </summary>
public static class Wind
{
    /// <summary>Where the wind blows toward, in the game frame's XZ (unit): Godot's (0.8, 0.6) with Z mirrored.</summary>
    public static readonly Vector2 Direction = Vector2.Normalize(new Vector2(0.8f, -0.6f));

    /// <summary>The steady breeze the smoke drifts in (metres a second).</summary>
    public const float Breeze = 1.2f;

    /// <summary>
    /// The gust front the plants sway in (vegetation.gdshader: <c>0.55 + 0.45 sin(0.45 t - 0.035 s)</c>, s the distance
    /// along the wind), 0.1..1: a front crossing the land downwind at about 13 m/s.
    /// </summary>
    public static float Front(float t, float along) => 0.55f + 0.45f * MathF.Sin(t * 0.45f - along * 0.035f);
}

/// <summary>What a tumbleweed rolls over: the drawn ground, the objects' footprints, the shrubs it may catch on.</summary>
public interface ITumbleGround
{
    /// <summary>Whether there is ground to roll on here (outside the mission's tiles: none).</summary>
    bool Has(float x, float z);
    /// <summary>The drawn ground's height (where it rests).</summary>
    float Height(float x, float z);
    /// <summary>The ground's height roughly, for its slope (read several times a step): <see cref="Height"/> unless cheaper.</summary>
    float Rough(float x, float z) => Height(x, z);
    /// <summary>Whether a ball of this radius here would be inside a building or an object's footprint.</summary>
    bool Blocked(Vector2 p, float radius);
    /// <summary>A shrub near p that could catch a tumbleweed (its centre and the radius of its crown), or none.</summary>
    (Vector2 Centre, float Radius)? Shrub(Vector2 p) => null;
    /// <summary>Whether a tumbleweed may start here (not on a river bottom, say); the slope and the footprints are tested apart.</summary>
    bool Suitable(float x, float z) => true;
}

/// <summary>A car the tumbleweeds burst against: where it is and how it moves (game frame, metres and metres a second).</summary>
public readonly record struct TumbleCar(Vector3 Position, Vector3 Velocity);

/// <summary>
/// One rolling tumbleweed or a fragment of a burst one. Position (its centre) and velocity in the game frame;
/// orientation and spin in the drawn frame (Godot's: Z mirrored, right-handed), so they go to the renderer as they are.
/// </summary>
public struct Tumble
{
    public bool Active;
    public Vector3 Position, Velocity;
    public Quaternion Orientation;
    /// <summary>Angular velocity, drawn frame (radians a second about the axis).</summary>
    public Vector3 Spin;
    /// <summary>The ball's radius (metres) and the mesh's scale that gives it.</summary>
    public float Radius, Scale;
    public int Variant;
    /// <summary>How much of it is drawn (0..1, by the dither) and where that is going.</summary>
    public float Fade, FadeTo;
    public bool Grounded;
    /// <summary>The ground under it, and its rise over run along X and Z there (for its rolling and its shade).</summary>
    public float GroundY;
    public Vector2 Slope;
    /// <summary>Its own share of the wind's push (drag per metre).</summary>
    public float Drag;
    public float Phase;
    /// <summary>Caught on a shrub: seconds left at least; and a while after it tears free in which it catches on none.</summary>
    public float Snag, SnagCooldown;
    /// <summary>Seconds it has lain still (a long rest in a lull or against a wall: it fades out and comes again upwind).</summary>
    public float Still;
    /// <summary>A fragment's age and life (seconds); 0 life: a whole tumbleweed.</summary>
    public float Age, Life;
}

/// <summary>
/// The modern look's tumbleweeds (docs/modern-vegetation.md, Tumbleweeds): a few Russian thistles rolling before the
/// wind round the camera, purely visual (nothing the simulation or a network game reads). Each rolls in the wind's
/// gusts and stops in its lulls, hops over the ground's bumps and off crests, slows uphill, turns aside at the objects'
/// footprints and catches on shrubs for a moment, spinning as it rolls (ω = n × v / r). They start upwind just out of
/// sight and go when they are far downwind, so the count stays the same; a car driving into one bursts it into a few
/// spinning pieces that fade. Seeded from the mission, so a run repeats.
/// </summary>
public sealed class TumbleweedField
{
    public const float Gravity = 9.8f;
    /// <summary>The strongest wind the gusts reach (metres a second).</summary>
    public const float MaxWind = 7.5f;
    /// <summary>Rolling resistance (of g) and the push it takes to start one rolling from rest.</summary>
    public const float RollResistance = 0.12f, StartResistance = 0.22f;
    /// <summary>The share of the ground's slope that pulls a rolling one back (a light tangle, about a hollow ball's).</summary>
    public const float SlopePull = 0.6f;
    /// <summary>Hops off the ground's small bumps: the chance per metre rolled at 4 m/s.</summary>
    public const float HopsPerMetre = 0.22f;
    /// <summary>A car bursts a tumbleweed within this much of its centre (plus the weed's radius).</summary>
    public const float CarRadius = 1.9f;
    public const int MaxFragments = 40;

    private readonly ITumbleGround _ground;
    private readonly float[] _meshRadius;
    private VegRng _rng;
    private readonly float _lullPhase1, _lullPhase2;
    private Tumble[] _weeds = [];
    private readonly Tumble[] _frags = new Tumble[MaxFragments];
    private Vector3 _lastEye;
    private bool _haveEye, _scatterNew;
    private int _substep;

    /// <param name="ground">the land</param>
    /// <param name="seed">the mission's</param>
    /// <param name="meshRadius">each mesh variant's radius at scale 1 (metres)</param>
    public TumbleweedField(ITumbleGround ground, ulong seed, float[] meshRadius)
    {
        _ground = ground;
        _meshRadius = meshRadius.Length > 0 ? meshRadius : [0.35f];
        _rng = new VegRng(VegHash.Mix(seed, 0x7a3b1e));
        _lullPhase1 = _rng.Range(0, MathF.Tau);
        _lullPhase2 = _rng.Range(0, MathF.Tau);
    }

    /// <summary>How far round the camera they roll (metres): they start on this ring upwind and go beyond it.</summary>
    public float Range { get; set; } = 150f;
    /// <summary>The wind's speed at a time and place, over the gusts and lulls (tests replace it).</summary>
    public Func<float, Vector2, float>? WindSpeed { get; set; }
    /// <summary>Added to the distance along the wind for the gust front, so it lines up with the plants' (the shader's frame is offset).</summary>
    public float FrontOffset { get; set; }
    public float Time { get; private set; }
    public ReadOnlySpan<Tumble> Weeds => _weeds;
    public ReadOnlySpan<Tumble> Fragments => _frags;
    /// <summary>Tumbleweeds burst by cars so far, and ones started again upwind (counts, for the tests and the stats line).</summary>
    public int Bursts { get; private set; }
    public int Respawns { get; private set; }
    /// <summary>Times one caught on a shrub (a count, for the stats line).</summary>
    public int Snags { get; private set; }

    /// <summary>How many roll at Vegetation Off (0), Low (1) and High (2) for a biome's rate (0..1): up to 4 on Low, 7 on High.</summary>
    public static int CountFor(float rate, int level) => level <= 0 || rate <= 0 ? 0 : (int)MathF.Round(Math.Clamp(rate, 0, 1) * (level >= 2 ? 7 : 4));

    /// <summary>The range round the camera at Low and High (metres).</summary>
    public static float RangeFor(int level) => level >= 2 ? 180f : 150f;

    /// <summary>How many there are; new ones are scattered round the last eye and fade in, extra ones go at once.</summary>
    public int Count
    {
        get => _weeds.Length;
        set
        {
            value = Math.Max(0, value);
            if (value == _weeds.Length) return;
            int old = _weeds.Length;
            Array.Resize(ref _weeds, value);
            for (int i = old; i < value; i++) _weeds[i].Active = false;
            if (value > old) _scatterNew = true;
        }
    }

    /// <summary>The wind's speed (metres a second): the gust front, and slow lulls (the same everywhere) in which it falls nearly calm.</summary>
    public float WindAt(float t, Vector2 p)
    {
        if (WindSpeed is { } f) return f(t, p);
        float front = Vegetation.Wind.Front(t, Vector2.Dot(p, Vegetation.Wind.Direction) + FrontOffset);
        float lull = 0.15f + 0.85f * SmoothStep(-0.8f, 0.1f, 0.6f * MathF.Sin(t * 0.071f + _lullPhase1) + 0.4f * MathF.Sin(t * 0.163f + _lullPhase2));
        return MaxWind * lull * (0.25f + 0.75f * front * front);
    }

    /// <summary>
    /// Advance them by dt seconds round the eye (game frame); <paramref name="cars"/> burst the ones they drive into.
    /// A jump of the eye (a camera cut, the first call) scatters them afresh round it.
    /// </summary>
    public void Step(float dt, Vector3 eye, ReadOnlySpan<TumbleCar> cars)
    {
        if (dt <= 0) return;
        dt = MathF.Min(dt, 0.1f);
        bool jump = !_haveEye || Flat(eye - _lastEye).Length() > Range * 0.5f;
        _lastEye = eye; _haveEye = true;
        if (jump) for (int i = 0; i < _weeds.Length; i++) _weeds[i].Active = false;
        // a jump, or more of them (the level went up): the new ones anywhere round the eye, fading in; else on the ring upwind
        bool scatter = jump || _scatterNew;
        _scatterNew = false;
        for (int i = 0; i < _weeds.Length; i++)
            if (!_weeds[i].Active) Spawn(ref _weeds[i], eye, ring: !scatter);
        // in steps of at most 1/60 s (the hops and the ground's contact are sharp)
        int n = (int)MathF.Ceiling(dt * 60f - 1e-4f);
        float h = dt / n;
        for (int s = 0; s < n; s++)
        {
            Time += h;
            _substep++;
            for (int i = 0; i < _weeds.Length; i++)
            {
                ref var w = ref _weeds[i];
                if (!w.Active) continue;
                Roll(ref w, h);
                float d = Flat(w.Position - eye).Length();
                // far beyond the ring, off the land or long still: start again upwind (a still one fades out first)
                // (the land's edge is looked for a few times a second: the test is not cheap)
                if (d > Range + 10f || ((_substep + i) % 8 == 0 && !_ground.Has(w.Position.X, w.Position.Z))) { w.Active = false; Respawns++; continue; }
                if (w.Still > 30f) w.FadeTo = 0;
                w.Fade = MoveToward(w.Fade, w.FadeTo, h / 1.2f);
                if (w.FadeTo == 0 && w.Fade == 0) { w.Active = false; Respawns++; }
            }
            for (int i = 0; i < _frags.Length; i++)
                if (_frags[i].Active) Fly(ref _frags[i], h);
        }
        // the cars, over the whole frame's sweep
        if (!cars.IsEmpty)
            for (int i = 0; i < _weeds.Length; i++)
                if (_weeds[i].Active) Hit(ref _weeds[i], cars, dt);
    }

    // ---- starting ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Start one: on the ring upwind of the eye (out of sight; it rolls in), or anywhere within it, fading in (after a
    /// jump). It needs ground, not too steep, clear of the objects, and suitable; a few tries a call, else it waits.
    /// </summary>
    public bool Spawn(ref Tumble w, Vector3 eye, bool ring)
    {
        // a few tries (fewer for the ring, which is retried every frame: upwind may be off the map for a while)
        for (int tries = 0; tries < (ring ? 3 : 8); tries++)
        {
            Vector2 p;
            if (ring)
            {
                float a = _rng.Range(-1.3f, 1.3f);   // within 75 degrees of straight upwind
                p = Flat(eye) + Rotate(-Vegetation.Wind.Direction, a) * _rng.Range(Range - 8f, Range);
            }
            else
            {
                float a = _rng.Range(0, MathF.Tau), d = Range * MathF.Sqrt(_rng.Float());
                p = Flat(eye) + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            }
            int variant = _rng.Int(_meshRadius.Length);
            float diameter = _rng.Range(0.5f, 1.0f);
            float r = diameter * 0.5f;
            if (!_ground.Has(p.X, p.Y) || !_ground.Suitable(p.X, p.Y) || _ground.Blocked(p, r + 0.5f)) continue;
            float gy = _ground.Height(p.X, p.Y);
            var g = Gradient(p);
            if (g.Length() > 0.5f) continue;
            w = new Tumble
            {
                Active = true, Position = new Vector3(p.X, gy + r, p.Y), Velocity = new Vector3(Vegetation.Wind.Direction.X, 0, Vegetation.Wind.Direction.Y) * _rng.Range(0.3f, 1.5f),
                Orientation = RandomOrientation(), Radius = r, Scale = r / _meshRadius[variant], Variant = variant,
                Fade = ring ? 1 : 0, FadeTo = 1, Grounded = true, GroundY = gy, Slope = g, Drag = _rng.Range(0.42f, 0.6f), Phase = _rng.Float(),
            };
            return true;
        }
        return false;
    }

    /// <summary>Put tumbleweed i at p (game XZ) on the ground, at rest (a capture hook: one ahead of the car to drive into).</summary>
    public void Place(int i, Vector2 p)
    {
        if (i < 0 || i >= _weeds.Length) return;
        ref var w = ref _weeds[i];
        if (!w.Active) w = new Tumble { Active = true, Radius = 0.35f, Scale = 0.35f / _meshRadius[0], Orientation = Quaternion.Identity, Drag = 0.5f, FadeTo = 1 };
        float gy = _ground.Height(p.X, p.Y);
        w.Position = new Vector3(p.X, gy + w.Radius, p.Y);
        w.Velocity = Vector3.Zero;
        w.GroundY = gy;
        w.Slope = Gradient(p);
        w.Grounded = true;
        w.Fade = w.FadeTo = 1;
        w.Still = w.Snag = 0;
    }

    private Quaternion RandomOrientation()
    {
        var q = new Quaternion(_rng.Bell(), _rng.Bell(), _rng.Bell(), _rng.Bell());
        return q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(q);
    }

    // ---- rolling ----------------------------------------------------------------------------------------------

    /// <summary>One step of a whole tumbleweed: the wind's drag, rolling on the ground or flying, the obstacles, its spin.</summary>
    private void Roll(ref Tumble w, float dt)
    {
        var p = new Vector2(w.Position.X, w.Position.Z);
        var v = new Vector2(w.Velocity.X, w.Velocity.Z);
        float wind = WindAt(Time, p);
        var rel = Vegetation.Wind.Direction * wind - v;
        var drag = rel * (rel.Length() * w.Drag);
        w.SnagCooldown = MathF.Max(0, w.SnagCooldown - dt);

        if (w.Snag > 0)
        {
            // caught on a shrub: it rocks in the wind until the time is up and a gust is strong enough to tear it free round the side
            w.Snag -= dt;
            var axis = Vector3.Cross(Vector3.UnitY, Drawn(new Vector3(Vegetation.Wind.Direction.X, 0, Vegetation.Wind.Direction.Y)));
            w.Spin = axis * (0.9f * MathF.Sin(Time * 6.5f + w.Phase * 9f) * wind / MaxWind);
            Turn(ref w, dt);
            if (w.Snag <= 0 && wind > 3.8f)
            {
                var side = Rotate(Vegetation.Wind.Direction, (_rng.Float() < 0.5f ? -1 : 1) * _rng.Range(0.45f, 0.8f));
                w.Velocity = new Vector3(side.X * 1.6f, 1.4f, side.Y * 1.6f);
                w.Grounded = false;
                w.Snag = 0; w.SnagCooldown = 2.5f;
            }
            else if (w.Snag <= 0) w.Snag = 0.25f;
            w.Still = 0;
            return;
        }

        if (w.Grounded)
        {
            var grad = w.Slope;
            var a = drag - grad * (Gravity * SlopePull);
            float speed = v.Length();
            if (speed < 0.05f && a.Length() < StartResistance * Gravity)
            {
                v = Vector2.Zero;
                w.Still += dt;
            }
            else
            {
                w.Still = speed < 0.3f ? w.Still + dt : 0;
                var vn = v + a * dt;
                // rolling resistance slows it, never turns it round
                float sn = vn.Length();
                if (sn > 1e-4f) vn *= MathF.Max(0, sn - RollResistance * Gravity * dt) / sn;
                v = vn;
            }
            var np = p + v * dt;
            Obstacles(ref w, p, ref np, ref v);
            float gy = _ground.Height(np.X, np.Y);
            var gradNew = Gradient(np);
            float vyGround = Vector2.Dot(gradNew, v), vyOld = Vector2.Dot(grad, v);
            w.Velocity = new Vector3(v.X, vyGround, v.Y);
            // off a crest: the ground falls away faster than gravity can pull it down, so it flies on
            if ((vyGround - vyOld) / dt < -Gravity * 0.9f)
            {
                w.Velocity.Y = vyOld;
                w.Grounded = false;
                w.Position = new Vector3(np.X, w.Position.Y + vyOld * dt, np.Y);
            }
            else
            {
                w.Position = new Vector3(np.X, gy + w.Radius, np.Y);
                // the small bumps and stones: now and then a hop, more of them the faster it goes
                float metres = v.Length() * dt;
                if (metres > 0 && _rng.Float() < HopsPerMetre * metres * MathF.Min(1.5f, v.Length() / 4f))
                {
                    float up = _rng.Range(0.25f, 0.6f) * MathF.Min(v.Length(), 6f) * (_rng.Float() < 0.12f ? 1.7f : 1f);
                    w.Velocity.Y = vyGround + up;
                    w.Grounded = false;
                }
            }
            w.GroundY = gy;
            w.Slope = gradNew;
            // rolling contact: the point on the ground is still, ω = n × v / r (n up, drawn frame)
            w.Spin = Vector3.Cross(Vector3.UnitY, Drawn(new Vector3(v.X, 0, v.Y))) / w.Radius;
        }
        else
        {
            // in the air: the wind's drag (all of it, nothing rolling against it), gravity, a little drag on the fall
            v += drag * dt;
            float vy = w.Velocity.Y + (-Gravity - 0.25f * w.Velocity.Y * MathF.Abs(w.Velocity.Y)) * dt;
            var np = p + v * dt;
            Obstacles(ref w, p, ref np, ref v);
            float y = w.Position.Y + vy * dt;
            float gy = _ground.Height(np.X, np.Y);
            w.GroundY = gy;
            var grad = w.Slope = Gradient(np);
            w.Spin *= 1 - 0.3f * dt;
            if (y - w.Radius <= gy)
            {
                float vyGround = Vector2.Dot(grad, v);
                float impact = vy - vyGround;
                y = gy + w.Radius;
                if (impact < -1.2f)
                {
                    // a bounce: a light tangle gives back little, and the ground drags on it
                    vy = vyGround - 0.35f * impact;
                    v *= 0.85f;
                }
                else
                {
                    vy = vyGround;
                    w.Grounded = true;
                    w.Spin = Vector3.Cross(Vector3.UnitY, Drawn(new Vector3(v.X, 0, v.Y))) / w.Radius;
                }
            }
            w.Position = new Vector3(np.X, y, np.Y);
            w.Velocity = new Vector3(v.X, vy, v.Y);
            w.Still = 0;
        }
        Turn(ref w, dt);
    }

    /// <summary>
    /// The objects in its way: at a footprint it turns aside along it (the nearest clear heading within 90 degrees,
    /// slowed) or stops against it; at a shrub it catches for a moment (6 in 10) or glances off round it.
    /// </summary>
    private bool Obstacles(ref Tumble w, Vector2 p, ref Vector2 np, ref Vector2 v)
    {
        bool hit = false;
        if (_ground.Blocked(np, w.Radius * 0.8f))
        {
            hit = true;
            float speed = v.Length();
            var dir = speed > 1e-4f ? v / speed : Vegetation.Wind.Direction;
            bool turned = false;
            foreach (float a in (ReadOnlySpan<float>)[0.5f, -0.5f, 1.0f, -1.0f, 1.5f, -1.5f])
            {
                var d = Rotate(dir, a);
                var q = p + d * MathF.Max(speed, 0.5f) * (1f / 60f);
                if (_ground.Blocked(q, w.Radius * 0.8f)) continue;
                v = d * (speed * MathF.Cos(a) * 0.7f);
                np = p + v * (1f / 60f);
                turned = true;
                break;
            }
            if (!turned || _ground.Blocked(np, w.Radius * 0.8f)) { v = Vector2.Zero; np = p; }
        }
        if (w.SnagCooldown <= 0 && _ground.Shrub(np) is { } s)
        {
            float reach = s.Radius * 0.8f + w.Radius * 0.5f;
            var off = np - s.Centre;
            if (off.LengthSquared() < reach * reach && Vector2.Dot(v, s.Centre - p) > 0)
            {
                hit = true;
                if (_rng.Float() < 0.6f)
                {
                    w.Snag = _rng.Range(1.2f, 4.5f);
                    Snags++;
                    v = Vector2.Zero; np = p;
                    w.Grounded = true;
                }
                else
                {
                    // glances off: along the crown's edge, with a hop
                    var n = off.LengthSquared() > 1e-6f ? Vector2.Normalize(off) : Rotate(Vegetation.Wind.Direction, 1.57f);
                    var t = new Vector2(-n.Y, n.X);
                    if (Vector2.Dot(t, v) < 0) t = -t;
                    v = t * v.Length() * 0.7f + n * 0.5f;
                    np = p + v * (1f / 60f);
                    w.SnagCooldown = 1.5f;
                    if (w.Grounded) { w.Grounded = false; w.Velocity.Y = 1.2f; }
                }
            }
        }
        return hit;
    }

    // ---- cars ---------------------------------------------------------------------------------------------------

    /// <summary>A car moving at least this fast (metres a second) bursts a tumbleweed; a slower or standing one stops it like a wall.</summary>
    public const float BurstSpeed = 3f;

    /// <summary>
    /// A car that sweeps over it this frame bursts it: it goes (and starts again upwind) and its pieces fly. One standing
    /// or creeping holds it off: it comes to rest against the car's side.
    /// </summary>
    private void Hit(ref Tumble w, ReadOnlySpan<TumbleCar> cars, float dt)
    {
        foreach (var c in cars)
        {
            var a = Flat(c.Position - c.Velocity * dt);
            var b = Flat(c.Position);
            var p = Flat(w.Position);
            float reach = CarRadius + w.Radius * 0.7f;
            if (VegMask.SegDistance(a, b, p) > reach) continue;
            if (MathF.Abs(w.Position.Y - c.Position.Y) > 1.6f + w.Radius) continue;
            if (Flat(c.Velocity).Length() >= BurstSpeed) { Burst(ref w, c); return; }
            var off = p - b;
            var n = off.LengthSquared() > 1e-6f ? Vector2.Normalize(off) : -Vegetation.Wind.Direction;
            var q = b + n * reach;
            w.Position = new Vector3(q.X, w.Position.Y, q.Y);
            var v = Flat(w.Velocity);
            float into = Vector2.Dot(v, n);
            if (into < 0) v -= n * into;
            w.Velocity = new Vector3(v.X * 0.5f, w.Velocity.Y, v.Y * 0.5f);
        }
    }

    /// <summary>
    /// The tumbleweed bursts: its main tangle knocked up and away from the car with a spin, three smaller pieces off it,
    /// all fading over a second or two. Nothing touches the car.
    /// </summary>
    public void Burst(ref Tumble w, TumbleCar car)
    {
        // thrown ahead of the car and out to the side it was on
        var cv = Flat(car.Velocity);
        var ahead = cv.LengthSquared() > 1e-4f ? Vector2.Normalize(cv) : Vegetation.Wind.Direction;
        var off = Flat(w.Position - car.Position);
        var lateral = off - ahead * Vector2.Dot(off, ahead);
        var away = Vector2.Normalize(ahead + (lateral.LengthSquared() > 1e-6f ? Vector2.Normalize(lateral) : new Vector2(-ahead.Y, ahead.X) * (_rng.Float() < 0.5f ? -1 : 1)) * 0.8f);
        int pieces = 4;
        for (int k = 0; k < pieces; k++)
        {
            int slot = FreeFragment();
            if (slot < 0) break;
            bool main = k == 0;
            var side = Rotate(away, _rng.Range(-0.9f, 0.9f));
            float push = main ? _rng.Range(1.5f, 3f) : _rng.Range(2f, 4.5f);
            var hv = Flat(car.Velocity) * _rng.Range(0.55f, 0.9f) + side * push;
            var axis = Vector3.Normalize(new Vector3(_rng.Bell(), _rng.Bell(), _rng.Bell()) + new Vector3(0, 0, 1e-3f));
            float scale = main ? 0.8f : _rng.Range(0.38f, 0.55f);
            _frags[slot] = new Tumble
            {
                Active = true, Variant = main ? w.Variant : _rng.Int(_meshRadius.Length),
                Position = w.Position + new Vector3(side.X, 0.2f, side.Y) * (main ? 0 : w.Radius * 0.6f),
                Velocity = new Vector3(hv.X, _rng.Range(2.5f, 5.5f), hv.Y),
                Orientation = main ? w.Orientation : RandomOrientation(), Spin = axis * _rng.Range(7f, 16f),
                Radius = w.Radius * scale, Fade = 1, FadeTo = 0, GroundY = w.GroundY, Phase = _rng.Float(),
                Life = main ? _rng.Range(1.8f, 2.4f) : _rng.Range(1.1f, 1.8f), Drag = main ? 0.22f : 0.35f,
            };
            _frags[slot].Scale = _frags[slot].Radius / _meshRadius[_frags[slot].Variant];
        }
        w.Active = false;
        Bursts++;
    }

    private int FreeFragment()
    {
        for (int i = 0; i < _frags.Length; i++) if (!_frags[i].Active) return i;
        return -1;
    }

    /// <summary>A piece of a burst one: it flies, slows in the air toward the wind, falls, bounces a little, fades out.</summary>
    private void Fly(ref Tumble f, float dt)
    {
        f.Age += dt;
        if (f.Age >= f.Life) { f.Active = false; return; }
        f.Fade = 1 - SmoothStep(f.Life * 0.45f, f.Life, f.Age);
        var v = new Vector2(f.Velocity.X, f.Velocity.Z);
        var rel = Vegetation.Wind.Direction * WindAt(Time, Flat(f.Position)) - v;
        v += rel * (rel.Length() * f.Drag) * dt;
        float vy = f.Velocity.Y + (-Gravity - 0.15f * f.Velocity.Y * MathF.Abs(f.Velocity.Y)) * dt;
        var np = new Vector2(f.Position.X, f.Position.Z) + v * dt;
        float y = f.Position.Y + vy * dt;
        float gy = _ground.Height(np.X, np.Y);
        f.GroundY = gy;
        if (y - f.Radius < gy)
        {
            y = gy + f.Radius;
            if (vy < 0) vy *= -0.3f;
            v *= 0.6f;
            f.Spin *= 0.6f;
        }
        f.Position = new Vector3(np.X, y, np.Y);
        f.Velocity = new Vector3(v.X, vy, v.Y);
        Turn(ref f, dt);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static void Turn(ref Tumble w, float dt)
    {
        float rate = w.Spin.Length();
        if (rate < 1e-5f) return;
        var dq = Quaternion.CreateFromAxisAngle(w.Spin / rate, rate * dt);
        w.Orientation = Quaternion.Normalize(Mul(dq, w.Orientation));
    }

    /// <summary>The Hamilton product a b (b first, then a, as rotations).</summary>
    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>v rotated by the unit quaternion q.</summary>
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var t = 2 * Vector3.Cross(u, v);
        return v + q.W * t + Vector3.Cross(u, t);
    }

    /// <summary>A game-frame vector in the drawn frame (Z mirrored).</summary>
    public static Vector3 Drawn(Vector3 v) => new(v.X, v.Y, -v.Z);

    /// <summary>The ground's rise over run along X and Z here (from its rough heights half a metre either side).</summary>
    private Vector2 Gradient(Vector2 p)
    {
        const float e = 0.5f;
        return new Vector2(_ground.Rough(p.X + e, p.Y) - _ground.Rough(p.X - e, p.Y), _ground.Rough(p.X, p.Y + e) - _ground.Rough(p.X, p.Y - e)) / (2 * e);
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private static Vector2 Rotate(Vector2 v, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float MoveToward(float from, float to, float step) => from < to ? MathF.Min(to, from + step) : MathF.Max(to, from - step);

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
