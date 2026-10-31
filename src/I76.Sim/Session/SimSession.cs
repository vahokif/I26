using I76.Sim.Collision;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Session;

/// <summary>
/// What a driver's keys leave between ticks (SimDriver's fields before the
/// session): each key ramp's hold time, and the trigger request latched while
/// the car could be driven.
/// </summary>
public struct DriverState
{
    /// <summary>How long throttle up, down, steer right and left have been held (the t, capped at 3 s).</summary>
    public float HoldUp, HoldDown, HoldRight, HoldLeft;
    /// <summary>The fire key and the hardpoint keys (bit i, hardpoint i + 1), as last latched.</summary>
    public bool Fire;
    public byte Keys;
    /// <summary>The armoury the latched request goes to.</summary>
    public Armory? Armed;
}

/// <summary>A human-driven car: inputs[Slot] of each tick drive it.</summary>
public sealed class Driver(Vehicle car, int slot)
{
    public Vehicle Car { get; } = car;
    public int Slot { get; } = slot;
    /// <summary>The input of the tick that last ran.</summary>
    public CarInput Input { get; internal set; }
    public DriverState State;
    /// <summary>The horn is held (for the sound; it changes nothing).</summary>
    public bool Horn => Input.Has(CarButtons.Horn);
    /// <summary>The car's <see cref="Vehicle.NetGeneration"/> the driver's key state belongs to (a client's prediction restores it).</summary>
    public int Generation = car.NetGeneration;
}

/// <summary>
/// The simulation's tick, without Godot: what SimDriver._Process did per
/// fixed step, in the same order, driven by one <see cref="CarInput"/> per
/// human-driven car. A tick applies each driver's commands and controls,
/// then runs the frame (the AI, contacts, vehicle steps), the drivers'
/// triggers, the mission script or melee (which runs the weapons), else
/// the weapons, and the drivers' arms. The AI and the script keep writing
/// their cars' controls from inside the tick. Nothing outside the inputs
/// and the session's own state decides an outcome, so a session replays
/// exactly from its inputs (docs/multiplayer.md).
/// </summary>
public sealed partial class SimSession
{
    /// <summary>The session's rate; it never changes while the session runs.</summary>
    public SimRate Rate { get; }
    /// <summary>The fixed step (<see cref="SimRate"/>): 102/2048 s at the default 20 Hz.</summary>
    public float TickSeconds => Rate.TickSeconds;

    public SimWorld World { get; }
    public Combat? Combat { get; }
    public MissionScript? Script { get; }
    public MeleeGame? Melee => Script?.Melee;
    public SessionOptions Options { get; }
    public NetIds Ids { get; } = new();
    public IReadOnlyList<Driver> Drivers => _drivers;
    /// <summary>Ticks run since the session started (the original's frames, at the fixed step).</summary>
    public int Ticks { get; private set; }
    /// <summary>The tick's clicks: a driver's trigger found that hardpoint's weapon broken (wmiss.wav).</summary>
    public List<(Vehicle Car, int Hardpoint)> Misfires { get; } = [];
    /// <summary>I76_BOOM=tick[,effect][;tick[,effect]...]: an explosion 15 m ahead of the player at that tick, or with "kill" / "kill-ai" the player's car / the nearest other living car destroyed then (honoured with <see cref="SessionOptions.DebugHooks"/>).</summary>
    public string? DebugBoom { get; set; }

    /// <summary>Raised in each tick once the inputs are applied, just before the frame.</summary>
    public event Action? FrameStarting;
    /// <summary>Raised in each tick just after the frame (the vehicles have moved; <see cref="Ticks"/> counts it).</summary>
    public event Action? FrameDone;

    private readonly List<Driver> _drivers = [];
    private bool _skip;
    private int _boomTick;

