using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;

namespace I76.Sim.Vehicles;

/// <summary>
/// The vehicle step, ported from the original game: the main step and its helpers. Ported:
/// drive and brake, the commanded yaw rate, resistance, slope gravity, the
/// grip test, per-wheel forces when sliding, the two-stage integration, the
/// turn about the pivot, airborne flight and landing; the collision
/// response (C1); the suspension IIR and bump table (V4); the attitude of
/// the contact step (contact mask, pivots, plane fit); rollover; the far path.
/// The skid marks' rand() draws are the original's; the marks
/// and their CHUNK3 debris are drawn by the game layer.
/// </summary>
public sealed class VehicleStep(IGround ground, CrtRand rand)
{
    public const float Gravity = -9.8f;

    /// <summary>N = trunc(Δ/0.05)+1 capped at 20, dt = (1/n)·Δ.</summary>
    public static (int Count, float Dt) Substeps(float frameDelta)
    {
        int n = (int)(frameDelta * 20f) + 1;
        if (n > 20) n = 20;
        return (n, (1f / n) * frameDelta);
    }

    /// <summary>
    /// the far path for a car more than 25 (+ the camera's own
    /// distance setting) from the camera: a flat
    /// model with the ground normal forced to (0, 1, 0) and surface 2, no
    /// wheels, no terrain following. A moving car moves along its forward row
    /// and is then levelled to that normal, so
    /// it keeps the height it had; flag 0x20000 marks it so that coming
    /// near again snaps the car to the ground first.
    /// </summary>
    public void CheapFrame(Vehicle v, float frameDelta, bool isPlayer)
    {
        Damage?.Timer(v, frameDelta);     // far cars: once per frame
        // The slide bit cleared and the skid marks ended
        v.RearLeft.Marking = v.RearRight.Marking = false;
        if ((v.Flags & (VehicleFlags)0x20000) == 0)
        {
            v.GroundNormal = Vector3.UnitY;
            v.NormalLoad = 9.8f;
        }
        var flags = v.Flags & ~(VehicleFlags)0x206;
        float speed = v.Speed;
        if (CheapMotion(v, frameDelta, isPlayer, out var pos, out var vel, out var rot, out float sp, out float yaw))
        {
            v.Rot = rot;
            v.Pos = Double3.From(pos);
            v.Vel = vel;
            v.AngVel.Y = yaw;
            v.Speed = MathF.Abs(sp);
            speed = sp;
        }
        v.GroundNormal = Vector3.UnitY;
        v.Surface = 2;
        v.Flags = flags | (VehicleFlags)0x20000;
        v.Engine.Update(v.Throttle, speed, v.Flags, v.Gear < 0, v.DriveState, frameDelta);
    }

    /// <summary>
    /// the motion: the drive force and commanded yaw on the car's
    /// own forward row, integrated over <paramref name="dt"/>, levelled to the
    /// ground normal and turned. False when the car is still with no drive
    /// (it stays as it is).
    /// </summary>
    private bool CheapMotion(Vehicle v, float dt, bool isPlayer, out Vector3 pos, out Vector3 vel, out Rows rot, out float sp, out float yaw)
    {
        float throttle = v.Throttle;
        if (v.Handbrake != 0 || v.DriveState == 0) throttle = -1;
        if (throttle < 0 && v.Unk_0xf4 != 0) throttle += throttle;
        var flags = v.Flags & ~(VehicleFlags)0x206;
        float speed = v.Speed;
        pos = v.Pos.ToVector3(); vel = Vector3.Zero; rot = v.Rot; sp = 0; yaw = 0;
        if (speed == 0 && throttle <= 0) return false;
        var fwd = v.Rot.Forward;
        if (Vector3.Dot(v.Vel, fwd) < 0) speed = -speed;
        bool slide = false;
        float drive = DriveForce(v, v.Throttle, false, (uint)flags, ref slide);
        float blend = 1;
        yaw = YawRate(v, speed, isPlayer, ref blend, out _);
        var posStart = pos;
        vel = fwd * speed;
        var acc = fwd * drive + v.Rot.Right * (-(speed * yaw));
        Integrate(ref pos, ref vel, acc, dt);
        if (throttle >= 0 || Vector3.Dot(v.Vel, vel) >= 0)
        {
            sp = vel.Length();
            if (throttle > 0 || sp <= -1 || sp >= 1) { if (Vector3.Dot(vel, fwd) < 0) sp = -sp; }
            else { sp = 0; vel = Vector3.Zero; }
        }
        else { sp = 0; pos = posStart; vel = Vector3.Zero; }
        // The rows rebuilt on the ground normal (forced to
        // (0, 1, 0) on the far path), so a car that went far while climbing stops climbing; then the turn
        var n = v.GroundNormal;
        var level = v.Rot;
        var right = Vector3.Cross(n, fwd);
        if (right.LengthSquared() > 1e-12f)
        {
            right = Vector3.Normalize(right);
            level = new Rows(right, n, Vector3.Cross(right, n));
        }
        rot = Rows.Concat(Rows.AboutY(-(dt * yaw)), level);
        return true;
    }

    /// <summary>
    /// The AI's one-frame look-ahead (the back-away look-ahead):
    /// the car stepped by <paramref name="frameDelta"/> with these inputs, through
    /// the force model when near and the far model
    /// when far, into copies; the car is left as it was, except that the
    /// airborne branch writes its height above the ground as the
    /// original's does.
    /// </summary>
    public (Vector3 Pos, Vector3 Vel, Rows Rot) Predict(Vehicle v, float frameDelta, bool far, bool isPlayer, float steer, float throttle)
    {
        var (s0, t0) = (v.Steer, v.Throttle);
        v.Steer = steer; v.Throttle = throttle;
        try
        {
            if (far)
                return CheapMotion(v, frameDelta, isPlayer, out var cp, out var cv, out var cr, out _, out _) ? (cp, cv, cr) : (v.Pos.ToVector3(), Vector3.Zero, v.Rot);
            // the forces' airborne branch writes the height above ground into the car itself, and the original
            // leaves it written: every candidate tested moves the clearance the next "still rising" test reads
            var (pos, vel, rot, ang) = (v.Pos, v.Vel, v.Rot, v.AngVel);
            Forces(v, frameDelta, isPlayer, out _, out _);
            var r = (v.Pos.ToVector3(), v.Vel, v.Rot);
            (v.Pos, v.Vel, v.Rot, v.AngVel) = (pos, vel, rot, ang);
            return r;
        }
        finally { v.Steer = s0; v.Throttle = t0; }
    }

    /// <summary>Back from the far path, snap to the ground and settle the attitude.</summary>
    public void Rejoin(Vehicle v, float frameDelta)
    {
        var p = v.Pos;
        var (h, n) = ground.Query(p.X, p.Y, p.Z);
        v.Pos = new Double3(p.X, h, p.Z);
        v.GroundNormal = n;
        Attitude(v, Vector3.Zero, 0, frameDelta, rejoin: true);
        v.Flags &= ~(VehicleFlags)0x20000;      // only the far bit; 0x200 is left as it was
    }

    /// <summary>One frame of the near path.</summary>
    public void Frame(Vehicle v, float frameDelta, bool isPlayer = true)
    {
        if ((v.Flags & (VehicleFlags)0x20000) != 0) Rejoin(v, frameDelta);
        if (!ExternalContacts) ObjectContacts(v, frameDelta);
        var (n, dt) = Substeps(frameDelta);
        FrameDelta = frameDelta;
        for (int i = 0; i < n; i++) Step(v, dt, isPlayer);
    }

