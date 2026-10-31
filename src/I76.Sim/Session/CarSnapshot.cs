using I76.Sim.Damage;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Session;

/// <summary>
/// One car's whole simulation state as values, restorable exactly: the body
/// (pose, motion, controls, flags, timers, the contact record), its
/// parameters (which damage lowers), engine, wheels, arms, armour, chassis
/// and every damage part, the armoury (selection, link group, and each
/// weapon's ammo, timers, heat, aim and hit points), the radar's target and,
/// for a driven car, the driver's key ramps. A client predicting its own car
/// restores the server's state with this and replays its inputs.
///
/// The objects the car holds (its engine, armoury, damage state) are put
/// back as well as their contents, so a capture from before a regen restores
/// the old car. Plain objects are copied field by field (<see cref="FieldCopy"/>),
/// so a field added to them later is covered without being listed here.
/// </summary>
public sealed class CarSnapshot
{
    public Vehicle Car { get; }
    private readonly Vehicle _body;
    private readonly VehicleParams _p;
    private readonly Engine _engine;
    private readonly Wheel[] _wheels;
    private readonly CrewArms _arms;
    private readonly ArmoryState? _armory;
    private readonly DamageState? _health;
    private readonly bool _hasTarget;
    private readonly Vehicle? _target;
    private readonly Driver? _driver;
    private readonly DriverState _driverState;

    private sealed record ArmoryState(Armory Of, int Selected, bool Link, int[] Group, (Weapon Live, Weapon Copy)[] Weapons);
    private sealed record DamageState(VehicleDamage Of, int[][] Pools, float RoughCarry, (Part Live, Part Copy)?[] Slots);

    private CarSnapshot(Vehicle v, Combat? combat, Driver? driver)
    {
        Car = v;
        _body = FieldCopy.Clone(v);
        _p = FieldCopy.Clone(v.P);
        _engine = FieldCopy.Clone(v.Engine);
        _wheels = [.. v.Wheels.Concat(v.MiddleWheels).Select(FieldCopy.Clone)];
        _arms = v.Arms.Clone();
        if (v.Armory is { } a)
            _armory = new(a, a.Selected, a.Link, [.. a.Group],
                [.. a.Weapons.Concat(a.Handguns.OfType<Weapon>()).Select(w => (w, FieldCopy.Clone(w)))]);
        if (v.Health is { } h)
            _health = new(h, [[.. h.Armour], [.. h.Chassis], [.. h.ArmourMax], [.. h.ChassisMax], [.. h.ArmourMirror], [.. h.ChassisMirror]],
                h.RoughCarry, [.. h.Slots.Select(s => s is null ? ((Part, Part)?)null : (s, FieldCopy.Clone(s)))]);
        if (combat is not null) _hasTarget = combat.RadarTarget.TryGetValue(v, out _target);
        _driver = driver;
        if (driver is not null) _driverState = driver.State;
    }

    public static CarSnapshot Capture(Vehicle v, Combat? combat = null, Driver? driver = null) => new(v, combat, driver);

    public void Restore(Combat? combat = null)
    {
        var v = Car;
        // the body first: it puts back which engine, wheels, armoury and damage state the car had
        FieldCopy.Into(_body, v);
        FieldCopy.Into(_p, v.P);
        FieldCopy.Into(_engine, v.Engine);
        foreach (var (w, saved) in v.Wheels.Concat(v.MiddleWheels).Zip(_wheels)) FieldCopy.Into(saved, w);
        v.Arms.CopyFrom(_arms);
        if (_armory is { } a)
        {
            a.Of.Selected = a.Selected;
            a.Of.Link = a.Link;
            a.Of.Group.Clear();
            a.Of.Group.AddRange(a.Group);
            foreach (var (live, copy) in a.Weapons) FieldCopy.Into(copy, live);
        }
        if (_health is { } h)
        {
            int[][] pools = [h.Of.Armour, h.Of.Chassis, h.Of.ArmourMax, h.Of.ChassisMax, h.Of.ArmourMirror, h.Of.ChassisMirror];
            for (int k = 0; k < pools.Length; k++) h.Pools[k].CopyTo(pools[k], 0);
            h.Of.RoughCarry = h.RoughCarry;
            for (int k = 0; k < h.Slots.Length; k++)
            {
                if (h.Slots[k] is { } s) FieldCopy.Into(s.Copy, s.Live);
                h.Of.Slots[k] = h.Slots[k]?.Live;
            }
        }
        if (combat is not null)
        {
            if (_hasTarget) combat.RadarTarget[v] = _target;
            else combat.RadarTarget.Remove(v);
        }
        if (_driver is not null) _driver.State = _driverState;
    }
}
