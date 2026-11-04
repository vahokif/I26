using System.Numerics;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Save;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// What a bot is up to between frames (docs/multiplayer.md "Bots"): its target, the path it was ordered along, its role in
/// capture the flag. Thought again after a restore, so none of it is saved.
/// </summary>
internal sealed class BotBrain
{
    /// <summary>The car being attacked, and when to look for a nearer one.</summary>
    public Entity? Target;
    public float RetargetAt;
    /// <summary>The path in the standing order (the same list object while it stands: a new one restarts the AI's search) and where it ends.</summary>
    public List<Vector3>? Path;
    public Vector3 Dest;
    /// <summary>What the standing order is (a mode's own tag): a different one issues afresh.</summary>
    public int Plan = -1;
    /// <summary>The seconds of mission time the car last moved at a pace, for a bot that sits stuck.</summary>
    public float MovedAt;
    /// <summary>A race: the route handed to the AI, the tracker's count before each point, and since when the AI has been a point ahead of the tracker.</summary>
    public int[]? Before, Owner;
    public float AheadSince = -1;
    /// <summary>When the dead bot asks to come back (a little after the 5 s are up, as a person's finger would be).</summary>
    public float? RaiseAt;
    /// <summary>The patrol point's turn (a defender circling its stand), and the wander's.</summary>
    public float PatrolAt;
    public float Phase;
    /// <summary>The once-a-second sample of where the car is, how many seconds in a row it has barely moved, and until when its collision avoidance is held on to get it out.</summary>
    public float SampleAt, AvoidUntil;
    public Vector3 SamplePos;
    public int StillSeconds;
    /// <summary>A racer that got stuck without its avoidance drives with it on the next time (and the other way round after that).</summary>
    public bool CarefulRace;
    /// <summary>A race: the tracker's checkpoint last seen, and when it last moved on.</summary>
    public int LastCur = -2;
    public float ProgressAt;
    /// <summary>A goto: the nearest the car has been to its goal, when it last got 30 m nearer, and whether it is on the grid's route (<see cref="Ai.GridRoute"/>) since it stopped getting nearer.</summary>
    public float BestDist, BestAt;
    public bool Gridded;

    internal void Save(SaveWriter w)
    {
        w.Ent(Target); w.F32(RetargetAt); w.Path(Path); w.V3(Dest); w.Int(Plan); w.F32(MovedAt);
        w.Bool(Before is not null); if (Before is not null) w.Ints(Before);
        w.Bool(Owner is not null); if (Owner is not null) w.Ints(Owner);
        w.F32(AheadSince); w.NF32(RaiseAt); w.F32(PatrolAt); w.F32(Phase);
        w.F32(SampleAt); w.F32(AvoidUntil); w.V3(SamplePos); w.Int(StillSeconds); w.Bool(CarefulRace); w.Int(LastCur); w.F32(ProgressAt);
        w.F32(BestDist); w.F32(BestAt); w.Bool(Gridded);
    }

    internal static BotBrain Load(SaveReader r)
    {
        var b = new BotBrain { Target = r.Ent(), RetargetAt = r.F32(), Path = r.Path() as List<Vector3>, Dest = r.V3(), Plan = r.Int(), MovedAt = r.F32() };
        if (r.Bool()) b.Before = r.Ints();
        if (r.Bool()) b.Owner = r.Ints();
        b.AheadSince = r.F32(); b.RaiseAt = r.NF32(); b.PatrolAt = r.F32(); b.Phase = r.F32();
        b.SampleAt = r.F32(); b.AvoidUntil = r.F32(); b.SamplePos = r.V3(); b.StillSeconds = r.Int(); b.CarefulRace = r.Bool(); b.LastCur = r.Int(); b.ProgressAt = r.F32();
        b.BestDist = r.F32(); b.BestAt = r.F32(); b.Gridded = r.Bool();
        return b;
    }