    /// <summary>
    /// The step's bookkeeping for a car another model moves (SimWorld.External):
    /// the damage timer once per substep and the rough-terrain
    /// damage on a fast grounded substep, as <see cref="Step"/>
    /// runs them.
    /// </summary>
    public void ExternalFrame(Vehicle v, float frameDelta, bool grounded, float signedSpeed)
    {
        var (n, dt) = Substeps(frameDelta);
        FrameDelta = frameDelta;
        for (int i = 0; i < n; i++)
        {
            Damage?.Timer(v, dt);
            if (grounded && signedSpeed > 7.65f) { if (Damage is { } d) d.Rough(v, dt); else rand.Next(); }
        }
    }

    /// <summary>The damage model's entry points inside the step (the step callers).</summary>
    public IStepDamage? Damage { get; set; }

    public void Step(Vehicle v, float dt, bool isPlayer)
    {
        Damage?.Timer(v, dt);     // once per substep before the step
        Collide(v, dt);
        Rollover(v);
        // A dying (0x8000) or dead (0x20) car brakes, every substep
        if (((uint)v.Flags & 0x8020) != 0) v.Throttle = -1;
        float speed0 = v.Speed;
        foreach (var w in v.Wheels) w.Spin += speed0 * dt;
        v.FrontRight.SteerAngle = v.FrontLeft.SteerAngle = v.Steer * -MathF.PI / 4;

        var oldFlags = v.Flags;
        var flags = Forces(v, dt, isPlayer, out var signedSpeed, out var accel);
        v.Accel = accel;
        v.Speed = MathF.Abs(signedSpeed);
        // landing: was airborne, now down, not dead -- the 35 m fall kill, else landing damage
        if (Damage is { } dmg && (oldFlags & VehicleFlags.Airborne) != 0 && (flags & VehicleFlags.Airborne) == 0 && ((uint)oldFlags & 0x8020) == 0)
        {
            if (v.GroundHeight - v.Pos.Y > 35.0) { v.Flags = flags; dmg.FallKill(v); return; }
            Landings.Add(v);
            dmg.Landing(v, -v.Vel, v.GroundNormal);
        }

        if ((flags & VehicleFlags.Airborne) == 0)
        {
            v.Flags = flags;
            // Ground under the body, offset along up when upside down.
            var p = v.Pos;
            if (v.Rot.Up.Y < 0) p += v.Rot.Up * v.P.RideOffset;
            v.Surface = ground.SurfaceId(p.X, p.Z);
            var (h, nrm) = ground.Query(p.X, p.Y, p.Z);
            v.GroundNormal = nrm;
            v.NormalLoad = nrm.Y * nrm.Y * nrm.Y * 9.8f;
            if ((v.Flags & VehicleFlags.Airborne) == 0) v.GroundHeight = h;
            Attitude(v, accel, oldFlags, dt);
            bool slideBit = (flags & VehicleFlags.Sliding) != 0;
            bool sliding = slideBit && (v.Flags & (VehicleFlags)0x8020) == 0;
            v.Flags = sliding ? v.Flags | VehicleFlags.Sliding : v.Flags & ~VehicleFlags.Sliding;
            // On the two rear wheels, with the forces' slide bit (a dead car's too)
            Skid(v.RearLeft, slideBit);
            Skid(v.RearRight, slideBit);
            // Rough-terrain damage: one rand() per grounded substep above 7.65 u/s
            if (signedSpeed > 7.65f) { if (Damage is { } d) d.Rough(v, dt); else rand.Next(); }
        }
        else
        {
            // the forces' airborne return clears the slide bit: the marks end
            v.Flags = flags & ~VehicleFlags.Sliding;
            Skid(v.RearLeft, false);
            Skid(v.RearRight, false);
            foreach (var w in v.Wheels) Suspension(v, w, true, 0, 0);   // locked, surface 0 in flight
        }
        // With the step's flags and signed speed
        v.Engine.Update(v.Throttle, signedSpeed, v.Flags, v.Gear < 0, v.DriveState, FrameDelta);
    }

    /// <summary>When a SimWorld runs the frame's collision pass itself (it also needs car pairs).</summary>
    public bool ExternalContacts { get; init; }

    /// <summary>The static half of the per-frame object pass, for SimWorld.</summary>
    /// <param name="signsOnly">Only the type-4 sphere test (signs, pickups): for a car another model moves, whose solid contacts that model finds.</param>
    public void StaticContacts(Vehicle v, float frameDelta, bool signsOnly = false) => ObjectContacts(v, frameDelta, signsOnly);

    /// <summary>The real frame delta, which the engine's smoothing uses (not the substep).</summary>
    public float FrameDelta { get; set; } = I76.Sim.Session.SimRate.R20.TickSeconds;

