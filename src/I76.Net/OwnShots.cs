using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Net;

/// <summary>
/// A network client's own shots, drawn the moment it fires (docs/multiplayer.md, "Own shots at once"). The prediction
/// runs the own car's triggers and weapons on each fresh input tick (<see cref="I76.Sim.Session.SimSession.PredictFire"/>):
/// the muzzle flash and sound play at once from the predicted car, and its rounds fly in the mirror's
/// <see cref="Combat.PredictedRounds"/>, harming nothing. This keeps them square with the server's:
/// <list type="bullet">
/// <item>The server's shot events for the own car are not drawn again (<see cref="ClientWorld"/>).</item>
/// <item>The server's own rounds of the fast kinds (guns, rockets, missiles, mortars in flight) are hidden: the drawn
/// ones are the predicted. Impacts, explosions and damage stay the server's events.</item>
/// <item>The lasting kinds (dropped oil, fire, caltrops, mines and Blox, and the chemical mortar's cloud) are the
/// server's to keep: the predicted copy is drawn until the server's appears in the view (landed, for the chemical
/// mortar), then the server's takes its place.</item>
/// <item>Each snapshot carries every own weapon's shot count after the acked input (<see cref="OwnFire"/>). Fewer than
/// drawn (out of ammo, the weapon destroyed, the car dead, a trigger the server never saw): the newest predicted rounds
/// of that weapon up to the acked tick are taken away. More (an input the server held, a tick before prediction began):
/// that many of the newest server rounds of that weapon are drawn.</item>
/// </list>
/// </summary>
public sealed class OwnShots(ClientWorld world)
{
    private readonly record struct Pending(uint Tick, Weapon Weapon, Round? Round);

    private readonly List<Pending> _pending = [];
    private readonly Dictionary<Weapon, ushort> _confirmed = [];
    private readonly HashSet<int> _adopted = [], _seen = [], _matched = [];
    private int _generation = -1;
    private Vehicle? _car;
    private bool _synced;

    /// <summary>The input tick being stepped fresh: the shots it fires are filed under it.</summary>
    public uint Tick { get; set; }
    /// <summary>The round trip (seconds), for how long a predicted lasting round waits for the server's.</summary>
    public double Rtt { get; set; } = 0.1;

    /// <summary>
    /// Counters: shots predicted; shots the server did not fire (Rejected) and of their rounds those still in flight taken
    /// away (Removed); server rounds drawn because it fired more than was predicted; lasting rounds handed to the server's.
    /// </summary>
    public int Predicted, Rejected, Removed, Adopted, HandedOver;

    private Combat Combat => world.Setup.Combat;

    /// <summary><see cref="Combat.PredictedShot"/>: a fresh tick's shot.</summary>
    public void OnShot(Weapon w, Round? r)
    {
        Predicted++;
        _pending.Add(new Pending(Tick, w, r));
    }

    private static bool Lasting(uint kind) => kind is 0xc or 0xe or 0xf or 0x10 or 0x11 or 0x13 or 0x16;

    /// <summary>
    /// The newest snapshot, before the car is put back and replayed: compares the server's shot counts after the acked
    /// input with the shots drawn up to it.
    /// </summary>
    public void Reconcile(Snapshot s, Vehicle car)
    {
        if (s.Fire is not { } fire) return;
        uint acked = s.AckedInput;
        // a new car, or a renewed one: start counting from the server's
        if (car != _car || car.NetGeneration != _generation || !_synced)
        {
            _car = car;
            _generation = car.NetGeneration;
            _synced = true;
            _confirmed.Clear();
            foreach (var e in fire.Weapons) if (RoundState.WeaponOf(car, e.Weapon) is { } w) _confirmed[w] = e.Shots;
            _pending.RemoveAll(p => p.Tick <= acked);
            foreach (var rs in s.Rounds) if (rs.Shooter == world.OwnSerial) _seen.Add(rs.Serial);
            return;
        }
        var fresh = s.Rounds.Where(rs => rs.Shooter == world.OwnSerial && !_seen.Contains(rs.Serial)).OrderByDescending(rs => rs.Serial).ToList();
        foreach (var e in fire.Weapons)
        {
            if (RoundState.WeaponOf(car, e.Weapon) is not { } w) continue;
            if (!_confirmed.TryGetValue(w, out ushort before)) { _confirmed[w] = e.Shots; continue; }
            var mine = _pending.Where(p => p.Weapon == w && p.Tick <= acked).ToList();
            int delta = (short)(ushort)(e.Shots - before) - mine.Count;
            if (delta < 0)
            {
                // drawn but never fired: the newest of them go
                Rejected -= delta;
                foreach (var p in mine.OrderByDescending(p => p.Tick).Take(-delta))
                    if (p.Round is { } r && Combat.PredictedRounds.Remove(r)) Removed++;
            }
            else if (delta > 0 && !Lasting(w.Def.Kind))
            {
                // fired but never drawn: the server's newest of that weapon are
                foreach (var rs in fresh.Where(rs => rs.Weapon == e.Weapon).Take(delta)) if (_adopted.Add(rs.Serial)) Adopted++;
            }
            _confirmed[w] = e.Shots;
        }
        _pending.RemoveAll(p => p.Tick <= acked);
        foreach (var rs in fresh) _seen.Add(rs.Serial);
        if (_seen.Count > 4096) { _seen.Clear(); foreach (var rs in s.Rounds) if (rs.Shooter == world.OwnSerial) _seen.Add(rs.Serial); }
        _adopted.IntersectWith(_seen);
    }

    /// <summary>
    /// Whether the view draws an own round of the server's (<see cref="ClientWorld"/> asks for each the view reaches). A
    /// lasting one that is on the ground takes the place of the oldest predicted copy of its weapon.
    /// </summary>
    public bool Show(int serial, Round r)
    {
        if (_adopted.Contains(serial)) return true;
        if (!Lasting(r.Kind)) return false;
        if (r.Kind == 0x16 && !r.Settled) return false;
        if (_matched.Add(serial))
        {
            var copy = Combat.PredictedRounds.Where(p => p.Def == r.Def && Lasting(p.Kind) && (p.Kind != 0x16 || p.Settled)).OrderByDescending(p => p.Age).FirstOrDefault()
                ?? Combat.PredictedRounds.Where(p => p.Def == r.Def && Lasting(p.Kind)).OrderByDescending(p => p.Age).FirstOrDefault();
            if (copy is not null && Combat.PredictedRounds.Remove(copy))
            {
                HandedOver++;
                r.Age = copy.Age;   // the server's copy goes on from the predicted one's age and life: a slick does not start over
                r.Life = copy.Life;
            }
            if (_matched.Count > 4096) _matched.Clear();
        }
        return true;
    }

    /// <summary>
    /// The frame: a predicted lasting round whose server copy has not come in time (it never landed for the server, or
    /// was spent before a snapshot showed it) goes.
    /// </summary>
    public void Sweep(double delay)
    {
        double wait = Rtt + delay + 0.6;
        Combat.PredictedRounds.RemoveAll(r => Lasting(r.Kind) && r.Age > (r.Kind == 0x16 ? wait + 3 : wait));
    }
}
