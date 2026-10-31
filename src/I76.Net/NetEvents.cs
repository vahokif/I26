using System.Numerics;

namespace I76.Net;

public enum EventKind : byte
{
    Shot = 1, Impact, Explosion, Click, ZoneDamaged, Killed, Wrecked, TyreBlown, PartFlung, Debris, SignKnocked, StructureDestroyed,
    CarSpawned, CarRemoved, PlayerJoined, PlayerLeft, Score, Kill, Renewed, Chat, GameOver, Misfire, Sound,
    // protocol 13
    CarChanged, Ended,
    // protocol 15: a co-op mission's script (Coop.cs)
    Radio, RadioCut, Objective, Movie, MissionEnd, Siren,
    // protocol 19: the movie is over for everyone
    MovieEnd,
}

/// <summary>
/// Something that happened on the server in one tick, sent reliably and in
/// order: what the client's views show and play (shots, impacts, explosions,
/// damage, deaths, structures), and the game's own news (cars coming and
/// going, players, scores, kills, respawns, chat, the end). Cars are named
/// by their id's serial, structures by theirs.
/// </summary>
public abstract record NetEvent
{
    public abstract EventKind Kind { get; }
    protected abstract void Body(NetWriter w);

    public void Write(NetWriter w) { w.U8((byte)Kind); Body(w); }

    public static NetEvent Read(ref NetReader r)
    {
        var k = (EventKind)r.U8();
        return k switch
        {
            EventKind.Shot => new ShotEvent((int)r.VarU(), r.U8(), r.Vec(), r.Dir()),
            EventKind.Impact => new ImpactEvent(r.Name(), r.Name(), r.Vec(), r.U8(), r.U8(), r.Vec(), r.HalfVec(), r.HalfVec()),
            EventKind.Explosion => new ExplosionEvent(r.Name(), r.Vec()),
            EventKind.Click => new ClickEvent((int)r.VarU(), r.Name()),
            EventKind.ZoneDamaged => new ZoneEvent((int)r.VarU(), r.U8(), r.U8()),
            EventKind.Killed => new KilledEvent((int)r.VarU(), r.Bool()),
            EventKind.Wrecked => new WreckedEvent((int)r.VarU()),
            EventKind.TyreBlown => new TyreEvent((int)r.VarU(), r.U8()),
            EventKind.PartFlung => new FlungEvent((int)r.VarU(), r.U8(), r.HalfVec()),
            EventKind.Debris => new DebrisEvent(r.Vec(), r.HalfVec()),
            EventKind.SignKnocked => new SignEvent((int)r.VarU(), r.HalfVec()),
            EventKind.StructureDestroyed => StructureEvent.Read(ref r),
            EventKind.CarSpawned => new SpawnEvent(CarInfo.Read(ref r)),
            EventKind.CarRemoved => new RemoveEvent((int)r.VarU()),
            EventKind.PlayerJoined => new JoinedEvent(r.U8(), r.Str(), r.U8()),
            EventKind.PlayerLeft => new LeftEvent(r.U8(), r.Str()),
            EventKind.Score => new ScoreEvent(r.U8(), r.VarI(), (int)r.VarU(), (int)r.VarU()),
            EventKind.Kill => new KillEvent((int)r.VarU(), (int)r.VarU(), r.VarI()),
            EventKind.Renewed => new RenewedEvent((int)r.VarU(), r.Bool()),
            EventKind.Chat => new ChatEvent(r.I8(), r.Str()),
            EventKind.GameOver => GameOverEvent.ReadBody(ref r),
            EventKind.Misfire => new MisfireEvent((int)r.VarU(), r.U8()),
            EventKind.Sound => new SoundEvent((int)r.VarU(), r.Name()),
            EventKind.CarChanged => new CarChangedEvent(CarInfo.Read(ref r), r.U8()),
            EventKind.Ended => new EndEvent(r.Bool(), r.I8(), r.U8(), r.VarI()),
            EventKind.Radio => new RadioEvent(r.Name(), r.VarI(), r.U8()),
            EventKind.RadioCut => new RadioCutEvent(),
            EventKind.Objective => new ObjectiveEvent(r.U8(), r.U8()),
            EventKind.Movie => new MovieEvent(r.Str()),
            EventKind.MissionEnd => new MissionEndEvent(r.Bool(), r.VarI(), r.Str()),
            EventKind.Siren => new SirenEvent((int)r.VarU(), r.Bool()),
            EventKind.MovieEnd => new MovieEndEvent(),
            _ => throw new WireException($"unknown event {k}"),
        };
    }
}

/// <summary>A weapon fired (ShotFired): the car, its weapon (<see cref="RoundState.Weapon"/>'s numbering), the muzzle and the direction.</summary>
public sealed record ShotEvent(int Car, byte Weapon, Vector3 Muzzle, Vector3 Forward) : NetEvent
{
    public override EventKind Kind => EventKind.Shot;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.U8(Weapon); w.Vec(Muzzle); w.Dir(Forward); }
}

