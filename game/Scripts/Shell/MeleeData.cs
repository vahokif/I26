using Godot;
using I76.Formats.Bwd2;
using I76.Formats.Shell;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Game.Shell;

/// <summary>
/// What the melee forms choose from: the arenas and
/// scenarios, the car models of i76car.def's built-in table (each variant a
/// file &lt;stem&gt;&lt;n&gt;.vcf, the make and model from its .vdf, the variant's
/// name from its VCFC), and the 53 A.I. driver names.
/// </summary>
public sealed class MeleeData
{
    public static readonly (string Name, string Msn)[] Arenas = I76.Formats.Mission.MissionTitles.BaseArenas;

    /// <summary>In list order, which is not file order.</summary>
    public static readonly (string Name, string Msn)[] Scenarios = I76.Formats.Mission.MissionTitles.BaseScenarios;

    public static string[] DriverNames => I76.Formats.Shell.MeleeDrivers.Names;

    /// <summary>A model: its stem, make and model, the variants' names (variant n is Variants[n − 1]) and how many of them are stock.</summary>
    public sealed record Model(int Id, string Stem, string MakeModel, List<string> Variants, int Base, int? ArtId = null, bool HiddenModel = false)
    {
        /// <summary>The car art's index (banks 0x3c and 0x3d): the id, any above 22 drawn as 23.</summary>
        public int Art => ArtId ?? (Id > 22 ? 23 : Id);
    }

    /// <summary>Where CONFIGURE CHASSIS keeps the variants it saves (the original's addon\&lt;stem&gt;&lt;n&gt;.vcf).</summary>
    public string AddonDir { get; } = "user://addon";

    /// <summary>The models whose first variant loads; their index is the blueprint index (bank 0x3c, clamped to 23).</summary>
    public List<Model> Models { get; } = [];
    private readonly AssetVfs _vfs;

    /// <summary>The base game's list (i76car.def's built-in table, CarTable.BaseModels); <paramref name="hidden"/> appends the complete hidden models.</summary>
    public MeleeData(AssetVfs vfs, bool hidden = false)
        : this(vfs, WithHidden(vfs, CarTable.BaseModels.Select(m => (m.Id, m.Stem, m.Variants, (int?)null, false)).ToList(), hidden, 23, null), "user://addon", hidden) { }

    /// <summary>The Nitro Pack's cars (nitscar.def or nitcar.def): the art id is the row's own, clamped to 36 (bank 54 sprites 2·id, 2·id+1; bank 55 sprite id).</summary>
    public MeleeData(AssetVfs vfs, IEnumerable<I76.Formats.Shell.CarRow> rows, string addonDir, bool hidden = false)
        : this(vfs, WithHidden(vfs, rows.Select(r => (r.ArtId, r.Stem, r.BuiltIn, (int?)Math.Min(r.ArtId, 36), false)).ToList(), hidden, 36, 36), addonDir, hidden) { }

    /// <summary>
    /// Hidden Weapons &amp; Cars: the complete hidden models after the list's own (docs/hidden-content.md), each with as many
    /// stock variants as the install has. They have no art of their own: the art index past the list's (23 on the base
    /// game, 36 on Nitro).
    /// </summary>
    private static List<(int Id, string Stem, int Variants, int? Art, bool Hidden)> WithHidden(AssetVfs vfs, List<(int Id, string Stem, int Variants, int? Art, bool Hidden)> table, bool hidden, int artId, int? art)
    {
        if (!hidden) return table;
        var offered = new HashSet<string>(table.Select(t => t.Stem), StringComparer.OrdinalIgnoreCase);
        foreach (var stem in I76.Sim.Trip.HiddenContent.HiddenModels(vfs, offered))
        {
            // only the archive's own variants are stock: a saved one (the overlay, addon\) is the player's, to overwrite and delete
            int n = I76.Sim.Trip.HiddenContent.StockVariants(vfs, stem);
            table.Add((artId, stem, n, art, true));
        }
        return table;
    }

    /// <summary>Whether the list was built with Hidden Weapons &amp; Cars on.</summary>
    public bool Hidden { get; }

    /// <summary>The models of the list itself, before any hidden ones (the A.I. drivers pick among these, as the original's).</summary>
    public int StockCount => Models.Count(m => !m.HiddenModel);

