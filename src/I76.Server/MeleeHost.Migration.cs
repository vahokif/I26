using I76.Net;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("I76.Net.Tests")]

namespace I76.Server;

/// <summary>
/// Host migration's part of the game (docs/multiplayer.md "Host migration"): what a successor needs besides the session's
/// snapshot (the custom cars served, each weapon's shot count), and the successor's restore: the session from the
/// savestate, the seats from its players and drivers, the host's inputs since replayed tick for tick.
/// </summary>
public sealed partial class MeleeHost
{
    /// <summary>The game's own state outside the session that the successor's game must have: the cars served by content, the shot counts.</summary>
    internal void WriteMigrationExtras(NetWriter w)
    {
        w.VarU((uint)_custom.Count);
        foreach (var (name, vcf) in _custom.OrderBy(kv => kv.Key, StringComparer.Ordinal)) { w.Str(name); w.VarU((uint)vcf.Length); w.Bytes(vcf); }
        var shots = new List<(int Car, byte Weapon, ushort Count)>();
        foreach (var v in Setup.World.Vehicles)
        {
            if (v.NetId == 0 || v.Armory is not { } a) continue;
            for (int i = 0; i < a.Weapons.Count; i++) if (Shots(a.Weapons[i]) is var n and > 0) shots.Add((v.NetId, (byte)i, n));
            for (int k = 0; k < 2; k++) if (a.Handguns[k] is { } hg && Shots(hg) is var n and > 0) shots.Add((v.NetId, (byte)(RoundState.Handgun + k), n));
        }
        w.VarU((uint)shots.Count);
        foreach (var (car, weapon, count) in shots) { w.VarU((uint)car); w.U8(weapon); w.U16(count); }
        // the host's CB mutes (by name)
        w.VarU((uint)CbMuted.Count);
        foreach (var name in CbMuted.Order(StringComparer.Ordinal)) w.Str(name);
    }

    internal void ReadMigrationExtras(ref NetReader r)
    {
        int n = (int)r.VarU();
        for (int i = 0; i < n; i++)
        {
            string name = r.Str();
            var vcf = r.Bytes((int)r.VarU()).ToArray();
            _custom[name] = vcf;
            I76.Formats.Vfs.AssetVfs.Addon[name] = vcf;
        }
        var byId = Setup.World.Vehicles.Where(v => v.NetId != 0).ToDictionary(v => v.NetId);
        n = (int)r.VarU();
        for (int i = 0; i < n; i++)
        {
            int car = (int)r.VarU();
            byte weapon = r.U8();
            ushort count = r.U16();
            if (!byId.TryGetValue(car, out var v) || v.Armory is not { } a) continue;
            var w = weapon >= RoundState.Handgun ? a.Handguns[weapon - RoundState.Handgun] : weapon < a.Weapons.Count ? a.Weapons[weapon] : null;
            if (w is not null) _shots.AddOrUpdate(w, new(count));
        }
        CbMuted.Clear();
        for (int k = (int)r.VarU(); k > 0; k--) CbMuted.Add(r.Str());
    }

    /// <summary>
    /// Restores the host's savestate into this freshly built game (the successor's): the session (an install that differs
    /// from the host's only in its archive is taken, the arena's own files having been checked at the join; a car made up
    /// differently still refuses), then every seat again from the melee's players and the session's drivers, none of them
    /// connected yet.
    /// </summary>
    internal RestoreResult RestoreMigrated(byte[] state, IReadOnlyDictionary<int, string>? names = null)
    {
        var h = SnapshotHeader.Read(state);
        if (Session.Build is { } b && b.Install != h.Build.Install)
        {
            Log?.Invoke("this install differs from the host's (its archive); the arena's files are the same");
            Session.Build = b with { Install = h.Build.Install };
        }
        if (Mode is CoopMode co && h.Build.Coop is { } seatCars) co.AdoptCars(seatCars.Cars);
        var res = Session.Restore(state);
        _seats.Clear();
        foreach (var (actor, d) in Mode.Restore(Session.Drivers, names ?? new Dictionary<int, string>()))
        {
            var seat = new Seat { Slot = d.Slot, Name = actor.Name, Actor = actor, Driver = d, Inputs = new InputQueue(Session.Rate) };
            seat.Told = (actor.Score, actor.Kills, actor.Deaths);
            seat.ToldEpoch = actor.CarEpoch;
            _seats.Add(seat);
        }
        _seats.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        foreach (var v in res.Dropped) _defs.Remove(v);
        foreach (var v in Setup.World.Vehicles)
            if (v.NetId != 0 && !_defs.ContainsKey(v) && _seats.All(s => s.Actor.Car != v)) _defs[v] = v.Definition;
        _spawned.Clear();
        _pending.Clear();
        return res;
    }

    /// <summary>
    /// The host's inputs after the savestate, tick for tick (the game is deterministic, so this is the host's game again),
    /// as far as they go without a gap or a barrier; then, held (each driver's last keys, no commands, as a starved
    /// input queue holds them), up to <paramref name="upTo"/>. The events of all this were the old host's to send: only a
    /// game over is kept. Returns the ticks replayed and held.
    /// </summary>
    internal (int Replayed, int Held) ReplayMigrated(IReadOnlyList<LoggedTick> log, uint upTo)
    {
        int replayed = 0, held = 0;
        foreach (var t in log)
        {
            if (t.Tick <= Session.Ticks) continue;
            if (t.Tick != Session.Ticks + 1 || t.Barrier) break;
            StepWith(t.Inputs);
            replayed++;
        }
        while (Session.Ticks < upTo && !Over && !Mode.Holding(Seats))
        {
            int n = Session.Drivers.Count == 0 ? 0 : Session.Drivers.Max(d => d.Slot) + 1;
            var inputs = new CarInput[n];
            foreach (var d in Session.Drivers) inputs[d.Slot] = d.Input with { Commands = CarCommands.None };
            StepWith(inputs);
            held++;
        }
        var over = _pending.OfType<GameOverEvent>().ToList();
        _pending.Clear();
        _pending.AddRange(over);
        return (replayed, held);
    }

    /// <summary>A restored seat's player is back: the seat goes on with its connection (a new seat object; the game's player and driver are the same).</summary>
    internal Seat Bind(Seat s, object connection)
    {
        var b = new Seat { Slot = s.Slot, Name = s.Name, Actor = s.Actor, Driver = s.Driver, Inputs = new InputQueue(Session.Rate), Connection = connection };
        b.Told = s.Told;
        int i = _seats.IndexOf(s);
        if (i >= 0) _seats[i] = b;
        return b;
    }
}
