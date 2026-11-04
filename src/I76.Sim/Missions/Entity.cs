using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// A placed object the mission script can name: a simulated vehicle or a
/// piece of scenery, bound by the placement's raw 8-byte name (the entity
/// registry the original fills). Carries the AI-block fields the FSM opcodes
/// read and write.
/// </summary>
public sealed partial class Entity(string name, byte[] id, uint type, uint objectFlags)
{
    public string Name { get; } = name;
    /// <summary>The ODEF record's raw name field, high bits included: the instance id.</summary>
    public byte[] Id { get; } = id;
    /// <summary>The ODEF type: 1 vehicle, 2 building, 3 small object, 4 scenery, 11/12 structures …</summary>
    public uint Type { get; } = type;
    /// <summary>The ODEF flags: bit 4 the player; bits 16–18 the team.</summary>
    public uint ObjectFlags { get; } = objectFlags;
    /// <summary>(obj.flags >> 16) &amp; 7: 1 the player's side, 2 the enemy, 0 nobody's. A network melee's team 8 (Men In Black) is <see cref="Team8"/>.</summary>
    public int Team => (ObjectFlags & (Team8 | 0x10)) == (Team8 | 0x10) ? 8 : (int)((ObjectFlags >> 16) & 7);

    /// <summary>
    /// I26's mark for a network player on the eighth team (Men In Black). The original kept a team in three bits as 0–7,
    /// a free for all being decided per machine; here 0 is nobody's (the free for all and the AI fill), so the teams are
    /// 1–8 and the eighth needs a bit of its own, read only with the player bit (0x10). Only <see cref="TeamFlags"/> sets it.
    /// </summary>
    public const uint Team8 = 0x8000_0000;

    /// <summary>The object flags' team bits for a network player's team (0 nobody's, 1–8).</summary>
    public static uint TeamFlags(int team) => team == 8 ? Team8 : (uint)(team & 7) << 16;

    /// <summary>The object class; 9 is the helicopter the AI special-cases.</summary>
    public bool IsHeli => Vehicle is { } v ? v.ObjectClass == 9 : Type == 9;

    public Vehicle? Vehicle { get; set; }
    public Vector3 StaticPosition { get; set; }
    public Rows StaticRot { get; set; } = Rows.Identity;
    public Vector3 Position => Vehicle?.Pos.ToVector3() ?? StaticPosition;
    public Rows Rot => Vehicle?.Rot ?? StaticRot;

    /// <summary>The placement's scenery collider, for structures.</summary>
    public Collision.StaticObject? Static { get; set; }
    private bool _dead;
    /// <summary>flags &amp; 0x200 on the object (the dead flag for a vehicle; a destroyed structure).</summary>
    public bool Dead
    {
        get => Vehicle is { } v ? (v.Flags & VehicleFlags.Dead) != 0 : _dead || Static?.Destroyed == true;
        set => _dead = value;
    }
    /// <summary>Gone from the world; scripts lose the handle for good.</summary>
    public bool Removed { get; set; }
    /// <summary>The health percentage.</summary>
    public float Health => Vehicle?.Heli is { } heli ? heli.HealthPercent : Vehicle?.Emplacement is { } gun ? gun.HealthPercent : Vehicle?.Health is { } h ? h.HealthPercent() : Static is { } s ? (s.Destroyed ? 0 : 100f * s.Hp / System.Math.Max(1, s.MaxHp)) : _dead ? 0 : 100;

    // The damage-event recorder: FSM ticks of the last hit of each kind.
    public int ShotTick = -10;
    public int RammedTick = -10;
    public int AttackedTick = -10;
    public Entity? LastRammer;
    public Entity? LastShooter;
    public Entity? LastAttacker;
    public bool KilledByPlayer;

    // The standing order (the script's AI order) and what the script reads back.
    public AiOrder? Order;
    public Entity? OrderTarget;
    public Entity? FollowRef;
    public IReadOnlyList<Vector3>? Path;
    public float OrderSpeed;
    public float FollowF1;
    public float FollowF2;
    public int FollowFlag;
    public bool Arrived;
    public bool AtFollow;
    public AiBlock Ai { get; } = new();
    // The Ptable
    public readonly List<int> Stack = [];
    public int Depth = -1;
    public Entity? LastTarget;
    public IReadOnlyList<Vector3>? NavPath;
    public int NavIndex;
    public readonly List<Vector3> Waypoints = [];
    public int WaypointIndex;
    /// <summary>2 not started, 1 searching, 0 path ready.</summary>
    public int PlanState = 2;
    public bool ReachedLeg;
    public bool ReachedEnd;
    public float WeaveTimer;
    public bool Weave;
    /// <summary>Mission time of the last damage taken from a vehicle.</summary>
    public float LastDamageTime = -100;
    public int SkillA = 3, SkillB = 3;
    /// <summary>The setAgg level 0..4 (the original starts it at 4).</summary>
    public int Aggression = 4;
    /// <summary>Collision avoidance registers a helicopter (type 9) without it.</summary>
    public bool Avoid = type != 9;
    /// <summary>How many may attack this vehicle at once (the original sets 1 at registration).</summary>
    public int MaxAttackers = 1;

