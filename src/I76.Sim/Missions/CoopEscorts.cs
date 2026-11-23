using System.Numerics;
using I76.Sim.Fsm;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// Co-op Escorts (the port's, part of Co-op Scaling; docs/multiplayer.md "Co-op Scaling"): a car the mission is about is never
/// copied (<see cref="CoopKeyCars"/>), so where this table names it, the copies it would have had come as escort cars instead: as
/// many as Co-op Scaling would copy (min(N − 1, cap)), each a stock car file with its own loadout, taken in turn from the rule's pool. An escort keeps station in a
/// formation behind the car (the AI's follow order), firing at the nearest human within <see cref="FireRange"/> (in a cut-scene it
/// holds its fire); a human who
/// comes within <see cref="EngageRange"/> while the escort is within <see cref="Leash"/> of the car is attacked (the AI's attack
/// order) until it gets away or the escort strays too far, and the escort goes back to its station. It never targets the car,
/// and nothing it fires or rams hurts the car (<see cref="Damage.DamageModel.Spared"/>). Like a copy the script never sees it,
/// so its death counts for nothing; once the car is dead it attacks the nearest human as a copy does.
/// </summary>
public static class CoopEscorts
{
    /// <summary>
    /// An escort's car: <paramref name="Car"/> (a car file: the chassis and its loadout), driven at setSkill
    /// (<paramref name="DriveSkill"/>, <paramref name="ShootSkill"/>) and setAgg <paramref name="Agg"/>.
    /// </summary>
    public sealed record EscortCar(string Car, int DriveSkill, int ShootSkill, int Agg);

    /// <summary>
    /// A mission's escort rule: in <paramref name="Mission"/> (the file's stem), the key car placed as <paramref name="KeyCar"/>
    /// is escorted by cars of <paramref name="Pool"/>, the k-th escort (from 1) by the pool's (k − 1) mod its size: the same on
    /// every machine, and a restore makes each escort again from its own car file.
    /// </summary>
    public sealed record Rule(string Mission, string KeyCar, IReadOnlyList<EscortCar> Pool)
    {
        /// <summary>The car of the k-th escort (from 1).</summary>
        public EscortCar For(int k) => Pool[(k - 1) % Pool.Count];
    }

    /// <summary>
    /// The table. T09: Callisto's Dover Lightning (he flees along the canyon road and back; disable him, do not kill him, do not
    /// lose him) is escorted by three of the base game's stock enemy cars, each a different chassis, all with turrets and with
    /// the engines to keep up with him (measured in the sim: docs/multiplayer.md "Co-op Scaling"): S02's Courcheval Manta
    /// (s02vcm01: HADES and FireRite turrets, two 7.62 mm machine guns, landmines), T15's light ABX Strider (t15as02: a
    /// Pyro-Turret and a fire dropper) and S06's heavy Police Car (s06vc01: a HADES turret, a 7.62 mm machine gun, a blox
    /// dropper). Each takes the best driving (it holds a formation on a weaving car) and the shooting most of the campaign's
    /// attackers get (setSkill 1, 3; setAgg 4). Only base-game files: T09 is a base-game mission, and a client builds the
    /// escorts from its own install.
    /// </summary>
    public static readonly IReadOnlyList<Rule> Table =
    [
        new("t09", "t09dl01", [new("s02vcm01.vcf", 1, 3, 4), new("t15as02.vcf", 1, 3, 4), new("s06vc01.vcf", 1, 3, 4)]),
    ];

    /// <summary>The rule for a key car of a mission, or null.</summary>
    public static Rule? For(string mission, string keyCar) =>
        Table.FirstOrDefault(r => r.Mission.Equals(mission, StringComparison.OrdinalIgnoreCase) && r.KeyCar.Equals(keyCar, StringComparison.OrdinalIgnoreCase));

    /// <summary>The stations (right, ahead of the car, in its frame), by the escort's number: either rear quarter, then behind.</summary>
    public static readonly Vector3[] Stations = [new(-7, 0, -9), new(7, 0, -9), new(0, 0, -17)];