/// <summary>A round's hit (Impact). Victim: 0 the ground, 1 a car, 2 a structure.</summary>
public sealed record ImpactEvent(string Effect, string Sound, Vector3 At, byte Victim, byte RoundKind, Vector3 Hit, Vector3 N, Vector3 Dir) : NetEvent
{
    public override EventKind Kind => EventKind.Impact;
    protected override void Body(NetWriter w) { w.Name(Effect); w.Name(Sound); w.Vec(At); w.U8(Victim); w.U8(RoundKind); w.Vec(Hit); w.HalfVec(N); w.HalfVec(Dir); }
}

/// <summary>An explosion by its .xdf.</summary>
public sealed record ExplosionEvent(string Effect, Vector3 At) : NetEvent
{
    public override EventKind Kind => EventKind.Explosion;
    protected override void Body(NetWriter w) { w.Name(Effect); w.Vec(At); }
}

public sealed record ClickEvent(int Car, string Sound) : NetEvent
{
    public override EventKind Kind => EventKind.Click;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.Name(Sound); }
}

public sealed record ZoneEvent(int Car, byte Zone, byte Level) : NetEvent
{
    public override EventKind Kind => EventKind.ZoneDamaged;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.U8(Zone); w.U8(Level); }
}

public sealed record KilledEvent(int Car, bool DriverKilled) : NetEvent
{
    public override EventKind Kind => EventKind.Killed;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.Bool(DriverKilled); }
}

public sealed record WreckedEvent(int Car) : NetEvent
{
    public override EventKind Kind => EventKind.Wrecked;
    protected override void Body(NetWriter w) => w.VarU((uint)Car);
}

public sealed record TyreEvent(int Car, byte Slot) : NetEvent
{
    public override EventKind Kind => EventKind.TyreBlown;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.U8(Slot); }
}

public sealed record FlungEvent(int Car, byte Slot, Vector3 Velocity) : NetEvent
{
    public override EventKind Kind => EventKind.PartFlung;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.U8(Slot); w.HalfVec(Velocity); }
}

public sealed record DebrisEvent(Vector3 At, Vector3 Velocity) : NetEvent
{
    public override EventKind Kind => EventKind.Debris;
    protected override void Body(NetWriter w) { w.Vec(At); w.HalfVec(Velocity); }
}

/// <summary>A sign knocked flying: the structure's serial (its place in the static world, from 1).</summary>
public sealed record SignEvent(int Structure, Vector3 Velocity) : NetEvent
{
    public override EventKind Kind => EventKind.SignKnocked;
    protected override void Body(NetWriter w) { w.VarU((uint)Structure); w.HalfVec(Velocity); }
}

/// <summary>
/// A structure lost nodes or its root: its serial, its whole destroyed state
/// (<see cref="StructureState"/>'s mask) and the explosions that went with it
/// for their sounds (−1 the root's, n node n's).
/// </summary>
public sealed record StructureEvent(int Structure, byte[] Mask, int[] Booms) : NetEvent
{
    public override EventKind Kind => EventKind.StructureDestroyed;
    protected override void Body(NetWriter w)
    {
        new StructureState(Structure, Mask).Write(w);
        w.VarU((uint)Booms.Length);
        foreach (int b in Booms) w.VarI(b);
    }

    public static StructureEvent Read(ref NetReader r)
    {
        var s = StructureState.Read(ref r);
        var booms = new int[JoinAccept.Count(ref r, 4096)];
        for (int i = 0; i < booms.Length; i++) booms[i] = r.VarI();
        return new StructureEvent(s.Serial, s.Mask, booms);
    }

    public bool Equals(StructureEvent? o) => o is not null && Structure == o.Structure && Mask.AsSpan().SequenceEqual(o.Mask) && Booms.AsSpan().SequenceEqual(o.Booms);
    public override int GetHashCode() => Structure;
}

/// <summary>A car came into the arena (a player joining, the AI fill's next car).</summary>
public sealed record SpawnEvent(CarInfo Car) : NetEvent
{
    public override EventKind Kind => EventKind.CarSpawned;
    protected override void Body(NetWriter w) => Car.Write(w);
}

/// <summary>A car left the arena (a wreck cleared, a player gone).</summary>
public sealed record RemoveEvent(int Car) : NetEvent
{
    public override EventKind Kind => EventKind.CarRemoved;
    protected override void Body(NetWriter w) => w.VarU((uint)Car);
}

public sealed record JoinedEvent(byte Slot, string Name, byte Team) : NetEvent
{
    public override EventKind Kind => EventKind.PlayerJoined;
    protected override void Body(NetWriter w) { w.U8(Slot); w.Str(Name); w.U8(Team); }
}

public sealed record LeftEvent(byte Slot, string Reason) : NetEvent
{
    public override EventKind Kind => EventKind.PlayerLeft;
    protected override void Body(NetWriter w) { w.U8(Slot); w.Str(Reason); }
}