    public SimSession(SimWorld world, Combat? combat, MissionScript? script, SessionOptions? options = null, SimRate? rate = null)
    {
        Rate = rate ?? SimRate.Default;
        if (script is not null) script.Host.FrameDelta = Rate.TickSeconds;   // tick 0: the AI reads it before AfterFrame sets it
        if (combat is not null) combat.FrameDt = Rate.TickSeconds;
        World = world;
        Combat = combat;
        Script = script;
        Options = options ?? SessionOptions.SinglePlayer;
        World.Far = Options.Far;
        // isKeypress latches the key for the frame: every thread polling it this tick sees it
        if (script is not null) script.Host.SkipKey = () => _skip;
        if (script?.Melee is { } melee) melee.PlayerRenewed += Renewed;
        if (world.Ground is IHasStatics hs)
            foreach (var o in hs.World.Objects) o.NetId = Ids.Next(NetKind.Structure);
        AssignIds();
    }

    public SimSession(MissionSetup setup, SessionOptions? options = null, SimRate? rate = null) : this(setup.World, setup.Combat, setup.Script, options, rate) { }

    /// <summary>
    /// A melee car renewed (a respawn, a regen pad: a new armoury on the same car): the latched trigger request goes to
    /// the new armoury now, as the next tick's controls would send it, not to the old one no car holds (which a
    /// snapshot cannot name: it saves the car's own).
    /// </summary>
    private void Renewed(Vehicle car)
    {
        foreach (var d in _drivers)
            if (d.Car == car && d.State.Armed is not null) d.State.Armed = car.Armory;
    }

    /// <summary>A renewed car (a network melee's respawn or regen, a co-op respawn) starts with the driver's keys at rest.</summary>
    private bool FreshCarResets => Melee?.Rules.Network == true || Script?.Host.Coop is not null;

    /// <summary>
    /// Whether the reset-upright key (<see cref="CarCommands.ResetUpright"/>) works: always in single player, and in a network
    /// game or co-op where the host allows it (the host form's Car Reset, i26-server's --no-car-reset).
    /// </summary>
    public bool ResetUprightAllowed { get; set; } = true;

    /// <summary>In a cut-scene the script drives the player's car too; otherwise the keys do.</summary>
    public bool Cutscene => Script?.Host.View.Active == true;

    /// <summary>A co-op player's own camera scene (<c>CoopCameras</c>) holds that car's keys as a cut-scene does; the others play on.</summary>
    public bool CutsceneOf(Vehicle car) => Cutscene || Script?.Host.PersonalSceneOf(car) == true;

    /// <summary>A human driver for <paramref name="car"/>, fed inputs[n] for the n-th added; the car takes the player's rules.</summary>
    public Driver AddDriver(Vehicle car)
    {
        // the lowest slot nobody holds: a driver who left frees theirs for the next
        int slot = 0;
        while (_drivers.Any(x => x.Slot == slot)) slot++;
        var d = new Driver(car, slot);
        _drivers.Add(d);
        _drivers.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        if (!World.IsHuman(car)) World.Humans.Add(car);
        return d;
    }

    /// <summary>A driver leaves (a player quit a network melee): inputs[their slot] drives nobody, and the slot is free again.</summary>
    public void RemoveDriver(Driver d)
    {
        if (!_drivers.Remove(d)) return;
        // a co-op seat stays a human's (ScriptHost.CoopLeave takes it out of the group)
        if (d.Car != World.Player && Script?.Host.Coop is null) World.Humans.Remove(d.Car);
    }

    /// <summary>Ids for the cars and rounds that have none yet, in list order (creation order).</summary>
    public void AssignIds()
    {
        foreach (var v in World.Vehicles) if (v.NetId == 0) v.NetId = Ids.Next(NetKind.Vehicle);
        if (Combat is not null) foreach (var r in Combat.Rounds) if (r.NetId == 0) r.NetId = Ids.Next(NetKind.Round);
    }

