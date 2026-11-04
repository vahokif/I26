using I76.Formats.Assembly;
using I76.Formats.Audio;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// A mission made simulable: the ground (terrain plus drive-on structures),
/// the static colliders, every placed vehicle as a sim vehicle, an entity for
/// every placement the script can name, and the script itself.
/// </summary>
public sealed partial class MissionSetup
{
    public required SimWorld World { get; init; }
    public required StaticWorld Statics { get; init; }
    public required WorldGround Ground { get; init; }
    /// <summary>Every simulated car with the placement it came from (the player's first).</summary>
    public required List<(Placement Placement, string Definition, Vehicle Vehicle)> Cars { get; init; }
    public required List<Entity> Entities { get; init; }
    public MissionScript? Script { get; init; }
    public required Weapons.Combat Combat { get; init; }
    public required I76.Sim.Damage.DamageModel Damage { get; init; }
    public ObjectiveText? ObjectiveText { get; init; }
    /// <summary>The placement each scenery collider came from.</summary>
    public required Dictionary<StaticObject, Placement> PlacementOf { get; init; }

    /// <summary>
    /// The player's car is the placement flagged as the player (ODEF flags bit 4),
    /// else a 'spawn' keyword, else a placement of the requested car, else the
    /// first vehicle; requested overrides its definition (a scenario's or a
    /// trip's car replaces the player placement's). With no placement the car
    /// starts at origin.
    /// </summary>
    /// <summary>
    /// A structure's nodes and the root's explosion, from its .sdf.
    /// The nodes are the SGEO records (not roots): each its
    /// own HP (from the record for types 2, 3, 4, 0xb and 0xc, else the ctor's 400;
    /// signs and cacti 5, poles 50, pines 100, the grain hopper's leg and bin
    /// 100), type, flags and parent. The root keeps the
    /// ctor's 400 (<see cref="StaticObject.RootHp"/>). The root's explosion:
    /// only when a node carries flag 0x1000000 (an effect marker, the
    /// fireworks stand's CF1_ZZZ1, the hopper's CR1_ZZZ1), the .xdf named in
    /// the SDFC record, kept by the engine as 8 characters ("xbldx2.x", so by its
    /// stem), or x1_bldx1 where that is "null"; it goes off at the marker
    /// (<see cref="Marker"/>). A node's own explosion is by its flags
    /// (<see cref="StructureNode.Explosion"/>). The sound goes with either:
    /// the SDFC sound, or vexplode.wav where that is "null".
    /// </summary>
    private sealed record StructureDef(string? Explosion, string Sound, StructureNode[] Nodes, Affine? Marker)
    {
        public static readonly StructureDef None = new(null, "vexplode.wav", [], null);

