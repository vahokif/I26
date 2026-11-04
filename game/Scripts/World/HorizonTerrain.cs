using Godot;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Game.World;

/// <summary>
/// The modern look's distant terrain: real 3D mountains in a ring round the
/// playable map, out to past the far clip, in place of the sky's sprite
/// horizon panels (which the 1997 look keeps on its dome).
///
/// Each mission keeps its own skyline. The original's 16 panels (a 360°
/// ring at radius 6000) are read column by column for the height of their
/// silhouette; the profile is cut into features at its prominent peaks, and
/// each feature's own outline is revolved about its peak and stood at that
/// bearing, a few km out, scaled so that from the map's centre it fills the
/// same angle the panel did. A butte in the art becomes a round butte, a
/// spire a spire, a jagged range a jagged range; a second, farther ring of
/// the same shapes (turned half round) shows through the gaps. Procedural
/// detail (warped outlines, ridged noise on the peaks, rolling foothills,
/// ridged background ranges) goes on top, and colours come from the panels'
/// pixels (rock by bearing, the scrub at their foot) and the ground texture.
///
/// The heights are the outside ground's (OutsideGround, the one the
/// simulation drives on past the map's tiles): this draws that function far
/// out, OutsideTerrain draws its samples near the camera.
///
/// One fixed polar mesh, centred on the map, rings spaced geometrically so
/// its cells stay square and grow with distance: seen from the map, a cell's
/// size on screen stays about the same and nothing ever pops. Heights are
/// baked once on the CPU; inside the map's tiles it sinks under the playable
/// heightfield, and at the map's edge it starts at the map's own ground. Sunlight is shadowed by a march over the baked heights;
/// the chunks receive Godot's shadow map (1.5 km, the map's own walls and
/// scenery) but cast none, the march standing in for their own. Faces turned
/// from the sun take a warm fill from the sunlit desert floor, and distance
/// hazes it all toward the modern sky's own horizon colour
/// (horizon_terrain.gdshader).
/// The ring's base is sunk onto the cone through the panels' foot, 10.2 x the reference eye's game height (the map's average ground + 3) below
/// the eye at radius 6000, fixed per mission (OutsideGround, HorizonEdge.Ring); the features keep their angular size.
/// </summary>
public partial class HorizonTerrain : Node3D
{
    /// <summary>The panels' ring radius and panel height.</summary>
    private const float RefRadius = HorizonList.Radius;
    private static float PanelHeight => HorizonList.PanelHeight;

    /// <summary>A mission's skyline and colours, from its horizon panels.</summary>
    public sealed record Profile(float[] Height, Color[] Rock, Color Scrub, Color RockLight, Color RockDark, float Snow, bool Dark)
    {
        public int Columns => Height.Length;
    }

    /// <summary>
    /// Reads each panel column's silhouette (the highest opaque texel; the art
    /// is stored foot first) as metres above the panels' foot at radius 6000,
    /// less the band of scrub drawn along every foot; and the colours.
    /// Column c looks along bearing (c + 0.5)/N · 360° from game +Z toward +X.
    /// </summary>
    public static Profile? Measure(Texture2DArray? panels)
    {
        if (panels is null || panels.GetLayers() != HorizonList.Panels) return null;
        var imgs = new Image[HorizonList.Panels];
        for (int i = 0; i < imgs.Length; i++) imgs[i] = panels.GetLayerData(i);
        return Measure(imgs);
    }

