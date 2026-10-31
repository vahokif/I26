using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Net;

/// <summary>A car as a client holds it: what it is, its copy in the mirror world, and the two poses the view is between.</summary>
public sealed class MirrorCar
{
    /// <summary>What the car is (a reconfigured respawn changes it in place: <see cref="ClientWorld.Rebuilt"/>).</summary>
    public required CarInfo Info { get; set; }
    public required Vehicle Vehicle { get; init; }
    /// <summary>The two states the car is drawn between (its own, <see cref="Track"/>): the game's wheels and roll follow them.</summary>
    public Rows PrevRot, Rot;
    public Double3 PrevPos, Pos;
    /// <summary>The car has a state to draw (a car the server has sent nothing of yet is not drawn).</summary>
    public bool Present;
    public bool IsPlayer => Info.Owner >= 0;
    /// <summary>The car the server named, when this install could not build it and <see cref="Info"/>'s is a stand-in.</summary>
    public string? StandInFor { get; init; }
    /// <summary>Its states as they came and its own clock (interest management sends far cars less often).</summary>
    public CarTrack Track { get; } = new();
    /// <summary>Where to draw it this frame (null: nowhere yet), and a count bumped when <see cref="PrevPos"/> and <see cref="Pos"/> change.</summary>
    public (Rows Rot, Double3 Pos)? Drawn;
    public int PoseVersion;
    internal CarState? Applied, PoseFrom, PoseTo;
}

/// <summary>
/// A client's copy of the arena, written from what the server sends: a
/// mirror <see cref="MissionSetup"/> that is never stepped, whose cars are
/// written from snapshots so that the game's existing views (cars, combat,
/// damage, cockpit, HUD) draw them as they draw a local game. The view runs
/// about 100 ms behind the newest snapshot; each car is drawn on its own clock
/// between its own states (<see cref="CarTrack"/>: a far car comes less often),
/// going on along its velocity a little when none is newer. The server's events are released when the view reaches their
/// tick: shots, impacts and explosions into the mirror's Combat.Events,
/// damage into its Damage.Events (the views take them from there), and the
/// game's news (players, scores, kills, chat, the end) to <see cref="News"/>.
/// </summary>
public sealed class ClientWorld
{
    public MissionSetup Setup { get; }
    public SimSession Session { get; }
    public int OwnSerial { get; }
    public Dictionary<int, MirrorCar> Cars { get; } = [];
    public MirrorCar? Own => Cars.GetValueOrDefault(OwnSerial);

    /// <summary>How far behind the newest snapshot the view runs.</summary>
    public double Delay { get; set; } = 0.1;
    /// <summary>The view's place in server ticks (fractional), the interval it is in, and how far into it.</summary>
    public double RenderTick { get; private set; } = -1;
    public uint IntervalFrom { get; private set; }
    public uint IntervalTo { get; private set; }
    public float Alpha { get; private set; } = 1;
    /// <summary>Frames with no snapshot newer than the view (the view held still).</summary>
    public int Starved { get; private set; }
    public bool GameOver { get; private set; }
    /// <summary>The own car is predicted (<see cref="OwnCarPredictor"/>): the snapshots the view reaches leave it alone.</summary>
    public bool Predicted { get; set; }
    /// <summary>
    /// The own shots are drawn at once from the predicted car (<see cref="Net.OwnShots"/>): the server's shot events for
    /// the own car are not drawn again, its own rounds are drawn only as this says, and its own flames are its own.
    /// </summary>
    public OwnShots? OwnShots { get; set; }
    /// <summary>The own car's prediction, when there is one: the cars near it are drawn at its present (<see cref="CarTrack.Present"/>).</summary>
    public OwnCarPredictor? Prediction { get; internal set; }
    /// <summary>How far the input clock is into its next tick (the predicted car is drawn there), set by the view before <see cref="Advance"/>.</summary>
    public float InputAlpha { get; set; } = 1;
    /// <summary>
    /// The own predicted car is drawn where the snapshots have it, as the others are (a co-op cut-scene: the host's script
    /// drives it, which the prediction cannot know); the prediction goes on underneath and takes over again after.
    /// </summary>
    public bool OwnFromSnapshots { get; set; }
    /// <summary>The cars near the own predicted car are drawn at its present rather than on their own clocks (I76_NET_NEAR=0 turns it off).</summary>
    public bool NearAtPresent { get; set; } = true;