    /// <summary>Forces and integration. Returns the new flags.</summary>
    private VehicleFlags Forces(Vehicle v, float dt, bool isPlayer, out float signedSpeedOut, out Vector3 accelOut)
    {
        float throttle = v.Throttle;
        accelOut = default;
        var flags = v.Flags;

        // ---- airborne ----
        if ((flags & VehicleFlags.Airborne) != 0 || (flags & VehicleFlags.JustLanded) == 0)
        {
            var p = v.Pos.ToVector3();
            var av = v.Vel;
            p.X += av.X * dt;
            p.Z += av.Z * dt;
            p.Y += (av.Y + 0.5f * Gravity * dt) * dt;
            av.Y += Gravity * dt;
            var (g, _) = ground.Query(p.X, p.Y, p.Z);
            var t = p;                                      // the roof offset feeds the height test only
            if (v.Rot.Up.Y <= 0) t += v.Rot.Up * v.P.RideOffset;
            float above = t.Y - g;
            if (((flags & VehicleFlags.Airborne) != 0 && above > v.HeightAboveGround) || above > 0.5f)
            {
                v.HeightAboveGround = above;
                v.Pos = Double3.From(p);
                v.Vel = av;
                signedSpeedOut = av.Length();
                // nose eases down:
                if (v.AngVel.X > -0.15707964f) v.AngVel.X = Ease(v.AngVel.X, -0.15707964f, dt);
                v.Rot = Rotate(v.Rot, v.AngVel, dt);
                return (flags & ~(VehicleFlags)0x2202) | VehicleFlags.Airborne;
            }
            v.HeightAboveGround = above;
            if ((flags & VehicleFlags.Airborne) != 0)
            {
                p.Y = g;
                v.Pos = Double3.From(p);
                v.Vel = av;
                signedSpeedOut = av.Length();
                var nf = (flags & ~VehicleFlags.Airborne) | VehicleFlags.JustLanded;
                if (isPlayer && MathF.Abs(Vector3.Dot(v.Rot.Right, av)) > 1) nf |= VehicleFlags.Sliding;   // row 0
                return nf;
            }
            // not yet grounded and within 0.5: fall through to the grounded model
        }

        // ---- grounded ----
        var n = v.GroundNormal;
        var fwd = v.Rot.Forward;
        var right = v.Rot.Right;
        bool tipped = Vector3.Dot(n, v.Rot.Up) < 0.1f;
        if (tipped || !(v.Handbrake == 0 && v.DriveState != 0)) throttle = -1f;
        if (throttle > -1e-4f && v.DriveState == 2) throttle = 0;
        if (throttle < 0 && v.Unk_0xf4 != 0) throttle += throttle;

        var vel0 = v.Vel;
        float signedSpeed = v.Speed * (Vector3.Dot(fwd, vel0) < 0 ? -1 : 1);
        float yawFactor = 1f;
        // sliding: the sliding model this step; triggered: a slide triggered this step
        bool sliding, triggered;
        if (-1f <= signedSpeed * v.Gear || throttle <= 1e-4f)
        {
            if (v.Handbrake == 0 || v.Speed <= 2f) { triggered = false; sliding = (flags & VehicleFlags.Sliding) != 0; }
            else
            {
                triggered = true; sliding = true;
                if ((flags & VehicleFlags.Sliding) == 0) yawFactor = signedSpeed * 0.1f;
            }
        }
        else
        {
            sliding = true; triggered = true;
            flags |= VehicleFlags.Sliding;
        }

        float drive = DriveForce(v, throttle, tipped, (uint)flags, ref triggered);
        if (triggered) sliding = true;

        float yaw = 0, blend = 1;
        flags &= ~VehicleFlags.Blending;
        if (!sliding && !tipped)
        {
            yaw = YawRate(v, signedSpeed, isPlayer, ref blend, out bool slid);
            if (slid) { triggered = true; sliding = true; }
        }
        bool slidingBefore = sliding;

        // resistance
        float load4 = v.NormalLoad * 0.25f;
        var resist = Vector3.Zero;
        if (v.Speed >= 1f)
        {
            float drag = v.P.Drag * v.Speed * v.Speed * -0.1f;
            float roll = v.RearRight.Rolling + v.RearLeft.Rolling + v.FrontRight.Rolling + v.FrontLeft.Rolling;
            float k = v.InvSpeed * (drag - ground.Surface(v.Surface).Rolling * roll * load4);
            resist = vel0 * k;
        }

        // gravity, split by the ground normal
        var (_, gSlope) = VecOps.Split(new Vector3(0, Gravity, 0), n);
        float gripLoad = SurfaceGrip(v) * load4;
        float maxFriction = (v.FrontRight.Grip + v.FrontLeft.Grip + v.RearRight.Grip + v.RearLeft.Grip) * gripLoad;
        bool wheelModel = slidingBefore;
        if (gSlope.LengthSquared() > maxFriction * maxFriction) { triggered = true; wheelModel = true; }

        var torque = Vector3.Zero;
        Vector3 slope;
        if (tipped || (wheelModel && v.Speed >= 1f))
        {
            slope = gSlope;
            bool locked = tipped || v.Throttle < -0.5f || v.Handbrake != 0;
            if ((v.Flags & VehicleFlags.CommandedYaw) == 0)
                foreach (var w in v.Wheels) WheelForce(v, w, gripLoad, locked, ref resist, ref torque);
            else
            {
                // Sideways friction, -(right·v)/|v| · maxFriction, along right
                float k = -(Vector3.Dot(right, v.Vel) * v.InvSpeed * maxFriction);
                resist += right * k;
            }
        }
        else
        {
            // The transform's first row reads the object's rotation and is handed the transform
            // itself, so this is row 2: only the slope along the heading pulls.
            slope = fwd * Vector3.Dot(fwd, gSlope);
        }
        if (triggered) sliding = true;

        var pos = v.Pos.ToVector3();
        var posStart = pos;
        Vector3 vel, accel;
        if (!sliding)
        {
            Vector3 vNow;
            float yawCentripetal;
            if (blend >= 1f) { vNow = fwd * signedSpeed; yawCentripetal = yaw; }
            else { vNow = vel0; flags |= VehicleFlags.Blending; yawCentripetal = blend * yaw; }
            var a = resist + right * (-(yawCentripetal * signedSpeed));
            if (throttle < 0) a += fwd * drive;
            Integrate(ref pos, ref vNow, a, dt);
            if (Vector3.Dot(vNow, vel0) < 0) { vNow = Vector3.Zero; pos = posStart; }
            var a2 = throttle <= 0 ? slope : fwd * drive + slope;
            pos += 0.5f * dt * dt * a2;
            vNow += a2 * dt;
            vel = vNow;
            accel = a2 + a;
        }
        else
        {
            flags |= VehicleFlags.Sliding;
            var vNow = vel0;
            Integrate(ref pos, ref vNow, resist, dt);
            if (Vector3.Dot(vNow, vel0) < 0) { vNow = Vector3.Zero; pos = posStart; }
            var a2 = fwd * drive + slope;
            pos += 0.5f * dt * dt * a2;
            vNow += a2 * dt;
            vel = vNow;
            accel = a2 + resist;
            if ((v.Flags & VehicleFlags.CommandedYaw) == 0)
            {
                // On the angular velocity with the wheel torque: yaw = Δangle/dt
                float dAngle = (v.AngVel.Y + 0.5f * torque.Y * dt) * dt;
                yaw = dt > 1e-4f ? dAngle / dt : dAngle * 1e30f;
            }
            else
            {
                yaw = v.Steer * v.Gear * MathF.PI;
                if (v.Speed < 1f) yaw *= v.Speed;
                float rate = v.P.YawRateLimit * dt;
                if (rate >= 0 && rate <= 0.95f && MathF.Abs(yaw - v.AngVel.Y) >= 0.005f) yaw = rate * (yaw - v.AngVel.Y) + v.AngVel.Y;
                yaw *= yawFactor;
            }
        }

        // turn about the pivot ahead along forward (the original's rotation composition)
        var pivot = pos + fwd * v.P.PivotForward;
        var rot = Rows.Concat(Rows.AboutY(-(yaw * dt)), v.Rot);
        pos = pivot - rot.Forward * v.P.PivotForward;
        v.Rot = rot;
        v.Pos = Double3.From(pos);

        // keep the velocity in the ground plane: (n×v)×n
        vel = Vector3.Cross(Vector3.Cross(n, vel), n);
        float sp = vel.Length();
        if (Vector3.Dot(vel, rot.Forward) < 0) sp = -sp;
        if (throttle < 0 && sp > -1 && sp < 1)
        {
            // the substep that brings the car to rest keeps its braking acceleration for the attitude (the nose holds);
            // the next one, with the car already at rest, writes 0 (the nose rises)
            if (v.Vel == Vector3.Zero) accel = Vector3.Zero;
            vel = Vector3.Zero; sp = 0;
            flags &= ~(VehicleFlags.Blending | VehicleFlags.Sliding);
        }
        else if (sliding && !triggered && MathF.Abs(Vector3.Dot(rot.Right, vel)) < 3)
            flags &= ~(VehicleFlags.Blending | VehicleFlags.Sliding);

        v.Vel = vel;
        v.AngVel.Y = yaw;
        signedSpeedOut = sp;
        accelOut = accel;
        return flags;
    }