    /// <summary>As <see cref="Measure(Texture2DArray?)"/>, from the panels' images (any thread; they are decompressed and left without mipmaps).</summary>
    public static Profile? Measure(Image[] imgs)
    {
        if (imgs.Length != HorizonList.Panels) return null;
        foreach (var im in imgs)
        {
            if (im.IsCompressed()) im.Decompress();
            if (im.HasMipmaps()) im.ClearMipmaps();
        }
        int w = imgs[0].GetWidth(), h = imgs[0].GetHeight(), n = w * imgs.Length;
        var top = new int[n];
        for (int c = 0; c < n; c++)
        {
            var im = imgs[c / w];
            int x = c % w, t = 0;
            for (int y = h - 1; y >= 0; y--) if (im.GetPixel(x, y).A >= 0.5f) { t = y + 1; break; }
            top[c] = t;
        }
        var sorted = top.Order().ToArray();
        // the scrub band along the foot: as high as the lowest columns, up to an eighth of the panel
        int scrub = Math.Min(sorted[n / 50], h / 8);
        var height = new float[n];
        var rock = new Color[n];
        var lums = new List<(float L, Color C)>();
        var col = new List<(float L, Color C)>();
        Color scrubSum = new(0, 0, 0, 0);
        for (int c = 0; c < n; c++)
        {
            var im = imgs[c / w];
            int x = c % w;
            height[c] = Math.Max(top[c] - scrub, 0) / (float)h * PanelHeight;
            col.Clear();
            for (int y = 0; y < top[c]; y++)
            {
                var p = im.GetPixel(x, y);
                if (p.A < 0.5f) continue;
                var lin = p.SrgbToLinear();
                if (y < scrub * 3 / 4) { scrubSum += new Color(lin.R, lin.G, lin.B, 1); continue; }
                float l = 0.299f * lin.R + 0.587f * lin.G + 0.114f * lin.B;
                col.Add((l, lin));
                lums.Add((l, lin));
            }
            // the rock's own colour is its sunlit side: the brighter half of the column (the darker half is the
            // panel's painted shade, which the lighting here adds again; averaged in, the rock came out a muddy
            // grey-brown that read as dark smears on the steep faces)
            col.Sort((a, b) => a.L.CompareTo(b.L));
            Color sum = new(0, 0, 0, 0);
            for (int i = col.Count / 2; i < col.Count; i++) sum += new Color(col[i].C.R, col[i].C.G, col[i].C.B, 1);
            rock[c] = sum.A > 0 ? new Color(sum.R / sum.A, sum.G / sum.A, sum.B / sum.A) : new Color(-1, 0, 0);
        }
        // columns with no rock (scrub only) take their neighbours' colour
        for (int pass = 0; pass < n && rock.Any(r => r.R < 0); pass++)
            for (int c = 0; c < n; c++)
                if (rock[c].R < 0 && rock[(c + 1) % n].R >= 0) rock[c] = rock[(c + 1) % n];
                else if (rock[c].R < 0 && rock[(c + n - 1) % n].R >= 0) rock[c] = rock[(c + n - 1) % n];
        if (rock.Any(r => r.R < 0)) for (int c = 0; c < n; c++) rock[c] = new Color(0.3f, 0.2f, 0.12f);
        // smooth the colours by bearing: a panel's light and shade are its own painted lighting, not the rock's
        var sm = new Color[n];
        const int k = 24;
        for (int c = 0; c < n; c++)
        {
            Color s = new(0, 0, 0, 0);
            for (int d = -k; d <= k; d++) s += rock[(c + d + n) % n];
            sm[c] = new Color(s.R / (2 * k + 1), s.G / (2 * k + 1), s.B / (2 * k + 1)).LinearToSrgb();
        }
        lums.Sort((a, b) => a.L.CompareTo(b.L));
        Color Mean(double from, double to)
        {
            if (lums.Count == 0) return new Color(0.3f, 0.2f, 0.12f);
            int a = (int)(lums.Count * from), b = Math.Max(a + 1, (int)(lums.Count * to));
            float r = 0, g = 0, bl = 0;
            for (int i = a; i < b; i++) { r += lums[i].C.R; g += lums[i].C.G; bl += lums[i].C.B; }
            return new Color(r / (b - a), g / (b - a), bl / (b - a)).LinearToSrgb();
        }
        float snow = lums.Count == 0 ? 0 : lums.Count(p => p.L > 0.45f && Math.Max(p.C.R, Math.Max(p.C.G, p.C.B)) - Math.Min(p.C.R, Math.Min(p.C.G, p.C.B)) < 0.08f) / (float)lums.Count;
        float meanL = lums.Count == 0 ? 0 : lums.Average(p => p.L);
        var scrubC = scrubSum.A > 0 ? new Color(scrubSum.R / scrubSum.A, scrubSum.G / scrubSum.A, scrubSum.B / scrubSum.A).LinearToSrgb() : Mean(0, 0.3);
        return new Profile(height, sm, scrubC, Mean(0.75, 1), Mean(0, 0.25), snow, meanL < 0.02f);
    }

    /// <summary>A skyline for a mission without panels: rolling ranges (OutsideGround.DefaultSkyline).</summary>
    public static Profile Default()
    {
        var h = OutsideGround.DefaultSkyline();
        int n = h.Length;
        var rock = Enumerable.Repeat(new Color(0.55f, 0.36f, 0.24f), n).ToArray();
        return new Profile(h, rock, new Color(0.35f, 0.3f, 0.22f), new Color(0.7f, 0.5f, 0.35f), new Color(0.3f, 0.18f, 0.12f), 0, false);
    }

    /// <summary>Metres a texel of the floor grid.</summary>
    public const float FloorTexel = 10f;
    /// <summary>How far under the ground the ring may show inside the map.</summary>
    public const float FloorMargin = 3f;
    private float[]? _floor;
    private bool[]? _rim;




