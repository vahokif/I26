using Godot;
using I76.Formats.Assembly;
using I76.Game.World;

namespace I76.Game.Sim;

/// <summary>
/// What a car of one .vcf is made of, kept after the first one so that the next spawn of that car (a respawn, the next wave, a
/// bot taking the same car, a regen) only makes nodes: assembling the model and building its meshes (the overlay search, the
/// vertex fan, the lamps' glow twins) was 15 to 150 ms a car on the main thread. The meshes are shared between the cars (a node
/// never edits one: damage swaps in a mesh of its own), but for a wheel whose texture strips a node edits. One cache a mission
/// (its materials and palette are the mission's).
/// </summary>
public sealed class CarPartsCache(I76.Formats.Vfs.AssetVfs vfs, Func<Assets.MeshBuilder> meshSource)
{
    public CarPartsCache(MissionBuilder builder) : this(builder.Vfs, () => builder.Meshes!) { }

    public sealed class Parts
    {
        public AssembledModel Model = null!;
        public Mesh? Body;
        public CarNode.LampMeshes Lamps = null!;
        public List<(int Weapon, Mesh? Mesh)> Weapons = [];
        public List<(int Weapon, Mesh? Mesh, System.Numerics.Vector3 Pivot)> Turrets = [];
        public Mesh? Rotors;
        /// <summary>The wheels' meshes by WLOC slot; null when a wheel has texture strips (a tank's tread) that its node shows frames on, so each car builds its own.</summary>
        public List<(int Slot, Mesh Mesh, System.Numerics.Vector3 Centre)>? Wheels;
    }

    private readonly Dictionary<string, Parts> _parts = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string carDef) => _parts.ContainsKey(carDef);

    /// <summary>The intact car's parts, made on the first ask (a helicopter's are made each time: its node edits the rotor mesh).</summary>
    public Parts Get(string carDef, bool heli = false)
    {
        if (!heli && _parts.TryGetValue(carDef, out var hit)) return hit;
        var meshes = meshSource();
        var model = new ModelAssembler(vfs) { SkipParts = [] }.Assemble(carDef)!;
        var parts = new Parts { Model = model };
        Fill(parts, meshes, model, heli, CarNode.BuildLamps(meshes, model, null));
        if (!heli) _parts[carDef] = parts;
        return parts;
    }

    /// <summary>Parts for a model assembled some other way (the damage-look hook's), not kept.</summary>
    public Parts Make(AssembledModel model, bool heli, CarNode.LampMeshes lamps)
    {
        var parts = new Parts { Model = model };
        Fill(parts, meshSource(), model, heli, lamps);
        return parts;
    }

    private static void Fill(Parts parts, Assets.MeshBuilder meshes, AssembledModel model, bool heli, CarNode.LampMeshes lamps)
    {
        parts.Body = meshes.Build(model, wheels: false, include: CarNode.BodyParts(model));
        parts.Lamps = lamps;
        for (int w = 0; w < model.Gizmos.Count; w++) parts.Weapons.Add((w, meshes.Build(model, wheels: false, include: CarNode.WeaponParts(model, w))));
        foreach (var t in model.Turrets)
            parts.Turrets.Add((t.Weapon, meshes.Build(model, wheels: false, include: p => p.NodeName == t.Node && model.GizmoOf.TryGetValue(p, out int w) && w == t.Weapon, origin: t.Pivot), t.Pivot));
        if (heli && CarNode.Rotors(model).Count > 0) parts.Rotors = meshes.Build(model, wheels: false, include: CarNode.IsRotor);
        var wheels = new List<(int, Mesh, System.Numerics.Vector3)>();
        bool shareable = true;
        foreach (var loc in model.Locations.Where(l => !heli && l.Tag == "WLOC"))
        {
            var t = loc.Transform.Translation;
            if (MathF.Abs(t[0]) + MathF.Abs(t[1]) + MathF.Abs(t[2]) < 1e-6f) continue;
            if (meshes.BuildWheel(model, loc.Index) is not { } wm) continue;
            if (Assets.MeshBuilder.LookOf(wm) is { Strips.Count: > 0 }) shareable = false;
            wheels.Add((loc.Index, wm, new System.Numerics.Vector3(t[0], t[1], t[2])));
        }
        parts.Wheels = shareable ? wheels : null;
    }
}