    internal static void AuditFields(SaveAudit a) =>
        a.For<BotBrain>().Saved(nameof(Target), nameof(RetargetAt), nameof(Path), nameof(Dest), nameof(Plan), nameof(MovedAt), nameof(Before), nameof(Owner),
            nameof(AheadSince), nameof(RaiseAt), nameof(PatrolAt), nameof(Phase), nameof(SampleAt), nameof(AvoidUntil), nameof(SamplePos), nameof(StillSeconds),
            nameof(CarefulRace), nameof(LastCur), nameof(ProgressAt), nameof(BestDist), nameof(BestAt), nameof(Gridded));
}

/// <summary>
/// Bots: players the server drives with the original's AI. A bot is a seat like any other (a car, a team, a score, laps,
/// captures, a respawn after 5 s, a regen pad), but its car is steered by the Ptable instead of the keys: each frame the
/// bot decides what the mode asks of it and gives its entity the order a mission script would,
/// attack, goto or race, and the AI's tick does the driving and the shooting.
/// <list type="bullet">
/// <item>Combat: attack the nearest enemy, any other car in a free for all, another team's (or the AI fill's) in team play.</item>
/// <item>Race: the checkpoints in order, lap after lap, as the Race order does (the planner finds the way between them),
/// shooting what is ahead and near, as a person may.</item>
/// <item>Capture the flag: a team's bots take roles; raiders take an enemy flag from its stand and bring it to their own,
/// defenders stay about their stand and meet whoever comes, escorts go with the carrier; whoever is free hunts a carrier of
/// their team's flag.</item>
/// </list>
/// Everything a bot does is a function of the game's state and its random stream, so a successor host that restores the
/// savestate carries on with the same bots (host migration).
/// </summary>
public sealed partial class MeleeGame
{
    private readonly HashSet<Vehicle> _botCars = [];

    /// <summary>
    /// Team play: a bot holds its fire while a teammate (a car of a seat, bot or human, on its side) is in the way: nearer than
    /// its target and within 9 m of the line to it, or alongside the line and within 12 m of the target (a round bursts there).
    /// </summary>
    private bool FriendInLine(Entity shooter, Entity target)
    {
        if (!Rules.TeamPlay || !_botCars.Contains(shooter.Vehicle!)) return false;
        var from = shooter.Position;
        var d = target.Position - from;
        float dist = d.Length();
        if (dist < 1f) return false;
        var dir = d / dist;
        var sv = shooter.Vehicle!.Vel;
        foreach (var q in Players)
        {
            if (q.Entity == shooter || q.Entity == target || q.Team != shooter.Team || q.Destroyed) continue;
            // where the teammate is, and where it will be half a second on (a round takes that long to reach a far target)
            var now = q.Entity.Position - from;
            var soon = now + (q.Car.Vel - sv) * 0.5f;
            foreach (var r in (ReadOnlySpan<Vector3>)[now, soon])
            {
                float along = Vector3.Dot(r, dir);
                if (along < -3f) continue;
                float off = (r - dir * along).Length();
                if (along < dist && off < 11f) return true;
                if (along >= dist && along < dist + 150f && off < 8f) return true;     // a round that goes through
            }
            if (Vector3.DistanceSquared(q.Entity.Position, target.Position) < 12f * 12f) return true;
        }
        return false;
    }

    /// <summary>A bot lays no mines or blox (a dropper): the AI drops them where they hurt its own side and itself as much as the car it is after.</summary>
    private bool BotHoldsWeapon(Entity shooter, Weapons.Weapon w) => w.Def.Class == 6 && _botCars.Contains(shooter.Vehicle!);

    /// <summary>The AI tier of a bot's skill setting: the original's setSkill index, 1 the best driver and shot, 5 the worst (the AI block's default).</summary>
    public static int SkillTier(BotSkill skill) => skill switch { BotSkill.Easy => 4, BotSkill.Hard => 1, _ => 3 };

    /// <summary>A bot of this tier: the AI's skill and its aggression row (as <see cref="SetAggression"/>), and an attacker cap as a player's.</summary>
    private void SetBotSkill(MeleePlayer p)
    {
        Host.SetSkill(p.Entity, p.BotSkill, p.BotSkill);
        p.Entity.Aggression = p.BotSkill - 1;
        p.Entity.MaxAttackers = AttackCap;
    }

