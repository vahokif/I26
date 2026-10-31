using I76.Sim.Damage;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Save;

/// <summary>
/// A car's state: the body, its parameters (which damage lowers), engine, wheels, arms, armoury and every
/// weapon, the damage state and its parts, a helicopter's or an emplacement's block. The objects a car is made
/// of (and how many weapons, which damage parts) come from its files: a restore writes into the rebuilt car's
/// own objects, and refuses a car whose make-up differs.
/// </summary>
public static class VehicleSave
{
    internal static void AuditFields(SaveAudit a)
    {
        const string files = "built from the car's files";
        a.For<Vehicle>()
            .Saved(nameof(Vehicle.P), nameof(Vehicle.Engine), nameof(Vehicle.FrontRight), nameof(Vehicle.FrontLeft), nameof(Vehicle.RearRight), nameof(Vehicle.RearLeft), nameof(Vehicle.MiddleRight), nameof(Vehicle.MiddleLeft),
                nameof(Vehicle.Rot), nameof(Vehicle.Pos), nameof(Vehicle.Vel), nameof(Vehicle.AngVel), nameof(Vehicle.Speed), nameof(Vehicle.Accel),
                nameof(Vehicle.Steer), nameof(Vehicle.Throttle), nameof(Vehicle.Gear), nameof(Vehicle.Handbrake), nameof(Vehicle.Unk_0xf4), nameof(Vehicle.StartEngineKey),
                nameof(Vehicle.DriveState), nameof(Vehicle.Flags), nameof(Vehicle.Surface), nameof(Vehicle.GroundNormal), nameof(Vehicle.NormalLoad),
                nameof(Vehicle.HeightAboveGround), nameof(Vehicle.GroundHeight), nameof(Vehicle.Rec), nameof(Vehicle.Armory), nameof(Vehicle.Health),
                nameof(Vehicle.Timer), nameof(Vehicle.Wreck), nameof(Vehicle.Arms), nameof(Vehicle.NetId), nameof(Vehicle.NetGeneration),
                nameof(Vehicle.Heli), nameof(Vehicle.Emplacement), nameof(Vehicle.Paint))
            .Structural(files, nameof(Vehicle.EngineNumber), nameof(Vehicle.Ignition), nameof(Vehicle.BoxX), nameof(Vehicle.BoxY), nameof(Vehicle.BoxZ),
                nameof(Vehicle.Build), nameof(Vehicle.Loadout), nameof(Vehicle.ObjectClass), nameof(Vehicle.Definition));
        a.For<VehicleParams>().Saved(nameof(VehicleParams.Mass), nameof(VehicleParams.DriveScale), nameof(VehicleParams.SteerGain), nameof(VehicleParams.PivotForward),
            nameof(VehicleParams.Drag), nameof(VehicleParams.EngineScale), nameof(VehicleParams.YawRateLimit), nameof(VehicleParams.RideOffset),
            nameof(VehicleParams.ChassisGrip), nameof(VehicleParams.BrakeStrength), nameof(VehicleParams.Wheelbase), nameof(VehicleParams.Track));
        a.For<Engine>().Saved(nameof(Engine.Gear), nameof(Engine.Curve), nameof(Engine.Force), nameof(Engine.PeakForce), nameof(Engine.Damage), nameof(Engine.Rpm), nameof(Engine.Speedo));
        a.For<Wheel>()
            .Saved(nameof(Wheel.Grip), nameof(Wheel.Rolling), nameof(Wheel.SteerAngle), nameof(Wheel.Blown), nameof(Wheel.Spin), nameof(Wheel.Travel), nameof(Wheel.Rest),
                nameof(Wheel.Ground), nameof(Wheel.PrevTarget), nameof(Wheel.PrevRate), nameof(Wheel.AtLimit), nameof(Wheel.Marking), nameof(Wheel.Y1), nameof(Wheel.Y2))
            .Structural("the wheel's mount and its suspension filter, from the car's files (InitSuspension)",
                nameof(Wheel.Offset), nameof(Wheel.TravelMax), nameof(Wheel.TravelMin), nameof(Wheel.A1), nameof(Wheel.A2), nameof(Wheel.B0));
        a.For<Contact>().Saved(nameof(Contact.Hit), nameof(Contact.Other), nameof(Contact.OtherMass), nameof(Contact.OtherVel), nameof(Contact.T), nameof(Contact.C),
            nameof(Contact.RelVel), nameof(Contact.N), nameof(Contact.Node));
        a.For<CrewArms>().Saved(nameof(CrewArms.Right), nameof(CrewArms.Left), nameof(CrewArms.Clock));
        a.For<CrewArm>().Saved(nameof(CrewArm.Busy), nameof(CrewArm.Cur), nameof(CrewArm.Tgt), nameof(CrewArm.Axis), nameof(CrewArm.Next));
        a.For<HeliState>().Saved(nameof(HeliState.Hp), nameof(HeliState.MaxHp), nameof(HeliState.InvMax), nameof(HeliState.Thrust), nameof(HeliState.Side),
            nameof(HeliState.Height), nameof(HeliState.Touched));
        a.For<EmplacementState>()
            .Saved(nameof(EmplacementState.Hp), nameof(EmplacementState.MaxHp), nameof(EmplacementState.InvMax), nameof(EmplacementState.Enabled), nameof(EmplacementState.Rate))
            .Structural("its placement's collider, linked by the mission build", nameof(EmplacementState.Collider));
        a.For<Armory>()
            // which weapons there are comes from the car's files (the restore checks the count); their state is saved
            .Saved(nameof(Armory.Selected), nameof(Armory.Group), nameof(Armory.Link), nameof(Armory.Weapons), nameof(Armory.LoadOrder), nameof(Armory.Handguns));
        a.For<Weapon>()
            .Saved(nameof(Weapon.AimYaw), nameof(Weapon.AimPitch), nameof(Weapon.GunPitch), nameof(Weapon.GunPitchWant), nameof(Weapon.Hp), nameof(Weapon.MaxHp),
                nameof(Weapon.Damage), nameof(Weapon.Ammo), nameof(Weapon.WaitA), nameof(Weapon.WaitB), nameof(Weapon.BurstLeft), nameof(Weapon.Trigger),
                nameof(Weapon.WasFiring), nameof(Weapon.LockTime), nameof(Weapon.OnTarget))
            .Structural(files, nameof(Weapon.Def), nameof(Weapon.Hardpoint), nameof(Weapon.Style), nameof(Weapon.MountKind), nameof(Weapon.RestMuzzle), nameof(Weapon.BaseRot), nameof(Weapon.TurretPivot));
        a.For<VehicleDamage>()
            .Saved(nameof(VehicleDamage.Armour), nameof(VehicleDamage.Chassis), nameof(VehicleDamage.ArmourMax), nameof(VehicleDamage.ChassisMax),
                nameof(VehicleDamage.ArmourMirror), nameof(VehicleDamage.ChassisMirror), nameof(VehicleDamage.Slots), nameof(VehicleDamage.RoughCarry),
                // the pick table: the specials' rows are drawn from rand() when the car is built, so a rebuild with another seed differs
                nameof(VehicleDamage.Rows))
            .Structural("its car, and whether it was built with the player's rules", nameof(VehicleDamage.V), nameof(VehicleDamage.IsPlayer), nameof(VehicleDamage.Network));
        a.For<Part>().Saved(nameof(Part.Detached));
        a.For<WheelPart>().Saved(nameof(WheelPart.Hp), nameof(WheelPart.Max), nameof(WheelPart.GripNominal), nameof(WheelPart.RollingNominal))
            .Structural("the car's own wheel", nameof(WheelPart.Wheel));
        a.For<ChassisPart>().Saved(nameof(ChassisPart.Hp), nameof(ChassisPart.Max));
        a.For<EnginePart>().Saved(nameof(EnginePart.Hp), nameof(EnginePart.Max)).Structural("the car's engine and its floor by the player's rules", "e", "floor");
        a.For<BrakePart>().Saved(nameof(BrakePart.Hp), nameof(BrakePart.Max), nameof(BrakePart.Nominal)).Structural("the car's parameters", "P");
        a.For<SuspensionPart>().Saved(nameof(SuspensionPart.Hp), nameof(SuspensionPart.Max), nameof(SuspensionPart.Nominal), nameof(SuspensionPart.ImpactScale),
            nameof(SuspensionPart.Unk_0x0c), nameof(SuspensionPart.Unk_0x10)).Structural("the car's parameters", "P");
        a.For<SpecialPart>().Saved(nameof(SpecialPart.Hp), nameof(SpecialPart.Max), nameof(SpecialPart.Charges), nameof(SpecialPart.Unk_0x10), nameof(SpecialPart.OnSince))
            .Structural("the special's type, from the car's files", nameof(SpecialPart.Type));
        a.For<WeaponPart>().Structural("the weapon it stands for (whose hit points it is)", nameof(WeaponPart.Weapon));
    }