    /// <summary>
    /// the first section: with no
    /// pending object contact, sweep the body's corners against the terrain;
    /// respond to the contact once per episode (flag 0x10).
    /// Static branch only (terrain and scenery); the vehicle-pair branch
    /// arrives with other cars.
    /// </summary>
    private void Collide(Vehicle v, float dt)
    {
        const VehicleFlags Latch = (VehicleFlags)0x10;
        if (!v.Rec.Hit)
        {
            if (!TerrainSweep(v, dt)) { v.Flags &= ~Latch; return; }
            if ((v.Flags & Latch) != 0) return;
            v.Flags |= Latch;
        }
        var vel = v.Vel;
        var n = v.Rec.N;
        float cx = v.Rec.C.X, cz = v.Rec.C.Z;
        if (cx * cx < 1e-8f && cz * cz < 1e-8f) return;
        var cxz = new Vector3(cx, 0, cz);
        float k = v.Speed <= 1e-4f ? 1f : 1f - 0.75f * MathF.Pow(Vector3.Dot(vel, n) * v.InvSpeed, 2);
        k = System.Math.Clamp(k, 0.25f, 1f);

        Vector3 w;
        if (v.Rec.Other is Vehicle)
        {
            // vehicle branch: split off the shared centre-of-mass velocity, bounce the rest
            float m = v.P.Mass, mo = v.Rec.OtherMass;
            var vcm = (vel * m + v.Rec.OtherVel * mo) * (1f / (m + mo));
            var u = vel - vcm;
            u = u * 0.5f + n * (-Vector3.Dot(n, u));
            if (Vector3.Dot(u, u) < 16f) u = n * 4f;
            vel = u + vcm;
            var rl = v.Rot.ToLocal(v.Rec.RelVel);
            w = Vector3.Cross(rl, cxz) / Vector3.Dot(cxz, cxz);
            Impacts.Add((v, v.Rec.Other, v.Rec.RelVel, n, v.Rec.C));
            CarHits.Add((v, (Vehicle)v.Rec.Other, v.Rec.RelVel));
        }
        else
        {
            // static branch
            vel = (vel + n * (-2f * Vector3.Dot(n, vel))) * k;
            if (Vector3.Dot(vel, vel) > 16f)
            {
                if (v.Rec.Other is null)
                {
                    Impacts.Add((v, null, v.Rec.RelVel, n, v.Rec.C));   // vtcoll.wav +
                    Damage?.TerrainImpact(v, v.Rec.RelVel, n, v.Rec.C);
                }
            }
            else if (v.Rec.Other is not null || ((v.Flags & VehicleFlags.Airborne) == 0 && v.Rot.Up.Y > 0))
                vel = n * 4f;
            if (vel.Y > 1f) vel.Y *= 0.2f;
            var nl = v.Rot.ToLocal(n);
            w = Vector3.Cross(nl, cxz) * (v.Speed * 0.1f);
        }

        // common tail
        if ((v.Flags & VehicleFlags.JustLanded) == 0)
        {
            var om = v.AngVel + w * v.P.YawRateLimit;
            if (om.X > MathF.PI / 2) om *= (MathF.PI / 2) / om.X; else if (om.X < -MathF.PI / 2) om *= (-MathF.PI / 2) / om.X;
            if (om.Y > MathF.PI) om *= MathF.PI / om.Y; else if (om.Y < -MathF.PI) om *= -MathF.PI / om.Y;
            if (om.Z > MathF.PI / 4) om *= (MathF.PI / 4) / om.Z; else if (om.Z < -MathF.PI / 4) om *= (-MathF.PI / 4) / om.Z;
            v.AngVel = om;
        }
        v.Vel = vel;
        v.Speed = vel.Length();
        // Airborne and more than 35 below the last ground height mid-bounce: killed
        var np = v.Pos + vel * dt;
        if ((v.Flags & VehicleFlags.Airborne) != 0 && v.GroundHeight - (float)np.Y > 35f && ((uint)v.Flags & 0x8020) == 0)
            Damage?.FallKill(v);
        v.Pos = np;
        v.Rot = Rotate(v.Rot, v.AngVel, dt);
        v.Rec.Clear();
        // The slide flag, unless dead or disabled, and the skid marks
        if ((v.Flags & (VehicleFlags)0x8020) == 0) v.Flags |= VehicleFlags.Sliding;
        else v.Flags &= ~VehicleFlags.Sliding;
        Skid(v.RearLeft, true);
        Skid(v.RearRight, true);
    }

    /// <summary>
    /// One rear wheel's skid mark: none when off or with the
    /// wheel at its travel limit, which ends the mark; a mark's first
    /// substep starts the strip (no rand()); each later one extends it, draws
    /// one rand() and throws CHUNK3 debris unless it is a multiple of 3.
    /// </summary>
    internal void Skid(Wheel w, bool on)
    {
        if (!on || w.AtLimit) { w.Marking = false; return; }
        if (!w.Marking) { w.Marking = true; return; }
        rand.Next();
    }

    /// <summary>
    /// The per-frame object pass for this car against the static world
    /// (mesh class): XZ boxes,
    /// a swept-sphere test, then the four mid-height box corners as rays
    /// against the polygons; the earliest wins. Ramps and other structures
    /// hit with n.y &gt; 0.3 are left to the ground query (drive-on
    /// suppression). The record is then the vehicle callback's
    /// to consume on the next substep.
    /// </summary>
    private void ObjectContacts(Vehicle v, float dtFrame, bool signsOnly = false)
    {
        if (ground is not Collision.IHasStatics wg) return;
        var p0 = v.Pos.ToVector3();
        // body sphere: the 8 corners {x0,x3}×{y1,y3}×{z0,z3}, + 0.1
        float r = 0;
        var mid = new Vector3((v.BoxX[0] + v.BoxX[3]) / 2, (v.BoxY[1] + v.BoxY[3]) / 2, (v.BoxZ[0] + v.BoxZ[3]) / 2);
        foreach (var x in new[] { v.BoxX[0], v.BoxX[3] })
            foreach (var y in new[] { v.BoxY[1], v.BoxY[3] })
                foreach (var z in new[] { v.BoxZ[0], v.BoxZ[3] })
                    r = MathF.Max(r, Vector3.Distance(mid, new Vector3(x, y, z)));
        r += 0.1f;
        var c = p0 + v.Rot.ToWorld(mid);
        var c2 = c + v.Vel * dtFrame;
        float minX = MathF.Min(c.X, c2.X) - r, maxX = MathF.Max(c.X, c2.X) + r;
        float minZ = MathF.Min(c.Z, c2.Z) - r, maxZ = MathF.Max(c.Z, c2.Z) + r;
        float ym = (v.BoxY[1] + v.BoxY[3]) * 0.5f;
        var corners = new[] { new Vector3(v.BoxX[0], ym, v.BoxZ[0]), new Vector3(v.BoxX[3], ym, v.BoxZ[0]),
                              new Vector3(v.BoxX[3], ym, v.BoxZ[3]), new Vector3(v.BoxX[0], ym, v.BoxZ[3]) };
        float best = float.MaxValue;
        Contact rec = default;
        bool driveOn = false;
        foreach (var o in wg.World.Objects)
        {
            if (!o.Solid) continue;
            if (o.Type == 4)
            {
                // a sign: its sphere against the car's box, now and at the end of the frame; no response, it is knocked off
                float reach = r + o.SphereRadius + v.Speed * dtFrame;
                if ((new Vector2(o.Centre.X - c.X, o.Centre.Z - c.Z)).LengthSquared() > reach * reach) continue;
                if (SphereHitsBox(v, o.Centre, o.SphereRadius, dtFrame))
                {
                    ObjectHits.Add((v, o.Name, o.Type, Vector3.UnitY, v.Speed));
                    Damage?.StaticContact(v, o, new Contact { Hit = true, Other = o });
                }
                continue;
            }
            // another model moves this car: of the rest, only a structure's sign nodes, which that model leaves out
            if (signsOnly && !o.Nodes.Exists(n => n.Sign)) continue;
            if (o.Centre.X + o.Radius < minX || o.Centre.X - o.Radius > maxX || o.Centre.Z + o.Radius < minZ || o.Centre.Z - o.Radius > maxZ) continue;
            if (!SweptSphere(c, v.Vel, o.Centre, r + o.Radius, dtFrame)) continue;
            foreach (var b in o.Boxes)
            {
                if (signsOnly && !o.SignNode(b.Node)) continue;
                if (!SweptSphere(c, v.Vel, b.Centre, r + b.Radius, dtFrame)) continue;
                if (BoxBox(v, b, dtFrame) is { } hit && hit.T < best)
                {
                    best = hit.T;
                    rec = hit with { Other = o, Node = b.Node };
                }
            }
            var polys = signsOnly ? o.Polys.FindAll(q => o.SignNode(q.Node)) : o.Polys;
            if (polys.Count == 0) continue;
            foreach (var corner in corners)
            {
                var pw = p0 + v.Rot.ToWorld(corner);
                var pv = v.Vel + v.Rot.ToWorld(Vector3.Cross(corner, v.AngVel));
                var (t, poly) = Collision.StaticWorld.RayMesh(polys, pw, pv, dtFrame);
                if (poly is null || t >= best) continue;
                if (o.IsStructure && poly.N.Y > 0.3f) { driveOn = true; continue; }      // drive-on: the ground query handles it
                best = t;
                rec = new Contact { Hit = true, Other = o, T = t, C = corner, RelVel = -pv, N = poly.N, Node = poly.Node };
            }
        }
        if (driveOn && !DriveOns.Contains(v)) DriveOns.Add(v);   // vvbo1.wav, once a frame
        if (rec.Hit)
        {
            var so = (Collision.StaticObject)rec.Other!;
            // A type-4 node (signs, pickups, a structure's sign nodes) returns before copying the record -- no response at all
            bool sign = so.Type == 4 || so.SignNode(rec.Node ?? -1);
            if (!sign) v.Rec = rec;
            ObjectHits.Add((v, so.Name, sign ? 4u : so.Type, rec.N, v.Speed));
            Damage?.StaticContact(v, so, rec);
        }
    }

