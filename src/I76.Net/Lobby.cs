namespace I76.Net;

/// <summary>
/// A player in a co-op game's lobby (protocol 20): the id this lobby gave them (not a slot: the seats are dealt when the mission starts),
/// their name, the car they have chosen (a car file, or <see cref="Protocol.MissionCar"/> for the mission's own) and their ready mark.
/// </summary>
public sealed record LobbyMember(byte Id, string Name, string Car, bool Ready);

/// <summary>
/// What a co-op game's lobby holds, sent to every player in it whenever anything changes (protocol 20). <see cref="You"/> is the
/// receiver's id; <see cref="Host"/> the id of the player who starts the mission (255: a dedicated server, which starts it by itself when
/// <see cref="AutoMin"/> or more players are all ready; 0 on a game the host starts). <see cref="OwnCar"/> is the car file the mission gives its player,
/// which "the mission's own car" is (the lobby shows its blueprint).
/// </summary>
public sealed record LobbyState(string Mission, byte Max, byte You, byte Host, byte AutoMin, IReadOnlyList<LobbyMember> Members, string OwnCar = "")
{
    public const byte NoHost = 255;
    public bool IsHost => You == Host;
    public LobbyMember? Me => Members.FirstOrDefault(m => m.Id == You);

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.LobbyState);
        w.Str(Mission);
        w.U8(Max); w.U8(You); w.U8(Host); w.U8(AutoMin);
        w.U8((byte)Members.Count);
        foreach (var m in Members) { w.U8(m.Id); w.Str(m.Name); w.Str(m.Car); w.Bool(m.Ready); }
        w.Str(OwnCar);
    }

    /// <summary>Reads the body (after the type byte).</summary>
    public static LobbyState Read(ref NetReader r)
    {
        string mission = r.Str();
        byte max = r.U8(), you = r.U8(), host = r.U8(), auto = r.U8();
        int n = r.U8();
        if (n > Protocol.MaxCoopSeats) throw new WireException($"lobby of {n}");
        var l = new List<LobbyMember>(n);
        for (int i = 0; i < n; i++) l.Add(new LobbyMember(r.U8(), r.Str(), r.Str(), r.Bool()));
        return new LobbyState(mission, max, you, host, auto, l, r.Str());
    }
}

/// <summary>
/// A lobby player's change (protocol 20): the ready mark, and a new car when <see cref="Car"/> is not empty (a car file or
/// <see cref="Protocol.MissionCar"/>, with its .vcf when the server's install may lack it, and its paint, as a join's). A new car
/// is checked as a joining car is and answered with <see cref="ReconfigureAnswer"/>; it takes the ready mark off.
/// </summary>
public sealed record LobbySet(bool Ready, string Car, byte[] Config, byte[]? Paint = null)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.LobbySet);
        w.Bool(Ready);
        w.Str(Car);
        w.BlobU16(Config);
        w.BlobU16(Paint ?? []);
    }

    /// <summary>Reads the body (after the type byte).</summary>
    public static LobbySet Read(ref NetReader r) => new(r.Bool(), r.Str(), r.BlobU16(), r.BlobU16());
}

/// <summary>The lobby host starts the mission (protocol 20); <paramref name="Force"/> starts it though some are not ready.</summary>
public sealed record LobbyStart(bool Force)
{
    public void Write(NetWriter w) { w.U8((byte)MsgType.LobbyStart); w.Bool(Force); }
    public static LobbyStart Read(ref NetReader r) => new(r.Bool());
}

/// <summary>A chat line in the lobby, from the player of that name (protocol 20).</summary>
public sealed record LobbyChat(string Name, string Text)
{
    public void Write(NetWriter w) { w.U8((byte)MsgType.LobbyChat); w.Str(Name); w.Str(Text); }
    public static LobbyChat Read(ref NetReader r) => new(r.Str(), r.Str());
}