    // ---- the make-up, checked against the rebuilt car ----

    private static byte KindOf(Part? p) => p switch
    {
        null => 0, WheelPart => 1, ChassisPart => 2, EnginePart => 3, BrakePart => 4, SuspensionPart => 5, SpecialPart => 6, WeaponPart => 7,
        _ => throw new InvalidOperationException($"snapshot: no kind for a {p.GetType().Name}"),
    };

    /// <summary>What the car is made of, as the restore checks it: its class, weapons, handguns, damage parts, helicopter and emplacement blocks.</summary>
    internal static void WriteShape(SaveWriter w, Vehicle v)
    {
        w.U32(v.ObjectClass);
        w.Int(v.Armory is { } a ? a.LoadOrder.Count : -1);
        if (v.Armory is { } aa) { w.Bool(aa.Handguns[0] is not null); w.Bool(aa.Handguns[1] is not null); }
        w.Bool(v.Health is not null);
        if (v.Health is { } h)
            foreach (var p in h.Slots) { w.U8(KindOf(p)); if (p is SpecialPart sp) w.Int(sp.Type); }
        w.Bool(v.Heli is not null);
        w.Bool(v.Emplacement is not null);
    }

    internal static byte[] Shape(Vehicle v)
    {
        var w = new SaveWriter(new SaveRefs());
        WriteShape(w, v);
        return w.ToArray();
    }