    /// <summary>The longest a road route may be, against the way across, for a bot (<see cref="Entity.DetourLimit"/>).</summary>
    private const float BotDetour = 2.5f;

    /// <summary>
    /// A bot takes a seat in a network melee: the car built as a player's, on <paramref name="team"/>, with the AI to drive it
    /// (<see cref="SkillTier"/>). It scores, dies, comes back and regenerates as a player does.
    /// </summary>
    public MeleePlayer AddBot(string carFile, string name, int team, BotSkill skill, I76.Formats.Imaging.CarPaint? paint = null) => AddPlayer(carFile, name, team, SkillTier(skill), paint);

    /// <summary>Whether the car is a bot's.</summary>
    public bool IsBotCar(Vehicle v) => _botCars.Contains(v);

    // the plans a bot's standing order can be (BotBrain.Plan)
    private const int PlanNone = -1, PlanAttack = 0, PlanGoto = 1, PlanRace = 2, PlanSit = 3, PlanFollow = 4;

    /// <summary>The bots' frame: a dead one asks to come back, a live one gets the order its mode calls for.</summary>
    private void BotsFrame()
    {
        foreach (var p in Players)
        {
            if (!p.IsBot) continue;
            var b = p.Brain ??= new BotBrain { MovedAt = Host.Now };
            p.Entity.DetourLimit = BotDetour;
            if (p.Destroyed || Damage.DamageModel.IsDead(p.Car))
            {
                // RAISE_DEAD once the 5 s are up and a moment more (the key press of a person): refused until then, so it is asked on time
                if (p.Destroyed && !Ended && p.RespawnAt is { } at)
                {
                    b.RaiseAt ??= at + 0.3f + (Rand.Next() % 120) / 100f;
                    if (Host.Now >= b.RaiseAt) RequestRespawn(p);
                }
                b.Plan = PlanNone; b.Path = null; b.Target = null; b.AheadSince = -1;
                p.Entity.Depth = -1;
                p.Entity.Search = null;
                continue;
            }
            if (b.RaiseAt is not null)
            {
                // a fresh car: it thinks from the start
                b.RaiseAt = null; b.Plan = PlanNone; b.MovedAt = Host.Now;
                p.Entity.Depth = -1; p.Entity.Search = null;
            }
            if (Ended) { Order(p, PlanSit, null, null, 0); continue; }
            if (System.MathF.Abs(p.Car.Speed) > 2f) b.MovedAt = Host.Now;
            StuckCheck(p, b);
            switch (Rules.Kind)
            {
                case MeleeKind.Race: BotRace(p, b); break;
                case MeleeKind.Ctf: BotCtf(p, b); break;
                default: BotCombat(p, b); break;
            }
        }
    }

    /// <summary>
    /// Gives the bot's entity its order for this frame. A new <paramref name="plan"/>, target or path is a fresh order (the
    /// AI chooses its tactic, plans its path); the same one carries on, as a script re-issues its order every frame.
    /// </summary>
    private void Order(MeleePlayer p, int plan, Entity? target, List<Vector3>? path, float speed, Entity? follow = null, float behind = 0)
    {
        var b = p.Brain!;
        var e = p.Entity;
        b.Plan = plan;
        switch (plan)
        {
            case PlanAttack: Host.Order(e, target, AiOrder.Attack, null, 0); break;
            case PlanGoto: Host.Order(e, null, AiOrder.Goto, path, speed); break;
            case PlanRace: Host.Order(e, null, AiOrder.Race, path, speed); break;
            case PlanSit: Host.Order(e, null, AiOrder.Sit, null, 0); break;
            case PlanFollow: Host.Order(e, follow, AiOrder.Follow, null, speed, 0, 0, -behind, follow); break;
        }
    }

    /// <summary>The speed a bot is told to hold along a path: its tier's, the AI slowing for corners as it does (the Cruise speed) whatever it is told.</summary>
    private static float PathSpeed(MeleePlayer p) => p.BotSkill switch { >= 4 => 32f, 3 => 48f, _ => 1000f };

