using System.Numerics;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// The mission script's world: entities over the simulated vehicles and the
/// placed scenery, AI orders run through the Ptable (Ptable.cs), the CB
/// radio, objectives, the scripted camera and the mission's end.
/// </summary>
public sealed partial class ScriptHost : IFsmHost
{
    public SimWorld World { get; }
    public IReadOnlyList<Entity> Entities { get; }
    public Entity? Player { get; }
    public Objectives Objectives { get; }
    public CbRadio Cb { get; }
    public ScriptCamera View { get; }
    public float Now { get; set; }
    /// <summary>The FSM tick, for the damage-event window (this tick or the last).</summary>
    public int FsmTick { get; set; }
    /// <summary>The menu/skip key this frame.</summary>
    public Func<bool> SkipKey { get; set; } = () => false;
    /// <summary>successAll / failAll: the result, when it lands, and the debrief's failure line (−2 none).</summary>
    public (bool Success, float At, int FailMessage)? End { get; private set; }
    public event Action<string>? Logged;
    public event Action<string>? Movie;
    public event Action<Entity>? CarStarted;


    private readonly Dictionary<Vehicle, Entity> _byVehicle = [];

    public ScriptHost(SimWorld world, IReadOnlyList<Entity> entities, Objectives objectives, Func<string, float?> soundLength)
    {
        World = world;
        Entities = entities;
        BuiltEntities = entities.Count;
        foreach (var e in entities) if (e.Vehicle is { } v) _byVehicle[v] = e;
        Player = world.Player is { } p && _byVehicle.TryGetValue(p, out var pe) ? pe : null;
        Objectives = objectives;
        Objectives.Log = Log;
        Cb = new CbRadio(soundLength, SpeakerDead);
        View = new ScriptCamera((x, z) => world.Ground.BareHeight(x, z)) { Log = Log };
        world.Scene = View;
    }

    public Entity? Of(Vehicle v) => _byVehicle.GetValueOrDefault(v);

    /// <summary>The CB's test of a speaker: dead; in co-op a human speaks for the group, gone only once every human is dead.</summary>
    private bool SpeakerDead(object o) => Coop is not null && IsHumanEntity(o as Entity) ? !Players.Any() : IsDead(o);

    /// <summary>An entity created while the mission runs (a melee's cars): known to the host from now on.</summary>
    public void Add(Entity e)
    {
        if (Entities is List<Entity> l) l.Add(e);
        if (e.Vehicle is { } v) _byVehicle[v] = e;
    }

    /// <summary>An entity taken out of the world: its car leaves the simulation and the AI.</summary>
    public void Remove(Entity e)
    {
        e.Removed = true;
        if (e.Vehicle is not { } v) return;
        World.Ai.Remove(v);
        World.Vehicles.Remove(v);
        _byVehicle.Remove(v);
    }
    private static Entity E(object o) => (Entity)o;

    public void Log(string message) => Logged?.Invoke(message);

    // ---- objects ----
    /// <summary>The registry lookup scans from the last placement back: the latest wins.</summary>
    public object? FindByInstanceId(byte[] id)
    {
        for (int i = Entities.Count - 1; i >= 0; i--)
            if (Entities[i].Id.AsSpan().SequenceEqual(id)) return Entities[i];
        Log($"FSM - instance id for {System.Text.Encoding.Latin1.GetString(id.Select(b => (byte)(b & 0x7f)).ToArray()).TrimEnd('\0')} does not exist in mission file");
        return null;
    }
    public bool IsRemoved(object o) => E(o).Removed;
    public bool IsDead(object o) => E(o).Dead;
    public bool IsVehicle(object o) => E(o).Vehicle is not null;
    /// <summary>
    /// The structure test over the root colliders (934): types 2, 3 (billboards, shacks,
    /// trees, scenery wrecks), 4 (signs), 0xb, 0xc and 0x5c, placed with a collider.
    /// </summary>
    public bool IsStructure(object o) => E(o) is { Vehicle: null, Static: not null } e && e.Type is 2 or 3 or 4 or 0xb or 0xc or 0x5c;
    public bool IsAirborne(object o) => E(o).Vehicle is { } v && (v.Flags & VehicleFlags.Airborne) != 0;
    public float HealthPercent(object o) => E(o).Health;
    public Vector3 Position(object o) => E(o).Position;
    public int Team(object o) => E(o).Team;
    // (Co-op Scaling's copies are not the script's: CoopExtras)
    public IEnumerable<object> Vehicles => Entities.Where(e => e.Vehicle is not null && !e.Removed && e.ExtraOf is null);
    public IEnumerable<object> Structures => Entities.Where(e => !e.Removed && IsStructure(e));
    public IEnumerable<object> AiVehicles => Entities.Where(e => e.Vehicle is { IsEmplacement: false } && !e.Removed && e.ExtraOf is null).OrderBy(e => e.Team);