    /// <summary>
    /// Both ways for a car against a class-0x2000 node: the car's
    /// 8 mid-height perimeter points as rays into the node's box, and, if
    /// none hits, the node box's 8 points into the car's box (the car's spin
    /// ignored). The record is the car's: c in its frame, n from the node
    /// toward the car.
    /// </summary>
    private static Contact? BoxBox(Vehicle v, Collision.NodeBox b, float dt)
    {
        var p0 = v.Pos.ToVector3();
        var carMin = new Vector3(v.BoxX[0], v.BoxY[1], v.BoxZ[0]); var carMax = new Vector3(v.BoxX[3], v.BoxY[3], v.BoxZ[3]);
        float best = float.MaxValue;
        Contact? rec = null;
        foreach (var pt in Perimeter(carMin, carMax))
        {
            var pv = v.Vel + v.Rot.ToWorld(Vector3.Cross(pt, v.AngVel));
            var lp = b.Rot.ToLocal(p0 + v.Rot.ToWorld(pt) - b.Origin);
            var rv = b.Rot.ToLocal(pv);
            if (SlabRay(lp, rv, b.Min, b.Max, dt) is not { } h || h.T >= best) continue;
            best = h.T;
            rec = new Contact { Hit = true, T = h.T, C = pt, RelVel = -pv, N = b.Rot.ToWorld(h.N) };
        }
        if (rec is not null) return rec;
        var rvc = v.Rot.ToLocal(-v.Vel);
        foreach (var pt in Perimeter(b.Min, b.Max))
        {
            var lp = v.Rot.ToLocal(b.Origin + b.Rot.ToWorld(pt) - p0);
            if (SlabRay(lp, rvc, carMin, carMax, dt) is not { } h || h.T >= best) continue;
            best = h.T;
            rec = new Contact { Hit = true, T = h.T, C = lp + rvc * h.T, RelVel = -v.Vel, N = -v.Rot.ToWorld(h.N) };
        }
        return rec;
    }

    /// <summary>The 8 sample points at a box's mid-height: corners and edge midpoints, in the order.</summary>
    private static Vector3[] Perimeter(Vector3 min, Vector3 max)
    {
        float ym = (min.Y + max.Y) * 0.5f, xm = (min.X + max.X) * 0.5f, zm = (min.Z + max.Z) * 0.5f;
        return [new(min.X, ym, min.Z), new(xm, ym, min.Z), new(max.X, ym, min.Z), new(max.X, ym, zm),
                new(max.X, ym, max.Z), new(xm, ym, max.Z), new(min.X, ym, max.Z), new(min.X, ym, zm)];
    }

    /// <summary>
    /// With no pad: a slab ray against an AABB, axes x, z, y; a
    /// hit needs t_enter ≤ t_exit, t_exit ≥ 0 and 0 ≤ t_enter ≤ dt (a point
    /// already inside is no hit). N is the entered face's outward normal.
    /// </summary>
    private static (float T, Vector3 N)? SlabRay(Vector3 p, Vector3 v, Vector3 min, Vector3 max, float dt)
    {
        float tEnter = float.MinValue, tExit = float.MaxValue;
        Vector3 n = default;
        foreach (int axis in (ReadOnlySpan<int>)[0, 2, 1])
        {
            float pa = p[axis], va = v[axis], lo = min[axis], hi = max[axis];
            if (MathF.Abs(va) < 1e-8f)
            {
                if (pa < lo || pa > hi) return null;
                continue;
            }
            float t0 = (lo - pa) / va, t1 = (hi - pa) / va;
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (t0 > tEnter)
            {
                tEnter = t0;
                n = Vector3.Zero;
                n[axis] = va > 0 ? -1 : 1;
            }
            if (t1 < tExit) tExit = t1;
        }
        if (tEnter > tExit || tExit < 0 || tEnter < 0 || tEnter > dt) return null;
        return (tEnter, n);
    }

    /// <summary>A sphere against the car's COLP box (x0..x3, y1..y3, z0..z3) at the start and the end of the frame's motion.</summary>
    private static bool SphereHitsBox(Vehicle v, Vector3 centre, float radius, float dt)
    {
        var min = new Vector3(v.BoxX[0], v.BoxY[1], v.BoxZ[0]); var max = new Vector3(v.BoxX[3], v.BoxY[3], v.BoxZ[3]);
        var p0 = v.Pos.ToVector3();
        for (int k = 0; k <= 2; k++)
        {
            var local = v.Rot.ToLocal(centre - (p0 + v.Vel * (dt * k / 2f)));
            var q = Vector3.Clamp(local, min, max);
            if ((q - local).LengthSquared() < radius * radius) return true;
        }
        return false;
    }

    /// <summary>For a moving sphere against a static one.</summary>
    private static bool SweptSphere(Vector3 ca, Vector3 va, Vector3 cb, float rsum, float dt)
    {
        var d0 = cb - ca;
        if (va.X * va.X < 1e-8f && va.Y * va.Y < 1e-8f && va.Z * va.Z < 1e-8f) return d0.LengthSquared() <= rsum * rsum;
        float vv = va.LengthSquared();
        float t = vv < 1e-4f ? dt : Vector3.Dot(va, d0) / vv;
        if (t > dt) t = dt;
        var d = t > 0 ? d0 - va * t : d0;
        return d.LengthSquared() <= rsum * rsum;
    }