    public event Action<MirrorCar>? Added;
    public event Action<MirrorCar>? Removed;
    /// <summary>A car renewed in place (true: a respawn after a death; false: a regen pad).</summary>
    public event Action<MirrorCar, bool>? Renewed;
    /// <summary>A car became another car in place (a player's reconfigured respawn): the views draw it anew.</summary>
    public event Action<MirrorCar>? Rebuilt;
    /// <summary>The build of the own car the mirror has (<see cref="OwnCar.Epoch"/>): a snapshot of another build is not written into it.</summary>
    public byte OwnEpoch { get; private set; }
    /// <summary>Everything that is not drawn by the mirror's views: players, scores, kills, chat, the end, misfires and sounds.</summary>
    public event Action<NetEvent>? News;
    /// <summary>What went wrong with a car or an event (a car built as a stand-in, a view that threw), for the game's log.</summary>
    public event Action<string>? Log;

    private readonly Dictionary<int, StaticObject> _structures = [];
    private readonly Dictionary<int, Round> _rounds = [];
    private readonly Dictionary<(int, byte), Flame> _flames = [];
    private readonly List<EventBatch> _waiting = [];
    /// <summary>Cars removed (serials are never reused within a round): a late answer about one does not bring it back.</summary>
    private readonly HashSet<int> _removed = [];
    /// <summary>Cars the snapshots name and the mirror lacks, with when each was first seen missing and last asked about.</summary>
    private readonly Dictionary<int, (double Since, double Asked)> _unknown = [];
    private readonly List<int> _missing = [];
    private readonly HashSet<int> _named = [];
    /// <summary>The join accept this mirror was built from: what the client receives belongs to this mirror only while it is the client's.</summary>
    private readonly JoinAccept _accept;
    private double _now;
    private readonly IMirrorCars _cars;
    private static readonly object CarVictim = new();
    private static readonly StaticObject StructureVictim = new() { Name = "", Type = 0 };

    /// <param name="setup">
    /// The arena set up as a network melee with no cars (MissionSetup.Build with an empty roster); a co-op mission as the
    /// server built it (its <see cref="CoopInfo.SeatCars"/>), its script never run.
    /// </param>
    /// <param name="cars">How the mirror builds and rebuilds a car; null: the network melee's (<see cref="MeleeMirrorCars"/>).</param>
    public ClientWorld(MissionSetup setup, JoinAccept accept, IMirrorCars? cars = null)
    {
        Setup = setup;
        setup.Combat.RemoteSim = true;   // the server decides who jams: a car's flag is the truth here
        _cars = cars ?? new MeleeMirrorCars(setup.Melee ?? throw new ArgumentException("the mirror must be set up as a network melee"));
        // the session numbers the structures (and a mission's own cars) as the server's does; it is never ticked
        Session = new SimSession(setup, SessionOptions.Network, accept.Rate);
        _cars.Prepare(Session);
        if (setup.World.Ground is IHasStatics hs)
            foreach (var o in hs.World.Objects) _structures[o.NetId & 0xffffff] = o;
        OwnSerial = accept.CarSerial;
        _accept = accept;
        foreach (var c in accept.Cars) Add(c);
        foreach (var s in accept.DestroyedStructures) if (_structures.TryGetValue(s.Serial, out var o)) o.ApplyDestroyedMask(s.Mask);
        if (accept.Coop is { } coop) { _seat = coop.Seat; JoinedCoop(coop); }
    }

    /// <summary>This player's seat in a co-op game (the personal camera scene to show), −1 outside one.</summary>
    private int _seat = -1;

    /// <summary>The co-op mission's presentation as the accept found it (a late joiner's): the objectives, the sirens, the end; the radio's line once the clock is known.</summary>
    private void JoinedCoop(CoopInfo c)
    {
        if (Setup.Script?.Host is not { } h) return;
        var flags = h.Objectives.Flags;
        for (int i = 0; i < flags.Length && i < c.Objectives.Count; i++) flags[i] = c.Objectives[i];
        foreach (var serial in c.Sirens) if (Car(serial) is { } v && h.Of(v) is { } e) e.Siren = true;
        if (c.End is { } end) h.EndMission(end.Won, 0, end.FailMessage);
        _radioAtJoin = c.Radio is { } r ? (r, c.RadioSeconds) : null;
    }

    private (RadioEvent Line, float Seconds)? _radioAtJoin;

    public StaticObject? Structure(int serial) => _structures.GetValueOrDefault(serial);

    private int _migrations;