    // The combat tactics
    /// <summary>The tactic's deadline (rule timeUp).</summary>
    public float TacticDeadline;
    /// <summary>The target this vehicle holds an attacker slot on.</summary>
    public Entity? AttackCommit;
    /// <summary>The road war's road record.</summary>
    public readonly RoadPos Road = new();
    /// <summary>A tactic's stage (rockford, dirt_standoff, tactic_skid).</summary>
    public int TacticStage;
    /// <summary>Rockford's saved forward row.</summary>
    public Vector3 SavedForward;
    /// <summary>Tactic on deck's mode (0 flee, 1 side/miner).</summary>
    public int OnDeckMode;
    /// <summary>The last time the target was in sight (handle los).</summary>
    public float LosSeen;

    // The escape states
    /// <summary>The escape timer (rule clsnTime); the avoid predicates also write their result here.</summary>
    public float EscapeTimer;
    public bool AvoidSkip;
    public float AvoidSpeed;
    public float BackSteer;
    public float AvoidStart;
    /// <summary>The avoid test's time to impact.</summary>
    public float ImpactTime;
    /// <summary>The avoid test's obstacle.</summary>
    public Entity? Obstacle;
    public float AvoidSteer;
    public float BackHeading;
    public float BackHeight;
    /// <summary>The boxed-in ring of forward clearances, by FSM tick % 4.</summary>
    public readonly float[] ClearanceRing = [1f, 1f, 1f, 1f];
    /// <summary>
    /// The port's own (#42, docs/deviations.md): how long the boxed-in ring has read low while the car asked to go
    /// forward (throttle above 0) with nothing in its way, in seconds; 0 otherwise.
    /// </summary>
    public float StallTime;
    /// <summary>The port's own (#42): where back_away started and the farthest it has got from there, to tell a car that backs away from one that cannot.</summary>
    public Vector3 BackStart;
    public float BackFarthest;
    /// <summary>−10 from every planner tick; the damage recorder writes the time.</summary>
    public float PlanStamp;
    /// <summary>The private one-point nav path handle too far search aims at.</summary>
    public Vector3[] SnapshotPath = new Vector3[1];
    /// <summary>The planned waypoint's bridge flag (a node flagged 2); the road flag has no reader here.</summary>
    public readonly List<bool> WaypointBridge = [];
    /// <summary>The A* search in progress (expansions, open list).</summary>
    public Ai.PathSearch? Search;

    /// <summary>The control ring (100 entries): braccel, steer, reverse, ebrake.</summary>
    public readonly Queue<(float BrAccel, float Steer, float Reverse, int EBrake)> Controls = new();
    public bool ControlDone = true;   // the AI block's default
    /// <summary>The frame's driveControl took the wheel from the AI.</summary>
    public bool ScriptDriven;

    /// <summary>
    /// The port's own: a road route may be at most this many times the straight line (plus 100 m) before the A* is used instead;
    /// 0, the original's, takes any road route. Set for a network melee's bots, whose race and capture-the-flag routes cross a road's
    /// open seam (docs/multiplayer.md "Bots").
    /// </summary>
    public float DetourLimit;

    /// <summary>
    /// How near (across the ground) a path follow comes to a point of its path before it moves on to the next: 40 m, the original's.
    /// The port's own: a network race's bots set it lower every frame, so that a bot drives into a checkpoint's 30 m sphere before it
    /// turns for the next one (docs/multiplayer.md "Bots").
    /// </summary>
    public float NavReach = DefaultNavReach;
    public const float DefaultNavReach = 40f;

    /// <summary>The path a network melee gave this entity (a bot's route or goal): <see cref="Path"/> and <see cref="NavPath"/> may be this very list, which a savestate then names by the entity.</summary>
    public List<Vector3>? OwnPath;

    public bool Lights, Siren;

    /// <summary>
    /// Co-op (docs/multiplayer.md "Co-op"): the human an order at "the player" last went to, kept until another human
    /// is clearly nearer (<see cref="ScriptHost.NearestHuman"/>); and the seat of the human whose hit set
    /// <see cref="KilledByPlayer"/>, −1 for none.
    /// </summary>
    public Entity? CoopAim;
    public int KillerSeat = -1;

    public override string ToString() => Name;
}
