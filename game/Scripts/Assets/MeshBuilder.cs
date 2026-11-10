using Godot;
using I76.Formats.Assembly;
using I76.Game.Sim;

namespace I76.Game.Assets;

/// <summary>
/// An assembled model to one ArrayMesh, a surface per material, in Godot's
/// frame. Faces are polygons and are fanned to triangles.
///
/// Winding: tools/geoobj.py establishes that GEO face order, once mirrored, is
/// counter-clockwise from outside, and that GEO normals point into the solid.
/// Godot's front faces are clockwise, so each fan is emitted reversed, and
/// normals are negated before mirroring.
/// </summary>
public sealed class MeshBuilder(TextureResolver resolver, MaterialLibrary materials)
{
    public HashSet<string> Unresolved { get; } = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Surface
    {
        public readonly List<Vector3> V = [];
        public readonly List<Vector3> N = [];
        public readonly List<Vector2> UV = [];
        public (Vector4 Lo, Vector4 Hi, Vector4 Eye)? Cabin;
        public bool Glass;   // a material with window texels (MaterialLibrary.GlassOf): it carries the car's cabin box
    }

    private readonly Dictionary<string, I76.Formats.Imaging.CarMaterial.Paint> _paints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Color?> _meanColour = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A face's colour as drawn, sRGB: an untextured face's own, a textured one's mean over its texels (the key's left out; an
    /// animated face's first frame); null if its texture does not load. A lamp's light takes it (StructureLamps).
    /// </summary>
    public Color? FaceColour(AssembledModel model, I76.Formats.Geo.GeoFace f)
    {
        if (f.Texture.Length == 0 || f.Unk_0x20 == 0) return Color.Color8(f.R, f.G, f.B);
        var member = resolver.AnimatedFace(f.Texture) is { } anim ? anim.Frames[0] : resolver.ResolveFace(f.Texture, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps);
        if (member is null) return null;
        if (_meanColour.TryGetValue(member, out var known)) return known;
        Color? mean = null;
        if (materials.RgbaOf(member) is { } t)
        {
            double r = 0, g = 0, b = 0;
            int n = 0;
            for (int i = 0; i + 3 < t.Rgba.Length; i += 4)
            {
                if (t.Rgba[i + 3] == 0) continue;
                r += t.Rgba[i]; g += t.Rgba[i + 1]; b += t.Rgba[i + 2]; n++;
            }
            if (n > 0) mean = new Color((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
        }
        return _meanColour[member] = mean;
    }

    /// <summary>A vehicle's paint, for its material maps (MaterialLibrary, CarMaterial); none for anything else.</summary>
    private I76.Formats.Imaging.CarMaterial.Paint? PaintOf(AssembledModel model)
    {
        if (model.Kind != "vcf") return null;
        if (_paints.TryGetValue(model.Definition, out var p)) return p;
        var members = model.Parts.SelectMany(part => part.Mesh.Faces)
            .Where(f => f.Texture.Length > 0 && f.Unk_0x20 != 0)
            .Select(f => resolver.ResolveFace(f.Texture, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps)).OfType<string>();
        return _paints[model.Definition] = materials.PaintOf(members);
    }

    private readonly Dictionary<string, (string Key, HashSet<string> Members)> _schemes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PaintMasks> _paintMasks = [];

    /// <summary>A vehicle's paint scheme: its key (<see cref="PaintMasks.KeyOf"/>) and the body textures a respray recolours; null for anything but a vehicle.</summary>
    private (string Key, HashSet<string> Members)? SchemeOf(AssembledModel model)
    {
        if (model.Kind != "vcf") return null;
        // every model of the car (its outside, its cockpit's detail) shares the scheme: the textures of each join its set, and the masks,
        // made against the outside's paints, cover them
        if (!_schemes.TryGetValue(model.Definition, out var s))
        {
            var members = PaintMasks.MembersOf(model, resolver);
            return _schemes[model.Definition] = (PaintMasks.KeyOf(members), [.. members]);
        }
        if (!_schemeSeen.TryGetValue(model, out _))
        {
            _schemeSeen.Add(model, null);
            var more = PaintMasks.MembersOf(model, resolver).Where(m => !s.Members.Contains(m)).ToList();
            if (more.Count > 0)
            {
                foreach (var m in more) s.Members.Add(m);
                if (_paintMasks.TryGetValue(s.Key, out var masks)) masks.AddMembers(more);
            }
        }
        return s;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AssembledModel, object?> _schemeSeen = new();

    /// <summary>
    /// Makes ready a car's paint scheme for custom colours: its masks (computed once per scheme, from its body textures) go onto the scheme's
    /// materials. Cars with the factory paint never call it, and cost nothing.
    /// </summary>
    public void EnsurePaint(AssembledModel model)
    {
        if (SchemeOf(model) is not { } s || _paintMasks.ContainsKey(s.Key)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var masks = _paintMasks[s.Key] = PaintMasks.Compute(model, resolver, materials.RgbaOf);
        materials.EnablePaint(s.Key, masks);
        GD.Print($"respray: scheme {s.Key} of {model.Definition}, masks for the materials made so far in {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    /// <summary>The paint masks of a car's scheme (made on first ask): whether it has a secondary paint, which colours a respray can offer.</summary>
    public PaintMasks? PaintMasksOf(AssembledModel model)
    {
        EnsurePaint(model);
        return SchemeOf(model) is { } s ? _paintMasks.GetValueOrDefault(s.Key) : null;
    }

    /// <summary>A car with custom paint: its scheme's masks made ready, then its colours put on its meshes.</summary>
    public void Paint(CarNode node, AssembledModel model, I76.Formats.Imaging.CarPaint? paint)
    {
        if (paint is { IsNone: false }) EnsurePaint(model);
        node.SetPaint(paint);
    }

    /// <summary>The frames and rate of a face's animated .tmt (a police car's light bar), as Godot textures; null if it is not one.</summary>
    public (Texture2D[] Frames, float Fps)? AnimatedTextures(string faceTexture)
    {
        if (resolver.AnimatedFace(faceTexture) is not { } a) return null;
        var t = a.Frames.Select(f => materials.TextureOf(f)).OfType<Texture2D>().ToArray();
        return t.Length == a.Frames.Length ? (t, a.Fps) : null;
    }

    /// <param name="bothSides">every face drawn from both sides (the modern look's open wreck shape)</param>
    /// <param name="oneSided1997">the cut-out and translucent faces drawn from the front only in the 1997 look (MaterialLibrary.ForTexture)</param>
    /// <param name="frameOf">a face texture's .TMT entry: a damaged car's zones (CarNode.TexFrame, DamageTextures); entry 0 without it</param>
    /// <param name="selfLit">a structure whose self-lit faces shine at night in the modern look (<see cref="SelfLit"/>)</param>
    /// <param name="retro">a road sign, whose face (the faces the art marks self-lit) the modern look's night shows retroreflective (RetroLamps)</param>
    public ArrayMesh? Build(AssembledModel model, bool wheels = true, Func<ModelPart, bool>? include = null, System.Numerics.Vector3 origin = default,
        Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skipFace = null, bool bothSides = false, bool oneSided1997 = false, Func<string, int>? frameOf = null,
        bool selfLit = false, bool retro = false) =>
        Build(model, wheels, include, origin, skipFace, PaintOf(model), bothSides, oneSided1997, frameOf, selfLit, retro);

    /// <summary>
    /// Whether a structure's face shines of its own at night in the modern look: one the art marks self-lit (FaceLight.SelfLit)
    /// and not looking up (<paramref name="up"/>, its outward normal's y). A self-lit face looking up is ground under a lamp (the gas
    /// station's pad, the pump islands' tops): the lamps above light it (StructureLamps), as the cut-scene shows the forecourt.
    /// </summary>
    public static bool SelfLit(I76.Formats.Geo.GeoFace f, float up) => I76.Formats.Geo.FaceLight.SelfLit(f) && up <= I76.Formats.Assembly.SelfLitLamps.Down;

    private ArrayMesh? Build(AssembledModel model, bool wheels, Func<ModelPart, bool>? include, System.Numerics.Vector3 origin,
        Func<ModelPart, I76.Formats.Geo.GeoFace, bool>? skipFace, I76.Formats.Imaging.CarMaterial.Paint? paint, bool bothSides = false, bool oneSided1997 = false,
        Func<string, int>? frameOf = null, bool selfLit = false, bool retro = false)
    {
        selfLit &= model.Kind == "sdf";
        retro &= model.Kind == "sdf";
        var surfaces = new Dictionary<Material, Surface>();
        var cabin = new CabinFit();
        var seams = model.Kind == "vcf" ? new List<(Surface S, int I, Vector3 At, Vector3 FaceN, float W)>() : null;
        bool estimate = false;
        var decals = CoplanarFaces.Overlays(model, include, origin);
        var plates = BackToBack(model, include, origin);
        // what a prop is, for its material maps (wrecks are metal, plants leafy); a car keeps its paint
        var family = I76.Formats.Imaging.PropMaterial.FamilyOf(model.Definition);
        int partIndex = -1;
        foreach (var part in model.Parts)
        {
            partIndex++;
            if (include is not null && !include(part)) continue;
            // a driver is a person drawn as a car part: cloth and skin, not the car's clear-coated paint
            bool figure = I76.Formats.Imaging.PropMaterial.IsFigure(part.NodeName);
            var partPaint = figure ? null : paint;
            var scheme = figure ? null : SchemeOf(model);
            var partFamily = figure ? I76.Formats.Imaging.PropMaterial.Family.Figure : family;
            var g = part.Mesh;
            var placed = new Vector3[g.Vertices.Length];
            var normals = new Vector3[g.Normals.Length];
            for (int i = 0; i < placed.Length; i++) placed[i] = Coords.ToGodot(part.ToModel.Apply(g.Vertices[i]) - origin);
            for (int i = 0; i < normals.Length; i++) normals[i] = Coords.ToGodot(-part.ToModel.Rotate(g.Normals[i])).Normalized();
            foreach (var f in g.Faces)
            {
                if (f.Vertices.Length < 3 || skipFace?.Invoke(part, f) == true) continue;
                Material mat;
                string? faceMember = null;
                // a zero vertex normal (some GEOs have them) would light as NaN, which glow then spreads
                var faceN = Coords.ToGodot(-part.ToModel.Rotate(new System.Numerics.Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z))).Normalized();
                bool shines = selfLit && SelfLit(f, faceN.Y);
                // a sign's face: the faces the original draws full-bright to be read at night (the shield, the plate), not its post or back
                bool sheeting = retro && I76.Formats.Geo.FaceLight.SelfLit(f);
                if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && resolver.AnimatedFace(f.Texture) is { } anim)
                    mat = materials.ForAnimated(anim.Frames, anim.Fps, MaterialLibrary.ModeOf(f.Unk_0x20, f.Unk_0x21), partPaint, partFamily, bothSides,
                        unlit: I76.Formats.Geo.FaceLight.Unlit(f), selfLit: shines, retro: sheeting);
                else if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && resolver.ResolveFace(f.Texture, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps, frame: frameOf?.Invoke(f.Texture) ?? 0) is { } member)
                    mat = (faceMember = member) is null ? null! : materials.ForTexture(member, MaterialLibrary.ModeOf(f.Unk_0x20, f.Unk_0x21), partPaint, partFamily, oneSided: plates.Contains((partIndex, f)), bothSides: bothSides,
                        unlit: I76.Formats.Geo.FaceLight.Unlit(f), oneSided1997: oneSided1997,
                        scheme: scheme is { } sc && sc.Members.Contains(member) ? sc.Key : null, selfLit: shines, retro: sheeting);
                else
                {
                    if (f.Texture.Length > 0) Unresolved.Add(f.Texture);
                    mat = materials.ForColour(f.R, f.G, f.B, prop: partPaint is null, partFamily, bothSides, oneSided: plates.Contains((partIndex, f)), selfLit: shines, retro: sheeting);
                }
                if (!surfaces.TryGetValue(mat, out var s)) surfaces[mat] = s = new Surface();
                var fv = f.Vertices;
                // an overlay (a window, a lamp) lying in an earlier part's face: lifted off it, as drawing it later did
                var lift = decals.Contains((partIndex, f)) ? faceN * 0.012f : Vector3.Zero;
                if (faceMember is not null && model.Kind == "vcf") estimate |= EstimatesNormals(I76.Formats.Imaging.CarMaterialTable.StemOf(faceMember));
                if (faceMember is not null && model.Kind == "vcf" && materials.GlassCached(faceMember) is { } glass)
                {
                    s.Glass = true;
                    cabin.Rows = Math.Max(cabin.Rows, RowsOf(I76.Formats.Imaging.CarMaterialTable.StemOf(faceMember)));
                    cabin.Sample(glass, fv, placed, lift);
                }
                // the face's area, which with the corner's angle weighs it in its corners' normals (a big panel outweighs a bevel at its corner)
                float faceArea = 0;
                if (seams is not null)
                    for (int k = 1; k + 1 < fv.Length; k++)
                        if ((uint)fv[0].Index < placed.Length && (uint)fv[k].Index < placed.Length && (uint)fv[k + 1].Index < placed.Length)
                            faceArea += 0.5f * (placed[fv[k].Index] - placed[fv[0].Index]).Cross(placed[fv[k + 1].Index] - placed[fv[0].Index]).Length();
                for (int k = 1; k + 1 < fv.Length; k++)
                    foreach (var vi in new[] { 0, k + 1, k })
                    {
                        var v = fv[vi];
                        if ((uint)v.Index >= placed.Length) continue;
                        // the corner's angle in this triangle weighs its face in the normal (a fan's triangles add up to the polygon's)
                        if (seams is not null)
                        {
                            int o1 = vi == 0 ? k : 0, o2 = vi == 0 ? k + 1 : vi == k ? k + 1 : k;
                            var pc = placed[v.Index];
                            float ang = (uint)fv[o1].Index < placed.Length && (uint)fv[o2].Index < placed.Length
                                ? (placed[fv[o1].Index] - pc).AngleTo(placed[fv[o2].Index] - pc) : 0f;
                            seams.Add((s, s.V.Count, pc, faceN, (float.IsFinite(ang) ? ang : 0f) * MathF.Sqrt(MathF.Max(faceArea, 1e-6f))));
                        }
                        s.V.Add(placed[v.Index] + lift);
                        s.N.Add(Safe((uint)v.NormalIndex < normals.Length ? normals[v.NormalIndex] : faceN, faceN));
                        s.UV.Add(new Vector2(v.U, v.V));
                    }
            }
        }
        // Wheels, for vehicles: the .wdf's own meshes at each WLOC
        if (wheels && model.Kind == "vcf" && model.WheelGeos.Count > 0)
        {
            var part = new AssembledModel { Definition = model.Definition, Kind = "sdf", ObjectCode = model.ObjectCode };
            foreach (var loc in model.Locations.Where(l => l.Tag == "WLOC"))
                if (model.WheelGeos.TryGetValue(loc.Index, out var w))
                {
                    var t = loc.Transform.Translation;
                    var at = Affine.FromEngine([1, 0, 0, 0, 1, 0, 0, 0, 1], new System.Numerics.Vector3(t[0], t[1], t[2]));
                    part.Parts.Add(new ModelPart(w.Intact.Name, w.Intact, at.Compose(w.Node)));
                }
            if (part.Parts.Count > 0 && Build(part, false, null, default, null, paint) is { } wm)
                for (int i = 0; i < wm.GetSurfaceCount(); i++)
                {
                    var mat = wm.SurfaceGetMaterial(i);
                    var arr = wm.SurfaceGetArrays(i);
                    if (!surfaces.TryGetValue(mat, out var sw)) surfaces[mat] = sw = new Surface();
                    sw.V.AddRange((Vector3[])arr[(int)Mesh.ArrayType.Vertex]);
                    sw.N.AddRange((Vector3[])arr[(int)Mesh.ArrayType.Normal]);
                    sw.UV.AddRange((Vector2[])arr[(int)Mesh.ArrayType.TexUV]);
                }
        }
        // Wheels without their own mesh: synthesised (see Wheels).
        else if (wheels && model.Kind == "vcf" && resolver.FindByStem(Wheels.DefaultTexture) is { } wheelTex)
        {
            var mat = materials.ForTexture(wheelTex, car: paint);
            if (!surfaces.TryGetValue(mat, out var s)) surfaces[mat] = s = new Surface();
            foreach (var f in Wheels.Build(model))
                for (int k = 1; k + 1 < f.Corners.Length; k++)
                    foreach (var vi in new[] { 0, k + 1, k })
                    {
                        s.V.Add(Coords.ToGodot(f.Corners[vi]));
                        s.N.Add(Safe(Coords.ToGodot(f.Normals[vi]), Vector3.Up));
                        s.UV.Add(new Vector2(f.Uvs[vi].X, f.Uvs[vi].Y));
                    }
        }
        // the driver sits at the F1 view's eye (VLOC 40, else the first VLOC; as Main.AddCockpit finds it): the engine frame to the mesh's
        var vloc = model.Kind == "vcf" ? model.Locations.FirstOrDefault(l => l.Tag == "VLOC" && l.Index == 40) ?? model.Locations.FirstOrDefault(l => l.Tag == "VLOC") : null;
        Vector4 eye = Vector4.Zero;
        if (vloc is not null)
        {
            var g = Coords.ToGodot(new System.Numerics.Vector3(vloc.Transform.Translation[0], vloc.Transform.Translation[1], vloc.Transform.Translation[2]) - origin);
            eye = new Vector4(g.X, g.Y, g.Z, 1);
        }
        cabin.Apply(surfaces.Values, cabin.Rows, eye);
        if (seams is not null && estimate && !KeepsNormals) SmoothFromFaces(seams);
        return ToMesh(surfaces);
    }

    /// <summary>
    /// The car's cabin, for the glass shader's proxy interior: the box round the texels labelled glass (sampled over the faces that wear them,
    /// in the car's frame), carried by the vertices of the glass surfaces as CUSTOM0 (its low corner and 1) and CUSTOM1 (its high corner and the
    /// driver's weight, 1), so it follows the mesh through damage swaps, the garage and respray without any node bookkeeping.
    /// </summary>
    /// <summary>
    /// Seat rows in a car's cabin (the proxy interior), by the body texture's code (the garage's car names are hashes, so not the model's):
    /// 2 for the four-door cars (police, Royale, Sovereign, Vikea wagon; not the hearse, whose back is for the coffin), 3 for the bus (a row every window), 5 for its
    /// Godolphin Lines coach (the same rows a metre up, on its raised floor), 4 for the limo (benches along its sides, a rear-facing one
    /// at the partition and a forward-facing one across the back), 1 otherwise.
    /// The doors are not in the data.
    /// </summary>
    private static int RowsOf(string stem)
    {
        string code = stem.Length >= 4 ? stem[..4].ToLowerInvariant() : stem.ToLowerInvariant();
        return code switch
        {
            // the Godolphin Lines coach (the bus's second scheme, a layout of its own): rows of the bus on its raised floor
            "xb11" when stem.EndsWith('2') => 5,
            "xb11" => 3,
            "jd11" => 4,
            "cp11" or "cr11" or "js11" or "va11" => 2,
            _ => 1,
        };
    }

    private sealed class CabinFit
    {
        public int Rows = 1;

        private Vector3 _lo = new(float.MaxValue, float.MaxValue, float.MaxValue), _hi = new(float.MinValue, float.MinValue, float.MinValue);
        private int _hits;
        private readonly List<Vector3> _pts = [];

        /// <summary>Samples a face (fanned) on a barycentric grid; the texel under each sample, glass or not, widens the box if it is glass.</summary>
        public void Sample(MaterialLibrary.GlassEntry g, I76.Formats.Geo.FaceVertex[] fv, Vector3[] placed, Vector3 lift)
        {
            const int N = 6;
            for (int k = 1; k + 1 < fv.Length; k++)
            {
                var a = fv[0]; var b = fv[k]; var c = fv[k + 1];
                if ((uint)a.Index >= placed.Length || (uint)b.Index >= placed.Length || (uint)c.Index >= placed.Length) continue;
                for (int i = 0; i <= N; i++)
                    for (int j = 0; j <= N - i; j++)
                    {
                        float wb = i / (float)N, wc = j / (float)N, wa = 1 - wb - wc;
                        float u = a.U * wa + b.U * wb + c.U * wc, v = a.V * wa + b.V * wb + c.V * wc;
                        int x = Math.Clamp((int)((u - MathF.Floor(u)) * g.Width), 0, g.Width - 1), y = Math.Clamp((int)((v - MathF.Floor(v)) * g.Height), 0, g.Height - 1);
                        if (g.Mask[y * g.Width + x] < 128) continue;
                        var p = placed[a.Index] * wa + placed[b.Index] * wb + placed[c.Index] * wc + lift;
                        _pts.Add(p); _hits++;
                    }
            }
        }

        public void Apply(IEnumerable<Surface> surfaces, int rows, Vector4 eye)
        {
            if (_hits < 12) return;
            // the box of the middle 96% of the samples: a stray labelled texel (a lamp, a bumper) does not stretch the cabin
            int lo = (int)(_pts.Count * 0.02), hi = Math.Max(lo, (int)(_pts.Count * 0.98) - 1);
            float Pick(Func<Vector3, float> f, bool max) { var v = _pts.Select(f).Order().ToArray(); return max ? v[hi] : v[lo]; }
            _lo = new Vector3(Pick(q => q.X, false), Pick(q => q.Y, false), Pick(q => q.Z, false));
            _hi = new Vector3(Pick(q => q.X, true), Pick(q => q.Y, true), Pick(q => q.Z, true));
            var size = _hi - _lo;
            // a cabin is wider than a hand and taller than a finger: anything smaller is a stray patch of blue
            if (size.X < 0.5f || size.Y < 0.12f || size.Z < 0.5f) return;
            if (System.Environment.GetEnvironmentVariable("I76_CAR_WINDOWS_LOG") == "1") GD.Print($"cabin box {_lo} .. {_hi} ({_hits} samples) rows {rows} eye {eye}");
            foreach (var s in surfaces)
            {
                if (!s.Glass) continue;
                s.Cabin = (new Vector4(_lo.X, _lo.Y, _lo.Z, 1), new Vector4(_hi.X, _hi.Y, _hi.Z, rows), eye);
            }
        }
    }

    /// <summary>
    /// Faces with another face lying back to back against them (a sign's two faces, one texture each): the pair
    /// is two sides of one plate, so each is drawn from its own side only. Cut-out faces are otherwise drawn from
    /// both sides, and the two would fight, the sign flipping between its faces as the view moved.
    /// </summary>
    private static HashSet<(int, I76.Formats.Geo.GeoFace)> BackToBack(AssembledModel model, Func<ModelPart, bool>? include, System.Numerics.Vector3 origin)
    {
        var all = PlanarFaces(model, include, origin);
        var paired = new HashSet<(int, I76.Formats.Geo.GeoFace)>();
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                var a = all[i]; var b = all[j];
                if (System.Numerics.Vector3.Dot(a.N, b.N) > -0.999f || MathF.Abs(a.D + b.D) > 0.02f) continue;
                var ov = System.Numerics.Vector3.Min(a.Max, b.Max) - System.Numerics.Vector3.Max(a.Min, b.Min);
                if ((ov.X > 0.01f ? 1 : 0) + (ov.Y > 0.01f ? 1 : 0) + (ov.Z > 0.01f ? 1 : 0) >= 2) { paired.Add((a.Part, a.Face)); paired.Add((b.Part, b.Face)); }
            }
        return paired;
    }

    private static List<(int Part, I76.Formats.Geo.GeoFace Face, System.Numerics.Vector3 N, float D, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)> PlanarFaces(
        AssembledModel model, Func<ModelPart, bool>? include, System.Numerics.Vector3 origin)
    {
        var all = new List<(int Part, I76.Formats.Geo.GeoFace Face, System.Numerics.Vector3 N, float D, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            if (include is not null && !include(part)) continue;
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                var pts = f.Vertices.Select(v => part.ToModel.Apply(part.Mesh.Vertices[v.Index]) - origin).ToArray();
                var n = System.Numerics.Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
                if (n.LengthSquared() < 1e-10f) continue;
                n = System.Numerics.Vector3.Normalize(n);
                all.Add((pi, f, n, System.Numerics.Vector3.Dot(n, pts[0]), pts.Aggregate(System.Numerics.Vector3.Min), pts.Aggregate(System.Numerics.Vector3.Max)));
            }
        }
        return all;
    }

    /// <summary>
    /// The cars whose normals are estimated (SmoothFromFaces), by body texture code: those where the GEOs' own show a hard change
    /// where parts meet (Leprechaun al11, Street Van cv11, Skeeter's Van sv11, the bus xb11, the Limo jd11); every other car keeps the
    /// original's normals as made.
    /// </summary>
    private static bool EstimatesNormals(string stem) =>
        stem.Length >= 4 && stem[..4].ToLowerInvariant() is "al11" or "cv11" or "sv11" or "xb11" or "jd11";

    // development switch: I76_CAR_NORMALS=geo keeps the GEOs' own vertex normals on cars
    private static readonly bool KeepsNormals = System.Environment.GetEnvironmentVariable("I76_CAR_NORMALS") == "geo";

    /// <summary>
    /// A car's vertex normals estimated from its faces across the whole assembled car. The GEOs' own are per part (the body is cut
    /// into front, middle and back sections, the damage model's parts, besides doors and panels), each part smoothed on its own and
    /// some leaning off the surface, so where two parts meet they shade differently (a hard change of material behind a door, along
    /// a limo's middle). Corners are joined by where they sit before any overlay lift (a window lies on the body and is lifted off it
    /// to draw on top), so a part's faces, its neighbours' across the cut and the windows on them are one surface; each corner takes
    /// the mean of the face normals there within <c>Crease</c> of its own face's, weighted by the corner's angle and the root of the
    /// face's area (a big flat panel is not bent at its corners by the bevels round it), so curved panels and glass shade
    /// smoothly and a real fold (the roof's edge, a fender lip) keeps its edge.
    /// </summary>
    private const float Crease = 25f;

    private static void SmoothFromFaces(List<(Surface S, int I, Vector3 At, Vector3 FaceN, float W)> verts)
    {
        var groups = new Dictionary<(int, int, int), List<int>>();
        for (int k = 0; k < verts.Count; k++)
        {
            var p = verts[k].At;
            var key = ((int)MathF.Round(p.X * 500), (int)MathF.Round(p.Y * 500), (int)MathF.Round(p.Z * 500));
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = [];
            g.Add(k);
        }
        float cos = MathF.Cos(Mathf.DegToRad(Crease));
        foreach (var g in groups.Values)
            foreach (int a in g)
            {
                var fa = verts[a].FaceN;
                var sum = Vector3.Zero;
                foreach (int b in g) if (verts[b].FaceN.Dot(fa) >= cos) sum += verts[b].FaceN * MathF.Max(verts[b].W, 1e-3f);
                var (s, i, _, _, _) = verts[a];
                s.N[i] = Safe(sum, fa);
            }
    }

    /// <summary>A unit normal, or the fallback (then up) where it is zero or not a number.</summary>
    private static Vector3 Safe(Vector3 n, Vector3 fallback) =>
        n.LengthSquared() > 0.5f ? n : fallback.LengthSquared() > 0.5f ? fallback : Vector3.Up;

    /// <summary>
    /// How a wheel mesh from <see cref="BuildWheel"/> is drawn: whether it may turn about its axle (a round wheel;
    /// the port's stand-in for the hubcap frames), and otherwise its texture strips: each
    /// surface whose face names a .tmt, its own material copy and every name's texture in the .tmt's order.
    /// </summary>
    /// <param name="Smooth">The modern look's round wheel in place of the original polygon (<see cref="Original"/>), where one fits.</param>
    public sealed record WheelLook(bool Rolls, IReadOnlyList<(ShaderMaterial Material, Texture2D[] Frames)> Strips, ArrayMesh? Original = null, ArrayMesh? Smooth = null);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, WheelLook> WheelLooks = new();

    /// <summary>The look of a mesh <see cref="BuildWheel"/> made (null for any other).</summary>
    public static WheelLook? LookOf(Mesh? mesh) => mesh is not null && WheelLooks.TryGetValue(mesh, out var l) ? l : null;

    /// <summary>The wheel mesh's .tmt surfaces, each given its own copy of the material Build chose (frame 0's).</summary>
    private List<(ShaderMaterial, Texture2D[])> Strips(ArrayMesh mesh, AssembledModel part, I76.Formats.Geo.GeoMesh geo,
        I76.Formats.Imaging.CarMaterial.Paint? paint)
    {
        var family = I76.Formats.Imaging.PropMaterial.FamilyOf(part.Definition);
        var frames = new Dictionary<Material, Texture2D[]>();
        foreach (var f in geo.Faces)
        {
            if (f.Texture.Length == 0 || f.Unk_0x20 == 0 || resolver.AnimatedFace(f.Texture) is not null) continue;
            if (resolver.TmtFrames(f.Texture) is not { Length: > 1 } names) continue;
            if (resolver.ResolveFace(f.Texture, part.ObjectCode, part.TmtNames, vtfMaps: part.VtfMaps) is not { } member) continue;
            var shared = materials.ForTexture(member, MaterialLibrary.ModeOf(f.Unk_0x20, f.Unk_0x21), paint, family, unlit: I76.Formats.Geo.FaceLight.Unlit(f));
            var tex = names.Select(n => materials.TextureOf(n, MaterialLibrary.ModeOf(f.Unk_0x20, f.Unk_0x21))).ToArray();
            if (tex.All(t => t is not null)) frames.TryAdd(shared, tex!);
        }
        var o = new List<(ShaderMaterial, Texture2D[])>();
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            if (frames.TryGetValue(mesh.SurfaceGetMaterial(i), out var fr) && materials.StripCopy(mesh.SurfaceGetMaterial(i)) is { } copy)
            {
                mesh.SurfaceSetMaterial(i, copy);
                o.Add((copy, fr));
            }
        return o;
    }

    /// <summary>One wheel (WLOC slot) centred on itself, to spin and steer as its own node.</summary>
    public ArrayMesh? BuildWheel(AssembledModel model, int slot, bool blown = false)
    {
        // the .wdf's own wheel mesh, about the wheel's centre
        if (model.WheelGeos.TryGetValue(slot, out var w))
        {
            var geo = blown && w.Blown is not null ? w.Blown : w.Intact;
            var part = new I76.Formats.Assembly.AssembledModel { Definition = model.Definition, Kind = "sdf", ObjectCode = model.ObjectCode };
            part.TmtNames.AddRange(model.TmtNames);
            part.Parts.Add(new I76.Formats.Assembly.ModelPart(geo.Name, geo, w.Node));
            if (Build(part, false, null, default, null, PaintOf(model)) is { } mesh)
            {
                bool rolls = Wheels.RollsAboutHub(geo, w.Node);
                var smooth = rolls ? SmoothWheelMesh(model, geo, w.Node) : null;
                var wl = new WheelLook(rolls, rolls ? [] : Strips(mesh, part, geo, PaintOf(model)), mesh, smooth);
                WheelLooks.AddOrUpdate(mesh, wl);
                if (smooth is not null) WheelLooks.AddOrUpdate(smooth, wl);
                return mesh;
            }
        }
        if (resolver.FindByStem(Wheels.DefaultTexture) is not { } wheelTex) return null;
        var mat = materials.ForTexture(wheelTex, car: PaintOf(model));
        var plain = SynthWheelMesh(model, slot, mat, 16);
        if (plain is null) return null;
        var round = SynthWheelMesh(model, slot, mat, Wheels.SmoothSegments);
        var synth = new WheelLook(true, [], plain, round);
        WheelLooks.AddOrUpdate(plain, synth);
        if (round is not null) WheelLooks.AddOrUpdate(round, synth);
        return plain;
    }

    /// <summary>The synthesised wheel (<see cref="Wheels.Build"/>) of a slot with this many sides.</summary>
    private static ArrayMesh? SynthWheelMesh(AssembledModel model, int slot, Material mat, int segments)
    {
        var s = new Surface();
        foreach (var f in Wheels.Build(model, segments: segments, onlySlot: slot, centred: true))
            for (int k = 1; k + 1 < f.Corners.Length; k++)
                foreach (var vi in new[] { 0, k + 1, k })
                {
                    s.V.Add(Coords.ToGodot(f.Corners[vi]));
                    s.N.Add(Safe(Coords.ToGodot(f.Normals[vi]), Vector3.Up));
                    s.UV.Add(new Vector2(f.Uvs[vi].X, f.Uvs[vi].Y));
                }
        return s.V.Count == 0 ? null : ToMesh(new Dictionary<Material, Surface> { [mat] = s });
    }

    /// <summary>
    /// The modern look's wheel for a .wdf wheel mesh: a smooth disc and tread fitted to the tyre its texture draws
    /// (<see cref="SmoothWheel"/>), in the face's own material and the tread's flat colour. Null where the texture
    /// is no tyre in a keyed square.
    /// </summary>
    private ArrayMesh? SmoothWheelMesh(AssembledModel model, I76.Formats.Geo.GeoMesh geo, Affine node)
    {
        var face = geo.Faces.FirstOrDefault(f => f.Texture.Length > 0 && f.Unk_0x20 != 0);
        if (face is null || resolver.ResolveFace(face.Texture, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps) is not { } member
            || resolver.Load(member) is not { } tex || SmoothWheel.Build(geo, node, tex, Wheels.SmoothSegments) is not { } w) return null;
        var paint = PaintOf(model);
        var family = I76.Formats.Imaging.PropMaterial.FamilyOf(model.Definition);
        var faceMat = materials.ForTexture(member, MaterialLibrary.ModeOf(face.Unk_0x20, face.Unk_0x21), paint, family, unlit: I76.Formats.Geo.FaceLight.Unlit(face));
        if (paint is not null) faceMat = materials.ForWheelFace(faceMat);
        var treadMat = materials.ForTread(w.R, w.G, w.B);
        var disc = new Surface(); var tread = new Surface();
        int n = w.Rim.Length;
        var outN = new System.Numerics.Vector3(MathF.Sign(w.OuterX - w.InnerX), 0, 0);
        System.Numerics.Vector3 Inner(System.Numerics.Vector3 p) => p with { X = w.InnerX };
        // a triangle wound to face the way <paramref name="outward"/> points (Godot's front is clockwise)
        void Tri(Surface s, WheelVertex a, WheelVertex b, WheelVertex c, System.Numerics.Vector3 outward)
        {
            if ((Coords.ToGodot(b.P) - Coords.ToGodot(a.P)).Cross(Coords.ToGodot(c.P) - Coords.ToGodot(a.P)).Dot(Coords.ToGodot(outward)) > 0) (b, c) = (c, b);
            foreach (var v in new[] { a, b, c }) { s.V.Add(Coords.ToGodot(v.P)); s.N.Add(Coords.ToGodot(v.N)); s.UV.Add(v.Uv); }
        }
        var centre = new WheelVertex(w.Centre, outN, new Vector2(w.CentreUv.X, w.CentreUv.Y));
        var inCentre = new WheelVertex(Inner(w.Centre), -outN, Vector2.Zero);
        // the tread's texture coordinates: u counts tread blocks round the tyre (a whole number, so the pattern wraps), v 0..1 across
        // the width; the inner cap's v is out of range, which the shader reads as plain rubber
        float width = MathF.Abs(w.OuterX - w.InnerX), circ = 0;
        for (int k = 0; k < n; k++) circ += (YZ(w.Rim[(k + 1) % n]) - YZ(w.Rim[k])).Length();
        float blocks = MathF.Max(12, MathF.Round(circ / (width * 0.55f)));
        for (int k = 0; k < n; k++)
        {
            int k2 = (k + 1) % n;
            Tri(disc, centre, new(w.Rim[k], outN, new Vector2(w.RimUv[k].X, w.RimUv[k].Y)), new(w.Rim[k2], outN, new Vector2(w.RimUv[k2].X, w.RimUv[k2].Y)), outN);
            // the tread, shaded smooth about the axle
            float u0 = blocks * k / n, u1 = blocks * (k + 1) / n;
            WheelVertex t0 = new(w.Rim[k], w.RimNormals[k], new(u0, 0)), t1 = new(w.Rim[k2], w.RimNormals[k2], new(u1, 0));
            WheelVertex q0 = new(Inner(w.Rim[k]), w.RimNormals[k], new(u0, 1)), q1 = new(Inner(w.Rim[k2]), w.RimNormals[k2], new(u1, 1));
            var radial = w.RimNormals[k] + w.RimNormals[k2];
            Tri(tread, t0, t1, q1, radial); Tri(tread, t0, q1, q0, radial);
            // the inner end, a cap in the tread's colour (what shows through a hubcap's holes)
            Tri(tread, inCentre with { Uv = new(0, 2) }, q0 with { N = -outN, Uv = new(0, 2) }, q1 with { N = -outN, Uv = new(0, 2) }, -outN);
        }
        return ToMesh(new Dictionary<Material, Surface> { [faceMat] = disc, [treadMat] = tread });
    }

    private static Vector2 YZ(System.Numerics.Vector3 p) => new(p.Y, p.Z);

    private readonly record struct WheelVertex(System.Numerics.Vector3 P, System.Numerics.Vector3 N, Vector2 Uv);

    private static ArrayMesh? ToMesh(Dictionary<Material, Surface> surfaces)
    {
        if (surfaces.Count == 0) return null;
        var mesh = new ArrayMesh();
        foreach (var (mat, s) in surfaces)
        {
            if (s.V.Count == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = s.V.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = s.N.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = s.UV.ToArray();
            ulong flags = 0;
            if (s.Cabin is { } cab)
            {
                var c0 = new float[s.V.Count * 4];
                var c1 = new float[s.V.Count * 4];
                var c2 = new float[s.V.Count * 4];
                for (int i = 0; i < s.V.Count; i++)
                {
                    c0[i * 4] = cab.Lo.X; c0[i * 4 + 1] = cab.Lo.Y; c0[i * 4 + 2] = cab.Lo.Z; c0[i * 4 + 3] = 1;
                    c2[i * 4] = cab.Eye.X; c2[i * 4 + 1] = cab.Eye.Y; c2[i * 4 + 2] = cab.Eye.Z; c2[i * 4 + 3] = cab.Eye.W;
                    c1[i * 4] = cab.Hi.X; c1[i * 4 + 1] = cab.Hi.Y; c1[i * 4 + 2] = cab.Hi.Z; c1[i * 4 + 3] = cab.Hi.W;
                }
                arrays[(int)Mesh.ArrayType.Custom0] = c0;
                arrays[(int)Mesh.ArrayType.Custom1] = c1;
                arrays[(int)Mesh.ArrayType.Custom2] = c2;
                flags = ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift) | ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift) | ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift);
            }
            mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays, (Mesh.ArrayFormat)flags);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }
        return mesh;
    }
}