    /// <summary>
    /// The lowest the drawn ground can be near each texel of the map's bounds (centre-relative game frame, row by row from the
    /// bounds' low corner), less <see cref="FloorMargin"/>; 1e9 where there is no tile (nothing to hide the ring there). The modern
    /// look draws cliff faces and the map's edge walls off the simulation's heightfield by up to 2 x TerrainSurface.MaxFree, so within
    /// 60 m of the map's edge (where the ring's cells reach in) it is the drawn surface that is sampled, every 2.5 m, over the
    /// texel and its neighbours two texels out (the surface moves points sideways by as much); deeper in, the heightfield (the ring
    /// is far under it there).
    /// </summary>
    public static float[] FloorGrid(MissionWorld world, ITerrainSurface surface, (float X0, float Z0, float X1, float Z1)[] tiles, (float X0, float Z0, float X1, float Z1) bounds, System.Numerics.Vector2 centre, out bool[] rim)
    {
        int w = (int)MathF.Ceiling((bounds.X1 - bounds.X0) / FloorTexel), h = (int)MathF.Ceiling((bounds.Z1 - bounds.Z0) / FloorTexel);
        var low = new float[w * h];
        var inside = new bool[w * h];
        foreach (var (x0, z0, x1, z1) in tiles)
        {
            int i0 = (int)MathF.Round((x0 - bounds.X0) / FloorTexel), i1 = (int)MathF.Round((x1 - bounds.X0) / FloorTexel);
            int j0 = (int)MathF.Round((z0 - bounds.Z0) / FloorTexel), j1 = (int)MathF.Round((z1 - bounds.Z0) / FloorTexel);
            for (int j = j0; j < j1 && j < h; j++)
                for (int i = i0; i < i1 && i < w; i++) inside[j * w + i] = true;
        }
        const int band = 6;   // texels from the edge
        bool Out(int i, int j) => i < 0 || j < 0 || i >= w || j >= h || !inside[j * w + i];
        System.Threading.Tasks.Parallel.For(0, h, j =>
        {
            for (int i = 0; i < w; i++)
            {
                if (!inside[j * w + i]) { low[j * w + i] = 1e9f; continue; }
                bool edge = false;
                for (int dj = -band; dj <= band && !edge; dj++)
                    for (int di = -band; di <= band && !edge; di++) edge = Out(i + di, j + dj);
                float lo = float.MaxValue;
                int steps = edge ? 4 : 2;
                for (int sj = 0; sj <= steps; sj++)
                    for (int si = 0; si <= steps; si++)
                    {
                        float x = centre.X + bounds.X0 + (i + si / (float)steps) * FloorTexel, z = centre.Y + bounds.Z0 + (j + sj / (float)steps) * FloorTexel;
                        lo = MathF.Min(lo, (float)world.BilinearHeight(x, z));
                        if (edge) lo = MathF.Min(lo, surface.Point(x / TerrainNode.SampleSize, z / TerrainNode.SampleSize, out _).Y);
                    }
                low[j * w + i] = lo;
            }
        });
        var grid = new float[w * h];
        rim = new bool[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                if (!inside[j * w + i]) { grid[j * w + i] = 1e9f; continue; }
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++) rim[j * w + i] |= Out(i + di, j + dj);
                float lo = low[j * w + i];
                for (int dj = -2; dj <= 2; dj++)
                    for (int di = -2; di <= 2; di++)
                        if (!Out(i + di, j + dj)) lo = MathF.Min(lo, low[(j + dj) * w + i + di]);
                grid[j * w + i] = lo - FloorMargin;
            }
        return grid;
    }

    private (float X0, float Z0, float X1, float Z1)[] _tiles = [], _holes = [];
    private (float X0, float Z0, float X1, float Z1) _bounds;
    private float _base;
    // the baked sun visibility on the polar grid, for OutsideTerrain's chunks near the camera (Visibility)
    private float[] _vis = [], _hgt = [];
    private float _innerR = 1, _lnq = 1;
    private int _rings, _n;

    /// <summary>Vertices in the mesh; build time in ms; the inner and outer radii.</summary>
    public int Vertices { get; private set; }
    public long BuildMs { get; private set; }
    public string Timing { get; private set; } = "";
    public float InnerRadius { get; private set; }
    public float OuterRadius { get; private set; }
    public ShaderMaterial Material { get; private set; } = null!;

    /// <summary>Angular resolution: vertices round each ring.</summary>
    public int Segments { get; set; } = 1024;
    /// <summary>Radial cell size against the angular: seen from inside the map the radial direction is foreshortened.</summary>
    public float Aspect { get; set; } = 2f;
    /// <summary>Metres past the map's farthest corner the ring reaches: the far clip (20 km) from anywhere inside.</summary>
    public float Reach { get; set; } = 21000f;

    private System.Numerics.Vector2 _centre;
    /// <summary>The ring's centre (the map's tiles' centre), game frame XZ: the node stands there.</summary>
    public System.Numerics.Vector2 Centre => _centre;

    /// <summary>
    /// Builds the ring for a mission: <paramref name="lightGame"/> points toward
    /// the sun or moon (game frame), for the baked shadows.
    /// </summary>
    public void Build(MissionWorld world, Profile profile, System.Numerics.Vector3 lightGame, Color ground)
    {
        Compute(world, profile, lightGame, ground);
        Finish();
    }

    private readonly List<(string Name, Godot.Collections.Array Arrays)> _pending = [];
    private Profile? _profile;
    private Color _ground;
    private float _farCorner;
    private long _tHeights, _tShadow, _tArrays, _finishFrom = -1;
    private System.Diagnostics.Stopwatch? _sw;

    /// <summary>
    /// <see cref="Compute"/>, or what it made last time for the same ground, panels, light and colour in this map editor session (a playtest
    /// builds the ring its editor built; the CPU work is more than a second on a big map and the rest of the build waits for it).
    /// The key is the terrain and the seeds of its reachable ground (the surface's key), the panels' profile, the light and the ground's colour;
    /// the stored result is the fields <see cref="Compute"/> sets and the chunks' arrays, read only (<see cref="Finish"/> copies them into meshes).
    /// </summary>
    public void ComputeCached(MissionWorld world, Profile profile, System.Numerics.Vector3 lightGame, Color ground)
    {
        if (!I76.Formats.Vfs.SessionStore.Active || world.Terrain.Huge) { Compute(world, profile, lightGame, ground); return; }
        string key;
        using (var k = new I76.Formats.Assembly.BakeCache.Key("horizon-ring"))
        {
            k.Add(I76.Formats.Assembly.TerrainSurface.CacheKey(world.Terrain, I76.Formats.Assembly.TerrainSurface.Seeds(world).ToList(), world.Outside));
            k.Add<float>(profile.Height).Add<Color>(profile.Rock).Add(profile.Scrub.ToRgba32().ToString()).Add(profile.RockLight.ToRgba32().ToString())
                .Add(profile.RockDark.ToRgba32().ToString()).Add(profile.Snow).Add(profile.Dark);
            k.Add(lightGame.X).Add(lightGame.Y).Add(lightGame.Z).Add(ground.ToRgba32().ToString()).Add(Segments).Add(Reach).Add(Aspect);
            key = "horizon|" + k.Finish();
        }
        bool mine = false;
        var made = I76.Formats.Vfs.SessionStore.GetOrAdd(key, () =>
        {
            Compute(world, profile, lightGame, ground);
            mine = true;
            return new Computed([.. _fields.Select(f => (f, f.GetValue(this)))], [.. _pending], Vertices);
        }, c => c is null ? 0 : c.Vertices * 56L)!;
        if (mine) return;
        _sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (f, v) in made.Fields) f.SetValue(this, v);
        _pending.Clear();
        _pending.AddRange(made.Pending);
        PrintHash(profile);
    }

    private sealed record Computed((System.Reflection.FieldInfo F, object? V)[] Fields, (string Name, Godot.Collections.Array Arrays)[] Pending, int Vertices);

    /// <summary>What <see cref="Compute"/> leaves in the node besides the arrays, by reflection: every instance field this class declares that is not a Godot object, the stopwatch or the pending list.</summary>
    private static readonly System.Reflection.FieldInfo[] _fields = typeof(HorizonTerrain)
        .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
        .Where(f => !typeof(GodotObject).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(System.Diagnostics.Stopwatch) && f.Name != "_pending")
        .ToArray();

    /// <summary>Builds the ring's arrays: pure CPU, safe on any thread (no GPU resource, no node). <see cref="Finish"/> then makes the meshes on the main thread.</summary>
    public void Compute(MissionWorld world, Profile profile, System.Numerics.Vector3 lightGame, Color ground)
    {
        var sw = _sw = System.Diagnostics.Stopwatch.StartNew();
        // night panels are painted as black silhouettes: the rock is lit here, so it takes the ground's colours
        if (profile.Dark)
            profile = profile with
            {
                Rock = Enumerable.Repeat(ground.Darkened(0.1f), profile.Columns).ToArray(),
                RockLight = ground.Lightened(0.15f), RockDark = ground.Darkened(0.45f), Scrub = ground.Darkened(0.3f),
            };
        // the map's tiles, as squares in the game frame
        var outside = world.Outside ?? throw new InvalidOperationException("no outside ground for this map");
        var (r0, r1, c0, c1) = world.Terrain.Bounds();
        float tileW = TerrainTiles.Tile * TerrainNode.SampleSize;
        _centre = outside.Centre;
        var tiles = new List<(float, float, float, float)>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (world.Terrain.TileAt(r, c) != MissionFile.ZmapEmpty)
                    tiles.Add((c * tileW - _centre.X, r * tileW - _centre.Y, (c + 1) * tileW - _centre.X, (r + 1) * tileW - _centre.Y));
        _tiles = tiles.ToArray();
        var holes = new List<(float, float, float, float)>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (world.Terrain.TileAt(r, c) == MissionFile.ZmapEmpty)
                    holes.Add((c * tileW - _centre.X, r * tileW - _centre.Y, (c + 1) * tileW - _centre.X, (r + 1) * tileW - _centre.Y));
        _holes = holes.ToArray();
        _bounds = (c0 * tileW - _centre.X, r0 * tileW - _centre.Y, (c1 + 1) * tileW - _centre.X, (r1 + 1) * tileW - _centre.Y);
        _floor = FloorGrid(world, TerrainNode.SurfaceFor(world), _tiles, _bounds, _centre, out _rim);
        float farCorner = outside.FarCorner;
        _base = outside.Base;

        // ---- the polar mesh ----
        int n = Segments;
        float q = 1 + Aspect * MathF.Tau / n;
        // the inner ring: just inside the nearest point of the map's edge, an empty tile's too (t01's comes within
        // 320 m of the centre: a floor of 500 m left a hole there through to the sky). Not under 40 m: the rings
        // are geometric, and the chunks wholly under the map are left out anyway.
        float inner = float.MaxValue;
        for (int b = 0; b < 720; b++)
        {
            float a = b / 720f * MathF.Tau, sx = MathF.Sin(a), sz = MathF.Cos(a);
            for (float t = 0; t < farCorner + 10; t += 10)
                if (!OverTile(sx * t, sz * t)) { inner = MathF.Min(inner, t); break; }
        }
        inner = MathF.Max(40, MathF.Min(inner, farCorner) - 60);
        float outer = farCorner + Reach;
        int rings = (int)MathF.Ceiling(MathF.Log(outer / inner) / MathF.Log(q)) + 1;
        InnerRadius = inner; OuterRadius = inner * MathF.Pow(q, rings - 1);
        var radius = new float[rings];
        for (int j = 0; j < rings; j++) radius[j] = inner * MathF.Pow(q, j);
        var sin = new float[n + 1]; var cos = new float[n + 1];
        for (int i = 0; i <= n; i++) { sin[i] = MathF.Sin(i / (float)n * MathF.Tau); cos[i] = MathF.Cos(i / (float)n * MathF.Tau); }
        var hgt = new float[rings * n];      // the drawn height
        var shadowH = new float[rings * n];  // the height that shadows: the playable ground inside the map
        var aux = new Vector4[rings * n];    // per vertex: height above the base, distance from the map, rockiness, style
        float hmin = float.MaxValue;
        System.Threading.Tasks.Parallel.For(0, rings, j =>
        {
            float r = radius[j];
            for (int i = 0; i < n; i++)
            {
                float x = sin[i] * r, z = cos[i] * r;
                var p = outside.At(x, z);
                hgt[j * n + i] = p.H;
                aux[j * n + i] = new Vector4(p.Hrel, p.D, p.Rocky, p.Style);
                shadowH[j * n + i] = p.D < 0 ? (float)world.BilinearHeight(_centre.X + x, _centre.Y + z) : p.H;
            }
        });
        hmin = hgt.Min();
        long tHeights = _tHeights = sw.ElapsedMilliseconds;

        // sunlight: march toward the light over the baked heights (bilinear on the polar grid)
        var vis = new float[rings * n];
        var l = System.Numerics.Vector3.Normalize(lightGame);
        float lh = MathF.Sqrt(l.X * l.X + l.Z * l.Z);
        float tanElev = l.Y / MathF.Max(lh, 1e-4f);
        float lx = lh > 1e-4f ? l.X / lh : 0, lz = lh > 1e-4f ? l.Z / lh : 0;
        float lnq = MathF.Log(q);
        float Sample(float x, float z)
        {
            float r = MathF.Sqrt(x * x + z * z);
            float fj = MathF.Log(MathF.Max(r, inner) / inner) / lnq;
            if (fj >= rings - 1) return hmin;
            float fi = (MathF.Atan2(x, z) / MathF.Tau + 1) % 1 * n;
            int j0 = (int)fj, i0 = (int)fi % n, i1 = (i0 + 1) % n;
            float tj = fj - j0, ti = fi - MathF.Floor(fi);
            float a0 = shadowH[j0 * n + i0] + (shadowH[j0 * n + i1] - shadowH[j0 * n + i0]) * ti;
            float a1 = shadowH[(j0 + 1) * n + i0] + (shadowH[(j0 + 1) * n + i1] - shadowH[(j0 + 1) * n + i0]) * ti;
            return a0 + (a1 - a0) * tj;
        }
        float hmax = hgt.Max();
        System.Threading.Tasks.Parallel.For(0, rings, j =>
        {
            float r = radius[j], step0 = MathF.Max(r * MathF.Tau / n, 8);
            for (int i = 0; i < n; i++)
            {
                if (lh < 1e-4f || aux[j * n + i].Y < -50) { vis[j * n + i] = 1; continue; }
                float x = sin[i] * r, z = cos[i] * r;
                float h0 = hgt[j * n + i] + 2, v = 1;
                for (float t = step0; t < 9000; t *= 1.12f)
                {
                    float ray = h0 + t * tanElev;
                    if (ray > hmax) break;
                    float above = ray - Sample(x + lx * t, z + lz * t);
                    // a soft edge: a ridge that just clips the ray darkens a little
                    v = MathF.Min(v, Math.Clamp(above / (t * 0.04f + 3) + 0.5f, 0, 1));
                    if (v <= 0) break;
                }
                vis[j * n + i] = v;
            }
        });

        long tShadow = _tShadow = sw.ElapsedMilliseconds;
        (_vis, _hgt, _innerR, _lnq, _rings, _n) = (vis, hgt, inner, lnq, rings, n);
        if (System.Environment.GetEnvironmentVariable("I76_HORIZON_DEBUG") == "1")
        {
            // the skyline from the centre, at the base height: the 3D ring's against the panels'
            var sb = new System.Text.StringBuilder($"horizon: heights {tHeights} ms, shadows {tShadow - tHeights} ms, base {_base:0}, rings {rings}, centre {_centre.X:0},{_centre.Y:0}, tiles {string.Join(" ", _tiles.Select(t => $"{t.X0 + _centre.X:0},{t.Z0 + _centre.Y:0}"))}\n");
            for (int b = 0; b < 64; b++)
            {
                int i = b * n / 64;
                float best = -90;
                for (int j = 0; j < rings; j++)
                {
                    float r = radius[j];
                    best = MathF.Max(best, MathF.Atan2(hgt[j * n + i] - _base, r) * 180 / MathF.PI);
                }
                int c = (int)((i + 0.5f) / n * profile.Columns);
                float panel = MathF.Atan2(profile.Height[c], RefRadius) * 180 / MathF.PI;
                sb.Append($"  {i * 360f / n,6:0.0}°  3d {best,5:0.0}  panel {panel,5:0.0}\n");
            }
            GD.Print(sb.ToString());
        }

        // ---- the chunks ----
        _profile = profile; _ground = ground; _farCorner = farCorner;
        const int sector = 64, band = 48;
        int count = 0;
        Vector3 P(int j, int i)
        {
            i = (i % n + n) % n;
            return new Vector3(sin[i] * radius[j], hgt[j * n + i], -cos[i] * radius[j]);
        }
        for (int j0 = 0; j0 < rings - 1; j0 += band)
        {
            int j1 = Math.Min(j0 + band, rings - 1);
            for (int i0 = 0; i0 < n; i0 += sector)
            {
                int i1 = i0 + sector;
                bool any = false;
                for (int j = j0; j <= j1 && !any; j++)
                    for (int i = i0; i <= i1; i++)
                        if (aux[j * n + i % n].Y > -30) { any = true; break; }
                if (!any) continue;   // wholly under the map
                int cols = i1 - i0 + 1, rws = j1 - j0 + 1;
                var v = new Vector3[cols * rws];
                var nr = new Vector3[cols * rws];
                var col = new Color[cols * rws];
                var uv = new Vector2[cols * rws];
                var uv2 = new Vector2[cols * rws];
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int k = (j - j0) * cols + (i - i0), g = j * n + i % n;
                        v[k] = P(j, i);
                        var dr = P(Math.Min(j + 1, rings - 1), i) - P(Math.Max(j - 1, 0), i);
                        var da = P(j, i + 1) - P(j, i - 1);
                        var nn = dr.Cross(da).Normalized();
                        nr[k] = nn.Y < 0 ? -nn : nn;
                        var ax = aux[g];
                        col[k] = new Color(vis[g], ax.Z, ax.W, 1);
                        uv[k] = new Vector2(ax.X, ax.Y);
                        uv2[k] = new Vector2(hgt[g] - _base, 0);
                    }
                var idx = new int[(cols - 1) * (rws - 1) * 6];
                int w = 0;
                // which winding faces up (Godot's front face is clockwise seen from the front)
                var t0 = v[0]; var t1 = v[cols]; var t2 = v[1];
                bool flip = (t2 - t0).Cross(t1 - t0).Y < 0;
                for (int j = 0; j < rws - 1; j++)
                    for (int i = 0; i < cols - 1; i++)
                    {
                        int a = j * cols + i, b = a + 1, c = a + cols, d = c + 1;
                        if (!flip) { idx[w++] = a; idx[w++] = c; idx[w++] = b; idx[w++] = b; idx[w++] = c; idx[w++] = d; }
                        else { idx[w++] = a; idx[w++] = b; idx[w++] = c; idx[w++] = b; idx[w++] = d; idx[w++] = c; }
                    }
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = v;
                arrays[(int)Mesh.ArrayType.Normal] = nr;
                arrays[(int)Mesh.ArrayType.Color] = col;
                arrays[(int)Mesh.ArrayType.TexUV] = uv;
                arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
                arrays[(int)Mesh.ArrayType.Index] = idx;
                _pending.Add(($"horizon_{j0}_{i0}", arrays));
                count += v.Length;
            }
        }
        Vertices = count;
        _tArrays = sw.ElapsedMilliseconds;
        PrintHash(profile);
    }

    private void PrintHash(Profile profile)
    {
        if (MissionBuilder.HashOn)
        {
            using var hh = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var (_, arr) in _pending)
                foreach (var v in arr)
                    switch (v.Obj)
                    {
                        case Vector3[] a: hh.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())); break;
                        case Vector2[] a: hh.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())); break;
                        case Color[] a: hh.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())); break;
                        case int[] a: hh.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())); break;
                    }
            GD.Print($"horizon arrays hash {Convert.ToHexString(hh.GetHashAndReset())[..16]}, profile {string.Join(",", profile.Height.Take(4))} {profile.Rock[0]}");
        }
    }

    /// <summary>The meshes, material and textures from <see cref="Compute"/>'s arrays (main thread). Returns the chunks to go: call until 0.</summary>
    public int Finish(int max = int.MaxValue)
    {
        if (_profile is null) return 0;
        if (_finishFrom < 0) _finishFrom = _sw!.ElapsedMilliseconds;   // (Compute may have finished a while before)
        Material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/horizon_terrain.gdshader") };
        for (int k = 0; k < max && _pending.Count > 0; k++)
        {
            var (name, arrays) = _pending[0];
            _pending.RemoveAt(0);
            var mesh = new ArrayMesh();
            mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
            AddChild(new MeshInstance3D
            {
                Mesh = mesh, MaterialOverride = Material, Name = name, Layers = TerrainNode.TerrainLayer,
                // it receives the map's shadows but casts none: its own come from the march above (cast into the
                // shadow map too, its coarse cells would shadow themselves in blotches)
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        if (_pending.Count > 0) return _pending.Count;
        var profile = _profile; var ground = _ground; var farCorner = _farCorner;
        _profile = null;

        // the colours: rock by bearing from the panels, the ground's own colour near the map
        var rockImg = Image.CreateEmpty(profile.Columns, 1, false, Image.Format.Rgba8);
        for (int c = 0; c < profile.Columns; c++) rockImg.SetPixel(c, 0, profile.Rock[c]);
        var m = Material;
        m.SetShaderParameter("rock_by_bearing", ImageTexture.CreateFromImage(rockImg));
        m.SetShaderParameter("rock_light", profile.RockLight);
        m.SetShaderParameter("rock_dark", profile.RockDark);
        m.SetShaderParameter("scrub_color", profile.Scrub);
        m.SetShaderParameter("ground_color", ground);
        m.SetShaderParameter("snow", profile.Snow);
        m.SetShaderParameter("base_height", _base);
        m.SetShaderParameter("map_radius", farCorner);
        // inside the map the ring is a backstop under the terrain (no skirt hides a grazing view under its edge), and must never
        // show above it: its polar cells (~100 m across out at the map's far edge) straddle the edge, and a cell's outside corner
        // at a wall's height drew a slab through the playable ground. The shader draws a fragment over a tile only if it is under
        // the lowest ground near it (FloorTexel metres a texel, the least ground over it and its neighbours, less FloorMargin).
        var (fx0, fz0, fx1, fz1) = _bounds;
        int fw = (int)MathF.Ceiling((fx1 - fx0) / FloorTexel), fh = (int)MathF.Ceiling((fz1 - fz0) / FloorTexel);
        var floor = _floor ?? [];
        var rimTexels = _rim ?? [];
        var both = new float[floor.Length * 2];
        for (int k = 0; k < floor.Length; k++) { both[2 * k] = floor[k]; both[2 * k + 1] = rimTexels.Length > k && rimTexels[k] ? 1f : 0f; }
        var floorImg = Image.CreateFromData(fw, fh, false, Image.Format.Rgf, System.Runtime.InteropServices.MemoryMarshal.AsBytes(both.AsSpan()).ToArray());
        m.SetShaderParameter("map_floor", ImageTexture.CreateFromImage(floorImg));
        m.SetShaderParameter("map_origin", new Vector2(fx0, fz0));
        m.SetShaderParameter("map_extent", System.Environment.GetEnvironmentVariable("I76_HORIZON_MASK") == "0" ? Vector2.Zero : new Vector2(fw * FloorTexel, fh * FloorTexel));   // I76_HORIZON_MASK=0: as it was, for before captures
        _floor = null; _rim = null;
        // the work, not the wait between Compute and Finish
        BuildMs = _tArrays + _sw!.ElapsedMilliseconds - _finishFrom;
        Timing = $"heights {_tHeights} ms, shadows {_tShadow - _tHeights} ms, arrays {_tArrays - _tShadow} ms, meshes {BuildMs - _tArrays} ms";
        return 0;
    }

    private ShaderMaterial? _material1997;

    /// <summary>
    /// The ring in the 1997 look, as the map editor draws it (horizon_1997.gdshader): the map's 1997 terrain light and ground texture
    /// (<paramref name="faithful"/>'s parameters) with the ring's own masks; made once, after <see cref="Finish"/>.
    /// </summary>
    public ShaderMaterial Material1997(ShaderMaterial faithful)
    {
        if (_material1997 is not null) return _material1997;
        var m = _material1997 = OutsideTerrain.Lit1997("res://Shaders/horizon_1997.gdshader", faithful, _centre);
        foreach (var name in new[] { "map_floor", "map_origin", "map_extent" }) m.SetShaderParameter(name, Material.GetShaderParameter(name));
        return m;
    }

    /// <summary>Draws the ring with <paramref name="material"/> (<see cref="Material1997"/>), or with its own <see cref="Material"/> (null).</summary>
    public void Use1997(ShaderMaterial? material)
    {
        var m = material ?? Material;
        foreach (var child in GetChildren())
            if (child is MeshInstance3D mi && mi.MaterialOverride != m) mi.MaterialOverride = m;
    }

    private static float Len(float x, float z) => MathF.Sqrt(x * x + z * z);

    /// <summary>Whether a centre-relative point lies on one of the map's tiles.</summary>
    private bool OverTile(float x, float z)
    {
        foreach (var (x0, z0, x1, z1) in _tiles)
            if (x >= x0 && x <= x1 && z >= z0 && z <= z1) return true;
        return false;
    }

    /// <summary>
    /// The ring's drawn height at a centre-relative point: its polar mesh's triangle there (each cell cut from its (ring, segment + 1)
    /// corner to its (ring + 1, segment) one), read in the grid's own coordinates. OutsideTerrain's farthest chunks ease onto it, so
    /// where they end the ring goes on at the same height. NaN off the grid.
    /// </summary>
    public float RingHeight(float x, float z)
    {
        if (_hgt.Length == 0) return float.NaN;
        float r = Len(x, z);
        float fj = MathF.Log(MathF.Max(r, _innerR) / _innerR) / _lnq;
        if (fj >= _rings - 1) return float.NaN;
        float fi = (MathF.Atan2(x, z) / MathF.Tau + 1) % 1 * _n;
        int j0 = (int)fj, i0 = (int)fi % _n, i1 = (i0 + 1) % _n;
        float tj = fj - j0, ti = fi - MathF.Floor(fi);
        float a = _hgt[j0 * _n + i0], b = _hgt[j0 * _n + i1], c = _hgt[(j0 + 1) * _n + i0], d = _hgt[(j0 + 1) * _n + i1];
        return ti + tj <= 1 ? a + (b - a) * ti + (c - a) * tj : d + (c - d) * (1 - ti) + (b - d) * (1 - tj);
    }

    /// <summary>
    /// The ring's baked sun visibility (0 shadowed .. 1 lit) at a centre-relative point, bilinear on its polar grid: what OutsideTerrain's
    /// chunks near the camera are shaded with, so they meet the ring without a seam. 1 off the grid.
    /// </summary>
    public float Visibility(float x, float z)
    {
        if (_vis.Length == 0) return 1;
        float r = Len(x, z);
        float fj = MathF.Log(MathF.Max(r, _innerR) / _innerR) / _lnq;
        if (fj >= _rings - 1) return 1;
        float fi = (MathF.Atan2(x, z) / MathF.Tau + 1) % 1 * _n;
        int j0 = (int)fj, i0 = (int)fi % _n, i1 = (i0 + 1) % _n;
        float tj = fj - j0, ti = fi - MathF.Floor(fi);
        float a0 = _vis[j0 * _n + i0] + (_vis[j0 * _n + i1] - _vis[j0 * _n + i0]) * ti;
        float a1 = _vis[(j0 + 1) * _n + i0] + (_vis[(j0 + 1) * _n + i1] - _vis[(j0 + 1) * _n + i0]) * ti;
        return a0 + (a1 - a0) * tj;
    }
}
