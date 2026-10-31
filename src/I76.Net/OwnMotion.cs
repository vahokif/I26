using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Net;

/// <summary>
/// The recipient's own car at full precision, for its client's prediction (protocol 13): everything the vehicle step
/// and the driver's controls carry from one tick to the next (the body, its contact record, the engine, each wheel's
/// suspension, the key ramps), so that a client restoring it and stepping the same inputs on the same frame deltas gets
/// the server's car bit for bit. <see cref="Slow"/> is what only damage, a blow-out or a renewal changes (the car's
/// parameters, the engine's curve, each wheel's grip and rest): it rides along only for a while after it changes and
/// once a second. The rest of the own car (damage parts, armoury, specials) is <see cref="OwnCar"/>'s, the looks
/// <see cref="CarState"/>'s. The wheels' spin and steer angle are left out: one is drawn only, the other set from the
/// steer at the start of every step.
/// </summary>
public sealed class OwnMotion
{
    public Rows Rot;
    public Double3 Pos;
    public Vector3 Vel, AngVel, Accel;
    public float Speed, Steer, Throttle, Gear;
    public int Handbrake, Unk_0xf4, StartEngineKey, DriveState;
    public VehicleFlags Flags;
    public int Surface;
    public Vector3 GroundNormal;
    public float NormalLoad, HeightAboveGround, GroundHeight, Timer;
    public bool Wreck;
    public int Generation;
    /// <summary>The contact record (veh+0x38): what it touched (0 nothing, 1 the terrain, 2 a car, 3 a structure) and its id.</summary>
    public byte RecKind;
    public int RecSerial;
    public Contact Rec;
    public int EngineGear;
    public float Rpm, Speedo, Force;
    public WheelMotion[] Wheels = [];
    /// <summary>The driver's key ramps (hold times), and whether they belong to an older car (a renewal the next tick resets them for).</summary>
    public float HoldUp, HoldDown, HoldRight, HoldLeft;
    public bool KeysStale;
    /// <summary>A helicopter's flight state (veh+0x508: the eased controls, the height it holds, a contact this frame); only a class-9 car has it.</summary>
    public bool IsHeli, HeliTouched;
    public float HeliThrust, HeliSide, HeliHeight;
    public SlowMotion? Slow;

    public struct WheelMotion
    {
        public float Travel, Ground, PrevTarget, PrevRate;
        public bool AtLimit, Blown;
        public double Y1, Y2;
    }

    /// <summary>What changes only with damage, a blow-out or a renewal.</summary>
    public sealed class SlowMotion
    {
        public readonly float[] Params = new float[12];
        public float Curve, PeakForce, EngineDamage;
        public (float Grip, float Rolling, float RestHeight)[] Wheels = [];

        public static SlowMotion Capture(Vehicle v)
        {
            var s = new SlowMotion { Curve = v.Engine.Curve, PeakForce = v.Engine.PeakForce, EngineDamage = v.Engine.Damage };
            var p = v.P;
            float[] ps = [p.Mass, p.DriveScale, p.SteerGain, p.PivotForward, p.Drag, p.EngineScale, p.YawRateLimit, p.RideOffset, p.ChassisGrip, p.BrakeStrength, p.Wheelbase, p.Track];
            ps.CopyTo(s.Params, 0);
            s.Wheels = [.. AllWheels(v).Select(w => (w.Grip, w.Rolling, w.Rest))];
            return s;
        }

        public void ApplyTo(Vehicle v)
        {
            var p = v.P;
            var q = Params;
            (p.Mass, p.DriveScale, p.SteerGain, p.PivotForward, p.Drag, p.EngineScale) = (q[0], q[1], q[2], q[3], q[4], q[5]);
            (p.YawRateLimit, p.RideOffset, p.ChassisGrip, p.BrakeStrength, p.Wheelbase, p.Track) = (q[6], q[7], q[8], q[9], q[10], q[11]);
            (v.Engine.Curve, v.Engine.PeakForce, v.Engine.Damage) = (Curve, PeakForce, EngineDamage);
            int k = 0;
            foreach (var w in AllWheels(v))
            {
                if (k >= Wheels.Length) break;
                (w.Grip, w.Rolling, w.Rest) = Wheels[k++];
            }
        }