    // ---- AI ----
    /// <summary>The frame delta the AI's controllers see (the frame delta).</summary>
    public float FrameDelta { get; set; } = I76.Sim.Session.SimRate.R20.TickSeconds;
    private Ptable? _ptable;
    private int _playerDrivenTick = -10;
    public Ptable Ptable => _ptable ??= new Ptable(this);
    /// <summary>The AI tactics table the Ptable binds (read from the install's executable, <see cref="I76.Formats.Exe.TacticsTable"/>); null: no AI orders in this world.</summary>
    public I76.Formats.Exe.PtEntry[]? Tactics { get; init; }

    /// <summary>
    /// The order opcodes: their fields, then the script's AI order, which is the AI's
    /// whole tick for the frame. The player's car obeys only while the script
    /// holds the camera; otherwise its orders are dropped (the keys drive it).
    /// In co-op every human's car is the player's in this.
    /// </summary>
    public void Order(object actor, object? target, AiOrder order, IReadOnlyList<Vector3>? path, float speed, int followFlag = 0, float f1 = 0, float f2 = 0, object? reference = null)
    {
        var e = E(actor);
        if (e.Vehicle is null) return;
        bool human = IsHumanEntity(e);
        if (human && !View.Active) return;
        if (human) Moved(e);
        var t = target as Entity;
        if (order is AiOrder.Goto or AiOrder.Evade or AiOrder.Race) { e.Path = path; e.OrderSpeed = speed; e.Ai.CommandSpeed = speed; }
        if (order == AiOrder.Follow) { e.FollowRef = reference as Entity; e.FollowFlag = followFlag; e.FollowF1 = f1; e.FollowF2 = f2; }
        e.Order = order;
        e.OrderTarget = t;
        // the order's target replaces the radar's (the AI's radar-target update reads it): a structure held from an earlier order goes
        if (t?.Static is null && Combat is { } oc) oc.StructureTarget.Remove(e.Vehicle);
        // an order with no target (sit, hide, goto) sets the AI's current target to none, and its radar with it: the turrets
        // go back to straight ahead rather than holding the last order's target (#176, Nitro's Mother: evade, then sit)
        if (t is null && !human && !e.Vehicle.IsEmplacement && Combat is { } nc) nc.RadarTarget[e.Vehicle] = null;
        if (e.Dead) return;
        // The player's Ptable resets unless the script drove it last frame
        bool reset = human && _playerDrivenTick != FsmTick - 1 && _playerDrivenTick != FsmTick;
        if (human) _playerDrivenTick = FsmTick;
        Ptable.Issue(e, t, order, resetAlways: reset);
        if (!human) OrderExtras(e, t, order, path, speed, followFlag, f1, f2, reference);
    }

    /// <summary>
    /// Every weapon tries the target through the fire decision; the radar holds it for the turrets.
    /// The target is any object the order names: a structure too (T01's diner, T02's school, T08's warehouse).
    /// </summary>
    public void FireAt(Entity e, Entity target)
    {
        if (Combat is not { } c || e.Vehicle is not { } v) return;
        if (HoldFire?.Invoke(e, target) == true) return;
        // The AI's radar-target update: an AI radar holds the order's target, but an emplacement's holds the player (Emplacements.Frame)
        if (!v.IsEmplacement)
        {
            c.RadarTarget[v] = target.Vehicle;
            if (target.Vehicle is null && target.Static is { Vehicle: null } st) c.StructureTarget[v] = st;
            else c.StructureTarget.Remove(v);
        }
        if (target.Dead || v.Armory is not { } a) return;
        if (target.Vehicle is { } tv)
        {
            foreach (var w in a.Weapons)
                if (w.Hp > 0 && w.Ammo > 0 && HoldWeapon?.Invoke(e, w) != true && c.AiShouldFire(v, tv, w, e.Ai.FireSkill, e.Ai.FireAccuracy)) w.Trigger = true;
        }
        else if (target.Static is { Vehicle: null } s)
        {
            foreach (var w in a.Weapons)
                if (w.Hp > 0 && w.Ammo > 0 && c.AiShouldFire(v, s, target.Position, target.Rot.Forward, w, e.Ai.FireSkill, e.Ai.FireAccuracy)) w.Trigger = true;
        }
    }

