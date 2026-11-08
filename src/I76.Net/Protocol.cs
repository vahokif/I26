using I76.Sim.Missions;
using I76.Sim.Session;

namespace I76.Net;

/// <summary>
/// The network melee's wire protocol (docs/multiplayer.md, "The wire"). The
/// server owns the game: a client sends its inputs, the server sends back
/// snapshots of the cars and rounds every tick (unreliable) and what
/// happened (events, reliable and ordered). Every message starts with its
/// <see cref="MsgType"/> byte.
/// </summary>
public static class Protocol
{
    /// <summary>Bumped whenever a message's layout changes; a client of another version is turned away.</summary>
    public const ushort Version = 23;
    /// <summary>The first thing in a join request, so that stray packets are refused before they are read.</summary>
    public const string Magic = "I76N";
    public const int DefaultPort = 7676;
    /// <summary>The most players a game can have: the wire, the screens that list them and the server all take this many.</summary>
    public const int MaxSeats = 64;
    /// <summary>The seats of a co-op story mission.</summary>
    public const int MaxCoopSeats = 8;
    /// <summary>The longest game password, in characters (protocol 12).</summary>
    public const int MaxPassword = 32;
    /// <summary>
    /// The reasons a join is refused for its password (protocol 12). The joiner compares them to know to ask: a game
    /// that did not list itself as locked can still answer so.
    /// </summary>
    /// <summary>
    /// A join's car that asks for the co-op mission's own: the car the story gives its player (the default of the co-op form). A melee
    /// server takes it for the default car; a co-op server builds that seat in the mission's own car, checked against nothing.
    /// </summary>
    public const string MissionCar = "*mission";
    public const string NeedsPassword = "This game needs a password", WrongPassword = "Wrong password";
    /// <summary>A wrong password from one address blocks that address's next try for this long (seconds).</summary>
    public const double PasswordRetrySeconds = 1.0;
    public const string PasswordTooSoon = "Too many tries - wait a moment";
    // The tick-count limits below are seconds at the 28 Hz tick the wire was tuned at, and become ticks of the
    // server's rate (JoinAccept.RateHz): the same time on the wire at 20 Hz or 60.
    /// <summary>How many of its latest inputs a client sends each tick, newest first: a lost packet is covered by the next ones (0.2 s).</summary>
    public static int InputHistory(SimRate r) => r.TicksFor(0.2f);
    /// <summary>The most inputs a client may have waiting on the server before the oldest are dropped (0.288 s).</summary>
    public static int MaxQueuedInputs(SimRate r) => r.TicksFor(0.288f);
    /// <summary>
    /// More than this many waiting when the server takes one (a burst after a
    /// client's stall), and the oldest are merged away, so a hiccup does not
    /// leave the client's inputs standing that much later for good (0.2 s;
    /// 0.108 s before protocol 13, when the client's input clock began keeping
    /// one or two in hand and a jittery link's bunches had to fit above them).
    /// </summary>
    public static int CatchUpInputs(SimRate r) => r.TicksFor(0.2f);
    /// <summary>The snapshots a client keeps (2.3 s).</summary>
    public static int BufferTicks(SimRate r) => r.TicksFor(2.3f);
    /// <summary>The most inputs one message may carry, for any rate (the history at 60 Hz, doubled).</summary>
    public const int MaxInputsPerMessage = 32;
    /// <summary>Longest player name and chat line, in characters.</summary>
    public const int MaxName = 24, MaxChat = 120;
    /// <summary>What a client costs the host besides snapshots and events (LiteNetLib's acks and pings, the scoreboard), bytes a second, for the upload budget.</summary>
    public const int ClientOverheadBytesPerSecond = 768;
    /// <summary>The least a client's snapshots get of an upload budget, bytes a second (its own car and a few others every tick).</summary>
    public const int MinSnapshotBytesPerSecond = 4096;
    /// <summary>An invite-only join unanswered this long is turned away (30 s from the request).</summary>
    public const double InviteSeconds = 30;
    /// <summary>The joiner's words for an invite declined or unanswered (the original front end's).</summary>
    public const string InviteDeclined = "The host has rejected you. Go find another game, pink boy.", InviteTimedOut = "Your request timed out. Try again.";
    /// <summary>A booted player's notice (the original said nothing: the session just ended).</summary>
    public const string Booted = "The host booted you from the game.";
    /// <summary>A banned player's refusal on joining again (the host's /ban; the port's).</summary>
    public const string Banned = "The host has banned you from this game.";
}