    /// <summary>
    /// A bot that has hardly moved for a while is stuck (a pit, a pinch, a wall across the way the A* did not know of): its
    /// collision avoidance, off in a race for the sake of the pace, is held on for a few seconds to back it out, and one that
    /// does not get out in <see cref="GiveUpSeconds"/> blows itself up, SELF_DESTRUCT as a person stuck would, and comes back
    /// at a spawn point.
    /// </summary>
    private void StuckCheck(MeleePlayer p, BotBrain b)
    {
        var at = p.Car.Pos.ToVector3();
        if (Host.Now >= b.SampleAt)
        {
            b.StillSeconds = Dist2(at, b.SamplePos) < 3f * 3f ? b.StillSeconds + 1 : 0;
            b.SamplePos = at;
            b.SampleAt = Host.Now + 1f;
            if (b.StillSeconds == StuckSeconds) b.AvoidUntil = Host.Now + 10f;
            if (b.StillSeconds >= GiveUpSeconds) { b.StillSeconds = 0; b.CarefulRace = !b.CarefulRace; Explode?.Invoke(p.Car); }
        }
        // a race goes without the speculative collision test unless it is stuck (it slows a fast car at every bump of the ground)
        p.Entity.Avoid = Rules.Kind != MeleeKind.Race || b.CarefulRace || Host.Now < b.AvoidUntil;
    }

    private const int StuckSeconds = 4, GiveUpSeconds = 40;

    // ---- seeing ----

    private bool Alive(MeleePlayer q) => !q.Destroyed && !Damage.DamageModel.IsDead(q.Car);

    /// <summary>The cars a bot may attack: the other players of another side (any, in a free for all), and the AI fill.</summary>
    private IEnumerable<Entity> Enemies(MeleePlayer me)
    {
        foreach (var q in Players)
        {
            if (q == me || !Alive(q)) continue;
            if (Rules.TeamPlay && q.Team == me.Team) continue;
            yield return q.Entity;
        }
        foreach (var o in _opponents)
            if (!o.Dead && !o.Removed) yield return o;
    }

    private static float Dist2(Entity a, Entity b) { var d = a.Position - b.Position; return d.X * d.X + d.Z * d.Z; }

    private Entity? NearestEnemy(MeleePlayer me, Func<Entity, bool>? where = null)
    {
        Entity? best = null; float bd = float.MaxValue;
        foreach (var o in Enemies(me))
        {
            if (where is not null && !where(o)) continue;
            float d = Dist2(o, me.Entity);
            if (d < bd) { bd = d; best = o; }
        }
        return best;
    }

    /// <summary>
    /// A bot on its way somewhere shoots what it passes: the nearest enemy within <paramref name="range"/> that is ahead (the AI's fire
    /// decision, the one an attack order ends in, then judges the aim, as it does a teammate in the line).
    /// </summary>
    private void Shoot(MeleePlayer p, float range, float cone = 0.5f)
    {
        var fwd = p.Car.Rot.Forward;
        var at = p.Entity.Position;
        Entity? best = null; float bd = range * range;
        foreach (var o in Enemies(p))
        {
            var d = o.Position - at;
            float d2 = d.X * d.X + d.Z * d.Z;
            if (d2 >= bd || d2 < 1f) continue;
            if (Vector3.Dot(Vector3.Normalize(d), fwd) < cone) continue;
            bd = d2; best = o;
        }
        if (best is not null) Host.FireAt(p.Entity, best);
    }

    // ---- going ----

