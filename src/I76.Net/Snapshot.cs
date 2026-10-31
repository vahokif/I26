using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Net;

/// <summary>
/// What every client sees of a car, each tick: its id and generation, flags,
/// pose and motion, the controls (for the lamps), the engine (for its sound
/// and the gauges), the wheels and suspension, the damage zones (the lamps,
/// the body's looks, the target box's health bar), the engine, suspension and
/// brake health (the bar too) and its turrets' aim. About 78 bytes.
/// </summary>
public sealed class CarState
{
    public int Serial;
    /// <summary>
    /// The server tick this state is of (not on the wire: the snapshot's own tick when the snapshot carried the car).
    /// A snapshot that did not carry a car this tick (interest management: a far car goes out less often) lists it
    /// with the last state the client has, of an earlier tick.
    /// </summary>
    public uint SampleTick;
    /// <summary>The low byte of <see cref="Vehicle.NetGeneration"/>: a change means the car jumped (a respawn, a regen).</summary>
    public byte Generation;
    public VehicleFlags Flags;
    public Double3 Pos;
    public Rows Rot;
    public Vector3 Vel, AngVel;
    public float Steer, Throttle, Rpm, Speed;
    public int EngineGear;
    /// <summary>The gear selector (P R N D 2 1 as 0..5, <see cref="Vehicle.DriveState"/>): the dash's PRNDL strip.</summary>
    public int DriveState = 3;
    public bool Reverse, Handbrake, Wreck;
    public readonly float[] Travel = new float[4];
    public float SteerFront0, SteerFront1;
    public byte Blown;
    /// <summary>
    /// Armour[0..3], ArmourMirror[4] (the top), then Chassis[0..3],
    /// ChassisMirror[4]: what the zones' looks, the lamps and the target box's
    /// health bar read. A side's pool and its mirror only ever change together
    /// (VehicleDamage.ArmourStep), so the client writes both from one number.
    /// </summary>
    public readonly int[] Zones = new int[10];
    /// <summary>Engine, suspension and brake health as Hp/Max in 0–255 (<see cref="QPart"/>): the target box's bar takes the weakest.</summary>
    public readonly byte[] Parts = [255, 255, 255];
    public readonly List<(float Yaw, float Pitch)> Turrets = [];

    private const float TravelRange = 2f, SteerRange = 1.6f;

    public static CarState Capture(Vehicle v)
    {
        var s = new CarState
        {
            Serial = v.NetId & 0xffffff, Generation = (byte)v.NetGeneration, Flags = v.Flags, Pos = v.Pos, Rot = v.Rot,
            Vel = v.Vel, AngVel = v.AngVel, Steer = v.Steer, Throttle = v.Throttle, Rpm = v.Engine.Rpm, Speed = v.Speed,
            EngineGear = v.Engine.Gear, DriveState = v.DriveState, Reverse = v.Gear < 0, Handbrake = v.Handbrake != 0, Wreck = v.Wreck,
            SteerFront0 = v.FrontRight.SteerAngle, SteerFront1 = v.FrontLeft.SteerAngle,
        };
        int k = 0;
        foreach (var w in v.Wheels)
        {
            s.Travel[k] = w.Travel;
            if (w.Blown) s.Blown |= (byte)(1 << k);
            k++;
        }
        // the middle pair's tyres ride in bits 4 and 5 (they have no travel of their own)
        k = 4;
        foreach (var w in v.MiddleWheels) { if (w.Blown) s.Blown |= (byte)(1 << k); k++; }
        // a helicopter has one pool of hit points, in the first zone (its target box's bar reads it)
        if (v.Heli is { } hs) s.Zones[0] = hs.Hp;
        else if (v.Health is { } h)
        {
            for (int z = 0; z < 4; z++) { s.Zones[z] = h.Armour[z]; s.Zones[5 + z] = h.Chassis[z]; }
            s.Zones[4] = h.ArmourMirror[4]; s.Zones[9] = h.ChassisMirror[4];
            s.Parts[0] = QPart(h.Engine); s.Parts[1] = QPart(h.Suspension); s.Parts[2] = QPart(h.Brake);
        }
        if (v.Armory is { } a)
            foreach (var w in a.LoadOrder) if (w.Style == 3) s.Turrets.Add((w.AimYaw, w.AimPitch));
        return s;
    }