    private MeleeData(AssetVfs vfs, List<(int Id, string Stem, int Variants, int? Art, bool Hidden)> table, string addonDir, bool hidden)
    {
        _vfs = vfs;
        AddonDir = addonDir;
        Hidden = hidden;
        foreach (var (id, stem, n, art, hiddenModel) in table)
        {
            var names = new List<string>();
            string make = stem;
            for (int v = 1; v <= n; v++)
            {
                if (vfs.TryRead($"{stem}{v}.vcf") is not { } d) break;
                var vcf = new Bwd2File(d);
                if (vcf.VcfcFields() is not { } f) break;
                names.Add(f.Name);
                if (v == 1 && vfs.TryRead(f.Vdf) is { } vd && MakeModel(new Bwd2File(vd)) is { Length: > 0 } dn) make = dn;
            }
            if (names.Count == 0) continue;
            // the saved variants, n from one past the stock ones up to the first missing (re-s3 "Variants"): the player's own
            // (user://, served to the engine through the addon overlay like vehscn.vcf), else the install's addon\ (a
            // community car dropped there, which the VFS already serves)
            int stock = names.Count;
            for (int v = stock + 1; ; v++)
            {
                var path = $"{AddonDir}/{stem}{v}.vcf";
                bool own = Godot.FileAccess.FileExists(path);
                var d = own ? Godot.FileAccess.GetFileAsBytes(path)
                    : vfs.Install.Resolve("addon", $"{stem}{v}.vcf") is { } ip ? System.IO.File.ReadAllBytes(ip) : null;
                if (d is null || d.Length == 0 || new Bwd2File(d).VcfcFields() is not { } f) break;
                if (own) AssetVfs.Addon[$"{stem}{v}.vcf"] = d;
                LoadSidecar(stem, v);
                names.Add(f.Name);
            }
            Models.Add(new Model(id, stem, make, names, stock, art, hiddenModel));
        }
    }

    /// <summary>
    /// A saved variant's sidecar (its paint colours, <see cref="CarSidecar"/>): the player's own beside the file in the addon folder, else a
    /// community car's in the install's addon\ (served by the VFS already). The player's goes into the addon overlay under its name, where
    /// <see cref="CarPaints"/> reads it.
    /// </summary>
    private void LoadSidecar(string stem, int v)
    {
        var name = CarSidecar.NameOf($"{stem}{v}.vcf");
        var path = $"{AddonDir}/{name}";
        if (Godot.FileAccess.FileExists(path)) AssetVfs.Addon[name] = Godot.FileAccess.GetFileAsBytes(path);
        else if (_vfs.Install.Resolve("addon", name) is { } ip) AssetVfs.Addon[name] = System.IO.File.ReadAllBytes(ip);
        else AssetVfs.Addon.TryRemove(name, out _);
    }

    /// <summary>The custom paint colours of a model's variant (null: the factory paint).</summary>
    public CarPaint? Colours(int model, int variant) => CarPaints.Of(_vfs, Vcf(model, variant));