    /// <summary>Object contacts the cars made (which car, name, normal, speed), for sound and damage.</summary>
    public List<(Vehicle Self, string Name, uint Type, Vector3 N, float Speed)> ObjectHits { get; } = [];

    /// <summary>Cars that touched a ramp or bridge (types 11-13) from above this tick: no response, but the original plays vvbo1.wav (drive-on).</summary>
    public List<Vehicle> DriveOns { get; } = [];

    /// <summary>AI cars that honked this tick (1 in 4 when blocked by a vehicle): a positional one-shot of their horn. A police car's honk turns its siren on instead (Entity.Siren).</summary>
    public List<Vehicle> Honks { get; } = [];

    /// <summary>
    /// Impacts the step reported (for sound and damage), cleared by the caller:
    /// which car, what it struck (another vehicle, or null for the ground),
    /// and the contact's relative velocity, normal and point in the car's frame.
    /// </summary>
    public List<(Vehicle Self, object? Other, Vector3 RelVel, Vector3 N, Vector3 C)> Impacts { get; } = [];
    /// <summary>Car-against-car responses this frame (who was hit by whom), for the damage-event recorder; cleared by the caller.</summary>
    public List<(Vehicle Self, Vehicle Other, Vector3 RelVel)> CarHits { get; } = [];

    /// <summary>
    /// Sweep the four mid-height box corners through this
    /// substep against the bare heightfield; the first corner under the
    /// ground (earliest pass, index order) makes a terrain contact.
    /// </summary>
    private bool TerrainSweep(Vehicle v, float dt)
    {
        var tg = ground;
        var corners = v.Corners;
        var pw = new Vector3[4];
        var vp = new Vector3[4];
        var prev = new Vector3[4];
        var done = new bool[4];
        var p0 = v.Pos.ToVector3();
        for (int i = 0; i < 4; i++)
        {
            pw[i] = p0 + v.Rot.ToWorld(corners[i]);
            vp[i] = v.Vel + v.Rot.ToWorld(Vector3.Cross(corners[i], v.AngVel));
        }
        int n = System.Math.Min((int)(v.Speed * dt) + 1, 7);
        float step = dt / n;
        float H(Vector3 p) => tg.BareHeight(p.X, p.Z) - p.Y;
        for (int k = 0; k <= n; k++)
            for (int i = 0; i < 4; i++)
            {
                if (done[i]) continue;
                float h = H(pw[i]);
                if (h < 0) { prev[i] = pw[i]; pw[i] += vp[i] * step; continue; }
                if (k == 0)
                {
                    float h0 = h;
                    var q = pw[i];
                    for (int it = 0; it < 50; it++)
                    {
                        if (h < 0 || h > h0) break;
                        q -= vp[i] * step;
                        h = H(q);
                    }
                    if (h > 0) { done[i] = true; continue; }
                    if (h >= -0.01f) return Hit(v, i, q, 0, vp[i], tg);
                    return Hit(v, i, Bisect(pw[i], q), 0, vp[i], tg);
                }
                return Hit(v, i, Bisect(pw[i], prev[i]), k * step, vp[i], tg);
            }
        return false;

        Vector3 Bisect(Vector3 inside, Vector3 outside)
        {
            var m = inside;
            for (int it = 0; it < 50; it++)
            {
                m = (inside + outside) * 0.5f;
                float h = H(m);
                if (h < 0 && h > -0.01f) break;
                if (h > 0) inside = m; else outside = m;
            }
            return m;
        }
    }

    private static bool Hit(Vehicle v, int corner, Vector3 p, float t, Vector3 pointVel, IGround tg)
    {
        v.Rec = new Contact
        {
            Hit = true, Other = null, T = t, C = v.Corners[corner], RelVel = -pointVel, N = tg.BareNormal(p.X, p.Z),
        };
        return true;
    }

    /// <summary>
    /// An upside-down car at rest. With 0x10000 it flips
    /// upright; otherwise it is wrecked (reported) and its
    /// rates zeroed.
    /// </summary>
    /// <summary>
    /// Once the pitch and roll rates are under 0.628 and the car
    /// is square to the ground (|up·n| &gt; 0.8) it has settled: a car resting on
    /// its roof flips back (Arcade Phys., 0x10000) or is destroyed, and the
    /// "just landed" flag 0x200 clears with a second vland.
    /// </summary>
    private void Rollover(Vehicle v)
    {
        float d = Vector3.Dot(v.Rot.Up, v.GroundNormal);
        if (MathF.Abs(v.AngVel.X) >= 0.62831855f || MathF.Abs(v.AngVel.Z) >= 0.62831855f || MathF.Abs(d) <= 0.8f) return;
        if ((v.Flags & (VehicleFlags)0x24) == 0 && d < 0)
        {
            if ((v.Flags & VehicleFlags.LoadBasedCornering) != 0 && !NetSession)     // not in a network game
            {
                v.Rot = new Rows(-v.Rot.Right, -v.Rot.Up, v.Rot.Forward);
                Landings.Add(v);
                goto Settled;
            }
            Wrecked = true;
            Damage?.FallKill(v);
            v.AngVel.X = v.AngVel.Y = 0;
            v.AngVel.Z = 0;
        }
    Settled:
        if ((v.Flags & VehicleFlags.JustLanded) != 0)
        {
            Landings.Add(v);
            v.Flags &= ~VehicleFlags.JustLanded;
        }
    }

    /// <summary>A network session: an upside-down car is never righted, Arcade Phys. or not.</summary>
    public bool NetSession { get; set; }

    /// <summary>vland.wav: touchdowns and the settle after them (sound event 4).</summary>
    public List<Vehicle> Landings { get; } = [];

    /// <summary>Set when the car ends up upside down without the self-righting option.</summary>
    public bool Wrecked { get; set; }

    private static void Integrate(ref Vector3 p, ref Vector3 v, Vector3 a, float dt)
    {
        p += (v + a * (dt * 0.5f)) * dt;
        v += a * dt;
    }

    private static float Ease(float cur, float target, float k)
    {
        if (k < 0 || k > 0.95f) return target;
        if (MathF.Abs(target - cur) < 0.005f) return target;
        return (target - cur) * k + cur;
    }

    /// <summary>Rotate by Euler(−ω·dt) (pitch, yaw, roll), applied in the object's frame.</summary>
    private static Rows Rotate(Rows r, Vector3 w, float dt) =>
        Rows.Concat(Rows.Euler(-(dt * w.X), -(dt * w.Y), -(dt * w.Z)), r).Orthonormalized();

    /// <summary>The surface's grip, 0 if above 0.2 with flag 0x400.</summary>
    private float SurfaceGrip(Vehicle v)
    {
        float g = ground.Surface(v.Surface).Grip;
        return g > 0.2f && (v.Flags & VehicleFlags.NoGripOnLowSurface) != 0 ? 0 : g;
    }