    /// <summary>A part's health as a byte: 0 only when it is gone, 255 only when it is whole (so the bands' ends survive), else in between.</summary>
    public static byte QPart(Part? p)
    {
        if (p is null || p.Hp >= p.Max) return 255;
        if (p.Hp <= 0 || p.Max <= 0) return 0;
        return (byte)Math.Clamp((int)MathF.Round(p.Hp * 255f / p.Max), 1, 254);
    }

    /// <summary>The hit points a <see cref="QPart"/> byte stands for, of a part with <paramref name="max"/>.</summary>
    public static int UnQPart(byte q, int max) =>
        q == 255 ? max : q == 0 || max <= 0 ? 0 : Math.Clamp((int)MathF.Round(q * max / 255f), 1, Math.Max(1, max - 1));

    /// <summary>Writes this state into a client's copy of the car (its pose too; the view interpolates it).</summary>
    public void ApplyTo(Vehicle v)
    {
        v.Flags = Flags;
        v.Pos = Pos; v.Rot = Rot; v.Vel = Vel; v.AngVel = AngVel;
        v.Steer = Steer; v.Throttle = Throttle; v.Speed = Speed;
        v.Engine.Rpm = Rpm; v.Engine.Gear = EngineGear;
        v.Gear = Reverse ? -MathF.Abs(v.Gear == 0 ? 1 : v.Gear) : MathF.Abs(v.Gear == 0 ? 1 : v.Gear);
        v.DriveState = DriveState;
        v.Handbrake = Handbrake ? 1 : 0;
        v.Wreck = Wreck;
        v.NetGeneration = (v.NetGeneration & ~0xff) | Generation;
        int k = 0;
        foreach (var w in v.Wheels)
        {
            w.Travel = Travel[k];
            w.Blown = (Blown & (1 << k)) != 0;
            k++;
        }
        k = 4;
        foreach (var w in v.MiddleWheels) { w.Blown = (Blown & (1 << k)) != 0; k++; }
        v.FrontRight.SteerAngle = SteerFront0;
        v.FrontLeft.SteerAngle = SteerFront1;
        if (v.Heli is { } hs) hs.Hp = Zones[0];
        else if (v.Health is { } h)
        {
            for (int z = 0; z < 5; z++) { h.ArmourMirror[z] = Zones[z]; h.ChassisMirror[z] = Zones[5 + z]; }
            for (int z = 0; z < 4; z++) { h.Armour[z] = Zones[z]; h.Chassis[z] = Zones[5 + z]; }
            Part?[] parts = [h.Engine, h.Suspension, h.Brake];
            for (int i = 0; i < 3; i++) if (parts[i] is { } p) p.Hp = UnQPart(Parts[i], p.Max);
        }
        if (v.Armory is { } a)
        {
            int t = 0;
            foreach (var w in a.LoadOrder)
                if (w.Style == 3 && t < Turrets.Count) (w.AimYaw, w.AimPitch) = Turrets[t++];
        }
    }

    // ---- on the wire (SnapshotCodec): the hot channels, quantized to ints and sent as changes, then the cold bytes ----

    /// <summary>The hot channels: position (3), rotation (4), velocity (3), angular velocity (3), driving (4), wheels (6).</summary>
    public const int HotCount = 23;
    /// <summary>The channel groups a delta marks as changed, in channel order: pos, rot, vel, angvel, driving, wheels.</summary>
    internal static readonly (int Start, int Count)[] Groups = [(0, 3), (3, 4), (7, 3), (10, 3), (13, 4), (17, 6)];
    internal const int PosChannel = 0, VelChannel = 7, DrivingGroup = 4, WheelGroup = 5;
    /// <summary>The quantization: position 1/1024 m, velocity 1/256 m/s, angular velocity 1/1024 rad/s, speed 1/128 m/s, travel and front steer 1/2048.</summary>
    internal const double PosScale = 1024, VelScale = 256, AngScale = 1024, SpeedScale = 128, WheelScale = 2048;

    private static int Q(double v, double scale, double limit) => (int)Math.Round(Math.Clamp(double.IsFinite(v) ? v : 0, -limit, limit) * scale);

