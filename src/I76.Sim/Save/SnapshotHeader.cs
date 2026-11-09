using System.IO.Compression;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;

namespace I76.Sim.Save;

/// <summary>
/// What a session was built from: enough to build the same mission again before restoring a snapshot into it.
/// Static data (models, the mission file, terrain) is never in a snapshot; restoring rebuilds the mission from
/// the install named here and then applies the snapshot.
/// </summary>
public sealed record SessionBuild
{
    /// <summary>The mission's file stem, lower case ("t01", "m03", "n21").</summary>
    public required string Mission { get; init; }
    /// <summary><see cref="InstallIdentity"/> of the install the mission was read from.</summary>
    public ulong Install { get; init; }
    /// <summary>The requested car (a trip's, a scenario's or a melee's player car, .vcf), null for the mission's own.</summary>
    public string? Car { get; init; }
    /// <summary>The mission script runs (MissionSetup.Build's script).</summary>
    public bool Script { get; init; } = true;
    /// <summary>A melee's roster (the opponents' .vcf), null when the mission is not set up as a melee.</summary>
    public IReadOnlyList<string>? Melee { get; init; }
    /// <summary>The melee's rules, null for the single-player melee's.</summary>
    public MeleeRules? Rules { get; init; }
    /// <summary>A co-op story mission's seats (their cars), respawn delay, damage factor and scaling; null for single player.</summary>
    public CoopSetup? Coop { get; init; }

    /// <summary>
    /// What identifies the install for a snapshot: the profile, the archive's size, and the mission file's and
    /// its terrain's sizes and CRCs (<see cref="MapFingerprint"/>). Two installs that agree on these build the
    /// same mission.
    /// </summary>
    public static ulong InstallIdentity(GameInstall install, string mission)
    {
        ulong h = 14695981039346656037UL;
        void Add(ulong x) { for (int i = 0; i < 8; i++) { h ^= (x >> (8 * i)) & 0xff; h *= 1099511628211UL; } }
        Add((ulong)install.Profile);
        var zfs = install.FindFile("I76.ZFS") ?? install.FindFile("nitro.zfs");
        Add(zfs is null ? 0 : (ulong)new FileInfo(zfs).Length);
        try
        {
            if (MapFingerprint.Of(install, mission) is { } m) { Add(m.MissionSize); Add(m.MissionCrc); Add(m.TerrainSize); Add(m.TerrainCrc); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Add(0xdead); }
        return h;
    }

    internal void Write(SaveWriter w)
    {
        w.Str(Mission); w.VarU(Install); w.Str(Car); w.Bool(Script);
        w.Bool(Melee is not null);
        if (Melee is not null) { w.VarU((ulong)Melee.Count); foreach (var m in Melee) w.Str(m); }
        w.Bool(Rules is not null);
        if (Rules is { } r)
        {
            w.Bool(r.Network); w.Bool(r.TeamPlay); w.Int(r.MaxScore); w.U8((byte)r.Kind); w.Int(r.CtfTeams);
            w.U8((byte)r.Goal); w.Int(r.GoalValue); w.F32(r.TimeLimit); w.F32(r.RespawnDelay);
        }
        w.Bool(Coop is not null);
        if (Coop is { } c) { w.VarU((ulong)c.Cars.Count); foreach (var car in c.Cars) w.Str(car); w.F32(c.RespawnDelay); w.F32(c.DamageFactor); w.Int(c.ExtraEnemies); }
    }

    internal static SessionBuild Read(SaveReader r)
    {
        var mission = r.Str() ?? "";
        ulong install = r.VarU();
        var car = r.Str();
        bool script = r.Bool();
        List<string>? melee = null;
        if (r.Bool()) { int n = r.Count(); melee = []; for (int i = 0; i < n; i++) melee.Add(r.Str() ?? ""); }
        MeleeRules? rules = null;
        if (r.Bool())
            rules = new MeleeRules
            {
                Network = r.Bool(), TeamPlay = r.Bool(), MaxScore = r.Int(), Kind = (MeleeKind)r.U8(), CtfTeams = r.Int(),
                Goal = (MeleeGoal)r.U8(), GoalValue = r.Int(), TimeLimit = r.F32(), RespawnDelay = r.F32(),
            };
        CoopSetup? coop = null;
        if (r.Bool())
        {
            int n = r.Count(); var cars = new List<string?>();
            for (int i = 0; i < n; i++) cars.Add(r.Str());
            coop = new CoopSetup(cars) { RespawnDelay = r.F32(), DamageFactor = r.F32(), ExtraEnemies = r.Int() };
        }
        return new SessionBuild { Mission = mission, Install = install, Car = car, Script = script, Melee = melee, Rules = rules, Coop = coop };
    }

    /// <summary>Why a session built from <paramref name="other"/> cannot take this build's snapshot, or null.</summary>
    internal string? Differs(SessionBuild other)
    {
        if (!Mission.Equals(other.Mission, StringComparison.OrdinalIgnoreCase)) return $"the snapshot is of mission {Mission}, the session is {other.Mission}";
        if (Install != other.Install) return $"the snapshot was made with a different install ({Install:x16}) than the session's ({other.Install:x16})";
        if (!string.Equals(Car ?? "", other.Car ?? "", StringComparison.OrdinalIgnoreCase)) return $"the snapshot's car is {Car ?? "the mission's"}, the session's {other.Car ?? "the mission's"}";
        if (Script != other.Script) return Script ? "the snapshot runs the mission script, the session does not" : "the session runs the mission script, the snapshot does not";
        if ((Melee is null) != (other.Melee is null) || (Melee is not null && !Melee.SequenceEqual(other.Melee!, StringComparer.OrdinalIgnoreCase)))
            return "the snapshot's melee roster is not the session's";
        if ((Rules ?? MeleeRules.SinglePlayer) != (other.Rules ?? MeleeRules.SinglePlayer)) return "the snapshot's melee rules are not the session's";
        if ((Coop is null) != (other.Coop is null) || Coop is { } c && (c.RespawnDelay != other.Coop!.RespawnDelay
                || !c.Cars.Select(x => x ?? "").SequenceEqual(other.Coop.Cars.Select(x => x ?? ""), StringComparer.OrdinalIgnoreCase)))
            return "the snapshot's co-op seats are not the session's";
        if (Coop is { } cd && cd.DamageFactor != other.Coop!.DamageFactor) return $"the snapshot's co-op damage is x{cd.DamageFactor}, the session's x{other.Coop.DamageFactor}";
        if (Coop is { } cs && cs.ExtraEnemies != other.Coop!.ExtraEnemies) return $"the snapshot's co-op scaling is {cs.ExtraEnemies} extra, the session's {other.Coop.ExtraEnemies}";
        return null;
    }
}

/// <summary>A snapshot's header: readable without a session, to know what to build before restoring.</summary>
public sealed record SnapshotHeader(int Version, SessionBuild Build, SimRate Rate, FarPolicy Far, int Ticks, byte[] Host, int BodyOffset, int BodyLength)
{
    /// <summary>"I76SNAP" and a zero.</summary>
    public static ReadOnlySpan<byte> Magic => "I76SNAP\0"u8;