    internal static byte[] ReadShape(SaveReader r)
    {
        var w = new SaveWriter(new SaveRefs());
        w.U32(r.U32());
        int n = r.Int();
        w.Int(n);
        if (n >= 0) { w.Bool(r.Bool()); w.Bool(r.Bool()); }
        bool health = r.Bool();
        w.Bool(health);
        if (health)
            for (int i = 0; i < 20; i++) { byte k = r.U8(); w.U8(k); if (k == 6) w.Int(r.Int()); }
        w.Bool(r.Bool());
        w.Bool(r.Bool());
        return w.ToArray();
    }

    // ---- the state ----

    internal static void Write(SaveWriter w, Vehicle v)
    {
        w.Rows(v.Rot); w.D3(v.Pos); w.V3(v.Vel); w.V3(v.AngVel); w.F32(v.Speed); w.V3(v.Accel);
        w.F32(v.Steer); w.F32(v.Throttle); w.F32(v.Gear); w.Int(v.Handbrake); w.Int(v.Unk_0xf4); w.Int(v.StartEngineKey); w.Int(v.DriveState);
        w.U32((uint)v.Flags); w.Int(v.Surface); w.V3(v.GroundNormal); w.F32(v.NormalLoad); w.F32(v.HeightAboveGround); w.F32(v.GroundHeight);
        WriteContact(w, v.Rec);
        w.F32(v.Timer); w.Bool(v.Wreck); w.Int(v.NetGeneration);
        var p = v.P;
        w.F32(p.Mass); w.F32(p.DriveScale); w.F32(p.SteerGain); w.F32(p.PivotForward); w.F32(p.Drag); w.F32(p.EngineScale);
        w.F32(p.YawRateLimit); w.F32(p.RideOffset); w.F32(p.ChassisGrip); w.F32(p.BrakeStrength); w.F32(p.Wheelbase); w.F32(p.Track);
        var e = v.Engine;
        w.Int(e.Gear); w.F32(e.Curve); w.F32(e.Force); w.F32(e.PeakForce); w.F32(e.Damage); w.F32(e.Rpm); w.F32(e.Speedo);
        foreach (var wh in v.Wheels.Concat(v.MiddleWheels))
        {
            w.F32(wh.Grip); w.F32(wh.Rolling); w.F32(wh.SteerAngle); w.Bool(wh.Blown); w.F32(wh.Spin); w.F32(wh.Travel); w.F32(wh.Rest);
            w.F32(wh.Ground); w.F32(wh.PrevTarget); w.F32(wh.PrevRate); w.Bool(wh.AtLimit); w.Bool(wh.Marking); w.F64(wh.Y1); w.F64(wh.Y2);
        }
        foreach (var arm in (CrewArm[])[v.Arms.Right, v.Arms.Left]) { w.Bool(arm.Busy); w.Int(arm.Cur); w.Int(arm.Tgt); w.Int(arm.Axis); w.F64(arm.Next); }
        w.F64(v.Arms.Clock);
        if (v.Armory is { } a)
        {
            w.Int(a.Selected); w.Bool(a.Link); w.Ints(a.Group);
            foreach (var wp in a.LoadOrder) WriteWeapon(w, wp);
            foreach (var hg in a.Handguns) if (hg is not null) WriteWeapon(w, hg);
        }
        if (v.Health is { } h)
        {
            foreach (var pool in (int[][])[h.Armour, h.Chassis, h.ArmourMax, h.ChassisMax, h.ArmourMirror, h.ChassisMirror]) foreach (int x in pool) w.Int(x);
            w.F32(h.RoughCarry);
            foreach (var row in h.Rows) w.Ints(row);
            foreach (var part in h.Slots) if (part is not null) WritePart(w, part);
        }
        if (v.Heli is { } hs) { w.Int(hs.Hp); w.Int(hs.MaxHp); w.F32(hs.InvMax); w.F32(hs.Thrust); w.F32(hs.Side); w.F32(hs.Height); w.Bool(hs.Touched); }
        if (v.Emplacement is { } g) { w.Int(g.Hp); w.Int(g.MaxHp); w.F32(g.InvMax); w.Int(g.Enabled); w.Int(g.Rate); }
    }