    /// <summary>
    /// The port's own: a network melee's team play holds a bot's fire when a teammate stands in the line to its target
    /// (<see cref="MeleeGame"/>); the original's AI only tests its own weapon's line. Null in every other game.
    /// </summary>
    public Func<Entity, Entity, bool>? HoldFire { get; set; }
    /// <summary>The port's own: a weapon an AI car does not fire at a car (a network melee's bots do not lay mines or blox).</summary>
    public Func<Entity, Weapons.Weapon, bool>? HoldWeapon { get; set; }

    /// <summary>The mission's roads, for the road war and the flee tactics.</summary>
    public Ai.RoadNetwork? Roads { get; set; }

    /// <summary>Weapons and rounds, for the fire decision.</summary>
    public Weapons.Combat? Combat { get; set; }

    public bool ConsumeArrived(object actor) { var e = E(actor); bool a = e.Arrived; e.Arrived = false; return a; }
    public bool ConsumeAtFollow(object actor) { var e = E(actor); bool a = e.AtFollow; e.AtFollow = false; return a; }
    private bool Recent(int tick) => tick == FsmTick || tick == FsmTick - 1;
    public bool ConsumeShot(object o) { var e = E(o); if (!Recent(e.ShotTick)) return false; e.ShotTick = -10; return true; }
    public bool ConsumeRammed(object o) { var e = E(o); if (!Recent(e.RammedTick)) return false; e.RammedTick = -10; return true; }
    public bool ConsumeAttacked(object o)
    {
        var e = E(o);
        if (!Recent(e.AttackedTick)) return false;
        e.ShotTick = e.RammedTick = e.AttackedTick = -10;
        return true;
    }
    public object? LastAttacker(object o, int kind) => kind switch { 0 => E(o).LastRammer, 1 => E(o).LastShooter, _ => E(o).LastAttacker };
    public bool KilledByPlayer(object o) => E(o).KilledByPlayer;
    public void SetSkill(object o, int a, int b) { foreach (var e in WithExtras(o)) { e.SkillA = a; e.SkillB = b; e.Ai.SetSkill(a, b); } }
    /// <summary>setAgg: aggression n−1, and the next order re-chooses the tactic.</summary>
    public void SetAggression(object o, int n) { foreach (var e in WithExtras(o)) { e.Aggression = n - 1; e.Depth = -1; e.MaxAttackers = 1; } }
    public void SetAvoid(object o, int? on) { foreach (var e in WithExtras(o)) e.Avoid = on is null ? !e.Avoid : on != 0; }
    public void SetMaxAttackers(object o, int n) { foreach (var e in WithExtras(o)) e.MaxAttackers = n; }
    public void SetHeliHeight(object o, int height)
    {
        if (E(o) is not { IsHeli: true, Vehicle: { IsHeli: true } v }) { Log("FSM - setHeliHeight has been called for something that was not a helicopter"); return; }
        HeliStep.SetHeight(v, height);
    }
    /// <summary>setArtillery / setArtilleryRate: the block of a type-8 object; nothing else has one.</summary>
    public void SetArtillery(object o, int on) { if (E(o).Vehicle?.Emplacement is { } g) g.Enabled = on; }
    public void SetArtilleryRate(object o, int rate) { if (E(o).Vehicle?.Emplacement is { } g) g.Rate = rate; }

    /// <summary>
    /// Victim was damaged by attacker this FSM tick. Ram events
    /// come from car contacts until the damage model (M6) reports them.
    /// </summary>
    public enum Hit { Ram, Shot, Attacked }

    public void RecordDamage(Entity victim, Entity attacker, Hit kind)
    {
        if (kind == Hit.Ram) { victim.LastRammer = attacker; victim.RammedTick = FsmTick; }
        else if (kind == Hit.Shot) { victim.LastShooter = attacker; victim.ShotTick = FsmTick; }
        victim.LastAttacker = attacker;
        victim.AttackedTick = FsmTick;
        victim.LastDamageTime = Now;
        // co-op: any human's kill is the player's; the seat is kept for isGroovesFault's count
        if (victim.Dead && IsHumanEntity(attacker))
        {
            victim.KilledByPlayer = true;
            if (Coop?.SeatOf(attacker) is { } ks) victim.KillerSeat = ks.Index;
        }
    }

    // ---- CB ----
    public void CbSay(string sound, object? speaker, int priority) => Cb.Say(sound, speaker, priority);
    public bool CbEmpty => Cb.Empty;
    public void CbKill() => Cb.Kill();
    public void CbStop() => Cb.Say(CbRadio.StopName, null, 3);

