using Godot;
using I76.Formats.Vfs;
using I76.Game.Ui;
using I76.Sim.Session;

namespace I76.Game.Boot;

/// <summary>
/// The port's Developer panel (Ui/DevPanel.cs, docs/dev-hooks.md): on with --dev, I76_DEV=1 or the setting
/// (<c>developer_tools</c>), in single player only. Ctrl+F2 or the escape menu's Developer row opens it over the paused game.
/// The sim's side is DevTools and the session's own snapshot; this part re-syncs the views after an outside write, as a
/// checkpoint's retry does (docs/savestate.md). Any action marks the attempt a cheater's, as the Play Options cheats do
/// (the end menu's Turn off Cheater Options clears it).
/// </summary>
public partial class Main : IDevHost
{
    private bool? _devOn;
    /// <summary>The panel is on for this run (single player).</summary>
    private bool DevOn => _net is null && (_devOn ??= _opt.Dev || System.Environment.GetEnvironmentVariable("I76_DEV") == "1"
        || I76.Game.Shell.ShellSettings.Load().DeveloperTools);

    private CanvasLayer? _dev;
    private bool _devGod;
    private bool _devPausedTree;

    /// <summary>The stored state: kept across a Replay of the same mission (a rebuilt session takes it as a checkpoint's retry does).</summary>
    private sealed record DevStoredState(string Mission, byte[] Snapshot, byte[]? Salvage, int Tick, (uint Hp, string File)[] Loadout);
    private static DevStoredState? _devStore;

    private void OpenDevPanel()
    {
        if (_dev is not null) { DevClose(); return; }
        if (!DevOn || _sim?.Session is not { } s || !DevTools.Allowed(s) || _menu is not null || _picture is not null) return;
        _devPausedTree = !GetTree().Paused;
        GetTree().Paused = true;
        if (GetNodeOrNull<AudioStreamPlayer>("Music") is { } music) music.ProcessMode = ProcessModeEnum.Always;
        _dev = new CanvasLayer { Layer = 45, ProcessMode = ProcessModeEnum.Always, Name = "DevPanel" };
        _dev.AddChild(new DevPanel { Host = this });
        AddChild(_dev);
    }

    public void DevClose()
    {
        if (_dev is null) return;
        _dev.QueueFree();
        _dev = null;
        if (_devPausedTree && !_gamePaused) GetTree().Paused = false;
    }

    private string Cheat(string line)
    {
        I76.Game.Shell.CheatMarker.Mark();
        return line;
    }