    /// <summary>A human this near the escort is attacked …</summary>
    public const float EngageRange = 70f;
    /// <summary>… and let go once this far.</summary>
    public const float DisengageRange = 110f;
    /// <summary>An escort attacks only while this near the car it escorts, and breaks off past <see cref="LeashOut"/>.</summary>
    public const float Leash = 120f, LeashOut = 170f;
    /// <summary>Keeping station, the escort fires at the nearest human within this range.</summary>
    public const float FireRange = 200f;
    /// <summary>
    /// An escort this far from its station has lost it: it goes there by the AI's goto order (a one-point path at the station,
    /// moved with the car: the planner finds the way round a canyon's walls, where following straight at the station sticks),
    /// <see cref="CatchUp"/> faster than the car was told to go, until it is back within <see cref="Rejoin"/> of its station.
    /// </summary>
    public const float Lost = 120f, Rejoin = 70f, CatchUp = 10f;
    /// <summary>A catching-up escort plans again once its station is this far from where its planned way ends.</summary>
    public const float Replan = 100f;
}

public sealed partial class ScriptHost
{
    /// <summary>The key cars that have escorts in this mission (<see cref="CoopEscorts.Table"/>), with their rule; found at load with the key cars.</summary>
    public IReadOnlyDictionary<Entity, CoopEscorts.Rule> Escorted => _escorted;
    private readonly Dictionary<Entity, CoopEscorts.Rule> _escorted = [];

    /// <summary>The escorts of a key car (living or not), in the order they were made; none for a car without a rule.</summary>
    public IEnumerable<Entity> EscortsOf(Entity car) => _escorted.ContainsKey(car) ? ExtrasOf(car) : [];

    /// <summary>Whether an entity is an escort (a car made beside a key car that has a rule).</summary>
    public bool IsEscort(Entity? e) => e?.ExtraOf is { } o && _escorted.ContainsKey(o);

    /// <summary>The escort rules of the mission's key cars; called by <see cref="FindKeyCars"/>.</summary>
    private void FindEscorted(string mission)
    {
        _escorted.Clear();
        foreach (var e in _keyCars.Keys)
            if (CoopEscorts.For(mission, e.Name) is { } r && EnemyGroundCar(e)) _escorted[e] = r;
    }

    /// <summary>
    /// The damage rule (<see cref="Damage.DamageModel.Spared"/>): nothing an escort fires, drops or rams hurts the car it escorts
    /// (nor tells the script it was attacked), so the escorts can neither disable nor destroy the car the mission is about.
    /// </summary>
    internal bool SparedByEscort(Vehicle victim, Vehicle attacker) =>
        _escorted.Count > 0 && Of(attacker) is { ExtraOf: { } o } && o.Vehicle == victim && _escorted.ContainsKey(o);

    /// <summary>The escorts of a key car, made the first time the script orders it: as many as Co-op Scaling would copy it.</summary>
    private void SpawnEscorts(CoopGroup c, Entity e, CoopEscorts.Rule r, int n)
    {
        for (int k = 1; k <= n; k++) SpawnEscort(c, e, r, k);
        Log($"co-op: {e.Name} is escorted by {n}");
    }

    /// <summary>An escort beside the car, facing and moving as it does, on its team, its settings the rule's.</summary>
    private void SpawnEscort(CoopGroup c, Entity e, CoopEscorts.Rule rule, int k)
    {
        var r = rule.For(k);
        if (c.BuildEnemy is null || e.Vehicle is not { } o) return;
        var v = c.BuildEnemy(r.Car);
        v.Rot = o.Rot;
        v.Pos = SpawnSpace.Beside(World, o, v, c.Paths);
        v.Vel = o.Vel;
        v.Speed = o.Speed;
        v.Flags = (v.Flags & ~VehicleFlags.Lights) | (o.Flags & VehicleFlags.Lights);
        World.Vehicles.Add(v);
        var x = new Entity(Path.GetFileNameWithoutExtension(r.Car), [], e.Type, e.ObjectFlags) { Vehicle = v, ExtraOf = e, Scaled = true };
        Add(x);
        SetSkill(x, r.DriveSkill, r.ShootSkill);
        SetAggression(x, r.Agg);
        x.Lights = e.Lights;
        Log($"co-op: {e.Name} has escort {k} ({r.Car})");
        c.RaiseSpawned(v, r.Car);
    }

