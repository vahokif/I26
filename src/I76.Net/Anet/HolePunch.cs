using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;

namespace I76.Net.Anet;

/// <summary>
/// The hole punch's datagrams: LiteNetLib unconnected messages on the game
/// socket, "I26P", a kind, the session's karma and a nonce. A host answers a
/// joiner's probe; on the server's REQUEST_OPEN it probes the joiner's two
/// addresses itself. Each side's outgoing probe opens its own NAT toward the
/// other (an endpoint-independent mapping keeps the port the server saw), so
/// the other's next one gets through. Older I26 versions ignore them (not a
/// session query).
/// </summary>
public static class HolePunch
{
    public enum Kind : byte { JoinerProbe = 0, HostProbe = 1, Answer = 2 }
    private static readonly byte[] Magic = "I26P"u8.ToArray();
    public const int Size = 4 + 1 + 2 + 4;
    /// <summary>How often and how long probes go out.</summary>
    public const double Interval = 0.2, HostBurst = 10;

    public static byte[] Message(Kind kind, ushort karma, uint nonce)
    {
        var b = new byte[Size];
        Magic.CopyTo(b, 0);
        b[4] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), karma);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(7), nonce);
        return b;
    }

    public static bool TryRead(ReadOnlySpan<byte> d, out Kind kind, out ushort karma, out uint nonce)
    {
        kind = 0; karma = 0; nonce = 0;
        if (d.Length != Size || !d[..4].SequenceEqual(Magic) || d[4] > (byte)Kind.Answer) return false;
        kind = (Kind)d[4];
        karma = BinaryPrimitives.ReadUInt16LittleEndian(d[5..]);
        nonce = BinaryPrimitives.ReadUInt32LittleEndian(d[7..]);
        return true;
    }
}

/// <summary>
/// A host's anet presence (a listen server or i26-server): its session
/// listed on each server given, kept up to date from <c>describe</c> once a
/// second, and the hole punch's host side: probes to a joiner's two
/// addresses on each REQUEST_OPEN, answers to joiners' probes. Runs on the
/// game socket through <see cref="IAnetTransport"/>; call
/// <see cref="Update"/> and <see cref="HandleUnconnected"/> from the
/// network's thread.
/// </summary>
public sealed class AnetHost : IDisposable
{
    private readonly List<AnetClient> _clients = [];
    private readonly Func<AnetGame> _describe;
    private readonly Action<IPEndPoint, byte[]> _sendUnconnected;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(List<IPEndPoint> To, double Until)> _bursts = [];
    private double _nextDescribe, _nextProbe;
    private uint _nonce = (uint)Random.Shared.Next();

    public ushort Karma { get; } = (ushort)Random.Shared.Next(1, 65536);
    public IReadOnlyList<AnetClient> Servers => _clients;
    public event Action<string>? Log;
    /// <summary>Probes answered and REQUEST_OPENs heard (diagnostics, tests).</summary>
    public int Answered, Introductions;

    public AnetHost(IAnetTransport transport, IEnumerable<(string Host, int Port)> servers, Func<AnetGame> describe,
        Action<IPEndPoint, byte[]> sendUnconnected, ushort species = AnetSpecies.I26, AnetAddress? privateOverride = null)
    {
        _describe = describe;
        _sendUnconnected = sendUnconnected;
        foreach (var (h, p) in servers)
        {
            var c = new AnetClient(transport, h, p, species, () => _clock.Elapsed.TotalSeconds, Karma) { PrivateOverride = privateOverride };
            c.Log += m => Log?.Invoke(m);
            c.RequestOpen += (seen, reported) => Introduce(seen, reported);
            _clients.Add(c);
        }
    }

    /// <summary>At most this many joiners probed at once: the addresses come from the server, which is not trusted with more.</summary>
    public const int MaxBursts = 16;