    /// <summary>
    /// The game goes on under a new host (host migration): its cars as the new host restored them (a car it does not have
    /// goes, one the mirror lacks or had removed comes), its destroyed structures, and the view's clock taken afresh from
    /// the new connection's snapshots.
    /// </summary>
    private void Rejoined(JoinAccept a)
    {
        var want = a.Cars.Select(c => c.Serial).ToHashSet();
        foreach (var serial in Cars.Keys.Where(k => k != OwnSerial && !want.Contains(k)).ToList()) Guard($"car {serial}", () => Remove(serial));
        foreach (var c in a.Cars)
            if (!Cars.ContainsKey(c.Serial)) { _removed.Remove(c.Serial); Guard($"car {c.Serial}", () => Add(c)); }
        foreach (var st in a.DestroyedStructures) if (_structures.TryGetValue(st.Serial, out var o)) o.ApplyDestroyedMask(st.Mask);
        // a co-op mission's objectives, sirens and end as the new host has them (what came between the hosts is in them)
        if (a.Coop is { } coop && Setup.Script?.Host is { } h)
        {
            _seat = coop.Seat;
            var flags = h.Objectives.Flags;
            for (int i = 0; i < flags.Length && i < coop.Objectives.Count; i++) flags[i] = coop.Objectives[i];
            foreach (var e in h.Entities) if (e.Vehicle is { } v && v.NetId != 0) e.Siren = coop.Sirens.Contains(v.NetId & 0xffffff);
            if (coop.End is { } end && h.End is null) h.EndMission(end.Won, 0, end.FailMessage);
        }
        _ingested.Clear();
        _unknown.Clear();
        RenderTick = -1;
        Log?.Invoke($"the game goes on under a new host: {Cars.Count} cars");
    }
    public Vehicle? Car(int serial) => Cars.GetValueOrDefault(serial)?.Vehicle;
    public IEnumerable<StaticObject> DestroyedStructures => _structures.Values.Where(o => o.Destroyed || o.Nodes.Any(n => n.Destroyed));

    private void Add(CarInfo info)
    {
        if (Cars.ContainsKey(info.Serial) || _removed.Contains(info.Serial) || info.Def.Length == 0) return;
        if (info.Vcf is { } own) I76.Formats.Vfs.AssetVfs.Addon[info.Def] = own;       // a player's own variant, sent by the server
        if (Build(info) is not var (v, def)) return;
        string? wanted = null;
        if (def != info.Def)
        {
            Log?.Invoke($"car {info.Serial} ({info.Name}): {info.Def} cannot be built here; drawn as {def}");
            (wanted, info) = (info.Def, info with { Def = def });
        }
        v.NetId = (int)NetKind.Vehicle << 24 | info.Serial;
        v.Paint = I76.Formats.Imaging.CarPaint.FromBytes(info.Paint ?? []);
        v.Flags &= ~VehicleFlags.Lights;
        Setup.World.Vehicles.Add(v);
        if (info.Owner >= 0 && !Setup.World.Humans.Contains(v)) Setup.World.Humans.Add(v);
        // a co-op mission's car has its entity already (the script names it; the radio's speakers are entities)
        if (Setup.Script?.Host.Of(v) is { } known) known.Removed = false;
        else Setup.Script?.Host.Add(new Entity(info.Name, [], 1, (info.Owner >= 0 ? 0x10u : 0) | Entity.TeamFlags(info.Team)) { Vehicle = v });
        var m = new MirrorCar { Info = info, Vehicle = v, StandInFor = wanted };
        Cars[info.Serial] = m;
        _unknown.Remove(info.Serial);
        // what came of it before its spawn event was reached
        if (_snaps is not null)
            foreach (var s in _snaps.Values)
                foreach (var c in s.Cars)
                    if (c.Serial == info.Serial) { if (c.SampleTick == 0) c.SampleTick = s.Tick; m.Track.Add(c); }
        if (info.Serial == OwnSerial) Setup.World.Player = v;
        Added?.Invoke(m);
    }