    // ---- objectives and the end ----
    public void ObjectiveSuccess(int n) => Objectives.Success(n);
    public void ObjectiveFail(int n) => Objectives.Fail(n);
    public void ObjectiveReveal(int n) => Objectives.Reveal(n);
    public void EndMission(bool success, float delay, int failMessage) => End = (success, Now + delay, failMessage);

    // ---- vehicles ----
    /// <summary>Onto the nav point's ground, facing angle (degrees, yaw), moving at speed along the heading.</summary>
    public float? GroundHeight(float x, float z) => World.Ground.BareHeight(x, z);

    public void Teleport(object o, Vector3 at, int angleDegrees, float speed)
    {
        var e = E(o);
        int a = angleDegrees > 179 ? angleDegrees - 360 : angleDegrees;
        var rot = Rows.Euler(0, a * 0.017445918f, 0);
        if (e.Vehicle is not { } v) { e.StaticPosition = at; e.StaticRot = rot; return; }
        v.Pos = new Double3(at.X, at.Y, at.Z);
        v.Rot = rot;
        v.Steer = v.Throttle = 0;
        v.Vel = rot.Forward * speed;
        v.Speed = speed;
        v.AngVel = Vector3.Zero;
        if (IsHumanEntity(e)) { _playerDrivenTick = FsmTick; Moved(e); }   // the script moved the player
        else TeleportExtras(e);
    }

    /// <summary>The damage model, which destroy and the deaths go through.</summary>
    public Damage.DamageModel? Damage { get; set; }

    /// <summary>destroy: for a vehicle, or for a structure.</summary>
    public void Destroy(object o)
    {
        var e = E(o);
        if (e.Dead) { Log($"destroy: {e.Name} is already dead"); return; }
        if (e.Vehicle is { } v && Damage is { } d) { d.Destroy(v); v.Throttle = -1; }   // and the throttle command = −1
        else if (e.Static is { } s && Damage is { } d2) d2.DestroyStructure(s);
        else e.Dead = true;
    }

    /// <summary>A vehicle's death reaches the script: its CB lines go, its AI stops.</summary>
    public void Died(Entity e)
    {
        if (e.Vehicle is { } v) World.Ai.Remove(v);
        // co-op: the group's lines go only with the last human
        if (Coop is { } c && c.SeatOf(e) is not null)
        {
            if (!Players.Any()) foreach (var s in c.Seats) Cb.SpeakerDied(s.Entity);
            return;
        }
        Cb.SpeakerDied(e);
    }

    public void StartCar(object o)
    {
        foreach (var x in WithExtras(o).Skip(1)) StartCar(x);
        var e = E(o);
        if (e.Vehicle is not { } v || ((uint)v.Flags & 9) != 0) return;
        // the engine-start sound plays and the timer runs its length then sets bit 0
        if (Damage is { } d) d.Ignite(v);
        else { v.Flags |= VehicleFlags.Started | (VehicleFlags)0x80000000; v.Timer = 0; }
        CarStarted?.Invoke(e);
    }

    /// <summary>setLights: toggle when the state differs.</summary>
    public void SetLights(object o, bool on)
    {
        foreach (var e in WithExtras(o))
        {
            e.Lights = on;
            if (e.Vehicle is { } v) v.Flags = on ? v.Flags | VehicleFlags.Lights : v.Flags & ~VehicleFlags.Lights;
        }
    }
    public void SetSiren(object o, bool on) { foreach (var e in WithExtras(o)) e.Siren = on; }
    /// <summary>triggerGate (opcode 63): the object's first type-7 node starts sliding.</summary>
    public void TriggerGate(object o)
    {
        var e = E(o);
        if (e.Static is not { } s || !s.TriggerGate()) { Log($"triggerGate {e.Name}: no gate"); return; }
        Log($"triggerGate {e.Name} at {e.Position.X:0},{e.Position.Z:0}{(s.Nodes.FirstOrDefault(n => n.Gate is not null)?.Gate is { } g ? $" (gate at {g.X:0.00}, target {g.Target:0})" : "")}");
    }

    // ---- camera, keys, control ----
    public void Camera(string op, IReadOnlyList<int> args, IReadOnlyList<object?> objects, IReadOnlyList<Vector3>? path)
    {
        switch (op.ToLowerInvariant())
        {
            case "pushcam": View.Push(); break;
            case "popcam": View.Pop(); break;
            default: View.Set(op, [.. args], objects.Select(x => x as Entity).ToArray(), path); break;
        }
    }
    public bool CameraArrived() => View.ConsumeArrived();
    public bool SkipPressed() { SkipPolled = FsmTick; return SkipKey(); }