public enum MsgType : byte
{
    /// <summary>Server → client, reliable: the join was accepted (<see cref="JoinAccept"/>).</summary>
    JoinAccept = 1,
    /// <summary>Client → server, unreliable: the client's tick and its latest inputs (<see cref="InputMessage"/>).</summary>
    Input = 2,
    /// <summary>Server → client, unreliable: one tick's state (<see cref="Snapshot"/>).</summary>
    Snapshot = 3,
    /// <summary>Server → client, reliable ordered: one tick's events (<see cref="EventBatch"/>).</summary>
    Events = 4,
    /// <summary>Server → client, reliable: everyone's score and ping, once a second (<see cref="Scoreboard"/>).</summary>
    Scoreboard = 5,
    /// <summary>Client → server, reliable: a chat line.</summary>
    Chat = 6,
    /// <summary>Client → server, reliable: the player is leaving (the disconnect follows).</summary>
    Leave = 7,
    /// <summary>Server → client, unreliable: one piece of a snapshot too big for one datagram (<see cref="SnapshotParts"/>).</summary>
    SnapshotPart = 8,
    /// <summary>Server → client, reliable, before the seat: the arena is a custom map, its files and hash (<see cref="MapOffer"/>, protocol 11).</summary>
    MapOffer = 9,
    /// <summary>Client → server, reliable: send these files of the offered map, from these offsets (<see cref="MapRequest"/>).</summary>
    MapRequest = 10,
    /// <summary>Server → client, reliable ordered on channel 1: a piece of the map (<see cref="MapChunk"/>).</summary>
    MapChunk = 11,
    /// <summary>Client → server, reliable: the client has the offered map (the hash follows); the server seats it and sends the accept.</summary>
    MapReady = 12,
    /// <summary>
    /// Server → client, reliable ordered (protocol 12): the melee carries on in a new round on this arena. The client drops
    /// the old round (accept, snapshots, events) and waits for what a join gets: a map offer or the accept, on this connection.
    /// </summary>
    NextRound = 13,
    /// <summary>
    /// Client → server, reliable (protocol 13): what are these cars? The snapshots name a car the client's mirror does not
    /// have (its spawn was lost, or its car could not be built): <see cref="CarQuery"/>, answered with <see cref="Cars"/>.
    /// </summary>
    CarQuery = 14,
    /// <summary>Server → client, reliable ordered (protocol 13): the cars asked about that are in the arena (<see cref="CarList"/>).</summary>
    Cars = 15,
    /// <summary>
    /// Client → server, reliable (protocol 13): while dead, the car the next respawn should build (<see cref="Reconfigure"/>;
    /// I26's Reconfigure, not in the original). Checked as a joining car is; answered with <see cref="ReconfigureAnswer"/>.
    /// </summary>
    Reconfigure = 16,
    /// <summary>Server → client, reliable ordered (protocol 13): the reconfigured car taken (its file) or refused (why).</summary>
    ReconfigureAnswer = 17,
    /// <summary>
    /// Server → client, reliable (protocol 13), before the seat: the game is invite only and the join waits for the host's
    /// answer (the queue; "Let %s join the game?"), at most <see cref="Protocol.InviteSeconds"/>.
    /// </summary>
    JoinWait = 18,
    /// <summary>
    /// Client → server, reliable (protocol 19): this client's copy of the co-op mission's movie is over (played out, skipped, or
    /// not there to play); the name follows. The host's report ends the movie for everybody (<see cref="MovieEndEvent"/>).
    /// </summary>
    MovieDone = 19,
    /// <summary>Server → client, reliable (protocol 20), before the seat: the co-op game's lobby, who is in it and who is ready (<see cref="LobbyState"/>).</summary>
    LobbyState = 20,
    /// <summary>Client → server, reliable (protocol 20): in the lobby, the player's ready mark and perhaps a new car (<see cref="LobbySet"/>).</summary>
    LobbySet = 21,
    /// <summary>Client → server, reliable (protocol 20): the lobby's host starts the mission (<see cref="LobbyStart"/>).</summary>
    LobbyStart = 22,
    /// <summary>Server → client, reliable (protocol 20): a chat line in the lobby (<see cref="LobbyChat"/>).</summary>
    LobbyChat = 23,
    /// <summary>Client → server, unreliable (protocol 20): one frame of the player's CB voice (<see cref="VoicePacket"/>); the server relays it.</summary>
    Voice = 24,
    /// <summary>Server → client, unreliable (protocol 20): another player's CB voice frame, with the sender's slot (<see cref="VoicePacket"/>).</summary>
    VoiceFrom = 25,
    /// <summary>Server → client, reliable (protocol 20): one byte, whether the host has muted this player's CB for everyone (1) or not (0); sent when it changes and at the seat.</summary>
    VoiceMuted = 26,
    /// <summary>Client → server, reliable (protocol 20): the players this client does not want to hear on the CB (<see cref="VoiceIgnore"/>); the server stops relaying their voice to it.</summary>
    VoiceIgnore = 27,
    // host migration (protocol 13, HostMigration.cs); numbered apart so that other new messages do not meet them
    /// <summary>Client → host, reliable: this client can take over as host, and its own addresses (<see cref="HostCandidate"/>).</summary>
    HostCandidate = 40,
    /// <summary>Host → client, reliable ordered: who takes over if the host goes, and the tokens (<see cref="SuccessorInfo"/>).</summary>
    Successor = 41,
    /// <summary>Host → successor, reliable ordered on <see cref="HostMigration.Channel"/>: a piece of the savestate (<see cref="MigrationBuffer"/>).</summary>
    MigrationState = 42,
    /// <summary>Host → successor, reliable ordered on <see cref="HostMigration.Channel"/>: one tick's inputs, for the replay.</summary>
    MigrationLog = 43,
}

