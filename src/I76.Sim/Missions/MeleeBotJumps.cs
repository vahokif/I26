using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// Bots and jumps (docs/multiplayer.md "Bots"): a ramp is a way across the map for a bot whose car can take it. The routing is the
/// grid's (<see cref="GridRoute"/>) with every ramp the car can use as one more edge, from the ramp's line-up point to where the car comes
/// down; the bot takes it when that is a clearly shorter way than over the ground. The ramps are found and flown headless
/// (<see cref="JumpLinks"/>); a bot lines up on the ramp's axis, holds its speed in the window that lands well, keeps the wheel
/// straight over the ramp and does nothing in the air, and when it misses the line or the speed it gives the ramp up and the AI drives on.
/// </summary>
public sealed partial class MeleeGame
{
    private JumpLinks? _jumps;
    private int _plansThisFrame;
    /// <summary>The time the route searches with ramps took, and how many were made (for the log and the tests).</summary>
    public double PlanMilliseconds { get; private set; }
    public int Plans { get; private set; }
    /// <summary>Bots that committed to a ramp (were lined up and fast enough), and ones that gave the plan up before the run (for the log and the tests).</summary>
    public int JumpsCommitted { get; private set; }
    public int JumpsGivenUp { get; private set; }

    /// <summary>The map's jump links (found on first use; the story missions' AI never asks).</summary>
    public JumpLinks Jumps => _jumps ??= JumpLinks.Find(World.Ground);

    private float _jumpDt;
    /// <summary>The step the headless flights run at: the session's tick, fixed when the bots are prepared so that every machine flies the same.</summary>
    private float JumpDt => _jumpDt > 0 ? _jumpDt : _jumpDt = Host.FrameDelta;

    /// <summary>
    /// Flies the ramps that cross something for the cars the bots drive now (once, behind the loading card), so that no frame of play
    /// has to; a car met later (a bot that comes in as a human leaves) is flown when a bot first considers a ramp with it.
    /// </summary>
    public void PrepareJumps(float dt)
    {
        _jumpDt = dt;
        if (!BotsJump) return;
        var jl = Jumps;
        foreach (var l in jl.Links)
            if (l.Crossing)
                foreach (var p in Players)
                    if (p.IsBot) jl.Use(l, p.Car, dt);
    }

    /// <summary>Whether bots use ramps at all (the host's switch; tests turn it off to compare).</summary>
    public bool BotsJump { get; set; } = true;

    /// <summary>What a hop counts for on top of its flight, metres of driving: lining up, and the chance of missing.</summary>
    private const float JumpPenalty = 45f;
    /// <summary>A jump saves at least this much of the way over the ground (metres, and a fraction of it) to be taken.</summary>
    private const float JumpSavesMetres = 60f, JumpSavesFraction = 0.2f;
    /// <summary>How far before the foot a bot must be on the axis, with the speed, or it gives the ramp up; and the tolerances.</summary>
    private const float JumpGate = 22f, JumpLateral = 2f, JumpHeading = 0.1f;

    private static readonly I76.Sim.Ai.AiBlock JumpDriver = MakeJumpDriver();
    private static I76.Sim.Ai.AiBlock MakeJumpDriver() { var a = new I76.Sim.Ai.AiBlock(); a.SetSkill(1, 1); return a; }

    private static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt(Dist2(a, b));

    /// <summary>
    /// The bot's way to <paramref name="goal"/> over a ramp, when there is a clearly shorter one: true when it has the bot this frame
    /// (it gave the order or drove), false when the caller should order as usual.
    /// </summary>
    private bool BotJump(MeleePlayer p, BotBrain b, Vector3 goal)
    {
        if (!BotsJump) return false;
        var jl = Jumps;
        if (!jl.Any) return false;
        if (b.JumpPhase == 1) return DriveJump(p, b, jl, goal);
        if (b.JumpPhase != 0) return false;
        if (Host.Now < b.JumpPlanAt || _plansThisFrame > 0) return false;
        b.JumpPlanAt = Host.Now + 3f + 0.37f * (p.BotSlot % 8);
        var car = p.Car;
        var at = car.Pos.ToVector3();
        float dist = Flat(at, goal);
        // over open ground there is nothing to jump
        if (dist < 100f || !GridRoute.Blocked(World.Ground, at, goal)) return false;
        float dt = JumpDt;
        var hops = new List<GridRoute.Hop>();
        var uses = new Dictionary<int, JumpUse>();
        foreach (var l in jl.Links)
        {
            if (!l.Crossing || !l.Standing || l.Index == b.BanLink && Host.Now < b.BanUntil) continue;
            if (Flat(at, l.Approach) + Flat(l.Lip, goal) > dist * 1.5f + 150f) continue;
            if (jl.Use(l, car, dt) is not { } u) continue;
            uses[l.Index] = u;
            hops.Add(new GridRoute.Hop(l.Approach, u.Landing, Flat(l.Approach, u.Landing) + JumpPenalty, l.Index));
        }
        if (hops.Count == 0) return false;
        _plansThisFrame++;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = GridRoute.Find(World.Ground, at, goal, hops);
        if (plan?.Hop is not { } hop) return false;
        var ground = GridRoute.Find(World.Ground, at, goal, null);
        PlanMilliseconds += sw.Elapsed.TotalMilliseconds; Plans++;
        if (ground is not null && (ground.Cost - plan.Cost < JumpSavesMetres || plan.Cost > (1f - JumpSavesFraction) * ground.Cost)) return false;
        var use = uses[hop.Tag];
        (b.JumpLink, b.JumpPhase, b.JumpSpeed, b.JumpLo, b.JumpHi, b.JumpGoal, b.JumpSince) = (hop.Tag, 1, use.Speed, use.MinSpeed, use.MaxSpeed, goal, Host.Now);
        p.Entity.OwnPath = b.Path = plan.Route;
        b.Plan = PlanNone;
        return DriveJump(p, b, jl, goal);
    }

