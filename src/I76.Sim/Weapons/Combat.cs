using System.Numerics;
using I76.Formats.Bwd2;
using I76.Sim.Ai;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Vehicles;

namespace I76.Sim.Weapons;

/// <summary>A damage packet: attacker, channel mask (1 ballistic, 2 explosive, 4 fire, 8 impact), flags, four amounts.</summary>
public sealed class DamagePacket
{
    public object? Attacker;
    public ushort Mask;
    /// <summary>0x1 the .45 (kind 0x12); 0x10 bypasses armour (kind 0x16).</summary>
    public ushort Flags;
    public readonly int[] Ch = new int[4];
    /// <summary>Not in the original packet: what made it, for the mission script's recorder.</summary>
    public I76.Sim.Damage.DamageKind Kind;
    public int Total => Ch[0] + Ch[1] + Ch[2] + Ch[3];
}

/// <summary>Where damage goes: the vehicle and structure damage slots (class slot [10]).</summary>
public interface IDamageSink
{
    void Apply(object victim, DamagePacket p, Vector3 hitLocal);
    /// <summary>A hit on a structure's node (<see cref="StaticObject.Nodes"/>; −1 the root).</summary>
    void Apply(object victim, DamagePacket p, Vector3 hitLocal, int node) => Apply(victim, p, hitLocal);
    /// <summary>A caltrop under a car (CaltropTyreHit, from the caltrop round's probe): the tyre nearest <paramref name="at"/> goes down.</summary>
    void CaltropTyreHit(Vehicle v, Vector3 at) { }
}

/// <summary>A round in flight (the original's ordnance state).</summary>
public sealed class Round
{
    public required uint Kind;
    public required Vehicle Shooter;
    public required GizmoDef Def;
    public required OrdnanceDef Ord;
    public Vector3 Pos;
    public Rows Rot;
    /// <summary>Velocity in the round's own frame.</summary>
    public Vector3 LocalVel;
    public float Life, Age, Travelled;
    public int Count = 1;
    public int Damage;
    public object? Target;
    public Vector3 PrevPos;
    /// <summary>Mines and Blox: world velocity while tumbling, and whether they have come to rest.</summary>
    public Vector3 Vel;
    public bool Launched, Settled;
    /// <summary>The session's stable id (0 until a session assigns one; never reused within it).</summary>
    public int NetId;
}

public abstract record CombatEvent;
public sealed record ShotFired(Vehicle Shooter, Weapon Weapon, Vector3 Muzzle, Vector3 Forward) : CombatEvent;
/// <summary>A round's hit: At is the effect's place (bullets back off 2 m), Hit the struck point, N its normal, Dir the flight.</summary>
public sealed record Impact(string Effect, string Sound, Vector3 At, object? Victim, uint Kind = 0, Vector3 Hit = default, Vector3 N = default, Vector3 Dir = default) : CombatEvent;
public sealed record Explosion(EffectDef Effect, Vector3 At) : CombatEvent;
public sealed record WeaponClick(Vehicle Shooter, string Sound) : CombatEvent;

/// <summary>
/// Weapons, rounds and hits, once per
/// frame: rounds fly and test their path, then turrets aim and
/// the fire-control timers fire.
/// The original runs this on the raw frame delta; play mode feeds the fixed tick.
/// Flame throwers and droppers (oil, fire) are in Combat.Droppers.cs.
/// </summary>
public sealed partial class Combat(SimWorld world, Func<string, EffectDef?> effects)
{
    /// <summary>The .xdf effects by name (cached by the loader).</summary>
    public Func<string, EffectDef?> Effects => effects;
    public SimWorld World { get; } = world;
    private CrtRand Rand => World.Rand;
    public IDamageSink? Damage { get; set; }
    public List<Round> Rounds { get; } = [];
    public List<CombatEvent> Events { get; } = [];
    /// <summary>A vehicle's side ((obj.flags >> 16) &amp; 7); same-side vehicles are not radar targets.</summary>
    public Func<Vehicle, int> Team { get; set; } = _ => 0;
    /// <summary>
    /// A melee bot's car (the server's AI driving a player's seat, docs/multiplayer.md "Bots"). It is a human car for the
    /// world (player physics and damage, a dead car left for RAISE_DEAD), but its weapons fire as an AI car's: its fire
    /// decision never asks for a missile lock (nobody holds the selection a lock needs).
    /// </summary>
    public Func<Vehicle, bool> IsBot { get; set; } = _ => false;
    /// <summary>The radar's current target per vehicle: the player picks it, the AI's orders set it.</summary>
    public Dictionary<Vehicle, Vehicle?> RadarTarget { get; } = [];
    /// <summary>The radar's range per vehicle (radar +0): 0 is 100 m, 1 is 600 m, the default; R flips it. Display state, not hashed.</summary>
    public Dictionary<Vehicle, int> RadarRange { get; } = [];
    /// <summary>The radar camera's flag per vehicle; W flips it, only with a target selected.</summary>
    public HashSet<Vehicle> RadarCamera { get; } = [];
    /// <summary>radar_range_reset: the range field back to 1, the long (600 m) range.</summary>
    public void ResetRadarRange(Vehicle me) => RadarRange[me] = 1;

    /// <summary>R: the range flips between 100 m and 600 m. The original acts only while the radar is on; the radar part is always on here.</summary>
    public void ToggleRadarRange(Vehicle me) => RadarRange[me] = RadarRange.GetValueOrDefault(me, 1) == 0 ? 1 : 0;
    /// <summary>W: the radar camera's flag flips, with a target selected only.</summary>
    public void ToggleRadarCamera(Vehicle me)
    {
        if (RadarTarget.GetValueOrDefault(me) is null) return;
        if (!RadarCamera.Remove(me)) RadarCamera.Add(me);
    }
    /// <summary>
    /// The difficulty (Play Options' Difficulty Level: 0 Wimp, 1 Champion, 2 Badass), the factor on the player's own rounds
    /// and flame (<see cref="DifficultyFactor"/>). The game sets it from the settings at the mission's start, on resuming from
    /// the pad and over a checkpoint's copy.
    /// </summary>
    public int Difficulty { get; set; } = 1;

    /// <summary>A network game, melee or co-op, server or client: the difficulty's factor is 1 there.</summary>
    public bool NetworkGame => NetSession || RemoteSim || World.Coop;

    /// <summary>
    /// The factor on a round's or a flame's damage by its shooter: a human's ×2.0 at Wimp, ×1.0 at Champion, ×0.75 at
    /// Badass; anyone else's, and everyone's in a <see cref="NetworkGame"/> (the original applies it outside a network
    /// session only), ×1.
    /// </summary>
    public float DifficultyFactor(Vehicle shooter) =>
        NetworkGame || !World.IsHuman(shooter) ? 1f : Difficulty switch { 0 => 2f, 2 => 0.75f, _ => 1f };
    public bool UnlimitedAmmo { get; set; }
    /// <summary>
    /// The Nitro Pack's rules where its weapon code differs from the base game's: the Chemical Mortar kills the driver
    /// (<see cref="Damage.VehicleDamage.ComponentLoop"/>), the droppers' rate follows the speed (<see cref="Timing"/>).
    /// Set by the mission build from the install.
    /// </summary>
    public bool NitroRules { get; set; }
    public HashSet<uint> Unmodelled { get; } = [];