    /// <summary>
    /// The
    /// force along forward. Drive needs flag bit 0 (engine running) and drive
    /// state ≠ 2. Sliding (flag 2) drives with the engine's peak force and curve instead of the
    /// engine curve, and scales the brake cap by how much of the motion is along
    /// the heading. Tipped brakes at twice the cap.
    /// </summary>
    private float DriveForce(Vehicle v, float throttle, bool tipped, uint flags, ref bool slide)
    {
        if (throttle > 0 && ((flags & 1) == 0 || v.DriveState == 2)) return 0;
        float load = SurfaceGrip(v) * (v.NormalLoad * 0.25f);
        float fwdVel = Vector3.Dot(v.Vel, v.Rot.Forward);
        uint mode;
        if (tipped) { throttle = -1; mode = 1; } else mode = flags & 2;
        if (throttle > -1e-4f)
        {
            if (throttle < 1e-4f) return 0;
            float traction = (v.RearRight.Grip + v.RearLeft.Grip) * load;
            float f = (mode == 0 ? v.Engine.Force : v.Engine.PeakForce * v.Engine.Damage) * v.P.DriveScale * v.P.EngineScale;
            if ((v.Flags & VehicleFlags.TurboDrive) != 0) f *= 5;
            if (v.Speed > 1) f *= v.InvSpeed;
            if (f < 1e-4f) f = 1e-4f;
            f *= throttle;
            if (traction < f) { f = traction; slide = true; }
            return f * v.Gear;
        }
        if (v.Speed <= 1) return 0;
        float cap = -((v.RearRight.Grip + v.FrontRight.Grip + v.RearLeft.Grip + v.FrontLeft.Grip) * load);
        float b = v.P.BrakeStrength * throttle * 8f;
        if (mode != 0) { cap = v.InvSpeed * fwdVel * cap; if (fwdVel < 0) cap = -cap; }
        if (v.Handbrake != 0) cap *= 0.5f;
        if (!tipped) { if (b < cap) { b = cap; slide = true; } }
        else b = cap + cap;
        return fwdVel < 0 ? -b : b;
    }

    /// <summary>
    /// The forward-clearance measure, which despite the name measures
    /// no distance: it binary-searches (6 halvings from max/2) the largest
    /// throttle (or brake, for a negative max) that the original does not
    /// clamp to the traction limit, i.e. the most the car can take without
    /// wheelspin or locking. The AI throttle caps its demand with it. A far
    /// car or a helicopter gets <paramref name="max"/> back unsearched.
    /// </summary>
    public float TractionLimit(Vehicle v, float max, bool far = false)
    {
        if (far || v.IsHeli) return max;
        float x = max * 0.5f, step = x, best = 0;
        for (int i = 0; i < 6; i++)
        {
            step *= 0.5f;
            bool slide = false;
            DriveForce(v, x, false, (uint)v.Flags, ref slide);
            if (!slide) { best = x; x += step; }
            else x -= step;
        }
        return best;
    }

    /// <summary>The commanded yaw rate.</summary>
    private float YawRate(Vehicle v, float speed, bool isPlayer, ref float blend, out bool slid)
    {
        slid = false;
        float steer = v.Steer;
        float sumGrip = v.RearRight.Grip + v.RearLeft.Grip + v.FrontRight.Grip + v.FrontLeft.Grip;
        float maxLatLoad = v.NormalLoad * v.P.ChassisGrip * SurfaceGrip(v) * sumGrip * 0.25f;
        float maxLat = (v.Flags & VehicleFlags.LoadBasedCornering) == 0 ? sumGrip * 7.84f : maxLatLoad;
        float slideLimit = maxLatLoad + maxLatLoad;
        if (isPlayer)
        {
            bool fr = v.FrontRight.Blown, fl = v.FrontLeft.Blown;
            if (fr && fl) steer += (rand.Next() & 1) == 0 ? -0.3f : 0.3f;
            else
            {
                if (fl) steer += 0.3f;
                if (fr) steer -= 0.3f;
            }
            steer = System.Math.Clamp(steer, -1f, 1f);
        }
        float yaw = speed * speed * v.P.SteerGain <= maxLat ? steer * speed * v.P.SteerGain : steer * maxLat / speed;
        float lat = MathF.Abs(yaw * speed);
        if (lat <= slideLimit)
        {
            if (maxLatLoad < lat) blend = (lat - slideLimit) / (maxLatLoad - slideLimit);
        }
        else slid = true;
        return yaw;
    }

    /// <summary>One wheel's force when sliding (or tipped).</summary>
    private static void WheelForce(Vehicle v, Wheel w, float gripLoad, bool locked, ref Vector3 accel, ref Vector3 torque)
    {
        var wr = Rows.Concat(Rows.AboutY(w.SteerAngle), v.Rot);
        float x = w.Offset.X, z = w.Offset.Z;
        var om = v.AngVel;
        float a = z * om.X - x * om.Z, b = x * om.Y, c = -(z * om.Y);
        var vc = v.Vel + wr.Forward * b + wr.Up * a + wr.Right * c;
        float len2 = vc.LengthSquared();
        if (len2 < 3e-8f) return;
        float len = MathF.Sqrt(len2);
        Vector3 f = locked
            ? vc * (-(w.Grip * gripLoad) / len)
            : wr.Right * (-(Vector3.Dot(vc, wr.Right) * w.Grip * gripLoad) / len);
        var r = wr.Forward * z + wr.Right * x;
        var (along, _) = VecOps.Split(f, r);
        accel += along;
        var t = Vector3.Cross(r, f);
        torque.Y += wr.ToLocal(t).Y;
    }

    /// <summary>
    /// A newly placed car's suspension history, as the original's entity set-up leaves it once the car stands where it
    /// will start: each wheel's ground and last target are the ground under the wheel (the height
    /// <see cref="Suspension"/> queries there), and the target's rate is 0. Travel and the filter stay as built, at 0. A car
    /// at rest then sees no change in its target on its first substep and stands still on its springs; with the history
    /// left at 0 it took the whole terrain height as one bump and bobbed as the mission began (#141). Every path that
    /// places a fresh car calls this after its pose is set; a repair in place keeps its history, and a save or a
    /// snapshot restores its own.
    /// </summary>
    public static void PrimeWheels(Vehicle v, IGround ground)
    {
        foreach (var w in v.Wheels.Concat(v.MiddleWheels))
        {
            var p = v.Pos.ToVector3() + v.Rot.ToWorld(w.Mount);
            w.Ground = w.PrevTarget = ground.Query(p.X, p.Y, p.Z).Height;
            w.PrevRate = 0;
        }
    }

    /// <summary>
    /// One wheel's suspension. The target is the ground under
    /// the wheel plus a bump (by the wheel's world position,
    /// times the surface's roughness); the input is the drive term plus 5 ×
    /// the change in the target's rate, filtered by the two-pole IIR and
    /// clamped to the travel limits. Locked (airborne): the input is the
    /// lower limit.
    /// </summary>
    private void Suspension(Vehicle v, Wheel w, bool locked, float drive, int surface)
    {
        var world = v.Pos.ToVector3() + v.Rot.ToWorld(w.Mount);
        var (g, _) = ground.Query(world.X, world.Y, world.Z);
        w.Ground = g;
        int ix = (int)world.X, iz = (int)world.Z;    // ftol
        int bi = (ix & 7) + (iz & 7) * 8;
        float rough = ground.Surface(surface).Bump;
        float target = rough * Bump[bi] + g;
        if (w.Blown && rough < 0.1f) target += Bump[bi] * 0.1f;
        float rate = target - w.PrevTarget;
        float x = !locked ? drive + (rate - w.PrevRate) * 5f : w.TravelMin;
        w.AtLimit = locked || x < w.TravelMin;
        w.PrevTarget = target;
        w.PrevRate = rate;
        double y = x * w.B0 - w.A1 * w.Y1 - w.A2 * w.Y2;
        w.Y2 = w.Y1;
        w.Y1 = y;
        // the order: under the lower limit gives it, else over the upper gives that. A wheel with a
        // negative rest (S06) has the limits reversed: this picks the lower one, where Math.Clamp threw.
        float yf = (float)y;
        w.Travel = yf < w.TravelMin ? w.TravelMin : yf > w.TravelMax ? w.TravelMax : yf;
    }