    /// <summary>
    /// The ports either side of the one the server saw that are probed too. A symmetric NAT gives the joiner's probes to us
    /// a new port, and many hand them out in sequence: the server's, then ours a step or a few on (the joiner's probes may
    /// even go first, before the server's name is looked up). The probe that lands opens the joiner's NAT toward us and ours
    /// toward it, and the joiner's next probe gets in. A NAT that picks ports at random is not reached this way.
    /// </summary>
    public const int PredictSpan = 8;

    /// <summary>Where a joiner is probed on an introduction: the address the server saw, the one it reported, then the ports around the first.</summary>
    public static List<IPEndPoint> Targets(AnetAddress seen, AnetAddress reported)
    {
        var to = new List<IPEndPoint>();
        void Add(IPEndPoint ep) { if (!to.Contains(ep)) to.Add(ep); }
        if (!seen.IsZero) Add(seen.ToEndPoint());
        if (!reported.IsZero) Add(reported.ToEndPoint());
        if (!seen.IsZero)
        {
            var s = seen.ToEndPoint();
            for (int d = 1; d <= PredictSpan; d++)
                foreach (int p in new[] { s.Port + d, s.Port - d })
                    if (p is > 0 and <= 65535) Add(new IPEndPoint(s.Address, p));
        }
        return to;
    }

    private void Introduce(AnetAddress seen, AnetAddress reported)
    {
        Introductions++;
        var to = Targets(seen, reported);
        if (to.Count == 0) return;
        // the same joiner asking again (a second try) starts its burst over
        _bursts.RemoveAll(b => b.To[0].Equals(to[0]));
        if (_bursts.Count >= MaxBursts) return;
        _bursts.Add((to, _clock.Elapsed.TotalSeconds + HolePunch.HostBurst));
        foreach (var ep in to) _sendUnconnected(ep, HolePunch.Message(HolePunch.Kind.HostProbe, Karma, _nonce++));
    }

    public void Update()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now >= _nextDescribe)
        {
            _nextDescribe = now + 1;
            var g = _describe();
            foreach (var c in _clients) c.Publish(g);
        }
        foreach (var c in _clients) c.Update();
        if (now >= _nextProbe && _bursts.Count > 0)
        {
            _nextProbe = now + HolePunch.Interval;
            _bursts.RemoveAll(b => b.Until < now);
            foreach (var (to, _) in _bursts)
                foreach (var ep in to) _sendUnconnected(ep, HolePunch.Message(HolePunch.Kind.HostProbe, Karma, _nonce++));
        }
    }

    /// <summary>A punch datagram: a joiner's probe for our session is answered, and the burst toward it stops. True if it was one.</summary>
    public bool HandleUnconnected(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!HolePunch.TryRead(data, out var kind, out ushort karma, out uint nonce)) return false;
        if (karma != Karma || kind == HolePunch.Kind.HostProbe) return true;
        if (kind == HolePunch.Kind.JoinerProbe)
        {
            _sendUnconnected(from, HolePunch.Message(HolePunch.Kind.Answer, Karma, nonce));
            Answered++;
        }
        var ep = AnetLayer.Normal(from);
        _bursts.RemoveAll(b => b.To.Contains(ep));
        return true;
    }

    /// <summary>One line for the host's screens: listed, still contacting, or why not.</summary>
    public string Status => string.Join("; ", _clients.Select(c => c.State switch
    {
        AnetState.Online when c.Listed => $"Listed on {c.Name}",
        AnetState.Online or AnetState.Connecting or AnetState.Resolving => $"Contacting {c.Name}...",
        _ => $"Not listed: {c.Name} {c.Problem ?? "is not answering"}",
    }));

    public bool AnyListed => _clients.Any(c => c.Listed);

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
    }
}