    /// <summary>
    /// Sounds the original plays on the local player alone: cammo.wav when the
    /// selection or the link changes, clockon.wav when a radar missile is
    /// launched at it (the round's contrail). They ride a <see cref="WeaponClick"/> on that car; the views play them for
    /// its own driver only.
    /// </summary>
    public static bool IsDriverSound(string sound) =>
        sound.Equals("cammo.wav", StringComparison.OrdinalIgnoreCase) || sound.Equals("clockon.wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>A class-1 weapon fired (vehicle, tier): tiers 1 and 2 are the crew channels, whose arm plays its shot (<see cref="Vehicle.Arms"/>).</summary>
    public Action<Vehicle, int>? HandgunShot { get; set; }

    public static bool IsDead(Vehicle v) => (v.Flags & VehicleFlags.Dead) != 0;

    public void Frame(float dt)
    {
        FrameDt = dt;
        foreach (var k in _nextTimer.Keys.ToList()) _nextTimer[k] = System.Math.Max(0, _nextTimer[k] - dt);
        FindJammers();
        DropLostTargets();
        UpdateArcs();
        BombletFrame(dt);
        FlyRounds(Rounds, dt);
        LockTone.Clear();
        LockedOn.Clear();
        foreach (var v in World.Vehicles)
        {
            if (v.Armory is not { } a) continue;
            AimTurrets(v, a, dt);
            AimFixed(v, a, dt);
            Lock(v, a, dt);
            if (NetSession && World.IsHuman(v)) Jam(v, a, dt);
            foreach (var w in a.Weapons) Timing(v, w, dt);
            foreach (var w in a.Handguns) if (w is not null) Timing(v, w, dt);
        }
        FlameFrame(dt);
    }

    /// <summary>
    /// the network branch, after the triggers and before the fire timers, for each machine's own player
    /// (here every human's car): of its hardpoint weapons (not the handguns) with the trigger set, one with no ammo
    /// lets go (so the network never hears wclick.wav), and a damaged one jams with p = min(0.5, (1 − HP/max) − 0.3):
    /// one rand() each while p &gt; 0, and on a jam the trigger is cleared and ammo −= ftol(dt·rate), which can take it
    /// below 0 (the next shot clamps it). The server's alone: a client's prediction fires and the server corrects it.
    /// </summary>
    private void Jam(Vehicle v, Armory a, float dt)
    {
        foreach (var w in a.Weapons)
        {
            if (!w.Trigger) continue;
            if (w.Ammo == 0) { w.Trigger = false; continue; }
            float p = 1f - w.HpRatio - 0.3f;
            if (p > 0.5f) p = 0.5f;
            if (p > 0 && Rand.Next() < p * 32767f)
            {
                w.Trigger = false;
                w.Ammo -= (int)(dt * Rate(v, w.Def));
            }
        }
    }

    private static bool Dropper(GizmoDef def) => def.Kind is 0xc or 0x11 or 0xe;

    /// <summary>
    /// A weapon's rate, shots a second. The Nitro Pack overwrites a dropper's (oil, fire-drop,
    /// caltrops) with 0.18·speed + 1.2 before each of its shots, from the car about to drop.
    /// </summary>
    private float Rate(Vehicle v, GizmoDef def) => NitroRules && Dropper(def) ? v.Speed * 0.18f + 1.2f : def.Rate;

    // ---- a network client's own car (docs/multiplayer.md, "Own shots at once") ----

    /// <summary>A network client's own rounds, fired at once from its predicted car (<see cref="PredictFrame"/>): drawn, never harmful.</summary>
    public List<Round> PredictedRounds { get; } = [];
    /// <summary>A predicted shot on a fresh tick, with the round it left (null for a flame, or none).</summary>
    public Action<Weapon, Round?>? PredictedShot { get; set; }
    /// <summary>Drawing only: no damage, no pushes, no impact or explosion events (a client's predicted rounds, the flames it builds from snapshots).</summary>
    private bool _cosmetic;
    /// <summary>A replay of a predicted tick: the timers, ammo and aims run, nothing is fired again.</summary>
    private bool _silent;
    private IDamageSink? Sink => _cosmetic ? null : Damage;

    /// <summary>
    /// One tick of one car's weapons as <see cref="Frame"/> runs them for it (its arcs, turret and gun aims, the
    /// fire-control timers, its flame streams), for a network client's prediction of its own car: its shots go into
    /// <see cref="Events"/> and its rounds into <see cref="PredictedRounds"/>, which fly here and harm nothing. A replay
    /// (<paramref name="fresh"/> false) runs the timers and the ammo only. The server's rounds are not touched.
    /// </summary>
    public void PredictFrame(Vehicle v, float dt, bool fresh)
    {
        var frameDt = FrameDt;
        FrameDt = dt;
        _cosmetic = true;
        _silent = !fresh;
        try
        {
            if (fresh) FlyRounds(PredictedRounds, dt);
            UpdateArcs(v);
            if (fresh) LockTone.Remove(v);
            if (v.Armory is { } a)
            {
                AimTurrets(v, a, dt);
                AimFixed(v, a, dt);
                Lock(v, a, dt);
                foreach (var w in a.Weapons) Timing(v, w, dt);
                foreach (var w in a.Handguns) if (w is not null) Timing(v, w, dt);
            }
            if (fresh) FlameFrame(dt, v);
        }
        finally
        {
            _cosmetic = _silent = false;
            FrameDt = frameDt;
        }
    }

    /// <summary>A flame stream built for drawing only (a network client's copy of another car's, from the snapshot): no damage, no events.</summary>
    public void BuildFlameView(Flame f, float dt)
    {
        _cosmetic = true;
        try { BuildFlame(f, dt); }
        finally { _cosmetic = false; }
    }

    // ---- targets ----

    /// <summary>The melee flag: a melee (instant or network) is running; T may then pick team 0.</summary>
    public bool MeleeActive { get; set; }
    /// <summary>A network session. E may pick team 0 only then; every car's radar fills its own arcs.</summary>
    public bool NetSession { get; set; }
    /// <summary>
    /// A network melee without team play: every car is its own side for the radar's T and E. Everyone there is on
    /// team 0 (the humans and the AI fill alike), and the team test alone made no car a candidate for anyone.
    /// </summary>
    public bool FreeForAll { get; set; }

    /// <summary>The radar's reach: contacts are within 600 m on the ground plane.</summary>
    public const float ContactRange = 600f;
    /// <summary>The per-arc target's reach: 650 m, 3D.</summary>
    public const float ArcRange = 650f;
    private const float ArcCos2 = 0.9330127f;   // a 15 degree half-angle

    private readonly Dictionary<Vehicle, float> _nextTimer = [];
    /// <summary>The arc targets (front, rear, right, left), refilled each frame by <see cref="UpdateArcs"/>.</summary>
    private readonly Dictionary<Vehicle, Vehicle?[]> _arcs = [];

    private static float Planar(Vehicle a, Vehicle b)
    {
        double dx = a.Pos.X - b.Pos.X, dz = a.Pos.Z - b.Pos.Z;
        return (float)System.Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>A live vehicle whose engine runs (the engine is a car's test only: type 1); the only kind the radar lists.</summary>
    public static bool IsLive(Vehicle v) => !IsDead(v) && (v.IsHeli || v.IsEmplacement || (v.Flags & VehicleFlags.EngineOn) != 0);

    /// <summary>A network client: a car's jam is its flag from the server's snapshot, not the special's clock (<see cref="Specials.Jamming"/>).</summary>
    public bool RemoteSim { get; set; }

    /// <summary>
    /// Asks the jamming test of every jamming car once a frame, which ends the ones that ran out, so a
    /// jam ends (and its plate goes dark) on time even when no radar asks.
    /// </summary>
    private void FindJammers()
    {
        foreach (var v in World.Vehicles)
            if ((v.Flags & VehicleFlags.Jamming) != 0) Specials.Jamming(v, World.Time, RemoteSim);
    }

    /// <summary>
    /// Whether <paramref name="v"/> is jamming right now: the jamming test itself, as every consumer in the original
    /// calls it, so the emplacements and the planner (which run before <see cref="Frame"/>) see this tick's jam. A car
    /// without the flag costs one bit test.
    /// </summary>
    public bool IsJamming(Vehicle? v) => v is not null && (v.Flags & VehicleFlags.Jamming) != 0 && Specials.Jamming(v, World.Time, RemoteSim);

    /// <summary>
    /// A radar is blank (no list, no target) while its owner jams, or any
    /// live car within 600 m does. The per-arc targets are computed before this and are not touched.
    /// </summary>
    public bool RadarBlank(Vehicle me)
    {
        if (IsJamming(me)) return true;
        foreach (var v in World.Vehicles)
            if (IsJamming(v) && IsContact(me, v)) return true;
        return false;
    }

    /// <summary>
    /// The AI's radar-target update, single player: while the player jams, an AI's radar has no selected target,
    /// so its turrets (cars', helicopters') and guided weapons have none; its fixed guns keep the order target.
    /// The original blanks it the same way while the player's engine is off, whoever the
    /// order's target is, so a wingman's turrets fell silent against its enemies (#37). Here the engine off hides
    /// only the player: an AI whose <paramref name="target"/> is the player loses it, the others keep theirs
    /// (docs/deviations.md).
    /// </summary>
    /// <remarks>
    /// Co-op (docs/multiplayer.md "Co-op"): per human. A human's jammer blinds the radars at that human and at anything
    /// not a human (as the one player's blinds them all), its engine off hides its own car.
    /// </remarks>
    private bool AiRadarBlind(Vehicle me, object? target)
    {
        if (NetSession || World.IsHuman(me)) return false;
        if (!World.Coop) return World.Player is { } p && (IsJamming(p) || (p.Flags & VehicleFlags.EngineOn) == 0 && target == p);
        foreach (var h in CoopHumans())
        {
            if (IsJamming(h) && (target == h || !(target is Vehicle tv && World.IsHuman(tv)))) return true;
            if ((h.Flags & VehicleFlags.EngineOn) == 0 && target == h) return true;
        }
        return false;
    }

    /// <summary>Co-op's humans: the player's car, then the others.</summary>
    private IEnumerable<Vehicle> CoopHumans()
    {
        if (World.Player is { } p) yield return p;
        foreach (var h in World.Humans) if (h != World.Player) yield return h;
    }

    /// <summary>Whether the radar lists <paramref name="v"/> for <paramref name="me"/>.</summary>
    public bool IsContact(Vehicle me, Vehicle v) => v != me && IsLive(v) && Planar(me, v) <= ContactRange;

    /// <summary>The radar's contact list: nearest (planar) first.</summary>
    public List<Vehicle> Contacts(Vehicle me) =>
        RadarBlank(me) ? [] : World.Vehicles.Where(v => IsContact(me, v)).OrderBy(v => Planar(me, v)).ToList();

    /// <summary>
    /// The radar drops its target the frame it stops being a contact (it
    /// died, switched its engine off, or went past 600 m); nothing replaces it.
    /// </summary>
    private void DropLostTargets()
    {
        foreach (var me in World.Vehicles)
            if (World.IsHuman(me) && RadarTarget.GetValueOrDefault(me) is { } t && (!IsContact(me, t) || RadarBlank(me))) { RadarTarget[me] = null; RadarCamera.Remove(me); }
    }

    /// <summary>E / prev_target with the last press more than 5 s ago (the original runs first): the radar camera goes off.</summary>
    public bool NextRestarts(Vehicle me) => _nextTimer.GetValueOrDefault(me) <= 0;

    /// <summary>T's side test: another team; team 0 only in a network session or a melee.</summary>
    private bool NearestCandidate(Vehicle me, Vehicle v) => FreeForAll || Team(v) != Team(me) && (NetSession || MeleeActive || Team(v) != 0);
    /// <summary>E's side test: another team; team 0 only in a network session.</summary>
    private bool NextCandidate(Vehicle me, Vehicle v) => FreeForAll || Team(v) != Team(me) && (NetSession || Team(v) != 0);

    /// <summary>
    /// T: the nearest enemy (planar) and, for the player, the nearest live
    /// gun emplacement of any side (the nearest-emplacement lookup, 3D); the nearer by 3D distance wins.
    /// If that is not a radar contact the target is cleared.
    /// </summary>
    public Vehicle? NearestEnemy(Vehicle me)
    {
        if (RadarBlank(me)) return null;
        Vehicle? best = null; float bd = float.MaxValue;
        foreach (var v in World.Vehicles)
        {
            if (v == me || IsDead(v) || !NearestCandidate(me, v)) continue;
            float d = Planar(me, v);
            if (d < bd) { bd = d; best = v; }
        }
        if (World.IsPlayerSide(me) && NearestGun(me) is { } gun && (best is null || Dist2(me, best) >= Dist2(me, gun))) best = gun;
        return best is not null && IsContact(me, best) ? best : null;
    }

    private static float Dist2(Vehicle a, Vehicle b) => (a.Pos.ToVector3() - b.Pos.ToVector3()).LengthSquared();

    /// <summary>The nearest-emplacement lookup: the nearest live emplacement by 3D distance, any side.</summary>
    public Vehicle? NearestGun(Vehicle me)
    {
        Vehicle? best = null; float bd = float.MaxValue;
        foreach (var v in World.Vehicles)
        {
            if (!v.IsEmplacement || IsDead(v)) continue;
            float d = Dist2(me, v);
            if (d < bd) { bd = d; best = v; }
        }
        return best;
    }

    /// <summary>
    /// The target a weapon of mount kind <paramref name="mountKind"/> aims and shoots at. Turrets (4) take the
    /// radar target. Fixed mounts take their arc's slot: 1 front, 2 rear, 8 right,
    /// 0x10 left (the handguns); any other kind has none.
    /// </summary>
    public Vehicle? TargetFor(Vehicle me, int mountKind)
    {
        if (mountKind == 4)
        {
            var cur = RadarTarget.GetValueOrDefault(me);
            if (AiRadarBlind(me, cur)) return null;
            return cur is not null && IsDead(cur) ? null : cur;
        }
        int slot = mountKind switch { 1 => 0, 2 => 1, 8 => 2, 0x10 => 3, _ => -1 };
        if (slot < 0) return null;
        if (!OwnArcs(me)) return RadarTarget.GetValueOrDefault(me);
        return _arcs.TryGetValue(me, out var a) ? a[slot] : null;
    }

    /// <summary>
    /// An AI's order target when it is a structure (T01's diner, T02's school): the AI's radar-target update writes
    /// the order's target, whatever object it is, into the radar and its four arcs, so the guns aim and fire at it
    /// (measured in the game: T01's attackers fire at the diner from 110 m and its nodes take the rounds).
    /// <see cref="RadarTarget"/> holds null meanwhile.
    /// </summary>
    public Dictionary<Vehicle, StaticObject> StructureTarget { get; } = [];

    /// <summary><see cref="TargetFor"/>, or an AI's <see cref="StructureTarget"/>: a <see cref="Vehicle"/> or a <see cref="StaticObject"/>.</summary>
    public object? AimTargetFor(Vehicle me, int mountKind)
    {
        if (TargetFor(me, mountKind) is { } v) return v;
        if (OwnArcs(me) || !StructureTarget.TryGetValue(me, out var s) || s.Destroyed) return null;
        // the turrets read the radar's selected target, which the player's jamming empties; the arcs keep the target
        if (mountKind == 4) return AiRadarBlind(me, s) ? null : s;
        return mountKind is 1 or 2 or 8 or 0x10 ? s : null;
    }

    /// <summary>An object's collider centre (its entity slot's centre through its transform).</summary>
    private static Vector3 CentreOf(object t) => t is Vehicle v ? ColliderCentre(v) : ((StaticObject)t).Centre;

    /// <summary>Where guided rounds and fixed guns aim, the collider centre; for a structure the ground under it (at the centre's height) + 0.6.</summary>
    private Vector3 AimPointOf(object t)
    {
        if (t is Vehicle v) return AimPoint(v);
        var c = ((StaticObject)t).Centre;
        return new Vector3(c.X, World.Ground.Query(c.X, c.Y, c.Z).Height + 0.6f, c.Z);
    }

    /// <summary>
    /// Outside a network session only the player's radar fills its own arcs; any other's
    /// (the AI's radar-target update) writes its order target into all four, so an AI's fixed guns aim
    /// at what its orders say.
    /// </summary>
    private bool OwnArcs(Vehicle me) => NetSession || World.IsHuman(me);

    /// <summary>
    /// From the radar's update: the front arc always, the rear one for a car with a rear
    /// weapon, a side one only while that side's arm is off its rest (a car without the arm
    /// has none).
    /// </summary>
    private void UpdateArcs()
    {
        foreach (var me in World.Vehicles) UpdateArcs(me);
    }

    private void UpdateArcs(Vehicle me)
    {
        if (!OwnArcs(me) || me.IsEmplacement) { _arcs.Remove(me); return; }
        if (!_arcs.TryGetValue(me, out var a)) _arcs[me] = a = new Vehicle?[4];
        var hg = me.Armory?.Handguns;
        a[0] = ArcPick(me, me.Rot.Forward);
        a[1] = me.Armory?.Weapons.Any(w => w.MountKind == 2) == true ? ArcPick(me, -me.Rot.Forward) : null;
        a[2] = hg?[0] is not null && !me.Arms.Idle(1) ? ArcPick(me, me.Rot.Right) : null;
        a[3] = hg?[1] is not null && !me.Arms.Idle(2) ? ArcPick(me, -me.Rot.Right) : null;
    }

    /// <summary>
    /// The nearest car (object type 1, so no helicopter or emplacement) in the collision
    /// list, by its collider centre, in front of the axis within 15 degrees and 650 m; any side, alive or dead.
    /// </summary>
    private Vehicle? ArcPick(Vehicle me, Vector3 axis)
    {
        Vehicle? best = null; float bd = ArcRange * ArcRange;
        var p = me.Pos.ToVector3();
        foreach (var v in World.Vehicles)
        {
            if (v == me || v.IsEmplacement || v.IsHeli || v.Wreck) continue;
            var d = ColliderCentre(v) - p;
            float dot = Vector3.Dot(d, axis), d2 = d.LengthSquared();
            if (dot < 0 || d2 * ArcCos2 >= dot * dot || d2 >= bd) continue;
            bd = d2; best = v;
        }
        return best;
    }

    /// <summary>E: the next contact outward that is an enemy, wrapping; with the last press more than 5 s ago it starts again from the nearest.</summary>
    public Vehicle? NextTarget(Vehicle me)
    {
        bool restart = _nextTimer.GetValueOrDefault(me) <= 0;
        _nextTimer[me] = 5f;
        var list = Contacts(me);
        if (list.Count == 0) return RadarTarget.GetValueOrDefault(me);
        var cur = restart ? null : RadarTarget.GetValueOrDefault(me);
        int i = cur is null ? -1 : list.IndexOf(cur);
        for (int k = 1; k <= list.Count; k++)
        {
            var v = list[(i + k) % list.Count];
            if (NextCandidate(me, v)) return v;
        }
        return cur;
    }

    /// <summary>
    /// prev_target (unbound by default), with E's 5 s rule. The original walks the contact ring from after the
    /// target round to it, remembering the last enemy it passes, but then stores the walk's end (it writes the wrong register, not
    /// the candidate): with a target selected the target stays as it is; with none (or after the 5 s reset) the first
    /// contact is selected, whatever its side, if the walk found an enemy other than it. Kept as the release has it.
    /// </summary>
    public Vehicle? PrevTarget(Vehicle me)
    {
        bool restart = _nextTimer.GetValueOrDefault(me) <= 0;
        _nextTimer[me] = 5f;
        var list = Contacts(me);
        if (list.Count == 0) return RadarTarget.GetValueOrDefault(me);
        var cur = restart ? null : RadarTarget.GetValueOrDefault(me);
        if (cur is not null) return cur;
        // the walk from the first contact round to it: the last enemy found must not be the first contact itself
        for (int k = list.Count - 1; k >= 0; k--)
            if (NextCandidate(me, list[k])) return k > 0 ? list[0] : null;
        return null;
    }

    /// <summary>Q: the radar's front arc (15 degrees, 650 m, any side), if it is a radar contact; otherwise the target is cleared.</summary>
    public Vehicle? FrontalTarget(Vehicle me) =>
        !RadarBlank(me) && ArcPick(me, me.Rot.Forward) is { } v && IsContact(me, v) ? v : null;

    // ---- the AI's trigger ----

    /// <summary>
    /// Whether an AI shooter fires a weapon at a target this frame.
    /// p3 is the skill, p4 the accuracy. Guns and
    /// unguided rockets: an intercept test against the target's motion with
    /// a skill-dependent rejection and an accuracy-dependent timing error,
    /// a tolerance by range, line of sight, and the friendly-fire hold.
    /// Guided missiles: a cone and range test. Mortars: a fixed-time landing
    /// test. Flame throwers: 20 m and a 0.6 cone. Droppers: the target about to
    /// drive over the shooter's spot (AiDrop).
    /// </summary>
    public bool AiShouldFire(Vehicle shooter, Vehicle target, Weapon w, float p3, float p4) =>
        AiShouldFire(shooter, target, target.Pos.ToVector3(), target.Rot.Forward, w, p3, p4);

    /// <summary>
    /// The fire decision at a structure: the target is still, its aim point its collider centre, the guns' tolerance 20
    /// (the non-vehicle case); <paramref name="pos"/> and <paramref name="forward"/> are its object's position and heading.
    /// </summary>
    public bool AiShouldFire(Vehicle shooter, StaticObject target, Vector3 pos, Vector3 forward, Weapon w, float p3, float p4) =>
        AiShouldFire(shooter, (object)target, pos, forward, w, p3, p4);

    private bool AiShouldFire(Vehicle shooter, object target, Vector3 tpos, Vector3 tfwd, Weapon w, float p3, float p4)
    {
        var def = w.Def;
        var rot = Rows.Concat(w.MuzzleRot, shooter.Rot);
        var f = rot.Forward;
        var m = shooter.Pos.ToVector3() + shooter.Rot.ToWorld(w.MuzzlePos);
        var tc = CentreOf(target);
        var tv = target as Vehicle;
        var tvel = tv?.Vel ?? default;
        uint tier = def.Tier;
        bool guided = def.Class == 4 && tier is 1 or 2 or 3 or 4 or 0x65 or 0x66 or 0x67;
        if (def.Class is 1 or 2 || (def.Class == 4 && !guided))
        {
            if (Rand.Next() % 1000 + Rand.Next() % 1000 + Rand.Next() % 1000 > p3 * 6000) return false;
            float speed = def.Ordnance?.TerminalSpeed ?? def.Speed;
            var wv = shooter.Vel + f * speed;
            float miss = Miss(m, tc, tvel, wv, p4, out float t);
            if (t < 0 || t >= 4f) return false;
            float tol = 20;
            if (tv is not null)
            {
                float range = new Vector2(tc.X - m.X, tc.Z - m.Z).Length();
                tol = range < 12 ? 3 : range < 25 ? 6 : 10;
                if (def.Class == 4) tol *= 0.7f;
            }
            if (miss >= tol || !LineOfSight(m, tc)) return false;
            foreach (var o in World.Vehicles)
            {
                if (o == shooter || o == target || IsDead(o) || Team(o) == 0 || Team(o) != Team(shooter)) continue;
                var (oc, _) = Collider(o);
                float od = Vector3.Distance(oc, m);
                if (od > 100) continue;
                float om = Miss(m, oc, o.Vel, wv, 1f, out float ot);
                if (ot < 0) continue;
                if ((om < 8 && od >= 8) || om < 2) return false;
            }
            return true;
        }
        if (guided)
        {
            if (Rand.Next() % 5000 > p3 * p3 * 1000) return false;
            var d = tc - m;
            float d2 = d.LengthSquared();
            if (d2 > 62500) return false;
            float cos = Vector3.Dot(Vector3.Normalize(d), f);
            if (cos <= (d2 < 625 ? 0.95f : 0.85f)) return false;
            if (tier is 1 or 0x65 && MathF.Abs(Vector3.Dot(tfwd, f)) <= (d2 < 625 ? 0.7f : 0.75f)) return false;
            return LineOfSight(m, tc);
        }
        if (def.Class == 3 && tier != 3)
        {
            float t = tier == 0 ? 0.3f : tier == 2 ? 0.4f : 0.25f;
            // the shooter advances by its velocity x t; the target is predicted t + 0.09 s ahead (TargetPredictor)
            var land = shooter.Pos.ToVector3() + shooter.Vel * t + f * 80f;
            var at = tv is not null ? TargetPredictor.Predict(tv, t) : tc;
            float r2 = tier == 2 ? 150 : 400;
            return (land.X - at.X) * (land.X - at.X) + (land.Z - at.Z) * (land.Z - at.Z) < r2;
        }
        if (def.Class == 5)
        {
            // flame: the objects within 20 (3D), and the muzzle's forward on the aim point, the dot normalised
            // by the objects' distance rather than the muzzle's (as the original has it)
            float o2 = (shooter.Pos.ToVector3() - tpos).LengthSquared();
            if (o2 > 400f) return false;
            if (o2 > -0.0001f && o2 < 0.0001f) return true;
            return Vector3.Dot(tc - m, f) / MathF.Sqrt(o2) > 0.6f;
        }
        if (def.Class == 6) return tv is not null && AiDrop(shooter, tv);
        return false;
    }

    /// <summary>The intercept miss: t = ((M−T)·(Vt−w)) / |Vt−w|², jittered by the accuracy, clamped ≥ 0.</summary>
    private float Miss(Vector3 m, Vector3 tpos, Vector3 tvel, Vector3 wv, float p4, out float t)
    {
        var rv = tvel - wv;
        float rr = rv.LengthSquared();
        t = rr < 0.01f ? 1e7f : Vector3.Dot(m - tpos, rv) / rr;
        if (p4 < 1f) t -= (1 - p4) * 1.1f * (Rand.Next() % 1000 - 500) * -0.0005f;
        else Rand.Next();
        if (t < 0) t = 0;
        return Vector3.Distance(m + wv * t, tpos + tvel * t);
    }

    /// <summary>A terrain line of sight (the original also tests non-vehicle objects; not here).</summary>
    public bool LineOfSight(Vector3 a, Vector3 b)
    {
        float len = Vector3.Distance(a, b);
        int n = System.Math.Max(2, (int)(len / 5f));
        for (int i = 1; i < n; i++)
        {
            var p = Vector3.Lerp(a, b, i / (float)n);
            if (p.Y < World.Ground.BareHeight(p.X, p.Z)) return false;
        }
        return true;
    }

    // ---- fire control ----

    /// <summary>Turrets slew toward the radar target's lead point at dt·(0.75 + 0.25·HP/max)·1.7 rad, or back to the mount's forward without one.</summary>
    private void AimTurrets(Vehicle v, Armory a, float dt)
    {
        foreach (var w in a.Weapons.Where(w => w.Style == 3))
        {
            var t = AimTargetFor(v, 4);
            // with no target the aim is the mount's own forward: the turret slews back to rest (0, 0) at the same rate
            float yaw = 0, pitch = 0;
            if (t is not null)
            {
                // from the gun node's origin (the original composes it up to the world), which the aim does not move
                var muzzle = v.Pos.ToVector3() + v.Rot.ToWorld(w.TurretPivot ?? w.MuzzlePos);
                // a structure: its aim point, still (the non-vehicle case)
                var rel = (t is Vehicle tv ? tv.Pos.ToVector3() + new Vector3(0, 0.8f, 0) : CentreOf(t)) - muzzle;
                var dv = (t is Vehicle tv2 ? tv2.Vel : default) - v.Vel;
                float s = MathF.Max(w.Def.Speed, 1f);
                float time = System.Math.Clamp(rel.Length() / s, 0f, 5f);
                var aim = rel + dv * time;
                var local = w.BaseRot.ToLocal(v.Rot.ToLocal(aim));
                float len = local.Length();
                if (len < 1e-3f) continue;
                yaw = MathF.Atan2(local.X, local.Z);
                float sp = local.Y / len;
                pitch = MathF.Abs(sp) < 0.95f ? MathF.Asin(sp) : w.AimPitch;
                // a turret hung upside down under a body (the Phantom's belly mount) keeps below the body's own level: no aiming up through the hull
                if (w.BaseRot.Up.Y < -0.5f) pitch = MathF.Max(pitch, 0f);
            }
            // a helicopter's turrets slew four times as fast (6.8 for type 9)
            float slew = dt * (0.75f + 0.25f * w.HpRatio) * (v.IsHeli ? 6.8f : 1.7f);
            float ey = Ai.AiDriver.AngleDelta(w.AimYaw, yaw);
            w.AimYaw += System.Math.Clamp(ey, -slew, slew);
            // the pitch moves only while the yaw error is under 90°, and never for class 3 tier 3 (the EZK);
            // the on-target test is then the yaw's alone
            if (MathF.Abs(ey) < MathF.PI / 2 && !(w.Def.Class == 3 && w.Def.Tier == 3))
            {
                float ep = pitch - w.AimPitch;
                w.AimPitch += System.Math.Clamp(ep, -slew, slew);
                w.OnTarget = MathF.Abs(ey) < 0.0349f && MathF.Abs(ep) < 0.0349f;
            }
            else w.OnTarget = MathF.Abs(ey) < 0.0349f;
        }
    }

    /// <summary>
    /// From the non-turret branch: guns (class 1, 2)
    /// and class-4 tiers 0 and 5 tip their muzzle, about its own x, toward
    /// the elevation of the arc target's centre (the entity
    /// slot's centre, a structure's ground + 0.6), at most 10°, easing at
    /// 4·dt and snapping inside 0.005; with no target they ease back level.
    /// Measured in the game: the Piranha's roof MG sits 1° down at a target.
    /// </summary>
    private void AimFixed(Vehicle v, Armory a, float dt)
    {
        float k = dt * 4f;
        bool kOk = k >= 0 && k <= 0.95f;
        // the handguns too: the original walks every weapon instance, and theirs are class 1 (the side arcs)
        foreach (var w in a.Weapons.Concat(a.Handguns.OfType<Weapon>()))
        {
            if (w.Style == 3 || !(w.Def.Class is 1 or 2 || (w.Def.Class == 4 && w.Def.Tier is 0 or 5))) continue;
            if (AimTargetFor(v, w.MountKind) is not { } t)
            {
                w.GunPitchWant = 0;
                w.GunPitch = kOk && MathF.Abs(w.GunPitch) >= 0.005f ? w.GunPitch - w.GunPitch * k : 0;
                continue;
            }
            var f = Rows.Concat(w.BaseRot, v.Rot).Forward;
            var muzzle = v.Pos.ToVector3() + v.Rot.ToWorld(w.MuzzlePos);
            var d = (t is Vehicle tv ? tv.Pos.ToVector3() + tv.Rot.ToWorld(new Vector3((tv.BoxX[0] + tv.BoxX[3]) / 2, (tv.BoxY[1] + tv.BoxY[3]) / 2, (tv.BoxZ[0] + tv.BoxZ[3]) / 2))
                : AimPointOf(t)) - muzzle;
            float len = d.Length(), fl = f.Length();
            if (len < 1e-4f || fl < 1e-4f) continue;
            float want = System.Math.Clamp(MathF.Asin(System.Math.Clamp(d.Y / len, -1f, 1f)) - MathF.Asin(System.Math.Clamp(f.Y / fl, -1f, 1f)), -0.17453292f, 0.17453292f);
            w.GunPitchWant = want;
            float diff = want - w.GunPitch;
            w.GunPitch = kOk && MathF.Abs(diff) >= 0.005f ? w.GunPitch + diff * k : want;
        }
    }

    /// <summary>The 4Get-U-Not's tone this frame per shooter: msllock1.wav while it locks, msllock2.wav once locked.</summary>
    public Dictionary<Vehicle, string> LockTone { get; } = [];
    /// <summary>The cars another car's 4Get-U-Not has held locked (1.6 s or more) this frame: msllock3.wav, the warning, for them.</summary>
    public HashSet<Vehicle> LockedOn { get; } = [];
    public const float LockSeconds = 1.6f;

    /// <summary>
    /// the non-turret branch for class 4 tier 4 (the Nitro Pack's 4Get-U-Not): while
    /// the weapon's arc has a target (the radar's target for its mount kind, as the shot takes it) and the weapon is the
    /// selected one or linked (the original reads rec[1..5]), its lock time grows by the frame's dt; otherwise
    /// it is 0. The player's trigger waits for 1.6 s of it (after the aims: the shot can leave on the
    /// frame the lock reaches 1.6 s) and the shot spends it. Only the player's record is ever given a
    /// selection (the weapon cycle of the player alone starts every record at −1), so an AI car never
    /// locks, and its fire decision does not ask for a lock. The tones are looping
    /// voices on the weapons' sound object, kept alive by being asked for each frame: the shooter hears
    /// msllock1.wav under 1.6 s and msllock2.wav after, while the weapon has ammo; the car it holds hears msllock3.wav
    /// once the lock is 1.6 s. A turret-mounted one (style 3) takes the other branch and never locks.
    /// </summary>
    private void Lock(Vehicle v, Armory a, float dt)
    {
        bool human = World.IsHuman(v) && !IsBot(v);
        for (int i = 0; i < a.Weapons.Count; i++)
        {
            var w = a.Weapons[i];
            if (w.Def.Class != 4 || w.Def.Tier != 4) continue;
            if (w.Style != 3)
            {
                bool held = false;
                if (AimTargetFor(v, w.MountKind) is { } t)
                {
                    w.LockTime += dt;
                    if (human && (i == a.Selected || a.Group.Contains(i)))
                    {
                        held = true;
                        if (!_silent)
                        {
                            if (t is Vehicle tv && w.LockTime >= LockSeconds) LockedOn.Add(tv);
                            if (w.Ammo != 0) LockTone[v] = w.LockTime < LockSeconds ? "msllock1.wav" : "msllock2.wav";
                        }
                    }
                }
                if (!held) w.LockTime = 0;
            }
            // Runs after, so the player's trigger reads this frame's lock (measured: the shot
            // leaves on the frame the lock passes 1.6 s); a turret-mounted one never locks and never fires for a player
            if (human && w.LockTime < LockSeconds) w.Trigger = false;
        }
    }

    /// <summary>Play Options Unlim. Ammo: the player's car (in co-op every human's).</summary>
    private bool Unlimited(Vehicle v) => UnlimitedAmmo && World.IsPlayerSide(v);

    /// <summary>The rate, burst and reload timers, then the shot. MG and .45 shots in one frame are batched.</summary>
    private void Timing(Vehicle v, Weapon w, float frameDt)
    {
        var def = w.Def;
        float rate = Rate(v, def);
        float interval = rate > 0 ? 1f / rate : 1e9f;
        float dt = frameDt;
        int batched = 0; float batchDt = 0;
        bool batch = def.Kind is 1 or 0x12;
        // deviation (docs/deviations.md "Flame streams' length"): a flame weapon held with ammo grows its stream every tick
        bool flameHeld = def.Kind is 9 or 10 or 11 && w.Trigger && w.Hp > 0 && !IsDead(v) && (w.Ammo != 0 || Unlimited(v));
        void FireOne(float pre)
        {
            if (batch) { batched++; batchDt = MathF.Max(batchDt, pre); }
            else Shot(v, w, 1, pre);
        }
        if (dt <= w.WaitA || dt <= w.WaitB)
        {
            // a fresh press while the interval or burst cooldown still runs gives the player the dry click, once per press
            if (w.Trigger && !w.WasFiring && World.IsHuman(v) && !_cosmetic) Events.Add(new WeaponClick(v, "wclick.wav"));
            if (w.WaitA > 0) w.WaitA -= dt;
            if (w.WaitB > 0) w.WaitB -= dt;
        }
        else
        {
            while (dt > 0 && w.WaitA < dt && w.WaitB < dt)
            {
                if (w.WaitA > 0)
                {
                    dt -= w.WaitA; w.WaitA = 0;
                    if (dt < 0) { w.WaitA = -dt; dt = 0; }
                    if (dt < 0.01f) dt = 0;
                    continue;
                }
                if (w.WaitB > 0)
                {
                    dt -= w.WaitB; w.WaitB = 0;
                    if (dt < 0) { w.WaitB = -dt; dt = 0; }
                    if (dt < 0.01f) dt = 0;
                    continue;
                }
                if (w.BurstLeft >= 1)
                {
                    w.BurstLeft--;
                    w.WaitB = interval;
                    if (w.BurstLeft == 0 && interval < def.Delay) { w.WaitA = def.Delay; w.WaitB = 0; }
                    FireOne(dt);
                }
                else if (w.Trigger)
                {
                    if (w.Ammo != 0 || Unlimited(v))
                    {
                        w.BurstLeft = (int)def.Burst - 1;
                        if (!Unlimited(v) && w.Ammo < w.BurstLeft) w.BurstLeft = w.Ammo;
                        w.WaitB = interval;
                        // base game: droppers dribble below 20 u/s; the Nitro Pack has no gate (its rate follows the speed)
                        if (!NitroRules && Dropper(def) && v.Speed < 20f) w.WaitB = 1f + (interval - 1f) * 0.05f * v.Speed;
                        if (w.BurstLeft == 0)
                        {
                            if (w.WaitB <= def.Delay) { w.WaitA = def.Delay; w.WaitB = 0; }
                            else if (def.Delay == 0) { w.WaitA = w.WaitB; w.WaitB = 0; }
                        }
                        FireOne(dt);
                        // the loop goes on with what is left of the frame (no reset after a shot);
                        // a 20/s gun at 28 fps fires two frames in three (I76_FIRE_PROBE, 2026-10-06)
                    }
                    else
                    {
                        w.Trigger = false;
                        w.WaitA = 1f;
                        if (World.IsHuman(v) && !_cosmetic) Events.Add(new WeaponClick(v, "wclick.wav"));   // a client hears the server's
                        dt = 0;
                    }
                }
                else dt = 0;
            }
            if (dt < w.WaitA) { if (dt > 0) w.WaitA -= dt; w.WaitB = 0; w.BurstLeft = 0; }
            if (dt < w.WaitB) { if (dt > 0) w.WaitB -= dt; w.WaitA = 0; }
        }
        if (flameHeld) FlameHold(v, w);
        w.WasFiring = w.Trigger;
        w.Trigger = false;   // a per-frame request
        if (batched > 0) Shot(v, w, batched, batchDt / 3f);
    }

    /// <summary>Sound and flash, ammo, the target by ordnance kind, then the round.</summary>
    private void Shot(Vehicle v, Weapon w, int n, float dtPre)
    {
        var rot = Rows.Concat(w.MuzzleRot, v.Rot);
        var muzzle = v.Pos.ToVector3() + v.Rot.ToWorld(w.MuzzlePos);
        // The arm's shot animation, which holds the arm (and so the weapons it gates) for its three frames
        if (w.Def.Class == 1)
        {
            if (w.Def.Tier is 1 or 2) v.Arms.Shoot((int)w.Def.Tier);
            if (!_silent) HandgunShot?.Invoke(v, (int)w.Def.Tier);
        }
        if (w.Def.Kind == 0x14) w.LockTime = 0;   // the 4Get-U-Not's shot spends its lock
        if (_silent) { if (!Unlimited(v)) w.Ammo = System.Math.Max(0, w.Ammo - n); return; }
        object? target = w.Def.Kind switch
        {
            1 or 2 or 6 or 0x14 => AimTargetFor(v, w.MountKind),
            3 => IsDead(v) ? null : HeatTarget(muzzle, rot, v),
            _ => AimTargetFor(v, 4),
        };
        // Class 3 tier 3 (the EZK Mortar) lays its muzzle at the solved elevation before the round leaves
        if (w.Def.Class == 3 && w.Def.Tier == 3 && w.Def.Speed != 0)
        {
            float elev = target is null ? 0.2443461f : EzkElevation(v, w, target, w.Def.Speed);
            if (w.Style == 3) w.AimPitch = elev; else w.GunPitch = elev;
            rot = Rows.Concat(w.MuzzleRot, v.Rot);
            muzzle = v.Pos.ToVector3() + v.Rot.ToWorld(w.MuzzlePos);   // a turret's muzzle swings with its gun
        }
        Events.Add(new ShotFired(v, w, muzzle, rot.Forward));
        if (!Unlimited(v)) w.Ammo = System.Math.Max(0, w.Ammo - n);
        int had = PredictedRounds.Count;
        if (n > 0) Spawn(v, w, muzzle, rot, n, target, dtPre);
        if (_cosmetic) PredictedShot?.Invoke(w, PredictedRounds.Count > had ? PredictedRounds[^1] : null);
    }

    private float U() => Rand.Next() * 3.051851e-5f;

    /// <summary>The round leaves the muzzle with its spread and the rand() draws in the original's order.</summary>
    private void Spawn(Vehicle shooter, Weapon w, Vector3 pos, Rows rot, int count, object? target, float dtPre)
    {
        var def = w.Def;
        uint k = def.Kind;
        if (def.Ordnance is not { } ord) return;
        // the flame cases return before any rand(): one flame call per shot
        if (k is 9 or 10 or 11)
        {
            for (int i = 0; i < count; i++) FlameShot(shooter, w, (int)(k - 9), w.Damage);
            return;
        }
        float s = def.Spread;
        float a = 2 * s * U() - s, b = 2 * s * U() - s, f = 0.6f * U();
        static float Deg(float d) => d * MathF.PI / 180f;
        switch (k)
        {
            case 1 or 4 or 5 or 6 or 7 or 0xf or 0x10 or 0x12 or 0x13 or 0x15 or 0x16:
                if (a > 0 && b > 0) rot = Rows.Concat(Rows.AboutY(Deg(a)), rot);
                pos += rot.Forward * f;
                break;
            case 2 or 8 or 0x14:
                if (a > 0) rot = Rows.Concat(Rows.AboutY(Deg(a)), rot);
                pos += rot.Forward * (0.75f * U() - 0.375f);
                break;
            case 3:
                pos += rot.Forward * f;
                pos += rot.Forward * (0.2f * U() - 0.1f);
                if (target is Vehicle tv) rot = LookAlong(AimPoint(tv) - pos, rot);
                break;
            case 0xc or 0xe or 0x11:
                (pos, rot) = DropPlace(shooter, def, pos, rot, s);
                break;
            default:
                Unmodelled.Add(k);
                return;
        }
        for (int i = 0; i < 3; i++) Rand.Next();                       // visual tumble
        // The round's contrail, trail style 2 (the radar missiles): launched at the player, which is not
        // jamming (the jammer flag), it plays clockon.wav on the player; in a network game each machine's own
        if (k is 8 or 0x14 && target is Vehicle locked && World.IsHuman(locked) && (locked.Flags & VehicleFlags.Jamming) == 0 && !_cosmetic)
            Events.Add(new WeaponClick(locked, "clockon.wav"));
        if (k is 2 or 3 or 4 or 5 or 7 or 8 or 0x14 or 0x16) Rand.Next();  // trail roll
        float v0 = k is 0xf or 0x10 or 0x13 ? 5f : def.Speed;
        var r = new Round
        {
            Kind = k, Shooter = shooter, Def = def, Ord = ord, Pos = pos, PrevPos = pos, Rot = rot,
            LocalVel = new Vector3(0, 0, v0), Life = def.Life == 0 ? 5f : def.Life, Count = count, Damage = w.Damage, Target = target,
        };
        (_cosmetic ? PredictedRounds : Rounds).Add(r);
        if (dtPre != 0) { r.Life -= dtPre; Step(r, dtPre); }
    }

    private static Rows LookAlong(Vector3 d, Rows fallback)
    {
        if (d.LengthSquared() < 1e-8f) return fallback;
        var f = Vector3.Normalize(d);
        var up = MathF.Abs(f.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var r = Vector3.Normalize(Vector3.Cross(up, f));
        return new Rows(r, Vector3.Cross(f, r), f);
    }

    /// <summary>
    /// The nearest vehicle within 650 m whose heading lies along the missile's
    /// (|dot| &gt; 0.94: a tail or head-on aspect), in front of the muzzle inside a 20° cone,
    /// measured to its collider centre. A car needs its engine running (which
    /// Clears the frame after it dies); a helicopter or emplacement does not.
    /// </summary>
    private Vehicle? HeatTarget(Vector3 muzzle, Rows rot, Vehicle shooter)
    {
        Vehicle? best = null; float bd = 650f * 650f;
        foreach (var v in World.Vehicles)
        {
            if (v == shooter || !Hot(v)) continue;
            var d = Collider(v).Centre - muzzle;
            float d2 = d.LengthSquared();
            if (d2 > 650f * 650f) continue;
            if (MathF.Abs(Vector3.Dot(v.Rot.Forward, rot.Forward)) <= 0.94f) continue;
            float along = Vector3.Dot(d, rot.Forward);
            if (along < 0 || along * along <= 0.8830222f * d2 || d2 >= bd) continue;
            bd = d2; best = v;
        }
        return best;
    }

    /// <summary>The heat test's object test: a car only with its engine on.</summary>
    private static bool Hot(Vehicle v) => v.IsHeli || v.IsEmplacement || ((v.Flags & VehicleFlags.EngineOn) != 0 && !IsDead(v));

    /// <summary>Where a guided round or a fixed gun aims, the target's collider centre.</summary>
    private static Vector3 AimPoint(Vehicle v) => ColliderCentre(v);

    /// <summary>
    /// After a step that hit nothing: the lock holds while the line-of-sight test passes (the target
    /// is hot and its heading within |dot| 0.94 of the missile's); otherwise it re-acquires from the
    /// missile's own pose (the shooter excluded). With a target the pose snaps to look
    /// at it: no turn limit.
    /// </summary>
    private void HeatSteer(Round r)
    {
        if (r.Target is Vehicle t && (!Hot(t) || MathF.Abs(Vector3.Dot(r.Rot.Forward, t.Rot.Forward)) <= 0.94f)) r.Target = null;
        r.Target ??= HeatTarget(r.Pos, r.Rot, r.Shooter);
        // the local velocity is kept, so the flight turns with the pose
        if (r.Target is Vehicle tt) r.Rot = LookAlong(AimPoint(tt) - r.Pos, r.Rot);
    }

    // ---- flight ----

    private void FlyRounds(List<Round> rounds, float dt)
    {
        for (int i = 0; i < rounds.Count; i++)
        {
            var r = rounds[i];
            r.Life -= dt;
            if (r.Life < 0) { rounds.RemoveAt(i--); continue; }
            if (Step(r, dt)) rounds.RemoveAt(i--);
        }
    }

    /// <summary>One flight step and its hit test; true when the round is spent.</summary>
    /// <summary>A mine's eight probe corners, ±0.1 × ±0.1 × ±0.2.</summary>
    private static readonly Vector3[] MineCorners =
        [new(-0.1f, -0.1f, -0.2f), new(0.1f, -0.1f, -0.2f), new(0.1f, -0.1f, 0.2f), new(-0.1f, -0.1f, 0.2f),
         new(-0.1f, 0.1f, -0.2f), new(0.1f, 0.1f, -0.2f), new(0.1f, 0.1f, 0.2f), new(-0.1f, 0.1f, 0.2f)];

    /// <summary>
    /// Mines (0xf) and Blox (0x10): they fall and bounce off the
    /// ground (v' = 0.25·v − 0.5·(v·n)·n at a corner contact) until slower than
    /// 1, then lie flat on it; every frame a 1 m upward probe looks for a car
    /// (not the dropper for its first 2 s). A car that finds one takes the
    /// round's packet and effect and is thrown; the mine is gone.
    /// The original sweeps each corner in 5 m steps and tumbles the body; here
    /// the corners are tested at the frame's end and the pose stays level.
    /// </summary>
    private bool StepMine(Round r, float dt)
    {
        r.PrevPos = r.Pos;
        r.Age += dt;
        if (!r.Launched) { r.Vel = r.Rot.ToWorld(r.LocalVel); r.Launched = true; }
        if (!r.Settled && r.Vel.LengthSquared() >= 1f)
        {
            var acc = new Vector3(0, -9.8f * r.Ord.Gravity, 0);
            r.Pos += (r.Vel + acc * (dt / 2)) * dt;
            r.Vel += acc * dt;
            float deepest = 0;
            foreach (var c in MineCorners)
            {
                var w = r.Pos + r.Rot.ToWorld(c);
                deepest = MathF.Max(deepest, World.Ground.BareHeight(w.X, w.Z) - w.Y);
            }
            if (deepest > 0)
            {
                var n = World.Ground.BareNormal(r.Pos.X, r.Pos.Z);
                r.Pos.Y += deepest;
                r.Vel = r.Vel * 0.25f - n * (0.5f * Vector3.Dot(r.Vel, n));
            }
        }
        if (!r.Settled && r.Vel.LengthSquared() < 1f)
        {
            // at rest: on the ground, flat to it, heading kept
            r.Settled = true;
            r.Vel = Vector3.Zero;
            r.Pos.Y = World.Ground.BareHeight(r.Pos.X, r.Pos.Z);
            var n = World.Ground.BareNormal(r.Pos.X, r.Pos.Z);
            var f = r.Rot.Forward;
            f -= n * Vector3.Dot(f, n);
            if (f.LengthSquared() < 1e-6f) f = Vector3.Cross(Vector3.UnitX, n);
            f = Vector3.Normalize(f);
            r.Rot = new Rows(Vector3.Cross(n, f), n, f);
        }
        r.Travelled += Vector3.Distance(r.PrevPos, r.Pos);
        var up = Vector3.UnitY;
        foreach (var v in World.Vehicles)
        {
            if (v.Wreck || v.IsEmplacement || (v == r.Shooter && r.Age <= 2f)) continue;
            if (SegmentVsVehicle(v, r.Pos, up) is not { } h) continue;
            var at = r.Pos + up * h.T;
            OnHit(r, v, at, h.N, up);
            if (_cosmetic) return true;
            if (r.Kind == 0xf) Throw(v, at, 1f, 2f, 0.75f);
            else Throw(v, at, 0.5f, 0.5f, 0.5f);
            return true;
        }
        return false;
    }

    /// <summary>The spin a mine gives by which quarter of the car it went off under (local x, z).</summary>
    private static readonly Vector3[] ThrowSpin =
        [new(0.3926991f, 0, 0.19634955f), new(0.3926991f, 0, -0.19634955f), new(-0.3926991f, 0, 0.19634955f), new(-0.3926991f, 0, -0.19634955f)];

    /// <summary>Set spin by quarter ×a; above 2 u/s velocity ×c; vy += 4b, at most 20; airborne.</summary>
    private static void Throw(Vehicle v, Vector3 at, float a, float b, float c)
    {
        var l = v.Rot.ToLocal(at - v.Pos.ToVector3());
        int q = l.X <= 0 ? (l.Z <= 0 ? 2 : 0) : (l.Z <= 0 ? 3 : 1);
        v.AngVel = ThrowSpin[q] * a;
        if (v.Speed > 2f) v.Vel *= c;
        v.Vel.Y = MathF.Min(v.Vel.Y + 4f * b, 20f);
        v.Speed = v.Vel.Length();
        v.Flags |= VehicleFlags.Airborne;
    }

    private bool Step(Round r, float dt)
    {
        if (r.Kind is 0xf or 0x10) return StepMine(r, dt);
        if (r.Kind is 0xc or 0xe or 0x11) return StepSlick(r, dt);
        if (r.Kind == 0x16) return StepChem(r, dt);
        r.PrevPos = r.Pos;
        r.Age += dt;
        var acc = new Vector3(0, -9.8f * r.Ord.Gravity, 0);
        float kf = r.Kind is 1 or 6 or 0x12 ? dt : r.Kind == 2 ? dt * 0.5f : dt;
        if (r.Kind == 3)
        {
            // The step flies the pose it has, then (if it hit nothing) steers for the next one
            Approach(r, kf);
            r.Pos += r.Rot.ToWorld(r.LocalVel) * dt;
            r.Travelled += Vector3.Distance(r.PrevPos, r.Pos);
            if (HitTest(r)) return true;
            HeatSteer(r);
            return false;
        }
        else
        {
            // A target that jams, or is no longer a live vehicle, is let go for good; the 4Get-U-Not
            // (0x14) turns the same way but never lets go: it follows its target through a jam and death
            if (r.Kind == 8 && r.Target is Vehicle lost && (IsJamming(lost) || !IsLive(lost))) r.Target = null;
            if (r.Kind is 8 or 0x14 && r.Target is { } t) Guide(r, AimPointOf(t));
            Approach(r, kf);
            var vw = r.Rot.ToWorld(r.LocalVel);
            var disp = (vw + acc * (dt / 2)) * dt;
            r.Pos += disp;
            vw += acc * dt;
            // the new local velocity is taken in the pose the step flew (the integrators' own order:
            // converts with the old rows, then the pose is re-aimed), so a round that is turning keeps
            // its local components while the pose follows the displacement: the velocity turns with the pose,
            // on top of gravity, a step at a time. Mortars, WP and cluster bombs fly a curve that depends on
            // the step (measured: WP from a Piranha lands 44 m away at 20 fps, 38 at 28, 27 at 60)
            r.LocalVel = r.Rot.ToLocal(vw);
            // the bullet step (1, 6, 0x12 and the Ball O'Flame 0x15) never re-aims the pose
            if (r.Kind is not (1 or 6 or 0x12 or 0x15)) r.Rot = LookAlong(disp, r.Rot);
            if (r.Kind is 8 or 0x14)
            {
                float g = World.Ground.BareHeight(r.Pos.X, r.Pos.Z) + 0.5f;
                if (r.Pos.Y < g) r.Pos.Y = g;
            }
        }
        r.Travelled += Vector3.Distance(r.PrevPos, r.Pos);
        return HitTest(r);
    }

    /// <summary>
    /// Kind 0x16 (the same code in the Nitro Pack): the Nitro Pack's Chemical Mortar. It flies as the
    /// mortars do; its path is tested against the cars alone (no structures), and a car it meets takes the
    /// round's packet (armour bypassed, flag 0x10) and the impact. A car met or the ground reached, it lands: down on the
    /// ground under that point, flat to it, its shell gone, and it lingers as a cloud (the damage smoke's kind 3, the
    /// xcp1 puffs) until its life (16.7 s) runs out. Every frame of the cloud, count times, a 1 m probe straight up from
    /// it (the shooter excluded for the first second): a car in it takes the packet and the impact again (xclst1, whose
    /// splash of 45 reaches 20 m). Landing on the ground itself has no impact. <see cref="Round.Settled"/> marks the cloud.
    /// </summary>
    private bool StepChem(Round r, float dt)
    {
        r.Age += dt;
        var up = Vector3.UnitY;
        if (r.Settled)
        {
            r.PrevPos = r.Pos;
            for (int i = 0; i < r.Count; i++)
                if (ChemProbe(r, r.Pos, up) is { } c) OnHitOnce(r, c.Victim, c.At, up);
            return false;
        }
        r.PrevPos = r.Pos;
        var acc = new Vector3(0, -9.8f * r.Ord.Gravity, 0);
        Approach(r, dt);
        var vw = r.Rot.ToWorld(r.LocalVel);
        var disp = (vw + acc * (dt / 2)) * dt;
        var to = r.Pos + disp;
        vw += acc * dt;
        r.LocalVel = r.Rot.ToLocal(vw);
        bool met = false;
        for (int i = 0; i < r.Count && !met; i++)
            if (ChemProbe(r, r.PrevPos, to - r.PrevPos) is { } h)
            {
                to = h.At;
                OnHit(r, h.Victim, h.At, h.N, Vector3.Normalize(disp));
                met = true;
            }
        var d = to - r.PrevPos;
        float len = d.Length();
        Vector3? ground = len > 1e-6f ? TerrainHit(r.PrevPos, to, len) : null;
        if (met || ground is not null)
        {
            var at = met ? to : ground!.Value;
            // on the ground under it, flat to it, heading kept (the original's up/forward basis)
            at.Y = World.Ground.BareHeight(at.X, at.Z);
            var n = World.Ground.BareNormal(at.X, at.Z);
            var f = r.Rot.Forward - n * Vector3.Dot(r.Rot.Forward, n);
            if (f.LengthSquared() < 1e-6f) f = Vector3.Cross(Vector3.UnitX, n);
            f = Vector3.Normalize(f);
            r.Rot = new Rows(Vector3.Cross(n, f), n, f);
            r.Travelled += Vector3.Distance(r.PrevPos, at);
            r.Pos = at;
            r.Settled = true;
            r.LocalVel = Vector3.Zero;
            return false;
        }
        r.Travelled += len;
        if (len > 0.01f) r.Rot = LookAlong(disp, r.Rot);
        r.Pos = to;
        return false;
    }

    /// <summary>The earliest car (no emplacement, no wreck; the shooter for its first second) the segment enters, with its normal.</summary>
    private (Vehicle Victim, Vector3 At, Vector3 N)? ChemProbe(Round r, Vector3 p0, Vector3 d)
    {
        if (d.LengthSquared() < 1e-12f) return null;
        float best = 2f; Vehicle? victim = null; Vector3 normal = default;
        foreach (var v in World.Vehicles)
        {
            if (v.Wreck || v.IsEmplacement || (v == r.Shooter && ShooterExcluded(r))) continue;
            if (SegmentVsVehicle(v, p0, d) is { } h && h.T < best) { best = h.T; victim = v; normal = h.N; }
        }
        return victim is null ? null : (victim, p0 + d * best, normal);
    }

    private static void Approach(Round r, float k)
    {
        float vz = r.LocalVel.Z, vT = r.Ord.TerminalSpeed;
        if (MathF.Abs(vT - vz) >= 0.005f && k >= 0 && k <= 0.95f) r.LocalVel.Z = vz + (vT - vz) * k;
    }

    /// <summary>
    /// The radar missile's turn: about fwd × d̂ by min(0.75·|fwd × d̂|, cap); the cap grows with distance flown (none at exactly 150).
    /// Only the pose turns: the velocity is kept in the round's own frame, so the step that follows flies the new heading.
    /// </summary>
    private static void Guide(Round r, Vector3 aim)
    {
        var d = Vector3.Normalize(aim - r.Pos);   // steers at the collider centre (a structure's ground + 0.6)
        var f = r.Rot.Forward;
        if (Vector3.Dot(f, d) >= 0.998f) return;
        var axis = Vector3.Cross(f, d);
        float sin = axis.Length();
        if (sin < 1e-6f) return;
        float turn = 0.75f * sin;
        float cap = r.Travelled < 15 ? 0.0523359f : r.Travelled < 150 ? 0.25881904f : r.Travelled > 150 ? 0.34202015f : float.MaxValue;
        turn = MathF.Min(turn, cap);
        var q = Quaternion.CreateFromAxisAngle(axis / sin, turn);
        r.Rot = new Rows(Vector3.Transform(r.Rot.Right, q), Vector3.Transform(r.Rot.Up, q), Vector3.Transform(f, q));
    }

    // ---- hits ----

    private bool ShooterExcluded(Round r) => r.Kind switch
    {
        2 or 3 or 8 or 0x14 => r.Travelled < 70f,
        0xc or 0xe or 0xf or 0x10 or 0x11 => r.Age < 2f,
        _ => r.Age < 1f,
    };

    /// <summary>The path against the colliders (earliest wins), else the terrain (1 m march, 9 bisections).</summary>
    private bool HitTest(Round r)
    {
        var p0 = r.PrevPos; var d = r.Pos - p0;
        float len = d.Length();
        if (len < 1e-6f) return false;
        float best = 2f; object? victim = null; Vector3 normal = default; int node = -1;
        foreach (var v in World.Vehicles)
        {
            // an emplacement is hit through its static collider (any node maps to the root)
            if (v.Wreck || v.IsEmplacement || (v == r.Shooter && ShooterExcluded(r))) continue;
            if (SegmentVsVehicle(v, p0, d) is { } h && h.T < best) { best = h.T; victim = v; normal = h.N; }
        }
        if (World.Ground is IHasStatics hs)
        {
            float minX = MathF.Min(p0.X, r.Pos.X), maxX = MathF.Max(p0.X, r.Pos.X), minZ = MathF.Min(p0.Z, r.Pos.Z), maxZ = MathF.Max(p0.Z, r.Pos.Z);
            foreach (var o in hs.World.Objects)
            {
                if (!o.Solid || (o.Vehicle is { } ov && ov == r.Shooter && ShooterExcluded(r))) continue;
                if (o.Centre.X + o.Radius < minX || o.Centre.X - o.Radius > maxX || o.Centre.Z + o.Radius < minZ || o.Centre.Z - o.Radius > maxZ) continue;
                // Names the node it met: the round's packet goes to that node (measured in the original game)
                var (t, poly) = StaticWorld.RayMesh(o.Polys, p0, d, 1f);
                if (poly is not null && t < best) { best = t; victim = o; normal = poly.N; node = poly.Node; }
                if (StaticWorld.RayBoxes(o.Boxes, p0, d, 1f) is { } bh && bh.T < best) { best = bh.T; victim = o; normal = bh.N; node = bh.Box.Node; }
            }
        }
        Vector3 at;
        if (victim is null)
        {
            if (TerrainHit(p0, r.Pos, len) is not { } th) return false;
            at = th;
        }
        else at = p0 + d * best;
        OnHit(r, victim, at, normal, d / len, victim is StaticObject ? node : -1);
        return true;
    }

    private (float T, Vector3 N)? SegmentVsVehicle(Vehicle v, Vector3 p0, Vector3 d)
    {
        var (c, rad) = Collider(v);
        // sphere broadphase
        var m = p0 - c;
        var pos = v.Pos.ToVector3();
        float dd = Vector3.Dot(d, d);
        float tc = System.Math.Clamp(-Vector3.Dot(m, d) / dd, 0f, 1f);
        if ((m + d * tc).LengthSquared() > rad * rad) return null;
        // Kind 1: the COLP box (x0, y1, z0)–(x3, y3, z3) via, slab test
        var lp = v.Rot.ToLocal(p0 - pos); var ld = v.Rot.ToLocal(d);
        var min = new Vector3(v.BoxX[0], v.BoxY[1], v.BoxZ[0]); var max = new Vector3(v.BoxX[3], v.BoxY[3], v.BoxZ[3]);
        float enter = 0, exit = 1; Vector3 n = default;
        for (int axis = 0; axis < 3; axis++)
        {
            float p = axis == 0 ? lp.X : axis == 1 ? lp.Y : lp.Z;
            float q = axis == 0 ? ld.X : axis == 1 ? ld.Y : ld.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(q) < 1e-9f) { if (p < lo || p > hi) return null; continue; }
            float t1 = (lo - p) / q, t2 = (hi - p) / q;
            float tn = MathF.Min(t1, t2), tf = MathF.Max(t1, t2);
            if (tn > enter) { enter = tn; n = axis switch { 0 => new(-MathF.Sign(q), 0, 0), 1 => new(0, -MathF.Sign(q), 0), _ => new(0, 0, -MathF.Sign(q)) }; }
            exit = MathF.Min(exit, tf);
            if (enter > exit) return null;
        }
        return (enter, v.Rot.ToWorld(n));
    }

    /// <summary>
    /// The collider record's sphere: an aircraft's from its model (<see cref="Vehicle.ModelSphere"/>); else the box's
    /// middle, radius the farthest corner of {x0,x3}×{y1,y3}×{z0,z3} + 0.1.
    /// </summary>
    public static (Vector3 Centre, float Radius) Collider(Vehicle v)
    {
        if (v.ModelSphere is { } m) return (v.Pos.ToVector3() + v.Rot.ToWorld(m.Centre), m.Radius);
        var mid = ColliderMid(v);
        float r = 0;
        // the eight corners (no arrays: this runs for every round against every car each frame)
        for (int i = 0; i < 8; i++)
            r = MathF.Max(r, Vector3.Distance(mid, new Vector3(v.BoxX[(i & 4) != 0 ? 3 : 0], v.BoxY[(i & 2) != 0 ? 3 : 1], v.BoxZ[(i & 1) != 0 ? 3 : 0])));
        return (v.Pos.ToVector3() + v.Rot.ToWorld(mid), r + 0.1f);
    }

    private static Vector3 ColliderMid(Vehicle v) =>
        v.ModelSphere?.Centre ?? new((v.BoxX[0] + v.BoxX[3]) / 2, (v.BoxY[1] + v.BoxY[3]) / 2, (v.BoxZ[0] + v.BoxZ[3]) / 2);

    /// <summary>The collider's centre alone (<see cref="Collider"/>'s, without the radius).</summary>
    public static Vector3 ColliderCentre(Vehicle v) => v.Pos.ToVector3() + v.Rot.ToWorld(ColliderMid(v));

    /// <summary>March 1 m along the path, then 9 bisection steps (to 0.02 m).</summary>
    private Vector3? TerrainHit(Vector3 p0, Vector3 p1, float len)
    {
        bool Below(Vector3 p) => p.Y < World.Ground.BareHeight(p.X, p.Z);
        if (!Below(p1) && len <= 1f) return Below(p0) ? p0 : null;
        int steps = System.Math.Max(1, (int)MathF.Ceiling(len));
        var prev = p0;
        for (int i = 1; i <= steps; i++)
        {
            var q = Vector3.Lerp(p0, p1, MathF.Min(1f, i / (float)steps));
            if (Below(q))
            {
                var lo = prev; var hi = q;
                for (int j = 0; j < 9; j++) { var mid = (lo + hi) * 0.5f; if (Below(mid)) hi = mid; else lo = mid; }
                return hi;
            }
            prev = q;
        }
        return null;
    }

    /// <summary>The impact pair by what was hit: terrain; vehicles (1, 4, 8, 9); classes 2/0xc; 3/0xb; a structure by the node's own type.</summary>
    private static int ImpactClass(object? victim, int node = -1) => victim switch
    {
        null => 0,
        Vehicle => 1,
        StaticObject { Vehicle: not null } => 1,
        StaticObject o when node >= 0 && node < o.Nodes.Count => o.Nodes[node].Type switch { 2 or 0xc => 2, 3 or 0xb => 3, _ => 1 },
        StaticObject { Type: 2 or 0xc } => 2,
        StaticObject { Type: 3 or 0xb } => 3,
        _ => 1,
    };

    private float PlayerMult(Round r) => r.Kind == 0x12 ? 1f : DifficultyFactor(r.Shooter);

    /// <summary>Trunc(damage·mult) split by integer division over the set channels.</summary>
    public DamagePacket RoundPacket(Round r)
    {
        var p = new DamagePacket { Attacker = r.Shooter, Mask = (ushort)r.Ord.Mask, Kind = I76.Sim.Damage.DamageKind.Round };
        int dmg = (int)(r.Damage * PlayerMult(r));
        int bits = System.Numerics.BitOperations.PopCount(r.Ord.Mask & 0xf);
        if (bits > 0) for (int c = 0; c < 4; c++) if ((r.Ord.Mask & (1u << c)) != 0) p.Ch[c] = dmg / bits;
        if (r.Kind == 0x12) p.Flags |= 0x1;
        if (r.Kind == 0x16) p.Flags |= 0x10;
        return p;
    }

    /// <param name="bomblet">A cluster bomb's bomblet going off: the impact alone, no more bomblets.</param>
    private void OnHit(Round r, object? victim, Vector3 at, Vector3 n, Vector3 dir, int node = -1, bool bomblet = false)
    {
        if (_cosmetic) return;   // a client's own round: the server's impact is the one drawn
        if (victim is not null && Damage is { } sink)
            for (int i = 0; i < r.Count; i++)
                sink.Apply(victim, RoundPacket(r), Local(victim, at), node);
        int cls = ImpactClass(victim, node);
        var fx = r.Ord.ImpactXdf[cls];
        // the bullet step (kinds 1, 6, 0x12, 0x15) places its impact 2 m back along the path
        var place = r.Kind is 1 or 6 or 0x12 or 0x15 ? at - dir * 2f : at;
        Events.Add(new Impact(fx, r.Ord.ImpactWav[cls], place, victim, r.Kind, at, n, dir));
        if (fx.Length > 0 && !fx.Equals("null", StringComparison.OrdinalIgnoreCase) && effects(fx) is { } e)
        {
            Events.Add(new Explosion(e, place));
            if (e.SplashDamage != 0) Splash(place, e.SplashRadius, e.SplashDamage, r.Shooter);
        }
        // A cluster bomb's landing schedules its bomblets after its own impact
        if (r.Kind == 5 && !bomblet) ScheduleBomblets(r, at);
    }

    private static Vector3 Local(object victim, Vector3 at) => victim switch
    {
        Vehicle v => v.Rot.ToLocal(at - v.Pos.ToVector3()),
        StaticObject o => at - o.Centre,
        _ => Vector3.Zero,
    };

    /// <summary>The .xdf of an explosion's effect node name (x1_carx1 → xcarx1.xdf; a file name stays as it is).</summary>
    public static string ExplosionFile(string node) =>
        node.StartsWith("x1_", StringComparison.OrdinalIgnoreCase) ? "x" + node[3..] + ".xdf" : node.EndsWith(".xdf", StringComparison.OrdinalIgnoreCase) ? node : node + ".xdf";

    /// <summary>The explosions a car's or a structure's end sets off (DamageModel's): a car's blast and its burst, a building's, a car-like structure's.</summary>
    public static readonly string[] EndExplosions = ["x1_carx1", "x1_cars1", "x1_bldx1"];

    /// <summary>
    /// An explosion by its effect node name (x1_carx1 → xcarx1.xdf),
    /// drawn, and its splash applied at once.
    /// </summary>
    public void Explode(string node, Vector3 at, object? owner)
    {
        if (effects(ExplosionFile(node)) is not { } e) return;
        Events.Add(new Explosion(e, at));
        if (e.SplashDamage != 0) Splash(at, e.SplashRadius, e.SplashDamage, owner);
    }

    /// <summary>
    /// Every collider whose horizontal distance is within its
    /// radius + R takes (int)damage on the explosive channel. No falloff, no
    /// height test, no multiplier, nobody spared (the excluded object is 0).
    /// A structure is hit on its root, never a node, so on the root's 400
    /// (measured: a cactus 400 → 385, its 5-HP nodes untouched). The point
    /// hit is the collider's far side from the blast: −n̂ · radius, n̂ =
    /// normalize(blast − centre) or (0, 0, 1) where they meet, in world axes
    /// though the vehicle slot reads it as its own (measured on a car turned 90°: a blast to the
    /// west took zone 2, one to the north zone 3).
    /// </summary>
    /// <summary>CLocal for a collider (centre, radius): its far side from the blast, in world axes.</summary>
    public static Vector3 SplashPoint(Vector3 centre, float radius, Vector3 at)
    {
        var d = at - centre;
        var n = d.X * d.X >= 1e-8f || d.Y * d.Y >= 1e-8f || d.Z * d.Z >= 1e-8f ? Vector3.Normalize(d) : Vector3.UnitZ;
        return -n * radius;
    }

    public void Splash(Vector3 at, float radius, float damage, object? attacker)
    {
        if (Sink is not { } sink) return;
        foreach (var v in World.Vehicles)
        {
            if (v.Wreck || v.IsEmplacement) continue;   // an emplacement takes its splash through its static collider
            var (c, vr) = Collider(v);
            var d = c - at;
            if (d.X * d.X + d.Z * d.Z > (vr + radius) * (vr + radius)) continue;
            var p = new DamagePacket { Attacker = attacker, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Explosion };
            p.Ch[1] = (int)damage;
            sink.Apply(v, p, SplashPoint(c, vr, at));
        }
        if (World.Ground is IHasStatics hs)
            foreach (var o in hs.World.Objects)
            {
                if (o.Destroyed) continue;
                var d = o.Centre - at;
                float reach = (o.BoundRadius > 0 ? o.BoundRadius : o.Radius) + radius;
                if (d.X * d.X + d.Z * d.Z > reach * reach) continue;
                var p = new DamagePacket { Attacker = attacker, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Explosion };
                p.Ch[1] = (int)damage;
                sink.Apply(o, p, SplashPoint(o.Centre, o.BoundRadius, at), -1);
            }
    }
}