    /// <summary>
    /// The format's version. Any change to what a snapshot holds or how it is laid out bumps it; a snapshot of
    /// another version is refused (docs/savestate.md).
    /// </summary>
    public const int CurrentVersion = 16;

    public static SnapshotHeader Read(byte[] data)
    {
        if (data.Length < Magic.Length + 2 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new SnapshotFormatException("not a snapshot (no I76SNAP magic)");
        var r = new SaveReader(data, new SaveRefs(), Magic.Length);
        int version = r.Int();
        if (version != CurrentVersion)
            throw new SnapshotMismatchException($"the snapshot is format version {version}; this build reads version {CurrentVersion}");
        var build = SessionBuild.Read(r);
        int label = r.Int();
        float tick = r.F32();
        var far = (FarPolicy)r.U8();
        int ticks = r.Int();
        var host = r.Bytes();
        int length = r.Int();
        int offset = data.Length - length;
        if (length < 0 || offset < Magic.Length) throw new SnapshotFormatException("snapshot truncated");
        return new SnapshotHeader(version, build, new SimRate(label, tick), far, ticks, host, offset, length);
    }

    internal static byte[] Compose(SessionBuild build, SimRate rate, FarPolicy far, int ticks, byte[]? host, byte[] body)
    {
        var w = new SaveWriter(new SaveRefs());
        foreach (byte b in Magic) w.U8(b);
        w.Int(CurrentVersion);
        build.Write(w);
        w.Int(rate.Label);
        w.F32(rate.TickSeconds);
        w.U8((byte)far);
        w.Int(ticks);
        w.Bytes(host ?? []);
        var packed = Pack(body);
        w.Int(packed.Length);
        var head = w.ToArray();
        var all = new byte[head.Length + packed.Length];
        head.CopyTo(all, 0);
        packed.CopyTo(all, head.Length);
        return all;
    }

    private static byte[] Pack(byte[] body)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(body.Length));
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(body);
        return ms.ToArray();
    }

    /// <summary>The body, inflated.</summary>
    public byte[] Body(byte[] data)
    {
        var span = data.AsSpan(BodyOffset, BodyLength);
        if (span.Length < 4) throw new SnapshotFormatException("snapshot body truncated");
        int n = BitConverter.ToInt32(span);
        if (n < 0 || n > 1 << 30) throw new SnapshotFormatException("bad snapshot body length");
        var body = new byte[n];
        using var z = new DeflateStream(new MemoryStream(data, BodyOffset + 4, BodyLength - 4), CompressionMode.Decompress);
        int got = 0;
        while (got < n)
        {
            int k = z.Read(body, got, n - got);
            if (k <= 0) throw new SnapshotFormatException("snapshot body truncated");
            got += k;
        }
        return body;
    }
}