        /// <summary>A fresh copy of the nodes for one placement, and the part-name lookup its colliders are tagged by (an 'A' part is its '_' twin's).</summary>
        public (List<StructureNode> Nodes, Func<string, int> Index) Instance()
        {
            var nodes = Nodes.Select(n => new StructureNode
            {
                Name = n.Name, Parent = n.Parent, Type = n.Type, Flags = n.Flags, Hp = n.Hp, MaxHp = n.MaxHp,
                Gate = n.Gate is { } g ? new GateMotion { Offset = g.Offset, Delta = g.Delta, Rate = g.Rate } : null,
            }).ToList();
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++) at.TryAdd(nodes[i].Name, i);
            return (nodes, name => at.TryGetValue(name, out int i) ? i
                : name.Length > 4 && name[3] is 'A' or 'a' && at.TryGetValue(name[..3] + "_" + name[4..], out int t) ? t : -1);
        }
    }

    private static StructureDef StructureInfo(AssetVfs vfs, string? sdf)
    {
        if (sdf is null || vfs.TryRead(sdf) is not { } d) return StructureDef.None;
        var f = new Formats.Bwd2.Bwd2File(d);
        string? fx = null; string sound = "vexplode.wav";
        var nodes = new List<StructureNode>();
        Affine? marker = null;
        if (f.First("SGEO") is { } g && g.PayloadSize >= 4)
        {
            var scene = f.SceneNodes().Where(t => t.Tag == "SGEO").SelectMany(t => t.Nodes).ToList();
            var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in scene) parentOf.TryAdd(n.Name, n.IsRoot ? "" : n.Parent);
            var recs = new List<(string Name, uint Type, uint Flags, int Hp, GateMotion? Gate)>();
            var sceneByName = new Dictionary<string, Formats.Bwd2.SceneNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in scene) sceneByName.TryAdd(n.Name, n);
            // the leading u32 counts the records the engine makes nodes of (the first detail's, not its 'A' twins or other details)
            int count = (int)System.Math.Min(Formats.Bin.U32(f.Data, g.PayloadOffset), (uint)((g.PayloadSize - 4) / 0x78));
            for (int k = 0, q = g.PayloadOffset + 4; k < count; k++, q += 0x78)
            {
                var name = Formats.Bin.CStr(f.Data, q, 8);
                if (!parentOf.ContainsKey(name)) continue;   // an empty or NULL slot
                uint type = Formats.Bin.U32(f.Data, q + 0x5c), flags = Formats.Bin.U32(f.Data, q + 0x60);
                int hp = type is 2 or 3 or 4 or 0xb or 0xc ? Formats.Bin.I32(f.Data, q + 0x64) : StaticObject.RootHp;
                // a gate (the original's scene loader): the record's offset and its time; the init
                // takes B through the node's own transform and stores 1 / time
                GateMotion? gate = null;
                if (type == 7 && sceneByName.TryGetValue(name, out var gn))
                {
                    var offset = new System.Numerics.Vector3(Formats.Bin.F32(f.Data, q + 0x68), Formats.Bin.F32(f.Data, q + 0x6c), Formats.Bin.F32(f.Data, q + 0x70));
                    gate = new GateMotion { Offset = offset, Delta = Affine.From(gn.Transform).Rotate(offset), Rate = 1f / Formats.Bin.F32(f.Data, q + 0x74) };
                }
                recs.Add((name, type, flags, hp, gate));
            }
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < recs.Count; i++) index.TryAdd(recs[i].Name, i);
            foreach (var r in recs)
                nodes.Add(new StructureNode
                {
                    Name = r.Name, Type = r.Type, Flags = r.Flags, Hp = r.Hp, MaxHp = r.Hp, Gate = r.Gate,
                    Parent = parentOf.TryGetValue(r.Name, out var pn) && index.TryGetValue(pn, out int pi) ? pi : -1,
                });
            // The first marker node, its world origin by
            if (recs.FindIndex(r => (r.Flags & 0x1000000) != 0) is var mi and >= 0)
            {
                var byName = new Dictionary<string, Formats.Bwd2.SceneNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in scene) byName.TryAdd(n.Name, n);
                Affine World(Formats.Bwd2.SceneNode n, int depth)
                {
                    var t = Affine.From(n.Transform);
                    if (depth > 16 || n.IsRoot || n.Detached || !byName.TryGetValue(n.Parent, out var up) || ReferenceEquals(up, n)) return t;
                    return World(up, depth + 1).Compose(t);
                }
                if (byName.TryGetValue(recs[mi].Name, out var mn)) marker = World(mn, 0);
            }
        }
        if (f.First("SDFC") is { } c && c.PayloadSize >= 0x2c + 8)
        {
            if (marker is not null)
            {
                var n = Formats.Bin.CStr(f.Data, c.PayloadOffset + 0x2c, 13);
                if (n.IndexOf('.') is >= 0 and var dot) n = n[..dot];
                fx = n.Length == 0 || n.Equals("null", StringComparison.OrdinalIgnoreCase) ? "x1_bldx1" : n + ".xdf";
            }
            if (c.PayloadSize >= 0x39 + 1 && Formats.Bin.CStr(f.Data, c.PayloadOffset + 0x39, System.Math.Min(13, c.PayloadSize - 0x39)) is { Length: > 0 } snd
                && !snd.Equals("null", StringComparison.OrdinalIgnoreCase)) sound = snd;
        }
        else if (marker is not null) fx = "x1_bldx1";
        return new StructureDef(fx, sound, [.. nodes], marker);
    }

    /// <summary>Each SGEO node's own collision class, by name; 0 where it leaves the type's default.</summary>
    private static Func<string, uint> NodeClasses(AssetVfs vfs, string? sdf)
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (sdf is not null && sdf.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase) && vfs.TryRead(sdf) is { } d
            && new Formats.Bwd2.Bwd2File(d) is var f && f.First("SGEO") is { } g)
            for (int r = g.PayloadOffset + 4; r + 0x78 <= g.PayloadOffset + g.PayloadSize; r += 0x78)
            {
                // A type-0x37 node (an effect marker) or flag bit 1 never collides
                uint flags = Formats.Bin.U32(f.Data, r + 0x60), type = Formats.Bin.U32(f.Data, r + 0x5c);
                map.TryAdd(Formats.Bin.CStr(f.Data, r, 8), type == 0x37 || (flags & 1) != 0 ? 0x1000 : flags & 0xf000);
            }
        return n => map.GetValueOrDefault(n);
    }

    /// <summary>
    /// Each SGEO node's own type, by name; 0 for a root, whose type
    /// holds the placement's type instead (the original reads a bridge's root as 0xc).
    /// </summary>
    private static Func<string, uint> NodeTypes(AssetVfs vfs, string? sdf)
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (sdf is not null && sdf.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase) && vfs.TryRead(sdf) is { } d
            && new Formats.Bwd2.Bwd2File(d) is var f && f.First("SGEO") is { } g)
        {
            // only the placement's own root (the first record) takes the placement's type; a file whose nodes are
            // all scene roots (bebridg2: T05's bridge, deck EB1_ZRD2 among them) still has them as children of it
            var roots = f.SceneNodes().Where(t => t.Tag == "SGEO").SelectMany(t => t.Nodes).Where(n => n.IsRoot).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool first = true;
            for (int r = g.PayloadOffset + 4; r + 0x78 <= g.PayloadOffset + g.PayloadSize; r += 0x78)
            {
                var name = Formats.Bin.CStr(f.Data, r, 8);
                map.TryAdd(name, first && roots.Contains(name) ? 0 : Formats.Bin.U32(f.Data, r + 0x5c));
                if (name.Length > 0) first = false;
            }
        }
        return n => map.GetValueOrDefault(n);
    }

    /// <summary>A melee's game, when the mission was set up as one.</summary>
    public MeleeGame? Melee { get; init; }

    /// <summary>
    /// The human players' cars by seat (the player's first): a co-op mission's every seat, else the player's alone.
    /// Each takes a <see cref="Session.SimSession.AddDriver"/> in seat order.
    /// </summary>
    public IReadOnlyList<Vehicle> Humans { get; init; } = [];

    /// <param name="melee">A melee's roster (the opponents' cars, or a network melee's AI fill): the mission is set up as a melee.</param>
    /// <param name="meleeRules">The melee's kind; a network melee builds no player's car, its players join with <see cref="MeleeGame.AddPlayer"/>.</param>
    /// <param name="seed">The session's seed for what is not the game's rand stream (the poetry decks).</param>
    /// <param name="coop">
    /// A co-op story mission (docs/multiplayer.md "Co-op"): the player placement's car is seat 0's, each other seat's car
    /// stands in formation beside and behind it, every one a human (<see cref="SimWorld.Humans"/>), and the script's
    /// player is the group (<see cref="ScriptHost.Coop"/>). Null for single player; ignored for a melee.
    /// </param>
    public static MissionSetup Build(AssetVfs vfs, MissionWorld w, ModelAssembler asm, string? requested = null,
        System.Numerics.Vector3 origin = default, bool script = true, CrtRand? rand = null, IReadOnlyList<string>? melee = null,
        MeleeRules? meleeRules = null, int seed = 1, CoopSetup? coop = null)
    {
        if (melee is not null) coop = null;
        if (coop is not null && (coop.Cars.Count is < 1 or > 8)) throw new ArgumentOutOfRangeException(nameof(coop), "a co-op game has 1 to 8 humans");
        if (coop?.Cars[0] is { Length: > 0 } seat0) requested = seat0;
        bool network = melee is not null && meleeRules?.Network == true;
        var settings = WorldSettings.Read(w.Mission);
        var surfaces = settings is null ? Enumerable.Repeat(new Surface(1.9f, 0.03f, 0, 0, 0), 8).ToArray() : TerrainGround.ReadSurfaces(settings);
        // past the map's tiles: the outside ground (the one the drawing reads too), the same on every machine
        var terrain = new TerrainGround(w.Terrain, surfaces, w.Outside);
        var statics = new StaticWorld();
        var staticOf = new Dictionary<Placement, StaticObject>();
        var classes = new Dictionary<string, Func<string, uint>>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, Func<string, uint>>(StringComparer.OrdinalIgnoreCase);
        var structures = new Dictionary<string, StructureDef>(StringComparer.OrdinalIgnoreCase);
        var destroyedAsm = new ModelAssembler(vfs) { Destroyed = true };
        // every placement stands on the terrain (the original's scene loader with its ground flag, from the ODEF loader);
        // LDEF links (guardrails, tyre walls) have no ODEF type of their own; taken as type 2 (per-node box class), an assumption
        // a type 1/8/9 placement is created without the ground flag: a gun emplacement keeps its file y, collider included
        System.Numerics.Vector3 at8(Placement p) => p.Object.ObjectType == 8 ? p.Object.Position : w.GroundedPosition(p);
        foreach (var (p, type) in w.Placements.Select(p => (p, p.Object.ObjectType)).Concat(w.LinePlacements.Select(p => (p, 2u))))
        {
            if (p.Definition is null || !StaticWorld.Collides(type)) continue;
            // outside a CTF game the flags are not there at all (the object loader drops a1flag*)
            if (melee is not null && IsFlagPlacement(p) && meleeRules?.Kind != MeleeKind.Ctf) continue;
            var sd = structures.TryGetValue(p.Definition, out var known) ? known : structures[p.Definition] = StructureInfo(vfs, p.Definition);
            var (nodes, nodeIndex) = sd.Instance();
            if (asm.Assemble(p.Definition) is { } m && statics.Add(p.Object.Name, type, p.Object.Rotation, at8(p), m,
                    classes.TryGetValue(p.Definition, out var nc) ? nc : classes[p.Definition] = NodeClasses(vfs, p.Definition),
                    types.TryGetValue(p.Definition, out var nt) ? nt : types[p.Definition] = NodeTypes(vfs, p.Definition),
                    nodeIndex, (i, b) => nodes[i].Base ??= b) is { } so)
            {
                staticOf[p] = so;
                so.Nodes.AddRange(nodes);
                // a gate's slide in the world: the record's offset through its node's transform to the model and the placement
                var place = Affine.FromEngine(p.Object.Rotation, at8(p));
                foreach (var part in m.Parts)
                    if (nodeIndex(part.NodeName) is var gi and >= 0 && nodes[gi].Gate is { } gate && nodes[gi].Name.Equals(part.NodeName, StringComparison.OrdinalIgnoreCase))
                        gate.World = place.Rotate(part.ToModel.Rotate(gate.Offset));
                so.Explosion = sd.Explosion;
                so.Sound = sd.Sound;
                if (sd.Marker is { } mk) so.Marker = Affine.FromEngine(p.Object.Rotation, at8(p)).Apply(mk.Apply(System.Numerics.Vector3.Zero));
                // what it leaves when destroyed: the 'A' geometry, if any (type 3's dead nodes give no response)
                if (type != 3 && type != 4 && destroyedAsm.Assemble(p.Definition) is { } dm
                    && !dm.Parts.Select(x => x.NodeName).SequenceEqual(m.Parts.Select(x => x.NodeName)))
                    so.Remains = StaticWorld.Build(p.Object.Name, type, p.Object.Rotation, at8(p), dm, classes[p.Definition], types[p.Definition], nodeIndex);
            }
        }
        var ground = new WorldGround(terrain, statics);
        rand ??= new CrtRand(1);
        var world = new SimWorld(ground, rand);

        static bool IsCar(string? d) => d?.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) == true;
        var def = string.IsNullOrEmpty(requested) ? null : requested.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) ? requested : requested + ".vcf";
        // a melee builds its own cars (the melee flag): the player from a placement of its own, team 1 (0x10010)
        // the requested car (a trip's or a scenario's vehscn.vcf) takes the player's placement's place
        var at = melee is not null ? null : w.Placements.FirstOrDefault(p => p.Object.IsPlayer && IsCar(p.Definition))
                 ?? w.Placements.FirstOrDefault(p => p.Object.Name.Equals("spawn", StringComparison.OrdinalIgnoreCase))
                 ?? w.Placements.FirstOrDefault(p => def is not null && string.Equals(p.Definition, def, StringComparison.OrdinalIgnoreCase))
                 ?? w.Placements.FirstOrDefault(p => IsCar(p.Definition));
        def ??= IsCar(at?.Definition) ? at!.Definition! : "vppirna1.vcf";
        at ??= new Placement(new PlacedObject(def, 0x10, [1, 0, 0, 0, 1, 0, 0, 0, 1], origin, new byte[0x64]), def);

        var order = network ? [] : melee is not null ? [at] : w.Placements.Where(p => IsCar(p.Definition) && p != at).Prepend(at).ToList();
        var cars = new List<(Placement, string, Vehicle)>();
        var byPlacement = new Dictionary<Placement, Vehicle>();
        foreach (var p in order)
        {
            var carDef = p == at ? def : p.Definition!;
            if (asm.Assemble(carDef) is null) continue;
            // the car the shell gave (a trip's or a scenario's) sounds as its body; the mission's own cars as their files say
            var v = VehicleFactory.Create(vfs, carDef, meleeBrakes: melee is not null, bodySound: p == at && requested is { Length: > 0 });
            v.Rot = Rows.FromEngine(p.Object.Rotation);
            var pos = p.Object.Position;
            // the class is the .vdf's (the player's car sits on a type-1 placement whatever it is)
            // a helicopter (class 9) stays where it was placed, at least on the terrain (the helicopter's post-load)
            // an emplacement (class 8) stands where it was placed: the emplacement's post-load sets no height, and its class update is empty
            pos.Y = v.ObjectClass switch
            {
                9 => MathF.Max(pos.Y, terrain.Height(pos.X, pos.Z)),
                8 => pos.Y,
                _ => ground.Query(pos.X, pos.Y + 2, pos.Z).Height,
            };
            v.Pos = new Double3(pos.X, pos.Y, pos.Z);
            world.Vehicles.Add(v);
            byPlacement[p] = v;
            cars.Add((p, carDef, v));
            if (p == at) world.Player = v;
        }

        // co-op: the other seats' cars, built as the player's (the shell's car, its body's sounds) and stood beside it
        var seatCars = new List<(string Def, Vehicle Car)>();
        // the script's paths, which the formation keeps off (its cars are about to drive them)
        List<IReadOnlyList<System.Numerics.Vector3>>? coopPaths = null;
        if (coop is not null && world.Player is { } lead)
        {
            world.Coop = true;
            coopPaths = script ? w.Mission.ReadFsmProgram()?.Paths.Select(x => (IReadOnlyList<System.Numerics.Vector3>)x.Points).ToList() : null;
            seatCars.Add((def, lead));
            for (int k = 1; k < coop.Cars.Count; k++)
            {
                var carDef = coop.Cars[k] is { Length: > 0 } c ? (c.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) ? c : c + ".vcf") : def;
                if (asm.Assemble(carDef) is null) throw new FileNotFoundException($"co-op seat {k}: no car {carDef}");
                var v = VehicleFactory.Create(vfs, carDef, bodySound: true);
                VehicleFactory.AddDriverHandguns(vfs, carDef, v);   // the server has no cockpit to fit them (as a network melee's)
                v.Rot = lead.Rot;
                v.Pos = SpawnSpace.Beside(world, lead, v, coopPaths);
                world.Vehicles.Add(v);
                world.Humans.Add(v);
                cars.Add((at, carDef, v));
                seatCars.Add((carDef, v));
            }
        }

        var entities = new List<Entity>();
        foreach (var p in w.Placements)
        {
            var o = p.Object;
            var e = new Entity(o.Name, o.Raw.Length >= 8 ? o.Raw[..8] : [], o.ObjectType, o.ObjectFlags)
            {
                Vehicle = byPlacement.GetValueOrDefault(p),
                StaticPosition = o.Position,
                StaticRot = Rows.FromEngine(o.Rotation),
            };
            entities.Add(e);
            if (staticOf.TryGetValue(p, out var so)) { so.Owner = e; e.Static = so; }
        }
        if (!w.Placements.Contains(at) && byPlacement.TryGetValue(at, out var pv))
            entities.Add(new Entity(at.Object.Name, [], 1, 0x10010) { Vehicle = pv });
        // the other seats: the player's object flags (the player bit, team 1), no placement of their own
        for (int k = 1; k < seatCars.Count; k++)
            entities.Add(new Entity($"coop{k}", [], 1, 0x10010) { Vehicle = seatCars[k].Car });

        ObjectiveText? text = null;
        if (settings?.Text is { Length: > 0 } npt && vfs.TryRead(npt.Contains('.') ? npt : npt + ".npt") is { } bytes)
            text = ObjectiveText.Parse(System.Text.Encoding.Latin1.GetString(bytes));
        MissionScript? ms = null;
        var lengths = new Dictionary<string, float?>(StringComparer.OrdinalIgnoreCase);
        float? Length(string name)
        {
            if (!lengths.TryGetValue(name, out var l)) lengths[name] = l = SoundFiles.Read(vfs, name)?.Seconds;
            return l;
        }
        // Nitro's poetry key makes the driver of the mission say a line of their own (Poetry)
        int nitroDriver = vfs.Install.IsNitro ? I76.Formats.Vfs.GameInstall.DriverVariant(w.Mission.Name, true) : 0;
        // (Groove has none in the Nitro Pack: with Interstate '76 on the machine he says his I76 kill lines)
        bool i76Lines = nitroDriver == 1 && I76.Formats.Audio.CompanionSounds.Available;
        bool night = settings is not null && !Lighting.ForHour(settings.Hour).IsDay;
        MeleeGame? game = null;
        if (melee is not null)
        {
            var host = new ScriptHost(world, entities, new Objectives(text ?? new ObjectiveText([], [])), Length) { Tactics = I76.Formats.Exe.TacticsTable.For(vfs.Install) };
            game = new MeleeGame
            {
                World = world, Host = host, Rand = rand, Roster = melee, PlayerCar = def, Rules = meleeRules ?? MeleeRules.SinglePlayer,
                // the load: the car with its damage state; the opponents have no specials (SPEC 0 three times)
                Build = (vcf, player) =>
                {
                    var v = VehicleFactory.Create(vfs, vcf, meleeBrakes: true, bodySound: true, specials: player);
                    if (v.ObjectClass == 9) HeliStep.Setup(v, player);   // a hidden helicopter flown in a network melee
                    else if (v.Build is { } b) v.Health = I76.Sim.Damage.VehicleDamage.Create(v, b, player, rand, network);
                    if (player) VehicleFactory.AddDriverHandguns(vfs, vcf, v);   // the game fits the player's own at the cockpit; a network car has none
                    if (night) v.Flags |= VehicleFlags.Lights;
                    return v;
                },
            };
            LoadMeleePoints(game, w.Placements);
            ms = new MissionScript(null, host, rand, seed, nitroDriver, i76Lines) { Melee = game };
        }
        else if (script && w.Mission.ReadFsmProgram() is { Code.Length: > 0 } program)
        {
            var host = new ScriptHost(world, entities, new Objectives(text ?? new ObjectiveText([], [])), Length) { Tactics = I76.Formats.Exe.TacticsTable.For(vfs.Install) };
            // the human group before the script: the VM finds the group's actor slots and the per-human routines as it is made
            if (seatCars.Count > 0)
            {
                var seats = new List<CoopSeat>();
                for (int k = 0; k < seatCars.Count; k++)
                    seats.Add(new CoopSeat { Index = k, Entity = host.Of(seatCars[k].Car)!, CarFile = seatCars[k].Def });
                bool shellCar = requested is { Length: > 0 };
                host.Coop = new CoopGroup(seats, coop!.RespawnDelay, (vcf, seat) =>
                {
                    // a respawn's car: as the seat's first (the damage state, a helicopter's block, the handguns, the night's lights)
                    var v = VehicleFactory.Create(vfs, vcf, bodySound: seat > 0 || shellCar);
                    if (v.ObjectClass == 9) HeliStep.Setup(v, true);
                    else if (v.Build is { } b) v.Health = I76.Sim.Damage.VehicleDamage.Create(v, b, true, rand);
                    if (seat > 0) VehicleFactory.AddDriverHandguns(vfs, vcf, v);
                    if (night) v.Flags |= VehicleFlags.Lights;
                    return v;
                }) { Paths = coopPaths };
            }
            ms = new MissionScript(program, host, rand, seed, nitroDriver, i76Lines);
        }
        var effects = new Dictionary<string, Formats.Bwd2.EffectDef?>(StringComparer.OrdinalIgnoreCase);
        var combat = new Weapons.Combat(world, n =>
        {
            if (!effects.TryGetValue(n, out var e)) effects[n] = e = vfs.TryRead(n) is { } d ? Formats.Bwd2.EffectDef.Read(n, d) : null;
            return e;
        });
        var teamOf = entities.Where(e => e.Vehicle is not null).ToDictionary(e => e.Vehicle!, e => e.Team);
        combat.MeleeActive = melee is not null;
        combat.NetSession = network;
        world.Step.NetSession = network;
        combat.NitroRules = vfs.Install.IsNitro;
        combat.FreeForAll = network && meleeRules?.TeamPlay != true;
        combat.Team = v => teamOf.TryGetValue(v, out var t) ? t : ms?.Host.Of(v)?.Team ?? 0;
        if (ms is not null) { ms.Combat = combat; ms.Host.Combat = combat; ms.Host.Roads = Ai.RoadNetwork.Build(w.Mission, (x, z) => terrain.Height(x, z)); }
        // each car's damage state, in load order (the original draws one rand() per special)
        foreach (var (p, _, v) in cars)
            if (v.Build is { } b && v.ObjectClass is not (8 or 9)) v.Health = I76.Sim.Damage.VehicleDamage.Create(v, b, world.IsPlayerSide(v), rand);
        // the ctor's initial lights: on under a night preset
        if (settings is not null && !Lighting.ForHour(settings.Hour).IsDay)
            foreach (var (_, _, v) in cars) v.Flags |= VehicleFlags.Lights;
        // helicopters: the helicopter's creation's block and flags 0x81 (engine on, landed),
        // The helicopter's post-load's pool from the front armour; no car damage state (and none of its rand() draws)
        foreach (var (p, _, v) in cars)
            if (v.ObjectClass == 9)
            {
                HeliStep.Setup(v, world.IsPlayerSide(v));
            }
        // gun emplacements: the emplacement's creation's block, no car damage state; the
        // placement's static collider is theirs (a type-2-like per-node box collider), and when they die it keeps
        // the burnt state-3 geometry (the zones' state 3, the gun hidden)
        var wreckAsm = new ModelAssembler(vfs) { Wrecked = true };
        foreach (var (p, gunDef, v) in cars)
            if (v.ObjectClass == 8 && p != at)
            {
                v.Emplacement = new EmplacementState();
                // its collider record is the hierarchy's (its bounds' centre and radius), not a COLP box, which
                // type 8 skips: the box is set to the model's bounds, for its centre and radius (brackets, the aim point)
                if (asm.Assemble(gunDef) is { } gm && gm.Parts.SelectMany(x => x.Mesh.Vertices.Select(x.ToModel.Apply)).ToList() is { Count: > 0 } pts)
                {
                    var lo = pts.Aggregate(System.Numerics.Vector3.Min); var hi = pts.Aggregate(System.Numerics.Vector3.Max);
                    v.BoxX = [lo.X, lo.X, hi.X, hi.X]; v.BoxY = [lo.Y, lo.Y, hi.Y, hi.Y]; v.BoxZ = [lo.Z, lo.Z, hi.Z, hi.Z];
                }
                if (staticOf.TryGetValue(p, out var so))
                {
                    so.Vehicle = v;
                    v.Emplacement.Collider = so;
                    if (wreckAsm.Assemble(gunDef) is { } wm)
                    {
                        var burnt = new AssembledModel { Definition = wm.Definition, Kind = wm.Kind, ObjectCode = wm.ObjectCode };
                        burnt.Parts.AddRange(wm.Parts.Where(x => !wm.GizmoOf.ContainsKey(x)));
                        so.Remains = StaticWorld.Build(p.Object.Name, 8, p.Object.Rotation, at8(p), burnt);
                    }
                }
            }
        var damage = new I76.Sim.Damage.DamageModel(world, combat);
        if (ms is not null)
        {
            var host = ms.Host;
            host.Damage = damage;
            damage.Killed += v => { if (host.Of(v) is { } e) host.Died(e); };
            if (game is not null) { damage.KilledBy += game.Credit; game.Explode = damage.Destroy; }
            damage.Recorded += (victim, attacker, kind, mask) =>
            {
                if (host.Of(victim) is not { } ve || host.Of(attacker) is not { } ae) return;
                host.RecordDamage(ve, ae, kind switch
                {
                    I76.Sim.Damage.DamageKind.Collision => ScriptHost.Hit.Ram,
                    I76.Sim.Damage.DamageKind.Round or I76.Sim.Damage.DamageKind.Explosion => ScriptHost.Hit.Shot,
                    _ => ScriptHost.Hit.Attacked,
                });
            };
        }
        return new MissionSetup { Humans = seatCars.Count > 0 ? seatCars.Select(x => x.Car).ToList() : world.Player is { } hp ? [hp] : [], Melee = game, PlacementOf = staticOf.ToDictionary(kv => kv.Value, kv => kv.Key), Damage = damage, Combat = combat, World = world, Statics = statics, Ground = ground, Cars = cars, Entities = entities, Script = ms, ObjectiveText = text };
    }

    /// <summary>An a1flagN placement (the CTF flag), class 2 or 3.</summary>
    private static bool IsFlagPlacement(Placement p) =>
        p.Object.ObjectType is 2 or 3 && p.Object.Name.TrimStart('#').StartsWith("a1flag", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The object loader's melee rules, after a
    /// leading '#' is stripped: "spawn" (a prefix of 5, case-blind) a spawn point, in CTF
    /// into the table of team N−1 (0–4, else 0); "regen" a regen pad; "check" (a
    /// prefix) checkpoint N, check99 the finish; a1flagN a CTF flag. None of them is an object.
    /// </summary>
    internal static void LoadMeleePoints(MeleeGame game, IEnumerable<Placement> placements)
    {
        bool ctf = game.Rules.Kind == MeleeKind.Ctf;
        if (ctf) game.Ctf = new CtfGame();
        foreach (var p in placements)
        {
            string name = p.Object.Name.TrimStart('#');
            var pos = new Double3(p.Object.Position.X, p.Object.Position.Y, p.Object.Position.Z);
            if (p.Object.ObjectType == 1)
            {
                if (name.StartsWith("spawn", StringComparison.OrdinalIgnoreCase))
                {
                    var spot = new MeleeGame.Spot(Rows.FromEngine(p.Object.Rotation), pos);
                    int table = 0;
                    if (ctf)
                    {
                        // sscanf("spawn%d") minus 1: a team table 0–4, anything else 0
                        table = int.TryParse(name[5..], out int n) && n - 1 is >= 0 and <= 4 ? n - 1 : 0;
                        if (game.TeamSpawns[table].Count < 64) game.TeamSpawns[table].Add(spot);
                    }
                    else if (game.Spawns.Count < 64) game.Spawns.Add(spot);
                }
                else if (name.Equals("regen", StringComparison.OrdinalIgnoreCase)) game.Regens.Add(new MeleeGame.Spot(Rows.FromEngine(p.Object.Rotation), pos));
                else if (name.StartsWith("check", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[5..], out int cn) && cn is >= 1 and <= 99)
                    game.Checkpoints[cn] = pos;
            }
            else if (ctf && IsFlagPlacement(p) && int.TryParse(name[6..], out int fn))
                game.Ctf!.AddFlag(fn, pos);
        }
        // a CTF game's flags: only the teams that play (2–4) keep theirs in the table, the rest never have a member
        game.Ctf?.Flags.Sort((a, b) => a.Number.CompareTo(b.Number));
    }
}