    /// <summary>One tick; inputs[i] drives <see cref="Drivers"/>[i] (a missing one is no keys at all).</summary>
    public void Tick(IReadOnlyList<CarInput> inputs)
    {
        AssignIds();
        foreach (var d in _drivers)
        {
            d.Input = d.Slot < inputs.Count ? inputs[d.Slot] : default;
            // a network melee's fresh car (a respawn, a regen pad) has its controls at rest: so do the key ramps,
            // or a key held through the death would have run out its ramp and leave the new car at zero throttle
            // (a co-op respawn's too)
            if (d.Car.NetGeneration != d.Generation)
            {
                d.Generation = d.Car.NetGeneration;
                if (FreshCarResets) d.State = default;
            }
        }
        // the keys pressed since the last tick (SimDriver._UnhandledInput), then the held ones (its _Process)
        foreach (var d in _drivers) ApplyCommands(d);
        foreach (var d in _drivers) ApplyControls(d);
        Misfires.Clear();
        World.Step.Impacts.Clear();
        World.Step.ObjectHits.Clear();
        World.Step.DriveOns.Clear();
        World.Step.Honks.Clear();
        World.Step.CarHits.Clear();
        World.Step.Landings.Clear();
        FrameStarting?.Invoke();
        // the world runs the original's frame delta at this rate: at 20 Hz 2 frames in 5 are 103/2048 s and step the cars twice
        World.Frame(Rate.FrameDelta(Ticks));
        Ticks++;
        FrameDone?.Invoke();
        // the drivers' triggers are a per-frame request, set before the weapons run; then the selection (the order)
        foreach (var d in _drivers) { Triggers(d); Select(d); }
        _skip = _drivers.Any(d => d.Input.Has(CarCommands.Skip));
        Script?.AfterFrame(TickSeconds);
        Boom();
        if (Script is null && Combat is not null)
        {
            Vehicles.Emplacements.Frame(World, Combat, Melee?.Rules.Network == true);
            Combat.Frame(TickSeconds);
        }
        foreach (var d in _drivers) Arms(d);
        AssignIds();
    }