    internal static void Read(SaveReader r, Vehicle v)
    {
        v.Rot = r.Rows(); v.Pos = r.D3(); v.Vel = r.V3(); v.AngVel = r.V3(); v.Speed = r.F32(); v.Accel = r.V3();
        v.Steer = r.F32(); v.Throttle = r.F32(); v.Gear = r.F32(); v.Handbrake = r.Int(); v.Unk_0xf4 = r.Int(); v.StartEngineKey = r.Int(); v.DriveState = r.Int();
        v.Flags = (VehicleFlags)r.U32(); v.Surface = r.Int(); v.GroundNormal = r.V3(); v.NormalLoad = r.F32(); v.HeightAboveGround = r.F32(); v.GroundHeight = r.F32();
        v.Rec = ReadContact(r);
        v.Timer = r.F32(); v.Wreck = r.Bool(); v.NetGeneration = r.Int();
        var p = v.P;
        p.Mass = r.F32(); p.DriveScale = r.F32(); p.SteerGain = r.F32(); p.PivotForward = r.F32(); p.Drag = r.F32(); p.EngineScale = r.F32();
        p.YawRateLimit = r.F32(); p.RideOffset = r.F32(); p.ChassisGrip = r.F32(); p.BrakeStrength = r.F32(); p.Wheelbase = r.F32(); p.Track = r.F32();
        var e = v.Engine;
        e.Gear = r.Int(); e.Curve = r.F32(); e.Force = r.F32(); e.PeakForce = r.F32(); e.Damage = r.F32(); e.Rpm = r.F32(); e.Speedo = r.F32();
        foreach (var wh in v.Wheels.Concat(v.MiddleWheels))
        {
            wh.Grip = r.F32(); wh.Rolling = r.F32(); wh.SteerAngle = r.F32(); wh.Blown = r.Bool(); wh.Spin = r.F32(); wh.Travel = r.F32(); wh.Rest = r.F32();
            wh.Ground = r.F32(); wh.PrevTarget = r.F32(); wh.PrevRate = r.F32(); wh.AtLimit = r.Bool(); wh.Marking = r.Bool(); wh.Y1 = r.F64(); wh.Y2 = r.F64();
        }
        foreach (var arm in (CrewArm[])[v.Arms.Right, v.Arms.Left]) { arm.Busy = r.Bool(); arm.Cur = r.Int(); arm.Tgt = r.Int(); arm.Axis = r.Int(); arm.Next = r.F64(); }
        v.Arms.Clock = r.F64();
        if (v.Armory is { } a)
        {
            a.Selected = r.Int(); a.Link = r.Bool();
            a.Group.Clear(); a.Group.AddRange(r.Ints());
            foreach (var wp in a.LoadOrder) ReadWeapon(r, wp);
            foreach (var hg in a.Handguns) if (hg is not null) ReadWeapon(r, hg);
        }
        if (v.Health is { } h)
        {
            foreach (var pool in (int[][])[h.Armour, h.Chassis, h.ArmourMax, h.ChassisMax, h.ArmourMirror, h.ChassisMirror])
                for (int i = 0; i < pool.Length; i++) pool[i] = r.Int();
            h.RoughCarry = r.F32();
            foreach (var row in h.Rows) { row.Clear(); row.AddRange(r.Ints()); }
            foreach (var part in h.Slots) if (part is not null) ReadPart(r, part);
        }
        if (v.Heli is { } hs) { hs.Hp = r.Int(); hs.MaxHp = r.Int(); hs.InvMax = r.F32(); hs.Thrust = r.F32(); hs.Side = r.F32(); hs.Height = r.F32(); hs.Touched = r.Bool(); }
        if (v.Emplacement is { } g) { g.Hp = r.Int(); g.MaxHp = r.Int(); g.InvMax = r.F32(); g.Enabled = r.Int(); g.Rate = r.Int(); }
    }