    /// <summary>The tick the script last asked isKeypress: Esc is its skip key then, not the menu (the original consumes the key).</summary>
    public int SkipPolled { get; private set; } = int.MinValue;

    /// <summary>
    /// Control (ebrake and reverse from the last queued entry, or from the car when the queue is empty)
    /// and controlAll, both through: braccel or steer outside ±100 → 0, ebrake not 0 or 1 → 0, reverse
    /// not ±1 → 1. The queue is a ring of 100: the hundredth entry makes it look empty again.
    /// </summary>
    public void Control(object o, int braccel, int steer, int? ebrake, int? reverse)
    {
        var e = E(o);
        bool any = e.Controls.Count > 0;
        int eb = ebrake ?? (any ? e.Controls.Last().EBrake : e.Vehicle?.Handbrake ?? 0);
        int rv = reverse ?? (int)(any ? e.Controls.Last().Reverse : e.Vehicle?.Gear ?? 1f);   // ftol
        if (braccel is < -100 or > 100) { Log($"FSM enqueue braccel {braccel} illigal value"); braccel = 0; }
        if (steer is < -100 or > 100) { Log($"FSM enqueue steer {steer} illigal value"); steer = 0; }
        if (eb is not (0 or 1)) { Log($"FSM enque e brake {eb} illiagal value"); eb = 0; }
        float rev = rv is -1 or 1 ? rv : 1f;
        if (rv is not (-1 or 1)) Log($"FSM enqueue reverse {rv} illigal value");
        e.Controls.Enqueue((braccel * 0.01f, steer * 0.01f, rev, eb));
        if (e.Controls.Count == 100) { Log("Enqueue caused queue overflow"); e.Controls.Clear(); }
        e.ControlDone = false;
    }

    /// <summary>driveControl: one queued entry into the controls; an empty queue releases them (control mode 3).</summary>
    public void DriveControl(object o)
    {
        var e = E(o);
        if (e.Vehicle is not { } v) return;
        if (e.Dead) { if (IsHumanEntity(e)) _playerDrivenTick = FsmTick; return; }   // A dead car only latches
        if (e.Controls.TryDequeue(out var c))
        {
            e.ScriptDriven = true;    // the AI keeps off while the script steers
            World.Ai.Remove(v);
            v.Throttle = c.BrAccel; v.Steer = c.Steer; v.Gear = c.Reverse; v.Handbrake = c.EBrake;
            if (e.Controls.Count == 0) e.ControlDone = true;
            if (IsHumanEntity(e)) { _playerDrivenTick = FsmTick; Moved(e); }
            return;
        }
        v.Throttle = v.Steer = 0; v.Handbrake = 0; v.Gear = 1; v.DriveState = 3;
        e.ControlDone = true;
        e.ScriptDriven = false;
    }
    public bool ControlDone(object o) => E(o).ControlDone;
    public void PlayMovie(string name) => Movie?.Invoke(name);

    /// <summary>salvage: the file joins the mission's salvage pool (health −1, random condition); the trip flow listens.</summary>
    public event Action<string>? Salvaged;
    public void Salvage(string file) => Salvaged?.Invoke(file);

    /// <summary>
    /// canSee (Nitro): from a + 1.5 up to b + 1.5 up, the terrain first, then every placed object
    /// with a collider but the vehicles and b itself.
    /// </summary>
    public bool CanSee(object a, object b)
    {
        var p = E(a).Position + new Vector3(0, 1.5f, 0);
        var q = E(b).Position + new Vector3(0, 1.5f, 0);
        int n = System.Math.Max(2, (int)(Vector3.Distance(p, q) / 5f));
        for (int i = 1; i < n; i++)
        {
            var x = Vector3.Lerp(p, q, i / (float)n);
            if (x.Y < World.Ground.BareHeight(x.X, x.Z)) return false;
        }
        if (World.Ground is not Collision.IHasStatics hs) return true;
        var d = q - p;
        foreach (var so in hs.World.Objects)
        {
            if (!so.Solid || so.Vehicle is not null || ReferenceEquals(so, E(b).Static) || so.Polys.Count == 0) continue;
            if (Collision.StaticWorld.RayMesh(so.Polys, p, d, 1f).Item2 is not null) return false;
        }
        return true;
    }
}
