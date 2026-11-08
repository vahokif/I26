using System.Text.Json;
using I76.Formats.Bwd2;
using I76.Formats.Fsm;
using I76.Formats.Geo;
using I76.Formats.Mission;
using I76.Formats.Verification;
using I76.Formats.Vfs;

// i76: command-line access to the readers, for tests, cross-checks and poking around.
//   i76 [--install DIR] verify                    the format verifier's report
//   i76 [--install DIR] ls [glob]                 every name the VFS resolves, with its source
//   i76 [--install DIR] cat NAME > out            a file's bytes (original data: never commit)
//   i76 [--install DIR] dump NAME                 a parsed asset as JSON
//   i76 [--install DIR] mission NAME              a mission summary as JSON
//   i76 [--install DIR] fsm-disasm NAME           the mission script's bytecode
//   i76 [--install DIR] fsm-decompile MISSION [--names FILE]... [--out FILE.yaml] [--names-template FILE]   the script as YAML source (docs/mission-scripts.md)
//   i76 fsm-compile FILE.yaml --into MISSION_FILE --out NEW_FILE   the source compiled into a copy of a mission file (only its FSM chunk changes)
//   i76 fsm-roundtrip [--install DIR]... [--verbose]   every scripted mission of the install(s) (default I76_INSTALL and I76_NITRO_INSTALL): decompile, compile, compare
//   i76 [--install DIR] export-obj --out DIR [--nitro-install DIR] [--no-cars|--no-objects|--no-terrain] [--only a,b]   every car and scenery model as OBJ/MTL/PNG, and the terrains (original data: keep it out of the repository)
//   i76 [--install DIR] car-table survey|sheet   the car material table: a model's textures per paint scheme, and each beside its false-colour material map
//   i76 [--install DIR] car-tex DEF OUT [ACT] [--maps]  a model's textures as PPMs (and the modern look's material maps)
//   i76 [--install DIR] tex NAME OUT [ACT]        a texture, or each one a .tmt names, as PPMs
//   i76 geomap --bbox LAT1,LON1,LAT2,LON2 --name NAME --out DIR [--roads major|all] [--game i76|nitro|both]   a map from real terrain (AWS) and roads (OpenStreetMap)
//   i76 gallery OUT_DIR [--install DIR] [--nitro-install DIR]   a flat map with every breakable or drive-on structure in a cell of its own (gallery.lvl, gallery.cbt)
//   i76 installer info|list|extract SETUP.exe [DIR]   a GOG offline installer: what it holds, extract the game data
//   i76 [--install DIR] objects OUT.tsv           every name, ODEF type and flag the missions place; also catalogue, facing, decks, bridges, roadpts (the map editor's object surveys)
//   i76 [--install DIR] isect models [DIR]|survey|surface|payload NAME|strip IN OUT pieces|roads|spawn IN OUT x z yaw   the intersection pieces: geometry (and PNGs), where the stock maps place them, the ground under them
//   i76 [--install DIR] looks                     every stock mission's WRLD look: palette, .lum, .tbl, sky, ground, horizon, hour
//   i76 [--install DIR] seams DEF... [--all] [--reach R]  where a model's parts meet: welded, T-junctions, hairline gaps
//   i76 [--install DIR] props OUT [--maps]        every scenery texture over all missions, who uses it, and its prop material map

var argv = args.ToList();
if (argv.Count > 0 && argv[0] == "installer") return I76.Cli.InstallerCommand.Run(argv);
if (argv.FindIndex(a => a is "fsm-decompile" or "fsm-compile" or "fsm-roundtrip") is int fsmAt and >= 0)
{
    var cmd = argv[fsmAt];
    argv.RemoveAt(fsmAt);
    argv.Insert(0, cmd);
    return I76.Cli.FsmScriptCommand.Run(argv);
}
string? installDir = Environment.GetEnvironmentVariable("I76_INSTALL");
int ii = argv.IndexOf("--install");
if (ii >= 0) { installDir = argv[ii + 1]; argv.RemoveRange(ii, 2); }
if (argv.Count > 0 && argv[0] == "geomap") return I76.Cli.GeoMapCommand.Run(argv, installDir);
if (argv.Count > 0 && argv[0] == "gallery") return I76.Cli.GalleryCommand.Run(argv, installDir);
if (argv.Count > 0 && argv[0] == "export-obj") return I76.Cli.ExportObjCommand.Run(argv, installDir);
if (argv.Count > 0 && argv[0] == "car-table") return I76.Cli.CarTableCommand.Run(argv, installDir);
if (argv.Count > 0 && (argv[0] == "car-views" || argv[0] == "car-project" || argv[0] == "car-glass")) return I76.Cli.CarSamCommand.Run(argv, installDir);
if (argv.Count > 0 && argv[0] == "respray-preview") return I76.Cli.ResprayCommand.Run(argv, installDir);
if (argv.Count == 0 || installDir is null)
{
    Console.Error.WriteLine("usage: i76 [--install DIR] verify|ls|cat|dump|mission|fsm-disasm ...  (or set I76_INSTALL)");
    return 2;
}
var install = new GameInstall(installDir);
var json = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
string Arg(int i) => argv.Count > i ? argv[i] : throw new ArgumentException($"{argv[0]} needs an argument");
string MissionPath(string name)
{
    return install.FindMission(name) ?? throw new FileNotFoundException(name);
}