/// <summary>
/// A joiner's way in through a server (dpOpen's indirect join, then the
/// punch): log in to the server on the game socket, ask it to introduce us
/// to the session's host (INDIRECT_JOIN), and meanwhile probe the host's
/// public address (what the server sees) and its own address (behind the
/// same NAT as us, the one that answers). The first address that answers
/// with the session's karma is <see cref="Reached"/>: the game connects there.
/// A host whose port is forwarded answers before the server has done anything.
/// </summary>
public sealed class AnetJoiner : IDisposable
{
    private readonly AnetClient _client;
    private readonly IAnetTransport _transport;
    private readonly ushort _species;
    private readonly Action<IPEndPoint, byte[]> _sendUnconnected;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly IPEndPoint[] _targets;
    private double _nextProbe, _askedAt = -1, _checkUntil = -1;
    private uint _nonce = (uint)Random.Shared.Next();
    private AnetClient? _check;

    public AnetSession Target { get; }
    public IPEndPoint? Reached { get; private set; }
    public string? Failed { get; private set; }
    /// <summary>How long to try after the introduction was asked for, and in all.</summary>
    public double PunchTime { get; init; } = 10;
    public double TotalTime { get; init; } = 16;
    /// <summary>
    /// Other anet servers (the address book's) to tell whose router stopped a failed punch: one is asked, from this socket,
    /// where it sees us. Another port than the joined server saw is a symmetric NAT on our side. Up to <see cref="CheckTime"/>
    /// seconds more; none, or none answering, and the failure says it may be either side.
    /// </summary>
    public IReadOnlyList<(string Host, int Port)> CheckServers { get; init; } = [];
    public double CheckTime { get; init; } = 4;
    /// <summary>Whose router a failed punch blames, once known (diagnostics, tests).</summary>
    public NatVerdict Verdict { get; private set; }
    public event Action<string>? Log;

    public AnetJoiner(IAnetTransport transport, string serverHost, int serverPort, AnetSession target, Action<IPEndPoint, byte[]> sendUnconnected,
        ushort species = AnetSpecies.I26, AnetAddress? privateOverride = null)
    {
        Target = target;
        _transport = transport;
        _species = species;
        _sendUnconnected = sendUnconnected;
        _client = new AnetClient(transport, serverHost, serverPort, species, () => _clock.Elapsed.TotalSeconds) { PrivateOverride = privateOverride, Reconnect = false };
        _client.Log += m => Log?.Invoke(m);
        _targets = target.Master == target.KeyAddress ? [target.Master.ToEndPoint()] : [target.Master.ToEndPoint(), target.KeyAddress.ToEndPoint()];
    }

    /// <summary>Where it stands, for the loading screen.</summary>
    public string Stage => Reached is not null ? "Connecting to the host..."
        : Failed is not null ? Failed
        : _check is not null ? "Checking your connection..."
        : _askedAt >= 0 ? "Joining via server..."
        : $"Contacting {_client.Name}...";

    public void Update()
    {
        if (Reached is not null || Failed is not null) return;
        double now = _clock.Elapsed.TotalSeconds;
        _client.Update();
        if (_askedAt < 0 && _client.State == AnetState.Online && _client.IndirectJoin(Target)) _askedAt = now;
        if (now >= _nextProbe)
        {
            _nextProbe = now + HolePunch.Interval;
            foreach (var t in _targets) _sendUnconnected(t, HolePunch.Message(HolePunch.Kind.JoinerProbe, Target.Karma, _nonce++));
        }
        if (_checkUntil >= 0)
        {
            // the punch has timed out; still probing while another server says where it sees us
            _check?.Update();
            if (_check?.PublicAddress is null && _check?.State != AnetState.Offline && now < _checkUntil) return;
            Fail(Diagnose(_client.PublicAddress, _check?.PublicAddress));
            return;
        }
        if (_askedAt >= 0 && now - _askedAt > PunchTime)
        {
            var other = CheckServers.FirstOrDefault(c => !(c.Host.Equals(_client.Host, StringComparison.OrdinalIgnoreCase) && c.Port == _client.Port));
            if (_client.PublicAddress is not null && other.Host is { Length: > 0 })
            {
                _checkUntil = now + CheckTime;
                _check = new AnetClient(_transport, other.Host, other.Port, _species, () => _clock.Elapsed.TotalSeconds) { Reconnect = false };
                _check.Log += m => Log?.Invoke(m);
                return;
            }
            Fail(Diagnose(_client.PublicAddress, null));
        }
        else if (_askedAt < 0 && now > TotalTime)
            Fail($"{_client.Name} {_client.Problem ?? "is not answering"}, and the host did not answer directly");
    }