/// <summary>A dead player's new car for the next respawn: a car file, and its .vcf when the server's install may lack it (as a join's configuration).</summary>
public sealed record Reconfigure(string Car, byte[] Config, byte[]? Paint = null)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.Reconfigure);
        w.Str(Car);
        w.BlobU16(Config);
        // protocol 17, appended: the car's paint (CarPaint.ToBytes; empty for the factory paint)
        w.BlobU16(Paint ?? []);
    }

    /// <summary>Reads the body (after the type byte).</summary>
    public static Reconfigure Read(ref NetReader r) => new(r.Str(), r.BlobU16(), r.Remaining > 0 ? r.BlobU16() : null);
}

/// <summary>The server's answer to a <see cref="Reconfigure"/>: taken (the car file the respawn builds) or refused, with why.</summary>
public sealed record ReconfigureAnswer(bool Taken, string Text)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.ReconfigureAnswer);
        w.Bool(Taken);
        w.Str(Text);
    }

    /// <summary>Reads the body (after the type byte).</summary>
    public static ReconfigureAnswer Read(ref NetReader r) => new(r.Bool(), r.Str());
}

/// <summary>
/// A client asking about cars its snapshots name and its mirror lacks (protocol 13), by their serials. The server answers
/// with the <see cref="CarInfo"/> of each still in the arena (<see cref="CarList"/>), on the events' channel, so a car that
/// leaves after the answer is removed by its own event, which comes after it.
/// </summary>
public sealed record CarQuery(int[] Serials)
{
    public const int Max = 32;

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.CarQuery);
        w.VarU((uint)Math.Min(Serials.Length, Max));
        foreach (var s in Serials.Take(Max)) w.VarU((uint)s);
    }

    /// <summary>Reads the body (after the type byte).</summary>
    public static CarQuery Read(ref NetReader r)
    {
        var s = new int[JoinAccept.Count(ref r, Max)];
        for (int i = 0; i < s.Length; i++) s[i] = (int)r.VarU();
        return new CarQuery(s);
    }
}

/// <summary>The server's answer to a <see cref="CarQuery"/>: the cars asked about that are in the arena, as a join accept lists them.</summary>
public sealed record CarList(CarInfo[] Cars)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.Cars);
        w.VarU((uint)Cars.Length);
        foreach (var c in Cars) c.Write(w);
    }

    /// <summary>Reads the body (after the type byte).</summary>
    public static CarList Read(ref NetReader r)
    {
        var cars = new CarInfo[JoinAccept.Count(ref r, CarQuery.Max)];
        for (int i = 0; i < cars.Length; i++) cars[i] = CarInfo.Read(ref r);
        return new CarList(cars);
    }
}

