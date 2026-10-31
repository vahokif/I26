using System.Globalization;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;

namespace I76.Cli;

/// <summary>
/// Play a mission headless: the simulation and its script at the fixed tick
/// (a SimSession), nobody at the wheel, printing what the script does.
///
///   i76 run MISSION [SECONDS] [--skip-at S] [--player-at X,Z[,YAW][@S][;...]] [--hurt NAME@S[:AMOUNT][;...]]
///
/// --skip-at presses the skip key (isKeypress) from that mission second on.
/// </summary>
public static class MissionRun
{
    public static int Run(GameInstall install, List<string> args)
    {
        string mission = args[1];
        float seconds = args.Count > 2 && !args[2].StartsWith("--") ? float.Parse(args[2], CultureInfo.InvariantCulture) : 120;
        int ki = args.IndexOf("--skip-at");
        float skipAt = ki >= 0 ? float.Parse(args[ki + 1], CultureInfo.InvariantCulture) : float.MaxValue;
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var msnPath = install.FindMission(mission) ?? throw new FileNotFoundException(mission);
        var w = new MissionWorld(vfs, asm, msnPath);
        // --melee CAR,CAR...: the arena as an instant melee with that roster (the player in its first car)
        int mi = args.IndexOf("--melee");
        var roster = mi >= 0 ? args[mi + 1].Split(',').ToList() : null;
        // --car X.vcf: the player's car as a scenario or trip gives it (on the placement flagged as the player)
        int ci = args.IndexOf("--car");
        var car = ci >= 0 ? args[ci + 1] : roster is null ? null : "valepre1.vcf";
        int sdi = args.IndexOf("--seed");   // --seed N: the rand stream's start (the game takes the clock)
        var setup = MissionSetup.Build(vfs, w, asm, car, melee: roster, rand: sdi >= 0 ? new I76.Sim.Rng.CrtRand(uint.Parse(args[sdi + 1])) : null);
        setup.Melee?.Start();
        if (setup.Script is not { } script) { Console.WriteLine("no script"); return 1; }
        int ri = args.IndexOf("--rate");   // --rate HZ: 20 (default), 25, 28, 30, 40 or 60
        var session = new SimSession(setup, rate: ri >= 0 ? SimRate.FromLabel(int.Parse(args[ri + 1])) : null);
        float Tick = session.TickSeconds;
        // --player-at X,Z[,YAW][@S][;X,Z,YAW@S...]: the player's car moved there before the first tick, or at mission second S
        // (game units, degrees from +Z toward +X); further stops at their own seconds
        int pai = args.IndexOf("--player-at");
        var stops = new Queue<string>(pai >= 0 ? args[pai + 1].Split(';') : []);
        string[] pas = stops.Count > 0 ? stops.Dequeue().Split('@') : [];
        float moveAt = pas.Length > 1 ? float.Parse(pas[1], CultureInfo.InvariantCulture) : -1;
        void MovePlayer()
        {
        if (pai >= 0 && pas.Length > 0 && setup.World.Player is { } moved && pas[0].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray() is { Length: >= 2 } pa)
        {
            float yaw = pa.Length > 2 ? pa[2] * MathF.PI / 180f : 0f;
            var f = new System.Numerics.Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
            moved.Pos = new I76.Sim.Math.Double3(pa[0], setup.Ground.BareHeight(pa[0], pa[1]), pa[1]);
            moved.Rot = new I76.Sim.Math.Rows(System.Numerics.Vector3.Cross(System.Numerics.Vector3.UnitY, f), System.Numerics.Vector3.UnitY, f);
            moved.Vel = default;
        }
        }
        if (moveAt < 0)
        {
            MovePlayer();
            if (stops.Count > 0 && (pas = stops.Dequeue().Split('@')).Length > 1) moveAt = float.Parse(pas[1], CultureInfo.InvariantCulture);
        }
        // the player's car is driven, by no keys at all
        if (setup.World.Player is { } driven) session.AddDriver(driven);
        var h = script.Host;
        if (Environment.GetEnvironmentVariable("I76_RUN_ACTORS") is not null)
        {
            foreach (var (label, _, handle) in script.Vm!.Actors) Console.WriteLine($"  actor {label} = {(handle as Entity)?.Name}");
            // each thread's start and its arguments, as argLocal reaches them (-2 the last), an actor's label where one fits
            if (Environment.GetEnvironmentVariable("I76_RUN_ACTORS") == "paths") { int pi = 0; foreach (var pa in script.Vm.PathsDebug) Console.WriteLine($"  nav {pi++}: {string.Join(" ", pa.Select(q => $"({q.X:0},{q.Z:0})"))}"); }
            foreach (var t in script.Vm.Threads)
            {
                int td = t.Fp - 1;
                var targs = Enumerable.Range(0, td).Select(k => t.Stack[k].Ptr is { Data: true } r ? (r.Index, script.Vm.Data[r.Index]) : (-1, 0))
                    .Select((iv, k) => $"{k - td - 1}=[{iv.Item1}]{iv.Item2}{(iv.Item2 >= 0 && iv.Item2 < script.Vm.Actors.Count ? "/" + script.Vm.Actors[iv.Item2].Label : "")}");
                Console.WriteLine($"  thread {t.Index} @{t.Pc}: {string.Join(" ", targs)}");
            }
        }

        string T() => $"[{h.Now,7:0.00}]";
        h.Logged += m => Console.WriteLine($"{T()} log: {m}");
        h.Cb.Started += l => Console.WriteLine($"{T()} cb: {l.Name} ({l.Length:0.0} s, prio {l.Priority}{(l.Speaker is Entity e ? ", " + e.Name : "")})");
        h.Objectives.Sound += s => Console.WriteLine($"{T()} objectives: {Describe(h.Objectives)}");
        h.Movie += m => Console.WriteLine($"{T()} movie {m}");
        Console.WriteLine($"{w.Mission.Name}: {setup.Cars.Count} cars, {setup.Entities.Count} entities, {script.Vm?.Threads.Count ?? 0} threads; player {setup.Cars[0].Definition}");
        if (setup.ObjectiveText is { } ot)
            for (int i = 0; i < ot.Objectives.Count; i++) Console.WriteLine($"  objective {i + 1}: {ot.Objectives[i].Text}{(ot.Objectives[i].Hidden ? " (hidden)" : "")}");
        setup.Damage.Killed += v => Console.WriteLine($"{T()} killed: {h.Of(v)?.Name}");
        if (Environment.GetEnvironmentVariable("I76_RUN_TRACE") is { } who0)
            foreach (var e in setup.Entities.Where(e => e.Vehicle is not null && e.Name.Contains(who0)))
                Console.WriteLine($"  {e.Name}: weapons " + string.Join(", ", e.Vehicle!.Armory?.Weapons.Select(w => $"{w.Def.Name} class {w.Def.Class} tier {w.Def.Tier} mount {w.MountKind} fwd {w.BaseRot.Forward.Z:0.00} ammo {w.Ammo}") ?? []));
        // --hurt NAME@S[:AMOUNT][;NAME@S[:AMOUNT]...]: at mission second S the first live car whose name contains NAME takes a round from the player
        // (AMOUNT on the round channel, default 1): its damage recorder sees the player attack it
        int hi = args.IndexOf("--hurt");
        var hurts = new List<(string Name, float At, int Amount)>();
        if (hi >= 0)
            foreach (var hurt in args[hi + 1].Split(';'))
                if (hurt.Split('@') is [var hn, var hrest])
                {
                    var parts = hrest.Split(':');
                    hurts.Add((hn, float.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1));
                }
        // --destroy NAME@S[;...]: at mission second S the first live structure whose name contains NAME is destroyed (the script's destroy-object action's way)
        int di = args.IndexOf("--destroy");
        var destroys = di >= 0 ? args[di + 1].Split(';').Select(d => d.Split('@')).Where(d => d.Length == 2)
            .Select(d => (Name: d[0], At: float.Parse(d[1], CultureInfo.InvariantCulture))).ToList() : [];
        var orders = new Dictionary<Entity, string>();
        var structHp = new Dictionary<I76.Sim.Collision.StaticObject, int[]>();
        var wpLists = new Dictionary<Entity, string>();
        var poked = new HashSet<string>();
        int shots = 0;
        var shotsBy = new Dictionary<I76.Sim.Vehicles.Vehicle, int>();
        int pendingT = 0;
        bool ignited = false;
        bool cam = false;
        int depth = 0;
        int frames = (int)(seconds / Tick);
        for (int f = 0; f < frames; f++)
        {
            for (int hk = hurts.Count - 1; hk >= 0; hk--)
            {
                var (hurtName, hurtAt, hurtAmount) = hurts[hk];
                if (h.Now < hurtAt || setup.Entities.FirstOrDefault(e => e.Vehicle is not null && !e.Dead && e.Name.Contains(hurtName)) is not { Vehicle: { } hv }) continue;
                var pk = new I76.Sim.Weapons.DamagePacket { Attacker = setup.World.Player, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
                pk.Ch[1] = hurtAmount;
                setup.Damage.Apply(hv, pk, System.Numerics.Vector3.Zero);
                Console.WriteLine($"{T()} hurt {hurtName} by {hurtAmount}");
                hurts.RemoveAt(hk);
            }
            for (int dk = destroys.Count - 1; dk >= 0; dk--)
            {
                if (h.Now < destroys[dk].At || setup.Entities.FirstOrDefault(e => e.Static is { Destroyed: false } && e.Name.Contains(destroys[dk].Name)) is not { Static: { } ds } de) continue;
                setup.Damage.DestroyStructure(ds);
                if (!de.Dead) continue;   // type 2's quartering survives one: again next tick
                Console.WriteLine($"{T()} destroyed {de.Name}");
                destroys.RemoveAt(dk);
            }
            if (moveAt >= 0 && h.Now >= moveAt)
            {
                MovePlayer(); moveAt = -1; Console.WriteLine($"{T()} player moved"); pendingT = 30;
                if (stops.Count > 0 && (pas = stops.Dequeue().Split('@')).Length > 1) moveAt = float.Parse(pas[1], CultureInfo.InvariantCulture);
            }
            if (pendingT > 0 && --pendingT == 0 && setup.World.Player is { } tp) Console.WriteLine($"{T()} T would pick {(setup.Combat.NearestEnemy(tp) is { } te ? h.Of(te)?.Name : "nothing")}, nearest gun {(setup.Combat.NearestGun(tp) is { } ng ? h.Of(ng)?.Name + " team " + h.Of(ng)?.Team + " contact " + setup.Combat.IsContact(tp, ng) : "-")}");
            if (Environment.GetEnvironmentVariable("I76_RUN_GLUE") is { } glue && h.Now >= 10.4 && setup.World.Player is { } gp
                && setup.Entities.FirstOrDefault(e => e.Vehicle is not null && e.Name.Contains(Environment.GetEnvironmentVariable("I76_RUN_DRIVE_ON") ?? "t03js01")) is { Vehicle: { } gt }
                && glue.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray() is [var gx, var gz])
            {
                // a debugging aid: the player's car kept at an offset (right, ahead) in Taurus's frame, moving as he does
                gp.Rot = gt.Rot; gp.Vel = gt.Vel;
                gp.Pos = gt.Pos + gt.Rot.ToWorld(new System.Numerics.Vector3(gx, 0, gz));
            }
            // I76_RUN_POKE=I=V@S[;...]: the script's data word I set to V at mission second S (to skip a phase a player would play)
            if (Environment.GetEnvironmentVariable("I76_RUN_POKE") is { } poke)
                foreach (var pk in poke.Split(';'))
                    if (pk.Split('@') is [var iv, var at] && iv.Split('=') is [var pi, var pv] && h.Now >= float.Parse(at, CultureInfo.InvariantCulture) && !poked.Contains(pk))
                    {
                        script.Vm!.Data[int.Parse(pi)] = int.Parse(pv); poked.Add(pk);
                        Console.WriteLine($"{T()} poke data[{pi}] = {pv}");
                    }
            // I76_RUN_HEAL=NAME[,NAME]: the armour and chassis pools of the cars whose name contains a NAME refilled every tick (an escort's work)
            if (Environment.GetEnvironmentVariable("I76_RUN_HEAL") is { } healWho)
                foreach (var he in setup.Entities.Where(e => e.Vehicle?.Health is not null && healWho.Split(',').Any(e.Name.Contains)))
                {
                    var hd = he.Vehicle!.Health!;
                    Array.Copy(hd.ArmourMax, hd.Armour, 4); Array.Copy(hd.ChassisMax, hd.Chassis, 4);
                }
            var input = new CarInput { Commands = h.Now >= skipAt ? CarCommands.Skip : 0 };
            // I76_RUN_START=S: the ignition key pressed once at mission second S (the player's engine starts off in some missions)
            if (Environment.GetEnvironmentVariable("I76_RUN_START") is { } ign && h.Now >= float.Parse(ign, CultureInfo.InvariantCulture) && !ignited) { input.Commands |= CarCommands.StartEngine; ignited = true; }
            // I76_RUN_DRIVE=AHEAD[,SIDE]: after the cut-scene a driver steers the player's car at the point AHEAD units
            // along Taurus's heading (negative: behind) and SIDE to his right, at his speed plus a closing term
            if (Environment.GetEnvironmentVariable("I76_RUN_DRIVE") is { } drv && h.Now >= 10.4 && setup.World.Player is { } dp
                && setup.Entities.FirstOrDefault(e => e.Vehicle is not null && e.Name.Contains(Environment.GetEnvironmentVariable("I76_RUN_DRIVE_ON") ?? "t03js01")) is { Vehicle: { } dt }
                && drv.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray() is { Length: >= 1 } dv)
            {
                var goal = dt.Pos.ToVector3() + dt.Rot.Forward * dv[0] + dt.Rot.Right * (dv.Length > 1 ? dv[1] : 0);
                var to = goal - dp.Pos.ToVector3();
                float want = MathF.Atan2(to.X, to.Z), have = MathF.Atan2(dp.Rot.Forward.X, dp.Rot.Forward.Z);
                float err = want - have; while (err > MathF.PI) err -= 2 * MathF.PI; while (err < -MathF.PI) err += 2 * MathF.PI;
                float dist = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
                float wantSpeed = dt.Speed + System.Math.Clamp(dist * 0.5f, -15f, 20f);
                float thr = System.Math.Clamp((wantSpeed - dp.Speed) * 0.3f, -1f, 1f);
                input.Buttons |= CarButtons.Analog;
                input.Steer = (short)(System.Math.Clamp(-err * 1.5f, -1f, 1f) * 32767);
                input.Throttle = (short)(thr * 32767);
            }
            session.Tick([input]);
            shots += setup.Combat.Events.Count(e => e is I76.Sim.Weapons.ShotFired);
            foreach (var sf in setup.Combat.Events.OfType<I76.Sim.Weapons.ShotFired>()) shotsBy[sf.Shooter] = shotsBy.GetValueOrDefault(sf.Shooter) + 1;
            if (Environment.GetEnvironmentVariable("I76_RUN_SHOTS") is not null)
                foreach (var im in setup.Combat.Events.OfType<I76.Sim.Weapons.Impact>().Where(i => i.Victim == setup.World.Player)) Console.WriteLine($"{T()} hit on the player: {im.Effect}");
                foreach (var sf in setup.Combat.Events.OfType<I76.Sim.Weapons.ShotFired>()) Console.WriteLine($"{T()} shot: {h.Of(sf.Shooter)?.Name} {sf.Weapon.Def.Name} -> {(setup.Combat.RadarTarget.GetValueOrDefault(sf.Shooter) is { } rt ? h.Of(rt)?.Name : "-")}");
            // I76_RUN_STRUCT=NAME[,NAME]: every change to a watched structure's root or node HP, with what hit it this tick
            if (Environment.GetEnvironmentVariable("I76_RUN_STRUCT") is { } stw)
                foreach (var se in setup.Entities.Where(e => e.Static is not null && stw.Split(',').Any(e.Name.Contains)))
                {
                    var so = se.Static!;
                    var hps = new[] { so.Hp }.Concat(so.Nodes.Select(n => n.Hp)).ToArray();
                    if (structHp.TryGetValue(so, out var was))
                        for (int k = 0; k < hps.Length; k++)
                            if (hps[k] != was[k])
                            {
                                var by = setup.Combat.Events.OfType<I76.Sim.Weapons.Impact>().Where(i => i.Victim == so).Select(i => $"{i.Effect}/{i.Kind}");
                                Console.WriteLine($"{T()} {se.Name} {(k == 0 ? "root" : so.Nodes[k - 1].Name)} hp {was[k]} -> {hps[k]}{(so.Destroyed ? " (destroyed)" : "")} [{string.Join(" ", by)}]");
                            }
                    structHp[so] = hps;
                }
            setup.Combat.Events.Clear();
            setup.Damage.Events.Clear();
            if (f % 280 == 0)
                Console.WriteLine($"{T()} {shots} shots so far; health " + string.Join(" ", setup.Entities.Where(e => e.Vehicle is not null || (Environment.GetEnvironmentVariable("I76_RUN_STRUCT") is { } sw && e.Static is not null && sw.Split(',').Any(e.Name.Contains))).Select(e => $"{e.Name}:{e.Health:0.#}{(e.Static is { Nodes.Count: > 0 } sn ? $"/nodes {sn.Nodes.Sum(n => n.Hp)}" : "")}{(e.Dead ? "†" : "")}")));
            foreach (var e in setup.Entities.Where(e => e.Vehicle is not null))
            {
                var o = e.Order is null ? "" : $"{e.Order} {e.OrderTarget?.Name} {(e.Path is { } p ? $"path {e.NavIndex}/{p.Count} @{e.OrderSpeed}" : "")}".TrimEnd();
                if (orders.GetValueOrDefault(e, "") != o) { orders[e] = o; Console.WriteLine($"{T()} {e.Name}: {o}"); }
            }
            if (Environment.GetEnvironmentVariable("I76_RUN_TRACE") is { } who && f % (int.TryParse(Environment.GetEnvironmentVariable("I76_RUN_EVERY"), out var ev) ? ev : 56) == 0)
                foreach (var e in setup.Entities.Where(e => e.Vehicle is not null && e.Name.Contains(who)))
                {
                    var v = e.Vehicle!;
                    var tgt = e.Path is { } pp && e.NavIndex < pp.Count ? pp[e.NavIndex] : default;
                    Console.WriteLine($"{T()}   {e.Name} at {v.Pos.X:0.0},{v.Pos.Y:0.0},{v.Pos.Z:0.0} v {v.Speed:0.0} thr {v.Throttle:0.00} steer {v.Steer:0.00} gear {v.Gear} flags 0x{(uint)v.Flags:x} surf {setup.World.Ground.SurfaceId(v.Pos.X, v.Pos.Z)} off {OffRoute(e, v):0.0} wp {e.WaypointIndex}/{e.Waypoints.Count}{(e.WaypointIndex < e.WaypointBridge.Count && e.WaypointBridge[e.WaypointIndex] ? " bridge" : "")} nav {e.NavIndex} plan {e.PlanState}{(e.ReachedLeg ? " leg" : "")}{(e.ReachedEnd ? " end" : "")}{(e.Arrived ? " arrived" : "")} fwd {v.Rot.Forward.X:0.00},{v.Rot.Forward.Z:0.00} st {string.Join(",", e.Stack)} [{string.Join(" > ", e.Stack.Select(h.Ptable.NameOf))}]{(e.OrderTarget is { } tgtE ? $" target {tgtE.Name} at {System.Numerics.Vector3.Distance(tgtE.Position, e.Position):0}" : "")}{(v.Emplacement is { } gun ? $" gun hp {gun.Hp}/{gun.MaxHp} on {gun.Enabled} rate {gun.Rate} shots {shotsBy.GetValueOrDefault(v)} aim {(I76.Sim.Vehicles.Emplacements.Gun(v) is { } gw ? $"{gw.AimYaw * 180 / MathF.PI:0}/{gw.AimPitch * 180 / MathF.PI:0}{(gw.OnTarget ? " on target" : "")} ammo {gw.Ammo}" : "-")} radar {(setup.Combat.RadarTarget.GetValueOrDefault(v) is { } rt ? h.Of(rt)?.Name : "-")} player at {System.Numerics.Vector3.Distance(v.Pos.ToVector3(), setup.World.Player!.Pos.ToVector3()):0}" : "")}{(v.Heli is { } hl ? $" heli hp {hl.Hp}/{hl.MaxHp} hold {hl.Height:0} above {v.Pos.Y - setup.World.Ground.Query(v.Pos.X, v.Pos.Y, v.Pos.Z).Height:0.0} thrust {hl.Thrust:0.00} side {hl.Side:0.00}" : "")}");
                }
            // I76_RUN_WAYPOINTS=NAME: each new waypoint list of the cars whose name contains NAME (x,z, b = a bridge point)
            if (Environment.GetEnvironmentVariable("I76_RUN_WAYPOINTS") is { } wpWho)
                foreach (var e in setup.Entities.Where(e => e.Vehicle is not null && e.Name.Contains(wpWho)))
                {
                    var wl = string.Join(" ", e.Waypoints.Select((q, k) => $"({q.X:0},{q.Z:0}{(k < e.WaypointBridge.Count && e.WaypointBridge[k] ? "b" : "")})"));
                    if (wpLists.GetValueOrDefault(e) != wl) { wpLists[e] = wl; Console.WriteLine($"{T()} {e.Name} waypoints: {wl}"); }
                }
            if (h.View.Active != cam) { cam = h.View.Active; Console.WriteLine($"{T()} camera {(cam ? "scripted" : "released")}"); }
            if (h.View.Depth != depth) { depth = h.View.Depth; Console.WriteLine($"{T()} camera stack {depth} ({h.View.Op})"); }
            if (script.Result is { } r)
            {
                var msg = h.End is { FailMessage: >= 0 and var m } && setup.ObjectiveText is { } t && m < t.Failures.Count ? ": " + t.Failures[m] : "";
                Console.WriteLine($"{T()} mission {(r ? "succeeded" : "failed")}{msg}");
                break;
            }
        }
        var pl = setup.World.Player!;
        Console.WriteLine($"{T()} end: player at {pl.Pos.X:0},{pl.Pos.Z:0} speed {pl.Speed:0.0} flags 0x{(uint)pl.Flags:x} order {setup.Entities.FirstOrDefault(e => e.Vehicle == pl)?.Order}; {Describe(h.Objectives)}; {script.Vm?.Threads.Count(t => t.Finished) ?? 0} threads finished");
        return 0;
    }

    /// <summary>Distance (xz) from the car to the polyline of its current waypoints, or -1.</summary>
    static float OffRoute(Entity e, I76.Sim.Vehicles.Vehicle v)
    {
        float best = -1;
        var p = new System.Numerics.Vector2((float)v.Pos.X, (float)v.Pos.Z);
        for (int i = 0; i + 1 < e.Waypoints.Count; i++)
        {
            var a = new System.Numerics.Vector2(e.Waypoints[i].X, e.Waypoints[i].Z); var b = new System.Numerics.Vector2(e.Waypoints[i + 1].X, e.Waypoints[i + 1].Z);
            var ab = b - a; float t = ab.LengthSquared() > 0 ? System.Math.Clamp(System.Numerics.Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            float d = System.Numerics.Vector2.Distance(p, a + ab * t);
            if (best < 0 || d < best) best = d;
        }
        return best;
    }

    private static string Describe(Objectives o) =>
        string.Join(", ", o.Flags.Select((f, i) => $"{i + 1} {Objectives.Describe(f)}"));
}
