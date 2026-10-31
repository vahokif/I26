using Godot;
using I76.Editor.Objects;

namespace I76.Game.Editor.Objects;

/// <summary>
/// The palette's pictures: each item drawn once in a small off-screen viewport (a three-quarter view with a key light) and kept as a
/// PNG under user://cache/editor-thumbs/&lt;install&gt;/, so the next time they come off the disk. Rendered a few frames apart, the tab on
/// show first. The modern look's materials; the 1997 look's differ only in the texture filter, which a 96 px picture does not show.
/// </summary>
public partial class Thumbnails : Node
{
    public const int Size = 128;
    public required Func<CatalogueItem, Node3D?> Model { get; init; }
    public required string CacheDir { get; init; }
    /// <summary>Called on the main thread when an item's picture is ready.</summary>
    public Action<CatalogueItem, Texture2D>? Ready { get; set; }

    private readonly Queue<CatalogueItem> _queue = new();
    private readonly HashSet<string> _queued = [];
    private readonly Dictionary<string, Texture2D> _done = [];
    private SubViewport? _vp;
    private Node3D? _stage;
    private Camera3D? _cam;
    private CatalogueItem? _current;
    private int _wait;
    public int Rendered { get; private set; }
    public int FromDisk { get; private set; }

    public Texture2D? Get(CatalogueItem item) => _done.GetValueOrDefault(Key(item));

    private static string Key(CatalogueItem i) => i.Marker == MarkerKind.None || i.Marker == MarkerKind.Flag ? i.Name : "marker_" + i.Marker;

    /// <summary>Asks for a picture; <paramref name="first"/> puts it ahead of the others (the tab being looked at).</summary>
    public void Request(CatalogueItem item, bool first)
    {
        string k = Key(item);
        if (_done.ContainsKey(k) || !_queued.Add(k)) return;
        if (first)
        {
            var rest = _queue.ToList(); _queue.Clear(); _queue.Enqueue(item); foreach (var r in rest) _queue.Enqueue(r);
        }
        else _queue.Enqueue(item);
    }

    public override void _Ready()
    {
        _vp = new SubViewport { Size = new Vector2I(Size, Size), TransparentBg = true, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Msaa3D = Viewport.Msaa.Msaa4X };
        AddChild(_vp);
        _stage = new Node3D();
        _vp.AddChild(_stage);
        _cam = new Camera3D { Fov = 28, Near = 0.05f, Far = 4000 };
        _vp.AddChild(_cam);
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48, 35, 0), LightEnergy = 1.15f, ShadowEnabled = false });
        _vp.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0, 0, 0, 0),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.64f, 0.68f), AmbientLightEnergy = 0.9f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        });
    }

    public override void _Process(double delta)
    {
        if (_vp is null) return;
        if (_current is not null)
        {
            if (--_wait > 0) return;
            var img = _vp.GetTexture().GetImage();
            Finish(_current, img, save: true);
            _current = null;
            Rendered++;
            return;
        }
        // several off the disk a frame; one render at a time
        for (int guard = 0; guard < 8 && _queue.Count > 0; guard++)
        {
            var item = _queue.Dequeue();
            string path = System.IO.Path.Combine(CacheDir, Key(item) + ".png");
            if (System.IO.File.Exists(path))
            {
                var img = Image.LoadFromFile(path);
                if (img is not null && img.GetWidth() == Size) { Finish(item, img, save: false); FromDisk++; continue; }
            }
            if (Begin(item)) return;
        }
    }

    private bool Begin(CatalogueItem item)
    {
        foreach (var c in _stage!.GetChildren()) { _stage.RemoveChild(c); c.QueueFree(); }
        var model = Model(item);
        if (model is null) return false;
        _stage.AddChild(model);
        // frame the box of what was drawn
        var aabb = Bounds(model);
        var centre = aabb.GetCenter();
        float radius = Math.Max(aabb.Size.Length() / 2, 0.5f);
        float dist = radius / Mathf.Tan(Mathf.DegToRad(_cam!.Fov / 2)) * 1.12f;
        var dir = new Vector3(0.62f, 0.5f, -0.9f).Normalized();   // the model's front (game +z) is Godot -z
        _cam.Position = centre + dir * dist;
        _cam.LookAt(centre, Vector3.Up);
        _cam.Far = dist + radius * 4;
        _vp!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        _current = item;
        _wait = 3;
        return true;
    }

    private static Aabb Bounds(Node3D n)
    {
        Aabb? box = null;
        void Walk(Node x, Transform3D t)
        {
            if (x is Node3D n3) t *= n3.Transform;
            if (x is MeshInstance3D { Mesh: { } m, Visible: true } && x is not Label3D)
            {
                var b = t * m.GetAabb();
                box = box is { } a ? a.Merge(b) : b;
            }
            foreach (var c in x.GetChildren()) Walk(c, t);
        }
        foreach (var c in n.GetChildren()) Walk(c, Transform3D.Identity);
        if (n is MeshInstance3D { Mesh: { } mm }) box = mm.GetAabb();
        return box ?? new Aabb(new Vector3(-1, 0, -1), new Vector3(2, 2, 2));
    }

    private void Finish(CatalogueItem item, Image img, bool save)
    {
        if (save)
        {
            try { System.IO.Directory.CreateDirectory(CacheDir); img.SavePng(System.IO.Path.Combine(CacheDir, Key(item) + ".png")); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
        var tex = ImageTexture.CreateFromImage(img);
        _done[Key(item)] = tex;
        Ready?.Invoke(item, tex);
    }
}
