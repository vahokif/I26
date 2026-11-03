using Godot;
using I76.Formats.Assembly;
using I76.Game.Assets;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The 1997 look's ground clutter: the sixteen scrounge cards of the mission's
/// WRLD +0x5f .sdf (ngscrng1 / ngscrng2) repeated over the 3x3 cells round the camera, each picked (NG1_, NG2_, NG3_) by the
/// surface under it, standing on the bilinear ground, turned like the pine cards, lit by the camera's horizontal forward.
/// One MultiMesh per card and detail, set each frame from <see cref="GroundClutter.Place"/>. Render only; the modern look has
/// its own plants and hides this.
/// </summary>
public partial class GroundClutterView : Node3D
{
    private MissionWorld? _world;
    private readonly Mesh?[,] _meshes = new Mesh?[3, GroundClutter.Points.Length];
    private readonly MultiMeshInstance3D?[,] _mmi = new MultiMeshInstance3D?[3, GroundClutter.Points.Length];
    private readonly List<Transform3D>[,] _at = new List<Transform3D>[3, GroundClutter.Points.Length];
    private System.Numerics.Vector3 _forward;
    private int _turnVersion;
    private readonly int[,] _litVersion = new int[3, GroundClutter.Points.Length];
    private static readonly StringName CardX = "card_x", CardY = "card_y", CardZ = "card_z";

    /// <summary>Cards in the last frame (for tests and the overlay).</summary>
    public int Drawn { get; private set; }

    public bool Loaded { get; private set; }

    public void Load(MissionWorld world, I76.Formats.Assembly.ModelAssembler asm, MeshBuilder builder)
    {
        _world = world;
        var name = I76.Formats.Mission.WorldSettings.Read(world.Mission)?.Scrounge?.Trim('\0', ' ');
        if (string.IsNullOrEmpty(name)) return;
        for (int d = 0; d < 3; d++)
        {
            var model = new ModelAssembler(asm.Vfs) { Detail = (char)('1' + d) }.Assemble(name);
            if (model is null) continue;
            foreach (var part in model.Parts)
            {
                // NG1_B011: the card is the two digits after B
                if (part.NodeName.Length < 7 || !int.TryParse(part.NodeName.AsSpan(5, 2), out int n) || n < 1 || n > GroundClutter.Points.Length) continue;
                var one = builder.Build(model, include: p => ReferenceEquals(p, part), origin: part.ToModel.T);
                if (one is null) continue;
                _meshes[d, n - 1] = one;
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = one, InstanceCount = 9 };
                mm.VisibleInstanceCount = 0;
                var mi = new MultiMeshInstance3D { Multimesh = mm, Name = $"{part.NodeName}", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
                mi.AddToGroup(ScenerySpawner.NoDropShadow);
                AddChild(mi);
                _mmi[d, n - 1] = mi;
                _at[d, n - 1] = new List<Transform3D>(9);
                Loaded = true;
            }
        }
        SetProcess(Loaded);
    }

    public override void _Process(double delta)
    {
        if (!Visible || _world is null || GetViewport()?.GetCamera3D() is not { } cam) return;
        var gb = GlobalBasis.Inverse();
        var local = ToLocal(cam.GlobalPosition);
        System.Numerics.Vector3 Axis(Vector3 a) => Coords.ToGame(gb * a);
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0 ? size.X / size.Y : 1.333f;
        float t = Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f);
        float tanV = cam.KeepAspect == Camera3D.KeepAspectEnum.Height ? t : t / aspect;
        float tanH = cam.KeepAspect == Camera3D.KeepAspectEnum.Height ? t * aspect : t;
        var fwd = Axis(-cam.GlobalBasis.Z);
        var view = new GroundClutter.View(Coords.ToGame(local), Axis(cam.GlobalBasis.X), Axis(cam.GlobalBasis.Y), fwd, tanH, tanV);
        var cards = GroundClutter.Place(view, Height, Surface);

        var turn = Coords.ToGodot(Billboard.EngineRows(fwd));
        if (fwd != _forward || _turnVersion == 0) { _forward = fwd; _turnVersion++; }
        for (int d = 0; d < 3; d++)
            for (int p = 0; p < GroundClutter.Points.Length; p++) _at[d, p]?.Clear();
        foreach (var c in cards) _at[c.Detail - 1, c.Point]?.Add(new Transform3D(turn, Coords.ToGodot(c.Position)));
        Drawn = 0;
        var box = new Aabb(local - new Vector3(300, 300, 300), new Vector3(600, 600, 600));
        for (int d = 0; d < 3; d++)
            for (int p = 0; p < GroundClutter.Points.Length; p++)
            {
                if (_mmi[d, p] is not { } mi) continue;
                var list = _at[d, p]!;
                mi.Visible = list.Count > 0;
                if (list.Count == 0) continue;
                var mm = mi.Multimesh;
                for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, list[i]);
                mm.VisibleInstanceCount = list.Count;
                mm.CustomAabb = box;
                Drawn += list.Count;
                if (_litVersion[d, p] != _turnVersion)
                {
                    _litVersion[d, p] = _turnVersion;
                    // lit as a surface turned like the card: the normal is the camera's horizontal forward (the light is handed the turn)
                    mi.SetInstanceShaderParameter(CardX, turn.Column0);
                    mi.SetInstanceShaderParameter(CardY, turn.Column1);
                    mi.SetInstanceShaderParameter(CardZ, turn.Column2);
                }
            }
    }

    private I76.Sim.Terrain.TerrainGround? _ground;

    /// <summary>The bilinear ground, the outside ground's past the map's tiles (TerrainGround.Height, as the cars stand on it).</summary>
    private double Height(double x, double z)
    {
        if (_world!.Outside is null) return _world.BilinearHeight(x, z);
        _ground ??= new I76.Sim.Terrain.TerrainGround(_world.Terrain, new I76.Sim.Terrain.Surface[8], _world.Outside);
        return _ground.Height(x, z);
    }

    private int Surface(double x, double z)
    {
        // As TerrainGround.SurfaceId: the sample's top bits (5 outside the sample range, as the original returns it; the outside ground's own past the tiles)
        int sx = (int)Math.Floor(I76.Sim.Terrain.TerrainGround.Scale * (float)x + 0.5f), sz = (int)Math.Floor(I76.Sim.Terrain.TerrainGround.Scale * (float)z + 0.5f);
        if (_world!.Terrain.Sample(sx, sz) is { } v) return (v >> 13) & 7;
        if (_world.Outside is { } o) return o.SurfaceId;
        if (sx < 0 || sz < 0 || sx > 0x2800 || sz > 0x2800) return 5;
        return 0;
    }
}