    /// <summary>The hot fields as ints (the precision the client gets).</summary>
    public void Hot(Span<int> q)
    {
        q[0] = Q(Pos.X, PosScale, 2e6); q[1] = Q(Pos.Y, PosScale, 2e6); q[2] = Q(Pos.Z, PosScale, 2e6);
        ulong packed = Wire.PackQuat(Wire.ToQuat(Rot));
        q[3] = (int)(packed >> 45) & 3; q[4] = (int)(packed >> 30) & 0x7fff; q[5] = (int)(packed >> 15) & 0x7fff; q[6] = (int)packed & 0x7fff;
        q[7] = Q(Vel.X, VelScale, 4e4); q[8] = Q(Vel.Y, VelScale, 4e4); q[9] = Q(Vel.Z, VelScale, 4e4);
        q[10] = Q(AngVel.X, AngScale, 1e4); q[11] = Q(AngVel.Y, AngScale, 1e4); q[12] = Q(AngVel.Z, AngScale, 1e4);
        q[13] = Wire.Q8(Steer, 1); q[14] = Wire.Q8(Throttle, 1); q[15] = (int)Math.Clamp(float.IsFinite(Rpm) ? Rpm : 0, 0, 65535); q[16] = Q(Speed, SpeedScale, 4e4);
        for (int k = 0; k < 4; k++) q[17 + k] = Q(Travel[k], WheelScale, 100);
        q[21] = Q(SteerFront0, WheelScale, 100); q[22] = Q(SteerFront1, WheelScale, 100);
    }

    public void SetHot(ReadOnlySpan<int> q)
    {
        Pos = new Double3(q[0] / PosScale, q[1] / PosScale, q[2] / PosScale);
        ulong packed = (ulong)(q[3] & 3) << 45 | (ulong)(q[4] & 0x7fff) << 30 | (ulong)(q[5] & 0x7fff) << 15 | (ulong)(q[6] & 0x7fff);
        Rot = Wire.ToRows(Wire.UnpackQuat(packed));
        Vel = new Vector3((float)(q[7] / VelScale), (float)(q[8] / VelScale), (float)(q[9] / VelScale));
        AngVel = new Vector3((float)(q[10] / AngScale), (float)(q[11] / AngScale), (float)(q[12] / AngScale));
        Steer = Wire.UnQ8((sbyte)Math.Clamp(q[13], -127, 127), 1); Throttle = Wire.UnQ8((sbyte)Math.Clamp(q[14], -127, 127), 1);
        Rpm = q[15]; Speed = (float)(q[16] / SpeedScale);
        for (int k = 0; k < 4; k++) Travel[k] = (float)(q[17 + k] / WheelScale);
        SteerFront0 = (float)(q[21] / WheelScale); SteerFront1 = (float)(q[22] / WheelScale);
    }

    /// <summary>
    /// The cold fields (what changes seldom: generation, flags, gears, tyres, the damage zones, the parts, the turrets),
    /// sent as a byte delta (<see cref="XorDelta"/>) when they change. A new field of the car's goes here, at the end.
    /// </summary>
    public void WriteCold(NetWriter w)
    {
        w.U8(Generation);
        w.U32((uint)Flags);
        w.U8((byte)((EngineGear & 7) | (Reverse ? 8 : 0) | (Handbrake ? 16 : 0) | (Wreck ? 32 : 0)));
        w.U8((byte)Math.Clamp(DriveState, 0, 5));
        w.U8(Blown);
        foreach (var z in Zones) w.VarI(z);
        w.Bytes(Parts);
        w.U8((byte)Turrets.Count);
        foreach (var (yaw, pitch) in Turrets) { w.I16(Wire.Q16(Angle(yaw), MathF.PI)); w.I16(Wire.Q16(Angle(pitch), MathF.PI)); }
    }

    private static float Angle(float a) => MathF.IEEERemainder(a, 2 * MathF.PI);

    public void ReadCold(ref NetReader r)
    {
        Generation = r.U8();
        Flags = (VehicleFlags)r.U32();
        byte d = r.U8();
        EngineGear = d & 7; Reverse = (d & 8) != 0; Handbrake = (d & 16) != 0; Wreck = (d & 32) != 0;
        DriveState = Math.Min((int)r.U8(), 5);
        Blown = r.U8();
        for (int z = 0; z < 10; z++) Zones[z] = r.VarI();
        r.Bytes(3).CopyTo(Parts);
        int n = r.U8();
        Turrets.Clear();
        for (int t = 0; t < n; t++) Turrets.Add((Wire.UnQ16(r.I16(), MathF.PI), Wire.UnQ16(r.I16(), MathF.PI)));
    }

    /// <summary>The car as the client gets it: its channels and cold bytes (the same quantization a snapshot carries).</summary>
    public EntityRec Rec(uint tick)
    {
        var hot = new int[HotCount];
        Hot(hot);
        var w = new NetWriter(64);
        WriteCold(w);
        return new EntityRec(hot, w.ToArray(), tick);
    }