        public void Write(NetWriter w)
        {
            foreach (var x in Params) w.F32(x);
            w.F32(Curve); w.F32(PeakForce); w.F32(EngineDamage);
            w.U8((byte)Wheels.Length);
            foreach (var (g, r, rest) in Wheels) { w.F32(g); w.F32(r); w.F32(rest); }
        }

        public static SlowMotion Read(ref NetReader r)
        {
            var s = new SlowMotion();
            for (int i = 0; i < s.Params.Length; i++) s.Params[i] = r.F32();
            (s.Curve, s.PeakForce, s.EngineDamage) = (r.F32(), r.F32(), r.F32());
            int n = r.U8();
            if (n > 6) throw new WireException($"{n} wheels");
            s.Wheels = new (float, float, float)[n];
            for (int i = 0; i < n; i++) s.Wheels[i] = (r.F32(), r.F32(), r.F32());
            return s;
        }

        public bool SameAs(SlowMotion o) => Params.AsSpan().SequenceEqual(o.Params) && Curve == o.Curve && PeakForce == o.PeakForce
            && EngineDamage == o.EngineDamage && Wheels.AsSpan().SequenceEqual(o.Wheels);
    }

    private static IEnumerable<Wheel> AllWheels(Vehicle v) => v.Wheels.Concat(v.MiddleWheels);

    public static OwnMotion Capture(Vehicle v, Driver? d, bool slow = false)
    {
        var m = new OwnMotion
        {
            Rot = v.Rot, Pos = v.Pos, Vel = v.Vel, AngVel = v.AngVel, Accel = v.Accel, Speed = v.Speed, Steer = v.Steer, Throttle = v.Throttle,
            Gear = v.Gear, Handbrake = v.Handbrake, Unk_0xf4 = v.Unk_0xf4, StartEngineKey = v.StartEngineKey, DriveState = v.DriveState,
            Flags = v.Flags, Surface = v.Surface, GroundNormal = v.GroundNormal, NormalLoad = v.NormalLoad, HeightAboveGround = v.HeightAboveGround,
            GroundHeight = v.GroundHeight, Timer = v.Timer, Wreck = v.Wreck, Generation = v.NetGeneration, Rec = v.Rec,
            EngineGear = v.Engine.Gear, Rpm = v.Engine.Rpm, Speedo = v.Engine.Speedo, Force = v.Engine.Force,
        };
        (m.RecKind, m.RecSerial) = !v.Rec.Hit ? ((byte)0, 0) : v.Rec.Other switch
        {
            null => ((byte)1, 0),
            Vehicle o => ((byte)2, o.NetId & 0xffffff),
            StaticObject o => ((byte)3, o.NetId & 0xffffff),
            _ => ((byte)1, 0),
        };
        m.Wheels = [.. AllWheels(v).Select(w => new WheelMotion
        {
            Travel = w.Travel, Ground = w.Ground, PrevTarget = w.PrevTarget, PrevRate = w.PrevRate, AtLimit = w.AtLimit, Blown = w.Blown, Y1 = w.Y1, Y2 = w.Y2,
        })];
        if (d is not null)
        {
            (m.HoldUp, m.HoldDown, m.HoldRight, m.HoldLeft) = (d.State.HoldUp, d.State.HoldDown, d.State.HoldRight, d.State.HoldLeft);
            m.KeysStale = d.Generation != v.NetGeneration;
        }
        if (v.Heli is { } h) (m.IsHeli, m.HeliThrust, m.HeliSide, m.HeliHeight, m.HeliTouched) = (true, h.Thrust, h.Side, h.Height, h.Touched);
        if (slow) m.Slow = SlowMotion.Capture(v);
        return m;
    }

    private static readonly Vehicle SomeCar = new();
    private static readonly StaticObject SomeStructure = new() { Name = "", Type = 0 };

