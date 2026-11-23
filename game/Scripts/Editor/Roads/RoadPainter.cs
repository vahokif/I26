using Godot;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Game.World;

namespace I76.Game.Editor.RoadTools;

/// <summary>
/// Paints the roads into the terrain's chunks again after an edit, only for the chunks the edit reaches: the road quads of the
/// whole map are flattened (cheap), the layers of the touched chunks are rasterised on the thread pool (RoadRaster, the loader's own
/// code, with the gap fills and the repaired textures the modern look has; the 1997 look's apart where either reaches a chunk, and
/// its Software renderer's where a repaired texture does), and the finished layers swap into the chunks' materials a few at a time
/// on the main thread (TerrainNode.SetRoadLayer). The ground past the map's tiles in the edit's reach too (OutsideTerrain.SetRoadLayer):
/// the original draws roads, lots and road pieces there as well. With a texture pack's roads (MissionBuilder.RoadTexturesHd: a game,
/// not the map editor) the modern look's layer is painted from its upscales at MissionBuilder.RoadLayerResolutionHd, and the 1997
/// look's, every chunk's own then, from the originals, as the mission's load bakes them.
/// </summary>
public sealed class RoadPainter(MissionBuilder builder)
{
    private const int Res = MissionBuilder.RoadLayerResolution;
    private System.Numerics.Vector4? _wanted;
    private Task<Dictionary<(int, int), (Image? Modern, Image? Faithful, Image? Software)>>? _job;
    /// <summary>Chunks painted by the last finished job, and how long it took (ms), for the editor's log.</summary>
    public int LastChunks { get; private set; }
    public double LastMs { get; private set; }
    private System.Diagnostics.Stopwatch? _clock;
    public bool Busy => _job is not null || _wanted is not null;

    /// <summary>The roads inside this world rectangle (x0, z0, x1, z1) changed.</summary>
    public void Request(System.Numerics.Vector4 bounds)
    {
        if (bounds.Z < bounds.X) return;
        _wanted = _wanted is { } w ? RoadStore.Merge(w, bounds) : bounds;
    }

    public void Pump()
    {
        if (_job is { IsCompleted: true } done)
        {
            _job = null;
            if (done.IsFaulted) GD.PrintErr($"road repaint: {done.Exception?.GetBaseException()}");
            else Apply(done.Result);
        }
        if (_job is null && _wanted is { } w)
        {
            _wanted = null;
            Start(w);
        }
    }