    /// <summary>
    /// Writes variant <paramref name="n"/> of a model (DONE on the chassis
    /// form, written to addon\&lt;stem&gt;&lt;n&gt;.vcf): the file, the overlay
    /// and the name in the list (a new one appended).
    /// </summary>
    public void SaveVariant(int model, int n, byte[] vcf, string name, CarPaint? colours = null, bool setColours = false)
    {
        var m = Models[model];
        DirAccess.MakeDirRecursiveAbsolute(AddonDir);
        using (var f = Godot.FileAccess.Open($"{AddonDir}/{m.Stem}{n}.vcf", Godot.FileAccess.ModeFlags.Write)) f?.StoreBuffer(vcf);
        AssetVfs.Addon[$"{m.Stem}{n}.vcf"] = vcf;
        // the colours go beside it as the I26 sidecar (the .vcf itself stays as the original wrote it); none removes an older one
        // (the 2D form knows nothing of them and leaves a variant's sidecar as it is)
        var side = CarSidecar.NameOf($"{m.Stem}{n}.vcf");
        if (!setColours) { }
        else if (colours is { IsNone: false })
        {
            var bytes = CarSidecar.Write(colours);
            using (var f = Godot.FileAccess.Open($"{AddonDir}/{side}", Godot.FileAccess.ModeFlags.Write)) f?.StoreBuffer(bytes);
            AssetVfs.Addon[side] = bytes;
        }
        else
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath($"{AddonDir}/{side}"));
            AssetVfs.Addon.TryRemove(side, out _);
        }
        if (n <= m.Variants.Count) m.Variants[n - 1] = name; else m.Variants.Add(name);
    }

    /// <summary>
    /// DELETE in the Nitro VARIANT popup: the player's saved variant n goes from user://addon, the later
    /// ones are renumbered down, and the list and the overlay follow. Stock variants, and any that is not the player's own
    /// file (a community car in the install's addon\), are refused; the install is never written.
    /// </summary>
    public VariantFiles.Result DeleteVariant(int model, int n)
    {
        var m = Models[model];
        int count = m.Variants.Count;
        var r = VariantFiles.Delete(ProjectSettings.GlobalizePath(AddonDir), m.Stem, m.Base, count, n);
        if (r != VariantFiles.Result.Deleted) return r;
        for (int k = n; k < count; k++)
        {
            if (AssetVfs.Addon.TryGetValue(VariantFiles.FileName(m.Stem, k + 1), out var d)) AssetVfs.Addon[VariantFiles.FileName(m.Stem, k)] = d;
            if (AssetVfs.Addon.TryGetValue(VariantFiles.Sidecar(m.Stem, k + 1), out var sd)) AssetVfs.Addon[VariantFiles.Sidecar(m.Stem, k)] = sd;
            else AssetVfs.Addon.TryRemove(VariantFiles.Sidecar(m.Stem, k), out _);
        }
        AssetVfs.Addon.TryRemove(VariantFiles.FileName(m.Stem, count), out _);
        AssetVfs.Addon.TryRemove(VariantFiles.Sidecar(m.Stem, count), out _);
        m.Variants.RemoveAt(n - 1);
        return r;
    }

    public string Vcf(int model, int variant) => $"{Models[model].Stem}{variant}.vcf";

    /// <summary>The model and variant a car file is (<c>&lt;stem&gt;&lt;n&gt;.vcf</c>, any case, the longest stem), or null if it is none of this list's.</summary>
    public (int Model, int Variant)? Find(string vcf) =>
        VariantFiles.Find(Models.Select(m => m.Stem).ToList(), Models.Select(m => m.Variants.Count).ToList(), vcf);

    /// <summary>A car file's model (vppirna2.vcf → its MAKE / MODEL), or null for a file of no model on the list.</summary>
    public string? MakeModelOf(string vcf)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(vcf).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return Models.FirstOrDefault(m => m.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase))?.MakeModel;
    }

    /// <summary>The make and model, VDFC's first 20 bytes (the shell's vehicle +0x00, 0x14 long: "Courcheval Courchel").</summary>
    private static string? MakeModel(Bwd2File vdf) =>
        vdf.First("VDFC") is { } c ? I76.Formats.Bin.CStr(vdf.Data, c.PayloadOffset, 20) : null;

    /// <summary>The parts a variant lists under PARTS (x 470, from y 177): engine, suspension, brakes, then its tyres.</summary>
    public List<string> Parts(int model, int variant, I76.Sim.Trip.PartsDatabase db) =>
        _vfs.TryRead(Vcf(model, variant)) is { } d
            ? I76.Sim.Trip.TripCar.FromVcf(d, n => I76.Sim.Vehicles.VehicleFactory.EngineCompId(_vfs, n)).PartNames(db, _vfs.Install.IsNitro)
            : [];

    /// <summary>The weapons a variant mounts, by their gizmo's display name (the form's list at x 470), in the body's hardpoint order as the shell keeps them.</summary>
    public List<string> Weapons(int model, int variant)
    {
        var o = new List<string>();
        if (_vfs.TryRead(Vcf(model, variant)) is not { } d) return o;
        var car = I76.Sim.Trip.TripCar.FromVcf(d);
        var hps = _vfs.TryRead(car.Vdf) is { } vd ? Hardpoint.Read(new Bwd2File(vd)) : [];
        foreach (var (_, gdf) in car.Mounts(hps))
            if (!gdf.Equals("null", StringComparison.OrdinalIgnoreCase) && _vfs.TryRead(gdf) is { } g && new Bwd2File(g).DisplayName() is { Length: > 0 } n) o.Add(n);
        return o;
    }
}