/// <summary>
/// The join request, carried as the connection request's data: protocol
/// version, the player's name, the car they chose and its configuration, and the game's password if it has one (protocol 12).
/// The server builds the car from its own install by <see cref="Car"/>; for a
/// car the server's install does not have (a variant saved by CONFIGURE
/// CHASSIS) the configuration is the car's .vcf, which the server checks
/// against the garage's rules and builds from.
/// </summary>
public sealed record JoinRequest(ushort Version, string Name, string Car, byte Team, byte[] Config, string Password = "", byte[]? Paint = null)
{
    /// <summary>Without the password (a record prints every member).</summary>
    public override string ToString() => $"JoinRequest {{ Version = {Version}, Name = {Name}, Car = {Car}, Team = {Team}, Password = {(Password.Length > 0 ? "***" : "")} }}";

    public void Write(NetWriter w)
    {
        w.Str(Protocol.Magic);
        w.U16(Version);
        w.Str(Name);
        w.Str(Car);
        w.U8(Team);
        w.BlobU16(Config);
        // protocol 12, appended: a request of an older version ends before it and is refused for its version
        w.Str(Password.Length > Protocol.MaxPassword ? Password[..Protocol.MaxPassword] : Password);
        // protocol 13, appended: the seat it held under the host that left (host migration, HostMigration.cs)
        if (Rejoin is { } rj) { w.U8(1); w.U8(rj.Slot); w.U32((uint)rj.Token); w.U32((uint)(rj.Token >> 32)); }
        else w.U8(0);
        // protocol 17, appended: the car's paint (CarPaint.ToBytes; empty for the factory paint)
        w.BlobU16(Paint ?? []);
    }

    /// <summary>A player coming back to its seat after the host left (host migration): the slot and the seat's token, which the host gave it.</summary>
    public (byte Slot, ulong Token)? Rejoin { get; init; }

    public static JoinRequest Read(ref NetReader r)
    {
        if (r.Str() != Protocol.Magic) throw new WireException("not an I76 join request");
        ushort version = r.U16();
        string name = r.Str(), car = r.Str();
        byte team = r.U8();
        var config = r.BlobU16();
        var j = new JoinRequest(version, name, car, team, config, r.Remaining > 0 ? r.Str() : "");
        if (r.Remaining > 0 && r.U8() == 1 && r.Remaining >= 9) j = j with { Rejoin = (r.U8(), r.U32() | (ulong)r.U32() << 32) };
        if (r.Remaining >= 2) j = j with { Paint = r.BlobU16() is { Length: > 0 } p ? p : null };
        return j;
    }
}

/// <summary>A car the client should know: its id, its .vcf, whose it is (a player's slot, or −1 for the AI fill), its team and name.</summary>
public sealed record CarInfo(int Serial, string Def, sbyte Owner, byte Team, string Name, byte[]? Vcf = null, byte[]? Paint = null)
{
    /// <summary>Vcf: the car file itself when it is not one every install has (a player's own variant), else empty.</summary>
    public void Write(NetWriter w) { w.VarU((uint)Serial); w.Str(Def); w.I8(Owner); w.U8(Team); w.Str(Name); w.BlobU16(Vcf ?? []); w.BlobU16(Paint ?? []); }   // protocol 17: the paint
    public static CarInfo Read(ref NetReader r) => new((int)r.VarU(), r.Str(), r.I8(), r.U8(), r.Str(), r.BlobU16() is { Length: > 0 } b ? b : null, r.BlobU16() is { Length: > 0 } pb ? pb : null);
}

/// <summary>
/// A structure's destroyed state (<see cref="I76.Sim.Collision.StaticObject.DestroyedMask"/>):
/// its serial, bit 0 its root, bit n + 1 its node n.
/// </summary>
public sealed record StructureState(int Serial, byte[] Mask)
{
    public void Write(NetWriter w) { w.VarU((uint)Serial); w.VarU((uint)Mask.Length); w.Bytes(Mask); }

