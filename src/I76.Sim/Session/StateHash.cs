using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Vehicles;

namespace I76.Sim.Session;

/// <summary>
/// A 64-bit FNV-1a hash over what a session's outcome is made of (<see cref="Of"/>: its snapshot), and over single
/// values and cars for tests that hash part of it. Two runs that hash the same tick after tick ran the same game; a
/// replay or a server and its client compare these.
/// </summary>
public struct StateHash
{
    private ulong _h;

    public StateHash() => _h = 14695981039346656037UL;

    public readonly ulong Value => _h;

    public void Add(ulong x)
    {
        for (int i = 0; i < 8; i++) { _h ^= (x >> (8 * i)) & 0xff; _h *= 1099511628211UL; }
    }
    public void Add(int x) => Add((ulong)(uint)x);
    public void Add(bool x) => Add(x ? 1UL : 0UL);
    public void Add(float x) => Add((ulong)BitConverter.SingleToUInt32Bits(x));
    public void Add(double x) => Add((ulong)BitConverter.DoubleToInt64Bits(x));
    public void Add(Vector3 v) { Add(v.X); Add(v.Y); Add(v.Z); }

    /// <summary>The id of a car, round or structure, or of nothing.</summary>
    private void AddRef(object? o) => Add(o switch { Vehicle v => v.NetId, Weapons.Round r => r.NetId, StaticObject s => s.NetId, null => 0, _ => -1 });

    /// <summary>One car's simulation state (what <see cref="CarSnapshot"/> restores, bar the driver's keys).</summary>
    public void Add(Vehicle v)
    {
        Add(v.NetId); Add(v.NetGeneration);
        Add(v.Pos.X); Add(v.Pos.Y); Add(v.Pos.Z);
        Add(v.Rot.Right); Add(v.Rot.Up); Add(v.Rot.Forward);
        Add(v.Vel); Add(v.AngVel); Add(v.Accel); Add(v.Speed);
        Add(v.Steer); Add(v.Throttle); Add(v.Gear); Add(v.Handbrake); Add(v.Unk_0xf4); Add(v.DriveState);
        Add((int)v.Flags); Add(v.Surface); Add(v.GroundNormal); Add(v.NormalLoad); Add(v.HeightAboveGround); Add(v.GroundHeight);
        Add(v.Timer); Add(v.Wreck);
        var c = v.Rec;
        Add(c.Hit); AddRef(c.Other); Add(c.OtherMass); Add(c.OtherVel); Add(c.T); Add(c.C); Add(c.RelVel); Add(c.N);
        Add(v.P.BrakeStrength); Add(v.P.ChassisGrip); Add(v.P.Mass);
        var e = v.Engine;
        Add(e.Gear); Add(e.Force); Add(e.Damage); Add(e.Rpm); Add(e.Speedo);
        foreach (var w in v.Wheels.Concat(v.MiddleWheels))
        {
            Add(w.Grip); Add(w.Rolling); Add(w.SteerAngle); Add(w.Blown); Add(w.Spin); Add(w.Travel); Add(w.Rest); Add(w.Ground);
            Add(w.PrevTarget); Add(w.PrevRate); Add(w.AtLimit); Add(w.Y1); Add(w.Y2);
        }
        foreach (var arm in (CrewArm[])[v.Arms.Right, v.Arms.Left]) { Add(arm.Busy); Add(arm.Cur); Add(arm.Tgt); Add(arm.Axis); Add(arm.Next); }
        Add(v.Arms.Clock);
        if (v.Health is { } h)
        {
            foreach (var pool in (int[][])[h.Armour, h.Chassis, h.ArmourMax, h.ChassisMax]) foreach (int x in pool) Add(x);
            Add(h.RoughCarry);
            foreach (var part in h.Slots)
            {
                Add(part?.Hp ?? -1); Add(part?.Detached ?? false);
                if (part is Damage.SpecialPart sp) { Add(sp.Charges); Add(sp.Unk_0x10); }   // the keyed specials use them up
            }
        }
        else Add(-2);
        if (v.Emplacement is { } gun) { Add(gun.Hp); Add(gun.Enabled); Add(gun.Rate); }
        if (v.Armory is { } a)
        {
            Add(a.Selected); Add(a.Link); Add(a.Group.Count);
            foreach (int g in a.Group) Add(g);
            foreach (var w in a.Weapons.Concat(a.Handguns.OfType<Weapons.Weapon>()))
            {
                Add(w.Hp); Add(w.Ammo); Add(w.WaitA); Add(w.WaitB); Add(w.BurstLeft); Add(w.Trigger); Add(w.WasFiring);
                Add(w.LockTime); Add(w.OnTarget); Add(w.AimYaw); Add(w.AimPitch); Add(w.GunPitch); Add(w.GunPitchWant);
            }
        }
        else Add(-3);
    }

    /// <summary>
    /// The session's whole state: everything its snapshot holds (<see cref="SimSession.Snapshot"/>, docs/savestate.md),
    /// bar the events the views have not taken yet. The snapshot guard keeps that complete, so two sessions that hash
    /// the same are in the same state, as far as anything that decides the game goes.
    /// </summary>
    public static ulong Of(SimSession s)
    {
        var h = new StateHash();
        foreach (byte b in s.SnapshotBody(events: false)) { h._h ^= b; h._h *= 1099511628211UL; }
        return h.Value;
    }
}