    /// <summary>A car's state from its channels and cold bytes.</summary>
    public static CarState FromRec(int serial, EntityRec rec)
    {
        var s = new CarState { Serial = serial, SampleTick = rec.SampleTick };
        s.SetHot(rec.Hot);
        var r = new NetReader(rec.Cold);
        if (rec.Cold.Length > 0) s.ReadCold(ref r);
        return s;
    }
}

/// <summary>
/// The recipient's own car, which only its driver needs in full: the radar
/// target, the speedometer, the armoury (selection, link group, each
/// weapon's ammo and hit points), every damage part's exact hit points and
/// the specials' charges, and the driver's arms. (The armour and chassis
/// pools are every car's, in <see cref="CarState.Zones"/>.)
/// </summary>
public sealed class OwnCar
{
    public int Target;
    public float Speedo;
    public sbyte Selected = -1;
    public bool Link;
    /// <summary>The radar's range (radar +0: true is 100 m) and its camera flag (+0xc bit 1): R and W, the sim's own state.</summary>
    public bool RadarShort, RadarCamera;
    /// <summary>Another car's 4Get-U-Not holds this one locked (<see cref="Combat.LockedOn"/>): the client plays msllock3.wav.</summary>
    public bool LockedOn;
    public List<byte> Group { get; } = [];
    public List<(int Ammo, int Hp)> Weapons { get; } = [];
    /// <summary>Each damage slot's hit points (−1 for an empty slot).</summary>
    public readonly int[] Parts = new int[20];
    /// <summary>Each special slot: the charges, and bit 7 for "on" (the jammer, which lights its plate).</summary>
    public readonly byte[] Charges = new byte[3];
    public readonly (sbyte Cur, sbyte Tgt, byte Axis, bool Busy)[] Arms = new (sbyte, sbyte, byte, bool)[2];
    /// <summary>
    /// Which build of the player's car this is (protocol 13): bumped when a reconfigured respawn rebuilds it as another
    /// car (<see cref="CarChangedEvent"/>). A client whose copy is of another build leaves the snapshot alone.
    /// </summary>
    public byte Epoch;

    public static OwnCar Capture(Vehicle v, Combat? combat)
    {
        var o = new OwnCar { Speedo = v.Engine.Speedo };
        if (combat?.RadarTarget.GetValueOrDefault(v) is { } t) o.Target = t.NetId & 0xffffff;
        if (combat is not null) { o.RadarShort = combat.RadarRange.GetValueOrDefault(v, 1) == 0; o.RadarCamera = combat.RadarCamera.Contains(v); o.LockedOn = combat.LockedOn.Contains(v); }
        if (v.Armory is { } a)
        {
            o.Selected = (sbyte)a.Selected;
            o.Link = a.Link;
            foreach (var g in a.Group) o.Group.Add((byte)g);
            foreach (var w in a.Weapons) o.Weapons.Add((w.Ammo, w.Hp));
        }
        if (v.Health is { } h)
        {
            for (int i = 0; i < 20; i++) o.Parts[i] = h.Slots[i]?.Hp ?? -1;
            for (int i = 0; i < 3; i++) o.Charges[i] = h.Slots[17 + i] is Sim.Damage.SpecialPart sp ? (byte)(Math.Clamp(sp.Charges, 0, 127) | (sp.Unk_0x10 != 0 ? 0x80 : 0)) : (byte)0;
        }
        else Array.Fill(o.Parts, -1);
        for (int ch = 1; ch <= 2; ch++)
        {
            var arm = v.Arms[ch];
            o.Arms[ch - 1] = ((sbyte)arm.Cur, (sbyte)arm.Tgt, (byte)arm.Axis, arm.Busy);
        }
        return o;
    }