    public string DevStatus()
    {
        if (_sim?.Session is not { } s) return "no mission";
        var (n, dt) = I76.Sim.Vehicles.VehicleStep.Substeps(s.Rate.FrameDelta(s.Ticks));
        string car = _car?.Sim is { } v
            ? $"\ncar ({v.Pos.X:0.0}, {v.Pos.Y:0.0}, {v.Pos.Z:0.0}) heading {DevTools.HeadingOf(v.Rot.Forward):0}  {v.Speed * 3.6f:0} km/h  health {v.Health?.HealthPercent():0}%"
            : "";
        return $"tick {s.Ticks}  {s.Rate.Label} Hz  next tick {s.Rate.FrameDelta(s.Ticks) * 2048:0}/2048 s = {n} substep{(n == 1 ? "" : "s")} of {dt * 1000:0.0} ms"
            + $"\nsim time {s.World.Time:0.00} s  {Engine.GetFramesPerSecond():0} fps{(_sim.DevFrozen ? "  FROZEN" : "")}{(_sim.DevTimeScale != 1 ? $"  x{_sim.DevTimeScale:0.##}" : "")}{(I76.Game.Shell.CheatMarker.Set ? "  (cheater)" : "")}" + car;
    }

    /// <summary>Where the player's car was placed at the mission's start (the panel's "start").</summary>
    private (double X, double Z, float Heading)? _devStart;
    /// <summary>The mission's generated places, read from its terrain once (DevLandmarks).</summary>
    private System.Collections.Generic.IReadOnlyList<DevPoint>? _devLandmarks;

    /// <summary>The user's own places, under the mission's key: Keep and Forget in the panel.</summary>
    private const string DevPlacesPath = "user://dev-places.cfg";
    private const string KeptPrefix = "kept: ";

    private string DevPlacesKey => (_opt.Mission ?? "").ToLowerInvariant();

    private static System.Collections.Generic.List<string> DevPlacesOf(ConfigFile cfg, string key) =>
        cfg.HasSectionKey(key, "places") ? [.. (string[])cfg.GetValue(key, "places")] : [];

    private System.Collections.Generic.List<DevPoint> DevKept()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(DevPlacesPath) != Error.Ok) return [];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = new System.Collections.Generic.List<DevPoint>();
        foreach (var e in DevPlacesOf(cfg, DevPlacesKey))
            if (e.Split('|') is [var name, var x, var z, var h] && double.TryParse(x, inv, out double px) && double.TryParse(z, inv, out double pz) && float.TryParse(h, inv, out float ph))
                list.Add(new DevPoint(KeptPrefix + name, px, pz, ph));
        return list;
    }

    /// <summary>The user's kept places first, then the generated landmarks, then the script's paths and cars.</summary>
    public System.Collections.Generic.IReadOnlyList<DevPoint> DevPoints()
    {
        if (_sim?.Session is not { } s) return [];
        _devLandmarks ??= DevLandmarks.Of(s, _devStart);
        return [.. DevKept(), .. _devLandmarks, .. DevTools.Points(s)];
    }

    public string DevKeep(string name)
    {
        if (_car?.Sim is not { } v) return "no car";
        name = name.Replace("|", " ").Trim();
        if (name.Length == 0) name = $"place {DevKept().Count + 1}";
        var cfg = new ConfigFile();
        cfg.Load(DevPlacesPath);   // a missing file is a new one
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = DevPlacesOf(cfg, DevPlacesKey);
        list.RemoveAll(e => e.Split('|')[0] == name);
        list.Insert(0, string.Join('|', name, v.Pos.X.ToString("0.0", inv), v.Pos.Z.ToString("0.0", inv), DevTools.HeadingOf(v.Rot.Forward).ToString("0", inv)));
        cfg.SetValue(DevPlacesKey, "places", list.ToArray());
        return cfg.Save(DevPlacesPath) == Error.Ok ? $"kept \"{name}\" for {DevPlacesKey}" : "could not write the places file";
    }

    public string DevForget(string pointName)
    {
        if (!pointName.StartsWith(KeptPrefix)) return "only a kept place can be forgotten";
        var name = pointName[KeptPrefix.Length..];
        var cfg = new ConfigFile();
        if (cfg.Load(DevPlacesPath) != Error.Ok) return "nothing kept";
        var list = DevPlacesOf(cfg, DevPlacesKey);
        if (list.RemoveAll(e => e.Split('|')[0] == name) == 0) return $"\"{name}\" is not kept";
        if (list.Count == 0) cfg.EraseSectionKey(DevPlacesKey, "places"); else cfg.SetValue(DevPlacesKey, "places", list.ToArray());
        return cfg.Save(DevPlacesPath) == Error.Ok ? $"forgot \"{name}\"" : "could not write the places file";
    }

    public (double X, double Z, float Heading)? DevHere() =>
        _car?.Sim is { } v ? (v.Pos.X, v.Pos.Z, DevTools.HeadingOf(v.Rot.Forward)) : null;

    public string DevTeleport(double x, double z, float heading)
    {
        if (_sim?.Session is not { } s || s.World.Player is not { } p) return "no car";
        if (!DevTools.Teleport(s, p, x, z, heading)) return "not now: the car is dead, not a car, or the session refuses it";
        _sim.Jumped();
        return Cheat($"teleported to ({p.Pos.X:0.0}, {p.Pos.Y:0.0}, {p.Pos.Z:0.0}) heading {heading:0}");
    }

    public string DevRepair()
    {
        if (_sim?.Session is not { } s || s.World.Player is not { } p) return "no car";
        if (!DevTools.Repair(s, p)) return "not repaired: the car is dead or has no damage state";
        if (_carNodes.GetValueOrDefault(p) is { } node && _damageView is not null) _damageView.Restore(node);
        return Cheat("repaired and refilled: armour, chassis, every part, the tyres, every weapon's ammunition");
    }

    public bool DevGodOn => _sim?.Session is { } s && DevTools.GodMode(s);

    public string DevGod(bool on)
    {
        if (_sim?.Session is not { } s || !DevTools.SetGodMode(s, on)) return "refused";
        _devGod = on;
        if (!on) ApplyPlayOptions(I76.Game.Shell.ShellSettings.Load());   // back to what Play Options say
        return on ? Cheat("God mode on: unlimited ammo, armour and chassis reinforcement") : "God mode off (the Play Options' cheats as set)";
    }

    public string DevBring()
    {
        if (_sim?.Session is not { } s || s.World.Player is not { } p) return "no car";
        if (DevTools.BringNearest(s, p) is not { } other) return "no other car to bring";
        _sim.Jumped();
        return Cheat($"brought {other.Definition} to 30 m ahead");
    }

    public string DevStore()
    {
        if (_sim?.Session is not { } s) return "no mission";
        _devStore = new DevStoredState(_opt.Mission ?? "", s.Snapshot(), _salvage?.Save(), s.Ticks, DevLoadoutOf(s.World.Player));
        return $"stored tick {s.Ticks} ({_devStore.Snapshot.Length / 1024} KB)";
    }

    public string? DevStored => _devStore is { } st ? $"tick {st.Tick} of {st.Mission}" : null;

    public string DevRestore()
    {
        if (_sim?.Session is not { } s) return "no mission";
        if (_devStore is not { } st) return "nothing stored";
        if (!st.Mission.Equals(_opt.Mission ?? "", System.StringComparison.OrdinalIgnoreCase)) return $"the stored state is of {st.Mission}";
        // the weapons the store had, back on their mounts first (the snapshot names each weapon and refuses a car with others)
        if (s.World.Player is { } pv && _builder?.Vfs is { } vfs)
            foreach (var (hp, file) in st.Loadout)
                if (pv.Armory?.LoadOrder.FirstOrDefault(w => w.Hardpoint.Id == hp) is { } now && !now.Def.File.Equals(file, System.StringComparison.OrdinalIgnoreCase)
                    && vfs.TryRead(file) is { } gd && I76.Formats.Bwd2.GizmoDef.Read(file, gd) is { } g)
                    DevTools.SwapWeapon(s, pv, hp, g);
        RestoreResult r;
        try { r = s.Restore(st.Snapshot); }
        catch (System.Exception ex)
        {
            // a mismatch is refused before anything is touched; a failure after that leaves the session unusable (docs/savestate.md)
            GD.PrintErr($"developer: the restore failed: {ex.Message}");
            return ex is I76.Sim.Save.SnapshotMismatchException ? $"refused: {ex.Message}" : $"FAILED: {ex.Message} (replay the mission)";
        }
        foreach (var v in r.Dropped)
            if (_carNodes.Remove(v, out var n)) { _sim.Cars.Remove(n); n.QueueFree(); }
        if (_builder?.World is not null && _sim.Session.World.Ground is I76.Sim.Collision.WorldGround ground)
            foreach (var v in r.Created)
                if (v.Definition is { } def) MakeCarNode(v, def, ground);
        if (st.Salvage is { } salvage) _salvage?.Load(salvage);
        // every car's look back to whole, then to the restored damage (a structure destroyed after the store stays drawn destroyed)
        if (_damageView is not null)
        {
            foreach (var node in _carNodes.Values) _damageView.Restore(node);
            _damageView.Resync();
        }
        if (_devGod) DevTools.SetGodMode(s, true);
        _sim.Jumped();
        return Cheat($"restored tick {st.Tick}{(r.Created.Count + r.Dropped.Count > 0 ? $" ({r.Created.Count} cars made, {r.Dropped.Count} dropped)" : "")}");
    }

    public string DevMission => (_opt.Mission ?? "").ToUpperInvariant();

    public System.Collections.Generic.IReadOnlyList<(string Id, string Label)> DevMissions()
    {
        if (_opt.Install is null) return [];
        var install = new GameInstall(_opt.Install);
        return MainMenu.MissionIds(install).Select(id => (id, MainMenu.IsTrip(id) ? id : MainMenu.ArenaName(install, id) is { } n ? $"{id}  {n}" : id)).ToList();
    }

    public string? DevCurrentCar => _sim?.World.Player?.Definition;

    public System.Collections.Generic.IReadOnlyList<string> DevCars() => _builder?.Vfs is { } vfs
        ? vfs.Names.Where(n => n.EndsWith(".vcf", System.StringComparison.OrdinalIgnoreCase)).Select(n => n.ToLowerInvariant()).Distinct()
            .OrderBy(n => n, System.StringComparer.Ordinal).ToList()
        : [];

    public string DevStart(string mission, string car)
    {
        if (_opt.Install is null || _net is not null) return "no install";
        GD.Print($"developer: start {mission} in {(car.Length > 0 ? car : "the mission's own car")}");
        DevClose();
        // as the escape menu's Replay, into another mission: the F9 list's way (no shell launch), in the chosen car
        _ended = true;
        _checkpoint = null;
        _launch = null;
        DevCar = car;
        NextMission = mission;
        GetTree().Paused = false;
        GetTree().ReloadCurrentScene();
        return $"starting {mission}";
    }

    private static (uint Hp, string File)[] DevLoadoutOf(I76.Sim.Vehicles.Vehicle? v) =>
        v?.Armory is { } a ? [.. a.LoadOrder.Select(w => (w.Hardpoint.Id, w.Def.File))] : [];

    private static I76.Sim.Trip.PartsDatabase? _devParts;

    private static string GdfFile(string stem) => stem.EndsWith(".gdf", System.StringComparison.OrdinalIgnoreCase) ? stem : stem + ".gdf";

    public System.Collections.Generic.IReadOnlyList<DevMount> DevMounts()
    {
        if (_car?.Sim is not { Armory: { } a, Definition: { } vcf } || _builder?.Vfs is not { } vfs) return [];
        try
        {
            _devParts ??= I76.Sim.Trip.PartsDatabase.Build(vfs);
            var hps = I76.Formats.Bwd2.Hardpoint.Read(new I76.Formats.Bwd2.Bwd2File(vfs.Read(I76.Sim.Trip.TripCar.FromVcf(vfs.Read(vcf)).Vdf)));
            var list = new System.Collections.Generic.List<DevMount>();
            foreach (var w in a.LoadOrder)
            {
                // the garage's rule for the mount: its slot kind and what that slot accepts (ChassisRules)
                var slot = I76.Sim.Trip.ChassisRules.WeaponSlot((int)w.Hardpoint.Id, hps.FirstOrDefault(h => h.Id == w.Hardpoint.Id));
                var options = I76.Sim.Trip.ChassisRules.WeaponsFor(_devParts, slot, I76.Game.Shell.ShellSettings.Nitro, hidden: false)
                    .Select(p => (GdfFile(p.Stem).ToLowerInvariant(), I76.Sim.Trip.HiddenContent.WeaponName(vfs, p.Stem))).ToList();
                if (!options.Any(o => o.Item1.Equals(w.Def.File, System.StringComparison.OrdinalIgnoreCase)))
                    options.Insert(0, (w.Def.File.ToLowerInvariant(), I76.Sim.Trip.HiddenContent.WeaponName(vfs, w.Def.File)));
                // the hardpoint's own facing and style (HLOC), as the garage labels its slots
                string where = (slot.MountStyle, w.Hardpoint.Facing) switch { (4, _) => "dropper", (1, _) => "roof", (_, 2) => "rear", (_, 3) => "right", (_, 4) => "left", _ => "front" };
                list.Add(new DevMount(w.Hardpoint.Id, $"HP {w.Hardpoint.Id} {where}", w.Def.File.ToLowerInvariant(), options));
            }
            return list;
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.ArgumentException or System.IndexOutOfRangeException)
        {
            GD.PrintErr($"developer: the car's mounts: {ex.Message}");
            return [];
        }
    }

    public string DevSwap(uint hardpoint, string file)
    {
        if (_sim?.Session is not { } s || s.World.Player is not { } p || _builder?.Vfs is not { } vfs) return "no car";
        if (vfs.TryRead(file) is not { } gd || I76.Formats.Bwd2.GizmoDef.Read(file, gd) is not { } g) return $"{file}: no such weapon";
        if (DevTools.SwapWeapon(s, p, hardpoint, g) is not { } old) return "not swapped: the car is dead or has no weapon there";
        return Cheat($"HP {hardpoint}: {old.Def.File} for {g.File}, loaded ({g.Ammo} rounds; the car is still drawn with its old guns)");
    }

    public string DevRefill()
    {
        if (_sim?.Session is not { } s || s.World.Player is not { } p) return "no car";
        return DevTools.RefillAmmo(s, p) ? Cheat("refilled: every weapon's ammunition and the specials' charges, nothing repaired") : "not refilled: the car is dead";
    }

    public bool DevFrozen
    {
        get => _sim?.DevFrozen == true;
        set { if (_sim is not null) _sim.DevFrozen = value; }
    }

    public string DevStep(int ticks)
    {
        if (_sim?.Session is not { } s) return "no mission";
        for (int i = 0; i < ticks; i++) _sim.DevStep();
        return $"stepped {ticks} tick{(ticks == 1 ? "" : "s")}: now tick {s.Ticks}";
    }

    public double DevScale
    {
        get => _sim?.DevTimeScale ?? 1;
        set { if (_sim is not null) _sim.DevTimeScale = value; }
    }
}