    /// <param name="fresh">A prediction's first run of this input (not a replay): its specials' sounds play at once.</param>
    private void ApplyCommands(Driver d, bool predict = false, bool fresh = false)
    {
        var p = d.Car;
        var c = d.Input.Commands;
        // a client's prediction runs what drives the car (engine key, lights, gears, reverse, the specials, the weapon
        // selection); the targets, the radar, the poem, self-destruct and the cheats are the server's alone
        if (predict) c &= CarCommands.StartEngine | CarCommands.Lights | CarCommands.ShiftUp | CarCommands.ShiftDown | CarCommands.Reverse
            | CarCommands.Special1 | CarCommands.Special2 | CarCommands.Special3 | CarCommands.WeaponCycle | CarCommands.WeaponLink;
        p.StartEngineKey = (c & CarCommands.StartEngine) != 0 ? 1 : 0;   // rewrites it every frame
        if (c == 0) return;
        // the weapon cycle and link keys are read after the triggers, in Select
        if ((c & CarCommands.Lights) != 0) p.Flags ^= VehicleFlags.Lights;
        // poetry is disabled outright in a network game
        // (Nitro has no such gate in its key handler, here a network melee still ignores it, and in Nitro the
        // speaker is whichever human pressed: co-op's every seat)
        if ((c & CarCommands.Poem) != 0 && Melee?.Rules.Network != true && Script is { } script)
        {
            if (script.Poetry.Nitro) { if (script.Host.Of(p) is { } speaker && (p == World.Player || World.IsHuman(p))) script.RequestPoem(speaker); }
            else if (p == World.Player) script.RequestPoem();
        }
        // the order: the shifts (cars only), e8 from the drive selector,
        // then reverse_direction flips e8 and sets the selector from it
        bool car = p.ObjectClass == 1;   // object class 1: the gear block of
        if (car && (c & (CarCommands.ShiftUp | CarCommands.ShiftDown)) != 0)
        {
            if ((c & CarCommands.ShiftUp) != 0 && p.DriveState < 5) p.DriveState++;
            if ((c & CarCommands.ShiftDown) != 0 && p.DriveState > 0) p.DriveState--;
            p.Gear = p.DriveState == 1 ? -1f : 1f;
        }
        if (car && (c & CarCommands.Reverse) != 0)
        {
            p.Gear = -p.Gear;
            p.DriveState = p.Gear < 0 ? 1 : 3;
        }
        if ((c & (CarCommands.Special1 | CarCommands.Special2 | CarCommands.Special3)) != 0 && (p.Flags & VehicleFlags.Dead) == 0)
            for (int i = 0; i < 3; i++)
                if ((c & (CarCommands)((uint)CarCommands.Special1 << i)) != 0)
                {
                    if (!predict) UseSpecial(p, i);
                    // a client's prediction plays its sound when it first runs the press (the server's click for it is not played)
                    else if (Specials.Activate(p, i, World.Time) is { } sound && fresh) Combat?.Events.Add(new WeaponClick(p, sound));
                }
        if ((c & CarCommands.SelfDestruct) != 0 && (p.Flags & VehicleFlags.Dead) == 0) (Combat?.Damage as Damage.DamageModel)?.Destroy(p);
        // RESET_UPRIGHT: not in a cut-scene (the script holds the car), not dead, and where the host allows it
        if ((c & CarCommands.ResetUpright) != 0 && ResetUprightAllowed && !CutsceneOf(p) && UprightReset.Can(p)) UprightReset.Apply(World, p);
        // the typed cheats, the player's own car only (the network never carries them)
        if (p == World.Player)
        {
            if ((c & CarCommands.CheatGetDown) != 0) Script?.GetDown();
            foreach (var (cmd, slot) in CheatTyper.Tyres)
                if ((c & cmd) != 0) (Combat?.Damage as Damage.DamageModel)?.FlattenTyre(p, slot);
        }
        if (Combat is not { } cb) return;
        if ((c & CarCommands.RadarRange) != 0) cb.ToggleRadarRange(p);
        if ((c & CarCommands.RadarRangeReset) != 0) cb.ResetRadarRange(p);
        if ((c & CarCommands.RadarCamera) != 0) cb.ToggleRadarCamera(p);
        // the radar camera's flag goes with the target: T and Y always, E / prev after 5 s, Q when it finds nothing
        if ((c & CarCommands.TargetPrev) != 0) { if (cb.NextRestarts(p)) cb.RadarCamera.Remove(p); cb.RadarTarget[p] = cb.PrevTarget(p); }
        if ((c & CarCommands.TargetNearest) != 0) { cb.RadarCamera.Remove(p); cb.RadarTarget[p] = cb.NearestEnemy(p); }
        if ((c & CarCommands.TargetNext) != 0) { if (cb.NextRestarts(p)) cb.RadarCamera.Remove(p); cb.RadarTarget[p] = cb.NextTarget(p); }
        if ((c & CarCommands.TargetFrontal) != 0 && (cb.RadarTarget[p] = cb.FrontalTarget(p)) is null) cb.RadarCamera.Remove(p);
        if ((c & CarCommands.TargetReset) != 0) { cb.RadarTarget[p] = null; cb.RadarCamera.Remove(p); }
    }

    /// <summary>
    /// special1..3: <see cref="Specials.Activate"/>, whose
    /// sound plays on the car as a click event (3D, attached; the network carries it as one).
    /// </summary>
    private void UseSpecial(Vehicle p, int slot)
    {
        if (Specials.Activate(p, slot, World.Time) is { } sound) Combat?.Events.Add(new WeaponClick(p, sound));
    }