    /// <summary>Writes the driver's view of the car into the client's copy; <paramref name="byId"/> finds the target.</summary>
    public void ApplyTo(Vehicle v, Combat? combat, Func<int, Vehicle?> byId)
    {
        v.Engine.Speedo = Speedo;
        if (combat is not null)
        {
            combat.RadarTarget[v] = Target == 0 ? null : byId(Target);
            combat.RadarRange[v] = RadarShort ? 0 : 1;
            if (RadarCamera) combat.RadarCamera.Add(v); else combat.RadarCamera.Remove(v);
            if (LockedOn) combat.LockedOn.Add(v); else combat.LockedOn.Remove(v);
        }
        if (v.Armory is { } a)
        {
            a.Selected = Selected;
            a.Link = Link;
            a.Group.Clear();
            foreach (var g in Group) a.Group.Add(g);
            for (int i = 0; i < a.Weapons.Count && i < Weapons.Count; i++) (a.Weapons[i].Ammo, a.Weapons[i].Hp) = Weapons[i];
        }
        if (v.Health is { } h)
        {
            for (int i = 0; i < 20; i++) if (h.Slots[i] is { } p && Parts[i] >= 0) p.Hp = Parts[i];
            for (int i = 0; i < 3; i++) if (h.Slots[17 + i] is Sim.Damage.SpecialPart sp) (sp.Charges, sp.Unk_0x10) = (Charges[i] & 0x7f, Charges[i] >> 7);
        }
        for (int ch = 1; ch <= 2; ch++)
        {
            var arm = v.Arms[ch];
            (sbyte cur, sbyte tgt, byte axis, bool busy) = Arms[ch - 1];
            (arm.Cur, arm.Tgt, arm.Axis, arm.Busy) = (cur, tgt, axis, busy);
        }
    }

    public void Write(NetWriter w)
    {
        w.VarU((uint)Target);
        w.Half(Speedo);
        w.I8(Selected);
        w.U8((byte)((Link ? 1 : 0) | (RadarShort ? 2 : 0) | (RadarCamera ? 4 : 0) | (LockedOn ? 8 : 0)));
        w.U8((byte)Group.Count);
        foreach (var g in Group) w.U8(g);
        w.U8((byte)Weapons.Count);
        foreach (var (ammo, hp) in Weapons) { w.VarI(ammo); w.VarI(hp); }
        uint mask = 0;
        for (int i = 0; i < 20; i++) if (Parts[i] >= 0) mask |= 1u << i;
        w.U32(mask);
        for (int i = 0; i < 20; i++) if (Parts[i] >= 0) w.VarI(Parts[i]);
        w.Bytes(Charges);
        foreach (var (cur, tgt, axis, busy) in Arms) { w.I8(cur); w.I8(tgt); w.U8((byte)(axis | (busy ? 0x80 : 0))); }
        w.U8(Epoch);
    }

    public static OwnCar Read(ref NetReader r)
    {
        var o = new OwnCar { Target = (int)r.VarU(), Speedo = r.Half(), Selected = r.I8() };
        byte bits = r.U8();
        (o.Link, o.RadarShort, o.RadarCamera, o.LockedOn) = ((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0);
        int g = r.U8();
        for (int i = 0; i < g; i++) o.Group.Add(r.U8());
        int n = r.U8();
        for (int i = 0; i < n; i++) o.Weapons.Add((r.VarI(), r.VarI()));
        uint mask = r.U32();
        for (int i = 0; i < 20; i++) o.Parts[i] = (mask & 1u << i) != 0 ? r.VarI() : -1;
        r.Bytes(3).CopyTo(o.Charges);
        for (int ch = 0; ch < 2; ch++)
        {
            sbyte cur = r.I8(), tgt = r.I8();
            byte ax = r.U8();
            o.Arms[ch] = (cur, tgt, (byte)(ax & 0x7f), (ax & 0x80) != 0);
        }
        o.Epoch = r.U8();
        return o;
    }
}

/// <summary>A round in flight: its id, whose and which weapon's it is (so the client knows its look), where it is and which way it points.</summary>
public sealed class RoundState
{
    public int Serial, Shooter;
    /// <summary>The shooter's weapon: its index in the armoury's sorted list, or 0x80 + k for handgun k.</summary>
    public byte Weapon;
    public Vector3 Pos;
    public Rows Rot;
    public float Travelled;
    public bool Settled;

    public const byte Handgun = 0x80;

    /// <summary>The weapon a round came from, as an index the client can look up in its own copy of the armoury; null if it is gone.</summary>
    public static byte? WeaponIndex(Round r)
    {
        if (r.Shooter.Armory is not { } a) return null;
        for (int i = 0; i < a.Weapons.Count; i++) if (a.Weapons[i].Def == r.Def) return (byte)i;
        for (int i = 0; i < a.Weapons.Count; i++) if (a.Weapons[i].Def.File.Equals(r.Def.File, StringComparison.OrdinalIgnoreCase)) return (byte)i;
        for (int k = 0; k < 2; k++) if (a.Handguns[k] is { } h && (h.Def == r.Def || r.Kind == 0x12)) return (byte)(Handgun + k);
        return null;
    }

    public static Weapon? WeaponOf(Vehicle shooter, byte index) => shooter.Armory is not { } a ? null
        : index >= Handgun ? a.Handguns.ElementAtOrDefault(index - Handgun) : a.Weapons.ElementAtOrDefault(index);