    /// <summary>
    /// An escort's frame while its car lives: the attack order at a human near it (<see cref="CoopEscorts.EngageRange"/>, held to
    /// <see cref="CoopEscorts.DisengageRange"/>) while it is near the car (<see cref="CoopEscorts.Leash"/>, held to
    /// <see cref="CoopEscorts.LeashOut"/>); otherwise the follow order on the car at its station, firing at the nearest human within
    /// <see cref="CoopEscorts.FireRange"/>; lost (<see cref="CoopEscorts.Lost"/>), the goto order at its station, firing so too. While the script holds the camera, or the human is in a personal cut-scene, it only keeps station and holds its
    /// fire. Whether it is attacking or catching up is its standing order, so nothing new is saved. Collision avoidance is on
    /// for the attack and the goto, as an AI car has it (it is what backs a car off a wall), and off in the formation: on, the
    /// escort brakes and swerves away from the car it keeps station on and from the other escorts.
    /// </summary>
    private void EscortFrame(Entity x, Entity car, int slot)
    {
        var h = NearestHuman(x) as Entity;
        bool scene = View.Active || h?.Vehicle is { } hv && PersonalSceneOf(hv);
        if (scene) h = null;
        static float Planar(Entity a, Entity b) { var d = a.Position - b.Position; return MathF.Sqrt(d.X * d.X + d.Z * d.Z); }
        bool attacking = x.Order == AiOrder.Attack && x.OrderTarget is { } t && IsHumanEntity(t);
        float toCar = Planar(x, car), toHuman = h is null ? float.MaxValue : Planar(x, h);
        if (h is not null && toCar <= (attacking ? CoopEscorts.LeashOut : CoopEscorts.Leash) && toHuman <= (attacking ? CoopEscorts.DisengageRange : CoopEscorts.EngageRange))
        {
            x.Avoid = true;
            Order(x, h, AiOrder.Attack, null, 0);
            return;
        }
        var st = CoopEscorts.Stations[System.Math.Min(slot, CoopEscorts.Stations.Length - 1)];
        bool fire = h is not null && toHuman <= CoopEscorts.FireRange;
        var off = car.Position + car.Rot.ToWorld(st) - x.Position;
        float toStation = MathF.Sqrt(off.X * off.X + off.Z * off.Z);
        bool catching = x.Order == AiOrder.Goto;
        if (toStation > (catching ? CoopEscorts.Rejoin : CoopEscorts.Lost))
        {
            // a goto at its station, by a one-point path of its own that moves with the car: the AI plans its way there (round the
            // canyon's walls), and plans again each time it reaches the point as it stood, or once the car has gone on
            // <see cref="CoopEscorts.Replan"/> from where the planned way ends (a new path: the AI starts over)
            var at = car.Position + car.Rot.ToWorld(st);
            static float D2(Vector3 a, Vector3 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
            if (x.OwnPath is not { Count: 1 } own || !catching
                || x.Waypoints.Count > 0 && D2(x.Waypoints[^1], at) > CoopEscorts.Replan * CoopEscorts.Replan)
                x.OwnPath = own = [at];
            else own[0] = at;
            x.Avoid = true;
            Order(x, null, AiOrder.Goto, own, car.OrderSpeed + CoopEscorts.CatchUp);
            // a goto has no target: the guns are given the human as an AI order gives its target to them
            if (fire) FireAt(x, h!);
            else if (Combat is { } gc && x.Vehicle is { } gv) gc.RadarTarget[gv] = null;
            return;
        }
        x.Avoid = false;
        Order(x, car, AiOrder.Follow, null, 0, fire ? 1 : 0, st.X, st.Z, h);
        // holding fire, the turrets go back to straight ahead rather than track the last human
        if (!fire && Combat is { } c && x.Vehicle is { } v) c.RadarTarget[v] = null;
    }
}