    private void GiveUpJump(MeleePlayer p, BotBrain b, float ban)
    {
        b.BanLink = b.JumpLink; b.BanUntil = Host.Now + ban;
        JumpsGivenUp++;
        EndJump(p, b);
    }

    private void EndJump(MeleePlayer p, BotBrain b)
    {
        b.JumpPhase = 0; b.JumpLink = -1;
        b.JumpPlanAt = Host.Now + 2.5f;
        b.Plan = PlanNone; b.Path = null;
        p.Entity.Depth = -1; p.Entity.Search = null;
    }

    /// <summary>One frame of a bot on a ramp plan. False when the plan was given up and the caller orders as usual.</summary>
    private bool DriveJump(MeleePlayer p, BotBrain b, JumpLinks jl, Vector3 goal)
    {
        var l = jl.Links[b.JumpLink];
        var car = p.Car;
        if (!l.Standing) { GiveUpJump(p, b, 30f); return false; }
        var pos = car.Pos.ToVector3();
        var rel = new Vector2(pos.X - l.Foot.X, pos.Z - l.Foot.Z);
        var side = new Vector2(-l.Heading.Y, l.Heading.X);
        float along = Vector2.Dot(rel, l.Heading), lateral = Vector2.Dot(rel, side);
        float head = MathF.Atan2(l.Heading.X, l.Heading.Y);
        float dt = Host.FrameDelta;
        var ground = World.Ground;
        bool airborne = (car.Flags & VehicleFlags.Airborne) != 0;
        float TractionOf(float m) => World.Step.TractionLimit(car, m, World.IsFar(car));
        switch (b.JumpPhase)
        {
            case 1:
            {
                // on the way to the line-up point by the AI's own driving; the goal moving on or a long time gives it up
                if (Host.Now - b.JumpSince > 80f || Dist2(goal, b.JumpGoal) > 150f * 150f) { GiveUpJump(p, b, 20f); return false; }
                b.JumpTouched = Host.Now;
                float d = Flat(pos, l.Approach);
                bool onAxis = along > -l.LineUp - 15f && along < -JumpGate && MathF.Abs(lateral) < 12f;
                if (d < 30f || onAxis) { b.JumpPhase = 2; goto case 2; }
                Order(p, PlanGoto, null, b.Path, PathSpeed(p));
                Shoot(p, 60f);
                return true;
            }
            case 2:
            {
                if (Host.Now - b.JumpSince > 120f || along > -2f && MathF.Abs(lateral) > 6f) { GiveUpJump(p, b, 20f); return false; }
                float look = MathF.Max(12f, 0.8f * car.Speed);
                float aimAlong = MathF.Min(along + look, 0f);
                var aim = new Vector3(l.Foot.X + l.Heading.X * aimAlong + side.X * 0f, 0, l.Foot.Z + l.Heading.Y * aimAlong);
                float toAim = MathF.Abs(lateral) < 0.5f ? head : MathF.Atan2(aim.X - pos.X, aim.Z - pos.Z);
                float err = MathF.Abs(AiDriver.AngleDelta(AiDriver.Heading(car), head));
                AiDriver.Steer(car, JumpDriver, ground, toAim, dt);
                float want = MathF.Abs(lateral) > 4f || err > 0.35f ? MathF.Min(b.JumpSpeed, 12f) : b.JumpSpeed;
                AiDriver.Throttle(car, JumpDriver, ground, want, dt, TractionOf);
                Shoot(p, 60f);
                if (along > -JumpGate)
                {
                    bool ok = MathF.Abs(lateral) <= JumpLateral && err <= JumpHeading && car.Speed >= b.JumpLo + 0.2f && car.Speed <= b.JumpHi - 0.2f;
                    if (!ok) { GiveUpJump(p, b, 20f); return false; }
                    b.JumpPhase = 3; JumpsCommitted++;
                }
                return true;
            }
            case 3:
            {
                // the run: the wheel held to the axis, then straight over the ramp; the speed held to the target
                if (along < -6f) AiDriver.Steer(car, JumpDriver, ground, head, dt); else car.Steer = 0;
                AiDriver.Throttle(car, JumpDriver, ground, b.JumpSpeed, dt, TractionOf);
                if (airborne && along > 0f) { b.JumpPhase = 4; b.JumpAirAt = Host.Now; }
                else if (along > 70f) { GiveUpJump(p, b, 20f); return false; }
                return true;
            }
            default:
            {
                // in the air: nothing to steer or brake with
                car.Steer = 0; car.Throttle = 0;
                if (!airborne && Host.Now - b.JumpAirAt > 0.2f || Host.Now - b.JumpAirAt > 10f) { b.BanLink = b.JumpLink; b.BanUntil = Host.Now + 10f; EndJump(p, b); Landed?.Invoke(p, l); return false; }
                return true;
            }
        }
    }

    /// <summary>A bot has come down from a ramp (tests and logs).</summary>
    public event Action<MeleePlayer, JumpLink>? Landed;
}