    /// <summary>The hot channels: distance travelled (1/4 m, the half float it was), position (3, 1/128 m), rotation (4).</summary>
    public const int HotCount = 8;
    internal static readonly (int Start, int Count)[] Groups = [(0, 1), (1, 3), (4, 4)];
    internal const double TravelScale = 4, PosScale = 128;

    public void Hot(Span<int> q)
    {
        q[0] = (int)Math.Round(Math.Clamp(float.IsFinite(Travelled) ? Travelled : 0, 0, 1e6) * TravelScale);
        for (int k = 0; k < 3; k++) q[1 + k] = (int)Math.Round(Math.Clamp(float.IsFinite(Pos[k]) ? Pos[k] : 0, -2e6, 2e6) * PosScale);
        ulong packed = Wire.PackQuat(Wire.ToQuat(Rot));
        q[4] = (int)(packed >> 45) & 3; q[5] = (int)(packed >> 30) & 0x7fff; q[6] = (int)(packed >> 15) & 0x7fff; q[7] = (int)packed & 0x7fff;
    }

    public void SetHot(ReadOnlySpan<int> q)
    {
        Travelled = (float)(q[0] / TravelScale);
        Pos = new Vector3((float)(q[1] / PosScale), (float)(q[2] / PosScale), (float)(q[3] / PosScale));
        Rot = Wire.ToRows(Wire.UnpackQuat((ulong)(q[4] & 3) << 45 | (ulong)(q[5] & 0x7fff) << 30 | (ulong)(q[6] & 0x7fff) << 15 | (ulong)(q[7] & 0x7fff)));
    }

    /// <summary>What a round never or seldom changes: its shooter and weapon, whether it has settled. A new field goes here, at the end.</summary>
    public void WriteCold(NetWriter w)
    {
        w.VarU((uint)Shooter);
        w.U8(Weapon);
        w.Bool(Settled);
    }

    public void ReadCold(ref NetReader r)
    {
        Shooter = (int)r.VarU();
        Weapon = r.U8();
        Settled = r.Bool();
    }

    public EntityRec Rec(uint tick)
    {
        var hot = new int[HotCount];
        Hot(hot);
        var w = new NetWriter(16);
        WriteCold(w);
        return new EntityRec(hot, w.ToArray(), tick);
    }

    public static RoundState FromRec(int serial, EntityRec rec)
    {
        var s = new RoundState { Serial = serial };
        s.SetHot(rec.Hot);
        var r = new NetReader(rec.Cold);
        if (rec.Cold.Length > 0) s.ReadCold(ref r);
        return s;
    }
}

/// <summary>
/// The recipient's own weapons' fire control at full precision (protocol 13), for the client's prediction of its own
/// shots (docs/multiplayer.md, "Own shots at once"): each weapon's timers, burst, lock and aims as the server had them
/// after the acked input, and how many shots it has fired (a count the server keeps per weapon, wrapping at 2^16), by
/// which the client finds the shots it drew that the server did not fire, or the other way round. The ammo and hit
/// points are in <see cref="OwnCar"/>.
/// </summary>
public sealed class OwnFire
{
    public sealed class Entry
    {
        /// <summary>The weapon (<see cref="RoundState.Weapon"/>'s numbering: the armoury's index, or 0x80 + handgun).</summary>
        public byte Weapon;
        public float WaitA, WaitB, LockTime, GunPitch, GunPitchWant, AimYaw, AimPitch;
        public int BurstLeft;
        public bool WasFiring, OnTarget;
        public ushort Shots;
    }

    public List<Entry> Weapons { get; } = [];

    public static OwnFire Capture(Vehicle v, Func<Weapon, ushort> shots)
    {
        var o = new OwnFire();
        if (v.Armory is not { } a) return o;
        void Add(Weapon w, byte index) => o.Weapons.Add(new Entry
        {
            Weapon = index, WaitA = w.WaitA, WaitB = w.WaitB, LockTime = w.LockTime, GunPitch = w.GunPitch, GunPitchWant = w.GunPitchWant,
            AimYaw = w.AimYaw, AimPitch = w.AimPitch, BurstLeft = w.BurstLeft, WasFiring = w.WasFiring, OnTarget = w.OnTarget, Shots = shots(w),
        });
        for (int i = 0; i < a.Weapons.Count; i++) Add(a.Weapons[i], (byte)i);
        for (int k = 0; k < 2; k++) if (a.Handguns[k] is { } h) Add(h, (byte)(RoundState.Handgun + k));
        return o;
    }