    /// <summary>
    /// The keyboard path, once a tick: each steer key's hold
    /// time t (capped at 3 s) gives ±√(t/3), so full lock takes 3 s, and 0 the
    /// moment it is released; throttle up adds 0.4·(1 − t/3) a tick and down
    /// takes away 0.5·(1 − t/3), 0 on release; both clamped to ±0.9999. Down
    /// only brakes: reverse is its own command. With the car dead, or the
    /// script holding the camera, the keys do nothing.
    /// </summary>
    private void ApplyControls(Driver d)
    {
        var p = d.Car;
        // honk_horn in a network game sets the horn flag for the other players' ears (the key itself is sampled by the view)
        if (Melee?.Rules.Network == true)
        {
            bool honk = !CutsceneOf(p) && (p.Flags & VehicleFlags.Dead) == 0 && d.Input.Has(CarButtons.Horn);
            p.Flags = honk ? p.Flags | VehicleFlags.Honking : p.Flags & ~VehicleFlags.Honking;
        }
        var i = d.Input;
        ref var s = ref d.State;
        // weapons (input.map): the fire key works the selected group, 1–5 their hardpoints. Read every tick, a dead car's too:
        // Takes the keys as they are each frame with no test of the car, so a trigger held through a death lets go
        // when the key does (read only while alive, the last live tick's fire stayed latched and the wreck fired on for good)
        if (p.Armory is { } a)
        {
            s.Fire = i.Has(CarButtons.Fire);
            s.Keys = (byte)(((uint)i.Buttons / (uint)CarButtons.Hardpoint1) & 0x1f);
            s.Armed = a;
        }
        if (CutsceneOf(p) || (p.Flags & VehicleFlags.Dead) != 0) return;
        if (World.External?.Invoke(p) == true)
        {
            // another model drives this car (Modern Physics): it reads the keys at every one of its own steps and ramps
            // throttle and steer by time (KeyRamp), so this tick's ramp is not run
            s.HoldUp = s.HoldDown = s.HoldRight = s.HoldLeft = 0;
        }
        else
        {
            float throttle = p.Throttle, steer = p.Steer;
            KeyRamp.Apply(ref s, i, TickSeconds, null, ref throttle, ref steer);
            p.Throttle = throttle;
            p.Steer = steer;
        }
        p.Handbrake = i.Has(CarButtons.EBrake) ? 1 : 0;
    }

    private static readonly bool[][] KeySets = Enumerable.Range(0, 32).Select(k => Enumerable.Range(0, 5).Select(i => (k & 1 << i) != 0).ToArray()).ToArray();

    /// <summary>
    /// The hardpoints answer only while both arms are down and
    /// still; a raised, still arm's handgun takes the fire key instead. Out of
    /// the cockpit the arms rest down.
    /// </summary>
    private void Triggers(Driver d)
    {
        if (d.State.Armed is not { } arm || CutsceneOf(d.Car)) return;
        // a wrecked car's guns stay silent, whatever the keys say (#109: a held trigger killed from the grave)
        if ((d.Car.Flags & VehicleFlags.Dead) != 0) { foreach (var w in arm.Weapons) w.Trigger = false; return; }
        var arms = d.Car.Arms;
        bool cockpit = d.Input.Has(CarButtons.Cockpit);
        bool fire = d.State.Fire;
        if (!cockpit || (arms.Idle(1) && arms.Idle(2)))
            foreach (var i in arm.PlayerTriggers(fire, KeySets[d.State.Keys])) Misfires.Add((d.Car, i));
        else
            foreach (int ch in (int[])[2, 1])
                if (arm.Handguns[ch - 1] is { } hg && arms.Ready(ch)) hg.Trigger = hg.Ammo >= 1 && fire;
    }