    /// <summary>64 cells of 0 or 0.1 by (x &amp; 7, z &amp; 7), rows by z.</summary>
    public static readonly float[] Bump = BuildBump();

    private static float[] BuildBump()
    {
        string[] rows = ["10100101", "00001000", "00100110", "10010000", "00100101", "01001000", "00000010", "01010001"];
        var t = new float[64];
        for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
                t[x + z * 8] = rows[z][x] == '1' ? 0.1f : 0f;
        return t;
    }

    /// <summary>
    /// The attitude. The acceleration's along-heading and
    /// sideways parts (× −0.008) drive the four springs in opposing pairs;
    /// the body is lifted along the ground normal by the largest penetration;
    /// wheels still above the ground set a contact mask whose pitch and roll
    /// targets (±π per wheel) are eased in at 2·dt and applied about the
    /// mask's pivot. The plane fit through the four contacts
    /// only applies when neither the grounded nor
    /// the slide flag is set; it is approximated here.
    /// </summary>
    /// <summary>
    /// The suspension, then the body lifted (or pulled down) so
    /// its lowest contact sits on the ground, and the attitude: targets of ±π
    /// per lifted contact eased at 2dt and rotated about the mask's pivot,
    /// snapping to the plane through the front contacts and the rear-axle
    /// midpoint (at ground − travel) only when the eased step would overshoot
    /// it and the car is neither sliding nor just landed. On its side or
    /// upside down (up.y ≤ 0.1) the roof corners are the contacts.
    ///
    /// </summary>
    private void Attitude(Vehicle v, Vector3 accel, VehicleFlags oldFlags, float dt, bool rejoin = false)
    {
        var fwd = v.Rot.Forward;
        var (along, across) = VecOps.Split(accel, fwd);
        float fc = Vector3.Dot(along, fwd) * -0.008f;
        float f8 = Vector3.Dot(across, v.Rot.Right) * -0.008f;
        Suspension(v, v.FrontLeft, false, fc - f8, v.Surface);
        Suspension(v, v.FrontRight, false, f8 + fc, v.Surface);
        Suspension(v, v.RearLeft, false, -fc - f8, v.Surface);
        Suspension(v, v.RearRight, false, f8 - fc, v.Surface);

        var pos = v.Pos.ToVector3();
        bool inverted = v.Rot.Up.Y <= 0.1f;
        float roof = v.P.RideOffset;
        float Corner(float x, float z) => (pos + v.Rot.ToWorld(new Vector3(x, roof, z))).Y;
        float C(Wheel w) => (pos + v.Rot.ToWorld(w.Mount)).Y - w.Rest;   // wheel centre − rest
        float fl, fr, rl, rr;
        if (inverted) { fl = Corner(v.BoxX[0], v.BoxZ[3]); fr = Corner(v.BoxX[3], v.BoxZ[3]); rl = Corner(v.BoxX[0], v.BoxZ[0]); rr = Corner(v.BoxX[3], v.BoxZ[0]); }
        else { fl = C(v.FrontLeft); fr = C(v.FrontRight); rl = C(v.RearLeft); rr = C(v.RearRight); }
        float lift = MathF.Max(MathF.Max(v.FrontLeft.Ground - fl, v.FrontRight.Ground - fr), MathF.Max(v.RearLeft.Ground - rl, v.RearRight.Ground - rr));

        float pitch = 0, roll = 0;
        int mask = 0;
        if (fl + lift - v.FrontLeft.Ground > 1e-4f) { roll -= MathF.PI / 2; pitch = -MathF.PI; mask = 8; }
        if (fr + lift - v.FrontRight.Ground > 1e-4f) { pitch -= MathF.PI; roll += MathF.PI / 2; mask |= 4; }
        if (rl + lift - v.RearLeft.Ground > 1e-4f) { pitch += MathF.PI; roll -= MathF.PI / 2; mask |= 2; }
        if (rr + lift - v.RearRight.Ground > 1e-4f) { pitch += MathF.PI; roll += MathF.PI / 2; mask |= 1; }
        if (inverted) { pitch = -pitch; roll = -roll; }
        float k = dt + dt;
        if (k >= 0 && k <= 0.95f && MathF.Abs(pitch - v.AngVel.X) >= 0.005f) pitch = k * (pitch - v.AngVel.X) + v.AngVel.X;
        if (k >= 0 && k <= 0.95f && MathF.Abs(roll - v.AngVel.Z) >= 0.005f) roll = k * (roll - v.AngVel.Z) + v.AngVel.Z;

        v.Pos += v.GroundNormal * lift;
        var basePos = v.Pos;
        var rot = v.Rot; var rotPos = basePos;
        if (pitch * pitch >= 1e-8f || roll * roll >= 1e-8f)
        {
            var (px, pz) = v.P.Pivot(mask);
            var pivot = basePos + v.Rot.Right * px + v.Rot.Forward * pz;
            rot = Rotate(v.Rot, new Vector3(pitch, 0, roll), dt);
            rotPos = pivot - (rot.Right * px + rot.Forward * pz);
        }

        // the plane through FL, FR and the rear-axle midpoint, at ground − travel
        var p0 = basePos.ToVector3();
        Vector3 Q(Wheel w) { var q = p0 + v.Rot.ToWorld(w.Mount); return new(q.X, w.Ground - w.Travel, q.Z); }
        var qfl = Q(v.FrontLeft); var qfr = Q(v.FrontRight);
        var mid = (Q(v.RearLeft) + Q(v.RearRight)) * 0.5f;
        var np = Vector3.Cross(mid - qfr, qfl - qfr);
        np = np.LengthSquared() > 1e-12f ? Vector3.Normalize(np) : Vector3.UnitY;
        var up = v.Rot.Up;
        bool keep = (oldFlags & (VehicleFlags)0x202) != 0;
        if (!rejoin)
        {
            if (keep || Vector3.Dot(up, np) <= Vector3.Dot(up, rot.Up))
            {
                v.Rot = rot; v.Pos = rotPos;
                v.AngVel.X = pitch;
                v.AngVel.Z = roll;
                return;
            }
        }
        else if (np.Y < 0) np = -np;
        // Snap to the plane, heading kept
        var f = fwd;
        if (np.Y > 1e-4f) f.Y = -(np.Z * f.Z + np.X * f.X) / np.Y;
        var right = Vector3.Cross(np, f);
        if (right.LengthSquared() > 1e-12f)
        {
            right = Vector3.Normalize(right);
            v.Rot = new Rows(right, np, Vector3.Cross(right, np));
        }
        v.AngVel.X = 0;
        v.AngVel.Z = 0;
    }
}