/// <summary>
/// The melee form's remembered setup (user.mel):
/// the area or scenario, the player's model and variant, the driver count,
/// the game name and the driver name ("Unnamed" by default). In user://.
/// </summary>
public sealed class MeleeSetup
{
    private const string Path = "user://melee.cfg";
    public int Area { get; set; }
    public int Model { get; set; }
    public int Variant { get; set; } = 1;
    public int Drivers { get; set; } = 2;
    public string Game { get; set; } = "Unnamed";
    /// <summary>The Nitro host form's game type (an index into its types) and the arena's file in that type's list.</summary>
    public int NitroKind { get; set; }
    public string NitroArena { get; set; } = "";
    /// <summary>The co-op host form's story mission (its stem; the form has no index of its own to keep).</summary>
    public string CoopArena { get; set; } = "";
    /// <summary>The base game's host form was left a co-op form (the Nitro Pack's GAME TYPE keeps its own).</summary>
    public bool CoopHost { get; set; }
    public string Driver { get; set; } = "Unnamed";
    /// <summary>The host forms' Bots (the port's: players the server drives, docs/multiplayer.md "Bots") and their skill (0 Easy, 1 Normal, 2 Hard).</summary>
    public int Bots { get; set; }
    public int BotSkill { get; set; } = 1;
    /// <summary>The host forms' A.I. CARS (the port's; BotCarsPopup): the car file of each bot seat, empty for Random. Kept here, not in the original's .mel files.</summary>
    public List<string> BotCars { get; set; } = [];
    /// <summary>The host forms' simulation rate for the game they host (Hz), kept apart from Play Options' own for single player; 0 until the host form has set it (then that one).</summary>
    public int HostRate { get; set; }
    /// <summary>The host forms' UNLIM. TURRETS (the port's): the game lets a car carry several roof turrets. Off by default.</summary>
    public bool HostTurrets { get; set; }
    /// <summary>The Nitro instant melee's driver count in full, beside nituser.mel's clamped one (<see cref="SaveNitroDrivers"/>).</summary>
    public int NitroDrivers { get; set; }

    /// <summary>The Nitro form's driver count in full (nituser.mel keeps the original's eight at most), a key of its own so melee.cfg's base-game values stay.</summary>
    public static void SaveNitroDrivers(int drivers)
    {
        var c = new ConfigFile();
        c.Load(Path);
        c.SetValue("melee", "nitro_drivers", drivers);
        c.Save(Path);
    }

    public static MeleeSetup Load()
    {
        var s = new MeleeSetup();
        var c = new ConfigFile();
        if (c.Load(Path) != Error.Ok) return s;
        s.Area = (int)c.GetValue("melee", "area", 0);
        s.Model = (int)c.GetValue("melee", "model", 0);
        s.Variant = (int)c.GetValue("melee", "variant", 1);
        s.Drivers = (int)c.GetValue("melee", "drivers", 2);
        s.Game = (string)c.GetValue("melee", "game", "Unnamed");
        s.Driver = (string)c.GetValue("melee", "driver", "Unnamed");
        s.NitroKind = (int)c.GetValue("melee", "nitro_kind", 0);
        s.NitroArena = (string)c.GetValue("melee", "nitro_arena", "");
        s.CoopArena = (string)c.GetValue("melee", "coop_arena", "");
        s.CoopHost = (bool)c.GetValue("melee", "coop_host", false);
        s.Bots = Math.Clamp((int)c.GetValue("melee", "bots", 0), 0, 63);
        s.BotSkill = Math.Clamp((int)c.GetValue("melee", "bot_skill", 1), 0, 2);
        s.BotCars = [.. ((string)c.GetValue("melee", "bot_cars", "")).Split(',').Select(x => x.Trim()).Take(63)];
        while (s.BotCars.Count > 0 && s.BotCars[^1].Length == 0) s.BotCars.RemoveAt(s.BotCars.Count - 1);
        s.NitroDrivers = Math.Max(0, (int)c.GetValue("melee", "nitro_drivers", 0));
        s.HostRate = I76.Sim.Session.SimRate.IsOffered((int)c.GetValue("melee", "host_rate", 0)) ? (int)c.GetValue("melee", "host_rate", 0) : 0;
        s.HostTurrets = (bool)c.GetValue("melee", "host_turrets", false);
        return s;
    }

    public void Save()
    {
        var c = new ConfigFile();
        c.SetValue("melee", "area", Area);
        c.SetValue("melee", "model", Model);
        c.SetValue("melee", "variant", Variant);
        c.SetValue("melee", "drivers", Drivers);
        c.SetValue("melee", "game", Game);
        c.SetValue("melee", "driver", Driver);
        c.SetValue("melee", "nitro_kind", NitroKind);
        c.SetValue("melee", "nitro_arena", NitroArena);
        if (CoopArena.Length > 0) c.SetValue("melee", "coop_arena", CoopArena);
        if (CoopHost) c.SetValue("melee", "coop_host", true);
        if (Bots > 0) c.SetValue("melee", "bots", Bots);
        if (BotCars.Any(x => x.Length > 0)) c.SetValue("melee", "bot_cars", string.Join(",", BotCars));
        if (BotSkill != 1) c.SetValue("melee", "bot_skill", BotSkill);
        if (HostRate > 0) c.SetValue("melee", "host_rate", HostRate);
        if (HostTurrets) c.SetValue("melee", "host_turrets", true);
        if (NitroDrivers > 0) c.SetValue("melee", "nitro_drivers", NitroDrivers);
        c.Save(Path);
    }
}