    /// <summary>
    /// After the triggers: the cycle and link keys and the auto-cycle off a selected weapon shot to nothing
    /// (<see cref="Armory.Select"/>), for each driver's car. A changed selection turns the radar camera off;
    /// cammo.wav plays on the car when the original asks for it (a selection changed, a weapon linked or
    /// unlinked), as a click event the network carries; a client's prediction leaves the sound to the server's.
    /// </summary>
    private void Select(Driver d, bool predict = false)
    {
        if (d.Car.Armory is not { } arm) return;
        var c = d.Input.Commands;
        int sounds = arm.Select((c & CarCommands.WeaponCycle) != 0, (c & CarCommands.WeaponLink) != 0, out bool changed);
        if (changed) Combat?.RadarCamera.Remove(d.Car);
        if (sounds > 0 && !predict) Combat?.Events.Add(new WeaponClick(d.Car, "cammo.wav"));
    }

    /// <summary>The arms follow the glance in the cockpit (a tenth of a second a frame); out of it they rest down.</summary>
    private void Arms(Driver d)
    {
        var arms = d.Car.Arms;
        if (!d.Input.Has(CarButtons.Cockpit)) { arms.Reset(); return; }
        arms.Want(1, d.Input.Has(CarButtons.RaiseRightArm));
        arms.Want(2, d.Input.Has(CarButtons.RaiseLeftArm));
        arms.Step(TickSeconds);
    }