    private void Start(System.Numerics.Vector4 w)
    {
        var world = builder.World!;
        var terrain = builder.Terrain!;
        if (builder.RoadTextures is not { } texs || builder.RoadPieceTris is not { } tris) return;
        // the 1997 look's textures: a stock texture's slip as the file has it (TextureRepairs); the map editor's are the same set
        var texs97 = builder.RoadTextures97 ?? texs;
        // the modern look's with a texture pack's roads: its upscales, denser (null: the same set as the 1997 look's Software renderer's)
        var texsHd = builder.RoadTexturesHd;
        int resModern = texsHd is null ? Res : MissionBuilder.RoadLayerResolutionHd;
        var differing = RoadRaster.Differing(texs, texs97);
        float chunk = TerrainNode.Chunk * TerrainNode.SampleSize, margin = 12f;
        var origins = TerrainNode.ChunkOrigins(world.Terrain)
            .Where(o => o.X0 * 5f < w.Z + margin && o.X0 * 5f + chunk > w.X - margin && o.Z0 * 5f < w.W + margin && o.Z0 * 5f + chunk > w.Y - margin).ToList();
        // the outside ground's chunks in reach (an empty ZMAP cell, or past the grid): painted where a road reaches them, else their layer taken away
        if (builder.OutsideNode is { Roads: true })
        {
            int cx0 = (int)MathF.Floor((w.X - margin) / chunk), cx1 = (int)MathF.Floor((w.Z + margin) / chunk);
            int cz0 = (int)MathF.Floor((w.Y - margin) / chunk), cz1 = (int)MathF.Floor((w.W + margin) / chunk);
            if ((long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1) <= 1 << 16)
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                        if (world.Terrain.Sample(cx * TerrainNode.Chunk, cz * TerrainNode.Chunk) is null) origins.Add((cx * TerrainNode.Chunk, cz * TerrainNode.Chunk));
        }
        if (origins.Count == 0) return;
        _clock = System.Diagnostics.Stopwatch.StartNew();
        var tri = tris.ToList();
        _job = Task.Run(() =>
        {
            // the quads of every road as the loader flattens them, with the gap fills the modern look adds
            var segments = I76.Formats.Assembly.Roads.Segments(world.Mission, world.BilinearHeight);
            var pieces = I76.Formats.Assembly.Roads.Build(world.Mission, world.BilinearHeight);
            var flat = RoadRaster.Flatten(pieces);
            var fills = RoadRaster.Flatten(RoadGaps.Fill(segments, flat, tri, world.BilinearHeight));
            var modernQuads = flat.Concat(fills).ToList();
            var result = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), (Image?, Image?, Image?)>();
            var indexModern = new RoadRaster.Index(modernQuads, tri, chunk);
            var indexFaithful = new RoadRaster.Index(flat, tri, chunk);
            Parallel.ForEach(origins, o =>
            {
                var origin = new System.Numerics.Vector2(o.X0 * 5f, o.Z0 * 5f);
                Image? Make(IReadOnlyList<RoadRaster.FlatQuad> q, RoadRaster.Index ix, Dictionary<int, RoadRaster.Tex?> set, int res = Res)
                {
                    var layer = RoadRaster.Bake(q, origin, chunk, res, i => set.GetValueOrDefault(i), tris: tri, index: ix);
                    if (layer is null) return null;
                    var img = Image.CreateFromData(res, res, false, Image.Format.Rgba8, layer);
                    img.GenerateMipmaps();
                    return img;
                }
                var modern = Make(modernQuads, indexModern, texsHd ?? texs, resModern);
                // the 1997 look draws no fills, and the files' own textures: the same image where neither a fill nor a repaired texture reaches
                // the chunk (never with a pack's roads, the modern look's)
                bool own = texsHd is not null || RoadRaster.OwnFaithful(origin, chunk, fills, tri, differing);
                // its Software renderer, where a repaired texture reaches: the repaired copy without the fills (the modern look's image where none
                // reaches, unless that is a pack's)
                Image? software = null;
                if (own && RoadRaster.OwnFaithful(origin, chunk, [], tri, differing))
                    software = texsHd is not null || RoadRaster.OwnFaithful(origin, chunk, fills, [], differing) ? Make(flat, indexFaithful, texs) : modern;
                result[(o.X0, o.Z0)] = (modern, own ? Make(flat, indexFaithful, texs97) : modern, software);
            });
            return new Dictionary<(int, int), (Image?, Image?, Image?)>(result);
        });
    }

    private void Apply(Dictionary<(int, int), (Image? Modern, Image? Faithful, Image? Software)> layers)
    {
        var terrain = builder.Terrain!;
        var world = builder.World!;
        float lightY = builder.Light.Direction.Y * builder.Light.Intensity;
        foreach (var (o, (m, f, s)) in layers)
        {
            var mt = m is null ? null : ImageTexture.CreateFromImage(m);
            var ft = f is null ? null : ReferenceEquals(f, m) ? mt : ImageTexture.CreateFromImage(f);
            var st = s is null ? null : ReferenceEquals(s, m) ? mt : ImageTexture.CreateFromImage(s);
            if (world.Terrain.Sample(o.Item1, o.Item2) is null) builder.OutsideNode?.SetRoadLayer(o.Item1, o.Item2, mt, ft, lightY, st);
            else terrain.SetRoadLayer(o.Item1, o.Item2, mt, ft, lightY, st);
            GC.KeepAlive(f);   // held past the call that copies it (see MeshArrays)
            GC.KeepAlive(s);
        }
        LastChunks = layers.Count; LastMs = _clock?.Elapsed.TotalMilliseconds ?? 0;
        GD.Print($"roads: {layers.Count} chunks painted anew in {LastMs:0} ms");
    }
}