    /// <summary>
    /// The car the server named, or a stand-in when this install cannot build it (a model or part it lacks: another game's
    /// car, a mod's): the same model's first variant, else the default car. A player drawn as another car is still seen;
    /// one not drawn at all is a player nobody can find. Null only when not even the default car builds.
    /// </summary>
    private (Vehicle, string)? Build(CarInfo info)
    {
        var stock = System.Text.RegularExpressions.Regex.Replace(info.Def, @"\d+\.vcf$", "1.vcf", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string? why = null;
        foreach (var def in new[] { info.Def, stock, StandInCar }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { return (_cars.Build(info.Serial, def, info.Owner >= 0), def); }
            catch (Exception ex) { why ??= ex.Message; }
        }
        Log?.Invoke($"car {info.Serial} ({info.Name}): {info.Def} cannot be built here ({why}), nor a stand-in");
        return null;
    }

    /// <summary>The car drawn for one this install cannot build (the server's default car).</summary>
    public const string StandInCar = "vppirna1.vcf";

    /// <summary>
    /// A car rebuilt as another in place (the server's <see cref="CarChangedEvent"/>): the mirror's vehicle object stays (the
    /// views' tables and the prediction's driver are keyed by it) and takes every field of a fresh build of the new car
    /// (<see cref="IMirrorCars.Rebuild"/>), keeping where it is and that it is dead until the respawn's snapshot moves it.
    /// </summary>
    private void Change(CarChangedEvent c)
    {
        if (!Cars.TryGetValue(c.Car.Serial, out var m)) return;
        var info = c.Car;
        if (info.Vcf is { } own) I76.Formats.Vfs.AssetVfs.Addon[info.Def] = own;
        if (Build(info) is not var (fresh, def)) return;
        if (def != info.Def) Log?.Invoke($"car {info.Serial} ({info.Name}): {info.Def} cannot be built here; drawn as {def}");
        var v = m.Vehicle;
        var (rot, pos, vel, ang, flags, wreck) = (v.Rot, v.Pos, v.Vel, v.AngVel, v.Flags, v.Wreck);
        _cars.Rebuild(v, fresh, info.Owner >= 0);
        (v.Rot, v.Pos, v.Vel, v.AngVel, v.Flags, v.Wreck) = (rot, pos, vel, ang, flags, wreck);
        v.Flags &= ~VehicleFlags.Lights;
        v.Paint = I76.Formats.Imaging.CarPaint.FromBytes(info.Paint ?? []);
        m.Info = info with { Def = def };
        m.Applied = null;
        if (info.Serial == OwnSerial) OwnEpoch = c.Epoch;
        Rebuilt?.Invoke(m);
    }

    private void Remove(int serial)
    {
        _removed.Add(serial);
        _unknown.Remove(serial);
        if (!Cars.Remove(serial, out var m)) return;
        Setup.World.Vehicles.Remove(m.Vehicle);
        Setup.World.Humans.Remove(m.Vehicle);
        Setup.Combat.RadarTarget.Remove(m.Vehicle);
        if (Setup.Script?.Host.Of(m.Vehicle) is { } e) Setup.Script.Host.Remove(e);
        if (Setup.World.Player == m.Vehicle) Setup.World.Player = null;
        Removed?.Invoke(m);
    }

    /// <summary>
    /// The frame: moves the view's clock on by <paramref name="dt"/> (kept
    /// <see cref="Delay"/> behind the newest snapshot), takes the two
    /// snapshots around it, writes the later one into the mirror, and
    /// releases the events up to its tick.
    /// </summary>
    /// <summary>
    /// A co-op movie has the game stand still (the server does not tick): events are still taken, but the view's clock, the cars'
    /// tracks and the rounds' ages do not move; when it lets go the clock is taken afresh from the snapshots.
    /// </summary>
    public bool Hold { get; set; }
    private bool _held;

    public void Advance(double dt, MeleeClient client)
    {
        // a new round on this connection is the next mirror's (the scene that builds it reloads after this one's last
        // frames, or was loading when it began): this one must not take its events, or the cars spawned at its start are
        // lost to both. (A client with no accept and no round is a test's feed.)
        if (!ReferenceEquals(client.Accept, _accept) && (client.Accept is not null || client.Rounds > 0)) return;
        _now = client.Now;
        // the radio's line ends by its own length, as the host's does (the next comes as an event)
        if (Setup.Script?.Host.Cb is { } cb)
        {
            cb.Update(_now * 1000);
            if (_radioAtJoin is var (line, at))
            {
                _radioAtJoin = null;
                Guard("radio", () => { Radio(line); if (cb.Current is { } c) c.StartedMs = _now * 1000 - at * 1000; });
            }
        }
        // the host migration's lines for the feed, and its new host's game (HostMigration.cs)
        while (client.Notices.Count > 0) { var n = client.Notices.Dequeue(); Guard("notice", () => News?.Invoke(new ChatEvent(-1, n))); }
        if (client.Migrations != _migrations && client.RejoinAccept is { } ra) { _migrations = client.Migrations; Rejoined(ra); }
        while (client.Events.Count > 0)
        {
            var batch = client.Events.Dequeue();
            _waiting.Add(batch);
            // a car rebuilt as another is rebuilt here at once, before any snapshot of the new car is read into the old
            foreach (var e in batch.Events) if (e is CarChangedEvent cc) Guard($"car {cc.Car.Serial} changed", () => Change(cc));
        }
        if (Hold) { _held = true; return; }
        if (_held) { _held = false; RenderTick = -1; }
        var snaps = client.Snapshots;
        if (snaps.Count == 0) return;
        _snaps = snaps;
        Ingest(snaps);
        double tick = Session.TickSeconds;
        // where the newest snapshot is now, by the time since it came, less the delay
        double target = client.NewestTick + (client.Now - client.NewestAt) / tick - Delay / tick;
        if (RenderTick < 0 || Math.Abs(target - RenderTick) > StaleTicks) RenderTick = target;
        else RenderTick += dt / tick * (1 + Math.Clamp((target - RenderTick) * 0.5, -0.25, 0.25));
        // the two snapshots around the view: the latest at or before it, the first after
        Snapshot? a = null, b = null;
        for (int i = snaps.Count - 1; i >= 0; i--)
        {
            var s = snaps.Values[i];
            if (s.Tick > RenderTick) b = s;
            else { a = s; break; }
        }
        if (b is null) { Starved++; b = a; }
        a ??= b;
        if (a is null || b is null) return;
        Alpha = b.Tick == a.Tick ? 1f : (float)Math.Clamp((RenderTick - a.Tick) / (b.Tick - a.Tick), 0, 1);
        if (b.Tick != IntervalTo || a.Tick != IntervalFrom)
        {
            // the cars the server described when asked, and the events, first (a car spawned at b's tick is then written from it)
            while (client.CarAnswers.Count > 0) { var info = client.CarAnswers.Dequeue(); Guard($"car {info.Serial}", () => Add(info)); }
            Release(b.Tick);
            Write(a, b);
            IntervalFrom = a.Tick;
            IntervalTo = b.Tick;
        }
        // the server's rounds age here (the snapshot carries neither their age nor their life, and nothing else steps them):
        // the views fade a dropped slick by its life left and spread it by its age
        foreach (var r in _rounds.Values) { r.Age += (float)dt; r.Life -= (float)dt; }
        StepCars(dt);
        // snapshots the view has passed are no longer needed (each car keeps its own states)
        while (snaps.Count > 2 && snaps.Keys[0] < a.Tick) snaps.RemoveAt(0);
        AskAboutUnknown(client);
    }

    /// <summary>Cars the snapshots have named this long without the mirror having them are asked about (and again each <see cref="AskAgain"/>).</summary>
    public const double AskAfter = 0.5, AskAgain = 1.0;
    /// <summary>The cars this mirror has asked the server about, all told.</summary>
    public int Asked { get; private set; }

    /// <summary>
    /// A car the snapshots name that the mirror does not have: its spawn never reached it (a lost event batch, a view that
    /// threw on it) or it could not be built. Normally that lasts only until its spawn event is released; past
    /// <see cref="AskAfter"/> the server is asked what it is (<see cref="MeleeClient.AskCars"/>), and the answer builds it.
    /// </summary>
    private void AskAboutUnknown(MeleeClient client)
    {
        if (_unknown.Count == 0) return;
        double now = client.Now;
        _missing.Clear();
        foreach (var (serial, (since, asked)) in _unknown)
            if (now - since >= AskAfter && (asked < 0 || now - asked >= AskAgain)) _missing.Add(serial);
        if (_missing.Count == 0) return;
        foreach (var serial in _missing) _unknown[serial] = (_unknown[serial].Since, now);
        Asked += _missing.Count;
        Log?.Invoke($"asking the server about cars {string.Join(", ", _missing)}: in the snapshots, not in the mirror");
        client.AskCars(_missing);
    }

    /// <summary>Runs one event's or car's effects; one that throws (a view's handler) is logged and does not stop the rest.</summary>
    private void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log?.Invoke($"{what}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private readonly HashSet<uint> _ingested = [];
    private SortedList<uint, Snapshot>? _snaps;
    private OwnCar? _lastOwn;

    /// <summary>Each car's states from the snapshots not yet taken: those a snapshot carried at its own tick.</summary>
    private void Ingest(SortedList<uint, Snapshot> snaps)
    {
        foreach (var (t, s) in snaps)
        {
            if (!_ingested.Add(t)) continue;
            foreach (var c in s.Cars)
            {
                if (c.SampleTick == 0) c.SampleTick = s.Tick;
                if (c.SampleTick == s.Tick && Cars.TryGetValue(c.Serial, out var m)) m.Track.Add(c);
            }
        }
        if (_ingested.Count > 4 * snaps.Count + 64) _ingested.RemoveWhere(t => t < snaps.Keys[0]);
    }

    /// <summary>
    /// Each car on its own clock (<see cref="CarTrack"/>): where to draw it, and its latest state written into the mirror
    /// (the radar, the target box, the sounds read it).
    /// </summary>
    private void StepCars(double dt)
    {
        float tick = Session.TickSeconds;
        CarTrack.Present? present = NearAtPresent && !OwnFromSnapshots && Prediction is { Ready: true } p ? new(p.PresentTick(InputAlpha), p.Pose(InputAlpha).Pos) : null;
        foreach (var m in Cars.Values)
        {
            if (Predicted && m.Info.Serial == OwnSerial)
            {
                m.Present = true;
                // a co-op cut-scene drives the own car by the host's script: drawn where the snapshots have it, as the camera is
                // (the prediction keeps the car itself)
                if (!OwnFromSnapshots) continue;
                m.Drawn = m.Track.Step(RenderTick, dt, tick, null);
                if ((m.Track.B ?? m.Track.A) is { } own) Pose(m, own);
                continue;
            }
            m.Drawn = m.Track.Step(RenderTick, dt, tick, present);
            m.Present = m.Drawn is not null;
            if ((m.Track.B ?? m.Track.A) is not { } now) continue;
            if (now != m.Applied)
            {
                now.ApplyTo(m.Vehicle);
                SurfaceUnder(m.Vehicle);
                m.Applied = now;
                // the own car's parts come exactly in its own block: the car's bytes of them must not stand
                if (m.Info.Serial == OwnSerial && _lastOwn is { } lo && lo.Epoch == OwnEpoch) lo.ApplyTo(m.Vehicle, Setup.Combat, Car);
            }
            Pose(m, now);
        }
    }

    /// <summary>The two states the car's wheels and roll are drawn between: its track's latest pair.</summary>
    private static void Pose(MirrorCar m, CarState now)
    {
        var before = m.Track.A ?? now;
        if (ReferenceEquals(before, m.PoseFrom) && ReferenceEquals(now, m.PoseTo)) return;
        (m.PoseFrom, m.PoseTo) = (before, now);
        (m.PrevRot, m.PrevPos, m.Rot, m.Pos) = (before.Rot, before.Pos, now.Rot, now.Pos);
        m.PoseVersion++;
    }

    /// <summary>The CTF flags in the newest snapshot the view has reached (empty outside CTF).</summary>
    public IReadOnlyList<FlagState> Flags { get; private set; } = [];

    /// <summary>The flags in the snapshot before <see cref="Flags"/> (the same list at the first), to draw a loose flag between the two as the cars are.</summary>
    public IReadOnlyList<FlagState> PreviousFlags { get; private set; } = [];

    private void Write(Snapshot a, Snapshot b)
    {
        Flags = b.Flags;
        WriteScript(a, b);
        // the cars are each on their own clock (StepCars)
        _lastOwn = Predicted ? null : b.Own;
        PreviousFlags = a.Flags;
        // a car the snapshot lists (every car this client's frame holds, the far ones with an older state) that the mirror
        // lacks is asked about after a while (AskAboutUnknown); one gone from the frame is not asked about any more
        _named.Clear();
        foreach (var c in b.Cars)
            if (!Cars.ContainsKey(c.Serial) && !_removed.Contains(c.Serial)) { _named.Add(c.Serial); _unknown.TryAdd(c.Serial, (_now, -1)); }
        foreach (var gone in _unknown.Keys.Where(k => !_named.Contains(k)).ToList()) _unknown.Remove(gone);
        if (b.Own is { } own && Own is { } me && !Predicted && own.Epoch == OwnEpoch) own.ApplyTo(me.Vehicle, Setup.Combat, Car);
        // rounds: the same Round object for the same id, so the views keep its trail
        var live = new HashSet<int>();
        var rounds = Setup.Combat.Rounds;
        rounds.Clear();
        foreach (var rs in b.Rounds)
        {
            if (Car(rs.Shooter) is not { } shooter) continue;
            if (!_rounds.TryGetValue(rs.Serial, out var r))
            {
                if (RoundState.WeaponOf(shooter, rs.Weapon) is not { Def.Ordnance: { } ord } w) continue;
                r = new Round { Kind = w.Def.Kind, Shooter = shooter, Def = w.Def, Ord = ord, Life = w.Def.Life == 0 ? 5f : w.Def.Life, Pos = rs.Pos, PrevPos = rs.Pos, Rot = rs.Rot, NetId = (int)NetKind.Round << 24 | rs.Serial };
                _rounds[rs.Serial] = r;
            }
            else r.PrevPos = r.Pos;
            r.Pos = rs.Pos;
            r.Rot = rs.Rot;
            r.Travelled = rs.Travelled;
            r.Settled = rs.Settled;
            live.Add(rs.Serial);
            if (OwnShots is { } os && rs.Shooter == OwnSerial && !os.Show(rs.Serial, r)) continue;
            rounds.Add(r);
        }
        foreach (var gone in _rounds.Keys.Where(k => !live.Contains(k)).ToList()) _rounds.Remove(gone);
        WriteFlames(b);
    }

    /// <summary>
    /// The other cars' flame streams from the snapshot the view reached, built from the cars as they are drawn
    /// (<see cref="Combat.BuildFlameView"/>; the flame's harm is the server's). The own car's are its prediction's when
    /// its shots are predicted.
    /// </summary>
    private void WriteFlames(Snapshot b)
    {
        var flames = Setup.Combat.Flames;
        var seen = new HashSet<(int, byte)>();
        foreach (var fs in b.Flames)
        {
            if (OwnShots is not null && fs.Owner == OwnSerial) continue;
            if (Car(fs.Owner) is not { } owner || RoundState.WeaponOf(owner, fs.Weapon) is not { Def.Kind: 9 or 10 or 11 } w) continue;
            var key = (fs.Owner, fs.Weapon);
            if (!_flames.TryGetValue(key, out var f) || f.Owner != owner || !flames.Contains(f))
            {
                if (f is not null) flames.Remove(f);
                _flames[key] = f = new Flame { Owner = owner, Weapon = w, Style = (int)(w.Def.Kind - 9), Count = fs.Count };
                flames.Add(f);
            }
            f.Count = Math.Clamp((int)fs.Count, 2, 19);
            f.Damage = w.Damage;
            Setup.Combat.BuildFlameView(f, (float)Session.TickSeconds);
            seen.Add(key);
        }
        foreach (var (key, f) in _flames.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            flames.Remove(f);
            _flames.Remove(key);
        }
    }

    /// <summary>
    /// The script's camera and gates from the snapshots (a co-op mission's): the camera between the two the view is between
    /// (a jump of more than <see cref="CameraCutMetres"/> is a cut, not a pan), every gate where the later one has it (a
    /// gate it does not list stands where it was placed).
    /// </summary>
    private void WriteScript(Snapshot a, Snapshot b)
    {
        if (Setup.Script?.Host is not { } h) return;
        // the story's camera if the script holds the view for everyone, else this player's own scene (protocol 19); the view
        // is the one the player sees, so the game reads it as it reads a single player's cut-scene
        CameraState? cam = b.Camera, before = a.Camera;
        if (cam is null && _seat >= 0)
        {
            foreach (var (seat, pc) in b.Personal) if (seat == _seat) cam = pc;
            foreach (var (seat, pc) in a.Personal) if (seat == _seat) before = pc;
        }
        if (cam is not null)
        {
            var from = before is { } pc && Vector3.Distance(pc.Eye, cam.Eye) < CameraCutMetres ? pc : cam;
            h.View.Mirror(true, from.Eye, from.LookAt ?? cam.LookAt, cam.Eye, cam.LookAt, cam.Basis);
            h.View.Personal = b.Camera is null;
        }
        else if (h.View.Active) { h.View.Mirror(false, h.View.Eye, h.View.LookAt, h.View.Eye, h.View.LookAt, h.View.Basis); h.View.Personal = false; }
        if (b.Gates.Count == 0 && !_gatesMoved) return;
        _gatesMoved = b.Gates.Count > 0;
        foreach (var o in Setup.Statics.Gated)
            foreach (var n in o.Nodes) if (n.Gate is { } g) g.X = 0;
        foreach (var gs in b.Gates)
            if (Structure(gs.Structure) is { } so && gs.Node < so.Nodes.Count && so.Nodes[gs.Node].Gate is { } g) g.X = gs.X;
    }

    private bool _gatesMoved;
    /// <summary>The script camera moving this far between two snapshots is a cut.</summary>
    public const float CameraCutMetres = 40;

    /// <summary>The host's radio line here: on the air at once, the speaker this mission's entity (a human's line is this player's own).</summary>
    private void Radio(RadioEvent r)
    {
        if (Setup.Script?.Host is not { } h) return;
        object? speaker = r.Speaker == RadioEvent.Player ? h.Player : r.Speaker >= 0 && r.Speaker < h.Entities.Count ? h.Entities[r.Speaker] : null;
        h.Cb.Play(r.Sound, speaker, r.Priority);
    }

    /// <summary>An objective's flags as the host has them, its sound played as the host's own change would have (a success, a reveal).</summary>
    private void Objective(ObjectiveEvent o)
    {
        if (Setup.Script?.Host.Objectives is not { } ob || o.Number < 1 || o.Number > ob.Flags.Length) return;
        int was = ob.Flags[o.Number - 1], now = o.Flags;
        if ((now & Objectives.Succeeded) != 0 && (was & Objectives.Succeeded) == 0) { ob.Flags[o.Number - 1] = was & ~(Objectives.Succeeded | Objectives.Failed); ob.Success(o.Number); }
        else if ((now & Objectives.Hidden) == 0 && (was & Objectives.Hidden) != 0) ob.Reveal(o.Number);
        ob.Flags[o.Number - 1] = now;
    }

    /// <summary>
    /// The ground a mirrored car is on (the server's step sets it, the snapshot does not carry it): the road feel of the
    /// player's force feedback and the tyre sounds read it. Kept while airborne, as the step does.
    /// </summary>
    private void SurfaceUnder(Vehicle v)
    {
        if ((v.Flags & VehicleFlags.Airborne) != 0) return;
        var p = v.Pos;
        if (v.Rot.Up.Y < 0) p += v.Rot.Up * v.P.RideOffset;
        v.Surface = Setup.Ground.SurfaceId(p.X, p.Z);
    }

    private void Release(uint upTo)
    {
        int n = 0;
        while (n < _waiting.Count && _waiting[n].Tick <= upTo) n++;
        if (n == 0) return;
        var batches = _waiting.GetRange(0, n);
        _waiting.RemoveRange(0, n);
        foreach (var batch in batches)
            foreach (var e in batch.Events)
                // long gone (what came while the arena loaded): what it left, not the shots and bangs of it
                if (batch.Tick + StaleTicks >= upTo || !Cosmetic(e)) Guard(e.GetType().Name, () => Apply(e));
    }

    /// <summary>Events older than this when they are released change state only (a client catching up after its load).</summary>
    /// <summary>0.36 s in ticks: older cosmetic events are dropped, and a view this far off its target jumps.</summary>
    public uint StaleTicks => (uint)Session.Rate.TicksFor(0.36f);

    private static bool SpecialSound(string s) => s.Equals("bnitro.wav", StringComparison.OrdinalIgnoreCase) || s.Equals("cjamm.wav", StringComparison.OrdinalIgnoreCase);

    private static bool Cosmetic(NetEvent e) => e is ShotEvent or ImpactEvent or ExplosionEvent or ClickEvent or DebrisEvent or FlungEvent or SoundEvent or MisfireEvent;

    private void Apply(NetEvent e)
    {
        var combat = Setup.Combat;
        var damage = Setup.Damage.Events;
        switch (e)
        {
            // the own car's shots were drawn when they were fired, from the predicted car
            case ShotEvent s when OwnShots is not null && s.Car == OwnSerial:
                break;
            case ShotEvent s when Car(s.Car) is { } v && RoundState.WeaponOf(v, s.Weapon) is { } w:
                combat.Events.Add(new ShotFired(v, w, s.Muzzle, s.Forward));
                break;
            case ImpactEvent i:
                combat.Events.Add(new Impact(i.Effect, i.Sound, i.At, i.Victim switch { 0 => null, 1 => CarVictim, _ => StructureVictim }, i.RoundKind, i.Hit, i.N, i.Dir));
                break;
            case ExplosionEvent x when combat.Effects(x.Effect) is { } fx:
                combat.Events.Add(new Explosion(fx, x.At));
                break;
            // the own specials' sounds were played when the prediction first ran the press
            case ClickEvent c when Prediction is not null && c.Car == OwnSerial && SpecialSound(c.Sound):
                break;
            case ClickEvent c when Car(c.Car) is { } v:
                combat.Events.Add(new WeaponClick(v, c.Sound));
                break;
            case ZoneEvent z when Car(z.Car) is { } v: damage.Add(new ZoneDamaged(v, z.Zone, z.Level)); break;
            case KilledEvent k when Car(k.Car) is { } v: damage.Add(new VehicleKilled(v, k.DriverKilled)); News?.Invoke(e); break;
            case WreckedEvent w when Car(w.Car) is { } v: damage.Add(new VehicleWrecked(v)); break;
            case TyreEvent t when Car(t.Car) is { } v: damage.Add(new TyreBlown(v, t.Slot)); break;
            case FlungEvent f when Car(f.Car) is { } v: damage.Add(new PartFlung(v, f.Slot, f.Velocity)); break;
            case DebrisEvent d: damage.Add(new Debris(d.At, d.Velocity)); break;
            case SignEvent sk when Structure(sk.Structure) is { } o: o.Knock(); damage.Add(new SignKnocked(o, sk.Velocity)); break;
            case StructureEvent st when Structure(st.Structure) is { } o:
                if (o.ApplyDestroyedMask(st.Mask) || st.Booms.Length > 0) damage.Add(new StructureDestroyed(o, st.Booms));
                break;
            case SpawnEvent sp: Add(sp.Car); break;
            case CarChangedEvent: break;   // applied as it came (Advance)
            case RemoveEvent rm: Remove(rm.Car); break;
            case RenewedEvent rn when Cars.TryGetValue(rn.Car, out var m):
                Renewed?.Invoke(m, rn.Respawn);
                News?.Invoke(e);
                break;
            case GameOverEvent:
                GameOver = true;
                News?.Invoke(e);
                break;
            // a co-op mission's script, as the host's ran it
            case RadioEvent r: Radio(r); News?.Invoke(e); break;
            case RadioCutEvent: Setup.Script?.Host.Cb.Kill(); News?.Invoke(e); break;
            case ObjectiveEvent o: Objective(o); News?.Invoke(e); break;
            case MovieEvent m: Setup.Script?.Host.PlayMovie(m.Name); News?.Invoke(e); break;
            case MissionEndEvent end: Setup.Script?.Host.EndMission(end.Won, 0, end.FailMessage); News?.Invoke(e); break;
            case SirenEvent sr:
                if (Car(sr.Car) is { } sv && Setup.Script?.Host.Of(sv) is { } se) se.Siren = sr.On;
                News?.Invoke(e);
                break;
            default:
                News?.Invoke(e);
                break;
        }
    }
}