    private void Fail(string why)
    {
        Failed = why;
        _check?.Dispose();
        _check = null;
        Log?.Invoke($"join via {_client.Name}: {Failed}");
    }

    /// <summary>
    /// The failed punch's message from where two servers see us (<paramref name="second"/> null: one only): the same port is
    /// an endpoint-independent NAT here, so the host's router stopped it; another port is a symmetric NAT here.
    /// </summary>
    private string Diagnose(AnetAddress? first, AnetAddress? second)
    {
        Verdict = first is null || second is null ? NatVerdict.Unknown : first == second ? NatVerdict.HostSide : NatVerdict.JoinerSymmetric;
        if (first is not null && second is not null) Log?.Invoke($"join via {_client.Name}: two servers see us at {first} and {second}");
        return FailureText(Verdict, Target.KeyAddress.Port);
    }

    /// <summary>What a failed punch tells the player: whose router, and the port the host can forward (its game's own UDP port).</summary>
    public static string FailureText(NatVerdict verdict, int hostPort) => verdict switch
    {
        NatVerdict.JoinerSymmetric =>
            "your router gives every connection a new port (a symmetric NAT, as phone hotspots and shared ISP addresses do), so the host's "
            + $"router cannot answer you. The host can forward UDP port {hostPort} to their computer, or join from another network",
        NatVerdict.HostSide =>
            "your router is fine, but the host's would not let you in (it may give every connection a new port: a symmetric NAT). "
            + $"The host can forward UDP port {hostPort} to their computer",
        _ => "no direct connection to the host: one of your routers gives every connection a new port (a symmetric NAT). "
            + $"The host can forward UDP port {hostPort} to their computer, or the one joining can try another network",
    };

    /// <summary>A punch datagram from the host (its probe or its answer to ours). True if it was one.</summary>
    public bool HandleUnconnected(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!HolePunch.TryRead(data, out var kind, out ushort karma, out _)) return false;
        if (karma != Target.Karma || kind == HolePunch.Kind.JoinerProbe || Reached is not null || Failed is not null) return true;
        Reached = AnetLayer.Normal(from);
        Log?.Invoke($"join via {_client.Name}: the host answered from {Reached} ({kind})");
        return true;
    }

    public void Dispose()
    {
        _check?.Dispose();
        _client.Dispose();
    }
}

/// <summary>Whose router stopped a failed hole punch (<see cref="AnetJoiner.CheckServers"/>).</summary>
public enum NatVerdict { Unknown, JoinerSymmetric, HostSide }

/// <summary>
/// I26's anet species: the session type a game logs in with, which keys its
/// own session table on every server (dp_addClient creates a table for any
/// species on demand, so no server needs to know us beforehand).
/// <para>
/// 0x4926 = 18726: "I" (0x49) and 26. Activision handed species out from one
/// registry, small numbers in the order games shipped (the Nitro Pack is 604,
/// the anet demos 64 and 65, the docs' examples 1103 and 1234); 18726 is far
/// above anything they assigned and below 0x8000, since some packets carry the
/// type as a signed short. Never 604: that would put I26 games in real Nitro
/// players' lists, where joining them fails.
/// </para>
/// </summary>
public static class AnetSpecies
{
    public const ushort I26 = 0x4926;
}
