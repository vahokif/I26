using I76.Sim.Math;
using I76.Sim.Save;

namespace I76.Sim.Missions;

/// <summary>
/// A melee's state: the players (made again from the snapshot; their cars and entities are in the session's
/// tables), the roster's entities and slots, the kill credits, the waves, the race trackers, the CTF flags,
/// and the end.
/// </summary>
public sealed partial class MeleeGame
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<MeleeGame>()
            .Structural("derived from the players' bots", nameof(_botCars))
            .Saved(nameof(Ctf), nameof(_races), nameof(_overAt), nameof(Winner), nameof(WinningTeam), nameof(WinCount), nameof(TimeUp), nameof(_departed), nameof(Players), nameof(Aggression), nameof(AttackCap),
                nameof(PlayerDestroyed), nameof(_opponents), nameof(_rosterSlot), nameof(_credited), nameof(_alive), nameof(_endAt), nameof(_over), nameof(_started))
            .Structural("the arena's points, from the mission file", nameof(Spawns), nameof(Regens), nameof(TeamSpawns), nameof(Checkpoints))
            .Structural("the mission build's world, host, rand stream and car factory, and the session's roster and rules",
                nameof(World), nameof(Host), nameof(Rand), nameof(Build), nameof(Roster), nameof(PlayerCar), nameof(Rules), nameof(Explode))
            .Transient("the host's hooks", nameof(Message), nameof(LapCounted), nameof(Spawned), nameof(Removed), nameof(PlayerRenewed), nameof(Landed))
            .Derived("the map's ramps and what they do to the bots' cars, flown again from the map and the cars (a pure function of them)", nameof(_jumps), nameof(_jumpDt))
            .Structural("the host's switch for bots on ramps", nameof(BotsJump))
            .Transient("the ramp search's counters, for logs and tests, and its once-a-frame limit", nameof(Plans), nameof(PlanMilliseconds), nameof(JumpsCommitted), nameof(JumpsGivenUp), nameof(_plansThisFrame));
        a.For<MeleePlayer>()
            .Saved(nameof(MeleePlayer.Brain))
            .Saved(nameof(MeleePlayer.Car), nameof(MeleePlayer.Entity), nameof(MeleePlayer.CarFile), nameof(MeleePlayer.Name), nameof(MeleePlayer.Score),
                nameof(MeleePlayer.Kills), nameof(MeleePlayer.Deaths), nameof(MeleePlayer.Destroyed), nameof(MeleePlayer.RespawnAt), nameof(MeleePlayer.RegenAt),
                nameof(MeleePlayer.OffPad), nameof(MeleePlayer.Death), nameof(MeleePlayer.BotSkill), nameof(MeleePlayer.BotSlot), nameof(MeleePlayer.NextCar), nameof(MeleePlayer.CarEpoch), nameof(MeleePlayer.RespawnWanted), nameof(MeleePlayer.Paint));
        a.For<CtfGame>()
            .Saved(nameof(CtfGame.Flags), nameof(CtfGame.TeamCaptures))
            .Transient("the host's hooks", nameof(CtfGame.Message), nameof(CtfGame.Captured));
        a.For<CtfFlag>()
            .Saved(nameof(CtfFlag.State), nameof(CtfFlag.Pos), nameof(CtfFlag.Carrier))
            .Structural("the flag's placement", nameof(CtfFlag.Number), nameof(CtfFlag.Team), nameof(CtfFlag.HomePos), nameof(CtfFlag.StubRadius));
        RaceTracker.AuditFields(a);
        BotBrain.AuditFields(a);
    }

    internal void Save(SaveWriter w)
    {
        w.Bool(_started); w.NF32(_overAt); w.NF32(_endAt); w.Bool(_over); w.Int(_alive); w.Int(Aggression); w.Int(AttackCap); w.Bool(PlayerDestroyed);
        w.NInt(WinningTeam); w.Int(WinCount); w.Bool(TimeUp);
        w.VarU((ulong)_departed.Count); foreach (var v in _departed) w.Car(v);
        w.VarU((ulong)Players.Count);
        foreach (var p in Players)
        {
            w.Car(p.Car); w.Ent(p.Entity); w.Str(p.CarFile); w.Str(p.Name); w.Int(p.Score); w.Int(p.Kills); w.Int(p.Deaths); w.Bool(p.Destroyed);
            w.NF32(p.RespawnAt); w.F32(p.RegenAt); w.Bool(p.OffPad); w.Int((int)p.Death); w.Str(p.NextCar); w.Int(p.CarEpoch); w.Bool(p.RespawnWanted); w.Int(p.BotSkill); w.Int(p.BotSlot); w.Bytes(p.Paint?.ToBytes() ?? []);
            w.Bool(p.Brain is not null); p.Brain?.Save(w);
        }
        w.Int(Winner is null ? -1 : Players.IndexOf(Winner));
        w.VarU((ulong)_opponents.Count); foreach (var e in _opponents) w.Ent(e);
        var slots = _rosterSlot.OrderBy(kv => w.Refs.EntityIndex[kv.Key]).ToList();
        w.VarU((ulong)slots.Count); foreach (var (e, s) in slots) { w.Ent(e); w.Int(s); }
        var credited = _credited.OrderBy(e => w.Refs.EntityIndex[e]).ToList();
        w.VarU((ulong)credited.Count); foreach (var e in credited) w.Ent(e);
        var races = _races.Where(kv => Players.Contains(kv.Key)).OrderBy(kv => Players.IndexOf(kv.Key)).ToList();
        w.VarU((ulong)races.Count); foreach (var (p, t) in races) { w.Int(Players.IndexOf(p)); t.Save(w); }
        w.Bool(Ctf is not null);
        if (Ctf is { } ctf)
        {
            w.VarU((ulong)ctf.Flags.Count);
            foreach (var f in ctf.Flags) { w.Int(f.State); w.D3(f.Pos); w.Int(f.Carrier is null ? -1 : Players.IndexOf(f.Carrier)); }
            w.VarU((ulong)ctf.TeamCaptures.Count); foreach (var (t, n) in ctf.TeamCaptures.OrderBy(kv => kv.Key)) { w.Int(t); w.Int(n); }
        }
    }

    internal void Load(SaveReader r)
    {
        _started = r.Bool(); _overAt = r.NF32(); _endAt = r.NF32(); _over = r.Bool(); _alive = r.Int(); Aggression = r.Int(); AttackCap = r.Int(); PlayerDestroyed = r.Bool();
        WinningTeam = r.NInt(); WinCount = r.Int(); TimeUp = r.Bool();
        _departed.Clear(); for (int k = r.Count(); k > 0; k--) _departed.Add(r.Car()!);
        Players.Clear();
        _botCars.Clear();
        for (int k = r.Count(); k > 0; k--)
        {
            var car = r.Car()!; var ent = r.Ent()!; var file = r.Str() ?? ""; var name = r.Str() ?? "";
            int score = r.Int(), kills = r.Int(), deaths = r.Int();
            bool destroyed = r.Bool(); var respawnAt = r.NF32(); float regenAt = r.F32(); bool offPad = r.Bool();
            var death = (DeathCredit)r.Int(); var next = r.Str(); int epoch = r.Int(); bool wanted = r.Bool(); int skill = r.Int(), botSlot = r.Int();
            var paint = I76.Formats.Imaging.CarPaint.FromBytes(r.Bytes());
            var p = new MeleePlayer { Car = car, Entity = ent, CarFile = file, Name = name, BotSkill = skill, BotSlot = botSlot, Paint = paint };
            p.Score = score; p.Kills = kills; p.Deaths = deaths; p.Destroyed = destroyed; p.RespawnAt = respawnAt; p.RegenAt = regenAt; p.OffPad = offPad;
            p.Death = death; p.NextCar = next; p.CarEpoch = epoch; p.RespawnWanted = wanted;
            if (r.Bool()) p.Brain = BotBrain.Load(r);
            Players.Add(p);
            if (p.IsBot) _botCars.Add(car);
        }
        MeleePlayer? Player(int i) => i == -1 ? null : i >= 0 && i < Players.Count ? Players[i] : throw new SnapshotFormatException($"melee player {i} of {Players.Count}");
        Winner = Player(r.Int());
        _opponents.Clear(); for (int k = r.Count(); k > 0; k--) _opponents.Add(r.Ent()!);
        _rosterSlot.Clear(); for (int k = r.Count(); k > 0; k--) _rosterSlot[r.Ent()!] = r.Int();
        _credited.Clear(); for (int k = r.Count(); k > 0; k--) _credited.Add(r.Ent()!);
        _races.Clear();
        for (int k = r.Count(); k > 0; k--) { var p = Player(r.Int())!; var t = new RaceTracker(Checkpoints); t.Load(r); _races[p] = t; }
        if (r.Bool() != (Ctf is not null)) throw r.Mismatch("capture the flag");
        if (Ctf is { } ctf)
        {
            if (r.Count() != ctf.Flags.Count) throw r.Mismatch("the CTF flags");
            foreach (var f in ctf.Flags) { f.State = r.Int(); f.Pos = r.D3(); f.Carrier = Player(r.Int()); }
            ctf.TeamCaptures.Clear(); for (int k = r.Count(); k > 0; k--) ctf.TeamCaptures[r.Int()] = r.Int();
        }
    }
}

public sealed partial class RaceTracker
{
    internal static void AuditFields(SaveAudit a) =>
        a.For<RaceTracker>()
            .Saved(nameof(_cur), nameof(Laps))
            .Structural("the arena's checkpoints", nameof(_cp), nameof(_finish));

    internal void Save(SaveWriter w) { w.Int(_cur); w.Int(Laps); }
    internal void Load(SaveReader r) { _cur = r.Int(); Laps = r.Int(); }
}