    /// <summary>A goto along a one-point path; the same list object while the destination stands, so the AI keeps its plan.</summary>
    private void Go(MeleePlayer p, Vector3 dest, float? speed = null)
    {
        var b = p.Brain!;
        float d = System.MathF.Sqrt(Dist2(p.Entity.Position, dest));
        if (b.Plan != PlanGoto || b.Path is null || Vector3.DistanceSquared(b.Dest, dest) > 20f * 20f)
        {
            p.Entity.OwnPath = b.Path = [dest];
            (b.Dest, b.BestDist, b.BestAt, b.Gridded) = (dest, d, Host.Now, false);
        }
        else if (d < b.BestDist - 30f) (b.BestDist, b.BestAt) = (d, Host.Now);
        else if (!b.Gridded && d > 25f && Host.Now - b.BestAt > GridAfterSeconds)
        {
            // not getting nearer: the planner's pool of 1000 nodes may not reach round the wall; the grid's route goes by points it can
            b.BestAt = Host.Now;
            if (Ai.GridRoute.Between(World.Ground, p.Car.Pos.ToVector3(), dest) is { } route)
            {
                p.Entity.OwnPath = b.Path = route;
                b.Gridded = true;
            }
        }
        else if (b.Gridded && Host.Now - b.BestAt > GridForSeconds)
        {
            // the grid's route did not do it either (or it did, and the goal has moved on): the planner's again
            p.Entity.OwnPath = b.Path = [dest];
            (b.BestDist, b.BestAt, b.Gridded) = (d, Host.Now, false);
        }
        Order(p, PlanGoto, null, b.Path, speed ?? PathSpeed(p));
    }

    /// <summary>How long a goto may go without getting 30 m nearer before the grid's route is tried, and how long that is given.</summary>
    private const float GridAfterSeconds = 25f, GridForSeconds = 90f;

    /// <summary>Whether the bot needs a regen pad: hurt, and 30 s past its last, with a pad to go to.</summary>
    private bool NeedsRegen(MeleePlayer p) => Regens.Count > 0 && Host.Now - p.RegenAt >= 30f && p.Entity.Health < 40f;

    private Vector3? NearestRegen(MeleePlayer p)
    {
        Vector3? best = null; float bd = float.MaxValue;
        var at = p.Entity.Position;
        foreach (var r in Regens)
        {
            float d = Vector3.DistanceSquared(r.Pos.ToVector3(), at);
            if (d < bd) { bd = d; best = r.Pos.ToVector3(); }
        }
        return best;
    }

    /// <summary>With nobody to fight: a drive to a spawn point across the arena, then another.</summary>
    private void Wander(MeleePlayer p, BotBrain b)
    {
        var table = SpawnTable(p.Team);
        if (table.Count == 0) { Order(p, PlanSit, null, null, 0); return; }
        if (b.Plan != PlanGoto || b.Path is null || Dist2(p.Entity.Position, b.Dest) < 30f * 30f || Host.Now - b.MovedAt > 6f)
        {
            var dest = table[Rand.Next() % table.Count].Pos.ToVector3();
            p.Entity.OwnPath = b.Path = [dest]; b.Dest = dest; b.MovedAt = Host.Now;
        }
        Order(p, PlanGoto, null, b.Path, PathSpeed(p));
    }

    private static float Dist2(Vector3 a, Vector3 b) { var d = a - b; return d.X * d.X + d.Z * d.Z; }

    // ---- combat ----

    private void BotCombat(MeleePlayer p, BotBrain b)
    {
        // hurt and a pad to be had: to the pad, shooting what is in the way
        if (NeedsRegen(p) && NearestRegen(p) is { } pad)
        {
            Go(p, pad);
            Shoot(p, 60f);
            return;
        }
        // the target stands while it lives, until a much nearer car turns up (so two near ones do not flip the tactic every frame)
        if (b.Target is { } t && (t.Dead || t.Removed || !IsEnemyOf(p, t))) b.Target = null;
        if (b.Target is null || Host.Now >= b.RetargetAt)
        {
            var near = NearestEnemy(p);
            if (near is not null && (b.Target is null || Dist2(near, p.Entity) < 0.6f * Dist2(b.Target, p.Entity))) b.Target = near;
            b.RetargetAt = Host.Now + 1.5f;
        }
        if (b.Target is { } target) Order(p, PlanAttack, target, null, 0);
        else Wander(p, b);
    }

    private bool IsEnemyOf(MeleePlayer me, Entity o)
    {
        foreach (var q in Players)
            if (q.Entity == o) return q != me && Alive(q) && !(Rules.TeamPlay && q.Team == me.Team);
        return _opponents.Contains(o);
    }

    // ---- race ----