    public static StructureState Read(ref NetReader r)
    {
        int serial = (int)r.VarU();
        int n = JoinAccept.Count(ref r, 4096);
        return new StructureState(serial, r.Bytes(n).ToArray());
    }

    public bool Equals(StructureState? o) => o is not null && Serial == o.Serial && Mask.AsSpan().SequenceEqual(o.Mask);
    public override int GetHashCode() => Serial;
}

/// <summary>
/// The server's answer to an accepted join: the player's slot and car, the
/// arena, the server's tick, the melee's rules, every car in the arena and
/// the structures already (partly) destroyed. The arena's mission file and
/// terrain go with their sizes and CRCs (<see cref="I76.Formats.Mission.MapFingerprint"/>,
/// protocol 8), so a client without the host's map, or with another copy of it,
/// refuses before it builds anything (the original's JO check). A custom map's hash
/// (<see cref="I76.Formats.Mission.MapFileSet"/>, protocol 11) follows: the one offered before the seat (<see cref="MapOffer"/>).
/// </summary>
public sealed record JoinAccept(byte Slot, int CarSerial, string Arena, uint ServerTick, string Car,
    bool TeamPlay, int MaxScore, float TimeLimit, float RespawnDelay, IReadOnlyList<CarInfo> Cars, IReadOnlyList<StructureState> DestroyedStructures,
    MeleeKind Kind = MeleeKind.Combat, MeleeGoal Goal = MeleeGoal.None, int GoalValue = 0, byte Teams = 2, byte RateHz = 20,
    uint MapSize = 0, uint MapCrc = 0, uint TerrainSize = 0, uint TerrainCrc = 0, byte[]? MapHash = null)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.JoinAccept);
        w.U8(Slot);
        w.VarU((uint)CarSerial);
        w.Str(Arena);
        w.U32(ServerTick);
        w.Str(Car);
        w.Bool(TeamPlay);
        w.VarU((uint)MaxScore);
        w.F32(TimeLimit);
        w.F32(RespawnDelay);
        w.VarU((uint)Cars.Count);
        foreach (var c in Cars) c.Write(w);
        w.VarU((uint)DestroyedStructures.Count);
        foreach (var s in DestroyedStructures) s.Write(w);
        w.U8((byte)Kind);
        w.U8((byte)Goal);
        w.VarU((uint)GoalValue);
        w.U8(Teams);
        w.U8(RateHz);
        w.U32(MapSize);
        w.U32(MapCrc);
        w.U32(TerrainSize);
        w.U32(TerrainCrc);
        w.U8((byte)(MapHash?.Length ?? 0));
        if (MapHash is { } h) w.Bytes(h);
        // protocol 15: a co-op mission's part (Coop.cs)
        w.Bool(Coop is not null);
        Coop?.Write(w);
        // protocol 20: the CB radio is on in this game (the host's CB Radio option)
        w.Bool(Cb);
        // protocol 22: the chase crosshair is allowed in this game (the host's Chase Crosshair option)
        w.Bool(Crosshair);
    }

    /// <summary>The CB radio is allowed in this game (the host form's CB Radio, i26-server's --no-cb turns it off); off, the server relays no voice and the client neither sends nor opens the microphone.</summary>
    public bool Cb { get; init; } = true;

    /// <summary>
    /// The chase crosshair is allowed in this game (protocol 22; the host form's Chase Crosshair, i26-server's --crosshair): an aiming
    /// aid the original does not have, so off unless the host allows it; allowed, each player's own Chase Crosshair setting decides.
    /// The view's alone: nothing in the simulation reads it.
    /// </summary>
    public bool Crosshair { get; init; }

    /// <summary>A co-op story mission's part (protocol 15): the mission's seats, this player's, and its script's presentation as it stands; null in a melee.</summary>
    public CoopInfo? Coop { get; init; }

    /// <summary>Reads the body (the type byte already read).</summary>
    public static JoinAccept Read(ref NetReader r)
    {
        byte slot = r.U8();
        int car = (int)r.VarU();
        string arena = r.Str();
        uint tick = r.U32();
        string def = r.Str();
        bool team = r.Bool();
        int max = (int)r.VarU();
        float limit = r.F32(), respawn = r.F32();
        var cars = new CarInfo[Count(ref r, 4096)];
        for (int i = 0; i < cars.Length; i++) cars[i] = CarInfo.Read(ref r);
        var dead = new StructureState[Count(ref r, 1 << 20)];
        for (int i = 0; i < dead.Length; i++) dead[i] = StructureState.Read(ref r);
        var kind = (MeleeKind)r.U8();
        var goal = (MeleeGoal)r.U8();
        int value = (int)r.VarU();
        byte teams = r.U8();
        byte hz = r.U8();
        uint mapSize = r.U32(), mapCrc = r.U32(), terSize = r.U32(), terCrc = r.U32();
        int hashLength = r.U8();
        if (hashLength is not (0 or 32)) throw new WireException($"map hash of {hashLength} bytes");
        byte[]? mapHash = hashLength == 0 ? null : r.Bytes(32).ToArray();
        if (!SimRate.IsOffered(hz)) throw new WireException($"simulation rate {hz}");
        if (kind > MeleeKind.Ctf || goal > MeleeGoal.Kills) throw new WireException($"melee kind {kind} goal {goal}");
        var coop = r.Bool() ? CoopInfo.Read(ref r) : null;
        bool cb = r.Bool();
        bool crosshair = r.Bool();
        return new JoinAccept(slot, car, arena, tick, def, team, max, limit, respawn, cars, dead, kind, goal, value, teams, hz, mapSize, mapCrc, terSize, terCrc, mapHash) { Coop = coop, Cb = cb, Crosshair = crosshair };
    }

    /// <summary>The server's simulation rate, which every client runs at.</summary>
    public SimRate Rate => SimRate.FromLabel(RateHz);

    /// <summary>
    /// Why this client cannot play the server's arena from its own install, or null if its copy is the same (mission file
    /// and terrain, size and CRC). A server that sent no fingerprint (an arena it could not fingerprint) is not checked.
    /// </summary>
    public string? MapMismatch(I76.Formats.Vfs.GameInstall install) =>
        MapSize == 0 && MapCrc == 0 ? null : I76.Formats.Mission.MapFingerprint.Mismatch(install, Arena, MapSize, MapCrc, TerrainSize, TerrainCrc);

    internal static int Count(ref NetReader r, int max)
    {
        uint n = r.VarU();
        if (n > max || n > r.Remaining) throw new WireException($"count {n} out of range");
        return (int)n;
    }
}

