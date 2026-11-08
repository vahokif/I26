using System.Net;
using System.Net.Sockets;
using Godot;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The telemetry feed (I76_TELEMETRY_UDP or shell.cfg [port] telemetry, docs/dev-hooks.md "Telemetry out"): once a
/// drawn frame, the player's car as drawn (interpolated), its motion and controls and the events of the ticks since the
/// last packet, as UDP datagrams in this port's format or Live for Speed's OutSim / OutGauge, for motion seats, dashboards
/// and loggers. Reads the sim, never writes it; a send that cannot go at once is dropped.
/// </summary>
public partial class TelemetryOut : Node
{
    private readonly List<(IPEndPoint To, TelemetryFormat Format)> _targets = [];
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
    private readonly byte[] _buf = new byte[TelemetryPackets.MaxSize];
    private readonly List<TelemetryEvent> _events = [];
    private SimDriver? _driver;
    private uint _seq;
    private System.Numerics.Vector3 _velPrev, _velCur, _rates;
    private Vehicle? _carSeen;
    private int _errors;

    /// <summary>
    /// The feed's targets from I76_TELEMETRY_UDP (or the settings' key): <c>[format@]host:port[,...]</c>, the format
    /// <c>i26</c>, <c>outsim</c> or <c>outgauge</c> (I76_TELEMETRY_FORMAT, or the settings' telemetry_format, for those
    /// without one). Null when none is set or none parses.
    /// </summary>
    public static TelemetryOut? Configured()
    {
        I76.Game.Ui.DevSignals.TelemetryTargets = null;
        var settings = I76.Game.Shell.ShellSettings.Load();
        string? spec = System.Environment.GetEnvironmentVariable("I76_TELEMETRY_UDP") is { Length: > 0 } e ? e : settings.Telemetry;
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var fallback = TelemetryPackets.ParseFormat(System.Environment.GetEnvironmentVariable("I76_TELEMETRY_FORMAT") ?? settings.TelemetryFormat);
        var t = new TelemetryOut { Name = "TelemetryOut", ProcessMode = ProcessModeEnum.Always };
        foreach (var item in spec.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            if (ParseTarget(item, fallback) is { } target) t._targets.Add(target);
            else GD.PrintErr($"telemetry: target \"{item}\" not understood ([i26|outsim|outgauge@]host:port, a numeric address or localhost)");
        }
        if (t._targets.Count == 0) { t._socket.Dispose(); t.Free(); return null; }
        I76.Game.Ui.DevSignals.TelemetryTargets = string.Join(", ", t._targets.Select(x => $"{x.Format.ToString().ToLowerInvariant()} to {x.To}"));
        GD.Print($"telemetry: {I76.Game.Ui.DevSignals.TelemetryTargets}");
        return t;
    }