    /// <summary>
    /// The race: the checkpoints from the next one the tracker wants, two laps' worth in a Race order (the AI plans its way
    /// from point to point and loops the list; a car that sat out a pass of 30 m is told to go back for it).
    /// </summary>
    private void BotRace(MeleePlayer p, BotBrain b)
    {
        if (Checkpoints.Count == 0) { Wander(p, b); return; }
        if (!_races.TryGetValue(p, out var tr)) _races[p] = tr = new RaceTracker(Checkpoints);
        if (!tr.HasCheckpoints) { Wander(p, b); return; }
        var e = p.Entity;
        bool issue = b.Plan != PlanRace || b.Path is null || b.Before is null || b.Owner is null;
        if (!issue)
        {
            // where the AI is in its route against where the tracker is: the AI moves on a point at 40 m, the tracker counts at 30
            int j = System.Math.Clamp(e.NavIndex, 0, b.Owner!.Length - 1);
            int k = b.Owner[j], cur = tr.Current;
            if (k >= b.Before!.Length - 2) issue = true;
            else if (cur == b.Before[k]) b.AheadSince = -1;
            else if (k >= 1 && cur == b.Before[k - 1])
            {
                if (b.AheadSince < 0) b.AheadSince = Host.Now;
                else if (Host.Now - b.AheadSince > 2.5f) issue = true;
            }
            else if (cur == b.Before[k + 1]) b.AheadSince = -1;   // the tracker is a point on: the AI is still planning
            else issue = true;                                      // neither (a respawn, a reconfiguration): from the tracker's next
        }
        if (tr.Current != b.LastCur) { b.LastCur = tr.Current; b.ProgressAt = Host.Now; }
        else if (Host.Now - b.ProgressAt > NoProgressSeconds) { b.ProgressAt = Host.Now; b.CarefulRace = !b.CarefulRace; Explode?.Invoke(p.Car); return; }
        if (issue)
        {
            var route = tr.Route(2 * (tr.Count + 1), out var before);
            // hops of at most RaceHop m: the AI's road routes walk a whole road when two far points lie on it, the long way round
            // as often as not, but a short hop is the short way
            var path = new List<Vector3>();
            var owner = new List<int>();
            var from = p.Car.Pos.ToVector3();
            for (int k = 0; k < route.Length; k++)
            {
                var to = route[k].ToVector3();
                int n = System.Math.Max(1, (int)System.MathF.Ceiling(System.MathF.Sqrt(Dist2(from, to)) / RaceHop));
                for (int i = 1; i <= n; i++) { path.Add(Vector3.Lerp(from, to, i / (float)n)); owner.Add(k); }
                from = to;
            }
            p.Entity.OwnPath = b.Path = path;
            b.Owner = [.. owner];
            b.Before = before;
            b.AheadSince = -1;
            // a fresh Race order starts at the point nearest ahead unless the racer has just arrived: at the first
            e.Arrived = true;
            e.Depth = -1;
        }
        Order(p, PlanRace, null, b.Path, PathSpeed(p));
        // a racer shoots what is in its way, as a person may: close, and in front (a shot at a car off to the side costs the turn)
        Shoot(p, 45f, 0.9f);
    }

    private const float RaceHop = 100000f;
    /// <summary>A racer that passes no checkpoint in this long is lost (it goes round and round, or in and out of a dead end): it blows itself up and starts again.</summary>
    private const float NoProgressSeconds = 90f;

    // ---- capture the flag ----

    private enum CtfRole { Raider, Defender, Escort }

    /// <summary>A bot's role among its team's bots (in seat order): a raider, a defender, a raider, an escort, a defender ...</summary>
    private CtfRole RoleOf(MeleePlayer p)
    {
        int n = 0;
        foreach (var q in Players)
        {
            if (q == p) break;
            if (q.IsBot && q.Team == p.Team) n++;
        }
        return (n % 5) switch { 0 => CtfRole.Raider, 1 => CtfRole.Defender, 2 => CtfRole.Raider, 3 => CtfRole.Escort, _ => CtfRole.Defender };
    }