    /// <summary>
    /// Puts the car (and its driver's key ramps) back as the server had it. <paramref name="slow"/> is the latest slow
    /// part received (this one's own if it has one). The contact's other party is looked up by id; the step only asks
    /// what kind it is.
    /// </summary>
    public void ApplyTo(Vehicle v, Driver? d, SlowMotion? slow, Func<int, Vehicle?> car, Func<int, StaticObject?> structure)
    {
        v.Rot = Rot; v.Pos = Pos; v.Vel = Vel; v.AngVel = AngVel; v.Accel = Accel; v.Speed = Speed; v.Steer = Steer; v.Throttle = Throttle;
        v.Gear = Gear; v.Handbrake = Handbrake; v.Unk_0xf4 = Unk_0xf4; v.StartEngineKey = StartEngineKey; v.DriveState = DriveState;
        v.Flags = Flags; v.Surface = Surface; v.GroundNormal = GroundNormal; v.NormalLoad = NormalLoad; v.HeightAboveGround = HeightAboveGround;
        v.GroundHeight = GroundHeight; v.Timer = Timer; v.Wreck = Wreck; v.NetGeneration = Generation;
        var rec = Rec;
        rec.Other = RecKind switch { 2 => car(RecSerial) ?? SomeCar, 3 => (object?)structure(RecSerial) ?? SomeStructure, _ => null };
        v.Rec = rec;
        v.Engine.Gear = EngineGear; v.Engine.Rpm = Rpm; v.Engine.Speedo = Speedo; v.Engine.Force = Force;
        int k = 0;
        foreach (var w in AllWheels(v))
        {
            if (k >= Wheels.Length) break;
            var s = Wheels[k++];
            (w.Travel, w.Ground, w.PrevTarget, w.PrevRate, w.AtLimit, w.Blown, w.Y1, w.Y2) = (s.Travel, s.Ground, s.PrevTarget, s.PrevRate, s.AtLimit, s.Blown, s.Y1, s.Y2);
        }
        // the step sets the front pair's angle from the steer before it reads it; the drawn wheels want it now
        v.FrontRight.SteerAngle = v.FrontLeft.SteerAngle = Steer * -MathF.PI / 4;
        (Slow ?? slow)?.ApplyTo(v);
        if (IsHeli && v.Heli is { } h) (h.Thrust, h.Side, h.Height, h.Touched) = (HeliThrust, HeliSide, HeliHeight, HeliTouched);
        if (d is not null)
        {
            (d.State.HoldUp, d.State.HoldDown, d.State.HoldRight, d.State.HoldLeft) = (HoldUp, HoldDown, HoldRight, HoldLeft);
            d.Generation = KeysStale ? v.NetGeneration - 1 : v.NetGeneration;
        }
    }

    public void Write(NetWriter w)
    {
        w.Vec(Rot.Right); w.Vec(Rot.Up); w.Vec(Rot.Forward);
        w.F64(Pos.X); w.F64(Pos.Y); w.F64(Pos.Z);
        w.Vec(Vel); w.Vec(AngVel); w.Vec(Accel);
        w.F32(Speed); w.F32(Steer); w.F32(Throttle); w.F32(Gear);
        w.U8((byte)((Handbrake != 0 ? 1 : 0) | (Unk_0xf4 != 0 ? 2 : 0) | (StartEngineKey != 0 ? 4 : 0) | (Wreck ? 8 : 0) | (KeysStale ? 16 : 0) | (Slow is not null ? 32 : 0) | (IsHeli ? 64 : 0)));
        w.U8((byte)Math.Clamp(DriveState, 0, 255));
        w.U32((uint)Flags);
        w.VarI(Surface);
        w.Vec(GroundNormal);
        w.F32(NormalLoad); w.F32(HeightAboveGround); w.F32(GroundHeight); w.F32(Timer);
        w.VarU((uint)Generation);
        w.U8(RecKind);
        if (RecKind != 0)
        {
            w.VarU((uint)RecSerial);
            w.F32(Rec.OtherMass); w.Vec(Rec.OtherVel); w.F32(Rec.T); w.Vec(Rec.C); w.Vec(Rec.RelVel); w.Vec(Rec.N);
            w.VarI(Rec.Node ?? -1);
        }
        w.U8((byte)EngineGear);
        w.F32(Rpm); w.F32(Speedo); w.F32(Force);
        w.U8((byte)Wheels.Length);
        foreach (var x in Wheels)
        {
            w.F32(x.Travel); w.F32(x.Ground); w.F32(x.PrevTarget); w.F32(x.PrevRate);
            w.U8((byte)((x.AtLimit ? 1 : 0) | (x.Blown ? 2 : 0)));
            w.F64(x.Y1); w.F64(x.Y2);
        }
        w.F32(HoldUp); w.F32(HoldDown); w.F32(HoldRight); w.F32(HoldLeft);
        if (IsHeli) { w.F32(HeliThrust); w.F32(HeliSide); w.F32(HeliHeight); w.U8((byte)(HeliTouched ? 1 : 0)); }
        Slow?.Write(w);
    }