    /// <summary>Writes the fire control into the client's copy of its car.</summary>
    public void ApplyTo(Vehicle v)
    {
        foreach (var e in Weapons)
        {
            if (RoundState.WeaponOf(v, e.Weapon) is not { } w) continue;
            (w.WaitA, w.WaitB, w.LockTime, w.GunPitch, w.GunPitchWant, w.AimYaw, w.AimPitch) = (e.WaitA, e.WaitB, e.LockTime, e.GunPitch, e.GunPitchWant, e.AimYaw, e.AimPitch);
            (w.BurstLeft, w.WasFiring, w.OnTarget) = (e.BurstLeft, e.WasFiring, e.OnTarget);
            w.Trigger = false;
        }
    }

    // only what is not at rest goes: a weapon at rest is four bytes
    public void Write(NetWriter w)
    {
        w.U8((byte)Weapons.Count);
        foreach (var e in Weapons)
        {
            bool timers = e.WaitA != 0 || e.WaitB != 0 || e.BurstLeft != 0, aim = e.AimYaw != 0 || e.AimPitch != 0, gun = e.GunPitch != 0 || e.GunPitchWant != 0;
            w.U8(e.Weapon);
            w.U8((byte)((e.WasFiring ? 1 : 0) | (e.OnTarget ? 2 : 0) | (timers ? 4 : 0) | (aim ? 8 : 0) | (gun ? 16 : 0) | (e.LockTime != 0 ? 32 : 0)));
            w.U16(e.Shots);
            if (timers) { w.F32(e.WaitA); w.F32(e.WaitB); w.VarI(e.BurstLeft); }
            if (aim) { w.F32(e.AimYaw); w.F32(e.AimPitch); }
            if (gun) { w.F32(e.GunPitch); w.F32(e.GunPitchWant); }
            if (e.LockTime != 0) w.F32(e.LockTime);
        }
    }

    public static OwnFire Read(ref NetReader r)
    {
        var o = new OwnFire();
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            var e = new Entry { Weapon = r.U8() };
            byte f = r.U8();
            e.Shots = r.U16();
            (e.WasFiring, e.OnTarget) = ((f & 1) != 0, (f & 2) != 0);
            if ((f & 4) != 0) { e.WaitA = r.F32(); e.WaitB = r.F32(); e.BurstLeft = r.VarI(); }
            if ((f & 8) != 0) { e.AimYaw = r.F32(); e.AimPitch = r.F32(); }
            if ((f & 16) != 0) { e.GunPitch = r.F32(); e.GunPitchWant = r.F32(); }
            if ((f & 32) != 0) e.LockTime = r.F32();
            o.Weapons.Add(e);
        }
        return o;
    }
}

/// <summary>
/// A flame thrower's stream as the clients see it (protocol 13): whose and which weapon's (<see cref="RoundState.Weapon"/>'s
/// numbering) and how many cross-sections it has (2..19). The client builds the stream from the car as it draws it
/// (<see cref="Combat.BuildFlameView"/>); the flame's harm is the server's.
/// </summary>
public readonly record struct FlameState(int Owner, byte Weapon, byte Count)
{
    public void Write(NetWriter w) { w.VarU((uint)Owner); w.U8(Weapon); w.U8(Count); }
    public static FlameState Read(ref NetReader r) => new((int)r.VarU(), r.U8(), r.U8());
}

/// <summary>A CTF flag as the clients see it: whose it is (team 1–4), its state (0 inactive, 1 home, 2 dropped, 4 carried), where it is (the carrier's car when carried) and the carrier's car id (0 when none).</summary>
public readonly record struct FlagState(byte Team, byte State, Vector3 Pos, int Carrier)
{
    public void Write(NetWriter w) { w.U8(Team); w.U8(State); w.Vec(Pos); w.VarU((uint)Carrier); }
    public static FlagState Read(ref NetReader r) => new(r.U8(), r.U8(), r.Vec(), (int)r.VarU());
}