switch (argv[0])
{
    case "isect": return I76.Cli.IsectSurvey.Run(install, argv);
    case "verify":
    {
        var dirs = new[] { "miss16", "miss8" }.Select(d => install.FindDir(d)).OfType<string>();
        var results = VerifyAll.Run(install.ZfsPath, dirs);
        Console.Write(VerifyAll.Format(results));
        return results.All(r => r.Ok) ? 0 : 1;
    }
    case "ls":
    {
        var vfs = new AssetVfs(install);
        var glob = argv.Count > 1 ? argv[1] : "*";
        foreach (var n in vfs.Names.Where(n => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, n, true))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(vfs.Locate(n) switch
            {
                AssetSource.Loose l => $"{n}\tloose\t{l.Path}",
                AssetSource.Zfs z => $"{n}\tzfs\t{z.Entry.Size}",
                AssetSource.Pak p => $"{n}\tpak:{p.Stem}\t{p.Member.Size}",
                AssetSource.Memory m => $"{n}\taddon\t{m.Data.Length}",
                _ => n,
            });
        return 0;
    }
    case "smk":
    {
        // i76 smk FILE [--dump N,N,..] [--out DIR] [--wav PATH]: decode every frame and check the streams end where
        // they should; dump frames as PPM and track 0's audio as WAV
        var path = install.FindDir("smk") is { } sd && !File.Exists(Arg(1)) ? Path.Combine(sd, Arg(1)) : Arg(1);
        using var m = I76.Formats.Video.Smacker.Open(path);
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var dump = Opt("--dump")?.Split(',').Select(int.Parse).ToHashSet() ?? [];
        var outDir = Opt("--out") ?? ".";
        var wav = Opt("--wav");
        Console.WriteLine($"{m.Width}x{m.Height} {m.FrameCount} frames at {1 / m.FrameDuration:0.###} fps, flags {m.Flags}");
        for (int t = 0; t < 7; t++)
            if (m.HasAudio(t)) Console.WriteLine($"  audio {t}: {m.SampleRate(t)} Hz {(m.Is16Bit(t) ? 16 : 8)}-bit {(m.Stereo(t) ? "stereo" : "mono")}");
        var pcmAll = new MemoryStream();
        long audio = 0; int maxLeft = 0, worst = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (m.NextFrame((t, pcm) => { audio += pcm.Length; if (t == 0) pcmAll.Write(pcm); }))
        {
            int f = m.Position - 1;
            if (m.BitsLeft > maxLeft) { maxLeft = m.BitsLeft; worst = f; }
            if (!dump.Contains(f)) continue;
            using var o = File.Create(Path.Combine(outDir, $"smk_{f:00000}.ppm"));
            o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {m.Width} {m.Height} 255\n"));
            var rgb = new byte[m.Width * m.Height * 3];
            for (int i = 0; i < m.Pixels.Length; i++) { int c = m.Pixels[i] * 3; rgb[i * 3] = m.Palette[c]; rgb[i * 3 + 1] = m.Palette[c + 1]; rgb[i * 3 + 2] = m.Palette[c + 2]; }
            o.Write(rgb);
        }
        Console.WriteLine($"decoded in {sw.ElapsedMilliseconds} ms; audio {audio} bytes; most video bits left over {maxLeft} (frame {worst})");
        if (wav is not null && m.HasAudio(0))
        {
            using var w = new BinaryWriter(File.Create(wav));
            int ch = m.Stereo(0) ? 2 : 1, bits = m.Is16Bit(0) ? 16 : 8, rate = m.SampleRate(0);
            w.Write("RIFF"u8); w.Write((int)(36 + pcmAll.Length)); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch);
            w.Write(rate); w.Write(rate * ch * bits / 8); w.Write((short)(ch * bits / 8)); w.Write((short)bits); w.Write("data"u8); w.Write((int)pcmAll.Length);
            w.Write(pcmAll.ToArray());
        }
        return 0;
    }
    case "nitro-map":
    {
        // i76 --install BASE nitro-map NITRO_DIR > src/I76.Formats/Shell/NitroShellMap.g.cs: base DATABASE.MW2 numbers to database.tvf's
        // (see NitroShellMap.cs). Records by identical bytes, sprites by size and pixels; what changed for good:
        // hand pairs (looked at side by side: the backgrounds the Nitro forms replaced), then nearest size.
        var bd = I76.Formats.Shell.ShellDatabase.Open(install.FindFile("DATABASE.MW2") ?? throw new FileNotFoundException("DATABASE.MW2 (--install is the base game)"), false);
        var nd = I76.Formats.Shell.ShellDatabase.Open(new GameInstall(Arg(1)).FindFile("database.tvf") ?? throw new FileNotFoundException("database.tvf"), false);
        var rec = new int[84];
        var byHash = new Dictionary<string, int>();
        string H(ReadOnlySpan<byte> b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b));
        for (int n = 1; n <= nd.Count; n++) byHash.TryAdd(H(nd.RawNative(n)), n);
        for (int b = 1; b <= bd.Count; b++) rec[b] = byHash.GetValueOrDefault(H(bd.Raw(b)));
        // base records Nitro redrew or dropped. Backgrounds: 1 BUILD AND REPAIR and 3 / 29 / 30 CAR/VAN INVENTORY -> CHASSIS CONFIGURATION FORM (1);
        // 4 PARTS CATALOG -> its Nitro redraw (2); 5 AUTO MELEE-SINGLE PLAYER -> SCENARIO-DRIVER ENTRY FORM (21); 14 the old menu sheet
        // and the main menu 26 -> the Nitro logo card (22); 27 CHOOSE YOUR VEHICLE and 31 YOU ARE UNCONSCIOUS: none (black).
        // 25 AUTO MELEE-DRIVER ENTRY -> 21; 28 STANDINGS -> COMBAT STANDINGS (23); 83 the credits text -> 76.
        foreach (var (b, n) in new[] { (1, 1), (3, 1), (29, 1), (30, 1), (4, 2), (5, 21), (14, 22), (26, 22), (25, 21), (28, 23), (83, 76),
            // base 10 (play options) -> 7, 16 (control configuration) -> 12, 18..23 the net/modem forms -> 14..19: byte-different redraws
            (10, 7), (16, 12), (18, 14), (19, 15), (20, 16), (21, 17), (22, 18), (23, 19),
            // banks: 35 / 36 (the form panels) -> 29 / 30, 59..62 -> 53..56
            (35, 29), (36, 30), (59, 53), (60, 54), (61, 55), (62, 56) })
            if (rec[b] == 0) rec[b] = n;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// <auto-generated> by `i76 --install BASE nitro-map NITRO_DIR` (tools/I76.Cli); see NitroShellMap.cs. </auto-generated>");
        sb.AppendLine("namespace I76.Formats.Shell;\npublic static partial class NitroShellMap\n{");
        sb.AppendLine($"    private static readonly int[] Records = [0, {string.Join(", ", rec.Skip(1).Take(83))}];");
        sb.AppendLine("    private static readonly Dictionary<int, int[]> Sprites = new()\n    {");
        string SH(I76.Formats.Shell.Sprite sp)
        {
            var px = new byte[sp.Pixels.Length];
            for (int k = 0; k < px.Length; k++) px[k] = sp.Opaque[k] ? sp.Pixels[k] : (byte)0;
            return $"{sp.Width}x{sp.Height}:{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(px))}:{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(sp.Opaque.Select(o => (byte)(o ? 1 : 0)).ToArray()))}";
        }
        for (int b = 33; b <= 64; b++)
        {
            if (rec[b] == 0) continue;
            var bb = bd.Bank(b);
            var nb = nd.BankNative(rec[b]);
            var nhash = new Dictionary<string, int>();
            for (int i = 0; i < nb.NativeCount; i++) if (nb.Native(i) is { } s) nhash.TryAdd(SH(s), i);
            var map = new int[bb.Count];
            var used = new HashSet<int>();
            for (int i = 0; i < map.Length; i++) { map[i] = bb[i] is { } s && nhash.TryGetValue(SH(s), out int j) ? j : -1; if (map[i] >= 0) used.Add(map[i]); }
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] >= 0 || bb[i] is not { } s) continue;
                int best = -1, bd2 = int.MaxValue;
                for (int j = 0; j < nb.NativeCount; j++)
                    if (!used.Contains(j) && nb.Native(j) is { } t && Math.Abs(t.Width - s.Width) + Math.Abs(t.Height - s.Height) is var d && d < bd2) { bd2 = d; best = j; }
                if (best >= 0 && bd2 <= 80) { map[i] = best; used.Add(best); }
            }
            if (!map.Select((m, i) => m == i).All(x => x) || nb.NativeCount != map.Length)
                sb.AppendLine($"        [{b}] = [{string.Join(", ", map)}],");
        }
        sb.AppendLine("    };\n}");
        Console.Write(sb.ToString());
        return 0;
    }
    case "bg":
    {
        // i76 bg OUTDIR [ID]: shell background record(s) as PPMs (bgNN.ppm); I76_NATIVE=1: a Nitro database by its own record numbers
        bool native = Environment.GetEnvironmentVariable("I76_NATIVE") == "1";
        var db = I76.Formats.Shell.ShellDatabase.Open((install.FindFile("DATABASE.MW2") ?? install.FindFile("database.tvf")) ?? throw new FileNotFoundException("DATABASE.MW2"), translate: !native);
        Directory.CreateDirectory(Arg(1));
        for (int n = 1; n <= (native ? 76 : 83); n++)
        {
            if (argv.Count > 2 && int.Parse(argv[2]) != n) continue;
            I76.Formats.Shell.Pcx pcx;
            try { pcx = native ? db.BackgroundNative(n) : db.Background(n); } catch (Exception) { continue; }
            if (pcx.Width < 100) continue;
            using var o = File.Create(Path.Combine(Arg(1), $"bg{n:00}.ppm"));
            o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {pcx.Width} {pcx.Height} 255\n"));
            for (int k = 0; k < pcx.Width * pcx.Height; k++)
            { int c = pcx.Pixels[k] * 3; o.Write([pcx.Palette[c], pcx.Palette[c + 1], pcx.Palette[c + 2]]); }
            Console.WriteLine($"{n}\t{pcx.Width}x{pcx.Height}");
        }
        return 0;
    }
    case "bank":
    {
        // i76 bank ID OUTDIR [PALETTE_RECORD]: a DATABASE.MW2 sprite bank as PPMs (NN.ppm, transparent pixels magenta),
        // in a background record's palette (26, the main menu's, by default)
        // I76_NATIVE=1: a Nitro database by its own record and sprite numbers (the ID and the palette record too)
        bool native = Environment.GetEnvironmentVariable("I76_NATIVE") == "1";
        var db = I76.Formats.Shell.ShellDatabase.Open((install.FindFile("DATABASE.MW2") ?? install.FindFile("database.tvf")) ?? throw new FileNotFoundException("DATABASE.MW2"), translate: !native);
        int id = Arg(1).StartsWith("0x") ? Convert.ToInt32(Arg(1)[2..], 16) : int.Parse(Arg(1));
        var outDir = Arg(2);
        var pal = (native ? db.BackgroundNative(argv.Count > 3 ? int.Parse(Arg(3)) : 22) : db.Background(args.Length > 3 ? int.Parse(args[3]) : 26)).Palette;
        var bank = native ? db.BankNative(id) : db.Bank(id);
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < bank.Count; i++)
        {
            if (bank[i] is not { } sp) continue;
            using var o = File.Create(Path.Combine(outDir, $"{i:00}.ppm"));
            o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {sp.Width} {sp.Height} 255\n"));
            for (int k = 0; k < sp.Pixels.Length; k++)
                if (sp.Opaque[k]) o.Write([pal[sp.Pixels[k] * 3], pal[sp.Pixels[k] * 3 + 1], pal[sp.Pixels[k] * 3 + 2]]);
                else o.Write([(byte)255, (byte)0, (byte)255]);
            Console.WriteLine($"{i}\t{sp.Width}x{sp.Height}\tat {sp.X0},{sp.Y0}");
        }
        return 0;
    }
    case "tex":
    {
        // i76 tex NAME OUTDIR [PALETTE.act]: a texture (or every texture a .tmt names) as PPMs, keyed texels magenta
        var vfs = new AssetVfs(install);
        var res = new I76.Formats.Assembly.TextureResolver(vfs);
        var pal = argv.Count > 3 && vfs.TryRead(argv[3]) is { } pd ? new I76.Formats.Imaging.Palette(pd) : null;
        Directory.CreateDirectory(Arg(2));
        var names = Arg(1).EndsWith(".tmt", StringComparison.OrdinalIgnoreCase) && vfs.TryRead(Arg(1)) is { } td
            ? new I76.Formats.Imaging.TmtMaterial(td).Names.Select(n => res.FindByStem(n) ?? n).ToList() : [Arg(1)];
        foreach (var n in names)
        {
            if (res.Load(n) is not { } t) { Console.WriteLine($"{n}\tnot loaded"); continue; }
            var rgba = I76.Formats.Imaging.TextureReader.ToRgba(t, pal);
            var stem = Path.GetFileNameWithoutExtension(n.Replace('\\', '/').Split('/', ':').Last());
            using var o = File.Create(Path.Combine(Arg(2), stem + ".ppm"));
            o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {t.Width} {t.Height} 255\n"));
            for (int k = 0; k < t.Width * t.Height; k++)
                o.Write(rgba[k * 4 + 3] == 0 ? [(byte)255, (byte)0, (byte)255] : [rgba[k * 4], rgba[k * 4 + 1], rgba[k * 4 + 2]]);
            Console.WriteLine($"{stem}\t{t.Width}x{t.Height}");
        }
        return 0;
    }
    case "car-tex":
    {
        // i76 car-tex DEF OUTDIR [PALETTE.act]: every texture a model's faces resolve to, as PPMs (keyed texels magenta),
        // with each one's face count, face modes and the parts that use it
        var vfs = new AssetVfs(install);
        var model = new I76.Formats.Assembly.ModelAssembler(vfs).Assemble(Arg(1)) ?? throw new FileNotFoundException(Arg(1));
        var res = new I76.Formats.Assembly.TextureResolver(vfs);
        var outDir = Arg(2);
        var pal = argv.Count > 3 && vfs.TryRead(argv[3]) is { } pd ? new I76.Formats.Imaging.Palette(pd) : null;
        Directory.CreateDirectory(outDir);
        var use = new Dictionary<string, (int Faces, HashSet<string> Modes, HashSet<string> Parts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in model.Parts)
            foreach (var f in part.Mesh.Faces)
                if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && res.ResolveFace(f.Texture, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps) is { } m)
                {
                    if (!use.TryGetValue(m, out var u)) use[m] = u = (0, [], []);
                    use[m] = (u.Faces + 1, u.Modes, u.Parts);
                    u.Modes.Add($"{f.Unk_0x20:x2}/{f.Unk_0x21:x2}");
                    u.Parts.Add(part.NodeName);
                }
        // --maps: the modern look's material maps too (CarMaterial): STEM_orm.ppm (roughness, metallic, clear coat) and STEM_n.ppm
        bool maps = argv.Contains("--maps");
        var loaded = use.Keys.Select(m => (m, t: res.Load(m))).Where(x => x.t is not null).ToDictionary(x => x.m, x => I76.Formats.Imaging.TextureReader.ToRgba(x.t!, pal), StringComparer.OrdinalIgnoreCase);
        var paint = I76.Formats.Imaging.CarMaterial.DominantPaint(loaded.Select(kv => (kv.Value, res.Load(kv.Key)!.Width, res.Load(kv.Key)!.Height)));
        if (maps) Console.WriteLine($"paint {paint}");
        void Ppm(string path, int pw, int ph, Func<int, (byte, byte, byte)> px)
        {
            using var o = File.Create(path);
            o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {pw} {ph} 255\n"));
            for (int k = 0; k < pw * ph; k++) { var (r, g, b) = px(k); o.Write([r, g, b]); }
        }
        foreach (var (m, u) in use.OrderByDescending(k => k.Value.Faces))
        {
            if (res.Load(m) is not { } t) continue;
            var rgba = loaded[m];
            if (maps)
            {
                var mp = I76.Formats.Imaging.CarMaterial.Build(rgba, t.Width, t.Height, paint);
                var st = Path.GetFileNameWithoutExtension(m.Replace('\\', '/').Split('/', ':').Last());
                Ppm(Path.Combine(outDir, st + "_orm.ppm"), mp.Width, mp.Height, k => (mp.Orm[k * 4], mp.Orm[k * 4 + 1], mp.Orm[k * 4 + 2]));
                Ppm(Path.Combine(outDir, st + "_n.ppm"), mp.Width, mp.Height, k => (mp.Normal[k * 3], mp.Normal[k * 3 + 1], mp.Normal[k * 3 + 2]));
            }
            var stem = Path.GetFileNameWithoutExtension(m.Replace('\\', '/').Split('/', ':').Last());
            using (var o = File.Create(Path.Combine(outDir, stem + ".ppm")))
            {
                o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {t.Width} {t.Height} 255\n"));
                for (int k = 0; k < t.Width * t.Height; k++)
                    o.Write(rgba[k * 4 + 3] == 0 ? [(byte)255, (byte)0, (byte)255] : [rgba[k * 4], rgba[k * 4 + 1], rgba[k * 4 + 2]]);
            }
            Console.WriteLine($"{stem}\t{t.Width}x{t.Height}\tfaces {u.Faces}\tmodes {string.Join(",", u.Modes)}\tparts {string.Join(",", u.Parts.Take(8))}");
        }
        return 0;
    }
    case "cat":
    {
        var vfs = new AssetVfs(install);
        using var o = Console.OpenStandardOutput();
        o.Write(vfs.Read(Arg(1)));
        return 0;
    }
    case "car":
    {
        // the vehicle constants the sims read (modern physics tunes from them)
        var vfs = new AssetVfs(install);
        foreach (var name in argv.Skip(1))
        {
            if (!name.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
            {
                // a mission: its surface table (grip, rolling, bump, ?, damage per second)
                var ws = I76.Formats.Mission.WorldSettings.Read(new MissionFile(MissionPath(name)));
                if (ws is not null)
                    foreach (var (sf, i) in I76.Sim.Terrain.TerrainGround.ReadSurfaces(ws).Select((sf, i) => (sf, i)))
                        Console.WriteLine($"{name} surface {i}: {sf}");
                continue;
            }
            var v = I76.Sim.Vehicles.VehicleFactory.Create(vfs, name);
            Console.WriteLine($"{name}: mass {v.P.Mass:0} drag {v.P.Drag:0.####} peak {v.Engine.PeakForce:0} brake {v.P.BrakeStrength * v.P.Mass:0} chassisGrip {v.P.ChassisGrip:0.###} engineScale {v.P.EngineScale}");
            Console.WriteLine($"  box x [{string.Join(", ", v.BoxX)}] y [{string.Join(", ", v.BoxY)}] z [{string.Join(", ", v.BoxZ)}]");
            Console.WriteLine($"  wheelbase {v.P.Wheelbase:0.###} track {v.P.Track:0.###}");
            foreach (var (n, w) in new[] { ("FR", v.FrontRight), ("FL", v.FrontLeft), ("RR", v.RearRight), ("RL", v.RearLeft) })
                Console.WriteLine($"  {n} offset {w.Offset} grip {w.Grip:0.###} rolling {w.Rolling}");
        }
        return 0;
    }
    case "dump":
    {
        var vfs = new AssetVfs(install);
        var name = Arg(1);
        var d = vfs.Read(name);
        object r = Bwd2File.Ext(name) switch
        {
            "geo" => GeoMesh.Parse(d) is var g ? new
            {
                g.Name, Vertices = g.Vertices.Length, Faces = g.Faces.Length, g.Consumed,
                // each face's texture with its +0x1f,+0x20,+0x21 bytes, and whether the engine draws it unlit
                Textures = g.Faces.Select(f => $"{f.Texture} {f.Unk_0x1F},{f.Unk_0x20},{f.Unk_0x21}{(FaceLight.Unlit(f) ? " unlit" : "")}").Distinct().ToList(),
            } : null!,
            var e when Bwd2File.Extensions.Contains(e) => new Bwd2File(d) is var b ? new
            {
                Type = b.FileType(), Name = b.DisplayName(),
                Chunks = b.Chunks.Select(c => new { c.Tag, c.Offset, c.Size }),
                Refs = b.References().Order(),
                Nodes = b.SceneNodes().Select(t => new { t.Tag, Nodes = t.Nodes.Select(n => new { n.Name, n.Parent, T = n.Transform.Translation }) }),
            } : null!,
            _ => new { Size = d.Length },
        };
        Console.WriteLine(JsonSerializer.Serialize(r, json));
        return 0;
    }
    case "billboards":
    {
        // every definition with a camera-facing node (GEO record +0x60 & 0x40), and its ODEF placements
        var vfs = new AssetVfs(install);
        foreach (var n in vfs.Names.Where(Bwd2File.IsDefinition).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            Bwd2File f;
            try { f = new Bwd2File(vfs.Read(n)); } catch (InvalidDataException) { continue; }
            var b = f.BillboardNodes();
            if (b.Count > 0) Console.WriteLine($"{n}\t{f.DisplayName()}\t{string.Join(" ", b.Order(StringComparer.OrdinalIgnoreCase))}");
        }
        return 0;
    }
    case "mission":
    {
        var m = new MissionFile(MissionPath(Arg(1)));
        var (tiles, _) = m.TerrainGrid();
        var objs = m.PlacedObjects();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            m.Name, Palette = m.Palette(), Wdef = m.WdefRefs(), Music = m.MusicTrack(), Tiles = tiles,
            Objects = objs.Select(o => new { o.Name, Flags = o.Flags, Type = o.ObjectType, P = new[] { o.Position.X, o.Position.Y, o.Position.Z } }),
            Roads = m.RoadSegments().Select(r => new { r.Kind, Points = r.Points.Count }),
            Opcodes = m.FsmOpcodes().Count, Actors = m.FsmActors().Select(a => $"{a.Label}={a.Object}"),
        }, json));
        return 0;
    }
    case "looks":
    {
        // every stock mission's WRLD look: palette, .lum, .tbl, sky, ground, horizon, hour
        foreach (var ext in GameInstall.MissionExtensions)
            foreach (var path in install.ListMissions(ext))
                if (WorldSettings.Read(new MissionFile(path)) is { } w)
                    Console.WriteLine($"{Path.GetFileName(path),-14} {w.Palette,-12} {w.Luminance,-10} {w.Translucency,-10} {w.Sky,-13} {w.Ground,-13} {w.Horizon,-11} hour {w.Hour}");
        return 0;
    }
    case "fsm-disasm":
    {
        var m = new MissionFile(MissionPath(Arg(1)));
        if (FsmDisassembler.Disassemble(m) is not { } r) { Console.WriteLine("  no scripting (empty FSM chunk)"); return 0; }
        Console.WriteLine($"  program at 0x{r.Base:x5}, {r.Code.Count} instructions");
        foreach (var i in r.Code)
            Console.WriteLine($"    {i.Index,5}  {i.Offset:x5}  {i.Mnemonic,-14} {i.Operand}");
        return 0;
    }
    case "world":
    {
        var vfs = new AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var tex = new I76.Formats.Assembly.TextureResolver(vfs);
        var dir = install.MissionDir!;
        var names = argv.Count > 1 && argv[1] != "all" && argv[1] != "--why" ? [MissionPath(argv[1])]
            : Directory.EnumerateFiles(dir).Where(GameInstall.IsMissionFile).Order().ToList();
        var unresolvedTex = new Dictionary<string, int>();
        var missingGeo = new HashSet<string>();
        var unresolvedDefs = new Dictionary<string, int>();
        int tp = 0, tr = 0, faces = 0, facesOk = 0;
        var models = new Dictionary<string, I76.Formats.Assembly.AssembledModel?>();
        foreach (var path in names)
        {
            var w = new I76.Formats.Assembly.MissionWorld(vfs, asm, path);
            int kw = w.Placements.Count(p => p.Object.IsKeyword || p.Object.Name.Length == 0);
            int res = w.Placements.Count(p => p.Definition is not null);
            tp += w.Placements.Count - kw; tr += res;
            foreach (var p in w.Placements.Where(p => p.Definition is null && !p.Object.IsKeyword && p.Object.Name.Length > 0))
                unresolvedDefs[p.Object.Name] = unresolvedDefs.GetValueOrDefault(p.Object.Name) + 1;
            Console.WriteLine($"{w.Mission.Name,-6} tiles {w.Terrain.TileCount,2}  placements {w.Placements.Count,4}  keywords {kw,3}  resolved {res,4}  palette {w.PaletteName}  centre {w.Centre()}");
            foreach (var d in w.Placements.Select(p => p.Definition).OfType<string>().Distinct())
            {
                if (models.ContainsKey(d)) continue;
                var m = models[d] = asm.Assemble(d);
                if (m is null) continue;
                foreach (var g in m.MissingGeo) missingGeo.Add(g);
                foreach (var part in m.Parts)
                    foreach (var f in part.Mesh.Faces)
                    {
                        if (f.Texture.Length == 0) continue;
                        faces++;
                        if (tex.ResolveFace(f.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is not null) facesOk++;
                        else unresolvedTex[f.Texture] = unresolvedTex.GetValueOrDefault(f.Texture) + 1;
                    }
            }
        }
        Console.WriteLine($"\nplacements resolved {tr}/{tp}; models {models.Count(m => m.Value is not null)}/{models.Count}; parts missing .GEO {missingGeo.Count}");
        Console.WriteLine($"textured faces resolved {facesOk}/{faces}");
        Console.WriteLine("unresolved definitions: " + string.Join(" ", unresolvedDefs.OrderByDescending(k => k.Value).Take(20).Select(k => $"{k.Key}x{k.Value}")));
        Console.WriteLine("missing .GEO: " + string.Join(" ", missingGeo.Take(20)));
        if (argv.Contains("--why"))
            foreach (var (d, m) in models)
                if (m is not null && m.Parts.Any(p => p.Mesh.Faces.Any(f => f.Texture.Length > 0 && tex.ResolveFace(f.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is null)))
                    Console.WriteLine($"  unresolved in {d} obj={m.ObjectCode} tmts={string.Join(",", m.TmtNames.Take(6))} slots={string.Join(",", m.Parts.SelectMany(p => p.Mesh.Faces).Where(f => f.Texture.Length > 0 && tex.ResolveFace(f.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is null).Select(f => f.Texture).Distinct())}");
        Console.WriteLine("unresolved textures: " + string.Join(" ", unresolvedTex.OrderByDescending(k => k.Value).Take(30).Select(k => $"[{k.Key}]x{k.Value}")));
        return 0;
    }
    case "heights":
    {
        // i76 heights MISSION X0 Z0 X1 Z1 STEP: the bare ground on a grid, rows of z, for finding flat ground and bumps
        var vfs = new AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var w = new I76.Formats.Assembly.MissionWorld(vfs, asm, MissionPath(argv[1]));
        double N(int k) => double.Parse(argv[k], System.Globalization.CultureInfo.InvariantCulture);
        for (double z = N(5); z >= N(3); z -= N(6))
            Console.WriteLine($"{z,8:0} " + string.Concat(Enumerable.Range(0, (int)((N(4) - N(2)) / N(6)) + 1).Select(i => $"{w.BilinearHeight(N(2) + i * N(6), z),6:0.0}")));
        return 0;
    }
    case "bumps":
    {
        // i76 bumps MISSION [radius [speed [x z]]]: straight drivable runs (250 m, no slope over 22°) within radius (600) of a
        // point (default the player's spawn) ranked by their crests, where the ground bends down faster than gravity can
        // follow at that speed (30)
        var vfs = new AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var w = new I76.Formats.Assembly.MissionWorld(vfs, asm, MissionPath(argv[1]));
        var setup = I76.Sim.Missions.MissionSetup.Build(vfs, w, asm, null, script: false);
        var nums = argv.Skip(2).Select(a => double.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        double rad = nums.Count >= 1 ? nums[0] : 600, speed = nums.Count >= 2 ? nums[1] : 30;
        double cx = nums.Count >= 4 ? nums[2] : setup.World.Player?.Pos.X ?? 0, cz = nums.Count >= 4 ? nums[3] : setup.World.Player?.Pos.Z ?? 0;
        double bend = 9.8 / (speed * speed) * 5;          // the slope's drop over 5 m that gravity cannot follow
        var found = new List<(int Crests, double Worst, double X, double Z, int Hd)>();
        for (double x = cx - rad; x <= cx + rad; x += 25)
            for (double z = cz - rad; z <= cz + rad; z += 25)
                for (int hd = 0; hd < 360; hd += 30)
                {
                    double a = hd * Math.PI / 180, prevSlope = double.NaN, worst = 0;
                    int crests = 0; bool ok = true;
                    double prev = w.BilinearHeight(x, z);
                    for (double d = 5; d <= 250 && ok; d += 5)
                    {
                        double h = w.BilinearHeight(x + Math.Sin(a) * d, z + Math.Cos(a) * d), sl = (h - prev) / 5;
                        prev = h;
                        if (Math.Abs(sl) > 0.4) ok = false;
                        if (!double.IsNaN(prevSlope) && prevSlope - sl > bend) { crests++; worst = Math.Max(worst, prevSlope - sl); }
                        prevSlope = sl;
                    }
                    if (ok && crests > 0) found.Add((crests, worst, x, z, hd));
                }
        foreach (var f in found.OrderByDescending(f => f.Crests).ThenByDescending(f => f.Worst).Take(12))
            Console.WriteLine($"{f.Crests,3} crests (worst: the slope falls {f.Worst:0.00} in 5 m) from {f.X:0},{f.Z:0} heading {f.Hd}");
        return 0;
    }
    case "slopes":
    {
        // i76 slopes MISSION [x z] [radius]: the steepest ground near a point (default the player's spawn), for steering a run into it
        var vfs = new AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var w = new I76.Formats.Assembly.MissionWorld(vfs, asm, MissionPath(argv[1]));
        var setup = I76.Sim.Missions.MissionSetup.Build(vfs, w, asm, null, script: false);
        var pv = setup.World.Player;
        var nums = argv.Skip(2).TakeWhile(a => !a.StartsWith("--")).Select(a => double.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        double cx = nums.Count >= 2 ? nums[0] : pv?.Pos.X ?? 0;
        double cz = nums.Count >= 2 ? nums[1] : pv?.Pos.Z ?? 0;
        double rad = nums.Count is 1 or 3 ? nums[^1] : 300;
        if (argv.IndexOf("--ray") is var ri and >= 0)
        {
            // --ray X Z HEADING LEN: the ground every 5 m along a heading (degrees, 0 = +z, 90 = +x)
            double Num(int k) => double.Parse(argv[ri + k], System.Globalization.CultureInfo.InvariantCulture);
            double rx = Num(1), rz = Num(2), hd = Num(3) * System.Math.PI / 180, len = Num(4);
            double prev = w.BilinearHeight(rx, rz);
            for (double d = 0; d <= len; d += 5)
            {
                double x = rx + System.Math.Sin(hd) * d, z = rz + System.Math.Cos(hd) * d, h = w.BilinearHeight(x, z);
                Console.WriteLine($"{d,6:0} {x,8:0.0} {z,9:0.0}  h {h,6:0.0}  rise/5m {h - prev,6:0.00}  ({System.Math.Atan((h - prev) / 5) * 180 / System.Math.PI,5:0.0} deg)");
                prev = h;
            }
            return 0;
        }
        if (pv is not null) Console.WriteLine($"spawn {pv.Pos.X:0.0},{pv.Pos.Y:0.0},{pv.Pos.Z:0.0} fwd {pv.Rot.Forward.X:0.000},{pv.Rot.Forward.Z:0.000} heading {System.Math.Atan2(pv.Rot.Forward.X, pv.Rot.Forward.Z) * 180 / System.Math.PI:0.0}");
        var cells = new List<(double X, double Z, double Slope, double Bearing, double Dist, double H)>();
        for (double x = cx - rad; x <= cx + rad; x += 5)
            for (double z = cz - rad; z <= cz + rad; z += 5)
            {
                double dist = System.Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                if (dist > rad || dist < 20) continue;
                double gx = (w.BilinearHeight(x + 2.5, z) - w.BilinearHeight(x - 2.5, z)) / 5, gz = (w.BilinearHeight(x, z + 2.5) - w.BilinearHeight(x, z - 2.5)) / 5;
                double slope = System.Math.Atan(System.Math.Sqrt(gx * gx + gz * gz)) * 180 / System.Math.PI;
                cells.Add((x, z, slope, System.Math.Atan2(x - cx, z - cz) * 180 / System.Math.PI, dist, w.BilinearHeight(x, z)));
            }
        foreach (var c in cells.OrderByDescending(c => c.Slope).Take(argv.Contains("--all") ? 400 : 25))
            Console.WriteLine($"{c.X,8:0} {c.Z,8:0}  slope {c.Slope,5:0.0} deg  bearing {c.Bearing,6:0.0}  dist {c.Dist,5:0}  h {c.H:0.0}");
        // a profile straight ahead along the spawn heading
        if (pv is not null)
        {
            var f = pv.Rot.Forward;
            double l = System.Math.Sqrt(f.X * f.X + f.Z * f.Z);
            Console.Write("ahead (every 10 m):");
            for (int d = 0; d <= (int)rad; d += 10) Console.Write($" {w.BilinearHeight(cx + f.X / l * d, cz + f.Z / l * d):0.0}");
            Console.WriteLine();
        }
        return 0;
    }
    case "shadowsurvey":
    {
        // i76 shadowsurvey: for every placed scenery model of every mission, how the modern look's drop shadow clips it
        // (DropShadows, drop_shadow_soft.gdshader): a face lying under the ground casts nothing. Models 8 m or wider are
        // judged vertex by vertex against the terrain beneath, the rest against the ground at their centre.
        // Prints one line per placement that changes, and totals.
        var vfs = new AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var models = new Dictionary<string, I76.Formats.Assembly.AssembledModel?>(StringComparer.OrdinalIgnoreCase);
        bool verbose = argv.Contains("-v");
        int placed = 0, buriedN = 0, buriedBig = 0, silencedTall = 0, changed = 0, silenced = 0, bigPlaced = 0, bigChanged = 0, drift = 0;
        var buriedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var tallModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var changedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var silencedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var allModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(install.MissionDir!).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var w = new I76.Formats.Assembly.MissionWorld(vfs, asm, path);
            string mname = Path.GetFileNameWithoutExtension(path);
            foreach (var pl in w.Placements.Concat(w.LinePlacements).Where(p => p.Definition is not null && !p.Definition.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)))
            {
                if (!models.TryGetValue(pl.Definition!, out var m)) models[pl.Definition!] = m = asm.Assemble(pl.Definition!);
                if (m is null || m.Parts.Count == 0) continue;
                var at = new I76.Formats.Assembly.Affine(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, w.GroundedPosition(pl));
                try { at = I76.Formats.Assembly.Affine.FromEngine(pl.Object.Rotation, w.GroundedPosition(pl)); } catch { }
                var pts = new List<System.Numerics.Vector3>();
                foreach (var part in m.Parts) foreach (var v in part.Mesh.Vertices) pts.Add(at.Apply(part.ToModel.Apply(v)));
                if (pts.Count == 0) continue;
                var lo = pts.Aggregate(System.Numerics.Vector3.Min); var hi = pts.Aggregate(System.Numerics.Vector3.Max);
                bool big = Math.Max(hi.X - lo.X, hi.Z - lo.Z) >= 8f;
                double cg = w.BilinearHeight((lo.X + hi.X) / 2, (lo.Z + hi.Z) / 2);
                int below = 0, buried = 0;
                double maxdev = 0;
                foreach (var q in pts)
                {
                    double g = big ? w.BilinearHeight(q.X, q.Z) : cg;
                    if (q.Y - g < 0.05) below++;
                    if (q.Y - g < -0.3) buried++;
                    maxdev = Math.Max(maxdev, Math.Abs(w.BilinearHeight(q.X, q.Z) - cg));
                }
                placed++; allModels.Add(pl.Definition!);
                if (big) bigPlaced++;
                if (big && maxdev > 0.5) drift++;
                if (buried > 0) { buriedN++; buriedModels.Add(pl.Definition!); if (big) buriedBig++; }
                if (below == pts.Count && hi.Y - lo.Y > 0.3f) { silencedTall++; tallModels.Add(pl.Definition!); }
                if (below > 0) { changed++; changedModels.Add(pl.Definition!); if (big) bigChanged++; }
                if (below == pts.Count) { silenced++; silencedModels.Add(pl.Definition!); }
                if (verbose && (buried > 0 || below == pts.Count && hi.Y - lo.Y > 0.3f)) Console.WriteLine($"{mname}\t{pl.Definition}\t{below}/{pts.Count} vertices under ground\tterrain varies {maxdev:0.0} m over it");
            }
        }
        Console.WriteLine($"placements {placed} ({allModels.Count} models); 8 m or wider {bigPlaced}, of which terrain departs the centre's level by over 0.5 m: {drift}");
        Console.WriteLine($"placements with a vertex more than 0.3 m under the ground (their buried part stops casting): {buriedN} ({buriedModels.Count} models), 8 m or wider: {buriedBig}");
        Console.WriteLine($"placements wholly under ground yet over 0.3 m tall (stop casting altogether): {silencedTall} ({tallModels.Count} models: {string.Join(", ", tallModels.Order())})");
        Console.WriteLine($"placements with any vertex at or under ground level (clipped below 0.05 m, mostly bases at 0): {changed} ({changedModels.Count} models)");
        Console.WriteLine($"placements with every vertex under ground (cast nothing): {silenced} ({silencedModels.Count} models: {string.Join(", ", silencedModels.Order())})");
        return 0;
    }
    case "objects": return I76.Cli.ObjectSurvey.Run(install, argv);
    case "bridges": return I76.Cli.BridgeSurvey.Run(install, argv);
    case "facing": return I76.Cli.FacingSurvey.Run(install, argv);
    case "roadpts": return I76.Cli.RoadPoints.Run(install, argv);
    case "roadsurf": return I76.Cli.RoadSurfaceSurvey.Run(install, argv);
    case "decks": return I76.Cli.DeckSurvey.Run(install, argv);
    case "catalogue": return I76.Cli.CatalogueCommand.Run(install, argv);
    case "props":
        return I76.Cli.PropSurvey.Run(install, argv);
    case "run":
        return I76.Cli.MissionRun.Run(install, argv);
    case "wakecheck":
        return I76.Cli.WakeCheck.Run(install, argv);
    case "airtime":
        return I76.Cli.Airtime.Run(install, argv);
    case "ground":
        return I76.Cli.Airtime.Ground(install, argv);
    case "seams":
    {
        // i76 seams DEF... [--all] [--reach R]: where a model's parts meet -- each open-edge vertex near another part, its distance
        // to that part's nearest vertex and nearest open edge (0 welded, edge-only a T-junction, both > 0 a gap)
        var vfs = new AssetVfs(install);
        bool every = argv.Contains("--all");
        int ri = argv.IndexOf("--reach");
        float reach = ri > 0 ? float.Parse(argv[ri + 1], System.Globalization.CultureInfo.InvariantCulture) : 0.01f;
        if (ri > 0) argv.RemoveRange(ri, 2);
        foreach (var def in argv.Skip(1).Where(a => !a.StartsWith("--")))
        {
            var model = new I76.Formats.Assembly.ModelAssembler(vfs).Assemble(def) ?? throw new FileNotFoundException(def);
            var near = I76.Formats.Assembly.PartSeams.Measure(model.Parts, reach);
            int welded = near.Count(n => n.ToVertex < 1e-4f), tee = near.Count(n => n.ToVertex >= 1e-4f && n.ToEdge < 1e-4f);
            var gaps = near.Where(n => n.ToVertex >= 1e-4f && n.ToEdge >= 1e-4f).ToList();
            Console.WriteLine($"{def}: {model.Parts.Count} parts, {near.Count} open-edge vertices within {reach} of another part: {welded} on a vertex, {tee} on an edge only, {gaps.Count} off both (max {(gaps.Count > 0 ? gaps.Max(g => Math.Min(g.ToVertex, g.ToEdge)) : 0):0.#####})");
            foreach (var (pi, f) in I76.Formats.Assembly.CoplanarFaces.Overlays(model))
                Console.WriteLine($"  overlay (lifted off the face under it): {model.Parts[pi].NodeName} {f.Texture}");
            foreach (var n in (every ? near : gaps).OrderBy(n => n.Part).ThenBy(n => n.At.Z))
                Console.WriteLine($"  {n.Part,-10} ({n.At.X,8:0.####},{n.At.Y,8:0.####},{n.At.Z,8:0.####})  vertex {n.ToVertex:0.#####} ({n.VertexOf})  edge {n.ToEdge:0.#####} ({n.EdgeOf})");
        }
        return 0;
    }
    default:
        Console.Error.WriteLine($"unknown command {argv[0]}");
        return 2;
}