    private void BotCtf(MeleePlayer p, BotBrain b)
    {
        if (Ctf is not { } ctf) { BotCombat(p, b); return; }
        var home = ctf.Flags.FirstOrDefault(f => f.Team == p.Team);
        var mine = ctf.Carried(p);
        // carrying: straight home to the stand of one's colour, shooting what is in the way
        if (mine is not null && home is not null)
        {
            Go(p, home.StubPos.ToVector3(), 1000f);
            Shoot(p, 50f);
            return;
        }
        if (NeedsRegen(p) && NearestRegen(p) is { } pad && home is not null && Dist2(p.Entity.Position, home.StubPos.ToVector3()) > 60f * 60f)
        {
            Go(p, pad);
            Shoot(p, 60f);
            return;
        }
        var role = RoleOf(p);
        // someone carries this team's flag away: after the carrier, all but the raiders still on their way to a flag that is near it
        var thief = ctf.Flags.FirstOrDefault(f => f.Team == p.Team && f.Carrier is { } c && c.Team != p.Team && Alive(c))?.Carrier;
        if (thief is not null && (role != CtfRole.Raider || Dist2(p.Entity.Position, thief.Entity.Position) < 150f * 150f))
        {
            Order(p, PlanAttack, thief.Entity, null, 0);
            b.Target = thief.Entity;
            return;
        }
        switch (role)
        {
            case CtfRole.Raider:
                var stand = NearestEnemyStand(ctf, p);
                if (stand is { } at) { Go(p, at); Shoot(p, 50f); return; }
                break;
            case CtfRole.Escort:
                var runner = Players.FirstOrDefault(q => q != p && q.Team == p.Team && Alive(q) && ctf.Carried(q) is not null);
                if (runner is not null) { Order(p, PlanFollow, null, null, 1000f, runner.Entity, 14f); Shoot(p, 50f); return; }
                break;
        }
        // a defender, or nothing better to do: meet what comes near the stand, else keep about it
        if (home is not null)
        {
            var stand = home.StubPos.ToVector3();
            var near = NearestEnemy(p, o => Dist2(o.Position, stand) < 110f * 110f);
            if (near is not null) { Order(p, PlanAttack, near, null, 0); b.Target = near; return; }
            if (role != CtfRole.Raider || Dist2(p.Entity.Position, stand) > 70f * 70f || NearestEnemyStand(ctf, p) is null)
            {
                if (Host.Now >= b.PatrolAt || b.Plan != PlanGoto)
                {
                    b.PatrolAt = Host.Now + 9f + (Rand.Next() % 600) / 100f;
                    b.Phase = (Rand.Next() % 628) / 100f;
                }
                var spot = stand + new Vector3(System.MathF.Sin(b.Phase), 0, System.MathF.Cos(b.Phase)) * 28f;
                Go(p, spot, PathSpeed(p) * 0.6f);
                Shoot(p, 50f);
                return;
            }
        }
        BotCombat(p, b);
    }

    /// <summary>The nearest enemy flag a bot could take now: at its stand (or dropped) and not carried.</summary>
    private static Vector3? NearestEnemyStand(CtfGame ctf, MeleePlayer p)
    {
        Vector3? best = null; float bd = float.MaxValue;
        var at = p.Car.Pos.ToVector3();
        foreach (var f in ctf.Flags)
        {
            if (f.Team == p.Team || f.Carrier is not null || f.State is not (CtfFlag.Home or CtfFlag.Dropped)) continue;
            var s = f.StubPos.ToVector3();
            float d = Dist2(s, at);
            if (d < bd) { bd = d; best = s; }
        }
        return best;
    }
}

/// <summary>How good a bot is at driving and shooting: the host form's Easy, Normal and Hard (<see cref="MeleeGame.SkillTier"/>).</summary>
public enum BotSkill : byte { Easy, Normal, Hard }

/// <summary>How a bot's name is marked: the driver's name (<c>I76.Formats.Shell.MeleeDrivers.Names</c>, the melee form's 53) and " (bot)".</summary>
public static class BotNames
{
    /// <summary>What marks a bot's name on the scoreboard and in the feed (no protocol field: the name carries it).</summary>
    public const string Suffix = " (bot)";

    public static bool IsBot(string name) => name.EndsWith(Suffix, StringComparison.Ordinal);
}