    private void Boom()
    {
        if (!Options.DebugHooks || DebugBoom is not { } boom || Combat is not { } bc || World.Player is not { } bp) return;
        // several, separated by ';' (e.g. "10,kill-ai;20,kill-ai": two wrecks)
        foreach (var entry in boom.Split(';'))
        {
            var parts = entry.Split(',');
            if (!int.TryParse(parts[0], out int bt) || bt != _boomTick) continue;
            // "kill-all": every other car still alive destroyed (the FSM's destroy), as the I76_KILL_AT dev hook does to type-1 objects
            if (parts.Length > 1 && parts[1] == "kill-all")
            {
                foreach (var v in World.Vehicles.Where(v => v != bp && !v.IsHeli && !v.IsEmplacement && !Damage.DamageModel.IsDead(v)).ToList())
                    (bc.Damage as Damage.DamageModel)?.Destroy(v);
                continue;
            }
            // "kill" destroys the player's car there and then, "kill-ai" the nearest other one still alive (the FSM's destroy), for captures of a wreck
            var kill = parts.Length > 1 ? parts[1] switch
            {
                "kill" => bp,
                "kill-ai" => World.Vehicles.Where(v => v != bp && !Damage.DamageModel.IsDead(v)).OrderBy(v => (v.Pos.ToVector3() - bp.Pos.ToVector3()).LengthSquared()).FirstOrDefault(),
                _ => null,
            } : null;
            // "hit-ai[:N]": the nearest other one takes an N-point (default 1) round from the player (its recorder: attacked by the player)
            if (parts.Length > 1 && parts[1].Split(':') is ["hit-ai", ..] hitArgs
                && World.Vehicles.Where(v => v != bp && !Damage.DamageModel.IsDead(v)).OrderBy(v => (v.Pos.ToVector3() - bp.Pos.ToVector3()).LengthSquared()).FirstOrDefault() is { } hit)
            {
                var pk = new Weapons.DamagePacket { Attacker = bp, Mask = 2, Kind = Damage.DamageKind.Round };
                pk.Ch[1] = hitArgs.Length > 1 && int.TryParse(hitArgs[1], out int amount) ? amount : 1;
                bc.Damage?.Apply(hit, pk, System.Numerics.Vector3.Zero);
                continue;
            }
            // "hit-me[:N[:zone]]": the player takes an N-point (default 1) round in that zone (0 front, 1 left, 2 right, 3 rear, the default, 4 top)
            if (parts.Length > 1 && parts[1].Split(':') is ["hit-me", ..] meArgs)
            {
                var pk = new Weapons.DamagePacket { Mask = 2, Kind = Damage.DamageKind.Round };
                pk.Ch[1] = meArgs.Length > 1 && int.TryParse(meArgs[1], out int amount) ? amount : 1;
                var at = (meArgs.Length > 2 && int.TryParse(meArgs[2], out int z) ? z : 3) switch
                {
                    0 => new System.Numerics.Vector3(0, 0, 1), 1 => new System.Numerics.Vector3(-1, 0, 0), 2 => new System.Numerics.Vector3(1, 0, 0),
                    4 => new System.Numerics.Vector3(0, 1, 0), _ => new System.Numerics.Vector3(0, 0, -1),
                };
                bc.Damage?.Apply(bp, pk, at);
                continue;
            }
            // "kill-struct": the live structure nearest the point 15 m ahead destroyed (the FSM's destroy), for captures of a destroyed look
            // "kill-struct:NAME" the live structure of that name nearest the player instead
            if (parts.Length > 1 && parts[1].Split(':') is ["kill-struct", ..] ks && World.Ground is Collision.IHasStatics hs && bc.Damage is Damage.DamageModel sdm)
            {
                var at = bp.Pos.ToVector3() + bp.Rot.Forward * 15f;
                var named = ks.Length > 1 ? hs.World.Objects.Where(o => !o.Destroyed && o.Name.Equals(ks[1], StringComparison.OrdinalIgnoreCase)) : hs.World.Objects.Where(o => !o.Destroyed);
                if (ks.Length > 1) at = bp.Pos.ToVector3();
                if (named.OrderBy(o => (o.Centre - at).LengthSquared()).FirstOrDefault() is { } so) { sdm.DestroyStructure(so); Console.WriteLine($"[boom] destroyed {so.Name} at {so.Centre.X:0},{so.Centre.Z:0}"); }
                continue;
            }
            // "kill-node:NAME:PART": the node of the nearest live structure NAME whose name contains PART destroyed as a round's hit would (a bridge's rails)
            if (parts.Length > 1 && parts[1].Split(':') is ["kill-node", var kname, var kpart] && World.Ground is Collision.IHasStatics hn && bc.Damage is Damage.DamageModel ndm)
            {
                var near = hn.World.Objects.Where(o => !o.Destroyed && o.Name.Equals(kname, StringComparison.OrdinalIgnoreCase)).OrderBy(o => (o.Centre - bp.Pos.ToVector3()).LengthSquared()).FirstOrDefault();
                if (near is null) Console.WriteLine("[boom] no live structure " + kname + "; there are " + string.Join(",", hn.World.Objects.Select(o => o.Name).Distinct().Take(40)));
                if (near is not null)
                    for (int i = 0; i < near.Nodes.Count; i++)
                        if (near.Nodes[i].Name.Contains(kpart, StringComparison.OrdinalIgnoreCase))
                        {
                            var pk = new Weapons.DamagePacket { Mask = 1 };
                            pk.Ch[0] = near.Nodes[i].Hp + 1;
                            ndm.StructureSlot(near, i, pk);
                            Console.WriteLine($"[boom] destroyed node {near.Nodes[i].Name} of {near.Name}: type {near.Nodes[i].Type} hp {near.Nodes[i].Hp} dead {near.Nodes[i].Destroyed} root {near.Destroyed}");
                        }
                continue;
            }
            // "kill-gun": the nearest live gun emplacement takes a 1000-point round from the player (its damage slot's death)
            if (parts.Length > 1 && parts[1] == "kill-gun"
                && World.Vehicles.Where(v => v.IsEmplacement && !Damage.DamageModel.IsDead(v)).OrderBy(v => (v.Pos.ToVector3() - bp.Pos.ToVector3()).LengthSquared()).FirstOrDefault() is { } gun)
            {
                var pk = new Weapons.DamagePacket { Attacker = bp, Mask = 2, Kind = Damage.DamageKind.Round };
                pk.Ch[1] = 1000;
                bc.Damage?.Apply(gun, pk, System.Numerics.Vector3.Zero);
                continue;
            }
            if (kill is not null) (bc.Damage as Damage.DamageModel)?.Destroy(kill);
            else if (parts.Length < 2 || !parts[1].StartsWith("kill")) bc.Explode(parts.Length > 1 ? parts[1] : "x1_carx1", bp.Pos.ToVector3() + bp.Rot.Forward * 15f, null);
        }
        _boomTick++;
    }

