using Godot;
using I76.Formats.Assembly;
using I76.Formats.Vegetation;
using System.Threading.Tasks;
using F = I76.Formats.Vegetation.PlacedTrees;

namespace I76.Game.World;

/// <summary>
/// The modern look's real trees where the maps place trees as camera-facing cards (Seminole's pines;
/// docs/modern-vegetation.md, Placed trees): ScenerySpawner hands over each definition's placements, and they are drawn
/// with the vegetation's shader, its atlas and its ground tint, but not by the Vegetation setting: they are part of the
/// map, shown whatever it says. A tree is as tall as its card and stands at its placement's yaw; each definition's
/// placements are a MultiMesh per variant and detail tier (cross-faded as the vegetation's), which the spawner places,
/// hides when the object is destroyed and shows in the modern look only. Its meshes are grown on the pool.
/// </summary>
public partial class PlacedTrees : Node3D
{
    /// <summary>The middle and far tiers start about here (the vegetation's High shrubs').</summary>
    private const float Mid = 30f, Far = 70f;

    private sealed class Kind
    {
        public required SpeciesDef Species;
        public required float Height;
        public required Task<PlantMesh[][]> Job;
        public ArrayMesh[][]? Meshes;
        public readonly List<(MultiMeshInstance3D Node, int Variant, int Tier)> Nodes = [];
    }