    internal static void WriteContact(SaveWriter w, Contact c)
    {
        w.Bool(c.Hit); w.Obj(c.Other); w.F32(c.OtherMass); w.V3(c.OtherVel); w.F32(c.T); w.V3(c.C); w.V3(c.RelVel); w.V3(c.N); w.NInt(c.Node);
    }

    internal static Contact ReadContact(SaveReader r) => new()
    {
        Hit = r.Bool(), Other = r.Obj(), OtherMass = r.F32(), OtherVel = r.V3(), T = r.F32(), C = r.V3(), RelVel = r.V3(), N = r.V3(), Node = r.NInt(),
    };

    private static void WriteWeapon(SaveWriter w, Weapon x)
    {
        w.F32(x.AimYaw); w.F32(x.AimPitch); w.F32(x.GunPitch); w.F32(x.GunPitchWant); w.Int(x.Hp); w.Int(x.MaxHp); w.Int(x.Damage); w.Int(x.Ammo);
        w.F32(x.WaitA); w.F32(x.WaitB); w.Int(x.BurstLeft); w.Bool(x.Trigger); w.Bool(x.WasFiring); w.F32(x.LockTime); w.Bool(x.OnTarget);
    }

    private static void ReadWeapon(SaveReader r, Weapon x)
    {
        x.AimYaw = r.F32(); x.AimPitch = r.F32(); x.GunPitch = r.F32(); x.GunPitchWant = r.F32(); x.Hp = r.Int(); x.MaxHp = r.Int(); x.Damage = r.Int(); x.Ammo = r.Int();
        x.WaitA = r.F32(); x.WaitB = r.F32(); x.BurstLeft = r.Int(); x.Trigger = r.Bool(); x.WasFiring = r.Bool(); x.LockTime = r.F32(); x.OnTarget = r.Bool();
    }

    private static void WritePart(SaveWriter w, Part p)
    {
        w.Bool(p.Detached);
        if (p is WeaponPart) return;   // its hit points are the weapon's
        w.Int(p.Hp); w.Int(p.Max);
        switch (p)
        {
            case WheelPart wp: w.F32(wp.GripNominal); w.F32(wp.RollingNominal); break;
            case BrakePart bp: w.F32(bp.Nominal); break;
            case SuspensionPart sp: w.F32(sp.Nominal); w.F32(sp.ImpactScale); w.F32(sp.Unk_0x0c); w.F32(sp.Unk_0x10); break;
            case SpecialPart xp: w.Int(xp.Charges); w.Int(xp.Unk_0x10); w.F32(xp.OnSince); break;
        }
    }

    private static void ReadPart(SaveReader r, Part p)
    {
        p.Detached = r.Bool();
        if (p is WeaponPart) return;
        p.Hp = r.Int(); p.Max = r.Int();
        switch (p)
        {
            case WheelPart wp: wp.GripNominal = r.F32(); wp.RollingNominal = r.F32(); break;
            case BrakePart bp: bp.Nominal = r.F32(); break;
            case SuspensionPart sp: sp.Nominal = r.F32(); sp.ImpactScale = r.F32(); sp.Unk_0x0c = r.F32(); sp.Unk_0x10 = r.F32(); break;
            case SpecialPart xp: xp.Charges = r.Int(); xp.Unk_0x10 = r.Int(); xp.OnSince = r.F32(); break;
        }
    }
}