/// <summary>
/// A client's inputs: its tick (the newest input's) and its latest inputs,
/// newest first, one a tick back from it. The server applies each client
/// tick once, in order, so the repeats cost nothing but bytes. Then the tick of
/// the snapshot the client decoded last (protocol 13): the server writes the
/// next snapshots against the newest of these (<see cref="SnapshotEncoder"/>); 0 for none.
/// </summary>
public sealed record InputMessage(uint Tick, IReadOnlyList<CarInput> Inputs, uint SnapshotAck = 0)
{
    /// <summary>
    /// The hashes of the client's predicted car after its latest input ticks (<see cref="OwnMotion.Hash"/>), newest first
    /// from <see cref="HashTick"/> (protocol 13): the server confirms the motion with one instead of sending it when its
    /// own car hashes the same after the tick it acknowledges. 0 is none.
    /// </summary>
    public IReadOnlyList<uint> Hashes { get; init; } = [];
    public uint HashTick { get; init; }
    /// <summary>The client could not find the state a confirmation named: the server sends the motion itself until it has one.</summary>
    public bool NeedExact { get; init; }

    public const int MaxHashes = 8;

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.Input);
        w.U32(Tick);
        w.U8((byte)Inputs.Count);
        Span<byte> b = stackalloc byte[CarInput.Size];
        foreach (var i in Inputs) { i.Write(b); w.Bytes(b); }
        w.U32(SnapshotAck);
        int n = Math.Min(Hashes.Count, MaxHashes);
        w.U8((byte)(n | (NeedExact ? 0x80 : 0)));
        if (n == 0) return;
        w.VarU(Tick - Math.Min(HashTick, Tick));
        for (int i = 0; i < n; i++) w.U32(Hashes[i]);
    }

    public static InputMessage Read(ref NetReader r)
    {
        uint tick = r.U32();
        int n = r.U8();
        if (n > Protocol.MaxInputsPerMessage) throw new WireException($"{n} inputs in one message");
        var list = new CarInput[n];
        for (int i = 0; i < n; i++) list[i] = CarInput.Read(r.Bytes(CarInput.Size));
        uint ack = r.Remaining >= 4 ? r.U32() : 0;
        if (r.Remaining == 0) return new InputMessage(tick, list, ack);
        byte h = r.U8();
        int count = h & 0x7f;
        if (count > MaxHashes) throw new WireException($"{count} motion hashes");
        uint from = 0;
        var hashes = new uint[count];
        if (count > 0)
        {
            uint back = r.VarU();
            if (back > tick) throw new WireException("motion hashes before the first tick");
            from = tick - back;
            for (int i = 0; i < count; i++) hashes[i] = r.U32();
        }
        return new InputMessage(tick, list, ack) { Hashes = hashes, HashTick = from, NeedExact = (h & 0x80) != 0 };
    }
}