    /// <summary>One target. The host is an address or localhost: no name lookup, which could hold the frame.</summary>
    public static (IPEndPoint, TelemetryFormat)? ParseTarget(string item, TelemetryFormat? fallback)
    {
        var format = fallback;
        int at = item.IndexOf('@');
        if (at >= 0) { format = TelemetryPackets.ParseFormat(item[..at]); item = item[(at + 1)..]; }
        int colon = item.LastIndexOf(':');
        if (format is not { } f || colon <= 0 || !int.TryParse(item[(colon + 1)..], out int port) || port is < 1 or > 65535) return null;
        string host = item[..colon];
        var addr = host.Equals("localhost", System.StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.TryParse(host, out var a) ? a : null;
        return addr is { AddressFamily: AddressFamily.InterNetwork } ? (new IPEndPoint(addr, port), f) : null;
    }

    public override void _Ready()
    {
        _driver = GetParent() as SimDriver;
        if (_driver is null) return;
        _driver.TickRan += OnTick;
        if (_driver.Remote is null && _driver.Combat?.Damage is I76.Sim.Damage.DamageModel dm) dm.Damaging += OnDamage;
    }

    public override void _ExitTree()
    {
        if (_driver is not null)
        {
            _driver.TickRan -= OnTick;
            if (_driver.Combat?.Damage is I76.Sim.Damage.DamageModel dm) dm.Damaging -= OnDamage;
        }
        _socket.Dispose();
    }

    private Vehicle? Car => _driver?.Player?.Sim;
    private uint TickNow => (uint)System.Math.Max(0, _driver?.Ticks ?? 0);

    private void Add(TelemetryEvent e) { if (_events.Count < TelemetryPackets.MaxEvents) _events.Add(e); }

    private void OnDamage(Vehicle v, DamagePacket p)
    {
        if (v != Car || p.Total == 0) return;
        byte mask = 0;
        for (int ch = 0; ch < 4; ch++) if (p.Ch[ch] != 0) mask |= (byte)(1 << ch);
        Add(new(TelemetryEventKind.Hit, mask, 0, TickNow, new(p.Total, 0, 0, 0)));
    }

    /// <summary>A tick has run (a network client's: a frame's ticks): its motion and its events.</summary>
    private void OnTick(IReadOnlyList<CombatEvent>? events, int from)
    {
        if (Car is not { } car || _driver is null) return;
        if (car != _carSeen) { _carSeen = car; _velPrev = _velCur = car.Vel; }
        _velPrev = _velCur; _velCur = car.Vel;
        if (_driver.Player is { } node) { var (prev, cur) = node.TickRows; _rates = TelemetryPackets.BodyRates(prev, cur, _driver.Tick); }
        uint tick = TickNow;
        var me = car.Pos.ToVector3();
        if (events is not null)
            for (int i = System.Math.Max(0, from); i < events.Count; i++)
                switch (events[i])
                {
                    case ShotFired s when s.Shooter == car:
                        Add(new(TelemetryEventKind.Shot, (byte)System.Math.Min(255u, s.Weapon.Def.Class), (ushort)System.Math.Max(0, car.Armory?.LoadOrder.IndexOf(s.Weapon) ?? 0),
                            tick, new(s.Forward, 0)));
                        break;
                    case Explosion x:
                        var off = x.At - me;
                        Add(new(TelemetryEventKind.Explosion, 0, 0, tick, new(off.Length(), off.X, off.Y, off.Z)));
                        break;
                }
        var step = _driver.World.Step;
        foreach (var (self, other, rel, n, _) in step.Impacts)
            if (self == car) Add(new(TelemetryEventKind.Collision, (byte)(other is Vehicle ? 1 : 0), 0, tick, new(rel.Length(), n.X, n.Y, n.Z)));
        foreach (var (self, _, _, n, speed) in step.ObjectHits)
            if (self == car) Add(new(TelemetryEventKind.Collision, 2, 0, tick, new(speed, n.X, n.Y, n.Z)));
    }

    public override void _Process(double delta)
    {
        if (_driver is null) return;
        var f = new TelemetryFrame { Seq = _seq++, Tick = TickNow, Alpha = _driver.Alpha, Car = "I26" };
        float dt = _driver.Tick;
        f.Time = System.Math.Max(0, (f.Tick - 1 + f.Alpha) * dt);
        if (GetTree().Paused) f.Status |= TelemetryStatus.Paused;
        if (_driver.Remote is not null) f.Status |= TelemetryStatus.Network;
        if (_driver.Remote is null && _driver.Session.Cutscene) f.Status |= TelemetryStatus.Cutscene;
        if (Car is { } v && _driver.Player is { } node)
        {
            f.Status |= TelemetryStatus.Present;
            var flags = v.Flags;
            if ((flags & VehicleFlags.EngineOn) != 0) f.Status |= TelemetryStatus.EngineOn;
            if ((flags & VehicleFlags.Airborne) != 0) f.Status |= TelemetryStatus.Airborne;
            if ((flags & VehicleFlags.Sliding) != 0) f.Status |= TelemetryStatus.Sliding;
            if ((flags & (VehicleFlags.Dead | VehicleFlags.Disabled)) != 0) f.Status |= TelemetryStatus.Dead;
            if ((flags & VehicleFlags.Lights) != 0) f.Status |= TelemetryStatus.Lights;
            if (v.Handbrake != 0) f.Status |= TelemetryStatus.Handbrake;
            (f.Rot, f.Pos) = node.PoseAt(f.Alpha);
            f.Vel = System.Numerics.Vector3.Lerp(_velPrev, _velCur, f.Alpha);
            f.Accel = dt > 0 ? (_velCur - _velPrev) / dt : default;
            f.Rates = _rates;
            f.Speed = v.Speed;
            f.Rpm = v.Engine.Rpm;
            f.Gear = (byte)(v.Gear < 0 ? 0 : System.Math.Clamp(v.Engine.Gear, 1, 4));
            f.Throttle = System.Math.Max(0, v.Throttle);
            f.Brake = System.Math.Max(0, -v.Throttle);
            f.Steer = v.Steer;
            f.Health = v.Health?.GaugeFraction() ?? 1f;
            Wheel[] wheels = [v.FrontRight, v.FrontLeft, v.RearRight, v.RearLeft];
            for (int i = 0; i < 4; i++)
            {
                if (wheels[i].Marking) f.SkidMask |= (byte)(1 << i);
                if (wheels[i].Blown) f.BlownMask |= (byte)(1 << i);
            }
            if (v.Definition is { Length: > 0 } def) f.Car = System.IO.Path.GetFileNameWithoutExtension(def);
        }
        if (I76.Game.Ui.DevSignals.Watching && (f.Status & TelemetryStatus.Present) != 0)
        {
            var att = TelemetryPackets.Attitude(f.Rot);
            I76.Game.Ui.DevSignals.Telemetry(Time.GetTicksUsec() / 1e6, att.Y, att.Z, System.Numerics.Vector3.Dot(f.Accel, f.Rot.Forward),
                System.Numerics.Vector3.Dot(f.Accel, f.Rot.Right), System.Numerics.Vector3.Dot(f.Accel, f.Rot.Up));
        }
        string weapon = Car?.Armory is { Selected: >= 0 } arm && arm.Selected < arm.Weapons.Count ? arm.Weapons[arm.Selected].Def.Name : "";
        foreach (var (to, format) in _targets)
        {
            int n = TelemetryPackets.Write(format, _buf, f, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_events), weapon);
            try { _socket.SendTo(_buf.AsSpan(0, n), SocketFlags.None, to); }
            catch (SocketException ex)
            {
                // nobody listening (an ICMP answer surfaces as ConnectionReset on Windows) or the buffer full: this packet is dropped
                if (_errors++ == 0) GD.Print($"telemetry: a send to {to} failed ({ex.SocketErrorCode}); packets that cannot go are dropped");
            }
        }
        _events.Clear();
    }
}