    private readonly Dictionary<string, Kind?> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ShaderMaterial[] _mats = [.. Enumerable.Range(0, 3).Select(_ => new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/vegetation.gdshader") })];
    private bool _shown;

    public PlacedTrees()
    {
        var (m0, m1) = TierBands.Band(Mid);
        var (f0, f1) = TierBands.Band(Far);
        for (int t = 0; t < 3; t++)
        {
            var m = _mats[t];
            // drawn as far as the scenery is: no fade with distance
            m.SetShaderParameter("fade_start", 1e6f);
            m.SetShaderParameter("fade_end", 2e6f);
            m.SetShaderParameter("tier_in", t == 1 ? new Vector2(m0, m1) : t == 2 ? new Vector2(f0, f1) : Vector2.Zero);
            m.SetShaderParameter("tier_out", t == 0 ? new Vector2(m0, m1) : t == 1 ? new Vector2(f0, f1) : Vector2.Zero);
            m.SetShaderParameter("wind_dir", new Vector2(Wind.Direction.X, -Wind.Direction.Y));
        }
    }

    /// <summary>Whether a definition's cards are a tree the modern look grows (<see cref="F.For"/>).</summary>
    public bool Has(string def, AssembledModel model, HashSet<string> cards) => KindOf(def, model, cards) is not null;

    private Kind? KindOf(string def, AssembledModel model, HashSet<string> cards)
    {
        if (_kinds.TryGetValue(def, out var known)) return known;
        // the intact card's size: its top over the placement and its width in its own frame
        float top = 0, x0 = float.MaxValue, x1 = float.MinValue;
        foreach (var part in model.Parts)
        {
            if (!cards.Contains(part.NodeName)) continue;
            foreach (var v in part.Mesh.Vertices)
            {
                top = MathF.Max(top, part.ToModel.Apply(v).Y);
                x0 = MathF.Min(x0, v.X); x1 = MathF.Max(x1, v.X);
            }
        }
        if (top <= 0.2f || F.For(def, top, x1 - x0) is not { } species) return _kinds[def] = null;
        _ = VegetationNode.AtlasJob;
        return _kinds[def] = new Kind { Species = species, Height = top, Job = Task.Run(() => Grow(species, top)) };
    }

    /// <summary>Every variant's three tiers, scaled so that the full mesh stands as tall as the card.</summary>
    private static PlantMesh[][] Grow(SpeciesDef s, float height)
    {
        var o = new PlantMesh[s.Variants][];
        Parallel.For(0, s.Variants, v =>
        {
            var tiers = s.BuildTiers(VegHash.Mix(VegHash.Of(s.Id), (ulong)v));
            float k = height / MathF.Max(0.1f, tiers[0].Bounds().Max.Y);
            foreach (var t in tiers)
            {
                for (int i = 0; i < t.Positions.Count; i++) t.Positions[i] *= k;
                t.GroundRadius *= k;
            }
            o[v] = tiers;
        });
        return o;
    }

    /// <summary>
    /// The trees of a definition's placements (<paramref name="xf"/>: their transforms in the spawner's frame): for each,
    /// its index in <paramref name="placements"/> and its instance in each tier's MultiMesh.
    /// </summary>
    public IEnumerable<(int Placement, Node3D Node, int Index)> Add(string def, AssembledModel model, HashSet<string> cards, IReadOnlyList<Placement> placements, IReadOnlyList<Transform3D> xf)
    {
        if (KindOf(def, model, cards) is not { } k) return [];
        var o = new List<(int, Node3D, int)>();
        int variants = k.Species.Variants;
        ulong key = VegHash.Of(k.Species.Id);
        var byVariant = Enumerable.Range(0, placements.Count)
            .GroupBy(i => (int)(VegHash.Mix(key, (ulong)(long)MathF.Round(placements[i].Object.Position.X * 4), (ulong)(long)MathF.Round(placements[i].Object.Position.Z * 4)) % (ulong)variants));
        foreach (var g in byVariant)
        {
            var list = g.ToList();
            var anchor = list.Aggregate(Vector3.Zero, (a, i) => a + xf[i].Origin) / list.Count;
            for (int t = 0; t < 3; t++)
            {
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, InstanceCount = list.Count };
                var node = new MultiMeshInstance3D { Multimesh = mm, Position = anchor, MaterialOverride = _mats[t], Name = $"{k.Species.Id}_{g.Key}_{t}" };
                node.AddToGroup(ScenerySpawner.NoDropShadow);
                for (int j = 0; j < list.Count; j++)
                {
                    int i = list[j];
                    mm.SetInstanceTransform(j, new Transform3D(xf[i].Basis, xf[i].Origin - anchor));
                    var p = placements[i].Object.Position;
                    int px = (int)MathF.Round(p.X), pz = (int)MathF.Round(p.Z);
                    // stiffness, sway phase, height, a colour variation in -1..1 (vegetation.gdshader)
                    mm.SetInstanceCustomData(j, new Color(k.Species.Stiffness, VegHash.Unit(px, pz, key), k.Height, VegHash.Unit(pz, px, key) * 1.2f - 0.6f));
                    o.Add((i, node, j));
                }
                AddChild(node);
                k.Nodes.Add((node, g.Key, t));
            }
        }
        if (k.Meshes is not null) Attach(k);
        SetProcess(true);
        return o;
    }

    /// <summary>
    /// The modern look is showing (Main, with the vegetation): the ground's tint into the trees (the mission's ground
    /// colour, as the plants take it) and the atlas, once.
    /// </summary>
    public void Show(MissionBuilder b)
    {
        if (_shown || _kinds.Values.All(k => k is null)) return;
        _shown = true;
        var g = VegetationNode.GroundLook(b);
        var atlas = VegetationNode.AtlasTexture();
        foreach (var m in _mats)
        {
            m.SetShaderParameter("atlas", atlas);
            m.SetShaderParameter("tint", g.Tint);
            m.SetShaderParameter("dust", g.Dust);
            m.SetShaderParameter("saturation", g.Saturation);
        }
    }

    /// <summary>The trees' meshes in place now, behind the loading card (waits for them).</summary>
    public void Prewarm()
    {
        foreach (var k in _kinds.Values)
            if (k is { Meshes: null } && k.Job.Wait(10000)) Attach(k);
    }

    public override void _Process(double delta)
    {
        bool waiting = false;
        foreach (var k in _kinds.Values)
        {
            if (k is null || k.Meshes is not null) continue;
            if (k.Job.IsCompleted) Attach(k); else waiting = true;
        }
        if (!waiting) SetProcess(false);
    }

    private static void Attach(Kind k)
    {
        k.Meshes ??= [.. k.Job.Result.Select(v => v.Select(VegetationNode.ToMesh).ToArray())];
        foreach (var (node, v, t) in k.Nodes) node.Multimesh.Mesh ??= k.Meshes[v][t];
    }
}