    // ---- prediction and checks ----

    /// <summary>
    /// One tick of a single driver's car on its own, as <see cref="Tick"/> runs it for that car (a network client's
    /// prediction of its own car, docs/multiplayer.md): the fresh-car check, its driving commands, its controls, its
    /// contacts with the scenery and the other cars in <paramref name="cars"/> (one-sided: they are neither moved nor
    /// touched), and its step on the world's frame delta of the server's tick <paramref name="tick"/>. No AI, script,
    /// weapons or rounds; <paramref name="damage"/> stands in for the damage model inside the step (the server's damage
    /// is the server's). The step's lists (impacts, hits, landings) are cleared first.
    /// </summary>
    /// <param name="cars">Every car in the server's list order, <paramref name="d"/>'s own among them (the order decides which way a pair is tested first).</param>
    /// <param name="fresh">The input's first run (not a replay after a snapshot): the specials' sounds go into the combat events.</param>
    public void PredictTick(Driver d, CarInput input, int tick, IReadOnlyList<Vehicle> cars, Vehicles.IStepDamage? damage, bool fresh = false)
    {
        d.Input = input;
        if (d.Car.NetGeneration != d.Generation)
        {
            d.Generation = d.Car.NetGeneration;
            if (FreshCarResets) d.State = default;
        }
        ApplyCommands(d, predict: true, fresh);
        ApplyControls(d);
        World.Step.Impacts.Clear();
        World.Step.ObjectHits.Clear();
        World.Step.DriveOns.Clear();
        World.Step.Honks.Clear();
        World.Step.CarHits.Clear();
        World.Step.Landings.Clear();
        var saved = World.Step.Damage;
        var savedHeli = World.HeliDamage;
        World.Step.Damage = damage;
        if (damage is Vehicles.IHeliDamage hd) World.HeliDamage = hd;   // a helicopter's kill and crash: the state, none of the effects
        try { World.FrameOne(d.Car, Rate.FrameDelta(tick), cars); }
        finally { World.Step.Damage = saved; World.HeliDamage = savedHeli; }
    }

    /// <summary>
    /// The rest of <see cref="Tick"/> for that car after <see cref="PredictTick"/>: its triggers (the arms' gate), its
    /// weapons (<see cref="Combat.PredictFrame"/>: shots into the events, rounds into the predicted rounds, harmless) and
    /// its arms, as a network client predicts its own fire. A replay (<paramref name="fresh"/> false) only runs the
    /// timers, ammo, aims and arms. The misfires are the server's.
    /// </summary>
    public void PredictFire(Driver d, bool fresh)
    {
        if (Combat is null) return;
        Triggers(d);
        Select(d, predict: true);
        Misfires.Clear();
        Combat.PredictFrame(d.Car, TickSeconds, fresh);
        Arms(d);
    }

    /// <summary>The selection step alone (<see cref="Select"/>), for a prediction that does not run <see cref="PredictFire"/>.</summary>
    public void PredictSelect(Driver d) => Select(d, predict: true);

    /// <summary>A driver's car and key state, to restore exactly (client prediction replays from it).</summary>
    public CarSnapshot Capture(Driver d) => CarSnapshot.Capture(d.Car, Combat, d);

    /// <summary>Puts a captured car (and its driver's keys) back as they were.</summary>
    public void Restore(CarSnapshot s) => s.Restore(Combat);

    /// <summary>A hash of everything the session's outcome is made of (<see cref="Session.StateHash"/>).</summary>
    public ulong StateHash() => Session.StateHash.Of(this);
}