/// <summary>A player's score changed (a kill, a regen pad, a death).</summary>
public sealed record ScoreEvent(byte Slot, int Score, int Kills, int Deaths) : NetEvent
{
    public override EventKind Kind => EventKind.Score;
    protected override void Body(NetWriter w) { w.U8(Slot); w.VarI(Score); w.VarU((uint)Kills); w.VarU((uint)Deaths); }
}

/// <summary>The hit that destroyed a car, and who it scored for (0 for nobody).</summary>
public sealed record KillEvent(int Victim, int Attacker, int Points) : NetEvent
{
    public override EventKind Kind => EventKind.Kill;
    protected override void Body(NetWriter w) { w.VarU((uint)Victim); w.VarU((uint)Attacker); w.VarI(Points); }
}

/// <summary>A player's car renewed in place: a respawn after a death, or a regen pad.</summary>
public sealed record RenewedEvent(int Car, bool Respawn) : NetEvent
{
    public override EventKind Kind => EventKind.Renewed;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.Bool(Respawn); }
}

/// <summary>A chat line from a player's slot, or −1 for the server.</summary>
public sealed record ChatEvent(sbyte From, string Text) : NetEvent
{
    public override EventKind Kind => EventKind.Chat;
    protected override void Body(NetWriter w) { w.I8(From); w.Str(Text); }
}

/// <summary>
/// The melee is over: the final standings, best first (slot, score). <paramref name="NextRoundSeconds"/> (protocol 12) is
/// how long until the server starts another round on the same connections; 0 when it ends here.
/// </summary>
public sealed record GameOverEvent(IReadOnlyList<(byte Slot, int Score)> Standings, byte NextRoundSeconds = 0) : NetEvent
{
    public override EventKind Kind => EventKind.GameOver;
    protected override void Body(NetWriter w)
    {
        w.U8((byte)Standings.Count);
        foreach (var (slot, score) in Standings) { w.U8(slot); w.VarI(score); }
        w.U8(NextRoundSeconds);
    }

    internal static GameOverEvent ReadBody(ref NetReader r)
    {
        var list = new (byte, int)[r.U8()];
        for (int i = 0; i < list.Length; i++) list[i] = (r.U8(), r.VarI());
        return new GameOverEvent(list, r.U8());
    }
}

/// <summary>A driver's trigger found that hardpoint's weapon broken (wmiss.wav, for its own driver).</summary>
public sealed record MisfireEvent(int Car, byte Hardpoint) : NetEvent
{
    public override EventKind Kind => EventKind.Misfire;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.U8(Hardpoint); }
}

/// <summary>A sound riding on a car (collisions, landings), as SimDriver picks them.</summary>
public sealed record SoundEvent(int Car, string Name) : NetEvent
{
    public override EventKind Kind => EventKind.Sound;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.Name(Name); }
}

/// <summary>
/// A player's car became another car in place (a respawn after the player reconfigured it while dead; protocol 13): the
/// same serial, a new model or variant. A client rebuilds its copy at once, before the snapshots of it are read: the
/// own-car block's <see cref="OwnCar.Epoch"/> says which build a snapshot is of.
/// </summary>
public sealed record CarChangedEvent(CarInfo Car, byte Epoch) : NetEvent
{
    public override EventKind Kind => EventKind.CarChanged;
    protected override void Body(NetWriter w) { Car.Write(w); w.U8(Epoch); }
}

/// <summary>
/// The goal was reached or the time ran out (protocol 13); the game over follows 10 s later. Each machine says it in
/// its own words ("You win!" with the death-orbit camera, or "You lose, chump!"; the Nitro Pack on a time limit
/// "*** Time limit has elapsed!", everyone a winner). <paramref name="Winner"/> is the slot that reached it (−1 for
/// none), <paramref name="Team"/> the winning team in team play (0 otherwise), <paramref name="Count"/> its count in the
/// goal's measure.
/// </summary>
public sealed record EndEvent(bool TimeUp, sbyte Winner, byte Team, int Count) : NetEvent
{
    public override EventKind Kind => EventKind.Ended;
    protected override void Body(NetWriter w) { w.Bool(TimeUp); w.I8(Winner); w.U8(Team); w.VarI(Count); }
}

/// <summary>One tick's events.</summary>
public sealed record EventBatch(uint Tick, IReadOnlyList<NetEvent> Events)
{
    /// <param name="sized">Told each event and the bytes it took (the server's accounting).</param>
    public void Write(NetWriter w, Action<NetEvent, int>? sized = null)
    {
        w.U8((byte)MsgType.Events);
        w.U32(Tick);
        w.VarU((uint)Events.Count);
        foreach (var e in Events)
        {
            int at = w.Length;
            e.Write(w);
            sized?.Invoke(e, w.Length - at);
        }
    }

    public static EventBatch Read(ref NetReader r)
    {
        uint tick = r.U32();
        int n = JoinAccept.Count(ref r, 1 << 16);
        var list = new NetEvent[n];
        for (int i = 0; i < n; i++) list[i] = NetEvent.Read(ref r);
        return new EventBatch(tick, list);
    }
}