/// <summary>
/// One server tick as a client's views take it: the tick, the last of this client's input ticks the server has applied
/// (the ack), how many of its inputs were left waiting (protocol 13: the client's input clock keeps one in hand), the
/// client's own car in full, its motion at full precision for the prediction (protocol 13), then every car it knows
/// (each with the tick its state is of, <see cref="CarState.SampleTick"/>: a car the server did not send this tick
/// comes with an older state), every round in flight and the CTF flags. On the wire it is a delta against a snapshot the
/// client acknowledged (<see cref="SnapshotCodec"/>); <see cref="WriteHeader"/>, <see cref="WriteBody"/> and
/// <see cref="Read"/> are the full form, with no baseline.
/// </summary>
public sealed class Snapshot
{
    public uint Tick;
    public uint AckedInput;
    /// <summary>This client's inputs waiting on the server after the tick took one; −1 when it found none (it held the last).</summary>
    public sbyte Slack;
    public OwnCar? Own;
    public OwnMotion? Motion;
    /// <summary>
    /// The motion was confirmed rather than sent (protocol 13): the server's car is the one the client predicted for the
    /// acked input whose hash (<see cref="OwnMotion.Hash"/>) ends in these 16 bits.
    /// </summary>
    public ushort? MotionEcho;
    /// <summary>The motion's slow part, as the client has it after this snapshot (with a motion sent or confirmed).</summary>
    public OwnMotion.SlowMotion? Slow;
    /// <summary>The own weapons' fire control (protocol 13).</summary>
    public OwnFire? Fire;
    public List<CarState> Cars { get; } = [];
    public List<RoundState> Rounds { get; } = [];
    /// <summary>The CTF flags (none in other games).</summary>
    public List<FlagState> Flags { get; } = [];
    /// <summary>The flame throwers' streams (protocol 13).</summary>
    public List<FlameState> Flames { get; } = [];
    /// <summary>A mission's gates off their places (protocol 15).</summary>
    public List<GateState> Gates { get; } = [];
    /// <summary>A mission's script camera while it holds the view (protocol 15): a co-op cut-scene.</summary>
    public CameraState? Camera { get; set; }
    /// <summary>The seats' own camera scenes (protocol 19): a co-op player's moment of their own, shown to that player alone.</summary>
    public List<(byte Seat, CameraState Camera)> Personal { get; } = [];

    /// <summary>The own car's bytes as the codec sends them.</summary>
    public static byte[] Bytes(OwnCar own) { var w = new NetWriter(96); own.Write(w); return w.ToArray(); }
    /// <summary>The motion's fast part and its slow part (null when it has none).</summary>
    public static (byte[] Fast, byte[]? Slow) Bytes(OwnMotion m)
    {
        byte[]? slow = null;
        if (m.Slow is { } sl) { var w = new NetWriter(128); sl.Write(w); slow = w.ToArray(); }
        return (m.Key(), slow);
    }

    /// <summary>The own weapons' fire control's bytes as the codec sends them.</summary>
    public static byte[] Bytes(OwnFire fire) { var w = new NetWriter(64); fire.Write(w); return w.ToArray(); }

    /// <summary>A full snapshot's head (no baseline).</summary>
    public static void WriteHeader(NetWriter w, uint tick, uint acked, OwnCar? own, sbyte slack = 0, OwnMotion? motion = null, OwnFire? fire = null)
    {
        var (fast, slow) = motion is null ? (null, null) : Bytes(motion);
        SnapshotCodec.WriteHead(w, tick, acked, slack, null, own is null ? null : Bytes(own), fast, slow, new SnapFrame(), fire is null ? null : Bytes(fire));
    }

    /// <summary>A full snapshot's cars, rounds, flags and flames.</summary>
    public static void WriteBody(NetWriter w, IReadOnlyList<CarState> cars, IReadOnlyList<RoundState> rounds, IReadOnlyList<FlagState>? flags = null,
        IReadOnlyList<FlameState>? flames = null, IReadOnlyList<GateState>? gates = null, CameraState? camera = null, IReadOnlyList<(byte Seat, CameraState Camera)>? personal = null)
    {
        var f = new SnapFrame();
        SnapshotCodec.WriteCars(w, 0, 0, null, [.. cars.OrderBy(c => c.Serial).Select(c => (c.Serial, c.Rec(0)))], null, f);
        SnapshotCodec.WriteRounds(w, 0, 0, null, [.. rounds.OrderBy(r => r.Serial).Select(r => (r.Serial, r.Rec(0)))], null, f);
        SnapshotCodec.WriteTail(w, flags, flames, gates, camera, personal);
    }

    /// <summary>Reads a full snapshot (the type byte already read); one written against a baseline needs a <see cref="SnapshotDecoder"/>.</summary>
    public static Snapshot Read(ref NetReader r)
    {
        var d = SnapshotCodec.Read(ref r, 0, _ => null) ?? throw new WireException("a delta snapshot without its baseline");
        return SnapshotDecoder.View(d, _ => null);
    }
}