/// <summary>One player's line on the scoreboard.</summary>
public sealed record PlayerRow(byte Slot, string Name, byte Team, int Score, int Kills, int Deaths, ushort Ping, int CarSerial, bool Alive);

/// <summary>
/// Everyone's score and ping (once a second), and the time and score left. On the wire (protocol 13) a delta against the
/// board last sent to that client (the reliable ordered channel brings every one in order): a flags byte (1: in full,
/// the client starts from nothing), then each row that changed as its slot, a mask of the fields that follow (name,
/// team, score, kills, deaths, ping, car, alive) and those fields, then the slots that left. A ping goes when it moved
/// <see cref="PingStep"/> ms or more. 64 players' board was 1.5 KB a second to each client; a delta is a few bytes a row
/// that changed.
/// </summary>
public sealed record Scoreboard(uint Tick, float Clock, IReadOnlyList<PlayerRow> Players)
{
    public const int PingStep = 10;

    /// <summary>Writes the board against <paramref name="sent"/> (the client's board; null or empty: in full) and brings <paramref name="sent"/> up to what the client will have.</summary>
    public void Write(NetWriter w, Dictionary<byte, PlayerRow>? sent = null)
    {
        bool full = sent is null || sent.Count == 0;
        w.U8((byte)MsgType.Scoreboard);
        w.U32(Tick);
        w.F32(Clock);
        w.U8((byte)(full ? 1 : 0));
        var rows = new List<(PlayerRow Row, int Mask)>();
        foreach (var p in Players)
        {
            var was = full ? null : sent!.GetValueOrDefault(p.Slot);
            int m = was is null ? 0xff
                : (p.Name != was.Name ? 1 : 0) | (p.Team != was.Team ? 2 : 0) | (p.Score != was.Score ? 4 : 0) | (p.Kills != was.Kills ? 8 : 0)
                  | (p.Deaths != was.Deaths ? 16 : 0) | (Math.Abs(p.Ping - was.Ping) >= PingStep ? 32 : 0) | (p.CarSerial != was.CarSerial ? 64 : 0) | (p.Alive != was.Alive ? 128 : 0);
            if (m != 0) rows.Add((p, m));
        }
        w.VarU((uint)rows.Count);
        foreach (var (p, m) in rows)
        {
            w.U8(p.Slot);
            w.U8((byte)m);
            if ((m & 1) != 0) w.Str(p.Name);
            if ((m & 2) != 0) w.U8(p.Team);
            if ((m & 4) != 0) w.VarI(p.Score);
            if ((m & 8) != 0) w.VarU((uint)p.Kills);
            if ((m & 16) != 0) w.VarU((uint)p.Deaths);
            if ((m & 32) != 0) w.U16(p.Ping);
            if ((m & 64) != 0) w.VarU((uint)p.CarSerial);
            if ((m & 128) != 0) w.Bool(p.Alive);
        }
        var gone = full ? [] : sent!.Keys.Where(k => !Players.Any(p => p.Slot == k)).ToList();
        w.VarU((uint)gone.Count);
        foreach (var g in gone) w.U8(g);
        if (sent is null) return;
        if (full) sent.Clear();
        foreach (var (p, m) in rows)
            sent[p.Slot] = (m & 32) != 0 || !sent.TryGetValue(p.Slot, out var was) ? p : p with { Ping = was.Ping };
        foreach (var g in gone) sent.Remove(g);
    }

