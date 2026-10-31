using I76.Sim.Session;

namespace I76.Net;

/// <summary>
/// A client's inputs on the server: each client tick applied once, in order,
/// one a server tick. Inputs arrive several times over (each message repeats
/// the last few); the first copy of a tick counts. With nothing waiting, the
/// last input's held keys and analog position are held, without its
/// one-off commands, so a late packet leaves the car as it was rather than
/// letting go of the wheel. A client that runs ahead has its oldest inputs
/// dropped (their commands carried into the next). What the server accepts
/// is checked here: unknown bits are dropped, the analog positions kept in
/// range, the script's skip and the poetry key (single player's) ignored.
/// </summary>
public sealed class InputQueue
{
    private readonly SortedList<uint, CarInput> _waiting = [];
    private readonly int _maxQueued, _catchUp;

    public InputQueue(SimRate? rate = null)
    {
        var r = rate ?? SimRate.Default;
        _maxQueued = Protocol.MaxQueuedInputs(r);
        _catchUp = Protocol.CatchUpInputs(r);
    }
    private CarInput _held;
    /// <summary>The newest tick dropped unapplied: its repeats are not taken again.</summary>
    private uint _dropped;

    /// <summary>The newest client tick received, and the last one applied (what the snapshots ack).</summary>
    public uint LastReceived { get; private set; }
    public uint LastApplied { get; private set; }
    /// <summary>Inputs that had something out of range or not allowed (counted for the log).</summary>
    public int Corrected { get; private set; }
    /// <summary>Server ticks that found no input waiting (the last one held).</summary>
    public int Starved { get; private set; }
    /// <summary>Inputs dropped because too many were waiting.</summary>
    public int Dropped { get; private set; }
    public int Waiting => _waiting.Count;
    /// <summary>What the last <see cref="Next"/> left: the inputs still waiting, or −1 when it found none (the snapshot's slack, protocol 13).</summary>
    public sbyte Slack { get; private set; }

    public const CarButtons Buttons = (CarButtons)0xffff;
    public const CarCommands Commands = CarCommands.Reverse | CarCommands.Lights | CarCommands.WeaponCycle | CarCommands.WeaponLink
        | CarCommands.TargetNearest | CarCommands.TargetNext | CarCommands.TargetFrontal | CarCommands.TargetReset
        | CarCommands.ShiftUp | CarCommands.ShiftDown | CarCommands.StartEngine | CarCommands.Special1 | CarCommands.Special2 | CarCommands.Special3
        | CarCommands.RadarRange | CarCommands.RadarCamera | CarCommands.SelfDestruct | CarCommands.TargetPrev | CarCommands.RadarRangeReset | CarCommands.RaiseDead | CarCommands.ResetUpright;

    /// <summary>The skip key goes through (a co-op mission's host: the cut-scenes are its to skip); else it is taken off as any command the network does not carry.</summary>
    public bool AllowSkip { get; set; }

    public void Add(InputMessage m)
    {
        for (int k = 0; k < m.Inputs.Count; k++)
        {
            if (m.Tick < (uint)k) break;
            uint tick = m.Tick - (uint)k;
            if (tick <= LastApplied || tick <= _dropped || _waiting.ContainsKey(tick)) continue;
            _waiting.Add(tick, Check(m.Inputs[k]));
            if (tick > LastReceived) LastReceived = tick;
        }
        // a client far ahead of the server: keep the newest, carrying the dropped commands forward
        Trim(_maxQueued);
        // the predicted car's hashes: a later message's say for a tick wins (a replay may have changed it)
        for (int k = 0; k < m.Hashes.Count; k++)
        {
            if (m.HashTick < (uint)k) break;
            uint t = m.HashTick - (uint)k;
            ref var slot = ref _hashes[t % HashRing];
            if (m.Hashes[k] == 0 || (slot.Tick == t && slot.From > m.Tick)) continue;
            slot = (t, m.Hashes[k], m.Tick);
        }
        if (m.Tick >= _needFrom) (_needFrom, NeedExact) = (m.Tick, m.NeedExact);
    }

    private const int HashRing = 64;
    private readonly (uint Tick, uint Hash, uint From)[] _hashes = new (uint, uint, uint)[HashRing];
    private uint _needFrom;

    /// <summary>The hash the client gave of its predicted car after client tick <paramref name="tick"/>, if it gave one.</summary>
    public uint? HashOf(uint tick) => _hashes[tick % HashRing] is var s && s.Tick == tick && s.Hash != 0 ? s.Hash : null;

    /// <summary>The client's newest message asked for the motion itself (it could not find a confirmed state).</summary>
    public bool NeedExact { get; private set; }

    private void Trim(int keep)
    {
        while (_waiting.Count > keep)
        {
            var c = _waiting.Values[0].Commands;
            _dropped = _waiting.Keys[0];
            _waiting.RemoveAt(0);
            var next = _waiting.Values[0];
            _waiting[_waiting.Keys[0]] = next with { Commands = next.Commands | c };
            Dropped++;
        }
    }

    /// <summary>The input for the server's next tick.</summary>
    public CarInput Next()
    {
        if (_waiting.Count == 0)
        {
            Starved++;
            Slack = -1;
            return _held with { Commands = CarCommands.None };
        }
        Trim(_catchUp);
        uint tick = _waiting.Keys[0];
        var i = _waiting.Values[0];
        _waiting.RemoveAt(0);
        LastApplied = tick;
        Slack = (sbyte)Math.Min(_waiting.Count, sbyte.MaxValue);
        _held = i;
        return i;
    }

    /// <summary>The input as the server will take it: only the known buttons and network commands, analog in ±32767.</summary>
    public CarInput Check(CarInput i)
    {
        var o = i;
        o.Buttons &= Buttons;
        o.Commands &= Commands | (AllowSkip ? CarCommands.Skip : 0);
        if (o.Steer == short.MinValue) o.Steer = -short.MaxValue;
        if (o.Throttle == short.MinValue) o.Throttle = -short.MaxValue;
        if (!o.Has(CarButtons.Analog)) { o.Steer = 0; o.Throttle = 0; }
        if (o != i) Corrected++;
        return o;
    }
}

/// <summary>A client's side of the inputs: its own tick count and the last few inputs, to send newest first.</summary>
public sealed class InputHistory
{
    private readonly CarInput[] _ring = new CarInput[64];
    private readonly int _history;
    public InputHistory(SimRate? rate = null) => _history = System.Math.Min(Protocol.InputHistory(rate ?? SimRate.Default), _ring.Length);
    public uint Tick { get; private set; }

    /// <summary>The input for the next client tick; returns the message to send (this one and the ones before it).</summary>
    public InputMessage Push(CarInput i)
    {
        Tick++;
        _ring[Tick % _ring.Length] = i;
        int n = (int)Math.Min(Tick, (uint)_history);
        var list = new CarInput[n];
        for (int k = 0; k < n; k++) list[k] = _ring[(Tick - (uint)k) % _ring.Length];
        return new InputMessage(Tick, list);
    }

    public CarInput this[uint tick] => _ring[tick % _ring.Length];
}
