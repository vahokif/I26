using System.Buffers.Binary;
using System.Text;
using I76.Sim.Missions;

namespace I76.Net.Anet;

/// <summary>
/// A session as an anet game server lists it (<c>dp_session_t</c>, h/anet.h).
/// Its id is its table subkey: an address and the karma (a random number the
/// host picks). The original puts the host's address in both; I26 puts the
/// host's own (private) address in the subkey and the public one the server
/// sees in <see cref="Master"/>, which the record carries as a "migrated"
/// master address when the two differ, so joiners behind the same NAT find
/// the host at home. Two forms go over the wire: the host publishes the
/// 83-byte raw structure; the server repacks it compactly for the species
/// table (dp_pack_session): max players, players, flags, then length-prefixed
/// name, password and user bytes (dwUser1 then szUserField, trailing zeros
/// cut), then the master address if it is not the subkey's.
/// </summary>
public sealed record AnetSession(AnetAddress KeyAddress, ushort Karma, AnetAddress Master, int MaxPlayers, int Players, int Flags,
    string Name, string Password, byte[] User)
{
    public const int RawSize = 83, SubkeySize = 8, UserSize = 14, NameMax = 31, PasswordMax = 9;
    /// <summary>
    /// dp_SESSION_FLAGS_ENABLE_NEWPLAYERS and MIGRATE_FORBIDDEN: the record is the host's to publish, and anet's own master
    /// election (dpElectMaster, over a dp session the players never join here) has nothing to move. I26's host migration
    /// is its own (HostMigration.cs): the successor publishes a record of its own.
    /// </summary>
    public const int FlagsOpen = 0x04 | 0x20;

    public byte[] Subkey()
    {
        var b = new byte[SubkeySize];
        KeyAddress.Write(b);
        b[6] = (byte)Karma; b[7] = (byte)(Karma >> 8);
        return b;
    }

    /// <summary>The subkey as a number, for dictionaries.</summary>
    public ulong Id => IdOf(Subkey());

    public static ulong IdOf(ReadOnlySpan<byte> subkey)
    {
        Span<byte> b = stackalloc byte[8];
        subkey[..Math.Min(8, subkey.Length)].CopyTo(b);
        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    /// <summary>The raw structure the host publishes in its MYSESSIONS table (dp_pack_session with another default type: a memcpy).</summary>
    public byte[] PackRaw(ushort species, AnetAddress? gameServer = null)
    {
        var b = new byte[RawSize];
        Master.Write(b.AsSpan(1));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), species);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(15), Karma);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(17), (short)MaxPlayers);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(19), (short)Players);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(21), (short)Flags);
        Str(Name, NameMax).CopyTo(b.AsSpan(23));
        var u = UserPadded();
        u.AsSpan(4, 10).CopyTo(b.AsSpan(55));
        Str(Password, PasswordMax).CopyTo(b.AsSpan(65));
        // dwReserved1: the game server's IP, as dpOpen puts it (for server-to-server propagation)
        if (gameServer is { } gs) { var g = gs.ToBytes(); g.AsSpan(0, 4).CopyTo(b.AsSpan(75)); }
        u.AsSpan(0, 4).CopyTo(b.AsSpan(79));
        return b;
    }

    /// <summary>The compact form the server sends subscribers (dp_pack_session with the table's own type).</summary>
    public byte[] PackCompact()
    {
        var b = new List<byte> { (byte)MaxPlayers, (byte)Players, (byte)Flags };
        var n = Str(Name, NameMax); b.Add((byte)n.Length); b.AddRange(n);
        var p = Str(Password, PasswordMax); b.Add((byte)p.Length); b.AddRange(p);
        var u = UserPadded();
        int len = u.Length;
        while (len > 0 && u[len - 1] == 0) len--;
        b.Add((byte)len); b.AddRange(u.Take(len));
        if (Master != KeyAddress) b.AddRange(Master.ToBytes());
        // a compact record of exactly 83 bytes would read as a raw one: the server sends the raw one then
        return b.Count == RawSize ? PackRaw(0) : [.. b];
    }

    /// <summary>A record from a session table: compact, or raw if it is 83 bytes (dp_unpack_session). Null if it does not parse.</summary>
    public static AnetSession? Unpack(ReadOnlySpan<byte> subkey, ReadOnlySpan<byte> d)
    {
        if (subkey.Length < SubkeySize) return null;
        var keyAdr = AnetAddress.Read(subkey);
        ushort karma = (ushort)(subkey[6] | subkey[7] << 8);
        try
        {
            if (d.Length == RawSize)
            {
                var user = new byte[UserSize];
                d.Slice(79, 4).CopyTo(user);
                d.Slice(55, 10).CopyTo(user.AsSpan(4));
                return new AnetSession(keyAdr, karma, AnetAddress.Read(d[1..]), BinaryPrimitives.ReadInt16LittleEndian(d[17..]),
                    BinaryPrimitives.ReadInt16LittleEndian(d[19..]), BinaryPrimitives.ReadInt16LittleEndian(d[21..]),
                    CStr(d.Slice(23, 32)), CStr(d.Slice(65, 10)), user);
            }
            int at = 0;
            int max = d[at++], players = d[at++], flags = d[at++];
            int nl = d[at++];
            if (nl > 32) return null;
            string name = Printable(Encoding.Latin1.GetString(d.Slice(at, nl))); at += nl;
            int pl = d[at++];
            if (pl > 10) return null;
            string pass = Printable(Encoding.Latin1.GetString(d.Slice(at, pl))); at += pl;
            int ul = d[at++];
            var u = new byte[UserSize];
            d.Slice(at, ul).ToArray().AsSpan(0, Math.Min(ul, UserSize)).CopyTo(u);
            at += ul;
            var master = d.Length - at >= AnetAddress.Size ? AnetAddress.Read(d[at..]) : keyAdr;
            return new AnetSession(keyAdr, karma, master, max, players, flags, name, pass, u);
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    private byte[] UserPadded()
    {
        var u = new byte[UserSize];
        User.AsSpan(0, Math.Min(User.Length, UserSize)).CopyTo(u);
        return u;
    }

    private static byte[] Str(string s, int max)
    {
        var b = Encoding.Latin1.GetBytes(s);
        int n = Math.Min(b.Length, max);
        int z = Array.IndexOf(b, (byte)0);
        if (z >= 0 && z < n) n = z;
        return b[..n];
    }

    private static string CStr(ReadOnlySpan<byte> b)
    {
        int z = b.IndexOf((byte)0);
        return Printable(Encoding.Latin1.GetString(z >= 0 ? b[..z] : b));
    }

    /// <summary>Control characters from a server shown as '?'.</summary>
    private static string Printable(string s) => string.Concat(s.Select(c => c < 0x20 || c is >= '\x7f' and < '\xa0' ? '?' : c));

    /// <summary>What an I26 host says about its game in the user bytes, or null if they are not ours.</summary>
    public I26SessionTag? Tag => I26SessionTag.Read(User);
}

/// <summary>
/// The 14 user bytes of an I26 session (dwUser1, then szUserField):
/// <list type="bullet">
/// <item>0: 0x26, the layout's mark and version;</item>
/// <item>1-2: the I26 network protocol version (<see cref="Protocol.Version"/>), little-endian;</item>
/// <item>3: bit 0 Nitro Pack, bit 1 team play, bit 2 invite only, bit 3 over, bits 4-5 the kind (combat, race, CTF), bits 6-7 CTF teams less 2;</item>
/// <item>4-11: the arena file's stem (Latin-1, zero padded);</item>
/// <item>12: bits 0-6 its extension: 0 none (a base game arena), 1 .CBT, 2 .RAC, 3 .CF2, 4 .CF3, 5 .CF4 (any case), 127 another;
/// bit 7 a custom map (protocol 11; before it the byte was the code alone, 255 another);</item>
/// <item>13: bits 0-3 the goal (<see cref="MeleeGoal"/>), bit 4 the custom map is shared (protocol 11), bit 5 the game has a password (protocol 12),
/// bit 6 the game allows Hidden Weapons &amp; Cars, bit 7 bit 6 is said (after 0.5.0; a 0.5.0 host leaves both clear, so its rule is unknown).
/// The session's own password field stays empty: the server lists it in plain text, and does not use it.</item>
/// </list>
/// The game, I76 or Nitro, is a flag rather than a second species: one
/// species to ask the server operators about, one table, and each game
/// filters out the other's sessions.
/// </summary>
/// The map's hash itself does not fit: the direct answer (<see cref="SessionInfo.MapHash"/>) and the offer before the seat
/// carry it.
public sealed record I26SessionTag(ushort Protocol, bool Nitro, bool TeamPlay, bool InviteOnly, bool Over, MeleeKind Kind, int CtfTeams, MeleeGoal Goal, string Arena,
    bool CustomMap = false, bool MapShared = false, bool Locked = false, bool? Hidden = null, bool Coop = false, bool Lobby = false)
{
    public const byte Mark = 0x26;
    private static readonly string[] Exts = ["", ".CBT", ".RAC", ".CF2", ".CF3", ".CF4"];

    public byte[] Write()
    {
        var b = new byte[AnetSession.UserSize];
        b[0] = Mark;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), Protocol);
        // the kind's 2 bits: combat 0, race 1, CTF 2, a co-op story mission 3 (protocol 15; an older reader skips the game)
        b[3] = (byte)((Nitro ? 1 : 0) | (TeamPlay ? 2 : 0) | (InviteOnly ? 4 : 0) | (Over ? 8 : 0) | (Coop ? 3 : (int)Kind & 3) << 4 | (Math.Clamp(CtfTeams, 2, 5) - 2) << 6);
        string ext = Path.GetExtension(Arena), stem = Path.GetFileNameWithoutExtension(Arena);
        if (ext.Equals(".msn", StringComparison.OrdinalIgnoreCase)) ext = "";
        int e = Array.FindIndex(Exts, x => x.Equals(ext, StringComparison.OrdinalIgnoreCase));
        var s = Encoding.Latin1.GetBytes(stem);
        s.AsSpan(0, Math.Min(8, s.Length)).CopyTo(b.AsSpan(4));
        b[12] = (byte)((e < 0 ? 0x7f : e) | (CustomMap ? 0x80 : 0));
        b[13] = (byte)(((int)Goal & 0x0f) | (Coop && Lobby ? 0x08 : 0) | (CustomMap && MapShared ? 0x10 : 0) | (Locked ? 0x20 : 0) | (Hidden is { } h ? 0x80 | (h ? 0x40 : 0) : 0));
        return b;
    }

    public static I26SessionTag? Read(ReadOnlySpan<byte> u)
    {
        if (u.Length < AnetSession.UserSize || u[0] != Mark) return null;
        byte f = u[3];
        var stemBytes = u.Slice(4, 8);
        int z = stemBytes.IndexOf((byte)0);
        string stem = Encoding.Latin1.GetString(z >= 0 ? stemBytes[..z] : stemBytes);
        if (stem.Length == 0 || stem.Any(c => c < 0x20 || c is '/' or '\\' or ':')) return null;
        int code = u[12] & 0x7f;
        string ext = code < Exts.Length ? Exts[code] : "";
        var kind = (MeleeKind)((f >> 4) & 3);
        bool coop = (int)kind == 3;
        if (coop) kind = MeleeKind.Combat;
        int g = u[13] & (coop ? 0x07 : 0x0f);
        var goal = g <= (byte)MeleeGoal.Kills ? (MeleeGoal)g : MeleeGoal.None;
        return new I26SessionTag(BinaryPrimitives.ReadUInt16LittleEndian(u[1..]), (f & 1) != 0, (f & 2) != 0, (f & 4) != 0, (f & 8) != 0,
            kind, ((f >> 6) & 3) + 2, goal, stem + ext, (u[12] & 0x80) != 0, (u[13] & 0x10) != 0, (u[13] & 0x20) != 0,
            (u[13] & 0x80) != 0 ? (u[13] & 0x40) != 0 : null, coop, coop && (u[13] & 0x08) != 0);
    }
}