    /// <summary>Reads a board onto <paramref name="board"/> (the client's, by slot) and returns it whole.</summary>
    public static Scoreboard Read(ref NetReader r, Dictionary<byte, PlayerRow>? board = null)
    {
        board ??= [];
        uint tick = r.U32();
        float clock = r.F32();
        if ((r.U8() & 1) != 0) board.Clear();
        int n = JoinAccept.Count(ref r, 256);
        for (int i = 0; i < n; i++)
        {
            byte slot = r.U8();
            int m = r.U8();
            var p = board.GetValueOrDefault(slot) ?? new PlayerRow(slot, "", 0, 0, 0, 0, 0, 0, false);
            if ((m & 1) != 0) p = p with { Name = r.Str() };
            if ((m & 2) != 0) p = p with { Team = r.U8() };
            if ((m & 4) != 0) p = p with { Score = r.VarI() };
            if ((m & 8) != 0) p = p with { Kills = (int)r.VarU() };
            if ((m & 16) != 0) p = p with { Deaths = (int)r.VarU() };
            if ((m & 32) != 0) p = p with { Ping = r.U16() };
            if ((m & 64) != 0) p = p with { CarSerial = (int)r.VarU() };
            if ((m & 128) != 0) p = p with { Alive = r.Bool() };
            board[slot] = p;
        }
        n = JoinAccept.Count(ref r, 256);
        for (int i = 0; i < n; i++) board.Remove(r.U8());
        return new Scoreboard(tick, clock, [.. board.Values.OrderBy(p => p.Slot)]);
    }
}

/// <summary>
/// A snapshot bigger than one unreliable datagram (LiteNetLib fragments only
/// reliable messages) goes as pieces: [SnapshotPart][tick][index][count] and
/// a slice of the whole Snapshot message. The client puts them back
/// together; a snapshot missing a piece is lost like a dropped datagram.
/// </summary>
public static class SnapshotParts
{
    public const int Header = 1 + 4 + 1 + 1;

    /// <summary>The pieces of a whole Snapshot message (<paramref name="whole"/>), each at most <paramref name="max"/> bytes.</summary>
    public static IEnumerable<byte[]> Split(uint tick, ReadOnlyMemory<byte> whole, int max)
    {
        int chunk = max - Header;
        int count = (whole.Length + chunk - 1) / chunk;
        if (count > 255) throw new InvalidOperationException($"a snapshot of {whole.Length} bytes is too big");
        for (int i = 0; i < count; i++)
        {
            var piece = whole.Slice(i * chunk, Math.Min(chunk, whole.Length - i * chunk));
            var w = new NetWriter(Header + piece.Length);
            w.U8((byte)MsgType.SnapshotPart);
            w.U32(tick);
            w.U8((byte)i);
            w.U8((byte)count);
            w.Bytes(piece.Span);
            yield return w.ToArray();
        }
    }
}

/// <summary>A client's snapshots in pieces, by tick, until each is whole.</summary>
public sealed class SnapshotAssembler
{
    private readonly SortedList<uint, byte[]?[]> _pending = [];

    /// <summary>Forgets the pieces waiting (a new round's ticks start again).</summary>
    public void Clear() => _pending.Clear();

    /// <summary>Takes a piece (the type byte read); returns the whole Snapshot message once every piece of its tick has come.</summary>
    public byte[]? Add(ref NetReader r)
    {
        uint tick = r.U32();
        int index = r.U8(), count = r.U8();
        if (count == 0 || index >= count) throw new WireException($"snapshot piece {index} of {count}");
        if (!_pending.TryGetValue(tick, out var parts))
        {
            parts = new byte[]?[count];
            _pending[tick] = parts;
            // a tick that never completed is given up once a newer one has started
            while (_pending.Count > 8) _pending.RemoveAt(0);
        }
        if (parts.Length != count) throw new WireException("snapshot pieces disagree on their count");
        parts[index] = r.Bytes(r.Remaining).ToArray();
        if (parts.Any(p => p is null)) return null;
        _pending.Remove(tick);
        return [.. parts.SelectMany(p => p!)];
    }
}