    public static OwnMotion Read(ref NetReader r)
    {
        var m = new OwnMotion
        {
            Rot = new Rows(r.Vec(), r.Vec(), r.Vec()), Pos = new Double3(r.F64(), r.F64(), r.F64()),
            Vel = r.Vec(), AngVel = r.Vec(), Accel = r.Vec(), Speed = r.F32(), Steer = r.F32(), Throttle = r.F32(), Gear = r.F32(),
        };
        byte bits = r.U8();
        (m.Handbrake, m.Unk_0xf4, m.StartEngineKey) = ((bits & 1) != 0 ? 1 : 0, (bits & 2) != 0 ? 1 : 0, (bits & 4) != 0 ? 1 : 0);
        (m.Wreck, m.KeysStale) = ((bits & 8) != 0, (bits & 16) != 0);
        m.DriveState = r.U8();
        m.Flags = (VehicleFlags)r.U32();
        m.Surface = r.VarI();
        m.GroundNormal = r.Vec();
        (m.NormalLoad, m.HeightAboveGround, m.GroundHeight, m.Timer) = (r.F32(), r.F32(), r.F32(), r.F32());
        m.Generation = (int)r.VarU();
        m.RecKind = r.U8();
        if (m.RecKind > 3) throw new WireException($"contact kind {m.RecKind}");
        if (m.RecKind != 0)
        {
            m.RecSerial = (int)r.VarU();
            m.Rec = new Contact { Hit = true, OtherMass = r.F32(), OtherVel = r.Vec(), T = r.F32(), C = r.Vec(), RelVel = r.Vec(), N = r.Vec() };
            int node = r.VarI();
            m.Rec.Node = node < 0 ? null : node;
        }
        m.EngineGear = r.U8();
        (m.Rpm, m.Speedo, m.Force) = (r.F32(), r.F32(), r.F32());
        int n = r.U8();
        if (n > 6) throw new WireException($"{n} wheels");
        m.Wheels = new WheelMotion[n];
        for (int i = 0; i < n; i++)
        {
            var x = new WheelMotion { Travel = r.F32(), Ground = r.F32(), PrevTarget = r.F32(), PrevRate = r.F32() };
            byte f = r.U8();
            (x.AtLimit, x.Blown) = ((f & 1) != 0, (f & 2) != 0);
            (x.Y1, x.Y2) = (r.F64(), r.F64());
            m.Wheels[i] = x;
        }
        (m.HoldUp, m.HoldDown, m.HoldRight, m.HoldLeft) = (r.F32(), r.F32(), r.F32(), r.F32());
        if ((bits & 64) != 0) (m.IsHeli, m.HeliThrust, m.HeliSide, m.HeliHeight, m.HeliTouched) = (true, r.F32(), r.F32(), r.F32(), (r.U8() & 1) != 0);
        if ((bits & 32) != 0) m.Slow = SlowMotion.Read(ref r);
        return m;
    }

    /// <summary>The encoding without the slow part: two states are the same car exactly when these are equal.</summary>
    public byte[] Key()
    {
        var slow = Slow;
        Slow = null;
        var w = new NetWriter(512);
        Write(w);
        Slow = slow;
        return w.ToArray();
    }

    /// <summary>
    /// A state's hash (FNV-1a over its <see cref="Key"/>), never 0: the client sends it for each predicted tick and the
    /// server confirms the motion by it rather than sending it (protocol 13).
    /// </summary>
    public static uint Hash(ReadOnlySpan<byte> key)
    {
        uint h = 2166136261;
        foreach (var b in key) h = (h ^ b) * 16777619;
        return h == 0 ? 1 : h;
    }

    /// <summary>How far apart two states put the car: metres between the positions.</summary>
    public double Distance(OwnMotion o)
    {
        double dx = Pos.X - o.Pos.X, dy = Pos.Y - o.Pos.Y, dz = Pos.Z - o.Pos.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
